using MwbSwb.Audio;
using MwbSwb.Core;

namespace MwbSwb.Host;

/// <summary>
/// Independent Sound Synchro window. Local speaker always listed.
/// Remote rows appear when SecurityKey matches (name-probe reject discloses hostnames).
/// MWB machine matrix is optional hint only — not written by SWB.
/// </summary>
public sealed class SwbForm : Form
{
    private readonly CheckBox _chkSoundSynchro = new();
    private readonly TrackBar _tierSlider = new();
    private readonly ComboBox _tierCombo = new();
    private readonly Label _playbackLabel = new();
    private readonly ComboBox _playbackCombo = new();
    private readonly List<(string Id, string Name)> _playbackItems = new();
    private readonly Label _tierLabel = new();
    private readonly Label _tierValue = new();
    private readonly CheckBox _chkSend = new();
    private readonly CheckBox _chkRecv = new();
    private readonly CheckBox _chkAttenuate = new();
    private readonly RadioButton _rbSync = new();
    private readonly RadioButton _rb2d = new();
    private readonly RadioButton _rb3d = new();
    private readonly Button _btnHandshake = new();
    private readonly Label _mwbStatus = new();
    private readonly Label _swbPeerHint = new();
    private readonly ListView _matrix = new();
    private readonly TextBox _log = new();
    private readonly SpatialLayoutPanel _spatial = new();
    private readonly Panel _content = new();
    private readonly Panel _stage = new();
    private readonly Panel _grayOverlay = new();
    private readonly Label _grayLabel = new();
    private string _localMatrixHost = "";

    private MwbSettings _mwb = GarageMwbSettings.LoadOrEmpty();
    private SoundSynchroSettings _synchro = SoundSynchroSettings.LoadOrDefault();
    private SwbHandshakeService? _handshake;
    private AudioMatrixService? _audio;
    private ForceSyncCalibrator? _forceSync;
    private CancellationTokenSource? _probeCts;
    private readonly HashSet<string> _swbPeers = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _mwbOnlyPeers = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _autoFilledExactNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _liveSameKeyPeers = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _synchroGate = new(1, 1);
    private int _synchroBusy;
    private int _applyingRemoteSettings;
    private int _meshPushQueued;
    private bool _portConflict;

    public SwbForm()
    {
        Text = "Sound Synchro (SWB)";
        Width = 980;
        Height = 720;
        MinimumSize = new Size(860, 600);
        StartPosition = FormStartPosition.Manual;
        Location = new Point(-32000, -32000);
        Font = new Font("Segoe UI", 9f);
        ShowInTaskbar = false;

        BuildUi();
        ApplySynchroToUi();
        // Wire after Apply so persisted Enabled does not auto-bind ports during ctor.
        _chkSoundSynchro.CheckedChanged += async (_, _) => await OnSoundSynchroChangedAsync();
        RefreshMwbStatus();
        EnsureLocalMatrixRow();
        UpdateGrayOverlay();
    }

    /// <summary>Show SWB UI (tray / Launch). Attachment can run before this.</summary>
    public void RevealWindow()
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            BeginInvoke(RevealWindow);
            return;
        }

        ShowInTaskbar = true;
        StartPosition = FormStartPosition.CenterScreen;
        Location = new Point(
            Math.Max(0, (Screen.PrimaryScreen!.WorkingArea.Width - Width) / 2),
            Math.Max(0, (Screen.PrimaryScreen.WorkingArea.Height - Height) / 2));
        try
        {
            var stampPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MWB-SWB-Host", "BUILD_STAMP.txt");
            var stamp = File.Exists(stampPath) ? File.ReadAllText(stampPath).Trim() : "no-stamp";
            Text = "Sound Synchro (SWB)  |  " + stamp;
        }
        catch { Text = "Sound Synchro (SWB)"; }
        try
        {
            File.AppendAllText(@"C:\Users\Public\swb-enable.log",
                $"[{DateTime.Now:HH:mm:ss}] RevealWindow Visible={Visible} Loc={Location} Size={Size} Text={Text}{Environment.NewLine}");
        }
        catch { /* ignore */ }

        Opacity = 1;
        TopMost = true;
        ShowInTaskbar = true;
        WindowState = FormWindowState.Normal;
        Show();
        // Force on-screen even if multi-monitor math went weird
        var wa = Screen.PrimaryScreen!.WorkingArea;
        Width = Math.Min(980, wa.Width - 40);
        Height = Math.Min(720, wa.Height - 40);
        Location = new Point(
            Math.Max(20, (wa.Width - Width) / 2),
            Math.Max(20, (wa.Height - Height) / 2));
        BringToFront();
        Activate();
        Focus();
        _tierSlider.Focus();
        _tierSlider.BringToFront();
        _tierCombo.BringToFront();
        try
        {
            File.AppendAllText(@"C:\Users\Public\swb-enable.log",
                $"[{DateTime.Now:HH:mm:ss}] RevealWindow AFTER Visible={Visible} Loc={Location} sliderVal={_tierSlider.Value}{Environment.NewLine}");
        }
        catch { /* ignore */ }
        var drop = new System.Windows.Forms.Timer { Interval = 1500 };
        drop.Tick += (_, _) => { drop.Stop(); drop.Dispose(); if (!IsDisposed) TopMost = false; };
        drop.Start();
    }

    public async Task ShutdownAsync() => await TeardownAsync();

    /// <summary>TX/RX peak levels for tray metering (0..1). False when audio matrix is down.</summary>
    public bool TryGetAudioActivity(out float tx, out float rx)
    {
        tx = 0;
        rx = 0;
        if (_audio == null || !_audio.IsRunning)
            return false;
        (tx, rx) = _audio.SnapshotAndDecay();
        return true;
    }

    /// <summary>Turn on Sound Synchro (starts LAN handshake). Used when user explicitly wants synchro on.</summary>
    public void EnsureSoundSynchroEnabled()
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            BeginInvoke(EnsureSoundSynchroEnabled);
            return;
        }

        try
        {
            File.AppendAllText(@"C:\Users\Public\swb-enable.log",
                $"[{DateTime.Now:HH:mm:ss}] EnsureSoundSynchroEnabled checked={_chkSoundSynchro.Checked} busy={_synchroBusy} hs={_handshake != null}{Environment.NewLine}");
        }
        catch { /* ignore */ }

        RefreshMwbStatus();
        if (_portConflict)
            return;
        // Already up — do not tear down / rebind ports (avoids address-in-use + dual dial).
        if (_chkSoundSynchro.Checked && _handshake != null && _synchroBusy == 0)
            return;

        if (!_chkSoundSynchro.Checked)
            _chkSoundSynchro.Checked = true;
        else
            _ = OnSoundSynchroChangedAsync();
    }

    /// <summary>
    /// Boot / MWB-attach: start mesh only if last session left Sound Synchro Enabled=true.
    /// Does not force-on when user previously turned it off.
    /// </summary>
    public void ApplyPersistedSynchroState()
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            BeginInvoke(ApplyPersistedSynchroState);
            return;
        }

        try
        {
            File.AppendAllText(@"C:\Users\Public\swb-enable.log",
                $"[{DateTime.Now:HH:mm:ss}] ApplyPersistedSynchroState enabled={_synchro.Enabled} checked={_chkSoundSynchro.Checked} hs={_handshake != null}{Environment.NewLine}");
        }
        catch { /* ignore */ }

        RefreshMwbStatus();
        if (_portConflict)
            return;

        if (!_synchro.Enabled)
        {
            // Keep checkbox off; do not start handshake.
            if (_chkSoundSynchro.Checked)
            {
                Interlocked.Exchange(ref _applyingRemoteSettings, 1);
                try { _chkSoundSynchro.Checked = false; }
                finally { Interlocked.Exchange(ref _applyingRemoteSettings, 0); }
            }
            return;
        }

        // Persisted on — enable mesh (same as Ensure when already intended on).
        EnsureSoundSynchroEnabled();
    }

    private void BuildUi()
    {
        _content.Dock = DockStyle.Fill;

                var top = new Panel { Dock = DockStyle.Top, Height = 300, Padding = new Padding(10) };

        var title = new Label
        {
            AutoSize = true,
            Location = new Point(12, 6),
            ForeColor = Color.DimGray,
            Text = "This is the SWB window (latency slider below). Host status has NO slider — use tray Open SWB window.",
        };

        _chkSoundSynchro.Text = "Sound Synchro (on = LAN beacon + same-key name-probe; MWB list is hint only)";
        _chkSoundSynchro.AutoSize = true;
        _chkSoundSynchro.Location = new Point(12, 28);

        _tierLabel.AutoSize = true;
        _tierLabel.Location = new Point(12, 54);
        _tierLabel.Font = new Font("Segoe UI", 12f, FontStyle.Bold);
        _tierLabel.ForeColor = Color.DarkBlue;
        _tierLabel.Text = "Latency slider (drag 0..6)  /  delay slider:";

        _tierSlider.Location = new Point(12, 80);
        _tierSlider.Width = 520;
        _tierSlider.Height = 48;
        _tierSlider.Minimum = 0;
        _tierSlider.Maximum = 6;
        _tierSlider.TickFrequency = 1;
        _tierSlider.TickStyle = TickStyle.Both;
        _tierSlider.LargeChange = 1;
        _tierSlider.SmallChange = 1;
        _tierSlider.Value = 3;
        _tierSlider.BackColor = Color.AliceBlue;
        _tierSlider.Scroll += (_, _) => OnTierSliderScroll();

        _tierValue.AutoSize = true;
        _tierValue.Location = new Point(540, 92);
        _tierValue.Font = new Font("Segoe UI", 11f, FontStyle.Bold);
        _tierValue.ForeColor = Color.DarkRed;
        _tierValue.Text = "3 Balanced";

        _tierCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _tierCombo.Location = new Point(12, 136);
        _tierCombo.Width = 520;
        _tierCombo.Height = 28;
        _tierCombo.Font = new Font("Segoe UI", 10f);
        _tierCombo.Items.Clear();
        for (var i = 0; i <= 6; i++)
            _tierCombo.Items.Add(i + " — " + AudioTier.EnglishName(i));
        _tierCombo.SelectedIndex = 3;
        _tierCombo.SelectedIndexChanged += (_, _) => OnTierComboChanged();

        var tierHints = new Label
        {
            AutoSize = true,
            Location = new Point(12, 170),
            ForeColor = Color.DimGray,
            Text = "0 Force sync ... 3 Balanced ... 6 Ultra-low   (slider + dropdown are linked)",
        };

        _playbackLabel.AutoSize = true;
        _playbackLabel.Location = new Point(12, 198);
        _playbackLabel.Text = "Local playback (single-PC output):";
        _playbackCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _playbackCombo.Location = new Point(12, 220);
        _playbackCombo.Width = 520;
        _playbackCombo.Font = new Font("Segoe UI", 10f);
        _playbackCombo.SelectedIndexChanged += (_, _) => OnPlaybackDeviceChanged();
        RefreshPlaybackDeviceList();

        _chkSend.Text = "Send local loopback";
        _chkSend.AutoSize = true;
        _chkSend.Checked = true;
        _chkSend.Location = new Point(560, 28);
        _chkSend.CheckedChanged += (_, _) =>
        {
            if (_applyingRemoteSettings != 0) return;
            _synchro.SendLocalLoopback = _chkSend.Checked;
            _synchro.Save();
            PushMeshSettingsSoon();
        };

        _chkRecv.Text = "Receive & mix";
        _chkRecv.AutoSize = true;
        _chkRecv.Checked = true;
        _chkRecv.Location = new Point(560, 52);
        _chkRecv.CheckedChanged += (_, _) =>
        {
            if (_applyingRemoteSettings != 0) return;
            _synchro.ReceiveAndMix = _chkRecv.Checked;
            _synchro.Save();
            PushMeshSettingsSoon();
        };

        _chkAttenuate.Text = "Distance attenuation";
        _chkAttenuate.AutoSize = true;
        _chkAttenuate.Location = new Point(310, 252);
        _chkAttenuate.CheckedChanged += (_, _) =>
        {
            if (_applyingRemoteSettings != 0) return;
            _synchro.DistanceAttenuation = _chkAttenuate.Checked;
            _synchro.Save();
            _audio?.ApplySpatial(_synchro);
            PushMeshSettingsSoon();
        };

        _rbSync.Text = "Sync only";
        _rbSync.AutoSize = true;
        _rbSync.Location = new Point(480, 252);
        _rbSync.CheckedChanged += (_, _) =>
        {
            if (!_rbSync.Checked || _applyingRemoteSettings != 0) return;
            _synchro.SpatialMode = SpatialLayoutMode.SyncOnly;
            _synchro.Save();
            _spatial.Bind(_synchro);
            _audio?.ApplySpatial(_synchro);
            PushLocalPoseToHandshake();
            PushMeshSettingsSoon();
        };

        _rb2d.Text = "2D ring";
        _rb2d.AutoSize = true;
        _rb2d.Location = new Point(570, 252);
        _rb2d.CheckedChanged += (_, _) =>
        {
            if (!_rb2d.Checked || _applyingRemoteSettings != 0) return;
            _synchro.SpatialMode = SpatialLayoutMode.Ring2D;
            _synchro.Save();
            _spatial.Bind(_synchro);
            _audio?.ApplySpatial(_synchro);
            PushLocalPoseToHandshake();
            PushMeshSettingsSoon();
        };

        _rb3d.Text = "3D sphere";
        _rb3d.AutoSize = true;
        _rb3d.Location = new Point(650, 252);
        _rb3d.CheckedChanged += (_, _) =>
        {
            if (!_rb3d.Checked || _applyingRemoteSettings != 0) return;
            _synchro.SpatialMode = SpatialLayoutMode.Sphere3D;
            _synchro.Save();
            _spatial.Bind(_synchro);
            _audio?.ApplySpatial(_synchro);
            PushLocalPoseToHandshake();
            PushMeshSettingsSoon();
        };

        _btnHandshake.Text = "Re-probe peers";
        _btnHandshake.Location = new Point(740, 252);
        _btnHandshake.Width = 140;
        _btnHandshake.Enabled = false;
        _btnHandshake.Click += async (_, _) => await ProbeAndRefreshAsync();

        _mwbStatus.AutoSize = true;
        _mwbStatus.Location = new Point(12, 280);
        _swbPeerHint.AutoSize = true;
        _swbPeerHint.Location = new Point(520, 280);

        top.Controls.Add(title);
        top.Controls.Add(_chkSoundSynchro);
        top.Controls.Add(_tierLabel);
        top.Controls.Add(_tierSlider);
        top.Controls.Add(_tierValue);
        top.Controls.Add(_tierCombo);
        top.Controls.Add(tierHints);
        top.Controls.Add(_playbackLabel);
        top.Controls.Add(_playbackCombo);
        top.Controls.Add(_chkSend);
        top.Controls.Add(_chkRecv);
        top.Controls.Add(_chkAttenuate);
        top.Controls.Add(_rbSync);
        top.Controls.Add(_rb2d);
        top.Controls.Add(_rb3d);
        top.Controls.Add(_btnHandshake);
        top.Controls.Add(_mwbStatus);
        top.Controls.Add(_swbPeerHint);

        var matrixLabel = new Label
        {
            Dock = DockStyle.Top,
            Height = 22,
            Text = "SWB matrix (local speaker always listed; peers when SecurityKey matches)",
            Padding = new Padding(12, 4, 0, 0),
        };

        _matrix.Dock = DockStyle.Top;
        _matrix.Height = 160;
        _matrix.View = View.Details;
        _matrix.FullRowSelect = true;
        _matrix.CheckBoxes = true;
        _matrix.Columns.Add("Host", 220);
        _matrix.Columns.Add("IP", 120);
        _matrix.Columns.Add("Port", 60);
        _matrix.Columns.Add("Stereo", 70);
        _matrix.Columns.Add("SWB", 80);
        _matrix.ItemChecked += (_, e) =>
        {
            if (e.Item == null || _audio == null) return;
            if (IsLocalMatrixItem(e.Item))
            {
                if (!e.Item.Checked) e.Item.Checked = true;
                return;
            }
            _audio.SetInclude(e.Item.Text, e.Item.Checked);
        };

        _spatial.Dock = DockStyle.Top;
        _spatial.Height = 220;
        _spatial.LayoutChanged += () =>
        {
            _synchro.Save(bumpEpoch: false);
            _audio?.ApplySpatial(_synchro);
            PushLocalPoseToHandshake();
        };

        _log.Dock = DockStyle.Fill;
        _log.Multiline = true;
        _log.ScrollBars = ScrollBars.Vertical;
        _log.ReadOnly = true;
        _log.Font = new Font("Consolas", 9f);

        _stage.Dock = DockStyle.Fill;
        _stage.Controls.Add(_log);
        _stage.Controls.Add(_spatial);

        _grayOverlay.Dock = DockStyle.Fill;
        _grayOverlay.BackColor = Color.FromArgb(210, 128, 128, 128);
        _grayOverlay.Visible = false;
        _grayLabel.Dock = DockStyle.Fill;
        _grayLabel.TextAlign = ContentAlignment.MiddleCenter;
        _grayLabel.ForeColor = Color.White;
        _grayLabel.Font = new Font("Segoe UI", 14f, FontStyle.Bold);
        _grayLabel.Text =
            "Gray - no remote SWB peer with matching SecurityKey yet.\n" +
            "Local speaker stays listed above. MWB interconnect is unchanged.";
        _grayOverlay.Controls.Add(_grayLabel);
        _stage.Controls.Add(_grayOverlay);

        _content.Controls.Add(_stage);
        _content.Controls.Add(_matrix);
        _content.Controls.Add(matrixLabel);
        _content.Controls.Add(top);

        Controls.Add(_content);
    }

    private void RefreshPlaybackDeviceList()
    {
        _playbackItems.Clear();
        _playbackItems.Add((LocalAudioDeviceInfo.AllDevicesId, "All devices"));
        foreach (var ep in LocalAudioDeviceInfo.ListActiveRenderEndpoints())
            _playbackItems.Add((ep.Id, ep.FriendlyName));
        var keep = _playbackCombo.SelectedIndex;
        _playbackCombo.Items.Clear();
        foreach (var (_, name) in _playbackItems)
            _playbackCombo.Items.Add(name);
        if (_playbackCombo.Items.Count > 0)
            _playbackCombo.SelectedIndex = Math.Clamp(keep < 0 ? 0 : keep, 0, _playbackCombo.Items.Count - 1);
    }

    private void SelectPlaybackFromSettings()
    {
        RefreshPlaybackDeviceList();
        var want = string.IsNullOrWhiteSpace(_synchro.LocalPlaybackDeviceId)
            ? LocalAudioDeviceInfo.AllDevicesId
            : _synchro.LocalPlaybackDeviceId.Trim();
        var idx = _playbackItems.FindIndex(x => string.Equals(x.Id, want, StringComparison.OrdinalIgnoreCase));
        if (idx < 0) idx = 0;
        if (_playbackCombo.Items.Count > idx)
            _playbackCombo.SelectedIndex = idx;
    }

    private void OnPlaybackDeviceChanged()
    {
        if (_playbackCombo.SelectedIndex < 0 || _playbackCombo.SelectedIndex >= _playbackItems.Count) return;
        var id = _playbackItems[_playbackCombo.SelectedIndex].Id;
        _synchro.LocalPlaybackDeviceId = id;
        _synchro.Save(bumpEpoch: false);
        _audio?.SetPlaybackDeviceId(id);
    }

    private void OnTierSliderScroll()
    {
        ApplyTierFromUi(_tierSlider.Value, fromSlider: true);
    }

    private void OnTierComboChanged()
    {
        if (_tierCombo.SelectedIndex < 0) return;
        ApplyTierFromUi(_tierCombo.SelectedIndex, fromSlider: false);
    }

    private void ApplyTierFromUi(int tier, bool fromSlider)
    {
        if (_applyingRemoteSettings != 0) return;
        tier = Math.Clamp(tier, 0, 6);
        _synchro.AudioTier = tier;
        _synchro.Save();
        if (_tierSlider.Value != tier)
            _tierSlider.Value = tier;
        if (_tierCombo.SelectedIndex != tier && _tierCombo.Items.Count > tier)
            _tierCombo.SelectedIndex = tier;
        _tierValue.Text = tier + " " + AudioTier.EnglishName(tier);
        _tierLabel.Text = "Latency slider (drag 0..6)  /  " + AudioTier.EnglishName(tier);
        _audio?.SetAudioTier(tier);
        _audio?.ApplySpatial(_synchro);
        PushMeshSettingsSoon();
        if (tier <= 2)
            _ = ProbeAndRefreshAsync();
    }

    private void ApplySynchroToUi()
    {
        _chkSoundSynchro.Checked = _synchro.Enabled;
        var ti = Math.Clamp(_synchro.AudioTier, 0, 6);
        _tierSlider.Value = ti;
        if (_tierCombo.Items.Count > ti)
            _tierCombo.SelectedIndex = ti;
        _tierValue.Text = ti + " " + AudioTier.EnglishName(ti);
        _tierLabel.Text = "Latency slider (drag 0..6)  /  " + AudioTier.EnglishName(ti);
        _chkAttenuate.Checked = _synchro.DistanceAttenuation;
        _chkSend.Checked = _synchro.SendLocalLoopback;
        _chkRecv.Checked = _synchro.ReceiveAndMix;
        if (_synchro.SpatialMode == SpatialLayoutMode.SyncOnly) _rbSync.Checked = true;
        else if (_synchro.SpatialMode == SpatialLayoutMode.Sphere3D) _rb3d.Checked = true;
        else _rb2d.Checked = true;
        _spatial.Bind(_synchro);
        SelectPlaybackFromSettings();
    }

    private void RefreshMwbStatus()
    {
        _mwb = GarageMwbSettings.LoadOrEmpty();
        if (!_mwb.IsReadyForSwb)
            _mwb = MwbSettings.LoadOrEmpty();
        var peers = string.Join(", ", _mwb.MachineMatrix);
        if (string.IsNullOrWhiteSpace(peers)) peers = "(none - optional hint)";
        var keyOk = string.IsNullOrWhiteSpace(_mwb.SecurityKey) ? "no SecurityKey" : "SecurityKey present";
        var swbReady = _mwb.IsReadyForSwb ? "SWB ready (key)" : "SWB needs SecurityKey";
        _mwbStatus.Text = $"MWB identity: host={_mwb.LocalHostName} | {keyOk} | {swbReady} | MWB hints=[{peers}]";
    }

    private void UpdateGrayOverlay()
    {
        var synchroOn = _chkSoundSynchro.Checked;
        var noSwb = _swbPeers.Count == 0;
        _grayOverlay.Visible = synchroOn && noSwb;
        if (_grayOverlay.Visible)
            _grayOverlay.BringToFront();
        _swbPeerHint.Text = synchroOn
            ? $"SWB peers: {_swbPeers.Count} | named/hints: {_mwbOnlyPeers.Count}" +
              (noSwb ? " | gray stage = waiting same-key peer" : "")
            : "Sound Synchro off - local speaker still listed; enable to probe.";
    }

    private async Task OnSoundSynchroChangedAsync()
    {
        if (_applyingRemoteSettings != 0)
            return;
        if (Interlocked.CompareExchange(ref _synchroBusy, 1, 0) != 0)
            return;

        await _synchroGate.WaitAsync().ConfigureAwait(true);
        try
        {
            _synchro.Enabled = _chkSoundSynchro.Checked;
            _synchro.Save();
            PushMeshSettingsSoon();
            if (_chkSoundSynchro.Checked)
                await EnableSoundSynchroAsync();
            else
            {
                await TeardownAsync();
                _matrix.Items.Clear();
                _swbPeers.Clear();
                _mwbOnlyPeers.Clear();
                EnsureLocalMatrixRow();
                UpdateGrayOverlay();
            }
        }
        finally
        {
            _synchroGate.Release();
            Interlocked.Exchange(ref _synchroBusy, 0);
        }
    }

    private async Task EnableSoundSynchroAsync()
    {
        void Diag(string m)
        {
            try
            {
                File.AppendAllText(
                    @"C:\Users\Public\swb-enable.log",
                    $"[{DateTime.Now:HH:mm:ss}] {m}{Environment.NewLine}");
            }
            catch { /* ignore */ }
            AppendLog(m);
        }

        RefreshMwbStatus();
        Diag($"Enable start keyLen={_mwb.SecurityKey?.Length ?? 0} ready={_mwb.IsReadyForSwb}");
        if (!_mwb.IsReadyForSwb)
        {
            _chkSoundSynchro.Checked = false;
            _synchro.Enabled = false;
            _synchro.Save();
            UpdateGrayOverlay();
            Diag("ABORT: SecurityKey missing");
            AppendLog("MWB SecurityKey missing. Configure Garage MWB SecurityKey first. SWB does not require the MWB machine list.");
            return;
        }

        try
        {
            await TeardownAsync();
            _matrix.Items.Clear();
            _swbPeers.Clear();
            _mwbOnlyPeers.Clear();
            _liveSameKeyPeers.Clear();
            EnsureLocalMatrixRow();

            _audio = new AudioMatrixService();
            _audio.Log += AppendLog;
            _handshake = new SwbHandshakeService(_mwb)
            {
                LocalRole = SwbPeerRole.Both,
                SampleRate = 48000,
                Channels = 2,
                LocalMeshSettings = _synchro.ToMeshSettings(),
            };
            PushLocalPoseToHandshake();
            _handshake.Log += AppendLog;
            _handshake.MeshSettingsReceived += (ms, fromHost) =>
            {
                BeginInvoke(() => ApplyRemoteMeshSettings(ms, fromHost));
            };
            _handshake.PeerNamed += named =>
            {
                BeginInvoke(() =>
                {
                    var host = named.HostName;
                    var ip = named.IpAddress;
                    if (string.IsNullOrWhiteSpace(host))
                        return; // no name obtained → fill nothing
                    if (host.Equals(_mwb.LocalHostName, StringComparison.OrdinalIgnoreCase))
                        return;
                    _mwbOnlyPeers.Add(host);
                    _liveSameKeyPeers.Add(host);
                    if (!string.IsNullOrWhiteSpace(ip))
                        _liveSameKeyPeers.Add(ip);
                    UpsertNamedPeerRow(host, ip);
                    _synchro.UpsertAdvertisedPose(host, named.AzimuthDeg, named.ElevationDeg, named.Radius);
                    _spatial.Bind(_synchro);
                    _audio?.ApplySpatial(_synchro);
                    _synchro.Save(bumpEpoch: false);

                    TryFillMwbMatrixOnce(host);
                    UpdateGrayOverlay();
                });
            };
            _handshake.PeerConfirmed += peer =>
            {
                BeginInvoke(() =>
                {
                    _mwbOnlyPeers.Remove(peer.HostName);
                    _swbPeers.Add(peer.HostName);
                    _liveSameKeyPeers.Add(peer.HostName);
                    if (!string.IsNullOrWhiteSpace(peer.IpAddress))
                        _liveSameKeyPeers.Add(peer.IpAddress);
                    _audio?.UpsertPeer(peer, includeInMatrix: true);
                    _synchro.UpsertAdvertisedPose(peer.HostName, peer.AzimuthDeg, peer.ElevationDeg, peer.Radius);
                    _spatial.Bind(_synchro);
                    _audio?.ApplySpatial(_synchro);
                    _synchro.Save(bumpEpoch: false);
                    UpsertMatrixRow(peer);
                    TryFillMwbMatrixOnce(peer.HostName);
                    UpdateGrayOverlay();
                    PushMeshSettingsSoon();
                    try
                    {
                        File.AppendAllText(@"C:\Users\Public\swb-enable.log",
                            $"[{DateTime.Now:HH:mm:ss}] MESH_OK peers={_swbPeers.Count} last={peer.HostName}@{peer.IpAddress}{Environment.NewLine}");
                    }
                    catch { /* ignore */ }
                });
            };

            await _handshake.StartAsync();
            Diag("handshake.StartAsync OK (TCP 15200)");
            try
            {
                _audio.SetAudioTier(_synchro.AudioTier);
                _audio.ApplySpatial(_synchro);
                await _audio.StartAsync(
                    SwbHandshakeService.DefaultAudioPort,
                    sampleRate: 48000,
                    sendLocalLoopback: _chkSend.Checked,
                    receiveAndMix: _chkRecv.Checked);
                Diag("audio.StartAsync OK tier=" + _synchro.AudioTier);
            }
            catch (Exception audioEx)
            {
                // Server VMs often lack a playback/capture device — keep control handshake alive.
                Diag("Audio start degraded (handshake stays up): " + audioEx.Message);
            }

            _forceSync = new ForceSyncCalibrator { ToleranceMs = _synchro.TargetSyncToleranceMs };
            _forceSync.Log += AppendLog;
            _probeCts = new CancellationTokenSource();
            _ = ForceSyncCalibrator.RunProbeResponderAsync(SwbHandshakeService.DefaultAudioPort, _probeCts.Token);

            _btnHandshake.Enabled = true;
            await ProbeAndRefreshAsync();
            Diag("Sound Synchro enabled.");
            UpdateGrayOverlay();
        }
        catch (Exception ex)
        {
            var msg = ex.Message ?? "";
            var addrInUse = ex is System.Net.Sockets.SocketException
                || (ex.InnerException is System.Net.Sockets.SocketException)
                || msg.Contains("只允许使用一次", StringComparison.Ordinal)
                || msg.Contains("Address already in use", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("only one usage", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("10048", StringComparison.Ordinal)
                || msg.Contains("address", StringComparison.OrdinalIgnoreCase) && msg.Contains("use", StringComparison.OrdinalIgnoreCase);
            if (addrInUse)
            {
                _portConflict = true;
                // Do not flip checkbox in a loop — another Host instance owns the ports.
                try
                {
                    File.AppendAllText(@"C:\Users\Public\swb-enable.log",
                        $"[{DateTime.Now:HH:mm:ss}] Port conflict (another Host?) — stop retrying until restart{Environment.NewLine}");
                }
                catch { /* ignore */ }
                AppendLog("Port conflict: another MwbSwb.Host likely holds TCP/UDP 15200-15202. Close extras.");
                await TeardownAsync();
                UpdateGrayOverlay();
                return;
            }

            _chkSoundSynchro.Checked = false;
            _synchro.Enabled = false;
            _synchro.Save();
            Diag("Enable failed (MWB unaffected): " + msg);
            AppendLog("Enable failed: " + msg);
            await TeardownAsync();
            EnsureLocalMatrixRow();
            UpdateGrayOverlay();
        }
    }

    private async Task ProbeAndRefreshAsync()
    {
        if (_handshake == null) return;
        EnsureLocalMatrixRow();

        await _handshake.DiscoverAndConnectAsync();

        foreach (var ok in _swbPeers)
            _mwbOnlyPeers.Remove(ok);

        if (_synchro.AudioTier <= 2 && _forceSync != null && _audio != null)
        {
            var hosts = _swbPeers.ToList();
            if (hosts.Count > 0)
            {
                var ok = await _forceSync.CalibrateAsync(hosts, SwbHandshakeService.DefaultAudioPort);
                if (!ok)
                    AppendLog("ForceSync calibration degraded — SWB matrix still usable.");
                _audio.SetForceSyncOffsetProvider(host => _forceSync.GetWatermarkOffsetMs(host));
            }
            else
            {
                _audio.SetForceSyncOffsetProvider(null);
            }
        }
        else
        {
            _audio?.SetForceSyncOffsetProvider(null);
        }

        _audio?.ApplySpatial(_synchro);
        UpdateGrayOverlay();
        AppendLog($"Probe done. SWB={_swbPeers.Count}, named/hints={_mwbOnlyPeers.Count}, local speaker listed");
    }


    /// <summary>
    /// Auto-fill MWB matrix once per exact identical name. No name → no write.
    /// Manual non-empty slots are never overwritten (enforced in GarageMwbSettings).
    /// </summary>
    private void TryFillMwbMatrixOnce(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return;
        if (host.Equals(_mwb.LocalHostName, StringComparison.OrdinalIgnoreCase))
            return;
        if (!_autoFilledExactNames.Add(host))
            return;

        var result = GarageMwbSettings.TryAutoFillDiscoveredNameOnce(host, _liveSameKeyPeers, out var detail);
        switch (result)
        {
            case GarageMwbSettings.AutoFillResult.TooManySameKey:
                AppendLog(detail + " (MWB max " + GarageMwbSettings.MaxMachines + " machines; extras not written)");
                break;
            case GarageMwbSettings.AutoFillResult.Filled:
            case GarageMwbSettings.AutoFillResult.ReclaimedLeftover:
                AppendLog("Matrix auto-fill: " + detail + " (order of other slots unchanged)");
                RefreshMwbStatus();
                break;
            default:
                AppendLog("Skip matrix fill for '" + host + "': " + detail);
                break;
        }
    }

    private void EnsureLocalMatrixRow()
    {
        _localMatrixHost = LocalAudioDeviceInfo.FormatLocalMatrixHost(_mwb.LocalHostName);
        _synchro.GetOrCreatePose(_mwb.LocalHostName);
        _spatial.Bind(_synchro);
        PushLocalPoseToHandshake();

        foreach (ListViewItem existing in _matrix.Items)
        {
            if (IsLocalMatrixItem(existing))
            {
                existing.Text = _localMatrixHost;
                existing.SubItems[1].Text = "127.0.0.1";
                existing.SubItems[2].Text = "-";
                existing.SubItems[3].Text = "OK";
                existing.SubItems[4].Text = LocalAudioDeviceInfo.LocalMatrixTag;
                existing.Checked = true;
                return;
            }
        }

        var item = new ListViewItem(_localMatrixHost) { Checked = true, Tag = LocalAudioDeviceInfo.LocalMatrixTag };
        item.SubItems.Add("127.0.0.1");
        item.SubItems.Add("-");
        item.SubItems.Add("OK");
        item.SubItems.Add(LocalAudioDeviceInfo.LocalMatrixTag);
        _matrix.Items.Insert(0, item);
    }

    private void PushLocalPoseToHandshake()
    {
        if (_handshake == null) return;
        var pose = _synchro.GetLocalPose(_mwb.LocalHostName);
        _handshake.LocalAzimuthDeg = pose.AzimuthDeg;
        _handshake.LocalElevationDeg = pose.ElevationDeg;
        _handshake.LocalRadius = pose.Radius;
        _handshake.LocalMeshSettings = _synchro.ToMeshSettings();
    }

    private void PushMeshSettingsSoon()
    {
        if (_handshake == null) return;
        _handshake.LocalMeshSettings = _synchro.ToMeshSettings();
        if (Interlocked.Exchange(ref _meshPushQueued, 1) != 0)
            return;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(250).ConfigureAwait(false);
                Interlocked.Exchange(ref _meshPushQueued, 0);
                var hs = _handshake;
                if (hs == null) return;
                hs.LocalMeshSettings = _synchro.ToMeshSettings();
                await hs.PushSettingsToMeshAsync().ConfigureAwait(false);
            }
            catch
            {
                Interlocked.Exchange(ref _meshPushQueued, 0);
            }
        });
    }

    private void ApplyRemoteMeshSettings(SwbMeshSettings remote, string fromHost)
    {
        if (!_synchro.TryApplyMeshSettings(remote))
            return;

        Interlocked.Exchange(ref _applyingRemoteSettings, 1);
        try
        {
            ApplySynchroToUi();
            _audio?.SetAudioTier(_synchro.AudioTier);
            _audio?.SetSendRecv(_synchro.SendLocalLoopback, _synchro.ReceiveAndMix);
            _audio?.ApplySpatial(_synchro);
            if (_handshake != null)
                _handshake.LocalMeshSettings = _synchro.ToMeshSettings();
            AppendLog($"Mesh settings from {fromHost}: tier={_synchro.AudioTier} spatial={_synchro.SpatialMode} epoch={remote.Epoch}");
        }
        finally
        {
            Interlocked.Exchange(ref _applyingRemoteSettings, 0);
        }
    }

    private static bool IsLocalMatrixItem(ListViewItem item) =>
        Equals(item.Tag, LocalAudioDeviceInfo.LocalMatrixTag)
        || (item.SubItems.Count > 4
            && item.SubItems[4].Text.Equals(LocalAudioDeviceInfo.LocalMatrixTag, StringComparison.OrdinalIgnoreCase));

    private void UpsertNamedPeerRow(string hostName, string ip)
    {
        if (hostName.Equals(_mwb.LocalHostName, StringComparison.OrdinalIgnoreCase))
            return;
        foreach (ListViewItem existing in _matrix.Items)
        {
            if (IsLocalMatrixItem(existing)) continue;
            if (existing.Text.Equals(hostName, StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(ip))
                    existing.SubItems[1].Text = ip;
                if (existing.SubItems[4].Text != "yes")
                    existing.SubItems[4].Text = "named";
                return;
            }
        }

        var item = new ListViewItem(hostName) { Checked = false };
        item.SubItems.Add(string.IsNullOrWhiteSpace(ip) ? "?" : ip);
        item.SubItems.Add(SwbHandshakeService.DefaultControlPort.ToString());
        item.SubItems.Add("?");
        item.SubItems.Add("named");
        _matrix.Items.Add(item);
    }

    private void UpsertMatrixRow(SwbPeerInfo peer)
    {
        foreach (ListViewItem existing in _matrix.Items)
        {
            if (IsLocalMatrixItem(existing)) continue;
            if (existing.Text.Equals(peer.HostName, StringComparison.OrdinalIgnoreCase))
            {
                existing.Checked = true;
                existing.SubItems[1].Text = peer.IpAddress;
                existing.SubItems[2].Text = peer.AudioPort.ToString();
                existing.SubItems[3].Text = peer.StereoOk ? "OK" : "NO";
                existing.SubItems[4].Text = "yes";
                return;
            }
        }

        var item = new ListViewItem(peer.HostName) { Checked = true };
        item.SubItems.Add(peer.IpAddress);
        item.SubItems.Add(peer.AudioPort.ToString());
        item.SubItems.Add(peer.StereoOk ? "OK" : "NO");
        item.SubItems.Add("yes");
        _matrix.Items.Add(item);
    }

    private async Task TeardownAsync()
    {
        _btnHandshake.Enabled = false;
        try { _probeCts?.Cancel(); } catch { /* ignore */ }
        _probeCts?.Dispose();
        _probeCts = null;
        if (_handshake != null)
        {
            await _handshake.DisposeAsync();
            _handshake = null;
        }
        if (_audio != null)
        {
            await _audio.DisposeAsync();
            _audio = null;
        }
        _forceSync = null;
        AppendLog("Sound Synchro stopped.");
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
        // Persist mesh/handshake lines for headless attach (no UI window).
        if (line.Contains("Handshake", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Dial yield", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Discovery peer", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Name-probe", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Peer confirmed", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Mesh retry", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Probe done", StringComparison.OrdinalIgnoreCase)
            || line.Contains("loopback=", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Audio matrix up", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                File.AppendAllText(@"C:\Users\Public\swb-enable.log",
                    $"[{DateTime.Now:HH:mm:ss}] {line}{Environment.NewLine}");
            }
            catch { /* ignore */ }
        }
    }
}
