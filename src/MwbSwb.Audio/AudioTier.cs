namespace MwbSwb.Audio;

/// <summary>Latency tier 0 (force sync) .. 6 (ultra-low). Lookup table for frame/jitter/playout.</summary>
public static class AudioTier
{
    public const int Min = 0;
    public const int Max = 6;
    public const int Default = 3;

    public static readonly string[] EnglishNames =
    {
        "Force sync",
        "Firm sync",
        "Soft sync",
        "Balanced",
        "Responsive",
        "Near realtime",
        "Ultra-low",
    };

    public static readonly string[] ChineseNames =
    {
        "强制同步",
        "稳同步",
        "柔同步",
        "平衡",
        "迅响",
        "近实时",
        "超低延时",
    };

    public readonly record struct Profile(
        int Tier,
        int FrameMs,
        int JitterTargetMs,
        int JitterTargetMsHi,
        int PlayLatencyMs,
        int PlayLatencyMsHi,
        double SyncAlignStrength,
        bool LightPlc,
        bool FullPlc,
        bool Allow24kFallback);

    private static readonly Profile[] Table =
    {
        new(0, 20, 40, 60, 30, 40, 1.0, false, false, false),
        new(1, 16, 36, 50, 28, 36, 0.7, false, false, false),
        new(2, 12, 32, 42, 24, 32, 0.35, false, false, false),
        new(3, 10, 25, 35, 20, 30, 0.0, false, false, false),
        new(4, 8, 18, 26, 14, 20, 0.0, false, false, false),
        new(5, 6, 12, 18, 10, 14, 0.0, true, false, false),
        new(6, 4, 6, 10, 5, 8, 0.0, false, true, false),
    };

    public static int Clamp(int tier) => Math.Clamp(tier, Min, Max);

    public static Profile Get(int tier) => Table[Clamp(tier)];

    public static string EnglishName(int tier) => EnglishNames[Clamp(tier)];
    public static string ChineseName(int tier) => ChineseNames[Clamp(tier)];

    public static int SamplesPerChannel(int tier, int sampleRate) =>
        Math.Max(1, (int)Math.Round(sampleRate * Get(tier).FrameMs / 1000.0));

    public static int PayloadBytes(int tier, int sampleRate) =>
        SamplesPerChannel(tier, sampleRate) * 2 * sizeof(short);

    public static int ExpectedPacketBytes(int tier, int sampleRate) =>
        SwbAudioFrame.HeaderSize + PayloadBytes(tier, sampleRate);
}
