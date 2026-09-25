using NAudio.CoreAudioApi;

namespace MwbSwb.Audio;

/// <summary>Local render endpoints for SWB matrix + playback device picker.</summary>
public static class LocalAudioDeviceInfo
{
    public const string LocalMatrixTag = "local";
    /// <summary>Sentinel for "All devices" in SoundSynchroSettings.LocalPlaybackDeviceId.</summary>
    public const string AllDevicesId = "*";

    public readonly record struct RenderEndpoint(string Id, string FriendlyName);

    public static string GetDefaultRenderFriendlyName()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var name = device?.FriendlyName?.Trim();
            return string.IsNullOrWhiteSpace(name) ? "Speakers" : name;
        }
        catch
        {
            return "Speakers";
        }
    }

    public static string FormatLocalMatrixHost(string? machineName = null)
    {
        machineName ??= Environment.MachineName;
        var device = GetDefaultRenderFriendlyName();
        return $"{machineName} / {device}";
    }

    public static IReadOnlyList<RenderEndpoint> ListActiveRenderEndpoints()
    {
        var list = new List<RenderEndpoint>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                using (d)
                {
                    var id = d.ID?.Trim() ?? "";
                    var name = d.FriendlyName?.Trim();
                    if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name)) continue;
                    list.Add(new RenderEndpoint(id, name));
                }
            }
        }
        catch
        {
            /* empty */
        }
        return list;
    }

    public static bool IsAllDevices(string? deviceId) =>
        string.IsNullOrWhiteSpace(deviceId) || deviceId.Trim() == AllDevicesId;
}
