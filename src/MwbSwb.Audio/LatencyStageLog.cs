using System.Diagnostics;

namespace MwbSwb.Audio;

/// <summary>QPC stage marks. softSigma = first energetic RX to first audible.</summary>
public static class LatencyStageLog
{
    private const string Path = @"C:\Users\Public\swb-latency-stages.log";
    private static long _lastWriteTick;
    private static long _rxQpc;
    private static int _capLogged;
    private static int _rxLogged;
    private static int _audibleLogged;

    public static void BeginSession(string note)
    {
        _rxQpc = 0;
        Interlocked.Exchange(ref _capLogged, 0);
        Interlocked.Exchange(ref _rxLogged, 0);
        Interlocked.Exchange(ref _audibleLogged, 0);
        TryAppend("SESSION " + note + " utc=" + DateTimeOffset.UtcNow.ToString("o"), true);
    }

    public static void MarkCaptureSend()
    {
        if (Interlocked.CompareExchange(ref _capLogged, 1, 0) != 0) return;
        TryAppend("L1_cap_send", true);
    }

    public static void MarkUdpRxPush(float peak)
    {
        if (peak < 0.02f) return;
        if (Interlocked.CompareExchange(ref _rxLogged, 1, 0) != 0) return;
        _rxQpc = Stopwatch.GetTimestamp();
        TryAppend("L6_rx_push peak=" + peak.ToString("F3"), true);
    }

    public static void MarkFirstAudible()
    {
        if (Interlocked.CompareExchange(ref _audibleLogged, 1, 0) != 0) return;
        var now = Stopwatch.GetTimestamp();
        var sinceRx = _rxQpc > 0 ? (now - _rxQpc) * 1000.0 / Stopwatch.Frequency : -1;
        TryAppend("L9_first_audible sinceRx=" + sinceRx.ToString("F2") + "ms softSigma=" + sinceRx.ToString("F1") + "ms", true);
    }

    private static void TryAppend(string line, bool force = false)
    {
        var now = Environment.TickCount64;
        if (!force && now - _lastWriteTick < 5) return;
        _lastWriteTick = now;
        try { File.AppendAllText(Path, "[" + DateTime.Now.ToString("HH:mm:ss.fff") + "] " + line + Environment.NewLine); }
        catch { }
    }
}
