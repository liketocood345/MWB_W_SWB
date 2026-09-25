using System.Linq;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using MwbSwb.Core;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MwbSwb.Audio;

public sealed class AudioMatrixService : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, RemoteEndpoint> _remotes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _ipToHost = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _cts;
    private UdpClient? _udp;
    private WasapiLoopbackCapture? _capture;
    private ProcessExcludeLoopbackCapture? _excludeCapture;
    private WasapiOut? _output;
    private PlaybackFanOut? _fanOut;
    private string _playbackDeviceId = LocalAudioDeviceInfo.AllDevicesId;
    private MatrixSynth? _synth;
    private PlaybackReferenceRing? _refRing;
    private EchoCanceller? _aec;
    private int _sampleRate = 48000;
    private int _tier = AudioTier.Default;
    private bool _rate24k;
    private bool _sendEnabled;
    private bool _recvEnabled = true;
    private SoundSynchroSettings? _spatial;
    private Func<string?, int>? _forceSyncOffsetMs;
    private double _syncAlignStrength;
    private readonly object _peakLock = new();
    private float _txPeak;
    private float _rxPeak;
    private float _rxHold;
    private bool _matrixRunning;
    private ushort _txSeq;
    private readonly float[] _captureFloat = new float[48000];
    private readonly short[] _pcmScratch = new short[48000];
    private readonly float[] _rxFloat = new float[48000];
    private DateTime _lastTierLog = DateTime.MinValue;
    private readonly ConcurrentDictionary<string, long> _logNotBefore = new(StringComparer.OrdinalIgnoreCase);
    private int _audioPort;
    private int _lastUnderrunSnapshot;
    private Task? _underrunWatch;

    public event Action<string>? Log;

    private void Emit(string msg)
    {
        Log?.Invoke(msg);
        if (msg.StartsWith("loopback=", StringComparison.OrdinalIgnoreCase)
            || msg.StartsWith("Audio matrix up", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                File.AppendAllText(@"C:\Users\Public\swb-enable.log",
                    "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + msg + Environment.NewLine);
            }
            catch { /* ignore */ }
        }
    }
    public IReadOnlyDictionary<string, RemoteEndpoint> Remotes => _remotes;
    public bool IsRunning => _matrixRunning;
    public int CurrentTier => _tier;

    public (float Tx, float Rx) SnapshotAndDecay(float decay = 0.55f)
    {
        lock (_peakLock)
        {
            var tx = _sendEnabled ? _txPeak : 0f;
            var rx = _recvEnabled ? _rxPeak : 0f;
            _txPeak *= decay;
            _rxPeak *= decay;
            _rxHold *= Math.Max(decay, 0.82f);
            if (_txPeak < 0.001f) _txPeak = 0;
            if (_rxPeak < 0.001f) _rxPeak = 0;
            if (_rxHold < 0.001f) _rxHold = 0;
            return (tx, rx);
        }
    }

    private void NotePeak(ref float store, float peak)
    {
        if (peak <= 0) return;
        if (peak > 1f) peak = 1f;
        lock (_peakLock)
        {
            if (peak > store) store = peak;
        }
    }

    private void NoteRxPeak(float peak)
    {
        if (!_recvEnabled || peak < 0.02f) return;
        NotePeak(ref _rxPeak, peak);
        lock (_peakLock)
        {
            if (peak > _rxHold) _rxHold = peak;
        }
    }

    private void NoteTxPeak(float peak)
    {
        if (!_sendEnabled || peak < 0.02f) return;
        NotePeak(ref _txPeak, peak);
    }

    public sealed record RemoteEndpoint(string HostName, string IpAddress, int AudioPort, bool IncludeInMatrix, bool StereoOk);

    public void UpsertPeer(SwbPeerInfo peer, bool includeInMatrix = true)
    {
        _remotes[peer.HostName] = new RemoteEndpoint(peer.HostName, peer.IpAddress, peer.AudioPort, includeInMatrix, peer.StereoOk);
        if (!string.IsNullOrWhiteSpace(peer.IpAddress))
            _ipToHost[peer.IpAddress] = peer.HostName;
        _synth?.EnsurePeer(peer.HostName, _tier, EffectiveSampleRate());
        Log?.Invoke($"Matrix peer: {peer.HostName} @ {peer.IpAddress}:{peer.AudioPort} stereo={peer.StereoOk}");
    }

    public void SetInclude(string hostName, bool include)
    {
        if (_remotes.TryGetValue(hostName, out var r))
            _remotes[hostName] = r with { IncludeInMatrix = include };
    }

    /// <summary>Force-sync watermark offset provider (ms). Applied with tier align strength; not Task.Delay.</summary>
    public void SetForceSyncOffsetProvider(Func<string?, int>? provider) => _forceSyncOffsetMs = provider;

    [Obsolete("Use SetForceSyncOffsetProvider")]
    public void SetForceSyncDelayProvider(Func<string?, int>? provider) => SetForceSyncOffsetProvider(provider);

    public void ApplySpatial(SoundSynchroSettings settings)
    {
        _spatial = settings;
        var tier = AudioTier.Clamp(settings.AudioTier);
        _tier = tier;
        var p = AudioTier.Get(tier);
        _syncAlignStrength = p.SyncAlignStrength;
        _synth?.ApplySpatial(settings);
        _synth?.ConfigureTier(tier, EffectiveSampleRate());
        ApplySyncOffsets();
        settings.GetOrCreatePose(Environment.MachineName);
        var want = string.IsNullOrWhiteSpace(settings.LocalPlaybackDeviceId)
            ? LocalAudioDeviceInfo.AllDevicesId
            : settings.LocalPlaybackDeviceId.Trim();
        if (!string.Equals(_playbackDeviceId, want, StringComparison.OrdinalIgnoreCase))
        {
            _playbackDeviceId = want;
            if (_recvEnabled && _synth != null)
                TryRestartOutput();
        }
        LogRare($"spatial:{tier}:{settings.SpatialMode}", $"Spatial/tier applied ({settings.SpatialMode}), tier={tier} ({AudioTier.EnglishName(tier)}), atten={settings.DistanceAttenuation}, playDev={(LocalAudioDeviceInfo.IsAllDevices(_playbackDeviceId) ? "all" : "one")}");
    }

    /// <summary>Change local playback target (* = all active render devices, else MMDevice.ID).</summary>
    public void SetPlaybackDeviceId(string? deviceId)
    {
        var want = LocalAudioDeviceInfo.IsAllDevices(deviceId)
            ? LocalAudioDeviceInfo.AllDevicesId
            : (deviceId ?? LocalAudioDeviceInfo.AllDevicesId).Trim();
        if (string.Equals(_playbackDeviceId, want, StringComparison.OrdinalIgnoreCase)) return;
        _playbackDeviceId = want;
        if (_spatial != null)
        {
            _spatial.LocalPlaybackDeviceId = want;
            _spatial.Save();
        }
        if (_recvEnabled && _synth != null)
            TryRestartOutput();
        Log?.Invoke("Playback device -> " + (LocalAudioDeviceInfo.IsAllDevices(want) ? "All devices" : want));
    }

    public void SetAudioTier(int tier)
    {
        _tier = AudioTier.Clamp(tier);
        var p = AudioTier.Get(_tier);
        _syncAlignStrength = p.SyncAlignStrength;
        if (!p.Allow24kFallback) _rate24k = false;
        _synth?.ConfigureTier(_tier, EffectiveSampleRate());
        if (_output != null)
            TryRestartOutput();
        ApplySyncOffsets();
        Log?.Invoke($"Audio tier -> {_tier} ({AudioTier.EnglishName(_tier)}) T={p.FrameMs}ms play~{p.PlayLatencyMs}ms");
    }

    private int EffectiveSampleRate() => _rate24k ? 24000 : _sampleRate;

    private void ApplySyncOffsets()
    {
        if (_synth == null) return;
        if (_syncAlignStrength <= 0 || _forceSyncOffsetMs == null)
        {
            _synth.ClearSyncOffsets();
            return;
        }
        foreach (var r in _remotes.Values)
        {
            var raw = _forceSyncOffsetMs(r.HostName);
            var ms = (int)Math.Round(Math.Max(0, raw) * _syncAlignStrength);
            _synth.SetPeerSyncOffsetMs(r.HostName, ms);
        }
    }

    public async Task StartAsync(int audioPort, int sampleRate = 48000, bool sendLocalLoopback = true, bool receiveAndMix = true)
    {
        await StopAsync().ConfigureAwait(false);
        _sampleRate = sampleRate;
        _sendEnabled = sendLocalLoopback;
        _recvEnabled = receiveAndMix;
        _cts = new CancellationTokenSource();

        _refRing = new PlaybackReferenceRing(sampleRate);
        _aec = new EchoCanceller(_refRing);
        _synth = new MatrixSynth(EffectiveSampleRate(), _refRing);
        if (_spatial != null)
        {
            _synth.ApplySpatial(_spatial);
            _synth.ConfigureTier(_tier, EffectiveSampleRate());
        }
        else
            _synth.ConfigureTier(_tier, EffectiveSampleRate());

        foreach (var r in _remotes.Values)
            _synth.EnsurePeer(r.HostName, _tier, EffectiveSampleRate());

        _synth.FirstAudible += () => LatencyStageLog.MarkFirstAudible();
        LatencyStageLog.BeginSession($"tier={_tier} rate={EffectiveSampleRate()}");
        RxDumpSession.TryArm(EffectiveSampleRate(), 20);

        _audioPort = audioPort;
        _udp = new UdpClient(audioPort);
        _ = Task.Run(() => ReceiveLoopAsync(_cts.Token));

        if (_recvEnabled)
            StartPlayback();

        if (_sendEnabled)
            StartLoopbackCapture();

        _matrixRunning = true;
        _lastUnderrunSnapshot = 0;
        if (AudioTier.Get(_tier).Allow24kFallback)
            _underrunWatch = Task.Run(() => UnderrunWatchAsync(_cts.Token));
        var p = AudioTier.Get(_tier);
        Emit($"Audio matrix up (UDP:{audioPort}, tier={_tier} T={p.FrameMs}ms, send={_sendEnabled}, recv={_recvEnabled})");
    }

    public Task StopAsync()
    {
        RxDumpSession.Stop();
        _matrixRunning = false;
        lock (_peakLock) { _txPeak = 0; _rxPeak = 0; _rxHold = 0; }
        try { _cts?.Cancel(); } catch { /* ignore */ }
        try { _capture?.StopRecording(); } catch { /* ignore */ }
        _capture?.Dispose();
        _capture = null;
        try { _excludeCapture?.StopRecording(); } catch { /* ignore */ }
        _excludeCapture?.Dispose();
        _excludeCapture = null;
        
        StopPlaybackOutputs();
        _synth = null;
        _aec = null;
        _refRing = null;
        try { _udp?.Close(); } catch { /* ignore */ }
        _udp?.Dispose();
        _udp = null;
        _cts?.Dispose();
        _cts = null;
        return Task.CompletedTask;
    }

    private void StopPlaybackOutputs()
    {
        try { _output?.Stop(); } catch { /* ignore */ }
        try { _output?.Dispose(); } catch { /* ignore */ }
        _output = null;
        try { _fanOut?.Dispose(); } catch { /* ignore */ }
        _fanOut = null;
    }

    private void StartPlayback()
    {
        if (_synth == null) return;
        StopPlaybackOutputs();

        if (_spatial != null && !string.IsNullOrWhiteSpace(_spatial.LocalPlaybackDeviceId))
            _playbackDeviceId = _spatial.LocalPlaybackDeviceId.Trim();

        var p = AudioTier.Get(_tier);
        var latency = (p.PlayLatencyMs + p.PlayLatencyMsHi) / 2;
        var endpoints = LocalAudioDeviceInfo.ListActiveRenderEndpoints();
        var all = LocalAudioDeviceInfo.IsAllDevices(_playbackDeviceId);

        if (!all)
        {
            MMDevice? pick = null;
            try
            {
                using var en = new MMDeviceEnumerator();
                pick = en.GetDevice(_playbackDeviceId);
            }
            catch (Exception ex)
            {
                Emit("Playback device id invalid -> default: " + ex.Message);
            }

            if (pick != null)
            {
                try
                {
                    if (_tier >= 5 && !LooksLikeVirtualName(pick.FriendlyName + " " + pick.DeviceFriendlyName))
                    {
                        try
                        {
                            _output = new WasapiOut(pick, AudioClientShareMode.Exclusive, true, latency);
                            _output.Init(_synth);
                            _output.Play();
                            Emit($"WasapiOut Exclusive on '{pick.FriendlyName}' {latency}ms OK");
                            pick = null;
                            return;
                        }
                        catch (Exception ex)
                        {
                            Emit($"WasapiOut Exclusive failed -> Shared: {ex.Message}");
                            try { _output?.Dispose(); } catch { }
                            _output = null;
                        }
                    }
                    _output = new WasapiOut(pick, AudioClientShareMode.Shared, true, latency);
                    _output.Init(_synth);
                    _output.Play();
                    Emit($"WasapiOut Shared on '{pick.FriendlyName}' {latency}ms OK");
                    // Do not Dispose(pick): WasapiOut retains the MMDevice.
                    pick = null;
                    return;
                }
                finally
                {
                    try { pick?.Dispose(); } catch { }
                }
            }
            // fall through to default single-device path
        }

        if (all && endpoints.Count > 1)
        {
            // Primary = default multimedia; slaves = every other active render endpoint.
            MMDevice? primary = null;
            try
            {
                using var en = new MMDeviceEnumerator();
                primary = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                var primaryId = primary.ID;
                var fan = new PlaybackFanOut(_synth);
                _fanOut = fan;
                _output = new WasapiOut(primary, AudioClientShareMode.Shared, true, latency);
                _output.Init(fan);

                foreach (var ep in endpoints)
                {
                    if (string.Equals(ep.Id, primaryId, StringComparison.OrdinalIgnoreCase)) continue;
                    try
                    {
                        var slaveDev = new MMDeviceEnumerator().GetDevice(ep.Id);
                        var buf = new BufferedWaveProvider(WaveFormat.CreateIeeeFloatWaveFormat(EffectiveSampleRate(), 2))
                        {
                            BufferDuration = TimeSpan.FromMilliseconds(200),
                            DiscardOnBufferOverflow = true,
                        };
                        var slaveOut = new WasapiOut(slaveDev, AudioClientShareMode.Shared, true, latency);
                        slaveOut.Init(buf);
                        slaveOut.Play();
                        fan.AddSlave(slaveOut, buf);
                        Emit($"WasapiOut slave Shared on '{ep.FriendlyName}'");
                    }
                    catch (Exception ex)
                    {
                        Emit($"WasapiOut slave '{ep.FriendlyName}' failed: {ex.Message}");
                    }
                }

                _output.Play();
                Emit($"WasapiOut Shared ALL devices primary='{primary.FriendlyName}' slaves={endpoints.Count - 1} {latency}ms OK");
                primary = null; // owned by WasapiOut
                return;
            }
            catch (Exception ex)
            {
                Emit("All-devices playback failed -> single default: " + ex.Message);
                StopPlaybackOutputs();
            }
            finally
            {
                try { primary?.Dispose(); } catch { }
            }
        }

        // Single default device (Shared / Exclusive for t5-t6).
        if (_tier >= 5 && !LooksLikeVirtualRenderDevice())
        {
            try
            {
                _output = new WasapiOut(AudioClientShareMode.Exclusive, latency);
                _output.Init(_synth);
                _output.Play();
                Emit($"WasapiOut Exclusive {latency}ms OK (tier={_tier})");
                return;
            }
            catch (Exception ex)
            {
                Emit($"WasapiOut Exclusive {latency}ms failed -> Shared: {ex.Message}");
                try { _output?.Dispose(); } catch { }
                _output = null;
            }
        }

        try
        {
            _output = new WasapiOut(AudioClientShareMode.Shared, latency);
            _output.Init(_synth);
            _output.Play();
            Emit($"WasapiOut Shared {latency}ms OK (tier={_tier})");
        }
        catch (Exception ex)
        {
            Emit($"WasapiOut Shared {latency}ms failed, fallback Shared 50ms: {ex.Message}");
            _output?.Dispose();
            _output = new WasapiOut(AudioClientShareMode.Shared, 50);
            _output.Init(_synth);
            _output.Play();
        }
    }

    private static bool LooksLikeVirtualName(string name) =>
        name.Contains("VMware", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Virtual", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Remote Audio", StringComparison.OrdinalIgnoreCase);

    private void TryRestartOutput()
    {
        if (!_recvEnabled || _synth == null) return;
        StartPlayback();
    }

    private async Task UnderrunWatchAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && _matrixRunning)
            {
                await Task.Delay(1000, ct).ConfigureAwait(false);
                if (_synth == null || _rate24k) continue;
                if (!AudioTier.Get(_tier).Allow24kFallback) continue;
                // VMware virtual render thrashing underrun counters — do not flip 48k<->24k mid-test.
                if (LooksLikeVirtualRenderDevice()) continue;
                var u = _synth.UnderrunEvents;
                var delta = u - _lastUnderrunSnapshot;
                _lastUnderrunSnapshot = u;
                if (delta < 80) continue;
                if (!TryEnable24kFallback()) continue;
                Log?.Invoke($"Ultra-low underruns 螖={delta}/s 鈥?scheduling 24 kHz matrix restart");
                var port = _audioPort;
                var send = _sendEnabled;
                var recv = _recvEnabled;
                var rate = _sampleRate;
                _ = Task.Run(async () =>
                {
                    try { await StartAsync(port, rate, send, recv).ConfigureAwait(false); }
                    catch (Exception ex) { Log?.Invoke("24k restart failed: " + ex.Message); }
                });
                break;
            }
        }
        catch (OperationCanceledException) { /* ok */ }
        catch (Exception ex)
        {
            LogRare("underrun-watch", "underrun watch: " + ex.Message);
        }
    }

    private static bool LooksLikeVirtualRenderDevice()
    {
        try
        {
            using var enumator = new MMDeviceEnumerator();
            using var dev = enumator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var name = (dev.FriendlyName ?? "") + " " + (dev.DeviceFriendlyName ?? "");
            return LooksLikeVirtualName(name);
        }
        catch { return true; } // prefer Shared if unknown
    }

    private void StartLoopbackCapture()
    {
        
        _excludeCapture?.Dispose();
        _excludeCapture = null;
        try { _capture?.StopRecording(); } catch { /* ignore */ }
        _capture?.Dispose();
        _capture = null;
        try { _excludeCapture?.StopRecording(); } catch { /* ignore */ }
        _excludeCapture?.Dispose();
        _excludeCapture = null;
        

        if (ProcessExcludeLoopbackCapture.IsOsSupported())
        {
            try
            {
                var ex = new ProcessExcludeLoopbackCapture();
                ex.DataAvailable += OnExcludeCaptureData;
                ex.RecordingStopped += (_, e) =>
                {
                    if (e.Exception != null)
                        Log?.Invoke("Exclude capture stopped: " + e.Exception.Message);
                };
                ex.StartRecording();
                _excludeCapture = ex;
                // Silence watchdog: endpoint fallback fights LatencyProbe/PlayProbe (AUDCLNT_E_DEVICE_IN_USE).
                // Opt-in only via SWB_ENDPOINT_FALLBACK=1. Default: keep exclude-self even when quiet.
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(2500).ConfigureAwait(false);
                        float tx;
                        lock (_peakLock) tx = _txPeak;
                        if (_excludeCapture == null || !_matrixRunning) return;
                        if (tx >= 0.02f) return;
                        var allowEndpoint = string.Equals(
                            Environment.GetEnvironmentVariable("SWB_ENDPOINT_FALLBACK"), "1",
                            StringComparison.Ordinal)
                            || File.Exists(Path.Combine(
                                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                "MWB-SWB-Host", "endpoint-fallback.flag"))
                            || File.Exists(@"C:\Users\Public\MWB-SWB-Host\endpoint-fallback.flag");
                        if (!allowEndpoint)
                        {
                            Emit("loopback=exclude-self silent (no endpoint fallback; set SWB_ENDPOINT_FALLBACK=1 to enable)");
                            return;
                        }
                        Emit("loopback=exclude-self silent -> falling back to endpoint+AEC");
                        try { _excludeCapture.StopRecording(); } catch { /* ignore */ }
                        try { _excludeCapture.Dispose(); } catch { /* ignore */ }
                        _excludeCapture = null;
                        if (_capture != null) return;
                        _capture = new WasapiLoopbackCapture();
                        _capture.DataAvailable += OnCaptureData;
                        _capture.RecordingStopped += (_, e) =>
                        {
                            if (e.Exception != null)
                                Log?.Invoke("Capture stopped: " + e.Exception.Message);
                        };
                        _capture.StartRecording();
                        Emit("loopback=endpoint-fallback (after exclude silence)");
                    }
                    catch (Exception watchEx)
                    {
                        LogRare("exclude-watch", "exclude silence watch: " + watchEx.Message);
                    }
                });

                Emit("loopback=exclude-self pid=" + Environment.ProcessId
                    + " build=" + ProcessExcludeLoopbackCapture.GetOsBuild()
                    + " format=" + ex.WaveFormat);
                return;
            }
            catch (Exception ex)
            {
                Emit("loopback=exclude-self FAILED -> endpoint-fallback: " + ex.GetType().Name + ": " + ex.Message + (ex.InnerException != null ? " | inner=" + ex.InnerException.Message : ""));
                try { _excludeCapture?.Dispose(); } catch { /* ignore */ }
                _excludeCapture = null;
                
            }
        }
        else
        {
            Emit("loopback=exclude-self unsupported (build="
                + ProcessExcludeLoopbackCapture.GetOsBuild() + " need>="
                + ProcessExcludeLoopbackCapture.MinOsBuild + ") -> endpoint-fallback");
        }

        _capture = new WasapiLoopbackCapture();
        _capture.DataAvailable += OnCaptureData;
        _capture.RecordingStopped += (_, e) =>
        {
            if (e.Exception != null)
                Log?.Invoke("Capture stopped: " + e.Exception.Message);
        };
        _capture.StartRecording();
        Emit("loopback=endpoint-fallback");
    }

    private void OnExcludeCaptureData(object? sender, WaveInEventArgs e)
    {
        // Same send path; WaveFormat comes from exclude capture.
        OnCaptureDataCore(e, _excludeCapture!.WaveFormat, applyAec: false);
    }

    private void OnCaptureData(object? sender, WaveInEventArgs e)
    {
        if (_capture == null) return;
        OnCaptureDataCore(e, _capture.WaveFormat, applyAec: true);
    }

    private void OnCaptureDataCore(WaveInEventArgs e, WaveFormat sourceFormat, bool applyAec)
    {
        if (e.BytesRecorded <= 0 || _udp == null || !_sendEnabled) return;
        try
        {
            using var raw = new RawSourceWaveStream(e.Buffer, 0, e.BytesRecorded, sourceFormat);
            var rate = EffectiveSampleRate();
            var stereo = StereoPcmCodec.ToStereoFloatAtRate(raw, rate);
            var frameSamples = AudioTier.SamplesPerChannel(_tier, rate);
            var frameFloats = frameSamples * 2;
            var floats = _captureFloat;
            if (frameFloats > floats.Length) return;

            int read;
            while ((read = stereo.Read(floats, 0, frameFloats)) > 0)
            {
                var span = floats.AsSpan(0, read);
                // pad incomplete frame with zeros
                if (read < frameFloats)
                    span = floats.AsSpan(0, frameFloats); // include zeros after read? need clear rest
                if (read < frameFloats)
                {
                    floats.AsSpan(read, frameFloats - read).Clear();
                    span = floats.AsSpan(0, frameFloats);
                }

                float capturePeak = PeakOfFloat(span);
                float residualPeak;
                if (applyAec && _aec != null && _recvEnabled)
                    residualPeak = _aec.Process(span);
                else
                    residualPeak = capturePeak;

                float hold;
                lock (_peakLock) hold = _rxHold;
                if (applyAec && _aec != null && _recvEnabled && EchoCanceller.ShouldSuppressSend(residualPeak, hold, capturePeak))
                {
                    // dedup: do not rebroadcast remote playback
                    if (read < frameFloats) break;
                    continue;
                }

                var txGain = 1f;
                var gainEnv = Environment.GetEnvironmentVariable("SWB_TX_GAIN");
                if (string.IsNullOrWhiteSpace(gainEnv))
                {
                    foreach (var gp in new[]
                             {
                                 Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                     "MWB-SWB-Host", "tx-gain.txt"),
                                 @"C:\Users\Public\MWB-SWB-Host\tx-gain.txt",
                             })
                    {
                        if (!File.Exists(gp)) continue;
                        gainEnv = File.ReadAllText(gp).Trim();
                        break;
                    }
                }
                if (!string.IsNullOrWhiteSpace(gainEnv) && float.TryParse(gainEnv, out var gParsed) && gParsed > 0)
                    txGain = Math.Clamp(gParsed, 0.25f, 8f);
                if (Math.Abs(txGain - 1f) > 0.01f)
                {
                    for (var gi = 0; gi < span.Length; gi++)
                    {
                        var v = span[gi] * txGain;
                        if (v > 1f) v = 1f;
                        else if (v < -1f) v = -1f;
                        span[gi] = v;
                    }
                    residualPeak = Math.Min(1f, residualPeak * txGain);
                }

                NoteTxPeak(residualPeak);

                var pcm = _pcmScratch.AsSpan(0, frameFloats);
                SwbAudioFrame.FloatToPcm16(span, pcm);
                var p = AudioTier.Get(_tier);
                var hdr = new SwbAudioFrame.Header(
                    (byte)_tier,
                    _rate24k,
                    p.LightPlc || p.FullPlc,
                    _txSeq++,
                    (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                var packet = SwbAudioFrame.Encode(hdr, pcm);
                if (_remotes.Count == 0)
                    LogRare("tx-no-peers", "Capture has audio but remotes=0 鈥?not sending");
                else
                    LatencyStageLog.MarkCaptureSend();
                BroadcastPacket(packet);

                if (read < frameFloats) break;
            }
        }
        catch (Exception ex)
        {
            LogRare($"cap:{ex.Message}", $"Capture send error: {ex.Message}");
        }
    }

    private void BroadcastPacket(byte[] packet)
    {
        if (_udp == null) return;
        foreach (var remote in _remotes.Values)
        {
            if (!remote.IncludeInMatrix) continue;
            try
            {
                var dest = remote.IpAddress;
                if (!IPAddress.TryParse(dest, out _))
                {
                    try
                    {
                        var addrs = Dns.GetHostAddresses(dest);
                        var v4 = addrs.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
                        if (v4 != null) dest = v4.ToString();
                    }
                    catch { /* keep dest */ }
                }
                _udp.Send(packet, packet.Length, dest, remote.AudioPort);
            }
            catch (Exception ex)
            {
                LogRare($"udp-tx:{remote.HostName}:{ex.Message}", $"UDP -> {remote.HostName}: {ex.Message}");
            }
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _udp != null)
        {
            try
            {
                var result = await _udp.ReceiveAsync(ct).ConfigureAwait(false);
                if (!_recvEnabled || _synth == null) continue;

                HandleIncomingPacket(result.Buffer, result.RemoteEndPoint);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested)
                    LogRare($"udp-rx:{ex.Message}", $"UDP recv: {ex.Message}");
            }
        }
    }

    private void HandleIncomingPacket(byte[] buffer, IPEndPoint remoteEp)
    {
        if (_synth == null) return;
        if (!SwbAudioFrame.TryDecode(buffer, out var hdr, out var pcm16))
            return;
        if (SwbAudioFrame.IsStale(hdr.TsSec))
        {
            LogRare("stale-drop", $"Drop stale frame age~{(int)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - hdr.TsSec)}s tier={hdr.Tier}");
            return;
        }
        // Soft tier: still accept audio so mismatched sliders do not cause one-way silence.
        // Use payload length as truth for sample count.
        if (hdr.Tier != _tier)
            MaybeLogTierMismatch(hdr.Tier);

        var ip = NormalizeIp(remoteEp.Address);
        if (!_ipToHost.TryGetValue(ip, out var host))
        {
            host = null!;
            foreach (var r in _remotes.Values)
            {
                if (string.Equals(r.IpAddress, ip, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(r.HostName, ip, StringComparison.OrdinalIgnoreCase))
                {
                    host = r.HostName;
                    _ipToHost[ip] = host;
                    // Upgrade hostname-only send target to dotted IP once; never flip between two IPs.
                    if (!IPAddress.TryParse(r.IpAddress, out _))
                        _remotes[host] = r with { IpAddress = ip };
                    break;
                }
            }
            // Single-peer mesh: map RX source IP -> host for decode only.
            // Never overwrite an existing dotted-quad send address (that caused alternating one-way).
            if (host == null)
            {
                var only = _remotes.Values.Where(r => r.IncludeInMatrix).Take(2).ToList();
                if (only.Count == 1)
                {
                    host = only[0].HostName;
                    _ipToHost[ip] = host;
                    var cur = only[0].IpAddress;
                    var curIsIp = IPAddress.TryParse(cur, out _);
                    if (!curIsIp)
                    {
                        _remotes[host] = only[0] with { IpAddress = ip };
                        LogRare("learn-ip", $"Learned peer IP {host} <- {ip} (was '{cur}')");
                    }
                    else if (!string.Equals(cur, ip, StringComparison.OrdinalIgnoreCase))
                    {
                        // Keep send target stable; only alias RX IP for host lookup.
                        LogRare("learn-ip-alias", $"RX alias {ip} -> {host} (send stays {cur})", 30);
                    }
                }
            }
            if (host == null) return;
        }

        if (host.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase))
            return;

        if (pcm16.Length > _rxFloat.Length) return;
        SwbAudioFrame.Pcm16ToFloat(pcm16, _rxFloat.AsSpan(0, pcm16.Length));

        var ring = _synth.EnsurePeer(host, _tier, EffectiveSampleRate());
        if (_forceSyncOffsetMs != null && _syncAlignStrength > 0)
        {
            var raw = _forceSyncOffsetMs(host);
            ring.SetExtraOffsetMs((int)Math.Round(Math.Max(0, raw) * _syncAlignStrength), EffectiveSampleRate());
        }

        ring.Push(_rxFloat.AsSpan(0, pcm16.Length), hdr.Seq);

        float peak = 0;
        for (var i = 0; i < pcm16.Length; i++)
        {
            var s = _rxFloat[i];
            var a = s < 0 ? -s : s;
            if (a > peak) peak = a;
        }
        LatencyStageLog.MarkUdpRxPush(peak);
        RxDumpSession.WriteNet(_rxFloat.AsSpan(0, pcm16.Length));
        NoteRxPeak(peak);
    }

    private void MaybeLogTierMismatch(byte peerTier)
    {
        if ((DateTime.UtcNow - _lastTierLog).TotalSeconds < 15) return;
        _lastTierLog = DateTime.UtcNow;
        Log?.Invoke($"Drop frame: peer tier={peerTier} local={_tier} — align latency slider.");
    }

    private static float PeakOfFloat(ReadOnlySpan<float> floats)
    {
        float peak = 0;
        foreach (var s in floats)
        {
            var a = s < 0 ? -s : s;
            if (a > peak) peak = a;
        }
        return peak > 1f ? 1f : peak;
    }

    /// <summary>Tier 6 underrun hint: switch to 24 kHz (caller may restart).</summary>
    public bool TryEnable24kFallback()
    {
        if (!AudioTier.Get(_tier).Allow24kFallback || _rate24k) return false;
        _rate24k = true;
        Log?.Invoke("Ultra-low: falling back to 24 kHz.");
        return true;
    }

    private void LogRare(string key, string message, double minIntervalSec = 15)
    {
        var now = Environment.TickCount64;
        var gate = (long)(minIntervalSec * 1000);
        if (_logNotBefore.TryGetValue(key, out var nb) && now < nb) return;
        _logNotBefore[key] = now + gate;
        Log?.Invoke(message);
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private static string NormalizeIp(IPAddress addr)
    {
        if (addr.IsIPv4MappedToIPv6)
            addr = addr.MapToIPv4();
        return addr.ToString();
    }

}

