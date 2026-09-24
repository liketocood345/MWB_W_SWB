using System.Text.Json;
using System.Text.Json.Serialization;

namespace MwbSwb.Core;

/// <summary>Reads PowerToys MWB peer list + SecurityKey for SWB handshake.</summary>
public sealed class MwbSettings
{
    public const string RelativeSettingsPath = @"Microsoft\PowerToys\MouseWithoutBorders\settings.json";

    public string SecurityKey { get; init; } = "";
    public string LocalHostName { get; init; } = Environment.MachineName;
    public IReadOnlyList<string> MachineMatrix { get; init; } = Array.Empty<string>();
    public string SettingsPath { get; init; } = "";

    public IEnumerable<string> PeerHostNames =>
        MachineMatrix
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim())
            .Where(n => !n.Equals(LocalHostName, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase);

    public static string DefaultSettingsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), RelativeSettingsPath);

    public static MwbSettings LoadOrEmpty(string? path = null)
    {
        path ??= DefaultSettingsPath;
        if (!File.Exists(path))
            return new MwbSettings { SettingsPath = path, LocalHostName = Environment.MachineName };

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (!root.TryGetProperty("properties", out var props))
                return new MwbSettings { SettingsPath = path, LocalHostName = Environment.MachineName };

            var key = ReadStringValue(props, "SecurityKey") ?? "";
            var matrix = ReadStringList(props, "MachineMatrixString");

            if (matrix.Count == 0)
            {
                var pool = ReadStringValue(props, "MachinePool");
                if (!string.IsNullOrWhiteSpace(pool))
                {
                    matrix = pool
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(part => part.Split(':', 2)[0].Trim())
                        .Where(n => !string.IsNullOrWhiteSpace(n)
                                    && !n.Equals("NONE", StringComparison.OrdinalIgnoreCase)
                                    && n != "")
                        .ToList();
                }
            }

            return new MwbSettings
            {
                SettingsPath = path,
                SecurityKey = key,
                LocalHostName = Environment.MachineName,
                MachineMatrix = matrix,
            };
        }
        catch (JsonException)
        {
            // Corrupt / unexpected schema must not crash Host — treat as empty.
            return new MwbSettings { SettingsPath = path, LocalHostName = Environment.MachineName };
        }
    }

    public bool IsReadyForHandshake =>
        !string.IsNullOrWhiteSpace(SecurityKey) && MachineMatrix.Count > 0;

    /// <summary>SWB only needs the shared SecurityKey; MWB matrix is optional discovery hint.</summary>
    public bool IsReadyForSwb =>
        !string.IsNullOrWhiteSpace(SecurityKey);

    /// <summary>Accepts {"value":"..."} or bare string.</summary>
    private static string? ReadStringValue(JsonElement props, string name)
    {
        if (!props.TryGetProperty(name, out var el)) return null;
        if (el.ValueKind == JsonValueKind.String) return el.GetString();
        if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty("value", out var v))
        {
            if (v.ValueKind == JsonValueKind.String) return v.GetString();
            if (v.ValueKind == JsonValueKind.Number) return v.ToString();
        }
        return null;
    }

    /// <summary>
    /// Accepts {"value":["a","b"]}, bare ["a","b"], or {"value":"a,b"}.
    /// </summary>
    private static List<string> ReadStringList(JsonElement props, string name)
    {
        var list = new List<string>();
        if (!props.TryGetProperty(name, out var el)) return list;

        JsonElement arrOrVal = el;
        if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty("value", out var inner))
            arrOrVal = inner;

        if (arrOrVal.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in arrOrVal.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    var s = item.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) list.Add(s.Trim());
                }
            }
            return list;
        }

        if (arrOrVal.ValueKind == JsonValueKind.String)
        {
            var s = arrOrVal.GetString() ?? "";
            list.AddRange(s.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        return list;
    }
}
