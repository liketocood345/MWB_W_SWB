using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using MwbSwb.Core;

namespace MwbSwb.Audio;

/// <summary>
/// LAN RTT calibration for sync-leaning tiers (0-2).
/// Slowest path is baseline; earlier peers get extra jitter watermark (not Task.Delay).
/// </summary>
public sealed class ForceSyncCalibrator
{
    public const int ProbePortOffset = 2;

    public int ToleranceMs { get; set; } = 30;
    public Dictionary<string, double> RttMsByHost { get; } = new(StringComparer.OrdinalIgnoreCase);
    public double MaxRttMs { get; private set; }
    public bool LastCalibrationOk { get; private set; }
    public string? LastError { get; set; }
    public event Action<string>? Log;

    public async Task<bool> CalibrateAsync(IEnumerable<string> peerHosts, int baseUdpPort, CancellationToken ct = default)
    {
        RttMsByHost.Clear();
        MaxRttMs = 0;
        LastCalibrationOk = false;
        LastError = null;
        var port = baseUdpPort + ProbePortOffset;
        try
        {
            foreach (var host in peerHosts.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                ct.ThrowIfCancellationRequested();
                var rtt = await MeasureRttAsync(host, port, ct).ConfigureAwait(false);
                if (rtt < 0)
                {
                    Log?.Invoke($"ForceSync probe failed for {host}; continuing.");
                    continue;
                }
                RttMsByHost[host] = rtt;
                if (rtt > MaxRttMs) MaxRttMs = rtt;
                Log?.Invoke($"ForceSync RTT {host}: {rtt:F1} ms");
            }

            if (RttMsByHost.Count == 0)
            {
                LastError = "No peer answered ForceSync probes.";
                Log?.Invoke(LastError + " No Δ — sticky local watermark hold only (no Task.Delay).");
                return false;
            }

            LastCalibrationOk = true;
            Log?.Invoke($"ForceSync baseline (slowest) = {MaxRttMs:F1} ms; tolerance +/-{ToleranceMs} ms (sticky hold, not Task.Delay).");
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Log?.Invoke("ForceSync calibration error: " + ex.Message);
            return false;
        }
    }

    /// <summary>Extra jitter watermark ms so this peer aligns to slowest path. Deadband = ToleranceMs.</summary>
    public int GetWatermarkOffsetMs(string? remoteHost = null)
    {
        if (!LastCalibrationOk || MaxRttMs <= 0) return 0;
        double myPath = 0;
        if (!string.IsNullOrEmpty(remoteHost) && RttMsByHost.TryGetValue(remoteHost, out var r))
            myPath = r;
        var delta = Math.Max(0, MaxRttMs - myPath);
        if (delta < ToleranceMs) return 0;
        return (int)Math.Round(delta);
    }

    /// <summary>Legacy name — same as watermark offset (no longer used as Task.Delay).</summary>
    public int GetPlaybackDelayMs(string? remoteHost = null) => GetWatermarkOffsetMs(remoteHost);

    private static async Task<double> MeasureRttAsync(string host, int port, CancellationToken ct)
    {
        using var udp = new UdpClient();
        udp.Client.ReceiveTimeout = 800;
        udp.Client.SendTimeout = 800;
        var payload = Encoding.UTF8.GetBytes("SWB_SYNC_PING");
        var sw = Stopwatch.StartNew();
        try
        {
            await udp.SendAsync(payload, payload.Length, host, port).WaitAsync(ct).ConfigureAwait(false);
            var result = await udp.ReceiveAsync(ct).ConfigureAwait(false);
            sw.Stop();
            var text = Encoding.UTF8.GetString(result.Buffer);
            if (!text.StartsWith("SWB_SYNC_PONG", StringComparison.Ordinal))
                return -1;
            return sw.Elapsed.TotalMilliseconds;
        }
        catch
        {
            return -1;
        }
    }

    public static async Task RunProbeResponderAsync(int baseUdpPort, CancellationToken ct)
    {
        var port = baseUdpPort + ProbePortOffset;
        using var udp = new UdpClient(port);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await udp.ReceiveAsync(ct).ConfigureAwait(false);
                var text = Encoding.UTF8.GetString(result.Buffer);
                if (!text.StartsWith("SWB_SYNC_PING", StringComparison.Ordinal)) continue;
                var pong = Encoding.UTF8.GetBytes("SWB_SYNC_PONG");
                await udp.SendAsync(pong, pong.Length, result.RemoteEndPoint).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch { /* ignore */ }
        }
    }
}

public static class SpatialPan
{
    public static (float left, float right) Gains(DeviceSpatialPose pose, bool attenuate, SpatialLayoutMode mode = SpatialLayoutMode.Ring2D)
    {
        if (mode == SpatialLayoutMode.SyncOnly)
            return (1f, 1f);

        var az = pose.AzimuthDeg * Math.PI / 180.0;
        var pan = Math.Sin(az);
        var angle = (pan + 1) * 0.25 * Math.PI;
        var left = (float)Math.Cos(angle);
        var right = (float)Math.Sin(angle);
        var elev = pose.ElevationDeg * Math.PI / 180.0;
        var elevFactor = (float)(0.65 + 0.35 * Math.Cos(elev));
        left *= elevFactor;
        right *= elevFactor;
        if (attenuate)
        {
            var r = Math.Clamp(pose.Radius, 0.2, 1.5);
            var att = (float)(1.0 / r);
            left *= att;
            right *= att;
        }
        return (left, right);
    }
}
