namespace MwbSwb.Host;

static class Program
{
    private const string MutexName = "Global\\MwbSwb.Host.Singleton";
    private const string OpenSwbEventName = "Global\\MwbSwb.Host.OpenSwb";
    private const string ExitEventName = "Global\\MwbSwb.Host.ExitGraceful";
    private const string PulsePath = @"C:\Users\Public\MWB-SWB-Host\open-swb.pulse";
    private const string ExitPulsePath = @"C:\Users\Public\MWB-SWB-Host\exit-host.pulse";

    static string LocalExitPulsePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MWB-SWB-Host", "exit-host.pulse");

    static void BootLog(string line)
    {
        var text = $"[{DateTime.Now:HH:mm:ss.fff}] {line}{Environment.NewLine}";
        foreach (var path in new[]
                 {
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                         "MWB-SWB-Host", "host-main.log"),
                     @"C:\Users\Public\host-main.log",
                     @"C:\Users\Public\MWB-SWB-Host\host-main.log",
                 })
        {
            try
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                File.AppendAllText(path, text);
            }
            catch { /* try next */ }
        }
    }

    static void SignalOpenSwb()
    {
        try
        {
            using var ev = EventWaitHandle.OpenExisting(OpenSwbEventName);
            ev.Set();
            BootLog("signaled OpenSwb event");
        }
        catch (Exception ex)
        {
            BootLog("event signal failed: " + ex.Message);
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PulsePath)!);
            File.WriteAllText(PulsePath, DateTime.UtcNow.Ticks.ToString());
            BootLog("wrote open-swb.pulse");
        }
        catch (Exception ex)
        {
            BootLog("pulse write failed: " + ex.Message);
        }
    }

    [STAThread]
    static void Main(string[] args)
    {
        BootLog("Main args=[" + string.Join("|", args) + "]");

        bool openSwb = false;
        foreach (var a in args)
        {
            if (string.Equals(a, "/open-swb", StringComparison.OrdinalIgnoreCase)
                || string.Equals(a, "--open-swb", StringComparison.OrdinalIgnoreCase)
                || string.Equals(a, "open-swb", StringComparison.OrdinalIgnoreCase))
            {
                openSwb = true;
            }
        }

        BootLog("openSwb=" + openSwb);
        BootLog("sessionId=" + System.Diagnostics.Process.GetCurrentProcess().SessionId);
        // Session 0 (services) UI is invisible on the console desktop — hop to interactive session.
        if (System.Diagnostics.Process.GetCurrentProcess().SessionId == 0
            && !args.Any(a => string.Equals(a, "/nosessionhop", StringComparison.OrdinalIgnoreCase)))
        {
            BootLog("Session 0 detected — scheduling interactive relaunch then exit");
            // If Session 1 Host already owns the singleton, just nudge it — do NOT spawn another.
            try
            {
                using var probe = new Mutex(false, MutexName, out bool created);
                if (!created)
                {
                    BootLog("Session 0: interactive Host already running — SignalOpenSwb only");
                    SignalOpenSwb();
                    return;
                }
                // We accidentally created mutex in Session 0; release so Session 1 can take it.
                try { probe.ReleaseMutex(); } catch { /* ignore */ }
            }
            catch (Exception ex) { BootLog("Session 0 mutex probe: " + ex.Message); }

            TryRelaunchInInteractiveSession(args);
            return;
        }
        try { MwbSwb.Core.SwbFirewall.EnsureLanRules(BootLog); } catch (Exception ex) { BootLog("firewall: " + ex.Message); }

        using var mutex = new Mutex(initiallyOwned: true, name: MutexName, createdNew: out bool createdNew);
        BootLog("mutex createdNew=" + createdNew);
        if (!createdNew)
        {
            // Always nudge existing Host to open SWB when Launch SWB is clicked.
            SignalOpenSwb();
            return;
        }

        // Soft-restart / multi-root: ask prior Host to hide tray + exit before we create ours.
        // Hard Kill leaves NotifyIcon ghosts (flood of dead tray glyphs until mouse-over).
        RequestGracefulExitOfStrayHosts();

        using var openSwbEvent = new EventWaitHandle(false, EventResetMode.AutoReset, OpenSwbEventName);
        using var exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName);

        ApplicationConfiguration.Initialize();
        HostForm form;
        try
        {
            form = new HostForm();
            form.SetOpenSwbOnStart(openSwb);
            BootLog("HostForm created, SetOpenSwbOnStart(" + openSwb + ")");
        }
        catch (Exception ex)
        {
            BootLog("HostForm ctor FAILED: " + ex);
            throw;
        }

        var poll = new System.Windows.Forms.Timer { Interval = 400 };
        poll.Tick += (_, _) =>
        {
            if (exitEvent.WaitOne(0) || TryConsumeExitPulse())
            {
                BootLog("graceful-exit requested");
                form.RequestGracefulExit();
                return;
            }

            var viaEvent = openSwbEvent.WaitOne(0);
            var viaPulse = false;
            try
            {
                if (File.Exists(PulsePath))
                {
                    File.Delete(PulsePath);
                    viaPulse = true;
                }
            }
            catch { /* ignore */ }

            if (viaEvent || viaPulse)
            {
                BootLog("open-swb request viaEvent=" + viaEvent + " viaPulse=" + viaPulse);
                form.RequestOpenSwb();
            }
        };
        poll.Start();

        Application.Run(form);
        poll.Stop();
        poll.Dispose();
        BootLog("Application.Run exited");
    }

    static bool TryConsumeExitPulse()
    {
        // Accept Public (scripts/IT) or LocalAppData (in-proc soft-restart) pulse.
        foreach (var path in new[] { ExitPulsePath, LocalExitPulsePath })
        {
            try
            {
                if (!File.Exists(path)) continue;
                File.Delete(path);
                BootLog("consumed exit pulse: " + path);
                return true;
            }
            catch (Exception ex)
            {
                BootLog("exit pulse consume failed " + path + ": " + ex.Message);
            }
        }
        return false;
    }

    /// <summary>
    /// Ask other Host processes to Dispose NotifyIcon then exit. Only Kill as last resort.
    /// </summary>
    static void RequestGracefulExitOfStrayHosts()
    {
        try
        {
            var others = System.Diagnostics.Process.GetProcessesByName("MwbSwb.Host")
                .Where(p => p.Id != Environment.ProcessId)
                .ToList();

            // Always clear orphaned NotifyIcon glyphs from prior Force Kills / crashes.
            TrayNotifyCleanup.RefreshNotificationArea();

            if (others.Count == 0) return;

            BootLog("stray Host count=" + others.Count + " — signaling ExitGraceful");
            try
            {
                using var ev = EventWaitHandle.OpenExisting(ExitEventName);
                ev.Set();
            }
            catch
            {
                try
                {
                    using var ev = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName);
                    ev.Set();
                }
                catch (Exception ex) { BootLog("exit event: " + ex.Message); }
            }

            foreach (var path in new[] { ExitPulsePath, LocalExitPulsePath })
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllText(path, DateTime.UtcNow.Ticks.ToString());
                }
                catch { /* try next */ }
            }

            var deadline = DateTime.UtcNow.AddSeconds(8);
            while (DateTime.UtcNow < deadline)
            {
                others = System.Diagnostics.Process.GetProcessesByName("MwbSwb.Host")
                    .Where(p => p.Id != Environment.ProcessId)
                    .ToList();
                if (others.Count == 0) break;
                Thread.Sleep(200);
            }

            var killed = false;
            foreach (var p in others)
            {
                try
                {
                    if (p.HasExited) continue;
                    BootLog("stray still alive after graceful wait — Kill pid=" + p.Id);
                    p.Kill(entireProcessTree: true);
                    p.WaitForExit(2000);
                    killed = true;
                }
                catch (Exception ex) { BootLog("kill stray: " + ex.Message); }
                finally { try { p.Dispose(); } catch { } }
            }

            // Always poke tray: Force Kill and prior crashes leave NotifyIcon ghosts.
            if (killed || others.Count > 0)
                BootLog("refreshing notification area (clear ghost trays)");
            TrayNotifyCleanup.RefreshNotificationArea();
            Thread.Sleep(200);
            TrayNotifyCleanup.RefreshNotificationArea();
        }
        catch (Exception ex) { BootLog("scan Host: " + ex.Message); }
    }

    /// <summary>
    /// Host started in Session 0 (vmrun without -interactive, service, etc.) cannot show UI
    /// on the console desktop. Schedule a one-shot interactive task and exit.
    /// </summary>
    static void TryRelaunchInInteractiveSession(string[] args)
    {
        try
        {
            var exe = Environment.ProcessPath
                       ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
            {
                BootLog("relaunch: exe path missing");
                return;
            }

            const string task = "MWB-SWB-Host-Interactive";
            var work = Path.GetDirectoryName(exe) ?? @"C:\Users\Administrator\AppData\Local\MWB-SWB-Host";
            var xmlPath = Path.Combine(Path.GetTempPath(), "mwb-swb-host-it.xml");
            var xml =
                "<?xml version=\"1.0\"?>" +
                "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">" +
                "<Triggers/><Principals><Principal id=\"Author\">" +
                "<UserId>Administrator</UserId><LogonType>InteractiveToken</LogonType>" +
                "<RunLevel>LeastPrivilege</RunLevel></Principal></Principals>" +
                "<Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>" +
                "<DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>" +
                "<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>" +
                "<AllowHardTerminate>true</AllowHardTerminate>" +
                "<StartWhenAvailable>true</StartWhenAvailable>" +
                "<AllowStartOnDemand>true</AllowStartOnDemand>" +
                "<Enabled>true</Enabled><Hidden>false</Hidden></Settings>" +
                "<Actions Context=\"Author\"><Exec>" +
                "<Command>" + System.Security.SecurityElement.Escape(exe) + "</Command>" +
                "<Arguments>/open-swb</Arguments>" +
                "<WorkingDirectory>" + System.Security.SecurityElement.Escape(work) + "</WorkingDirectory>" +
                "</Exec></Actions></Task>";
            File.WriteAllText(xmlPath, xml);

            RunSchtasks("/Delete /TN \"" + task + "\" /F");
            RunSchtasks("/Create /TN \"" + task + "\" /XML \"" + xmlPath + "\" /F");
            RunSchtasks("/Run /TN \"" + task + "\"");
        }
        catch (Exception ex)
        {
            BootLog("relaunch failed: " + ex);
        }
    }

    static void RunSchtasks(string arguments)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "schtasks.exe",
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = System.Diagnostics.Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit(15000);
        BootLog("schtasks " + arguments + " => " + p.ExitCode + " " + o.Trim());
    }
}