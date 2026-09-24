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
    private readonly CheckBox _chkForceSync = new();
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
    private bool _tooManySameKeyPrompted;

    public SwbForm()
    {
        Text = "Sound Synchro (SWB)";
        Width = 980;
        Height = 720;
        MinimumSize = new Size(860, 600);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9f);

        BuildUi();
        ApplySynchroToUi();
        RefreshMwbStatus();
        EnsureLocalMatrixRow();
        UpdateGrayOverlay();
    }

    public async Task ShutdownAsync() => await TeardownAsync();

    /// <summary>Turn on Sound Synchro (starts LAN handshake). Used by Host /open-swb.</summary>
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
                $"[{DateTime.Now:HH:mm:ss}] EnsureSoundSynchroEnabled checked={_chkSoundSynchro.Checked}{Environment.NewLine}");
        }
        catch { /* ignore */ }

        RefreshMwbStatus();
        // Always kick enable path (CheckedChanged may not fire during early Show).
        if (!_chkSoundSynchro.Checked)
            _chkSoundSynchro.Checked = true;
        else
            _ = OnSoundSynchroChangedAsync();
    }

    private void BuildUi()
    {
        _content.Dock = DockStyle.Fill;

        var top = new Panel { Dock = DockStyle.Top, Height = 140, Padding = new Padding(10) };

        var title = new Label
        {
            AutoSize = true,
            Location = new Point(12, 8),
            ForeColor = Color.DimGray,
            Text = "SWB attachment to MWB — local speaker listed; same SecurityKey auto-names peers.",
        };

        _chkSoundSynchro.Text = "Sound Synchro (on = LAN beacon + same-key name-probe; MWB list is hint only)";
        _chkSoundSynchro.AutoSize = true;
        _chkSoundSynchro.Location = new Point(12, 36);
        _chkSoundSynchro.CheckedChanged += async (_, _) => await OnSoundSynchroChangedAsync();

        _chkForceSync.Text = "Force sound sync";
        _chkForceSync.AutoSize = true;
        _chkForceSync.Location = new Point(12, 60);
        _chkForceSync.CheckedChanged += (_, _) =>
        {
            _synchro.ForceSoundSync = _chkForceSync.Checked;
            _synchro.Save();
        };

        _chkSend.Text = "Send local loopback";
        _chkSend.AutoSize = true;
        _chkSend.Checked = true;
        _chkSend.Location = new Point(420, 36);

        _chkRecv.Text = "Receive & mix";
        _chkRecv.AutoSize = true;
        _chkRecv.Checked = true;
        _chkRecv.Location = new Point(420, 60);

        _chkAttenuate.Text = "Distance attenuation";
        _chkAttenuate.AutoSize = true;
        _chkAttenuate.Location = new Point(12, 84);
        _chkAttenuate.CheckedChanged += (_, _) =>
        {
            _synchro.DistanceAttenuation = _chkAttenuate.Checked;
            _synchro.Save();
            _audio?.ApplySpatial(_synchro);
        };

        _rbSync.Text = "Sync only";
        _rbSync.AutoSize = true;
        _rbSync.Location = new Point(220, 84);
        _rbSync.CheckedChanged += (_, _) =>
        {
            if (!_rbSync.Checked) return;
            _synchro.SpatialMode = SpatialLayoutMode.SyncOnly;
            _synchro.Save();
            _spatial.Bind(_synchro);
            _audio?.ApplySpatial(_synchro);
            PushLocalPoseToHandshake();
        };

        _rb2d.Text = "2D ring";
        _rb2d.AutoSize = true;
        _rb2d.Location = new Point(310, 84);
        _rb2d.CheckedChanged += (_, _) =>
        {
            if (!_rb2d.Checked) return;
            _synchro.SpatialMode = SpatialLayoutMode.Ring2D;
            _synchro.Save();
            _spatial.Bind(_synchro);
            _audio?.ApplySpatial(_synchro);
            PushLocalPoseToHandshake();
        };

        _rb3d.Text = "3D sphere";
        _rb3d.AutoSize = true;
        _rb3d.Location = new Point(390, 84);
        _rb3d.CheckedChanged += (_, _) =>
        {
            if (!_rb3d.Checked) return;
            _synchro.SpatialMode = SpatialLayoutMode.Sphere3D;
            _synchro.Save();
            _spatial.Bind(_synchro);
            _audio?.ApplySpatial(_synchro);
            PushLocalPoseToHandshake();
        };

        _btnHandshake.Text = "Re-probe peers";
        _btnHandshake.Location = new Point(500, 84);
        _btnHandshake.Width = 140;
        _btnHandshake.Enabled = false;
        _btnHandshake.Click += async (_, _) => await ProbeAndRefreshAsync();

        _mwbStatus.AutoSize = true;
        _mwbStatus.Location = new Point(12, 112);
        _swbPeerHint.AutoSize = true;
        _swbPeerHint.Location = new Point(520, 112);

        top.Controls.Add(title);
        top.Controls.Add(_chkSoundSynchro);
        top.Controls.Add(_chkForceSync);
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
            _synchro.Save();
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

    private void ApplySynchroToUi()
    {
        _chkSoundSynchro.Checked = _synchro.Enabled;
        _chkForceSync.Checked = _synchro.ForceSoundSync;
        _chkAttenuate.Checked = _synchro.DistanceAttenuation;
        if (_synchro.SpatialMode == SpatialLayoutMode.SyncOnly) _rbSync.Checked = true;
        else if (_synchro.SpatialMode == SpatialLayoutMode.Sphere3D) _rb3d.Checked = true;
        else _rb2d.Checked = true;
        _spatial.Bind(_synchro);
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
        _synchro.Enabled = _chkSoundSynchro.Checked;
        _synchro.Save();
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
            MessageBox.Show(
                "MWB SecurityKey missing. Configure Garage MWB SecurityKey first. SWB does not require the MWB machine list.",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        try
        {
            await TeardownAsync();
            _matrix.Items.Clear();
            _swbPeers.Clear();
            _mwbOnlyPeers.Clear();
            _liveSameKeyPeers.Clear();
            _tooManySameKeyPrompted = false;
            EnsureLocalMatrixRow();

            _audio = new AudioMatrixService();
            _audio.Log += AppendLog;
            _handshake = new SwbHandshakeService(_mwb)
            {
                LocalRole = SwbPeerRole.Both,
                SampleRate = 48000,
                Channels = 2,
            };
            PushLocalPoseToHandshake();
            _handshake.Log += AppendLog;
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
                    _synchro.Save();

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
                    _synchro.Save();
                    UpsertMatrixRow(peer);
                    TryFillMwbMatrixOnce(peer.HostName);
                    UpdateGrayOverlay();
                });
            };

            await _handshake.StartAsync();
            Diag("handshake.StartAsync OK (TCP 15200)");
            try
            {
                await _audio.StartAsync(
                    SwbHandshakeService.DefaultAudioPort,
                    sampleRate: 48000,
                    sendLocalLoopback: _chkSend.Checked,
                    receiveAndMix: _chkRecv.Checked);
                Diag("audio.StartAsync OK");
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
            Diag("Sound Synchro enabled (SWB window).");
            UpdateGrayOverlay();
        }
        catch (Exception ex)
        {
            _chkSoundSynchro.Checked = false;
            _synchro.Enabled = false;
            _synchro.Save();
            Diag("Enable failed (MWB unaffected): " + ex.Message);
            MessageBox.Show(ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
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

        if (_synchro.ForceSoundSync && _forceSync != null && _audio != null)
        {
            var hosts = _swbPeers.ToList();
            if (hosts.Count > 0)
            {
                var ok = await _forceSync.CalibrateAsync(hosts, SwbHandshakeService.DefaultAudioPort);
                if (!ok)
                    AppendLog("ForceSync calibration degraded — SWB matrix still usable.");
                _audio.SetForceSyncDelayProvider(host => _forceSync.GetPlaybackDelayMs(host));
            }
            else
            {
                _audio.SetForceSyncDelayProvider(null);
            }
        }
        else
        {
            _audio?.SetForceSyncDelayProvider(null);
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
                AppendLog(detail);
                if (!_tooManySameKeyPrompted)
                {
                    _tooManySameKeyPrompted = true;
                    MessageBox.Show(
                        detail + Environment.NewLine + Environment.NewLine + "MWB allows at most " + GarageMwbSettings.MaxMachines +
                        " machines (including this PC). Extra same-key devices were not written into the matrix.",
                        "SWB auto-fill",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                }
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
    }
}
