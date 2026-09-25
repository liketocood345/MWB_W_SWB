using MwbSwb.Core;

namespace MwbSwb.Host;

/// <summary>
/// Tray shell for SWB as an MWB attachment. Follows Garage Mouse without Borders lifecycle.
/// </summary>
public sealed class HostForm : Form
{
    private readonly NotifyIcon _tray = new();
    private readonly SwbTrayIcon _trayGlyph = new();
    private readonly System.Windows.Forms.Timer _trayMeter = new() { Interval = 200 };
    private readonly Label _status = new();
    private readonly TextBox _log = new();
    private readonly Button _btnOpenSwb = new();
    private readonly Button _btnAbout = new();
    private SwbForm? _swb;
    private readonly System.Windows.Forms.Timer _mwbWatch = new() { Interval = 1500 };
    private bool _mwbWasRunning;
    private bool _userClosedSwb;
    private bool _openSwbOnStart;

    public void SetOpenSwbOnStart(bool value) => _openSwbOnStart = value;

    /// <summary>Hide tray first (avoid ghost icons), then exit message loop.</summary>
    public void RequestGracefulExit()
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            try { Invoke(RequestGracefulExit); } catch { /* shutting down */ }
            return;
        }
        try { _trayMeter.Stop(); } catch { /* ignore */ }
        try { _mwbWatch.Stop(); } catch { /* ignore */ }
        try
        {
            _tray.Visible = false;
            _tray.Icon = null;
            _tray.Dispose();
        }
        catch { /* ignore */ }
        try { Application.Exit(); } catch { /* ignore */ }
    }

    public HostForm()
    {
        Text = "MWB+SWB Host";
        Width = 640;
        Height = 360;
        MinimumSize = new Size(520, 280);
        StartPosition = FormStartPosition.Manual;
        Location = new Point(-32000, -32000); // off-screen until user opens status
        Font = new Font("Segoe UI", 9f);
        ShowInTaskbar = false;

        BuildUi();
        SetupTray();

        Shown += (_, _) =>
        {
            AppendLog("Host started as MWB attachment (follows Garage Mouse without Borders).");
            try
            {
                File.AppendAllText(@"C:\Users\Public\swb-enable.log",
                    $"[{DateTime.Now:HH:mm:ss}] Host: Shown (tray){Environment.NewLine}");
            }
            catch { /* ignore */ }
            RefreshStatus();
            Hide();
            _mwbWatch.Tick += (_, _) => OnMwbWatchTick();
            _mwbWatch.Start();
            OnMwbWatchTick();
            // If MWB already up at Host start, first tick may race; re-check once shortly after.
            var once = new System.Windows.Forms.Timer { Interval = 2500 };
            once.Tick += (_, _) =>
            {
                once.Stop();
                once.Dispose();
                OnMwbWatchTick();
            };
            once.Start();
            if (_openSwbOnStart)
            {
                _openSwbOnStart = false;
                BeginInvoke(() => EnsureSwbAttachment(showWindow: true, enableSynchro: true));
            }
        };

        FormClosing += async (_, e) =>
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                ShowInTaskbar = false;
                AppendLog("Host status hidden (still in tray).");
                return;
            }

            await ShutdownAsync();
        };
    }

    // tray hidden in ShutdownAsync before process tear-down (soft-restart safe)

    private void BuildUi()
    {
        var hint = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Top,
            Height = 72,
            Padding = new Padding(12, 10, 12, 4),
            Text =
                "SWB is an MWB attachment: MWB first. Host follows Garage Mouse without Borders start/stop.\n" +
                "You may close the SWB window; reopen from tray. Host exits when MWB exits.",
        };

        _status.AutoSize = false;
        _status.Dock = DockStyle.Top;
        _status.Height = 28;
        _status.Padding = new Padding(12, 4, 12, 0);
        _status.ForeColor = Color.DimGray;

        var buttons = new Panel { Dock = DockStyle.Top, Height = 44, Padding = new Padding(12, 6, 12, 6) };
        _btnOpenSwb.Text = "Open SWB window";
        _btnOpenSwb.Width = 220;
        _btnOpenSwb.Location = new Point(12, 6);
        _btnOpenSwb.Click += (_, _) => OpenSwbWindow();
        _btnAbout.Text = "About";
        _btnAbout.Width = 80;
        _btnAbout.Location = new Point(250, 6);
        _btnAbout.Click += (_, _) => ShowAbout();
        buttons.Controls.AddRange(new Control[] { _btnOpenSwb, _btnAbout });

        _log.Dock = DockStyle.Fill;
        _log.Multiline = true;
        _log.ScrollBars = ScrollBars.Vertical;
        _log.ReadOnly = true;
        _log.Font = new Font("Consolas", 9f);
        _log.BackColor = Color.FromArgb(30, 30, 30);
        _log.ForeColor = Color.Gainsboro;

        Controls.Add(_log);
        Controls.Add(buttons);
        Controls.Add(_status);
        Controls.Add(hint);
    }

    private void SetupTray()
    {
        _tray.Text = "SWB  RX: quiet  |  TX: quiet";
        _tray.Icon = _trayGlyph.Idle;
        _tray.Visible = true;
        _tray.DoubleClick += (_, _) => OpenSwbWindow();
        var menu = new ContextMenuStrip();
        var openSwb = new ToolStripMenuItem("Open SWB window", null, (_, _) => OpenSwbWindow());
        openSwb.Font = new Font(openSwb.Font, FontStyle.Bold);
        menu.Items.Add(openSwb);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Open Host status", null, (_, _) => ShowHostStatus());
        menu.Items.Add("About (log)", null, (_, _) => ShowAbout());
        menu.Items.Add("Close SWB Host", null, async (_, _) =>
        {
            await ShutdownAsync();
            Application.Exit();
        });
        _tray.ContextMenuStrip = menu;

        _trayMeter.Tick += (_, _) => OnTrayMeterTick();
        _trayMeter.Start();
    }

    private void OnTrayMeterTick()
    {
        if (IsDisposed) return;
        float tx = 0, rx = 0;
        if (_swb != null && !_swb.IsDisposed)
            _swb.TryGetAudioActivity(out tx, out rx);
        _trayGlyph.TryUpdate(_tray, tx, rx);
    }

    private void OnMwbWatchTick()
    {
        var running = GarageMwbSettings.IsGarageProcessRunning();
        if (running && !_mwbWasRunning)
        {
            AppendLog("Garage MWB detected - SWB attachment + window (latency slider).");
            try
            {
                File.AppendAllText(@"C:\Users\Public\swb-enable.log",
                    $"[{DateTime.Now:HH:mm:ss}] Host: MWB detected -> EnsureSwbAttachment(showWindow=true){Environment.NewLine}");
            }
            catch { /* ignore */ }
            // Enable mesh silently; do not pop SWB / balloons.
            if (!_userClosedSwb)
                EnsureSwbAttachment(showWindow: true, enableSynchro: true);
        }
        else if (!running && _mwbWasRunning)
        {
            AppendLog("Garage MWB stopped - closing SWB attachment.");
            _ = CloseSwbOnlyAsync();
            _mwbWatch.Stop();
            BeginInvoke(async () =>
            {
                await ShutdownAsync();
                Application.Exit();
            });
        }
        else if (!running && !_mwbWasRunning)
        {
            _status.Text = "Waiting for Garage Mouse without Borders... (SWB attaches when MWB starts)";
        }

        _mwbWasRunning = running;
        if (running)
            RefreshStatus();
    }

    private async Task CloseSwbOnlyAsync()
    {
        if (_swb != null && !_swb.IsDisposed)
        {
            try { await _swb.ShutdownAsync(); } catch { /* ignore */ }
            try { _swb.Close(); } catch { /* ignore */ }
            try { _swb.Dispose(); } catch { /* ignore */ }
            _swb = null;
        }
    }

    /// <summary>Public entry for /open-swb and second-instance signaling.</summary>
    public void RequestOpenSwb()
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            BeginInvoke(RequestOpenSwb);
            return;
        }

        try
        {
            File.AppendAllText(@"C:\Users\Public\swb-enable.log",
                $"[{DateTime.Now:HH:mm:ss}] RequestOpenSwb mwbRunning={GarageMwbSettings.IsGarageProcessRunning()}{Environment.NewLine}");
        }
        catch { /* ignore */ }

        EnsureSwbAttachment(showWindow: true, enableSynchro: true);
    }

    private void ShowHostStatus()
    {
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.CenterScreen;
        Location = new Point(
            Math.Max(0, (Screen.PrimaryScreen!.WorkingArea.Width - Width) / 2),
            Math.Max(0, (Screen.PrimaryScreen.WorkingArea.Height - Height) / 2));
        Show();
        Activate();
    }

    private void OpenSwbWindow(bool enableSynchro = false)
        => EnsureSwbAttachment(showWindow: true, enableSynchro: enableSynchro);

    /// <summary>
    /// Create SWB attachment. Window only when <paramref name="showWindow"/> is true
    /// (tray / Launch SWB). MWB attach path enables synchro headlessly.
    /// </summary>
    private void EnsureSwbAttachment(bool showWindow, bool enableSynchro)
    {
        if (!GarageMwbSettings.IsGarageProcessRunning())
        {
            AppendLog("MWB not running - SWB stays closed (attachment mode).");
            return;
        }

        if (showWindow)
            _userClosedSwb = false;

        if (_swb == null || _swb.IsDisposed)
        {
            _swb = new SwbForm();
            _swb.FormClosed += (_, _) =>
            {
                _userClosedSwb = true;
                _swb = null;
                AppendLog("SWB window closed by user (Host stays while MWB runs).");
            };
        }

        // Force handle so Invoke/async enable works without flashing a window.
        if (!_swb.IsHandleCreated)
            _ = _swb.Handle;

        if (enableSynchro)
            _swb.EnsureSoundSynchroEnabled();

        if (showWindow)
        {
            _swb.RevealWindow();
            AppendLog("Opened SWB window (MWB attachment).");
            try
            {
                File.AppendAllText(@"C:\Users\Public\swb-enable.log",
                    $"[{DateTime.Now:HH:mm:ss}] Opened SWB window show=true{Environment.NewLine}");
            }
            catch { /* ignore */ }
        }
        else
            AppendLog("SWB attachment enabled (window hidden).");

        // /open-swb and tray: always reveal even if synchro was already enabled headlessly.
        if (showWindow && _swb != null && !_swb.IsDisposed)
            _swb.RevealWindow();
    }

    private void RefreshStatus()
    {
        var mwb = GarageMwbSettings.LoadOrEmpty();
        if (!mwb.IsReadyForHandshake)
            mwb = MwbSettings.LoadOrEmpty();
        var peers = string.Join(", ", mwb.MachineMatrix);
        if (string.IsNullOrWhiteSpace(peers)) peers = "(none)";
        var keyOk = string.IsNullOrWhiteSpace(mwb.SecurityKey) ? "no SecurityKey" : "SecurityKey present";
        _status.Text = $"MWB host={mwb.LocalHostName} | {keyOk} | peers=[{peers}]";
    }

    private async Task ShutdownAsync()
    {
        try { _mwbWatch.Stop(); } catch { /* ignore */ }
        try { _trayMeter.Stop(); } catch { /* ignore */ }
        if (_swb != null && !_swb.IsDisposed)
        {
            await _swb.ShutdownAsync();
            _swb.Close();
            _swb.Dispose();
            _swb = null;
        }
        _tray.Visible = false;
        _tray.Dispose();
        _trayGlyph.Dispose();
        _trayMeter.Dispose();
    }

    private void ShowAbout()
    {
        ShowHostStatus();
        AppendLog("About: MWB+SWB Host (Garage). MWB first; SWB attaches silently. Tray L=RX R=TX. Ports TCP 15200 / UDP 15201.");
    }

    private void AppendLog(string line)
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            BeginInvoke(() => AppendLog(line));
            return;
        }
        _log.AppendText($"[{DateTime.Now:HH:mm:ss}] {line}{Environment.NewLine}");
    }
}