using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MwbSwb.Core;

public enum SwbPeerRole : byte
{
    Idle = 0,
    Source = 1,
    Sink = 2,
    Both = 3,
}

public sealed record SwbPeerInfo(
    string HostName,
    string IpAddress,
    int ControlPort,
    int AudioPort,
    SwbPeerRole Role,
    int SampleRate,
    short Channels,
    bool StereoOk,
    double AzimuthDeg = 0,
    double ElevationDeg = 0,
    double Radius = 1.0);

/// <summary>
/// LAN control handshake keyed by MWB SecurityKey (HMAC), ports 15200/15201 (separate from MWB 15100/15101).
/// Interconnect: same SecurityKey → name-probe actively rejects with host name (no MWB matrix write).
/// Full audio mesh uses a second connect with intent=full.
/// </summary>
public sealed class SwbHandshakeService : IAsyncDisposable
{
    public const int DefaultControlPort = 15200;
    public const int DefaultAudioPort = 15201;
    public const int DefaultDiscoveryPort = 15202;
    public const int ProtocolVersion = 1;
    public const string IntentNameProbe = "name-probe";
    public const string IntentFull = "full";
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("SWB1");

    private readonly MwbSettings _mwb;
    private readonly CancellationTokenSource _cts = new();
    private TcpListener? _listener;
    private Task? _acceptLoop;
    private UdpClient? _discovery;
    private Task? _discoveryListen;
    private Task? _discoveryAnnounce;
    private readonly ConcurrentDictionary<string, string> _endpointHints =
        new(StringComparer.OrdinalIgnoreCase); // ipOrHost → last-seen host hint
    /// <summary>Exact hostnames already disclosed via name-probe (PeerNamed fires once each).</summary>
    private readonly ConcurrentDictionary<string, byte> _namedOnce =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _probeInFlight =
        new(StringComparer.OrdinalIgnoreCase);

    public int ControlPort { get; }
    public int AudioPort { get; }
    public int DiscoveryPort { get; }
    public SwbPeerRole LocalRole { get; set; } = SwbPeerRole.Both;
    public int SampleRate { get; set; } = 48000;
    public short Channels { get; set; } = 2;

    /// <summary>Local device pose advertised on hello / name-probe / full accept.</summary>
    public double LocalAzimuthDeg { get; set; }
    public double LocalElevationDeg { get; set; }
    public double LocalRadius { get; set; } = 1.0;

    public event Action<string>? Log;
    public event Action<SwbPeerInfo>? PeerConfirmed;
    /// <summary>Fired when a same-key peer discloses its hostname via name-probe reject (or UDP beacon).</summary>
    public event Action<SwbPeerInfo>? PeerNamed; // hostname disclosed (+ advertised pose)

    public SwbHandshakeService(
        MwbSettings mwb,
        int controlPort = DefaultControlPort,
        int audioPort = DefaultAudioPort,
        int discoveryPort = DefaultDiscoveryPort)
    {
        _mwb = mwb;
        ControlPort = controlPort;
        AudioPort = audioPort;
        DiscoveryPort = discoveryPort;
    }

    public async Task StartAsync()
    {
        if (string.IsNullOrWhiteSpace(_mwb.SecurityKey))
            throw new InvalidOperationException("MWB SecurityKey missing. Configure Mouse Without Borders first.");

        _listener = new TcpListener(IPAddress.Any, ControlPort);
        _listener.Start();
        Log?.Invoke($"SWB control listening on :{ControlPort} (audio UDP :{AudioPort})");
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));

        StartDiscovery();
        await Task.CompletedTask;
    }

    private void StartDiscovery()
    {
        try
        {
            _discovery = new UdpClient();
            _discovery.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _discovery.EnableBroadcast = true;
            _discovery.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));
            _discoveryListen = Task.Run(() => DiscoveryListenAsync(_cts.Token));
            _discoveryAnnounce = Task.Run(() => DiscoveryAnnounceAsync(_cts.Token));
            Log?.Invoke($"SWB discovery UDP :{DiscoveryPort} (beacon + name-probe once-per-name)");
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Discovery bind skipped: {ex.Message}");
        }
    }

    /// <summary>
    /// Simplified interconnect: optional MWB matrix hosts are hints only; LAN beacon supplies IPs;
    /// same SecurityKey → name-probe reject discloses hostname; then full handshake for audio.
    /// Does not write MWB registry/matrix.
    /// </summary>
    public async Task DiscoverAndConnectAsync(CancellationToken ct = default)
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hint in _mwb.PeerHostNames)
            candidates.Add(hint);
        foreach (var kv in _endpointHints)
            candidates.Add(kv.Key);

        // Let beacons arrive briefly when Synchro just enabled.
        try { await Task.Delay(1500, ct).ConfigureAwait(false); } catch { /* ignore */ }
        foreach (var kv in _endpointHints)
            candidates.Add(kv.Key);

        if (candidates.Count == 0)
        {
            Log?.Invoke("No SWB candidates yet (waiting for LAN beacon or optional MWB host hints).");
            return;
        }

        foreach (var target in candidates.ToList())
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var named = await ProbePeerNameAsync(target, ct).ConfigureAwait(false);
                if (named is null)
                    continue;

                RememberNamedPeer(named);

                var info = await HandshakeAsClientAsync(named.HostName, IntentFull, ct).ConfigureAwait(false)
                           ?? await HandshakeAsClientAsync(named.IpAddress, IntentFull, ct).ConfigureAwait(false);
                if (info != null)
                {
                    PeerConfirmed?.Invoke(info);
                    Log?.Invoke($"Handshake OK → {info.HostName} ({info.IpAddress}) stereo={info.StereoOk}");
                }
            }
            catch (Exception ex)
            {
                Log?.Invoke($"Handshake fail → {target}: {ex.Message}");
            }
        }
    }

    /// <summary>Legacy entry: same as DiscoverAndConnectAsync (matrix is hint-only).</summary>
    public Task ConnectPeersAsync(CancellationToken ct = default) => DiscoverAndConnectAsync(ct);

    /// <summary>
    /// Active reject-connect: auth with SecurityKey, server replies ok=false error=name-probe + host.
    /// </summary>
    public async Task<SwbPeerInfo?> ProbePeerNameAsync(string hostOrIp, CancellationToken ct = default)
    {
        try
        {
            using var client = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            await client.ConnectAsync(hostOrIp, ControlPort, timeout.Token).ConfigureAwait(false);
            await using var stream = client.GetStream();

            var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            var token = ComputeToken(_mwb.SecurityKey, nonce, _mwb.LocalHostName, "client");
            await WriteFrameAsync(stream, new
            {
                host = _mwb.LocalHostName,
                nonce,
                token,
                intent = IntentNameProbe,
                role = (int)LocalRole,
                sampleRate = SampleRate,
                channels = Channels,
                protocol = ProtocolVersion,
                azimuth = LocalAzimuthDeg,
                elevation = LocalElevationDeg,
                radius = LocalRadius,
            }, timeout.Token).ConfigureAwait(false);

            var reply = await ReadFrameAsync(stream, timeout.Token).ConfigureAwait(false);
            if (reply is null)
                return null;

            var err = reply.Value.TryGetProperty("error", out var e) ? e.GetString() : null;
            var remoteHost = reply.Value.TryGetProperty("host", out var h) ? h.GetString() : null;
            var ip = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? hostOrIp;

            if (string.Equals(err, IntentNameProbe, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(remoteHost))
            {
                Log?.Invoke($"Name-probe OK ← {remoteHost} @ {ip} (same key, reject-connect)");
                var (az, el, rad) = ReadPose(reply);
                return new SwbPeerInfo(remoteHost!, ip, ControlPort, AudioPort, SwbPeerRole.Both, SampleRate, Channels, Channels >= 2, az, el, rad);
            }

            // Key mismatch or unrelated deny — do not treat as named peer.
            if (string.Equals(err, "auth-failed", StringComparison.OrdinalIgnoreCase))
            {
                Log?.Invoke($"Name-probe auth-failed @ {hostOrIp} (different key)");
                return null;
            }

            // Old peer that still accepts full on any hello: use disclosed host if present.
            if (reply.Value.TryGetProperty("ok", out var okEl) && okEl.ValueKind == JsonValueKind.True
                && !string.IsNullOrWhiteSpace(remoteHost))
            {
                Log?.Invoke($"Name-probe legacy accept ← {remoteHost} @ {ip}");
                var (az2, el2, rad2) = ReadPose(reply);
                return new SwbPeerInfo(remoteHost!, ip, ControlPort, AudioPort, SwbPeerRole.Both, SampleRate, Channels, Channels >= 2, az2, el2, rad2);
            }

            Log?.Invoke($"Name-probe deny @ {hostOrIp}: {err ?? "unknown"}");
            return null;
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Name-probe fail → {hostOrIp}: {ex.Message}");
            return null;
        }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener != null)
        {
            TcpClient? client = null;
            try
            {
                client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                _ = Task.Run(() => HandleServerAsync(client, ct), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log?.Invoke($"Accept error: {ex.Message}");
                client?.Dispose();
            }
        }
    }

    private async Task HandleServerAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            await using var stream = client.GetStream();
            try
            {
                var hello = await ReadFrameAsync(stream, ct).ConfigureAwait(false);
                var challenge = hello?.GetProperty("nonce").GetString()
                                ?? throw new InvalidOperationException("missing nonce");
                var remoteHost = hello?.GetProperty("host").GetString() ?? "unknown";
                var intent = IntentFull;
                if (hello?.TryGetProperty("intent", out var intentEl) == true)
                    intent = intentEl.GetString() ?? IntentFull;

                // Key-first: do not gate on MWB MachineMatrix (matrix remains MWB-only).
                var expected = ComputeToken(_mwb.SecurityKey, challenge, remoteHost, "client");
                var got = hello?.GetProperty("token").GetString() ?? "";
                if (!CryptographicOperations.FixedTimeEquals(
                        Encoding.UTF8.GetBytes(expected),
                        Encoding.UTF8.GetBytes(got)))
                {
                    await WriteFrameAsync(stream, new { ok = false, error = "auth-failed" }, ct).ConfigureAwait(false);
                    Log?.Invoke($"Auth failed from {remoteHost}");
                    return;
                }

                var ip = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "";

                // Same key → actively reject name-probe and disclose local device/host name.
                if (string.Equals(intent, IntentNameProbe, StringComparison.OrdinalIgnoreCase))
                {
                    await WriteFrameAsync(stream, new
                    {
                        ok = false,
                        error = IntentNameProbe,
                        host = _mwb.LocalHostName,
                        controlPort = ControlPort,
                        audioPort = AudioPort,
                        protocol = ProtocolVersion,
                azimuth = LocalAzimuthDeg,
                elevation = LocalElevationDeg,
                radius = LocalRadius,
                    }, ct).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(ip))
                        _endpointHints[ip] = remoteHost;
                    Log?.Invoke($"Name-probe reject → disclosed host={_mwb.LocalHostName} to {remoteHost}");
                    return;
                }

                var serverNonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
                var serverToken = ComputeToken(_mwb.SecurityKey, serverNonce, _mwb.LocalHostName, "server");
                await WriteFrameAsync(stream, new
                {
                    ok = true,
                    host = _mwb.LocalHostName,
                    nonce = serverNonce,
                    token = serverToken,
                    controlPort = ControlPort,
                    audioPort = AudioPort,
                    role = (int)LocalRole,
                    sampleRate = SampleRate,
                    channels = Channels,
                    stereoOk = Channels >= 2,
                    protocol = ProtocolVersion,
                azimuth = LocalAzimuthDeg,
                elevation = LocalElevationDeg,
                radius = LocalRadius,
                }, ct).ConfigureAwait(false);

                var (raz, rel, rrad) = ReadPose(hello);
                PeerConfirmed?.Invoke(new SwbPeerInfo(
                    remoteHost, ip, ControlPort, AudioPort, SwbPeerRole.Both, SampleRate, Channels, Channels >= 2, raz, rel, rrad));
                Log?.Invoke($"Peer confirmed (inbound): {remoteHost}");
            }
            catch (Exception ex)
            {
                Log?.Invoke($"Server handshake error: {ex.Message}");
            }
        }
    }

    private async Task<SwbPeerInfo?> HandshakeAsClientAsync(string host, string intent, CancellationToken ct)
    {
        using var client = new TcpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        await client.ConnectAsync(host, ControlPort, timeout.Token).ConfigureAwait(false);
        await using var stream = client.GetStream();

        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var token = ComputeToken(_mwb.SecurityKey, nonce, _mwb.LocalHostName, "client");
        await WriteFrameAsync(stream, new
        {
            host = _mwb.LocalHostName,
            nonce,
            token,
            intent,
            role = (int)LocalRole,
            sampleRate = SampleRate,
            channels = Channels,
            protocol = ProtocolVersion,
                azimuth = LocalAzimuthDeg,
                elevation = LocalElevationDeg,
                radius = LocalRadius,
        }, timeout.Token).ConfigureAwait(false);

        var reply = await ReadFrameAsync(stream, timeout.Token).ConfigureAwait(false);
        if (reply is null || !reply.Value.GetProperty("ok").GetBoolean())
        {
            var err = reply?.TryGetProperty("error", out var e) == true ? e.GetString() : "denied";
            throw new InvalidOperationException(err ?? "denied");
        }

        var remoteHost = reply.Value.GetProperty("host").GetString() ?? host;
        var serverNonce = reply.Value.GetProperty("nonce").GetString() ?? "";
        var serverToken = reply.Value.GetProperty("token").GetString() ?? "";
        var expect = ComputeToken(_mwb.SecurityKey, serverNonce, remoteHost, "server");
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(expect),
                Encoding.UTF8.GetBytes(serverToken)))
            throw new InvalidOperationException("server-auth-failed");

        var audioPort = reply.Value.TryGetProperty("audioPort", out var ap) ? ap.GetInt32() : AudioPort;
        var sampleRate = reply.Value.TryGetProperty("sampleRate", out var sr) ? sr.GetInt32() : SampleRate;
        var channels = reply.Value.TryGetProperty("channels", out var ch) ? (short)ch.GetInt32() : Channels;
        var stereoOk = reply.Value.TryGetProperty("stereoOk", out var st) ? st.GetBoolean() : channels >= 2;
        var ip = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? host;

        var (caz, cel, crad) = ReadPose(reply);
        return new SwbPeerInfo(remoteHost, ip, ControlPort, audioPort, SwbPeerRole.Both, sampleRate, channels, stereoOk, caz, cel, crad);
    }

    private async Task DiscoveryListenAsync(CancellationToken ct)
    {
        if (_discovery is null) return;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await _discovery.ReceiveAsync(ct).ConfigureAwait(false);
                var text = Encoding.UTF8.GetString(result.Buffer);
                if (!text.StartsWith("SWB1DISC|", StringComparison.Ordinal))
                    continue;
                var parts = text.Split('|');
                if (parts.Length < 3) continue;
                var remoteHost = parts[1].Trim();
                if (string.IsNullOrWhiteSpace(remoteHost)
                    || remoteHost.Equals(_mwb.LocalHostName, StringComparison.OrdinalIgnoreCase))
                    continue;
                var ip = result.RemoteEndPoint.Address.ToString();
                if (IPAddress.IsLoopback(result.RemoteEndPoint.Address))
                    continue;
                // Beacon only supplies IP candidates; hostname confirmed by same-key name-probe reject.
                var isNew = !_endpointHints.ContainsKey(ip);
                _endpointHints[ip] = ip;
                Log?.Invoke($"Discovery beacon from {remoteHost} @ {ip}");
                if (isNew)
                    _ = Task.Run(() => ProbeNewEndpointAsync(ip, _cts.Token));
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex)
            {
                Log?.Invoke($"Discovery recv: {ex.Message}");
                try { await Task.Delay(500, ct).ConfigureAwait(false); } catch { break; }
            }
        }
    }

    private async Task DiscoveryAnnounceAsync(CancellationToken ct)
    {
        if (_discovery is null) return;
        var payload = Encoding.UTF8.GetBytes($"SWB1DISC|{_mwb.LocalHostName}|{ControlPort}");
        while (!ct.IsCancellationRequested)
        {
            try
            {
                foreach (var ep in EnumerateBroadcastEndpoints())
                {
                    try { await _discovery.SendAsync(payload, payload.Length, ep).ConfigureAwait(false); }
                    catch { /* ignore per-iface */ }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex)
            {
                Log?.Invoke($"Discovery announce: {ex.Message}");
            }

            try { await Task.Delay(2000, ct).ConfigureAwait(false); } catch { break; }
        }
    }

    private IEnumerable<IPEndPoint> EnumerateBroadcastEndpoints()
    {
        yield return new IPEndPoint(IPAddress.Broadcast, DiscoveryPort);
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback) continue;
            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (IPAddress.IsLoopback(ua.Address)) continue;
                var mask = ua.IPv4Mask;
                if (mask is null) continue;
                var ipBytes = ua.Address.GetAddressBytes();
                var maskBytes = mask.GetAddressBytes();
                var bcast = new byte[4];
                for (var i = 0; i < 4; i++)
                    bcast[i] = (byte)(ipBytes[i] | (byte)~maskBytes[i]);
                yield return new IPEndPoint(new IPAddress(bcast), DiscoveryPort);
            }
        }
    }


    private void RememberNamedPeer(SwbPeerInfo named)
    {
        if (named is null || string.IsNullOrWhiteSpace(named.HostName))
            return;
        if (!string.IsNullOrWhiteSpace(named.IpAddress))
            _endpointHints[named.IpAddress] = named.HostName;
        _endpointHints[named.HostName] = named.HostName;

        // PeerNamed only once for an exact identical hostname.
        if (!_namedOnce.TryAdd(named.HostName, 0))
            return;
        PeerNamed?.Invoke(named);
    }

    private async Task ProbeNewEndpointAsync(string hostOrIp, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(hostOrIp))
            return;
        if (!_probeInFlight.TryAdd(hostOrIp, 0))
            return;
        try
        {
            var named = await ProbePeerNameAsync(hostOrIp, ct).ConfigureAwait(false);
            if (named is null)
                return; // no name obtained → do not fill / do not PeerNamed
            RememberNamedPeer(named);

            try
            {
                var info = await HandshakeAsClientAsync(named.HostName, IntentFull, ct).ConfigureAwait(false)
                           ?? await HandshakeAsClientAsync(named.IpAddress, IntentFull, ct).ConfigureAwait(false);
                if (info != null)
                {
                    PeerConfirmed?.Invoke(info);
                    Log?.Invoke($"Handshake OK → {info.HostName} ({info.IpAddress}) stereo={info.StereoOk}");
                }
            }
            catch (Exception ex)
            {
                Log?.Invoke($"Handshake fail → {named.HostName}: {ex.Message}");
            }
        }
        catch (OperationCanceledException) { /* ignore */ }
        catch (Exception ex)
        {
            Log?.Invoke($"Beacon name-probe → {hostOrIp}: {ex.Message}");
        }
        finally
        {
            _probeInFlight.TryRemove(hostOrIp, out _);
        }
    }

    public static string ComputeToken(string securityKey, string nonce, string host, string side)
    {
        var material = $"{nonce}|{host}|{side}|SWB";
        var key = Encoding.UTF8.GetBytes(securityKey);
        var data = Encoding.UTF8.GetBytes(material);
        var hash = HMACSHA256.HashData(key, data);
        return Convert.ToHexString(hash);
    }


    private static (double az, double el, double r) ReadPose(JsonElement? el)
    {
        double az = 0, elev = 0, rad = 1.0;
        if (el is null) return (az, elev, rad);
        var v = el.Value;
        if (v.TryGetProperty("azimuth", out var a) && a.TryGetDouble(out var azv)) az = azv;
        if (v.TryGetProperty("elevation", out var e) && e.TryGetDouble(out var elv)) elev = elv;
        if (v.TryGetProperty("radius", out var rr) && rr.TryGetDouble(out var rv) && rv > 0) rad = rv;
        return (az, elev, rad);
    }

    private static async Task WriteFrameAsync(NetworkStream stream, object payload, CancellationToken ct)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(payload);
        var header = new byte[8];
        Magic.CopyTo(header, 0);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), json.Length);
        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        await stream.WriteAsync(json, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task<JsonElement?> ReadFrameAsync(NetworkStream stream, CancellationToken ct)
    {
        var header = new byte[8];
        await ReadExactAsync(stream, header, ct).ConfigureAwait(false);
        if (!header.AsSpan(0, 4).SequenceEqual(Magic))
            throw new InvalidOperationException("bad-magic");
        var len = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
        if (len is <= 0 or > 1_000_000)
            throw new InvalidOperationException("bad-length");
        var body = new byte[len];
        await ReadExactAsync(stream, body, ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.Clone();
    }

    private static async Task ReadExactAsync(NetworkStream stream, byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), ct).ConfigureAwait(false);
            if (n == 0) throw new EndOfStreamException();
            offset += n;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { _listener?.Stop(); } catch { /* ignore */ }
        try { _discovery?.Close(); } catch { /* ignore */ }
        _discovery?.Dispose();
        _discovery = null;
        if (_acceptLoop != null)
        {
            try { await _acceptLoop.ConfigureAwait(false); } catch { /* ignore */ }
        }
        if (_discoveryListen != null)
        {
            try { await _discoveryListen.ConfigureAwait(false); } catch { /* ignore */ }
        }
        if (_discoveryAnnounce != null)
        {
            try { await _discoveryAnnounce.ConfigureAwait(false); } catch { /* ignore */ }
        }
        _cts.Dispose();
    }
}