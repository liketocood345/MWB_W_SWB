namespace MwbSwb.Core;

/// <summary>Single pinned PowerToys MWB generation this Host may speak.</summary>
public static class UpstreamPin
{
    public const string Tag = "v0.99.1";
    public const string Commit = "184ccb75ec85cc799d04555f34ffae968e3ba7c4";
    public const string MwbExeName = "PowerToys.MouseWithoutBorders.exe";
    public const int MwbTcpPort = 15100;
    public const int MwbUdpPort = 15101;
    public const int SwbTcpPort = 15200;
    public const int SwbUdpPort = 15201;

    public static string Describe() => $"PowerToys Mouse Without Borders {Tag} ({Commit[..7]})";
}