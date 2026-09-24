using System.Text.Json;

namespace MwbSwb.Core;

public sealed class InstallState
{
    public const string RelativePath = @"Microsoft\MWB-SWB\InstallState.json";

    public string UpstreamPinTag { get; set; } = UpstreamPin.Tag;
    public string UpstreamPinCommit { get; set; } = UpstreamPin.Commit;
    public bool BlocksPlatformMwbUpdate { get; set; } = true;
    public bool ReplacesMwb { get; set; } = true;
    public string InstallRoot { get; set; } = "";
    public string BackupRoot { get; set; } = "";
    public string InstallPathMode { get; set; } = "";
    public DateTimeOffset InstalledUtc { get; set; }
    public string ConsentVersion { get; set; } = "1";

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), RelativePath);

    public static InstallState? TryLoad(string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path)) return null;
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<InstallState>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
