using System.Diagnostics;
using MwbSwb.Core;

namespace MwbSwb.Installer;

/// <summary>
/// Single integrated setup for Garage-track MWB+SWB.
/// Packaging a setup ≠ overwriting existing Garage: MSI runs only when requested;
/// Host is always additive under LocalAppData.
/// </summary>
public sealed class InstallerForm : Form
{
    private readonly Label _detect = new();
    private readonly CheckBox _chkGarageMsi = new();
    private readonly CheckBox _chkHost = new();
    private readonly CheckBox _chkShortcut = new();
    private readonly TextBox _log = new();
    private readonly Button _btnInstall = new();
    private readonly Button _btnUninstallHost = new();

    private readonly string _hostRoot =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MWB-SWB-Host");

    public InstallerForm()
    {
        Text = "MWB+SWB Setup (Garage)";
        Width = 740;
        Height = 560;
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9f);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;

        var notice = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Top,
            Height = 100,
            Padding = new Padding(12),
            Text =
                "Single installer for Microsoft Garage Mouse without Borders + Sound Synchro Host.\n" +
                "This is NOT PowerToys MWB. Existing Garage installs are left alone unless you tick Garage MSI.\n" +
                "Host is additive (%LOCALAPPDATA%\\MWB-SWB-Host) and does not replace Garage binaries.",
        };

        _detect.Dock = DockStyle.Top;
        _detect.Height = 48;
        _detect.Padding = new Padding(12, 0, 12, 0);
        _detect.ForeColor = Color.DimGray;

        _chkGarageMsi.AutoSize = true;
        _chkGarageMsi.Location = new Point(16, 160);
        _chkGarageMsi.Text = "Install Garage Mouse without Borders (official MSI)";

        _chkHost.AutoSize = true;
        _chkHost.Location = new Point(16, 190);
        _chkHost.Checked = true;
        _chkHost.Text = "Install Sound Synchro Host (additive; does not overwrite Garage)";

        _chkShortcut.AutoSize = true;
        _chkShortcut.Location = new Point(16, 220);
        _chkShortcut.Checked = true;
        _chkShortcut.Text = "Create Start Menu shortcut for Host";

        _btnInstall.Text = "Install";
        _btnInstall.Location = new Point(16, 260);
        _btnInstall.Width = 140;
        _btnInstall.Click += (_, _) => RunInstall();

        _btnUninstallHost.Text = "Remove Host only";
        _btnUninstallHost.Location = new Point(170, 260);
        _btnUninstallHost.Width = 150;
        _btnUninstallHost.Click += (_, _) => RemoveHostOnly();

        _log.Multiline = true;
        _log.ScrollBars = ScrollBars.Vertical;
        _log.ReadOnly = true;
        _log.Font = new Font("Consolas", 9f);
        _log.Location = new Point(16, 300);
        _log.Size = new Size(690, 200);

        Controls.Add(notice);
        Controls.Add(_detect);
        Controls.Add(_chkGarageMsi);
        Controls.Add(_chkHost);
        Controls.Add(_chkShortcut);
        Controls.Add(_btnInstall);
        Controls.Add(_btnUninstallHost);
        Controls.Add(_log);

        Shown += (_, _) => RefreshDetect();
    }

    private void RefreshDetect()
    {
        var garage = GarageMwbSettings.FindInstalledExe();
        if (garage is null)
        {
            _detect.Text = "Detection: Garage MWB not found → Garage MSI recommended.";
            _chkGarageMsi.Checked = true;
            _chkGarageMsi.Text = "Install Garage Mouse without Borders (official MSI) — recommended";
        }
        else
        {
            string ver = "?";
            try { ver = FileVersionInfo.GetVersionInfo(garage).FileVersion ?? "?"; } catch { /* ignore */ }
            _detect.Text = "Detection: Garage MWB present (" + ver + ") at " + garage +
                           " — left untouched unless you tick Garage MSI.";
            _chkGarageMsi.Checked = false;
            _chkGarageMsi.Text = "Reinstall / repair Garage MSI (optional; overwrites Garage program files only if checked)";
        }

        Log("Payload Garage MSI: " + (ResolveGarageMsi() is null ? "MISSING" : "OK"));
        Log("Payload Host: " + (ResolveHostPayload() is null ? "MISSING" : "OK"));
    }

    private void RunInstall()
    {
        try
        {
            if (!_chkGarageMsi.Checked && !_chkHost.Checked)
            {
                MessageBox.Show("Select at least Garage MSI and/or Host.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (_chkGarageMsi.Checked)
                InstallGarageMsi();

            if (_chkHost.Checked)
                InstallHost();

            Log("Done.");
            MessageBox.Show(
                "Install finished.\n\n" +
                "1) Use Garage Mouse without Borders for keyboard/mouse.\n" +
                "2) Run MWB+SWB Host for optional Sound Synchro.\n" +
                "Existing Garage was not replaced unless you ticked Garage MSI.",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            Log("ERROR: " + ex.Message);
            MessageBox.Show(ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void InstallGarageMsi()
    {
        var msi = ResolveGarageMsi() ?? throw new FileNotFoundException(
            "Garage MSI not found beside Setup. Expected Garage\\MouseWithoutBordersSetup.msi");
        var before = GarageKeyPreserve.Capture();
        Log(GarageKeyPreserve.HasUsableKey(before)
            ? "Preserving Garage SecurityKey / MachineMatrix across MSI..."
            : "No prior Garage SecurityKey in HKCU (fresh identity after MSI is OK).");
        Log("Running msiexec: " + msi);
        var psi = new ProcessStartInfo
        {
            FileName = "msiexec.exe",
            Arguments = "/i \"" + msi + "\" /qn /norestart",
            UseShellExecute = false,
        };
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start msiexec.");
        proc.WaitForExit();
        if (proc.ExitCode is not 0 and not 3010)
            throw new InvalidOperationException("msiexec exit " + proc.ExitCode);
        GarageKeyPreserve.RestoreIfMissing(before);
        var after = GarageKeyPreserve.Capture();
        Log("Garage MSI completed (exit " + proc.ExitCode + "). Key retained=" +
            GarageKeyPreserve.HasUsableKey(after));
    }

    private void InstallHost()
    {
        var src = ResolveHostPayload() ?? throw new DirectoryNotFoundException(
            "Host payload not found beside Setup. Expected Host\\MwbSwb.Host.exe");
        foreach (var proc in System.Diagnostics.Process.GetProcessesByName("MwbSwb.Host"))
        {
            try { proc.Kill(entireProcessTree: true); proc.WaitForExit(5000); } catch { }
            finally { proc.Dispose(); }
        }
        Directory.CreateDirectory(_hostRoot);
        CopyDirectory(src, _hostRoot);
        try {
            System.IO.File.WriteAllText(System.IO.Path.Combine(_hostRoot, "INSTALL_STAMP.txt"),
                "overwrite=" + System.DateTime.UtcNow.ToString("o") + "\n");
        } catch { }
        Log("Host deployed → " + _hostRoot);

        new InstallState
        {
            InstallRoot = _hostRoot,
            ReplacesMwb = false,
            BlocksPlatformMwbUpdate = false,
            UpstreamPinTag = "Garage-standalone",
            UpstreamPinCommit = "additive-host",
            InstallPathMode = "GarageAdditive",
            InstalledUtc = DateTimeOffset.UtcNow,
            ConsentVersion = "garage-1",
        }.Save();

        if (_chkShortcut.Checked)
            CreateStartMenuShortcut(Path.Combine(_hostRoot, "MwbSwb.Host.exe"));
    }

    private void RemoveHostOnly()
    {
        try
        {
            var lnk = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                "Programs", "MWB-SWB Host.lnk");
            if (File.Exists(lnk)) File.Delete(lnk);

            if (Directory.Exists(_hostRoot))
            {
                Directory.Delete(_hostRoot, true);
                Log("Removed Host: " + _hostRoot);
            }
            else
                Log("Host folder not present.");

            MessageBox.Show(
                "Host removed. Garage Mouse without Borders was not touched.",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            Log("ERROR: " + ex.Message);
            MessageBox.Show(ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static string? ResolveGarageMsi()
    {
        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDir, "Garage", "MouseWithoutBordersSetup.msi"),
            Path.Combine(baseDir, "MouseWithoutBordersSetup.msi"),
            Path.Combine(baseDir, "..", "Garage", "MouseWithoutBordersSetup.msi"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? ResolveHostPayload()
    {
        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDir, "Host"),
            Path.Combine(baseDir, "..", "Host"),
        };
        return candidates.FirstOrDefault(d => File.Exists(Path.Combine(d, "MwbSwb.Host.exe")));
    }

    private static void CopyDirectory(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var file in Directory.GetFiles(src))
        {
            if (file.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)) continue;
            File.Copy(file, Path.Combine(dst, Path.GetFileName(file)), overwrite: true);
        }
        foreach (var dir in Directory.GetDirectories(src))
        {
            var name = Path.GetFileName(dir);
            if (name.Equals("mwb-pin", StringComparison.OrdinalIgnoreCase)) continue; // never ship PT pin
            CopyDirectory(dir, Path.Combine(dst, name));
        }
    }

    private void CreateStartMenuShortcut(string targetExe)
    {
        var programs = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs");
        Directory.CreateDirectory(programs);
        var lnkPath = Path.Combine(programs, "MWB-SWB Host.lnk");
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType is null)
        {
            Log("WScript.Shell unavailable; skip shortcut.");
            return;
        }
        var shell = Activator.CreateInstance(shellType);
        if (shell is null) return;
        var create = shellType.GetMethod("CreateShortcut");
        var lnk = create?.Invoke(shell, new object[] { lnkPath });
        if (lnk is null) return;
        var lnkType = lnk.GetType();
        lnkType.GetProperty("TargetPath")?.SetValue(lnk, targetExe);
        lnkType.GetProperty("WorkingDirectory")?.SetValue(lnk, Path.GetDirectoryName(targetExe)!);
        lnkType.GetProperty("Description")?.SetValue(lnk, "MWB+SWB Sound Synchro Host (Garage additive)");
        lnkType.GetMethod("Save")?.Invoke(lnk, null);
        Log("Shortcut: " + lnkPath);
    }

    private void Log(string line)
    {
        if (_log.IsDisposed) return;
        _log.AppendText("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + line + Environment.NewLine);
    }
}
