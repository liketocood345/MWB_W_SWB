using NAudio.Wave;

namespace MwbSwb.Audio;

/// <summary>
/// Clocked by the primary WasapiOut: each Read from MatrixSynth is copied into
/// per-slave BufferedWaveProviders so additional render devices can follow.
/// </summary>
public sealed class PlaybackFanOut : ISampleProvider, IDisposable
{
    private readonly ISampleProvider _source;
    private readonly List<(BufferedWaveProvider Buf, WasapiOut Out)> _slaves = new();
    private readonly byte[] _byteScratch = new byte[192000];
    private readonly object _gate = new();
    private bool _disposed;

    public PlaybackFanOut(ISampleProvider source) => _source = source;

    public WaveFormat WaveFormat => _source.WaveFormat;

    public void AddSlave(WasapiOut output, BufferedWaveProvider buffer)
    {
        lock (_gate) _slaves.Add((buffer, output));
    }

    public int Read(float[] buffer, int offset, int count)
    {
        var n = _source.Read(buffer, offset, count);
        if (n <= 0) return n;
        lock (_gate)
        {
            if (_slaves.Count == 0) return n;
            var bytes = n * 4;
            if (bytes > _byteScratch.Length) return n;
            Buffer.BlockCopy(buffer, offset * 4, _byteScratch, 0, bytes);
            foreach (var (buf, _) in _slaves)
            {
                try
                {
                    // Drop oldest if overflowing so slaves stay near-live.
                    var free = buf.BufferLength - buf.BufferedBytes;
                    if (free < bytes)
                        buf.ClearBuffer();
                    buf.AddSamples(_byteScratch, 0, bytes);
                }
                catch { /* ignore slave underrun/overflow */ }
            }
        }
        return n;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_gate)
        {
            foreach (var (_, o) in _slaves)
            {
                try { o.Stop(); } catch { }
                try { o.Dispose(); } catch { }
            }
            _slaves.Clear();
        }
    }
}
