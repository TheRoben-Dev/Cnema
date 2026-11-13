namespace Cinema.Services;

/// <summary>Per-OS locations for settings, the library cache and thumbnails.</summary>
public static class AppPaths
{
    public static string DataDir { get; } = ResolveDataDir();
    public static string ThumbDir { get; } = Ensure(Path.Combine(DataDir, "thumbs"));
    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string LibraryFile => Path.Combine(DataDir, "library.json");

    private static string ResolveDataDir()
    {
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
        return Ensure(Path.Combine(root, "Cinema"));
    }

    private static string Ensure(string dir)
    {
        Directory.CreateDirectory(dir);
        return dir;
    }
}
