using Avalonia;
using Cnema.Services;

namespace Cnema;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--selftest")) return SelfTest();
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
    /// Headless check used by CI on packaged builds: does libVLC load, and are ffmpeg/ffprobe found?
    /// Exit code 0 = everything the app needs is present.
    /// </summary>
    private static int SelfTest()
    {
        VlcBootstrap.Initialize();
        var vlc = VlcBootstrap.LibVLC;
        Console.WriteLine($"libVLC:  {(vlc is null ? "MISSING - " + VlcBootstrap.Error : vlc.Version)}");
        Console.WriteLine($"ffmpeg:  {ToolLocator.Ffmpeg ?? "MISSING"}");
        Console.WriteLine($"ffprobe: {ToolLocator.Ffprobe ?? "MISSING"}");
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
        var sdl = GamepadService.SelfTest();
        Console.WriteLine($"gamepad: {sdl}");
        var ok = sdl == "OK" && vlc is not null && ToolLocator.Ffmpeg is not null && ToolLocator.Ffprobe is not null;
        Console.WriteLine(ok ? "selftest OK" : "selftest FAILED");
        return ok ? 0 : 1;
    }
}
