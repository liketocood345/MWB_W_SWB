using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace MwbSwb.MwbStub
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            try
            {
                var garageDir = Path.GetDirectoryName(Application.ExecutablePath)
                    ?? @"C:\Program Files (x86)\Microsoft Garage\Mouse without Borders";
                var original = Path.Combine(garageDir, "MouseWithoutBorders.original.exe");
                if (!File.Exists(original))
                {
                    MessageBox.Show(
                        "MouseWithoutBorders.original.exe missing.\nRe-run MWB-SWB-Setup.exe.",
                        "MWB+SWB",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                    return;
                }

                if (Process.GetProcessesByName("MouseWithoutBorders.original").Length == 0)
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = original,
                        WorkingDirectory = garageDir,
                        UseShellExecute = true,
                    });
                }

                var deadline = DateTime.UtcNow.AddSeconds(12);
                while (Process.GetProcessesByName("MouseWithoutBorders.original").Length == 0
                       && DateTime.UtcNow < deadline)
                {
                    Thread.Sleep(200);
                }

                if (Process.GetProcessesByName("MouseWithoutBorders.original").Length == 0
                    && Process.GetProcessesByName("MouseWithoutBordersHelper").Length == 0)
                {
                    MessageBox.Show(
                        "Garage Mouse without Borders failed to start.",
                        "MWB+SWB",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                    return;
                }

                Thread.Sleep(400);

                var host = ResolveHost();
                if (host == null)
                {
                    MessageBox.Show(
                        "MwbSwb.Host.exe not found.\nRe-run MWB-SWB-Setup.exe.",
                        "MWB+SWB",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                    return;
                }

                Process.Start(new ProcessStartInfo
                {
                    FileName = host,
                    Arguments = "/open-swb",
                    WorkingDirectory = Path.GetDirectoryName(host) ?? "",
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "MWB+SWB", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        static string ResolveHost()
        {
            var local = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MWB-SWB-Host", "MwbSwb.Host.exe");
            if (File.Exists(local)) return local;
            var pub = @"C:\Users\Public\MWB-SWB-Host\MwbSwb.Host.exe";
            if (File.Exists(pub)) return pub;
            return null;
        }
    }
}