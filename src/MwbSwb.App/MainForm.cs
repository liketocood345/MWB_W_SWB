using MwbSwb.Audio;
using MwbSwb.Core;

namespace MwbSwb.App;

public partial class MainForm : Form
{
    private readonly CheckBox _chkSoundSynchro = new();
    private readonly Button _btnRefreshMwb = new();
    private readonly Button _btnHandshake = new();
    private readonly ListView _matrix = new();
    private readonly TextBox _log = new();
    private readonly Label _mwbStatus = new();
    private readonly CheckBox _chkSend = new();
    private readonly CheckBox _chkRecv = new();

    private MwbSettings _mwb = MwbSettings.LoadOrEmpty();
    private SwbHandshakeService? _handshake;
    private AudioMatrixService? _audio;

    public MainForm()
    {
        Text = "MWB+SWB Sound Synchro (prototype App; prefer MwbSwb.Host)";
        Width = 780;
        Height = 560;
        MinimumSize = new Size(640, 480);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9f);

        BuildUi();
        RefreshMwbStatus();
        FormClosing += async (_, e) =>
        {
            await TeardownAsync();
        };
    }

    private void BuildUi()
    {
        var top = new Panel
        {
            Dock = DockStyle.Top,
            Height = 108,
            Padding = new Padding(12),
        };

        _chkSoundSynchro.Text = "Sound Synchro (optional SWB; MWB-only if peers lack SWB)";
        _chkSoundSynchro.AutoSize = true;
        _chkSoundSynchro.Location = new Point(12, 12);
        _chkSoundSynchro.CheckedChanged += async (_, _) => await OnSoundSynchroChangedAsync();

        _chkSend.Text = "Send local loopback -> peers";
        _chkSend.AutoSize = true;
        _chkSend.Checked = true;
        _chkSend.Location = new Point(12, 42);

        _chkRecv.Text = "Receive peers -> local mix";
        _chkRecv.AutoSize = true;
        _chkRecv.Checked = true;
        _chkRecv.Location = new Point(220, 42);

        _btnRefreshMwb.Text = "Refresh MWB settings";
        _btnRefreshMwb.Location = new Point(12, 70);
        _btnRefreshMwb.Width = 120;
        _btnRefreshMwb.Click += (_, _) => RefreshMwbStatus();

        _btnHandshake.Text = "Probe peers";
        _btnHandshake.Location = new Point(140, 70);
        _btnHandshake.Width = 100;
        _btnHandshake.Enabled = false;
        _btnHandshake.Click += async (_, _) =>
        {
            if (_handshake != null)
                await _handshake.ConnectPeersAsync();
        };

        _mwbStatus.AutoSize = true;
        _mwbStatus.Location = new Point(260, 74);
        _mwbStatus.ForeColor = Color.DimGray;

        top.Controls.Add(_chkSoundSynchro);
        top.Controls.Add(_chkSend);
        top.Controls.Add(_chkRecv);
        top.Controls.Add(_btnRefreshMwb);
        top.Controls.Add(_btnHandshake);
        top.Controls.Add(_mwbStatus);

        _matrix.Dock = DockStyle.Top;
        _matrix.Height = 180;
        _matrix.View = View.Details;
        _matrix.FullRowSelect = true;
        _matrix.CheckBoxes = true;
        _matrix.Columns.Add("Host", 80);
        _matrix.Columns.Add("Host", 140);
        _matrix.Columns.Add("IP", 140);
        _matrix.Columns.Add("Audio port", 80);
        _matrix.Columns.Add("Stereo", 70);
        _matrix.Columns.Add("Status", 120);
        _matrix.ItemChecked += (_, e) =>
        {
            if (_audio == null || e.Item == null) return;
            var host = e.Item.SubItems[1].Text;
            _audio.SetInclude(host, e.Item.Checked);
        };

        var matrixLabel = new Label
        {
            Text = "Sounding-device matrix (remote PCs)",
            Dock = DockStyle.Top,
            Height = 24,
            Padding = new Padding(12, 6, 0, 0),
        };

        _log.Dock = DockStyle.Fill;
        _log.Multiline = true;
        _log.ScrollBars = ScrollBars.Vertical;
        _log.ReadOnly = true;
        _log.Font = new Font("Consolas", 9f);
        _log.BackColor = Color.FromArgb(30, 30, 30);
        _log.ForeColor = Color.Gainsboro;

        var logLabel = new Label
        {
            Text = "Log",
            Dock = DockStyle.Top,
            Height = 24,
            Padding = new Padding(12, 6, 0, 0),
        };

        Controls.Add(_log);
        Controls.Add(logLabel);
        Controls.Add(_matrix);
        Controls.Add(matrixLabel);
        Controls.Add(top);
    }

    private void RefreshMwbStatus()
    {
        _mwb = MwbSettings.LoadOrEmpty();
        var peers = string.Join(", ", _mwb.MachineMatrix);
        if (string.IsNullOrEmpty(peers)) peers = "(none)";
        var keyOk = string.IsNullOrWhiteSpace(_mwb.SecurityKey) ? "no SecurityKey" : "SecurityKey present";
        _mwbStatus.Text = $"host={_mwb.LocalHostName} | {keyOk} | peers=[{peers}]";
        AppendLog($"MWB settings: {_mwb.SettingsPath}");
        AppendLog(_mwbStatus.Text);
    }

    private async Task OnSoundSynchroChangedAsync()
    {
        if (_chkSoundSynchro.Checked)
            await EnableSoundSynchroAsync();
        else
            await TeardownAsync();
    }

    private async Task EnableSoundSynchroAsync()
    {
        RefreshMwbStatus();
        if (!_mwb.IsReadyForSwb)
        {
            _chkSoundSynchro.Checked = false;
            MessageBox.Show(
                "MWB SecurityKey missing. SWB only needs the shared SecurityKey (machine list optional).",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        try
        {
            await TeardownAsync();
            _audio = new AudioMatrixService();
            _audio.Log += AppendLog;
            _handshake = new SwbHandshakeService(_mwb)
            {
                LocalRole = SwbPeerRole.Both,
                SampleRate = 48000,
                Channels = 2,
            };
            _handshake.Log += AppendLog;
            _handshake.PeerConfirmed += peer =>
            {
                BeginInvoke(() =>
                {
                    _audio?.UpsertPeer(peer, includeInMatrix: true);
                    UpsertMatrixRow(peer);
                });
            };

            await _handshake.StartAsync();
            await _audio.StartAsync(
                SwbHandshakeService.DefaultAudioPort,
                sampleRate: 48000,
                sendLocalLoopback: _chkSend.Checked,
                receiveAndMix: _chkRecv.Checked);

            _btnHandshake.Enabled = true;
            await _handshake.ConnectPeersAsync();
            AppendLog("Sound Synchro enabled.");
        }
        catch (Exception ex)
        {
            _chkSoundSynchro.Checked = false;
            AppendLog("Enable failed: " + ex.Message);
            MessageBox.Show(ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            await TeardownAsync();
        }
    }

    private void UpsertMatrixRow(SwbPeerInfo peer)
    {
        foreach (ListViewItem existing in _matrix.Items)
        {
            if (existing.SubItems[1].Text.Equals(peer.HostName, StringComparison.OrdinalIgnoreCase))
            {
                existing.Checked = true;
                existing.SubItems[2].Text = peer.IpAddress;
                existing.SubItems[3].Text = peer.AudioPort.ToString();
                existing.SubItems[4].Text = peer.StereoOk ? "OK" : "NO";
                existing.SubItems[5].Text = "updated";
                return;
            }
        }

        var item = new ListViewItem("");
        item.Checked = true;
        item.SubItems.Add(peer.HostName);
        item.SubItems.Add(peer.IpAddress);
        item.SubItems.Add(peer.AudioPort.ToString());
        item.SubItems.Add(peer.StereoOk ? "OK" : "NO");
        item.SubItems.Add("ready");
        _matrix.Items.Add(item);
    }

    private async Task TeardownAsync()
    {
        _btnHandshake.Enabled = false;
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
