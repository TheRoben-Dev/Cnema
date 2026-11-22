using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;

namespace Cnema.Services;

/// <summary>
/// Windows "System audio": records what the speakers play with WASAPI loopback and streams it as raw 48 kHz
/// stereo float32 PCM into a named pipe that the capture ffmpeg reads ("-f f32le -ar 48000 -ac 2"), the same
/// way the Mac's cnema-audio helper feeds a FIFO.
///
/// Where Windows supports it (process loopback, Windows 10 2004+/11) everything Savefile itself plays is left
/// out (its save sound, a clip playing in the window), like on the Mac. Older Windows records the whole output
/// device. When nothing plays, or the sound can't be opened at all, silence is written so ffmpeg's audio clock
/// never stalls the capture.
/// </summary>
/// Only used where <see cref="Supported"/> is true (Windows 10+).
public sealed class WindowsLoopback : IDisposable
{
    public const int SampleRate = 48_000;
    public const int Channels = 2;
    private const int BytesPerFrame = Channels * sizeof(float);

    /// <summary>The pipe ffmpeg opens; one per Savefile process.</summary>
    public static string PipePath => $@"\\.\pipe\savefile-audio-{Environment.ProcessId}";

    /// <summary>System audio needs Windows 10 or newer (WASAPI loopback plus a modern pipe/ffmpeg setup).</summary>
    public static bool Supported => OperatingSystem.IsWindowsVersionAtLeast(10);

    /// <summary>"process loopback (Savefile left out)", "speaker loopback", or why it fell back to silence.</summary>
    public string Mode { get; private set; } = "starting";

    /// <summary>Set when the sound couldn't be opened and silence is recorded instead.</summary>
    public string? Problem { get; private set; }

    /// <summary>Raised once, from the capture thread, when Mode/Problem are known.</summary>
    public event Action<WindowsLoopback>? Ready;

    private readonly NamedPipeServerStream _pipe;
    private readonly Thread _thread;
    private volatile bool _stop;

    private WindowsLoopback()
    {
        // Created before ffmpeg starts so the name exists when ffmpeg opens it.
        _pipe = new NamedPipeServerStream(PipePath.Replace(@"\\.\pipe\", ""), PipeDirection.Out, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 1 << 20);
        _thread = new Thread(Run) { IsBackground = true, Name = "System audio" };
        // MTA: WASAPI and its activation callback are free-threaded.
        if (OperatingSystem.IsWindows()) _thread.SetApartmentState(ApartmentState.MTA);
    }

    public static WindowsLoopback Start()
    {
        var l = new WindowsLoopback();
        l._thread.Start();
        return l;
    }

    public void Dispose()
    {
        _stop = true;
        try { _pipe.Dispose(); } catch { /* already closed */ }
        _thread.Join(2000);
    }

    private delegate void SpanWriter(ReadOnlySpan<byte> data);

    private void Run()
    {
        try
        {
            // Wait for ffmpeg to open the pipe (it does so right after it starts); give up when stopped.
            var connect = _pipe.WaitForConnectionAsync();
            while (!connect.Wait(200))
                if (_stop) return;
        }
        catch { return; } // disposed before ffmpeg connected

        Capture? capture = null;
        try
        {
            capture = Capture.Open(out var mode);
            Mode = mode;
        }
        catch (Exception ex)
        {
            Problem = ex.Message;
            Mode = "silence";
        }
        try { Ready?.Invoke(this); } catch { /* listener's problem */ }

        var clock = Stopwatch.StartNew();
        long written = 0;
        var buffer = new byte[SampleRate / 10 * BytesPerFrame];
        try
        {
            while (!_stop)
            {
                if (capture is not null)
                {
                    try
                    {
                        capture.Wait(20);
                        written += capture.Drain(data => _pipe.Write(data));
                    }
                    catch (IOException) { throw; }
                    catch (Exception ex)
                    {
                        // The output device went away (unplugged headset, driver reset): try to reopen it,
                        // writing silence meanwhile.
                        Log.Warn($"System audio: capture stopped ({ex.Message}); reopening");
                        capture.Dispose();
                        capture = null;
                        Thread.Sleep(500);
                        try { capture = Capture.Open(out var mode); Mode = mode; }
                        catch (Exception reopen) { Problem = reopen.Message; Mode = "silence"; }
                    }
                }
                else Thread.Sleep(20);

                // Loopback only delivers audio while something plays: fill gaps over 100 ms with silence.
                var expected = (long)(clock.Elapsed.TotalSeconds * SampleRate);
                var behind = expected - written;
                if (behind > SampleRate / 10)
                {
                    while (behind > 0)
                    {
                        var frames = (int)Math.Min(behind, buffer.Length / BytesPerFrame);
                        _pipe.Write(buffer, 0, frames * BytesPerFrame);
                        written += frames;
                        behind -= frames;
                    }
                }
            }
        }
        catch (Exception) { /* ffmpeg closed the pipe: capture stopped */ }
        finally
        {
            capture?.Dispose();
        }
    }

    /// <summary>One opened WASAPI loopback stream delivering 48 kHz stereo float.</summary>
    private sealed class Capture : IDisposable
    {
        private readonly IAudioClient _client;
        private readonly IAudioCaptureClient _capture;
        private readonly AutoResetEvent? _event;
        private byte[] _scratch = new byte[SampleRate / 10 * BytesPerFrame];

        private Capture(IAudioClient client, AutoResetEvent? evt)
        {
            _client = client;
            _event = evt;
            var iid = typeof(IAudioCaptureClient).GUID;
            Check(_client.GetService(ref iid, out var service), "GetService");
            _capture = (IAudioCaptureClient)service;
            Check(_client.Start(), "Start");
        }

        public static Capture Open(out string mode)
        {
            // Process loopback: the whole output minus Savefile's own process tree.
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            {
                try
                {
                    var client = ActivateProcessLoopback();
                    var evt = new AutoResetEvent(false);
                    Init(client, AudclntStreamflagsLoopback | AudclntStreamflagsEventcallback);
                    Check(client.SetEventHandle(evt.SafeWaitHandle.DangerousGetHandle()), "SetEventHandle");
                    mode = "process loopback (Savefile left out)";
                    return new Capture(client, evt);
                }
                catch (Exception ex)
                {
                    Log.Info($"System audio: process loopback unavailable ({ex.Message}); recording the speakers instead");
                }
            }

            // The default playback device as a whole.
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            try
            {
                Check(enumerator.GetDefaultAudioEndpoint(0 /* eRender */, 1 /* eMultimedia */, out var device),
                    "No playback device");
                var iid = typeof(IAudioClient).GUID;
                Check(device.Activate(ref iid, 23 /* CLSCTX_ALL */, IntPtr.Zero, out var obj), "Activate");
                var client = (IAudioClient)obj;
                Init(client, AudclntStreamflagsLoopback | AudclntStreamflagsAutoconvertpcm | AudclntStreamflagsSrcDefaultQuality);
                mode = "speaker loopback";
                return new Capture(client, null);
            }
            finally { Release(enumerator); }
        }

        private static void Init(IAudioClient client, uint flags)
        {
            var format = new WaveFormatEx
            {
                wFormatTag = 3, // WAVE_FORMAT_IEEE_FLOAT
                nChannels = Channels,
                nSamplesPerSec = SampleRate,
                nAvgBytesPerSec = SampleRate * BytesPerFrame,
                nBlockAlign = BytesPerFrame,
                wBitsPerSample = 32,
                cbSize = 0,
            };
            var ptr = Marshal.AllocHGlobal(Marshal.SizeOf<WaveFormatEx>());
            try
            {
                Marshal.StructureToPtr(format, ptr, false);
                // 200 ms buffer, shared mode.
                Check(client.Initialize(0, flags, 2_000_000, 0, ptr, IntPtr.Zero), "Initialize");
            }
            finally { Marshal.FreeHGlobal(ptr); }
        }

        public void Wait(int ms)
        {
            if (_event is not null) _event.WaitOne(ms);
            else Thread.Sleep(Math.Min(ms, 10));
        }

        /// <summary>Hands every waiting packet to <paramref name="write"/>; returns the frames written.</summary>
        public long Drain(SpanWriter write)
        {
            long total = 0;
            while (true)
            {
                Check(_capture.GetNextPacketSize(out var next), "GetNextPacketSize");
                if (next == 0) return total;
                Check(_capture.GetBuffer(out var data, out var frames, out var flags, out _, out _), "GetBuffer");
                try
                {
                    var bytes = (int)frames * BytesPerFrame;
                    if (bytes > 0)
                    {
                        if ((flags & AudclntBufferflagsSilent) != 0 || data == IntPtr.Zero)
                        {
                            if (_scratch.Length < bytes) _scratch = new byte[bytes];
                            Array.Clear(_scratch, 0, bytes);
                            write(_scratch.AsSpan(0, bytes));
                        }
                        else
                        {
                            unsafe { write(new ReadOnlySpan<byte>((void*)data, bytes)); }
                        }
                        total += frames;
                    }
                }
                finally { _capture.ReleaseBuffer(frames); }
            }
        }

        public void Dispose()
        {
            try { _client.Stop(); } catch { /* device gone */ }
            try { Release(_capture); } catch { /* already released */ }
            try { Release(_client); } catch { /* already released */ }
            _event?.Dispose();
        }
    }

    // ---------- process loopback activation ----------

    private static IAudioClient ActivateProcessLoopback()
    {
        var parameters = new AudioClientActivationParams
        {
            ActivationType = 1, // AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK
            TargetProcessId = (uint)Environment.ProcessId,
            ProcessLoopbackMode = 1, // PROCESS_LOOPBACK_MODE_EXCLUDE_TARGET_PROCESS_TREE
        };
        var paramsPtr = Marshal.AllocHGlobal(Marshal.SizeOf<AudioClientActivationParams>());
        var variantPtr = Marshal.AllocHGlobal(Marshal.SizeOf<PropVariantBlob>());
        try
        {
            Marshal.StructureToPtr(parameters, paramsPtr, false);
            Marshal.StructureToPtr(new PropVariantBlob
            {
                vt = 65, // VT_BLOB
                cbSize = (uint)Marshal.SizeOf<AudioClientActivationParams>(),
                pBlobData = paramsPtr,
            }, variantPtr, false);

            var handler = new ActivationHandler();
            var iid = typeof(IAudioClient).GUID;
            Check(ActivateAudioInterfaceAsync("VAD\\Process_Loopback", ref iid, variantPtr, handler, out var op),
                "ActivateAudioInterfaceAsync");
            try
            {
                if (!handler.Done.Wait(5000)) throw new TimeoutException("Windows didn't open the audio in time");
                Check(handler.Result, "Process loopback");
                return (IAudioClient)handler.Client!;
            }
            finally { Release(op); }
        }
        finally
        {
            Marshal.FreeHGlobal(variantPtr);
            Marshal.FreeHGlobal(paramsPtr);
        }
    }

    // Public (COM only hands callbacks to visible types); nothing outside this class uses them.
    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    public sealed class ActivationHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        internal readonly ManualResetEventSlim Done = new(false);
        internal int Result = unchecked((int)0x80004005);
        internal object? Client;

        public int ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation)
        {
            try
            {
                var hr = operation.GetActivateResult(out var result, out var client);
                Result = hr < 0 ? hr : result;
                Client = client;
            }
            catch (Exception ex) { Result = ex.HResult; }
            finally { Done.Set(); }
            return 0;
        }
    }

    private static void Release(object com)
    {
        if (OperatingSystem.IsWindows()) Marshal.ReleaseComObject(com);
    }

    private static void Check(int hr, string what)
    {
        if (hr < 0) throw new InvalidOperationException($"{what} failed (0x{hr:X8})");
    }

    // ---------- interop ----------

    private const uint AudclntStreamflagsLoopback = 0x00020000;
    private const uint AudclntStreamflagsEventcallback = 0x00040000;
    private const uint AudclntStreamflagsSrcDefaultQuality = 0x08000000;
    private const uint AudclntStreamflagsAutoconvertpcm = 0x80000000;
    private const uint AudclntBufferflagsSilent = 0x2;

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath, ref Guid riid, IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler, out IActivateAudioInterfaceAsyncOperation operation);

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WaveFormatEx
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public int nSamplesPerSec;
        public int nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioClientActivationParams
    {
        public int ActivationType;
        public uint TargetProcessId;
        public int ProcessLoopbackMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariantBlob
    {
        public ushort vt;
        public ushort r1, r2, r3;
        public uint cbSize;
        public IntPtr pBlobData;
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumerator { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint frames);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr handle);
        [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint frames);
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IActivateAudioInterfaceCompletionHandler
    {
        [PreserveSig] int ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation);
    }

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IActivateAudioInterfaceAsyncOperation
    {
        [PreserveSig] int GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    [ComImport, Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAgileObject { }
}
