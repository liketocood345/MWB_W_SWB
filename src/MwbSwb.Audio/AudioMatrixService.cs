using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using MwbSwb.Core;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace MwbSwb.Audio;

public sealed class AudioMatrixService : IAsyncDisposable
{
    public const int FrameMagic = 0x42575331; // '1SWB' le-ish marker
    private readonly ConcurrentDictionary<string, RemoteEndpoint> _remotes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, BufferedWaveProvider> _rxBuffers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _mixerInputs = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _cts;
    private UdpClient? _udp;
    private WasapiLoopbackCapture? _capture;
    private WasapiOut? _output;
    private MixingSampleProvider? _mixer;
    private int _sampleRate = 48000;
    private bool _sendEnabled;
    private bool _recvEnabled = true;

    public event Action<string>? Log;
    public IReadOnlyDictionary<string, RemoteEndpoint> Remotes => _remotes;

    public sealed record RemoteEndpoint(string HostName, string IpAddress, int AudioPort, bool IncludeInMatrix, bool StereoOk);

    public void UpsertPeer(SwbPeerInfo peer, bool includeInMatrix = true)
    {
        _remotes[peer.HostName] = new RemoteEndpoint(peer.HostName, peer.IpAddress, peer.AudioPort, includeInMatrix, peer.StereoOk);
        EnsureRxBuffer(peer.HostName, peer.SampleRate);
        Log?.Invoke($"Matrix peer: {peer.HostName} @ {peer.IpAddress}:{peer.AudioPort} stereo={peer.StereoOk}");
    }

    public void SetInclude(string hostName, bool include)
    {
        if (_remotes.TryGetValue(hostName, out var r))
            _remotes[hostName] = r with { IncludeInMatrix = include };
    }

    public async Task StartAsync(int audioPort, int sampleRate = 48000, bool sendLocalLoopback = true, bool receiveAndMix = true)
    {
        await StopAsync().ConfigureAwait(false);
        _sampleRate = sampleRate;
        _sendEnabled = sendLocalLoopback;
        _recvEnabled = receiveAndMix;
        _cts = new CancellationTokenSource();

        _udp = new UdpClient(audioPort);
        _ = Task.Run(() => ReceiveLoopAsync(_cts.Token));

        if (_recvEnabled)
            StartPlaybackMixer();

        if (_sendEnabled)
            StartLoopbackCapture();

        Log?.Invoke($"Audio matrix up (UDP:{audioPort}, {sampleRate}Hz stereo, send={_sendEnabled}, recv={_recvEnabled})");
    }

    public Task StopAsync()
    {
        try { _cts?.Cancel(); } catch { /* ignore */ }
        try { _capture?.StopRecording(); } catch { /* ignore */ }
        _capture?.Dispose();
        _capture = null;
        try { _output?.Stop(); } catch { /* ignore */ }
        _output?.Dispose();
        _output = null;
        _mixer = null;
        _mixerInputs.Clear();
        try { _udp?.Close(); } catch { /* ignore */ }
        _udp?.Dispose();
        _udp = null;
        _cts?.Dispose();
        _cts = null;
        return Task.CompletedTask;
    }

    private void StartLoopbackCapture()
    {
        _capture = new WasapiLoopbackCapture();
        _capture.DataAvailable += (_, e) =>
        {
            if (e.BytesRecorded <= 0 || _udp == null || !_sendEnabled) return;
            try
            {
                using var raw = new RawSourceWaveStream(e.Buffer, 0, e.BytesRecorded, _capture.WaveFormat);
                var stereo = StereoPcmCodec.ToStereoFloat(raw);
                if (stereo.WaveFormat.SampleRate != _sampleRate)
                {
                    // light path: still send native rate; peers adapt buffer format on first packet meta
                }

                var frameSamples = 960; // ~20ms @ 48k
                var floats = new float[frameSamples * 2];
                int read;
                while ((read = stereo.Read(floats, 0, floats.Length)) > 0)
                {
                    var bytes = new byte[read * sizeof(float)];
                    Buffer.BlockCopy(floats, 0, bytes, 0, bytes.Length);
                    BroadcastFrame(bytes, stereo.WaveFormat.SampleRate, (short)stereo.WaveFormat.Channels);
                    if (read < floats.Length) break;
                }
            }
            catch (Exception ex)
            {
                Log?.Invoke($"Capture send error: {ex.Message}");
            }
        };
        _capture.RecordingStopped += (_, e) =>
        {
            if (e.Exception != null)
                Log?.Invoke($"Capture stopped: {e.Exception.Message}");
        };
        _capture.StartRecording();
    }

    private void BroadcastFrame(byte[] pcmFloatStereo, int sampleRate, short channels)
    {
        if (_udp == null) return;
        var packet = BuildPacket(Environment.MachineName, sampleRate, channels, pcmFloatStereo);
        foreach (var remote in _remotes.Values)
        {
            if (!remote.IncludeInMatrix) continue;
            try
            {
                _udp.Send(packet, packet.Length, remote.IpAddress, remote.AudioPort);
            }
            catch (Exception ex)
            {
                Log?.Invoke($"UDP → {remote.HostName}: {ex.Message}");
            }
        }
    }

    private void StartPlaybackMixer()
    {
        var format = StereoPcmCodec.StereoFloatFormat(_sampleRate);
        _mixer = new MixingSampleProvider(format) { ReadFully = true };
        foreach (var kv in _rxBuffers)
        {
            if (_mixerInputs.TryAdd(kv.Key, 0))
                _mixer.AddMixerInput(kv.Value.ToSampleProvider());
        }

        _output = new WasapiOut(AudioClientShareMode.Shared, 50);
        _output.Init(_mixer);
        _output.Play();
    }

    private void EnsureRxBuffer(string host, int sampleRate)
    {
        var format = StereoPcmCodec.StereoFloatFormat(sampleRate);
        var buffer = _rxBuffers.GetOrAdd(host, _ =>
        {
            var b = new BufferedWaveProvider(format)
            {
                DiscardOnBufferOverflow = true,
                BufferDuration = TimeSpan.FromMilliseconds(400),
            };
            return b;
        });

        if (_mixer != null && _mixerInputs.TryAdd(host, 0))
            _mixer.AddMixerInput(buffer.ToSampleProvider());
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _udp != null)
        {
            try
            {
                var result = await _udp.ReceiveAsync(ct).ConfigureAwait(false);
                if (!_recvEnabled) continue;
                if (!TryParsePacket(result.Buffer, out var host, out var sampleRate, out var channels, out var payload))
                    continue;
                if (host.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (channels < 2)
                {
                    Log?.Invoke($"Drop non-stereo frame from {host} (ch={channels})");
                    continue;
                }

                EnsureRxBuffer(host, sampleRate);
                if (_rxBuffers.TryGetValue(host, out var buf))
                    buf.AddSamples(payload, 0, payload.Length);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested)
                    Log?.Invoke($"UDP recv: {ex.Message}");
            }
        }
    }

    private static byte[] BuildPacket(string host, int sampleRate, short channels, byte[] payload)
    {
        var hostBytes = Encoding.UTF8.GetBytes(host);
        if (hostBytes.Length > 64) hostBytes = hostBytes.AsSpan(0, 64).ToArray();
        var packet = new byte[4 + 1 + 64 + 4 + 2 + 4 + payload.Length];
        var span = packet.AsSpan();
        BinaryPrimitives.WriteInt32LittleEndian(span, FrameMagic);
        span[4] = (byte)hostBytes.Length;
        hostBytes.CopyTo(span[5..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[69..], sampleRate);
        BinaryPrimitives.WriteInt16LittleEndian(span[73..], channels);
        BinaryPrimitives.WriteInt32LittleEndian(span[75..], payload.Length);
        payload.CopyTo(span[79..]);
        return packet;
    }

    private static bool TryParsePacket(byte[] buffer, out string host, out int sampleRate, out short channels, out byte[] payload)
    {
        host = "";
        sampleRate = 0;
        channels = 0;
        payload = Array.Empty<byte>();
        if (buffer.Length < 79) return false;
        var span = buffer.AsSpan();
        if (BinaryPrimitives.ReadInt32LittleEndian(span) != FrameMagic) return false;
        var hostLen = span[4];
        if (hostLen is 0 or > 64) return false;
        host = Encoding.UTF8.GetString(span.Slice(5, hostLen));
        sampleRate = BinaryPrimitives.ReadInt32LittleEndian(span[69..]);
        channels = BinaryPrimitives.ReadInt16LittleEndian(span[73..]);
        var len = BinaryPrimitives.ReadInt32LittleEndian(span[75..]);
        if (len < 0 || 79 + len > buffer.Length) return false;
        payload = span.Slice(79, len).ToArray();
        return true;
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
