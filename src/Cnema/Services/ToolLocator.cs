namespace Cnema.Services;

/// <summary>
/// Finds ffmpeg/ffprobe. Release builds ship them in a "tools" folder next to the app, which wins.
/// Otherwise PATH is searched; GUI apps on macOS launched from Finder do not inherit the shell PATH,
/// so Homebrew/MacPorts locations are probed explicitly.
/// </summary>
public static class ToolLocator
{
    private static readonly string[] BundledDirs =
    {
        Path.Combine(AppContext.BaseDirectory, "tools"),
        AppContext.BaseDirectory,
    };

    private static readonly string[] ExtraDirs =
    {
        "/opt/homebrew/bin", "/usr/local/bin", "/opt/local/bin", "/usr/bin",
    };

    private static readonly Dictionary<string, string?> Cache = new();

    public static string? Ffmpeg => Find("ffmpeg");
    public static string? Ffprobe => Find("ffprobe");

    /// <summary>Every copy of a tool, bundled first. Used when a specific build feature is needed.</summary>
    public static IReadOnlyList<string> FindAll(string tool)
    {
        var exe = OperatingSystem.IsWindows() ? tool + ".exe" : tool;
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        return BundledDirs
            .Concat(path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            .Concat(ExtraDirs)
            .Select(d => Path.Combine(d, exe))
            .Where(File.Exists)
            .Distinct()
            .ToList();
    }

    public static string? Find(string tool)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(tool, out var hit)) return hit;
            var exe = OperatingSystem.IsWindows() ? tool + ".exe" : tool;
            var path = Environment.GetEnvironmentVariable("PATH") ?? "";
            var dirs = BundledDirs
                .Concat(path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                .Concat(ExtraDirs);
            var found = dirs.Select(d => Path.Combine(d, exe)).FirstOrDefault(File.Exists);
            Cache[tool] = found;
            return found;
        }
    }
}
