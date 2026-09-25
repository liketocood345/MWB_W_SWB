namespace MwbSwb.Audio;

/// <summary>Ring of mixed playback floats for echo reference (stereo interleaved).</summary>
public sealed class PlaybackReferenceRing
{
    private readonly float[] _buf;
    private readonly object _lock = new();
    private int _write;
    private long _totalWritten;
    private int _delaySamples;

    public PlaybackReferenceRing(int capacityFramesStereo)
    {
        var cap = Math.Max(capacityFramesStereo * 2, 48000 * 2);
        _buf = new float[cap];
        _delaySamples = 4800; // ~50ms @ 48k stereo interleaved count = samples*channels... we store interleaved so delay in stereo frames * 2
    }

    /// <summary>Delay in stereo frames (pairs).</summary>
    public void SetDelayFrames(int stereoFrames)
    {
        lock (_lock)
            _delaySamples = Math.Clamp(stereoFrames * 2, 0, _buf.Length - 4);
    }

    public void Write(ReadOnlySpan<float> interleavedStereo)
    {
        lock (_lock)
        {
            for (var i = 0; i < interleavedStereo.Length; i++)
            {
                _buf[_write] = interleavedStereo[i];
                _write = (_write + 1) % _buf.Length;
                _totalWritten++;
            }
        }
    }

    /// <summary>Copy delayed reference into dest (same length as requested).</summary>
    public void ReadDelayed(Span<float> dest)
    {
        lock (_lock)
        {
            if (_totalWritten < _delaySamples + dest.Length)
            {
                dest.Clear();
                return;
            }
            var start = _write - _delaySamples - dest.Length;
            while (start < 0) start += _buf.Length;
            for (var i = 0; i < dest.Length; i++)
            {
                dest[i] = _buf[(start + i) % _buf.Length];
            }
        }
    }
}
