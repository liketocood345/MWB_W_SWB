using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace MwbSwb.Core;

/// <summary>
/// Reads Microsoft Garage (standalone) Mouse without Borders settings from the registry.
/// Does not touch PowerToys-merged MWB settings.json.
/// </summary>
public static class GarageMwbSettings
{
    public const string ExeRelative = @"Microsoft Garage\Mouse without Borders\MouseWithoutBorders.exe";
    /// <summary>Garage / PowerToys MWB theoretical interconnect cap (including local).</summary>
    public const int MaxMachines = 4;

    public enum AutoFillResult
    {
        Skipped,
        Filled,
        ReclaimedLeftover,
        TooManySameKey,
    }

    public static string? FindInstalledExe()
    {
        foreach (var root in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                 })
        {
            var p = Path.Combine(root, ExeRelative);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    public static bool IsGarageProcessRunning()
    {
        try
        {
            // Wrapped install: real UI is MouseWithoutBorders.original.exe
            if (System.Diagnostics.Process.GetProcessesByName("MouseWithoutBorders.original").Length > 0)
                return true;

            // Helper-only still means Garage MWB session is up on some installs.
            if (System.Diagnostics.Process.GetProcessesByName("MousewithoutBordersHelper").Length > 0)
                return true;
            if (System.Diagnostics.Process.GetProcessesByName("MouseWithoutBordersHelper").Length > 0)
                return true;

            // Unwrapped / stock: process name MouseWithoutBorders.
            // When MWB+SWB stub wraps the exe, stub is ALSO named MouseWithoutBorders —
            // ignore stub by requiring path not equal to the wrap stub next to .original.exe.
            foreach (var p in System.Diagnostics.Process.GetProcessesByName("MouseWithoutBorders"))
            {
                try
                {
                    string? path = null;
                    try { path = p.MainModule?.FileName; } catch { /* access denied */ }
                    if (IsMwbSwbLaunchStub(path))
                        continue;
                    return true;
                }
                catch { /* ignore */ }
                finally { try { p.Dispose(); } catch { /* ignore */ } }
            }

            if (System.Diagnostics.Process.GetProcessesByName("MousewithoutBorders").Length > 0)
                return true;

            foreach (var p in System.Diagnostics.Process.GetProcesses())
            {
                try
                {
                    var n = p.ProcessName;
                    if (n.Equals("MouseWithoutBorders.original", StringComparison.OrdinalIgnoreCase))
                        return true;
                    if (n.Contains("MouseWithoutBorders", StringComparison.OrdinalIgnoreCase)
                        || n.Contains("MousewithoutBorders", StringComparison.OrdinalIgnoreCase))
                    {
                        string? path = null;
                        try { path = p.MainModule?.FileName; } catch { /* ignore */ }
                        if (IsMwbSwbLaunchStub(path))
                            continue;
                        return true;
                    }
                }
                catch { /* access denied on some system procs */ }
                finally { try { p.Dispose(); } catch { /* ignore */ } }
            }
        }
        catch { /* ignore */ }
        return false;
    }

    /// <summary>
    /// True when <paramref name="exePath"/> is the tiny MWB+SWB wrap stub
    /// (MouseWithoutBorders.exe beside MouseWithoutBorders.original.exe).
    /// </summary>
    public static bool IsMwbSwbLaunchStub(string? exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return false;
        try
        {
            var full = Path.GetFullPath(exePath);
            var name = Path.GetFileName(full);
            if (!name.Equals("MouseWithoutBorders.exe", StringComparison.OrdinalIgnoreCase))
                return false;
            var dir = Path.GetDirectoryName(full);
            if (string.IsNullOrEmpty(dir)) return false;
            if (File.Exists(Path.Combine(dir, "MouseWithoutBorders.original.exe")))
                return true;
            if (File.Exists(Path.Combine(dir, "MWB-SWB-WRAPPED.txt")))
                return true;
        }
        catch { /* ignore */ }
        return false;
    }

    public static MwbSettings LoadOrEmpty()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\MouseWithoutBorders")
                            ?? Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\MouseWithoutBorders")
                            ?? Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Microsoft\MouseWithoutBorders");
            if (key is null)
                return new MwbSettings { LocalHostName = Environment.MachineName };

            // SWB is an attachment: always follow Garage MWB's real key first.
            // Do NOT prefer lab shared-key.txt over MyKey — that desyncs SWB from MWB mesh.
            var keyStr = PreferPlaintextKey(key.GetValue("SecurityKey") as string)
                         ?? TryUnprotectGarageMyKey(key.GetValue("MyKey") as string)
                         ?? PreferPlaintextKey(key.GetValue("MyKey") as string)
                         ?? TryReadSharedKeyFile()
                         ?? "";
            var matrixRaw = (key.GetValue("MachineMatrix") as string)
                            ?? (key.GetValue("Machines") as string)
                            ?? "";
            var names = matrixRaw
                .Split(new[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n.Split(':')[0].Trim())
                .Where(n => !n.Equals("NONE", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new MwbSettings
            {
                SettingsPath = @"HKCU\Software\Microsoft\MouseWithoutBorders",
                SecurityKey = keyStr.Trim(),
                LocalHostName = Environment.MachineName,
                MachineMatrix = names,
            };
        }
        catch
        {
            return new MwbSettings { LocalHostName = Environment.MachineName };
        }
    }

    /// <summary>Lab-only fallback. Never preferred over Garage MyKey / SecurityKey.</summary>
    public static string? TryReadSharedKeyFile()
    {
        foreach (var path in new[]
                 {
                     @"C:\Users\Public\shared-key.txt",
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MWB-SWB", "shared-key.txt"),
                 })
        {
            try
            {
                if (!File.Exists(path)) continue;
                var s = File.ReadAllText(path).Trim();
                if (s.Length >= 16) return s;
            }
            catch { /* ignore */ }
        }

        return null;
    }

    /// <summary>
    /// Garage stores MyKey as DPAPI (CurrentUser) with fixed entropy GUID
    /// (same as Setting.Enc in Garage / PowerToys MWB).
    /// </summary>
    public static string? TryUnprotectGarageMyKey(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim().Replace("\r", "", StringComparison.Ordinal).Replace("\n", "", StringComparison.Ordinal);
        if (s.Length < 16) return null;
        try
        {
            // Must match Garage Setting.Enc entropy: "C39952A0-C11C-4fdd-9ADE-DEBC92E074D9" as Unicode bytes.
            var entropy = Encoding.Unicode.GetBytes("C39952A0-C11C-4fdd-9ADE-DEBC92E074D9");
            var bytes = Convert.FromBase64String(s);
            foreach (var scope in new[] { DataProtectionScope.CurrentUser, DataProtectionScope.LocalMachine })
            {
                try
                {
                    var plain = ProtectedData.Unprotect(bytes, entropy, scope);
                    // Garage GetStringU = Unicode
                    var candidate = PreferPlaintextKey(Encoding.Unicode.GetString(plain));
                    if (candidate != null) return candidate;
                    candidate = PreferPlaintextKey(Encoding.UTF8.GetString(plain));
                    if (candidate != null) return candidate;
                }
                catch
                {
                    /* try next scope */
                }
            }
        }
        catch
        {
            /* wrong format / corrupted */
        }

        return null;
    }

    /// <summary>Reject obvious DPAPI/base64 blobs; keep short Garage-style plaintext keys.</summary>
    static string? PreferPlaintextKey(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim();
        if (s.Length < 8) return null;
        // DPAPI / long base64 ciphertext is not usable as HMAC material
        if (s.Length > 64) return null;
        if (s.StartsWith("AQAA", StringComparison.Ordinal) || s.Contains('=')) return null;
        return s;
    }

    /// <summary>
    /// Auto-fill / reclaim Garage MachineMatrix slots.
    /// Rules:
    /// - No name → no write.
    /// - Same exact name already in matrix → skip (once).
    /// - Never reorder existing occupied slots; only mutate indices in place.
    /// - Prefer unused (empty/NONE) slots first.
    /// - Historical leftovers not present on the live IP/hostname table may be overwritten in place.
    /// - If same-key live peers would exceed MaxMachines (incl. local) → TooManySameKey, refuse fill.
    /// </summary>
    public static AutoFillResult TryAutoFillDiscoveredNameOnce(
        string discoveredHostName,
        IReadOnlyCollection<string>? liveHostOrIps,
        out string message)
    {
        message = "";
        if (string.IsNullOrWhiteSpace(discoveredHostName))
        {
            message = "no name obtained";
            return AutoFillResult.Skipped;
        }

        var name = discoveredHostName.Trim();
        if (name.Equals("NONE", StringComparison.OrdinalIgnoreCase))
        {
            message = "invalid name";
            return AutoFillResult.Skipped;
        }

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\MouseWithoutBorders");
            if (key is null)
            {
                message = "registry unavailable";
                return AutoFillResult.Skipped;
            }

            var raw = (key.GetValue("MachineMatrix") as string)
                      ?? (key.GetValue("Machines") as string)
                      ?? "";
            var slots = ParseMatrixSlots(raw);
            var local = Environment.MachineName;
            var live = NormalizeLive(liveHostOrIps, local);

            // Too many same-key devices on LAN vs MWB cap (count hostnames only, not raw IPs).
            var livePeersExcludingLocal = live
                .Where(h => !h.Equals(local, StringComparison.OrdinalIgnoreCase))
                .Where(h => !LooksLikeIp(h))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            // Include the candidate being filled if not already counted.
            if (!livePeersExcludingLocal.Any(h => h.Equals(name, StringComparison.OrdinalIgnoreCase)))
                livePeersExcludingLocal.Add(name);
            var totalWouldBe = livePeersExcludingLocal.Count + 1; // + local
            if (totalWouldBe > MaxMachines)
            {
                message =
                    $"Too many devices share this SecurityKey ({totalWouldBe} > MWB limit {MaxMachines}). Auto-fill refused.";
                return AutoFillResult.TooManySameKey;
            }

            // Already present → once only; do not reorder.
            if (slots.Any(s => s.Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                message = "already in matrix";
                return AutoFillResult.Skipped;
            }

            // Unused slots (empty/NONE).
            var unused = new List<int>();
            for (var i = 0; i < slots.Length; i++)
            {
                if (IsEmptyMatrixSlot(slots[i]))
                    unused.Add(i);
            }

            // Leftover slots: named but not local and not on live IP/hostname table.
            var leftovers = new List<int>();
            for (var i = 0; i < slots.Length; i++)
            {
                if (IsEmptyMatrixSlot(slots[i])) continue;
                if (slots[i].Equals(local, StringComparison.OrdinalIgnoreCase)) continue;
                if (!IsOnLiveTable(slots[i], live))
                    leftovers.Add(i);
            }

            int target;
            AutoFillResult kind;
            if (unused.Count > 0)
            {
                target = PreferSlotAfterLocal(unused, slots, local);
                kind = AutoFillResult.Filled;
            }
            else if (leftovers.Count > 0)
            {
                // Overwrite historical leftover in place — does not reorder other slots.
                target = leftovers[0];
                kind = AutoFillResult.ReclaimedLeftover;
            }
            else
            {
                message = "no unused or leftover slot";
                return AutoFillResult.Skipped;
            }

            slots[target] = name;
            // Write back preserving 4-slot width; order of other indices unchanged.
            key.SetValue("MachineMatrix", string.Join(",", slots), RegistryValueKind.String);
            message = kind == AutoFillResult.ReclaimedLeftover
                ? $"reclaimed leftover slot {target} → {name}"
                : $"filled unused slot {target} → {name}";
            return kind;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            return AutoFillResult.Skipped;
        }
    }

    /// <summary>Compatibility wrapper (no live table → no leftover reclaim, still enforces cap via candidate alone).</summary>
    public static bool TryAutoFillDiscoveredNameOnce(string discoveredHostName) =>
        TryAutoFillDiscoveredNameOnce(discoveredHostName, liveHostOrIps: null, out _)
            is AutoFillResult.Filled or AutoFillResult.ReclaimedLeftover;

    private static HashSet<string> NormalizeLive(IReadOnlyCollection<string>? live, string local)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { local };
        if (live == null) return set;
        foreach (var item in live)
        {
            if (string.IsNullOrWhiteSpace(item)) continue;
            set.Add(item.Trim());
        }
        return set;
    }

    private static bool IsOnLiveTable(string hostName, HashSet<string> live) =>
        live.Contains(hostName);

    private static int PreferSlotAfterLocal(List<int> unused, string[] slots, string local)
    {
        var localIdx = Array.FindIndex(slots, s => s.Equals(local, StringComparison.OrdinalIgnoreCase));
        if (localIdx >= 0)
        {
            foreach (var i in unused)
            {
                if (i > localIdx) return i;
            }
        }
        return unused[0];
    }

    private static bool LooksLikeIp(string s) =>
        System.Net.IPAddress.TryParse(s, out _);

    private static bool IsEmptyMatrixSlot(string slot) =>
        string.IsNullOrWhiteSpace(slot)
        || slot.Equals("NONE", StringComparison.OrdinalIgnoreCase);

    /// <summary>Always returns 4 slots (Garage matrix width), padding with NONE — does not reorder named entries.</summary>
    private static string[] ParseMatrixSlots(string raw)
    {
        var parts = (raw ?? "")
            .Split(new[] { ',', ';', '|' }, StringSplitOptions.None)
            .Select(p => (p ?? "").Trim())
            .ToList();
        var slots = new string[MaxMachines];
        for (var i = 0; i < MaxMachines; i++)
            slots[i] = i < parts.Count && !string.IsNullOrWhiteSpace(parts[i]) ? parts[i] : "NONE";
        return slots;
    }
}
