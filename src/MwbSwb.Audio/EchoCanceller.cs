namespace MwbSwb.Audio;

/// <summary>Lightweight electrical echo canceller: subtract delayed playback reference from loopback.</summary>
public sealed class EchoCanceller
{
    private readonly PlaybackReferenceRing _reference;
    private float _gain = 0.85f;
    private readonly float[] _refScratch = new float[8192];

    public EchoCanceller(PlaybackReferenceRing reference) => _reference = reference;

    /// <summary>
    /// In-place: captureFloat -= gain * delayed(ref).
    /// Returns residual peak (0..1). If near silence after cancel, caller may skip send.
    /// </summary>
    public float Process(Span<float> captureFloat)
    {
        if (captureFloat.Length == 0) return 0;
        if (captureFloat.Length > _refScratch.Length)
        {
            // Process in chunks
            float peak = 0;
            for (var off = 0; off < captureFloat.Length; off += _refScratch.Length)
            {
                var len = Math.Min(_refScratch.Length, captureFloat.Length - off);
                peak = Math.Max(peak, ProcessChunk(captureFloat.Slice(off, len)));
            }
            return peak;
        }
        return ProcessChunk(captureFloat);
    }

    private float ProcessChunk(Span<float> capture)
    {
        var scratch = _refScratch.AsSpan(0, capture.Length);
        _reference.ReadDelayed(scratch);

        double capE = 0, refE = 0;
        for (var i = 0; i < capture.Length; i++)
        {
            var c = capture[i];
            var r = scratch[i];
            capE += c * c;
            refE += r * r;
        }
        if (refE > 1e-8)
        {
            var ratio = (float)Math.Sqrt(capE / refE);
            var target = Math.Clamp(ratio, 0.15f, 1.2f);
            _gain = _gain * 0.9f + target * 0.1f;
        }

        float peak = 0;
        for (var i = 0; i < capture.Length; i++)
        {
            var v = capture[i] - _gain * scratch[i];
            if (v > 1f) v = 1f;
            else if (v < -1f) v = -1f;
            capture[i] = v;
            var a = v < 0 ? -v : v;
            if (a > peak) peak = a;
        }
        return peak > 1f ? 1f : peak;
    }

    /// <summary>True when residual is mostly echo of remote playback.</summary>
    public static bool ShouldSuppressSend(float residualPeak, float rxHold, float capturePeakBefore, float threshold = 0.035f) =>
        rxHold > 0.05f
        && capturePeakBefore > 0.04f
        && residualPeak < threshold
        && residualPeak < capturePeakBefore * 0.35f;
}
