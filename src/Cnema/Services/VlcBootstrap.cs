using System.Runtime.InteropServices;
using LibVLCSharp.Shared;

namespace Cnema.Services;

/// <summary>
/// Loads native libVLC. Resolution order:
///   1. CNEMA_LIBVLC_PATH env var (a folder containing libvlc + libvlccore)
///   2. macOS: the copy bundled next to the app (libvlc/osx-&lt;arch&gt;/lib), from the VideoLAN NuGet package
///   3. macOS: /Applications/VLC.app or ~/Applications/VLC.app
///   4. LibVLCSharp default probing (NuGet package on Windows, system lib on Linux)
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
            if (OperatingSystem.IsLinux()) MapLinuxSonames();
            var custom = Environment.GetEnvironmentVariable("CNEMA_LIBVLC_PATH");
            if (!string.IsNullOrWhiteSpace(custom) && Directory.Exists(custom))
            {
                Core.Initialize(custom);
            }
            else if (OperatingSystem.IsMacOS() && FindBundledMac() is { } bundled)
            {
                SetEnvNative("VLC_PLUGIN_PATH", Path.Combine(bundled, "vlc", "plugins"), 1);
                Core.Initialize(bundled);
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
                ? T("libVLC failed to load. Reinstall Cnema, or install VLC.app into /Applications as a fallback.")
                : OperatingSystem.IsLinux()
                    ? T("libVLC not found. Install it (e.g. sudo apt install libvlc5 vlc-plugin-base).")
                    : T("libVLC failed to load.");
            Log.Warn($"{Error}\n{ex}");
        }
    }

    /// <summary>
    /// Distros ship libvlc.so.5 / libvlccore.so.9; the unversioned libvlc.so only exists when the -dev
    /// package is installed. Map LibVLCSharp's "libvlc" import to the versioned names so the regular
    /// runtime package (libvlc5 / vlc-libs) is enough.
    /// </summary>
    private static void MapLinuxSonames()
    {
        NativeLibrary.SetDllImportResolver(typeof(LibVLC).Assembly, (name, assembly, searchPath) =>
        {
            var candidates = name switch
            {
                "libvlc" => new[] { "libvlc.so.5", "libvlc.so" },
                "libvlccore" => new[] { "libvlccore.so.9", "libvlccore.so" },
                _ => Array.Empty<string>(),
            };
            foreach (var c in candidates)
                if (NativeLibrary.TryLoad(c, assembly, searchPath, out var handle)) return handle;
            return IntPtr.Zero; // fall back to default probing
        });
    }

    private static string? FindBundledMac()
    {
        var arch = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "osx-arm64" : "osx-x64";
        var lib = Path.Combine(AppContext.BaseDirectory, "libvlc", arch, "lib");
        return File.Exists(Path.Combine(lib, "libvlc.dylib")) ? lib : null;
    }

    private static string? FindVlcApp()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new[] { "/Applications/VLC.app", Path.Combine(home, "Applications", "VLC.app") }
            .FirstOrDefault(Directory.Exists);
    }
}
