namespace MwbSwb.Host;

static class Program
{
    private const string MutexName = "Local\\MwbSwb.Host.Singleton";
    private const string OpenSwbEventName = "Local\\MwbSwb.Host.OpenSwb";

    static void BootLog(string line)
    {
        try
        {
            File.AppendAllText(@"C:\Users\Public\host-main.log",
                $"[{DateTime.Now:HH:mm:ss.fff}] {line}{Environment.NewLine}");
        }
        catch { /* ignore */ }
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

        using var mutex = new Mutex(initiallyOwned: true, name: MutexName, createdNew: out bool createdNew);
        BootLog("mutex createdNew=" + createdNew);
        if (!createdNew)
        {
            try
            {
                using var ev = EventWaitHandle.OpenExisting(OpenSwbEventName);
                ev.Set();
                BootLog("signaled existing OpenSwb event");
            }
            catch (Exception ex)
            {
                BootLog("signal failed: " + ex.Message);
            }

            return;
        }

        using var openSwbEvent = new EventWaitHandle(false, EventResetMode.AutoReset, OpenSwbEventName);

        ApplicationConfiguration.Initialize();
        var form = new HostForm();
        form.SetOpenSwbOnStart(openSwb);
        BootLog("HostForm created, SetOpenSwbOnStart(" + openSwb + ")");

        var poll = new System.Windows.Forms.Timer { Interval = 400 };
        poll.Tick += (_, _) =>
        {
            if (openSwbEvent.WaitOne(0))
                form.RequestOpenSwb();
        };
        poll.Start();

        Application.Run(form);
        poll.Stop();
        poll.Dispose();
        BootLog("Application.Run exited");
    }
}
