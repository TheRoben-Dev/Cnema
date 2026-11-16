using Avalonia;
using Cnema.Services;

namespace Cnema;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--selftest")) return SelfTest();
        Log.HookUnhandled();
        Log.Info($"Cnema {UpdateService.CurrentText} starting on {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
        // Cnema can live in the tray: a second launch just brings the running copy forward.
        if (SingleInstance.SignalExisting()) return 0;
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .With(new MacOSPlatformOptions { ShowInDock = true })
            .LogToTrace();

    /// <summary>
    /// Headless check used by CI on packaged builds: does libVLC load and decode, and is ffmpeg found?
    /// Exit code 0 = everything the app needs is present.
    /// </summary>
    private static int SelfTest()
    {
        VlcBootstrap.Initialize();
        var vlc = VlcBootstrap.LibVLC;
        Console.WriteLine($"libVLC:  {(vlc is null ? "MISSING - " + VlcBootstrap.Error : vlc.Version)}");
        Console.WriteLine($"ffmpeg:  {ToolLocator.Ffmpeg ?? "MISSING"}");
        var playback = vlc is null || ToolLocator.Ffmpeg is null ? "skipped" : PlaybackTest(vlc, ToolLocator.Ffmpeg);
        Console.WriteLine($"playback: {playback}");
        // Informational: what the clipping service would use on this machine.
        var capture = CaptureProbe.FindCaptureFfmpegAsync().GetAwaiter().GetResult();
        Console.WriteLine($"capture: {(capture is null ? $"no ffmpeg with {CaptureProbe.RequiredInput}" : capture)}");
        if (capture is not null)
        {
            var codecs = CaptureProbe.ListCodecsAsync(capture).GetAwaiter().GetResult();
            Console.WriteLine($"encoder: {string.Join(", ", codecs.Select(c => $"{c.Codec}={c.Encoder}"))}");
            if (OperatingSystem.IsMacOS())
                Console.WriteLine($"sysaudio: {CaptureProbe.SystemAudioHelper ?? "unavailable"}");
            if (OperatingSystem.IsWindows())
                Console.WriteLine($"ddagrab: {CaptureProbe.HasDdagrabAsync(capture).GetAwaiter().GetResult()}");
        }
        var ok = vlc is not null && ToolLocator.Ffmpeg is not null && playback.StartsWith("OK");
        Console.WriteLine(ok ? "selftest OK" : "selftest FAILED");
        return ok ? 0 : 1;
    }
    /// <summary>
    /// Makes a two-second H.264/AAC file with ffmpeg, reads its metadata and decodes it with libVLC.
    /// Proves that the libVLC plugins left in the package are enough to play ordinary video.
    /// </summary>
    private static string PlaybackTest(LibVLCSharp.Shared.LibVLC vlc, string ffmpeg)
    {
        var file = Path.Combine(Path.GetTempPath(), $"cnema-selftest-{Environment.ProcessId}.mp4");
        try
        {
            var made = ProcessRunner.RunAsync(ffmpeg, new[]
            {
                "-hide_banner", "-loglevel", "error", "-y",
                "-f", "lavfi", "-i", "testsrc=size=320x240:rate=30:duration=2",
                "-f", "lavfi", "-i", "sine=frequency=440:duration=2",
                "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", file,
            }).GetAwaiter().GetResult();
            if (made.ExitCode != 0 || !File.Exists(file)) return "FAILED - ffmpeg could not write a test file: " + made.StdErr.Trim();

            var entry = new Models.VideoEntry { Path = file };
            MediaProbe.ProbeAsync(entry, CancellationToken.None).GetAwaiter().GetResult();
            if (entry.Width != 320 || entry.Height != 240 || entry.DurationMs < 1500)
                return $"FAILED - metadata read as {entry.Width}x{entry.Height}, {entry.DurationMs} ms";

            const int w = 320, h = 240;
            var buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal(w * h * 4);
            var frames = 0;
            try
            {
                using var media = new LibVLCSharp.Shared.Media(vlc, file, LibVLCSharp.Shared.FromType.FromPath);
                media.AddOption(":no-audio");
                using var player = new LibVLCSharp.Shared.MediaPlayer(media);
                LibVLCSharp.Shared.MediaPlayer.LibVLCVideoLockCb onLock = (IntPtr _, IntPtr planes) =>
                {
                    System.Runtime.InteropServices.Marshal.WriteIntPtr(planes, buffer);
                    return IntPtr.Zero;
                };
                LibVLCSharp.Shared.MediaPlayer.LibVLCVideoDisplayCb onDisplay = (_, _) => Interlocked.Increment(ref frames);
                player.SetVideoFormat("RV32", w, h, w * 4);
                player.SetVideoCallbacks(onLock, null, onDisplay);
                player.Play();
                var until = DateTime.UtcNow.AddSeconds(15);
                while (DateTime.UtcNow < until && Volatile.Read(ref frames) < 10) Thread.Sleep(50);
                player.Stop();
                GC.KeepAlive(onLock);
                GC.KeepAlive(onDisplay);
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer);
            }
            return frames >= 10
                ? $"OK ({entry.VideoCodec} {entry.Width}x{entry.Height}, {frames} frames decoded)"
                : $"FAILED - libVLC decoded {frames} frames";
        }
        catch (Exception ex)
        {
            return "FAILED - " + ex.Message;
        }
        finally
        {
            try { File.Delete(file); } catch { /* temp file */ }
        }
    }
}
