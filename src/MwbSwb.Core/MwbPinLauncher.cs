using System.Diagnostics;

namespace MwbSwb.Core;

/// <summary>Starts/stops the pinned MWB binary only.</summary>
public sealed class MwbPinLauncher : IDisposable
{
    private Process? _process;

    public string? ExePath { get; private set; }
    public bool IsRunning => _process is { HasExited: false };
    public event Action<string>? Log;

    public static string? ResolvePinnedExe(string? installRoot = null)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(installRoot))
        {
            candidates.Add(Path.Combine(installRoot, "mwb-pin", UpstreamPin.MwbExeName));
            candidates.Add(Path.Combine(installRoot, UpstreamPin.MwbExeName));
        }

        var baseDir = AppContext.BaseDirectory;
        candidates.Add(Path.Combine(baseDir, "mwb-pin", UpstreamPin.MwbExeName));
        candidates.Add(Path.Combine(baseDir, UpstreamPin.MwbExeName));
        candidates.Add(Path.Combine(@"H:\mwb+swb\vendor\mwb-pin", UpstreamPin.MwbExeName));

        return candidates.FirstOrDefault(File.Exists);
    }

    public bool TryStart(string? installRoot = null)
    {
        if (IsRunning) return true;
        ExePath = ResolvePinnedExe(installRoot);
        if (ExePath is null)
        {
            Log?.Invoke("Pinned MWB binary not found. Run the overwrite installer (Path A/B) first.");
            return false;
        }

        var psi = new ProcessStartInfo
        {
            FileName = ExePath,
            WorkingDirectory = Path.GetDirectoryName(ExePath)!,
            UseShellExecute = false,
        };
        _process = Process.Start(psi);
        Log?.Invoke($"Started pinned MWB: {ExePath} ({UpstreamPin.Describe()})");
        return _process != null;
    }

    public void Stop()
    {
        try
        {
            if (_process is { HasExited: false })
            {
                _process.CloseMainWindow();
                if (!_process.WaitForExit(3000))
                    _process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex)
        {
            Log?.Invoke("Stop MWB: " + ex.Message);
        }
        finally
        {
            _process?.Dispose();
            _process = null;
        }
    }

    public void Dispose() => Stop();
}
