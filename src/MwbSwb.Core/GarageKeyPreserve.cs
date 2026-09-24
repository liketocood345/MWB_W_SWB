using Microsoft.Win32;

namespace MwbSwb.Core;

/// <summary>
/// Backup / restore Garage MWB identity across MSI repair/overwrite.
/// Preserves MyKey, SecurityKey, MachineMatrix, Machines under HKCU.
/// </summary>
public static class GarageKeyPreserve
{
    public const string RegPath = @"Software\Microsoft\MouseWithoutBorders";

    public sealed record Snapshot(
        string? MyKey,
        string? SecurityKey,
        string? MachineMatrix,
        string? Machines);

    public static Snapshot Capture()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegPath);
            if (key is null)
                return new Snapshot(null, null, null, null);
            return new Snapshot(
                key.GetValue("MyKey") as string,
                key.GetValue("SecurityKey") as string,
                key.GetValue("MachineMatrix") as string,
                key.GetValue("Machines") as string);
        }
        catch
        {
            return new Snapshot(null, null, null, null);
        }
    }

    public static bool HasUsableKey(Snapshot s) =>
        !string.IsNullOrWhiteSpace(s.MyKey) || !string.IsNullOrWhiteSpace(s.SecurityKey);

    /// <summary>
    /// After MSI: if registry key/matrix were wiped or emptied, restore from snapshot.
    /// Never overwrites a non-empty post-MSI value with empty snapshot data.
    /// </summary>
    public static void RestoreIfMissing(Snapshot before)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RegPath);
            if (key is null) return;

            var curMy = key.GetValue("MyKey") as string;
            var curSec = key.GetValue("SecurityKey") as string;
            var curMx = key.GetValue("MachineMatrix") as string;
            var curMachines = key.GetValue("Machines") as string;

            if (string.IsNullOrWhiteSpace(curMy) && !string.IsNullOrWhiteSpace(before.MyKey))
                key.SetValue("MyKey", before.MyKey, RegistryValueKind.String);
            if (string.IsNullOrWhiteSpace(curSec) && !string.IsNullOrWhiteSpace(before.SecurityKey))
                key.SetValue("SecurityKey", before.SecurityKey, RegistryValueKind.String);
            // If both key fields empty after MSI but snapshot had either, restore both available.
            if (string.IsNullOrWhiteSpace(curMy) && string.IsNullOrWhiteSpace(curSec))
            {
                if (!string.IsNullOrWhiteSpace(before.MyKey))
                    key.SetValue("MyKey", before.MyKey, RegistryValueKind.String);
                if (!string.IsNullOrWhiteSpace(before.SecurityKey))
                    key.SetValue("SecurityKey", before.SecurityKey, RegistryValueKind.String);
            }

            if (string.IsNullOrWhiteSpace(curMx) && !string.IsNullOrWhiteSpace(before.MachineMatrix))
                key.SetValue("MachineMatrix", before.MachineMatrix, RegistryValueKind.String);
            if (string.IsNullOrWhiteSpace(curMachines) && !string.IsNullOrWhiteSpace(before.Machines))
                key.SetValue("Machines", before.Machines, RegistryValueKind.String);
        }
        catch
        {
            /* best-effort */
        }
    }
}
