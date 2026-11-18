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

    /// <summary>The bundled plugin folder (Windows, macOS app), where libVLC keeps its plugin cache; null for a system VLC.</summary>
    public static string? PluginDir { get; private set; }

    /// <summary>How long loading libVLC took this run.</summary>
    public static long StartMs { get; private set; }

    /// <summary>The run-time option that has libVLC scan its plugins once and write the cache (what vlc-cache-gen does).</summary>
    public const string CacheFlag = "--vlc-cache";

    [DllImport("libc", EntryPoint = "setenv")]
    private static extern int SetEnvNative(string name, string value, int overwrite);

    private static int _started;

    /// <summary>Once per run; later calls (the loading screen did it already) do nothing.</summary>
    public static void Initialize() => Initialize(writeCache: false);

    /// <param name="writeCache">Scan every plugin and write plugins.dat next to them (the <see cref="CacheFlag"/> run).</param>
    public static void Initialize(bool writeCache)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        var clock = System.Diagnostics.Stopwatch.StartNew();
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
                PluginDir = Path.Combine(bundled, "vlc", "plugins");
                SetEnvNative("VLC_PLUGIN_PATH", PluginDir, 1);
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
                if (OperatingSystem.IsWindows())
                {
                    var arch = RuntimeInformation.ProcessArchitecture switch { Architecture.Arm64 => "win-arm64", Architecture.X86 => "win-x86", _ => "win-x64" };
                    var dir = Path.Combine(AppContext.BaseDirectory, "libvlc", arch, "plugins");
                    if (Directory.Exists(dir)) PluginDir = dir;
                }
                Core.Initialize();
            }

            // Without a plugin cache libVLC opens all of its ~250 plugins on every start (seconds on Windows, where
            // each one is also virus-scanned); with it, only the few a video needs, when it needs them.
            LibVLC = writeCache
                ? new LibVLC("--quiet", "--reset-plugins-cache")
                : new LibVLC("--no-video-title-show", "--no-snapshot-preview", "--quiet");
        }
        catch (Exception ex)
        {
            Error = OperatingSystem.IsMacOS()
                ? T("libVLC failed to load. Reinstall Savefile, or install VLC.app into /Applications as a fallback.")
                : OperatingSystem.IsLinux()
                    ? T("libVLC not found. Install it (e.g. sudo apt install libvlc5 vlc-plugin-base).")
                    : T("libVLC failed to load.");
            Log.Warn($"{Error}\n{ex}");
        }
        StartMs = clock.ElapsedMilliseconds;
    }

    private static string CacheFile(string dir) => Path.Combine(dir, "plugins.dat");

    /// <summary>
    /// A start that had to open every plugin (no cache, or one made stale when the files were copied with new
    /// dates) writes the cache for next time: a short run of the app itself with <see cref="CacheFlag"/>, in the
    /// background, once per version. Folders the app can't write to (an install for everyone) are left alone.
    /// </summary>
    public static void RefreshCacheInBackground()
    {
        if (LibVLC is null || PluginDir is not { } dir || !Directory.Exists(dir)) return;
        if (File.Exists(CacheFile(dir)) && StartMs < 1500) return;
        if (Environment.ProcessPath is not { } self) return;
        var marker = Path.Combine(AppPaths.DataDir, "vlc-cache.txt");
        var key = $"{UpdateService.CurrentText}|{dir}";
        try
        {
            if (File.Exists(marker) && File.ReadAllText(marker) == key) return; // tried for this version already
            File.WriteAllText(marker, key);
            var probe = Path.Combine(dir, $".write-test-{Environment.ProcessId}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
        }
        catch { return; } // read-only install
        Log.Info($"libVLC took {StartMs} ms to start ({(File.Exists(CacheFile(dir)) ? "stale" : "no")} plugin cache): writing one for next time");
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(20)); // after the start-up rush
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
                var r = await ProcessRunner.RunAsync(self, new[] { CacheFlag }, ct: cts.Token, background: true);
                Log.Info($"libVLC plugin cache: {(r.ExitCode == 0 ? "written" : "failed: " + (r.StdOut + r.StdErr).Trim())}");
            }
            catch (Exception ex) { Log.Warn($"libVLC plugin cache: {ex.Message}"); }
        });
    }

    /// <summary>The <see cref="CacheFlag"/> run: write plugins.dat and say how it went.</summary>
    public static int WriteCache()
    {
        Initialize(writeCache: true);
        var ok = LibVLC is not null && PluginDir is { } dir && File.Exists(CacheFile(dir));
        Console.WriteLine(ok ? $"plugin cache written ({StartMs} ms): {CacheFile(PluginDir!)}" : $"plugin cache not written: {Error ?? "no bundled plugin folder"}");
        LibVLC?.Dispose();
        return ok ? 0 : 1;
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
