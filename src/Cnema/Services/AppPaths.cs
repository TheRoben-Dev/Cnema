namespace Cnema.Services;

/// <summary>Per-OS locations for settings, the library cache and thumbnails.</summary>
public static class AppPaths
{
    public static string DataDir { get; } = ResolveDataDir();
    public static string ThumbDir { get; } = Ensure(Path.Combine(DataDir, "thumbs"));
    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string LibraryFile => Path.Combine(DataDir, "library.json");
    public static string CollectionsFile => Path.Combine(DataDir, "collections.json");

    /// <summary>
    /// Rolling capture segments for the clipping service. It lives in the user's cache folder rather than
    /// the temp folder, so a session cut short by a crash or a power cut is still there at the next start.
    /// </summary>
    public static string ClipBufferDir { get; } = ResolveBufferDir();

    /// <summary>Where the buffer lived up to v0.20; removed at start.</summary>
    public static string LegacyClipBufferDir => Path.Combine(Path.GetTempPath(), "cnema-clip-buffer");

    private static string ResolveBufferDir()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string root;
        if (OperatingSystem.IsMacOS())
            root = Path.Combine(home, "Library", "Caches", "Cnema");
        else if (OperatingSystem.IsWindows())
            root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cnema", "Cache");
        else
            root = Path.Combine(Environment.GetEnvironmentVariable("XDG_CACHE_HOME") is { Length: > 0 } xdg
                ? xdg
                : Path.Combine(home, ".cache"), "cnema");
        if (string.IsNullOrEmpty(home) && !OperatingSystem.IsWindows()) root = Path.Combine(Path.GetTempPath(), "cnema");
        return Path.Combine(root, "clip-buffer");
    }

    public static string DefaultClipsDir
    {
        get
        {
            var videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            if (string.IsNullOrEmpty(videos))
                videos = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    OperatingSystem.IsMacOS() ? "Movies" : "Videos");
            return Path.Combine(videos, "Cnema Clips");
        }
    }

    /// <summary>
    /// Portable mode: a "Cnema Data" folder next to the app (next to Cnema.app on macOS, next to the
    /// Cnema folder elsewhere) holds settings, library and thumbnails instead of the user profile.
    /// It sits beside the app rather than inside it so updates, which replace the app, leave it alone.
    /// </summary>
    public static string PortableDir
    {
        get
        {
            var dir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (OperatingSystem.IsMacOS() && Path.GetDirectoryName(Path.GetDirectoryName(dir)) is { } bundle &&
                bundle.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
                dir = bundle;
            return Path.Combine(Path.GetDirectoryName(dir) ?? dir, "Cnema Data");
        }
    }

    public static bool IsPortable { get; private set; }

    /// <summary>Copies settings, library and thumbnails into <see cref="PortableDir"/>; used from the next start.</summary>
    public static void MakePortable()
    {
        var target = PortableDir;
        Directory.CreateDirectory(target);
        foreach (var file in new[] { "settings.json", "library.json", "collections.json" })
        {
            var from = Path.Combine(DataDir, file);
            if (File.Exists(from)) File.Copy(from, Path.Combine(target, file), overwrite: true);
        }
        var thumbs = Path.Combine(target, "thumbs");
        Directory.CreateDirectory(thumbs);
        foreach (var f in Directory.EnumerateFiles(ThumbDir))
            File.Copy(f, Path.Combine(thumbs, Path.GetFileName(f)), overwrite: true);
    }

    private static string ResolveDataDir()
    {
        try
        {
            if (Directory.Exists(PortableDir))
            {
                IsPortable = true;
                return PortableDir;
            }
        }
        catch { /* fall back to the user profile */ }
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string root;
        if (OperatingSystem.IsMacOS())
            root = Path.Combine(home, "Library", "Application Support");
        else if (OperatingSystem.IsWindows())
            root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        else
            root = Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } xdg
                ? xdg
                : Path.Combine(home, ".local", "share");
        var dir = Path.Combine(root, "Cnema");
        // The app was called "Cinema" up to v0.3: carry its library, tags and settings over once.
        var legacy = Path.Combine(root, "Cinema");
        try
        {
            if (!Directory.Exists(dir) && Directory.Exists(legacy)) Directory.Move(legacy, dir);
        }
        catch { /* start fresh if the old folder cannot be moved */ }
        return Ensure(dir);
    }

    private static string Ensure(string dir)
    {
        Directory.CreateDirectory(dir);
        return dir;
    }
}
