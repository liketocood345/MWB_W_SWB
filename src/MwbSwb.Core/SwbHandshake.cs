using System.Buffers.Binary;
using System.Net;
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
    bool StereoOk);

/// <summary>
/// LAN control handshake keyed by MWB SecurityKey (HMAC), separate ports from MWB (15100/15101).
/// </summary>
public sealed class SwbHandshakeService : IAsyncDisposable
{
    public const int DefaultControlPort = 15200;
    public const int DefaultAudioPort = 15201;
    public const int ProtocolVersion = 1;
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("SWB1");

    private readonly MwbSettings _mwb;
    private readonly CancellationTokenSource _cts = new();
    private TcpListener? _listener;
    private Task? _acceptLoop;

    public int ControlPort { get; }
    public int AudioPort { get; }
    public SwbPeerRole LocalRole { get; set; } = SwbPeerRole.Both;
    public int SampleRate { get; set; } = 48000;
    public short Channels { get; set; } = 2;

    public event Action<string>? Log;
    public event Action<SwbPeerInfo>? PeerConfirmed;

    public SwbHandshakeService(MwbSettings mwb, int controlPort = DefaultControlPort, int audioPort = DefaultAudioPort)
    {
        _mwb = mwb;
        ControlPort = controlPort;
        AudioPort = audioPort;
    }

    public async Task StartAsync()
    {
        if (string.IsNullOrWhiteSpace(_mwb.SecurityKey))
            throw new InvalidOperationException("MWB SecurityKey missing. Configure Mouse Without Borders first.");

        _listener = new TcpListener(IPAddress.Any, ControlPort);
        _listener.Start();
        Log?.Invoke($"SWB control listening on :{ControlPort} (audio UDP :{AudioPort})");
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
        await Task.CompletedTask;
    }

    public async Task ConnectPeersAsync(CancellationToken ct = default)
    {
        foreach (var peer in _mwb.PeerHostNames)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var info = await HandshakeAsClientAsync(peer, ct).ConfigureAwait(false);
                if (info != null)
                {
                    PeerConfirmed?.Invoke(info);
                    Log?.Invoke($"Handshake OK → {info.HostName} ({info.IpAddress}) stereo={info.StereoOk}");
                }
            }
            catch (Exception ex)
            {
                Log?.Invoke($"Handshake fail → {peer}: {ex.Message}");
            }
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

            if (!IsAllowedPeer(remoteHost))
            {
                await WriteFrameAsync(stream, new { ok = false, error = "peer-not-in-mwb-matrix" }, ct).ConfigureAwait(false);
                Log?.Invoke($"Rejected {remoteHost}: not in MWB matrix");
                return;
            }

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
            }, ct).ConfigureAwait(false);

            var ip = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "";
            PeerConfirmed?.Invoke(new SwbPeerInfo(
                remoteHost, ip, ControlPort, AudioPort, SwbPeerRole.Both, SampleRate, Channels, Channels >= 2));
            Log?.Invoke($"Peer confirmed (inbound): {remoteHost}");
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Server handshake error: {ex.Message}");
        }
        }
    }

    private async Task<SwbPeerInfo?> HandshakeAsClientAsync(string host, CancellationToken ct)
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
            role = (int)LocalRole,
            sampleRate = SampleRate,
            channels = Channels,
            protocol = ProtocolVersion,
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

        return new SwbPeerInfo(remoteHost, ip, ControlPort, audioPort, SwbPeerRole.Both, sampleRate, channels, stereoOk);
    }

    private bool IsAllowedPeer(string hostName) =>
        _mwb.MachineMatrix.Any(m => m.Equals(hostName, StringComparison.OrdinalIgnoreCase))
        || _mwb.PeerHostNames.Any(m => m.Equals(hostName, StringComparison.OrdinalIgnoreCase));

    public static string ComputeToken(string securityKey, string nonce, string host, string side)
    {
        var material = $"{nonce}|{host}|{side}|SWB";
        var key = Encoding.UTF8.GetBytes(securityKey);
        var data = Encoding.UTF8.GetBytes(material);
        var hash = HMACSHA256.HashData(key, data);
        return Convert.ToHexString(hash);
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
        if (_acceptLoop != null)
        {
            try { await _acceptLoop.ConfigureAwait(false); } catch { /* ignore */ }
        }
        _cts.Dispose();
    }
}
