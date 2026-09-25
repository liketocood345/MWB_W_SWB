namespace MwbSwb.Audio;

/// <summary>Per-peer float stereo ring with sticky watermark startup gate and simple PLC.</summary>
public sealed class JitterRing
{
    private readonly float[] _buf;
    private readonly object _lock = new();
    private int _write;
    private int _read;
    private int _count;
    private int _targetSamples;
    private int _extraOffsetSamples;
    private ushort _lastSeq;
    private bool _haveSeq;
    private readonly float[] _lastFrame;
    private int _lastFrameLen;
    private bool _plc;
    private bool _primed;
    private bool _holdSilentUntilPrimed = true;
    private int _underrunEvents;
    private int _energySamples;

    public int UnderrunEvents => Volatile.Read(ref _underrunEvents);

    public JitterRing(int capacityMs, int sampleRate, bool plc)
    {
        var frames = Math.Max(sampleRate * capacityMs / 1000, sampleRate / 10);
        _buf = new float[frames * 2];
        _lastFrame = new float[sampleRate / 25 * 2];
        _plc = plc;
        SetTargetMs(30, sampleRate);
    }

    public void SetPlc(bool plc) => _plc = plc;

    public void SetHoldSilentUntilPrimed(bool silent)
    {
        lock (_lock) _holdSilentUntilPrimed = silent;
    }

    public bool IsPrimed
    {
        get { lock (_lock) return _primed; }
    }

    public int Available
    {
        get { lock (_lock) return _count; }
    }

    public bool HasReceivedData
    {
        get { lock (_lock) return _haveSeq; }
    }

    public void SetTargetMs(int ms, int sampleRate)
    {
        lock (_lock)
        {
            _targetSamples = Math.Max(2, ms * sampleRate / 1000 * 2);
            _primed = false;
            _energySamples = 0;
        }
    }

    public void SetExtraOffsetMs(int ms, int sampleRate)
    {
        lock (_lock)
        {
            _extraOffsetSamples = Math.Max(0, ms * sampleRate / 1000 * 2);
            _primed = false;
            _energySamples = 0;
        }
    }

    public void ResetPrimed()
    {
        lock (_lock)
        {
            _primed = false;
            _energySamples = 0;
        }
    }

    public void Push(ReadOnlySpan<float> interleaved, ushort seq)
    {
        lock (_lock)
        {
            if (_haveSeq)
            {
                var expect = (ushort)(_lastSeq + 1);
                if (seq != expect && seq != _lastSeq) { /* gap */ }
            }
            _lastSeq = seq;
            _haveSeq = true;

            float peak = 0;
            for (var i = 0; i < interleaved.Length; i++)
            {
                if (_count >= _buf.Length)
                {
                    _read = (_read + 1) % _buf.Length;
                    _count--;
                }
                var s = interleaved[i];
                _buf[_write] = s;
                _write = (_write + 1) % _buf.Length;
                _count++;
                var a = s < 0 ? -s : s;
                if (a > peak) peak = a;
            }

            var keep = Math.Min(interleaved.Length, _lastFrame.Length);
            interleaved[..keep].CopyTo(_lastFrame);
            _lastFrameLen = keep;

            // Force-sync: only energetic audio counts toward prime (silence must not release hold).
            if (peak >= 0.02f)
                _energySamples += interleaved.Length;
        }
    }

    public void Read(Span<float> dest)
    {
        lock (_lock)
        {
            var need = dest.Length;
            var target = _targetSamples + _extraOffsetSamples;

            if (!_primed)
            {
                var needEnergy = _holdSilentUntilPrimed && _energySamples < target;
                if (_count < target || needEnergy)
                {
                    if (!_holdSilentUntilPrimed && _plc && _lastFrameLen > 0)
                        FillPlc(dest);
                    else
                        dest.Clear();
                    return;
                }
                _primed = true;
            }

            var take = Math.Min(need, _count);
            if (_primed && take < need)
                Interlocked.Increment(ref _underrunEvents);

            for (var i = 0; i < take; i++)
            {
                dest[i] = _buf[_read];
                _read = (_read + 1) % _buf.Length;
                _count--;
            }
            if (take < need)
            {
                if (_plc && _lastFrameLen > 0)
                    FillPlc(dest[take..]);
                else
                    dest[take..].Clear();
            }
            else
            {
                var keep = Math.Min(need, _lastFrame.Length);
                dest[..keep].CopyTo(_lastFrame);
                _lastFrameLen = keep;
            }
        }
    }

    private void FillPlc(Span<float> dest)
    {
        if (_lastFrameLen <= 0)
        {
            dest.Clear();
            return;
        }
        for (var i = 0; i < dest.Length; i++)
        {
            var v = _lastFrame[i % _lastFrameLen] * 0.85f;
            dest[i] = v;
            _lastFrame[i % _lastFrameLen] = v;
        }
    }
}
