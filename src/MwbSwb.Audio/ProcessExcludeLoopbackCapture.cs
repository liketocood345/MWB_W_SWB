using System.Runtime.InteropServices;
using NAudio.Wave;

namespace MwbSwb.Audio;

/// <summary>
/// WASAPI process loopback excluding this process tree (Host WasapiOut).
/// Win10 Build 20348+ / Server 2022.
/// Activation uses native helper (managed ActivateAudioInterfaceAsync CCW returns E_NOINTERFACE).
/// </summary>
public sealed class ProcessExcludeLoopbackCapture : IDisposable
{
    public const int MinOsBuild = 20348;
    private static readonly Guid IidCaptureClient = new("C8ADBD64-E71E-48A0-A4DE-185C395CD645");

    private readonly uint _targetPid;
    private readonly ManualResetEventSlim _stop = new(false);
    private Thread? _thread;
    private IntPtr _audioClient = IntPtr.Zero;
    private IntPtr _captureClient = IntPtr.Zero;
    private IntPtr _eventHandle = IntPtr.Zero;
    private bool _started;
    private bool _disposed;

    public WaveFormat WaveFormat { get; private set; } =
        WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

    public event EventHandler<WaveInEventArgs>? DataAvailable;
    public event EventHandler<StoppedEventArgs>? RecordingStopped;

    public ProcessExcludeLoopbackCapture(uint? targetProcessId = null)
    {
        _targetPid = targetProcessId ?? (uint)Environment.ProcessId;
    }

    public static bool IsOsSupported()
    {
        try { return GetOsBuild() >= MinOsBuild; }
        catch { return false; }
    }

    public static int GetOsBuild()
    {
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
            @"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        var s = key?.GetValue("CurrentBuildNumber") as string
                ?? key?.GetValue("CurrentBuild") as string;
        return int.TryParse(s, out var n) ? n : 0;
    }

    public void StartRecording()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started) return;
        if (!IsOsSupported())
            throw new NotSupportedException(
                "Process loopback EXCLUDE requires OS build >= " + MinOsBuild);

        Exception? mtaEx = null;
        var mta = new Thread(() =>
        {
            try { ActivateAndInit(); }
            catch (Exception ex) { mtaEx = ex; }
        })
        {
            IsBackground = true,
            Name = "SWB-ExcludeActivate-MTA",
        };
        mta.SetApartmentState(ApartmentState.MTA);
        mta.Start();
        if (!mta.Join(20000))
            throw new TimeoutException("stage-mta: activate thread timed out");
        if (mtaEx != null)
            throw new InvalidOperationException("stage-mta: " + mtaEx.Message, mtaEx);

        _stop.Reset();
        _thread = new Thread(CaptureLoop)
        {
            IsBackground = true,
            Name = "SWB-ProcessExcludeLoopback",
        };
        _thread.Start();
        _started = true;
    }

    public void StopRecording()
    {
        if (!_started) return;
        _stop.Set();
        try { if (_audioClient != IntPtr.Zero) ComAudioClient.Stop(_audioClient); } catch { /* ignore */ }
        if (_thread != null && _thread.IsAlive)
            _thread.Join(3000);
        _thread = null;
        _started = false;
        RecordingStopped?.Invoke(this, new StoppedEventArgs());
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { StopRecording(); } catch { /* ignore */ }
        ReleaseCom();
        if (_eventHandle != IntPtr.Zero)
        {
            CloseHandle(_eventHandle);
            _eventHandle = IntPtr.Zero;
        }
        _stop.Dispose();
    }

    private void ActivateAndInit()
    {
        try
        {
            _eventHandle = CreateEventW(IntPtr.Zero, false, false, null);
            if (_eventHandle == IntPtr.Zero)
                throw new InvalidOperationException("stage-CreateEvent failed");

            WaveFormatExNative fmt = default;
            var hr = SwbStartExcludeLoopback(
                _targetPid,
                _eventHandle,
                out _audioClient,
                out _captureClient,
                ref fmt);
            if (hr < 0 || _audioClient == IntPtr.Zero || _captureClient == IntPtr.Zero)
                throw new InvalidOperationException(
                    "stage-native-start hr=0x" + unchecked((uint)hr).ToString("X8")
                    + " client=0x" + _audioClient.ToInt64().ToString("X")
                    + " capture=0x" + _captureClient.ToInt64().ToString("X"));

            // Match native PCM format (MS sample default).
            WaveFormat = new WaveFormat(
                fmt.nSamplesPerSec > 0 ? (int)fmt.nSamplesPerSec : 48000,
                fmt.wBitsPerSample > 0 ? fmt.wBitsPerSample : 16,
                fmt.nChannels > 0 ? fmt.nChannels : 2);
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "stage-ActivateAndInit: " + ex.GetType().Name + ": " + ex.Message, ex);
        }
    }

    private void InitClient(WaveFormat format, uint flags)
    {
        // Kept for potential fallback paths; primary init is native SwbStartExcludeLoopback.
        var fmtPtr = WaveFormatToHGlobal(format);
        try
        {
            var hr = ComAudioClient.Initialize(
                _audioClient,
                AudClntShareMode.Shared,
                flags,
                0,
                0,
                fmtPtr,
                Guid.Empty);
            if (hr < 0)
                throw new InvalidOperationException(
                    "Initialize hr=0x" + unchecked((uint)hr).ToString("X8"));
        }
        finally
        {
            Marshal.FreeHGlobal(fmtPtr);
        }
    }

    private void CaptureLoop()
    {
        var blockAlign = WaveFormat.BlockAlign;
        var buf = new byte[Math.Max(4096, WaveFormat.AverageBytesPerSecond / 5)];
        try
        {
            while (!_stop.IsSet)
            {
                var wait = WaitForSingleObject(_eventHandle, 100);
                if (_stop.IsSet) break;
                if (wait != 0 && wait != 0x00000080)
                    continue;

                while (true)
                {
                    var hr = ComCaptureClient.GetNextPacketSize(_captureClient, out var packetFrames);
                    if (hr < 0 || packetFrames == 0) break;

                    hr = ComCaptureClient.GetBuffer(_captureClient, out var data, out var numFrames, out var flags, out _, out _);
                    if (hr < 0) break;

                    var bytes = (int)(numFrames * (uint)blockAlign);
                    if (bytes > 0)
                    {
                        if (bytes > buf.Length)
                            buf = new byte[bytes];
                        if ((flags & AudClntBufferFlags.Silent) != 0)
                            Array.Clear(buf, 0, bytes);
                        else
                            Marshal.Copy(data, buf, 0, bytes);
                        DataAvailable?.Invoke(this, new WaveInEventArgs(buf, bytes));
                    }

                    ComCaptureClient.ReleaseBuffer(_captureClient, numFrames);
                }
            }
        }
        catch (Exception ex)
        {
            RecordingStopped?.Invoke(this, new StoppedEventArgs(ex));
        }
    }

    private void ReleaseCom()
    {
        if (_captureClient != IntPtr.Zero)
        {
            Marshal.Release(_captureClient);
            _captureClient = IntPtr.Zero;
        }
        if (_audioClient != IntPtr.Zero)
        {
            try { ComAudioClient.Stop(_audioClient); } catch { /* ignore */ }
            Marshal.Release(_audioClient);
            _audioClient = IntPtr.Zero;
        }
    }

    private static IntPtr WaveFormatToHGlobal(WaveFormat format)
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms))
            format.Serialize(w);
        var bytes = ms.ToArray();
        var ptr = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, ptr, bytes.Length);
        return ptr;
    }

    private static class ComAudioClient
    {
        public static int Initialize(IntPtr self, int shareMode, uint streamFlags,
            long hnsBufferDuration, long hnsPeriodicity, IntPtr pFormat, Guid session)
        {
            var fn = GetDelegate<InitializeFn>(self, 3);
            return fn(self, shareMode, streamFlags, hnsBufferDuration, hnsPeriodicity, pFormat, ref session);
        }

        public static int Start(IntPtr self) => GetDelegate<HrFn>(self, 10)(self);
        public static int Stop(IntPtr self) => GetDelegate<HrFn>(self, 11)(self);

        public static int SetEventHandle(IntPtr self, IntPtr eventHandle) =>
            GetDelegate<SetEventFn>(self, 13)(self, eventHandle);

        public static int GetService(IntPtr self, Guid iid, out IntPtr service) =>
            GetDelegate<GetServiceFn>(self, 14)(self, ref iid, out service);

        private static T GetDelegate<T>(IntPtr com, int slot) where T : Delegate
        {
            var vtable = Marshal.ReadIntPtr(com);
            var fnPtr = Marshal.ReadIntPtr(vtable, slot * IntPtr.Size);
            return Marshal.GetDelegateForFunctionPointer<T>(fnPtr);
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int InitializeFn(IntPtr self, int shareMode, uint streamFlags,
            long hnsBufferDuration, long hnsPeriodicity, IntPtr pFormat, ref Guid session);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int HrFn(IntPtr self);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int SetEventFn(IntPtr self, IntPtr eventHandle);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetServiceFn(IntPtr self, ref Guid iid, out IntPtr service);
    }

    private static class ComCaptureClient
    {
        public static int GetBuffer(IntPtr self, out IntPtr data, out uint numFrames,
            out uint flags, out ulong devicePos, out ulong qpc) =>
            GetDelegate<GetBufferFn>(self, 3)(self, out data, out numFrames, out flags, out devicePos, out qpc);

        public static int ReleaseBuffer(IntPtr self, uint numFrames) =>
            GetDelegate<ReleaseBufferFn>(self, 4)(self, numFrames);

        public static int GetNextPacketSize(IntPtr self, out uint frames) =>
            GetDelegate<GetNextFn>(self, 5)(self, out frames);

        private static T GetDelegate<T>(IntPtr com, int slot) where T : Delegate
        {
            var vtable = Marshal.ReadIntPtr(com);
            var fnPtr = Marshal.ReadIntPtr(vtable, slot * IntPtr.Size);
            return Marshal.GetDelegateForFunctionPointer<T>(fnPtr);
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetBufferFn(IntPtr self, out IntPtr data, out uint numFrames,
            out uint flags, out ulong devicePos, out ulong qpc);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int ReleaseBufferFn(IntPtr self, uint numFrames);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetNextFn(IntPtr self, out uint frames);
    }

    [DllImport("SwbProcessLoopback.dll", ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
    private static extern int SwbStartExcludeLoopback(
        uint targetPid,
        IntPtr eventHandle,
        out IntPtr outClient,
        out IntPtr outCapture,
        ref WaveFormatExNative outFormat);

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveFormatExNative
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateEventW(
        IntPtr lpEventAttributes, bool bManualReset, bool bInitialState, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    private static class AudClntShareMode
    {
        public const int Shared = 0;
    }

    private static class AudClntStreamFlags
    {
        public const uint Loopback = 0x00020000;
        public const uint EventCallback = 0x00040000;
        public const uint AutoConvertPcm = 0x80000000;
    }

    private static class AudClntBufferFlags
    {
        public const uint Silent = 0x2;
    }
}
