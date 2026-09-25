using System.Collections.Concurrent;
using System.Diagnostics;
using MwbSwb.Core;
using NAudio.Wave;

namespace MwbSwb.Audio;

/// <summary>Single-bus mixer: per-peer JitterRings -> pan/gain -> master float out + reference tap.</summary>
public sealed class MatrixSynth : ISampleProvider
{
    private readonly ConcurrentDictionary<string, JitterRing> _rings = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (float left, float right)> _gains = new(StringComparer.OrdinalIgnoreCase);
    private readonly PlaybackReferenceRing _reference;
    private readonly WaveFormat _format;
    private readonly float[] _scratch;
    private readonly float[] _mix;
    private SoundSynchroSettings? _spatial;
    private int _sampleRate;
    private bool _plc;
    private double _syncAlignStrength;
    private long _barrierStartQpc;
    private bool _barrierReleased;
    private const double BarrierTimeoutMs = 1500;

    public MatrixSynth(int sampleRate, PlaybackReferenceRing reference)
    {
        _sampleRate = sampleRate;
        _reference = reference;
        _format = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2);
        _scratch = new float[sampleRate / 10 * 2];
        _mix = new float[sampleRate / 10 * 2];
    }

    public WaveFormat WaveFormat => _format;
    public PlaybackReferenceRing Reference => _reference;

    /// <summary>Raised once when sticky prime / barrier first allows audible mix (for latency stage log).</summary>
    public event Action? FirstAudible;

    public int UnderrunEvents
    {
        get
        {
            var n = 0;
            foreach (var r in _rings.Values)
                n += r.UnderrunEvents;
            return n;
        }
    }

    private int _firstAudibleFired;

    public void ApplySpatial(SoundSynchroSettings settings)
    {
        _spatial = settings;
        _gains.Clear();
        foreach (var pose in settings.Layout)
            _gains[pose.HostName] = SpatialPan.Gains(pose, settings.DistanceAttenuation, settings.SpatialMode);
    }

    public void ConfigureTier(int tier, int sampleRate)
    {
        _sampleRate = sampleRate;
        var p = AudioTier.Get(tier);
        _plc = p.LightPlc || p.FullPlc;
        _syncAlignStrength = p.SyncAlignStrength;
        var targetMs = (p.JitterTargetMs + p.JitterTargetMsHi) / 2;
        var holdSilent = p.SyncAlignStrength >= 0.99; // t0: hard silence hold
        foreach (var ring in _rings.Values)
        {
            ring.SetTargetMs(targetMs, sampleRate);
            ring.SetPlc(_plc);
            ring.SetHoldSilentUntilPrimed(holdSilent || !_plc);
        }
        _reference.SetDelayFrames(Math.Max(1, p.PlayLatencyMs * sampleRate / 1000));
        _barrierStartQpc = 0;
        _barrierReleased = false;
        Interlocked.Exchange(ref _firstAudibleFired, 0);
    }

    public JitterRing EnsurePeer(string host, int tier, int sampleRate)
    {
        return _rings.GetOrAdd(host, _ =>
        {
            var p = AudioTier.Get(tier);
            var ring = new JitterRing(capacityMs: 200, sampleRate: sampleRate, plc: p.LightPlc || p.FullPlc);
            ring.SetTargetMs((p.JitterTargetMs + p.JitterTargetMsHi) / 2, sampleRate);
            ring.SetHoldSilentUntilPrimed(p.SyncAlignStrength >= 0.99 || !(p.LightPlc || p.FullPlc));
            return ring;
        });
    }

    public void SetPeerSyncOffsetMs(string host, int offsetMs)
    {
        if (_rings.TryGetValue(host, out var ring))
            ring.SetExtraOffsetMs(offsetMs, _sampleRate);
        _barrierReleased = false;
        _barrierStartQpc = 0;
        Interlocked.Exchange(ref _firstAudibleFired, 0);
    }

    public void ClearSyncOffsets()
    {
        foreach (var ring in _rings.Values)
            ring.SetExtraOffsetMs(0, _sampleRate);
    }

    public (float left, float right) GainsFor(string host)
    {
        if (_gains.TryGetValue(host, out var g)) return g;
        if (_spatial != null)
        {
            var pose = _spatial.GetOrCreatePose(host);
            g = SpatialPan.Gains(pose, _spatial.DistanceAttenuation, _spatial.SpatialMode);
            _gains[host] = g;
            return g;
        }
        return (1f, 1f);
    }

    /// <summary>t0 only: hold mix silent until every peer that has received data is primed (or timeout).</summary>
    private bool ForceSyncBarrierAllowsMix()
    {
        if (_syncAlignStrength < 0.99)
            return true;

        if (_barrierReleased)
            return true;

        if (_barrierStartQpc == 0)
            _barrierStartQpc = Stopwatch.GetTimestamp();

        var peersWithData = _rings.Where(kv => kv.Value.HasReceivedData).ToList();
        if (peersWithData.Count == 0)
            return false; // wait for first packet

        if (peersWithData.All(kv => kv.Value.IsPrimed))
        {
            _barrierReleased = true;
            return true;
        }

        var elapsedMs = (Stopwatch.GetTimestamp() - _barrierStartQpc) * 1000.0 / Stopwatch.Frequency;
        if (elapsedMs >= BarrierTimeoutMs)
        {
            _barrierReleased = true; // avoid permanent silence
            return true;
        }

        return false;
    }

    public int Read(float[] buffer, int offset, int count)
    {
        if (count > _mix.Length)
            count = _mix.Length;
        var mix = _mix.AsSpan(0, count);
        mix.Clear();

        if (!ForceSyncBarrierAllowsMix())
        {
            // Global hold: still drain nothing / keep rings accumulating via Push on RX thread.
            buffer.AsSpan(offset, count).Clear();
            _reference.Write(mix);
            RxDumpSession.Write(mix);
            return count;
        }

        var anyAudio = false;
        foreach (var kv in _rings)
        {
            var host = kv.Key;
            var ring = kv.Value;
            var scratch = _scratch.AsSpan(0, count);
            ring.Read(scratch);
            var (left, right) = GainsFor(host);
            for (var i = 0; i + 1 < count; i += 2)
            {
                mix[i] += scratch[i] * left;
                mix[i + 1] += scratch[i + 1] * right;
            }
            if (!anyAudio)
            {
                for (var i = 0; i < count; i++)
                {
                    if (Math.Abs(mix[i]) > 1e-5f) { anyAudio = true; break; }
                }
            }
        }

        // soft clip
        for (var i = 0; i < count; i++)
        {
            var v = mix[i];
            if (v > 1f) v = 1f;
            else if (v < -1f) v = -1f;
            buffer[offset + i] = v;
            mix[i] = v;
        }

        if (anyAudio && Interlocked.CompareExchange(ref _firstAudibleFired, 1, 0) == 0)
            FirstAudible?.Invoke();

        _reference.Write(mix);
            RxDumpSession.Write(mix);
        return count;
    }
}
