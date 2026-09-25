using NAudio.Wave;

namespace MwbSwb.Audio;

/// <summary>
/// Host-side RX dumps when WasapiLoopbackCapture fights WasapiOut on VMware.
/// Arm: LocalAppData/Public MWB-SWB-Host/dump-rx.flag
///   dump-rx.wav     = mix to WasapiOut (playback proxy)
///   dump-rx-net.wav = UDP RX floats at push (pre-jitter)
/// Timer starts on first energetic Write so boot silence does not burn the window.
/// </summary>
public static class RxDumpSession
{
    private static readonly object Gate = new();
    private static WaveFileWriter? _mixWriter;
    private static WaveFileWriter? _netWriter;
    private static FileStream? _mixFs;
    private static FileStream? _netFs;
    private static int _sampleRate;
    private static long _startedQpc;
    private static bool _started;
    private static int _maxSeconds = 20;

    public static bool IsArmed =>
        File.Exists(FlagPathLocal) || File.Exists(FlagPathPublic);

    private static string FlagPathLocal =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MWB-SWB-Host", "dump-rx.flag");

    private static string FlagPathPublic => @"C:\Users\Public\MWB-SWB-Host\dump-rx.flag";

    private static string DirPublic => Path.GetDirectoryName(FlagPathPublic)!;

    public static void TryArm(int sampleRate, int maxSeconds = 20)
    {
        if (!IsArmed) return;
        lock (Gate)
        {
            if (_mixWriter != null) return;
            OpenWriters_NoLock(sampleRate, maxSeconds);
        }
    }

    private static void OpenWriters_NoLock(int sampleRate, int maxSeconds)
    {
        _sampleRate = sampleRate <= 0 ? 48000 : sampleRate;
        _maxSeconds = Math.Clamp(maxSeconds, 3, 60);
        _started = false;
        _startedQpc = 0;
        Directory.CreateDirectory(DirPublic);
        var mixPath = Path.Combine(DirPublic, "dump-rx.wav");
        var netPath = Path.Combine(DirPublic, "dump-rx-net.wav");
        try { File.Delete(mixPath); } catch { /* ignore */ }
        try { File.Delete(netPath); } catch { /* ignore */ }
        _mixFs = new FileStream(mixPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        _netFs = new FileStream(netPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        var fmt = new WaveFormat(_sampleRate, 16, 2);
        _mixWriter = new WaveFileWriter(_mixFs, fmt);
        _netWriter = new WaveFileWriter(_netFs, fmt);
    }

    /// <summary>Mix path (what Host feeds WasapiOut).</summary>
    public static void Write(ReadOnlySpan<float> interleavedStereo) =>
        WriteTo(ref _mixWriter, interleavedStereo, reopenIfNeeded: true);

    /// <summary>UDP RX floats at HandleIncomingPacket (pre-jitter).</summary>
    public static void WriteNet(ReadOnlySpan<float> interleavedStereo) =>
        WriteTo(ref _netWriter, interleavedStereo, reopenIfNeeded: true);

    private static void WriteTo(ref WaveFileWriter? writer, ReadOnlySpan<float> interleavedStereo, bool reopenIfNeeded)
    {
        lock (Gate)
        {
            if (!IsArmed) return;
            if (writer == null)
            {
                if (!reopenIfNeeded) return;
                // Boot silence burned prior window — reopen so Kill Bill still lands.
                if (_mixWriter == null && _netWriter == null)
                    OpenWriters_NoLock(_sampleRate > 0 ? _sampleRate : 48000, _maxSeconds);
                if (writer == null) return;
            }

            float peak = 0;
            for (var i = 0; i < interleavedStereo.Length; i++)
            {
                var a = Math.Abs(interleavedStereo[i]);
                if (a > peak) peak = a;
            }

            if (!_started)
            {
                if (peak < 0.01f) return; // skip boot silence
                _started = true;
                _startedQpc = System.Diagnostics.Stopwatch.GetTimestamp();
            }

            var elapsed = (System.Diagnostics.Stopwatch.GetTimestamp() - _startedQpc)
                          * 1.0 / System.Diagnostics.Stopwatch.Frequency;
            if (elapsed > _maxSeconds)
            {
                Close_NoLock();
                return;
            }

            try
            {
                var buf = interleavedStereo.ToArray();
                writer.WriteSamples(buf, 0, buf.Length);
            }
            catch { /* drop */ }
        }
    }

    public static void Stop()
    {
        lock (Gate) Close_NoLock();
    }

    private static void Close_NoLock()
    {
        try { _mixWriter?.Flush(); } catch { }
        try { _netWriter?.Flush(); } catch { }
        try { _mixWriter?.Dispose(); } catch { }
        try { _netWriter?.Dispose(); } catch { }
        try { _mixFs?.Dispose(); } catch { }
        try { _netFs?.Dispose(); } catch { }
        _mixWriter = null;
        _netWriter = null;
        _mixFs = null;
        _netFs = null;
        _started = false;
        // keep dump-rx.flag so soft-restart can re-arm; operator deletes after pull
    }
}
