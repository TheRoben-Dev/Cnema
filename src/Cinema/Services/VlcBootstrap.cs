using System.Runtime.InteropServices;
using LibVLCSharp.Shared;

namespace Cinema.Services;

/// <summary>
/// Loads native libVLC. Resolution order:
///   1. CINEMA_LIBVLC_PATH env var (a folder containing libvlc + libvlccore)
///   2. macOS: /Applications/VLC.app or ~/Applications/VLC.app
///   3. LibVLCSharp default probing (NuGet package on Windows, system lib on Linux)
/// </summary>
public static class VlcBootstrap
{
    public static LibVLC? LibVLC { get; private set; }
    public static string? Error { get; private set; }

    [DllImport("libc", EntryPoint = "setenv")]
    private static extern int SetEnvNative(string name, string value, int overwrite);

    public static void Initialize()
    {
        try
        {
            var custom = Environment.GetEnvironmentVariable("CINEMA_LIBVLC_PATH");
            if (!string.IsNullOrWhiteSpace(custom) && Directory.Exists(custom))
            {
                Core.Initialize(custom);
            }
            else if (OperatingSystem.IsMacOS() && FindVlcApp() is { } app)
            {
                var macOsDir = Path.Combine(app, "Contents", "MacOS");
                // libvlc reads getenv() directly; .NET's Environment.SetEnvironmentVariable does not
                // touch the native environment on Unix, so call setenv() ourselves.
                SetEnvNative("VLC_PLUGIN_PATH", Path.Combine(macOsDir, "plugins"), 1);
                Core.Initialize(Path.Combine(macOsDir, "lib"));
            }
            else
            {
                Core.Initialize();
            }

            LibVLC = new LibVLC("--no-video-title-show", "--no-snapshot-preview", "--quiet");
        }
        catch (Exception ex)
        {
            Error = OperatingSystem.IsMacOS()
                ? "libVLC not found. Install VLC.app into /Applications (brew install --cask vlc)."
                : OperatingSystem.IsLinux()
                    ? "libVLC not found. Install it (e.g. sudo apt install libvlc5 vlc-plugin-base)."
                    : "libVLC failed to load.";
            Console.Error.WriteLine($"[Cinema] {Error}\n{ex}");
        }
    }

    private static string? FindVlcApp()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new[] { "/Applications/VLC.app", Path.Combine(home, "Applications", "VLC.app") }
            .FirstOrDefault(Directory.Exists);
    }
}
