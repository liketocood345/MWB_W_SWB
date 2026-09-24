using System.Text.Json;
using System.Text.Json.Nodes;

namespace MwbSwb.Core;

/// <summary>
/// After install: disable PowerToys MouseWithoutBorders module and record block markers
/// so platform updates do not re-enable / float MWB past UPSTREAM_PIN.
/// </summary>
public static class PlatformMwbUpdateBlocker
{
    public static string PowerToysSettingsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            @"Microsoft\PowerToys\settings.json");

    public static void ApplyBlock(string installRoot)
    {
        TryDisablePowerToysMwbModule();
        var markerDir = Path.Combine(installRoot, "block-pt-mwb-update");
        Directory.CreateDirectory(markerDir);
        File.WriteAllText(Path.Combine(markerDir, "BLOCKED"),
            $"Blocked at {DateTimeOffset.UtcNow:o}\nPin={UpstreamPin.Tag}\nCommit={UpstreamPin.Commit}\n");
        File.WriteAllText(Path.Combine(markerDir, "README.txt"),
            "PowerToys must not update or re-enable Mouse Without Borders while MWB+SWB Host owns the pin.\n" +
            "Rollback: run Uninstall-MwbSwb.ps1 or restore backup.\n");
    }

    public static void ClearBlock(string? installRoot)
    {
        if (!string.IsNullOrWhiteSpace(installRoot))
        {
            var markerDir = Path.Combine(installRoot, "block-pt-mwb-update");
            if (Directory.Exists(markerDir))
                Directory.Delete(markerDir, recursive: true);
        }
        TryEnablePowerToysMwbModule();
    }

    public static bool TryDisablePowerToysMwbModule()
    {
        try
        {
            var path = PowerToysSettingsPath;
            if (!File.Exists(path)) return false;
            var node = JsonNode.Parse(File.ReadAllText(path));
            if (node is null) return false;
            // Best-effort: common keys used by PT settings blob
            SetEnabled(node, "MouseWithoutBorders", false);
            SetEnabled(node, "Mouse Without Borders", false);
            File.WriteAllText(path, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool TryEnablePowerToysMwbModule()
    {
        try
        {
            var path = PowerToysSettingsPath;
            if (!File.Exists(path)) return false;
            var node = JsonNode.Parse(File.ReadAllText(path));
            if (node is null) return false;
            SetEnabled(node, "MouseWithoutBorders", true);
            SetEnabled(node, "Mouse Without Borders", true);
            File.WriteAllText(path, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void SetEnabled(JsonNode root, string moduleName, bool enabled)
    {
        // Walk shallow objects looking for enabled flags near module name
        if (root is JsonObject obj)
        {
            foreach (var kv in obj.ToList())
            {
                if (kv.Key.Contains("MouseWithoutBorders", StringComparison.OrdinalIgnoreCase) ||
                    kv.Key.Contains("Mouse Without Borders", StringComparison.OrdinalIgnoreCase))
                {
                    if (kv.Value is JsonObject mod)
                    {
                        if (mod.ContainsKey("enabled")) mod["enabled"] = enabled;
                        if (mod.ContainsKey("Enabled")) mod["Enabled"] = enabled;
                        if (mod["properties"] is JsonObject props && props["Enabled"] is JsonObject en && en.ContainsKey("value"))
                            en["value"] = enabled;
                    }
                }
                if (kv.Value is not null)
                    SetEnabled(kv.Value, moduleName, enabled);
            }
        }
        else if (root is JsonArray arr)
        {
            foreach (var item in arr)
                if (item is not null) SetEnabled(item, moduleName, enabled);
        }
    }
}
