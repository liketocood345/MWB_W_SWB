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
        {
            return new MwbSettings { SettingsPath = path, LocalHostName = Environment.MachineName };
        }

        using var stream = File.OpenRead(path);
        var doc = JsonSerializer.Deserialize<MwbSettingsDto>(stream, JsonOptions) ?? new MwbSettingsDto();
        var props = doc.Properties ?? new MwbPropertiesDto();
        var key = props.SecurityKey?.Value?.Trim() ?? "";
        var matrix = props.MachineMatrixString?.Value?
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s!.Trim())
            .ToList() ?? new List<string>();

        if (matrix.Count == 0 && !string.IsNullOrWhiteSpace(props.MachinePool?.Value))
        {
            matrix = props.MachinePool.Value
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(part => part.Split(':', 2)[0].Trim())
                .Where(n => !string.IsNullOrWhiteSpace(n) && !n.Equals("NONE", StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        return new MwbSettings
        {
            SettingsPath = path,
            SecurityKey = key,
            LocalHostName = Environment.MachineName,
            MachineMatrix = matrix,
        };
    }

    public bool IsReadyForHandshake =>
        !string.IsNullOrWhiteSpace(SecurityKey) && MachineMatrix.Count > 0;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private sealed class MwbSettingsDto
    {
        [JsonPropertyName("properties")]
        public MwbPropertiesDto? Properties { get; set; }
    }

    private sealed class MwbPropertiesDto
    {
        [JsonPropertyName("SecurityKey")]
        public StringProp? SecurityKey { get; set; }

        [JsonPropertyName("MachineMatrixString")]
        public StringListProp? MachineMatrixString { get; set; }

        [JsonPropertyName("MachinePool")]
        public StringProp? MachinePool { get; set; }
    }

    private sealed class StringProp
    {
        [JsonPropertyName("value")]
        public string? Value { get; set; }
    }

    private sealed class StringListProp
    {
        [JsonPropertyName("value")]
        public List<string?>? Value { get; set; }
    }
}