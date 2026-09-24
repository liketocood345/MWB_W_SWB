using System.Text.Json;
using System.Text.Json.Serialization;

namespace MwbSwb.Core;

public enum SpatialLayoutMode
{
    Ring2D = 0,
    Sphere3D = 1,
    /// <summary>No surround staging — equal stereo gains, sync/mix only.</summary>
    SyncOnly = 2,
}

public sealed class DeviceSpatialPose
{
    public string HostName { get; set; } = "";
    public double AzimuthDeg { get; set; }
    public double ElevationDeg { get; set; }
    public double Radius { get; set; } = 1.0;
}

public sealed class SoundSynchroSettings
{
    public const string RelativePath = @"Microsoft\MWB-SWB\SoundSynchro.json";

    public bool Enabled { get; set; }
    public bool ForceSoundSync { get; set; }
    public SpatialLayoutMode SpatialMode { get; set; } = SpatialLayoutMode.Ring2D;
    public bool DistanceAttenuation { get; set; } = true;
    public int TargetSyncToleranceMs { get; set; } = 30;
    public List<DeviceSpatialPose> Layout { get; set; } = new();
    public string SettingsPath { get; set; } = "";

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), RelativePath);

    public static SoundSynchroSettings LoadOrDefault(string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path))
            return new SoundSynchroSettings { SettingsPath = path, Enabled = false, ForceSoundSync = false };

        using var stream = File.OpenRead(path);
        var dto = JsonSerializer.Deserialize<SoundSynchroSettings>(stream, JsonOpts) ?? new SoundSynchroSettings();
        dto.SettingsPath = path;
        return dto;
    }

    public void Save()
    {
        var path = string.IsNullOrWhiteSpace(SettingsPath) ? DefaultPath : SettingsPath;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        SettingsPath = path;
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptsWrite));
    }

    /// <summary>
    /// Local/default pose: front center. Never invent ring slots by connection order.
    /// Remote peers should arrive via <see cref="UpsertAdvertisedPose"/>.
    /// </summary>
    public DeviceSpatialPose GetOrCreatePose(string hostName)
    {
        var existing = Layout.FirstOrDefault(p =>
            string.Equals(p.HostName, hostName, StringComparison.OrdinalIgnoreCase));
        if (existing != null) return existing;
        var pose = new DeviceSpatialPose
        {
            HostName = hostName,
            AzimuthDeg = 0,
            ElevationDeg = 0,
            Radius = 1.0,
        };
        Layout.Add(pose);
        return pose;
    }

    /// <summary>Apply pose advertised by a peer machine (authoritative for that host).</summary>
    public DeviceSpatialPose UpsertAdvertisedPose(string hostName, double azimuthDeg, double elevationDeg, double radius)
    {
        var pose = GetOrCreatePose(hostName);
        pose.AzimuthDeg = azimuthDeg;
        pose.ElevationDeg = elevationDeg;
        pose.Radius = Math.Clamp(radius <= 0 ? 1.0 : radius, 0.2, 1.5);
        return pose;
    }

    public DeviceSpatialPose GetLocalPose(string localHostName)
    {
        return GetOrCreatePose(localHostName);
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly JsonSerializerOptions JsonOptsWrite = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };
}
