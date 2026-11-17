using Avalonia;
using Cnema.Services;

namespace Cnema;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--selftest")) return SelfTest();
        if (Array.IndexOf(args, "--selftest-models") is var mi and >= 0) return ModelSelfTest(args.Skip(mi + 1).FirstOrDefault());
        if (Array.IndexOf(args, "--selftest-update") is var ui and >= 0 && ui + 1 < args.Length) return UpdateService.SelfTestInstallerUpdate(args[ui + 1]);
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
        var speech = SpeechService.RuntimeCheck();
        Console.WriteLine($"speech: {speech ?? "MISSING"}");
        var ok = vlc is not null && ToolLocator.Ffmpeg is not null && playback.StartsWith("OK") && speech is not null;
        Console.WriteLine(ok ? "selftest OK" : "selftest FAILED");
        return ok ? 0 : 1;
    }
    /// <summary>
    /// CI on packaged builds, with network: downloads the optional models like the app does (mirror, then fallback,
    /// SHA-256 checked) and runs every smart feature on a test video, so a model that can't load or run on this
    /// platform fails the build instead of silently doing nothing on someone's computer. With a speech recording
    /// (the macOS runner makes one with `say`), it also checks that the words come back.
    /// </summary>
    private static int ModelSelfTest(string? speechAudio)
    {
        var ok = true;
        void Check(string name, Func<string> run)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                // A model that hangs while starting fails here, instead of leaving the app waiting forever.
                var task = Task.Run(run);
                if (!task.Wait(TimeSpan.FromMinutes(5))) throw new TimeoutException("no answer within 5 minutes");
                Console.WriteLine($"{name}: {task.Result} ({sw.ElapsedMilliseconds} ms)");
            }
            catch (AggregateException ex) when (ex.InnerException is { } inner) { ok = false; Console.WriteLine($"{name}: FAILED - {inner.GetType().Name}: {inner.Message}"); }
            catch (Exception ex) { ok = false; Console.WriteLine($"{name}: FAILED - {ex.GetType().Name}: {ex.Message}"); }
        }
        var ffmpeg = ToolLocator.Ffmpeg;
        if (ffmpeg is null) { Console.WriteLine("ffmpeg: MISSING"); return 1; }
        Console.WriteLine($"models: {ModelStore.Dir}");
        Check("download", () =>
        {
            foreach (var pack in new IReadOnlyList<ModelInfo>[] { new[] { ModelStore.Speech }, ModelStore.Visual, ModelStore.Sound, ModelStore.Screen })
                ModelStore.DownloadAsync(pack, null, CancellationToken.None).GetAwaiter().GetResult();
            return "OK";
        });

        var video = Path.Combine(Path.GetTempPath(), $"cnema-models-{Environment.ProcessId}.mp4");
        var audio = speechAudio is { Length: > 0 } && File.Exists(speechAudio)
            ? new[] { "-i", speechAudio }
            : new[] { "-f", "lavfi", "-i", "sine=frequency=440:duration=8" };
        var made = ProcessRunner.RunAsync(ffmpeg, new[] { "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=30:duration=8" }
            .Concat(audio).Concat(new[] { "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", video })).GetAwaiter().GetResult();
        if (made.ExitCode != 0) { Console.WriteLine("test video: FAILED - " + made.StdErr.Trim()); return 1; }
        var entry = new Models.VideoEntry { Path = video };
        MediaProbe.ProbeAsync(entry, CancellationToken.None).GetAwaiter().GetResult();

        Check("best moments", () =>
        {
            var info = SmartAnalyzer.AnalyzeAsync(video, entry.DurationMs, CancellationToken.None).GetAwaiter().GetResult();
            return info is null ? throw new InvalidOperationException("no result") : $"OK (excitement {info.Excitement:0.00}, thumb {info.ThumbMs} ms)";
        });
        Check("speech", () =>
        {
            using var speech = SpeechService.Load(ModelStore.PathOf(ModelStore.Speech));
            var info = speech.TranscribeAsync(video, entry.DurationMs, CancellationToken.None).GetAwaiter().GetResult();
            var text = string.Join(" ", info.Lines.Select(l => l.Text));
            if (speechAudio is { Length: > 0 } && !text.Contains("savefile", StringComparison.OrdinalIgnoreCase) && !text.Contains("test", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"heard \"{text}\"");
            return $"OK ({info.Lines.Count} lines, {info.Language ?? "no language"}: \"{text}\")";
        });
        Check("visual", () =>
        {
            using var visual = VisualService.Load();
            var info = visual.LookAsync(video, entry.DurationMs, CancellationToken.None).GetAwaiter().GetResult();
            if (info is null || info.Frames.Length == 0) throw new InvalidOperationException("no frames looked at");
            var words = visual.EmbedText("a colourful test pattern");
            return $"OK ({info.Frames.Length} frames, match {Models.VisualInfo.Dot(info.Mean, words):0.000})";
        });
        Check("sound", () =>
        {
            using var sound = SoundService.Load();
            var info = sound.ListenAsync(video, entry.DurationMs, CancellationToken.None).GetAwaiter().GetResult();
            return $"OK ({info.Laugh.Count} laugh, {info.Hype.Count} hype moments)";
        });
        Check("screen", () =>
        {
            using var screen = ScreenService.Load();
            var info = screen.ReadAsync(video, entry.DurationMs, -1, CancellationToken.None).GetAwaiter().GetResult();
            return $"OK ({info.Lines.Count} lines: \"{string.Join(" | ", info.Lines.Select(l => l.Text))}\")";
        });
        try { File.Delete(video); } catch { /* temp */ }
        Console.WriteLine(ok ? "model selftest OK" : "model selftest FAILED");
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
