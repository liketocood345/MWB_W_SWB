using MwbSwb.Core;

namespace MwbSwb.Host;

/// <summary>
/// Tray shell for SWB as an MWB attachment. Follows Garage Mouse without Borders lifecycle.
/// </summary>
public sealed class HostForm : Form
{
    private readonly NotifyIcon _tray = new();
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

    public HostForm()
    {
        Text = "MWB+SWB Host";
        Width = 640;
        Height = 360;
        MinimumSize = new Size(520, 280);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9f);

        BuildUi();
        SetupTray();

        Shown += (_, _) =>
        {
            AppendLog("Host started as MWB attachment (follows Garage Mouse without Borders).");
            RefreshStatus();
            Hide();
            _mwbWatch.Tick += (_, _) => OnMwbWatchTick();
            _mwbWatch.Start();
            OnMwbWatchTick();
            if (_openSwbOnStart)
            {
                _openSwbOnStart = false;
                // Defer so Hide/handle settle before opening SWB + enabling synchro.
                BeginInvoke(RequestOpenSwb);
            }
        };

        FormClosing += async (_, e) =>
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                _tray.ShowBalloonTip(1500, "MWB+SWB", "Still running in the tray.", ToolTipIcon.Info);
                return;
            }

            await ShutdownAsync();
        };
    }

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
        _tray.Text = "Mouse without Borders + SWB";
        _tray.Icon = SystemIcons.Application;
        _tray.Visible = true;
        _tray.DoubleClick += (_, _) => OpenSwbWindow();
        var menu = new ContextMenuStrip();
        var openSwb = new ToolStripMenuItem("Open SWB window", null, (_, _) => OpenSwbWindow());
        openSwb.Font = new Font(openSwb.Font, FontStyle.Bold);
        menu.Items.Add(openSwb);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Open Host status", null, (_, _) => { Show(); Activate(); });
        menu.Items.Add("About", null, (_, _) => ShowAbout());
        menu.Items.Add("Close SWB Host", null, async (_, _) =>
        {
            await ShutdownAsync();
            Application.Exit();
        });
        _tray.ContextMenuStrip = menu;
    }

    private void OnMwbWatchTick()
    {
        var running = GarageMwbSettings.IsGarageProcessRunning();
        if (running && !_mwbWasRunning)
        {
            AppendLog("Garage MWB detected - SWB attachment active.");
            if (!_userClosedSwb)
                OpenSwbWindow(enableSynchro: true);
            _tray.ShowBalloonTip(
                3000,
                "MWB + SWB",
                "SWB attached to Mouse without Borders.",
                ToolTipIcon.Info);
        }
        else if (!running && _mwbWasRunning)
        {
            AppendLog("Garage MWB stopped - closing SWB attachment.");
            _ = CloseSwbOnlyAsync();
            _tray.ShowBalloonTip(2000, "MWB + SWB", "MWB exited; SWB Host stopping.", ToolTipIcon.Info);
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

        OpenSwbWindow(enableSynchro: true);
    }

    private void OpenSwbWindow(bool enableSynchro = false)
    {
        if (!GarageMwbSettings.IsGarageProcessRunning())
        {
            AppendLog("MWB not running - SWB stays closed (attachment mode).");
            _tray.ShowBalloonTip(2500, "MWB + SWB", "Start Garage Mouse without Borders first.", ToolTipIcon.Warning);
            return;
        }

        _userClosedSwb = false;
        if (_swb == null || _swb.IsDisposed)
        {
            _swb = new SwbForm();
            _swb.FormClosed += (_, _) =>
            {
                _userClosedSwb = true;
                AppendLog("SWB window closed by user (Host stays while MWB runs).");
            };
        }

        _swb.Show();
        _swb.WindowState = FormWindowState.Normal;
        _swb.Activate();
        if (enableSynchro)
            _swb.EnsureSoundSynchroEnabled();
        AppendLog("Opened SWB window (MWB attachment).");
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
        if (_swb != null && !_swb.IsDisposed)
        {
            await _swb.ShutdownAsync();
            _swb.Close();
            _swb.Dispose();
            _swb = null;
        }
        _tray.Visible = false;
        _tray.Dispose();
    }

    private static void ShowAbout()
    {
        MessageBox.Show(
            "MWB+SWB Host (Garage track)\n\n" +
            "- MWB first: keyboard/mouse via Garage Mouse without Borders.\n" +
            "- SWB is an attachment: starts/stops with MWB; window can be closed manually.\n" +
            "- Tray: open SWB window (only while MWB is running).\n" +
            "- Ports: SWB TCP 15200 / UDP 15201 (not MWB 15100/15101).",
            "About MWB+SWB",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
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