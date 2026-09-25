using System.Diagnostics;

namespace MwbSwb.Core;

/// <summary>
/// Ensure Windows Firewall allows SWB LAN ports. Missing UDP rules commonly cause one-way audio
/// (TCP handshake works; return UDP is dropped).
/// </summary>
public static class SwbFirewall
{
    public static void EnsureLanRules(Action<string>? log = null)
    {
        try
        {
            EnsureRule(
                "MWB-SWB Control TCP 15200",
                "in",
                "TCP",
                SwbHandshakeService.DefaultControlPort,
                log);
            EnsureRule(
                "MWB-SWB Audio UDP 15201",
                "in",
                "UDP",
                SwbHandshakeService.DefaultAudioPort,
                log);
            EnsureRule(
                "MWB-SWB Discovery UDP 15202",
                "in",
                "UDP",
                SwbHandshakeService.DefaultDiscoveryPort,
                log);
        }
        catch (Exception ex)
        {
            log?.Invoke("Firewall setup skipped: " + ex.Message);
        }
    }

    private static void EnsureRule(string name, string dir, string protocol, int port, Action<string>? log)
    {
        // Idempotent: delete then add (netsh is available without extra packages).
        RunNetsh($"advfirewall firewall delete rule name=\"{name}\"", ignoreFail: true);
        var args =
            $"advfirewall firewall add rule name=\"{name}\" dir={dir} action=allow protocol={protocol} localport={port} profile=any";
        var ok = RunNetsh(args, ignoreFail: false);
        log?.Invoke(ok
            ? $"Firewall OK: {protocol} {port} ({name})"
            : $"Firewall WARN: could not add {protocol} {port} — run Host elevated once");
    }

    private static bool RunNetsh(string args, bool ignoreFail)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            });
            if (p == null) return false;
            p.WaitForExit(8000);
            return ignoreFail || p.ExitCode == 0;
        }
        catch
        {
            return ignoreFail;
        }
    }
}
