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

/// <summary>Mesh-replicated SWB UI settings (not per-device playback id / poses).</summary>
public sealed record SwbMeshSettings(
    long Epoch,
    int AudioTier,
    int SpatialMode,
    bool DistanceAttenuation,
    bool SendLocalLoopback,
    bool ReceiveAndMix,
    bool Enabled,
    int TargetSyncToleranceMs);

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
    /// <summary>Legacy; migrated into AudioTier on load (true -> 0).</summary>
    public bool ForceSoundSync { get; set; }
    /// <summary>0 Force sync .. 6 Ultra-low. Default 3 Balanced.</summary>
    public int AudioTier { get; set; } = 3;
    public SpatialLayoutMode SpatialMode { get; set; } = SpatialLayoutMode.Ring2D;
    public bool DistanceAttenuation { get; set; } = true;
    public int TargetSyncToleranceMs { get; set; } = 30;
    /// <summary>Capture+send local loopback. Set false on RX-only probe hosts so LatencyProbe can open WASAPI loopback.</summary>
    public bool SendLocalLoopback { get; set; } = true;
    public bool ReceiveAndMix { get; set; } = true;
    /// <summary>
    /// Local playback target. Empty or "*" = all active render devices;
    /// otherwise MMDevice.ID of a single endpoint.
    /// </summary>
    public string LocalPlaybackDeviceId { get; set; } = "*";
    public List<DeviceSpatialPose> Layout { get; set; } = new();
    public string SettingsPath { get; set; } = "";
    /// <summary>Utc ticks of last intentional mesh-relevant change; last-writer-wins across devices.</summary>
    public long SettingsEpoch { get; set; }

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), RelativePath);

    public static SoundSynchroSettings LoadOrDefault(string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path))
            return new SoundSynchroSettings { SettingsPath = path, Enabled = false, AudioTier = 3 };

        using var stream = File.OpenRead(path);
        var dto = JsonSerializer.Deserialize<SoundSynchroSettings>(stream, JsonOpts) ?? new SoundSynchroSettings();
        dto.SettingsPath = path;
        dto.MigrateLegacyTier();
        if (dto.SettingsEpoch <= 0)
            dto.SettingsEpoch = 1;
        return dto;
    }

    /// <summary>Old ForceSoundSync=true maps to tier 0 when AudioTier still default and flag set.</summary>
    public void MigrateLegacyTier()
    {
        if (ForceSoundSync && AudioTier == 3)
        {
            // Ambiguous: could be fresh default. Prefer: if ForceSoundSync explicitly true in old files without AudioTier property,
            // deserializer leaves AudioTier=3. Use ForceSoundSync as override once.
            AudioTier = 0;
            ForceSoundSync = false;
        }
        AudioTier = Math.Clamp(AudioTier, 0, 6);
    }

    public void Save(bool bumpEpoch = true)
    {
        var path = string.IsNullOrWhiteSpace(SettingsPath) ? DefaultPath : SettingsPath;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        SettingsPath = path;
        ForceSoundSync = AudioTier <= 2;
        if (bumpEpoch)
            SettingsEpoch = Math.Max(SettingsEpoch + 1, DateTime.UtcNow.Ticks);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptsWrite));
    }

    /// <summary>Apply mesh-synced fields from a peer when remote epoch is newer. Returns true if applied.</summary>
    public bool TryApplyMeshSettings(SwbMeshSettings remote)
    {
        if (remote.Epoch <= SettingsEpoch)
            return false;
        Enabled = remote.Enabled;
        AudioTier = Math.Clamp(remote.AudioTier, 0, 6);
        SpatialMode = (SpatialLayoutMode)Math.Clamp(remote.SpatialMode, 0, 2);
        DistanceAttenuation = remote.DistanceAttenuation;
        SendLocalLoopback = remote.SendLocalLoopback;
        ReceiveAndMix = remote.ReceiveAndMix;
        TargetSyncToleranceMs = Math.Clamp(remote.TargetSyncToleranceMs, 5, 200);
        SettingsEpoch = remote.Epoch;
        ForceSoundSync = AudioTier <= 2;
        Save(bumpEpoch: false);
        return true;
    }

    public SwbMeshSettings ToMeshSettings() => new(
        SettingsEpoch,
        Math.Clamp(AudioTier, 0, 6),
        (int)SpatialMode,
        DistanceAttenuation,
        SendLocalLoopback,
        ReceiveAndMix,
        Enabled,
        TargetSyncToleranceMs);

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

    public DeviceSpatialPose UpsertAdvertisedPose(string hostName, double azimuthDeg, double elevationDeg, double radius)
    {
        var pose = GetOrCreatePose(hostName);
        pose.AzimuthDeg = azimuthDeg;
        pose.ElevationDeg = elevationDeg;
        pose.Radius = Math.Clamp(radius <= 0 ? 1.0 : radius, 0.2, 1.5);
        return pose;
    }

    public DeviceSpatialPose GetLocalPose(string localHostName) => GetOrCreatePose(localHostName);

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
