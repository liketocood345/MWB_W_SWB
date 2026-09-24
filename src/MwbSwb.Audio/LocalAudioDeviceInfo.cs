using NAudio.CoreAudioApi;

namespace MwbSwb.Audio;

/// <summary>Default local render endpoint for SWB matrix (visible even with zero peers).</summary>
public static class LocalAudioDeviceInfo
{
    public const string LocalMatrixTag = "local";

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
}
