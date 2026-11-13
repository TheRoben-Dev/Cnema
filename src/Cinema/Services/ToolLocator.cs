namespace Cinema.Services;

/// <summary>
/// Finds external CLI tools (ffmpeg/ffprobe). GUI apps on macOS launched from Finder do not
/// inherit the shell PATH, so Homebrew/MacPorts locations are probed explicitly.
/// </summary>
public static class ToolLocator
{
    private static readonly string[] ExtraDirs =
    {
        AppContext.BaseDirectory,
        Path.Combine(AppContext.BaseDirectory, "tools"),
        "/opt/homebrew/bin", "/usr/local/bin", "/opt/local/bin", "/usr/bin",
    };

    private static readonly Dictionary<string, string?> Cache = new();

    public static string? Ffmpeg => Find("ffmpeg");
    public static string? Ffprobe => Find("ffprobe");

    public static string? Find(string tool)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(tool, out var hit)) return hit;
            var exe = OperatingSystem.IsWindows() ? tool + ".exe" : tool;
            var path = Environment.GetEnvironmentVariable("PATH") ?? "";
            var dirs = path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).Concat(ExtraDirs);
            var found = dirs.Select(d => Path.Combine(d, exe)).FirstOrDefault(File.Exists);
            Cache[tool] = found;
            return found;
        }
    }
}
