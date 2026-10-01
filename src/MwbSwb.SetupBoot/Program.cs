using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;

namespace MwbSwb.SetupBoot;

/// <summary>
/// Double-click installer: extracts embedded payload.zip and runs Install.cmd.
/// </summary>
static class Program
{
    private const string PayloadResourceName = "payload.zip";

    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();

        using var ui = new StatusForm();
        ui.Show();
        ui.SetStatus("Preparing install...");
        Application.DoEvents();

        string? work = null;
        string statusPath = Path.Combine(Path.GetTempPath(), "mwb-swb-setup-status.txt");
        try
        {
            try { File.Delete(statusPath); } catch { /* ignore */ }

            work = Path.Combine(Path.GetTempPath(), "MWB-SWB-Setup-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);

            ui.SetStatus("Unpacking install files...");
            Application.DoEvents();
            ExtractEmbeddedPayload(work);

            string installCmd = Path.Combine(work, "Install.cmd");
            if (!File.Exists(installCmd))
                throw new FileNotFoundException("Install.cmd missing inside payload.", installCmd);

            ui.SetStatus("Installing...");
            Application.DoEvents();

            var psi = new ProcessStartInfo
            {
                FileName = installCmd,
                WorkingDirectory = work,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start Install.cmd.");

            while (!proc.HasExited)
            {
                try
                {
                    if (File.Exists(statusPath))
                    {
                        string line = File.ReadAllText(statusPath).Trim();
                        if (!string.IsNullOrWhiteSpace(line))
                            ui.SetStatus(line);
                    }
                }
                catch { /* status file may be mid-write */ }

                Application.DoEvents();
                Thread.Sleep(400);
            }

            if (proc.ExitCode != 0)
            {
                ui.SetStatus($"Install finished with exit code {proc.ExitCode}.");
                MessageBox.Show(
                    $"Setup finished with exit code {proc.ExitCode}.\n\n" +
                    "See %TEMP%\\mwb-swb-setup.log",
                    "MWB+SWB Setup",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            ui.SetStatus("Install complete. SWB Host should be running.");
            Application.DoEvents();
            Thread.Sleep(800);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "MWB+SWB Setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            if (work is not null)
            {
                try { Directory.Delete(work, recursive: true); } catch { /* best-effort */ }
            }
            ui.Close();
        }
    }

    private static void ExtractEmbeddedPayload(string destDir)
    {
        var asm = Assembly.GetExecutingAssembly();
        string? resName = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(PayloadResourceName, StringComparison.OrdinalIgnoreCase));
        if (resName is null)
            throw new InvalidOperationException(
                "Embedded payload.zip missing. Rebuild with build-payload.ps1.");

        using Stream? src = asm.GetManifestResourceStream(resName)
            ?? throw new InvalidOperationException("Cannot open embedded payload.zip.");
        using var zip = new ZipArchive(src, ZipArchiveMode.Read, leaveOpen: false);
        string destRoot = Path.GetFullPath(destDir);
        foreach (var entry in zip.Entries)
        {
            string target = Path.GetFullPath(Path.Combine(destRoot, entry.FullName));
            if (!target.StartsWith(destRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(target, destRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Zip entry escapes destination: " + entry.FullName);

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }
    }
}

sealed class StatusForm : Form
{
    private readonly Label _label;

    public StatusForm()
    {
        Text = "MWB+SWB Setup";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(480, 100);
        TopMost = true;

        _label = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI", 10.5f),
            Text = "Starting...",
            Padding = new Padding(12),
        };
        Controls.Add(_label);
    }

    public void SetStatus(string text) => _label.Text = text;
}