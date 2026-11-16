using System.Text.RegularExpressions;

namespace Cnema.Services;

// New kinds go at the end: settings store them as numbers.
public enum RecorderKind { Obs, Nvidia, XboxGameBar, Steam, Medal, AmdRelive, Outplayed, Insights, SteelSeries, SnippingTool, MacScreen, Custom }

/// <summary>A recording folder of another app that the user brought into the library.</summary>
public sealed class RecorderImport
{
    public RecorderKind Kind { get; set; }
    public string Path { get; set; } = "";
    /// <summary>Cnema added the library folder for it (so removing the recorder removes the folder too).</summary>
    public bool AddedFolder { get; set; }
    /// <summary>Steam: the clip folders already copied, so a copy you deleted doesn't come back.</summary>
    public List<string> Converted { get; set; } = new();
}

/// <summary>What Cnema found on this computer for one recorder.</summary>
public sealed record RecorderInfo(RecorderKind Kind, string Name, string? Path, bool Supported);

/// <summary>
/// Finds the recording folders of OBS, NVIDIA (ShadowPlay / NVIDIA app), Xbox Game Bar and Steam, names the game
/// a file belongs to, and turns Steam's streaming-format clips into normal MP4 files.
/// </summary>
public static partial class RecorderService
{
    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static string Videos
    {
        get
        {
            var v = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            if (!string.IsNullOrEmpty(v)) return v;
            return System.IO.Path.Combine(Home, OperatingSystem.IsMacOS() ? "Movies" : "Videos");
        }
    }

    public static string NameOf(RecorderKind k) => k switch
    {
        RecorderKind.Obs => "OBS Studio",
        RecorderKind.Nvidia => "NVIDIA",
        RecorderKind.XboxGameBar => "Xbox Game Bar",
        RecorderKind.Medal => "Medal",
        RecorderKind.AmdRelive => "AMD ReLive",
        RecorderKind.Outplayed => "Outplayed",
        RecorderKind.Insights => "Insights Capture",
        RecorderKind.SteelSeries => "SteelSeries Moments",
        RecorderKind.SnippingTool => "Snipping Tool",
        RecorderKind.MacScreen => T("macOS screen recordings"),
        RecorderKind.Custom => T("Another app"),
        _ => "Steam",
    };

    public static List<RecorderInfo> Detect() => new()
    {
        new(RecorderKind.Obs, NameOf(RecorderKind.Obs), ObsFolder(), true),
        new(RecorderKind.Nvidia, NameOf(RecorderKind.Nvidia), OperatingSystem.IsWindows() ? NvidiaFolder() : null, OperatingSystem.IsWindows()),
        new(RecorderKind.XboxGameBar, NameOf(RecorderKind.XboxGameBar), OperatingSystem.IsWindows() ? XboxFolder() : null, OperatingSystem.IsWindows()),
        new(RecorderKind.Steam, NameOf(RecorderKind.Steam), SteamClipsFolder(), true),
        Win(RecorderKind.Medal, () => FirstDir(Path.Combine(Videos, "Medal"), @"C:\Medal\Clips", @"C:\Medal")),
        Win(RecorderKind.AmdRelive, () => FirstDir(Path.Combine(Videos, "Radeon ReLive"), Path.Combine(Videos, "AMD"))),
        Win(RecorderKind.Outplayed, () => FirstDir(Path.Combine(Videos, "Overwolf", "Outplayed"))),
        Win(RecorderKind.Insights, () => FirstDir(Path.Combine(Videos, "Insights Capture"))),
        Win(RecorderKind.SteelSeries, () => FirstDir(Path.Combine(Videos, "SteelSeries Moments"), Path.Combine(Videos, "SteelSeries", "Moments"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SteelSeries", "GG", "apps", "moments", "clips"))),
        Win(RecorderKind.SnippingTool, () => FirstDir(Path.Combine(Videos, "Screen Recordings"))),
        new(RecorderKind.MacScreen, NameOf(RecorderKind.MacScreen), OperatingSystem.IsMacOS() ? MacScreenFolder() : null, OperatingSystem.IsMacOS()),
        new(RecorderKind.Custom, NameOf(RecorderKind.Custom), null, true),
    };

    private static RecorderInfo Win(RecorderKind k, Func<string?> find) =>
        new(k, NameOf(k), OperatingSystem.IsWindows() ? find() : null, OperatingSystem.IsWindows());

    private static string? FirstDir(params string[] candidates) => candidates.FirstOrDefault(Directory.Exists);

    /// <summary>Where macOS saves screen recordings: the screencapture location if set, otherwise the Desktop.</summary>
    private static string? MacScreenFolder()
    {
        try
        {
            var plist = Path.Combine(Home, "Library", "Preferences", "com.apple.screencapture.plist");
            if (File.Exists(plist))
            {
                var psi = new System.Diagnostics.ProcessStartInfo("/usr/bin/defaults", "read com.apple.screencapture location")
                    { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                using var p = System.Diagnostics.Process.Start(psi);
                var output = p?.StandardOutput.ReadToEnd().Trim();
                p?.WaitForExit(2000);
                if (!string.IsNullOrEmpty(output))
                {
                    var dir = output.StartsWith('~') ? Home + output[1..] : output;
                    if (Directory.Exists(dir)) return dir;
                }
            }
        }
        catch { /* fall back to the Desktop */ }
        return FirstDir(Path.Combine(Home, "Desktop"));
    }

    // ---------- finding the folders ----------
    /// <summary>OBS keeps the recording path per profile in basic.ini; the first one that exists wins.</summary>
    public static string? ObsFolder()
    {
        var root = OperatingSystem.IsWindows()
            ? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "obs-studio")
            : OperatingSystem.IsMacOS()
                ? System.IO.Path.Combine(Home, "Library", "Application Support", "obs-studio")
                : System.IO.Path.Combine(Home, ".config", "obs-studio");
        var profiles = System.IO.Path.Combine(root, "basic", "profiles");
        if (!Directory.Exists(profiles)) return null;
        try
        {
            foreach (var ini in Directory.EnumerateFiles(profiles, "basic.ini", SearchOption.AllDirectories))
            {
                var values = ReadIni(ini);
                var advanced = values.TryGetValue("Output.Mode", out var mode) && mode.Equals("Advanced", StringComparison.OrdinalIgnoreCase);
                var keys = advanced ? new[] { "AdvOut.RecFilePath", "SimpleOutput.FilePath" } : new[] { "SimpleOutput.FilePath", "AdvOut.RecFilePath" };
                foreach (var k in keys)
                    if (values.TryGetValue(k, out var p) && Directory.Exists(p)) return p;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        // OBS is installed but never changed the path: it records to the Videos / Movies folder.
        return Directory.Exists(Videos) ? Videos : null;
    }

    private static Dictionary<string, string> ReadIni(string file)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var section = "";
        foreach (var raw in File.ReadLines(file))
        {
            var line = raw.Trim().TrimStart('\uFEFF');
            if (line.StartsWith('[') && line.EndsWith(']')) { section = line[1..^1]; continue; }
            var eq = line.IndexOf('=');
            if (eq > 0) d[section + "." + line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }
        return d;
    }

    /// <summary>NVIDIA saves into Videos, one subfolder per game, unless the user picked another folder in its overlay.</summary>
    private static string? NvidiaFolder()
    {
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var installed = Directory.Exists(System.IO.Path.Combine(pf, "NVIDIA Corporation", "NVIDIA app")) ||
                        Directory.Exists(System.IO.Path.Combine(pf, "NVIDIA Corporation", "NVIDIA GeForce Experience"));
        return installed && Directory.Exists(Videos) ? Videos : null;
    }

    private static string? XboxFolder()
    {
        var p = System.IO.Path.Combine(Videos, "Captures");
        return Directory.Exists(p) ? p : null;
    }

    private static IEnumerable<string> SteamRoots()
    {
        if (OperatingSystem.IsWindows())
        {
            yield return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
            yield return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam");
        }
        else if (OperatingSystem.IsMacOS())
            yield return System.IO.Path.Combine(Home, "Library", "Application Support", "Steam");
        else
        {
            yield return System.IO.Path.Combine(Home, ".local", "share", "Steam");
            yield return System.IO.Path.Combine(Home, ".steam", "steam");
            yield return System.IO.Path.Combine(Home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam");
        }
    }

    /// <summary>Steam's game recordings: userdata/&lt;account&gt;/gamerecordings (the first account that has one).</summary>
    public static string? SteamClipsFolder()
    {
        foreach (var root in SteamRoots().Where(Directory.Exists))
        {
            var userdata = System.IO.Path.Combine(root, "userdata");
            if (!Directory.Exists(userdata)) continue;
            try
            {
                foreach (var account in Directory.EnumerateDirectories(userdata))
                {
                    var rec = System.IO.Path.Combine(account, "gamerecordings");
                    if (Directory.Exists(rec)) return rec;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return null;
    }

    // ---------- game names ----------
    /// <summary>The game a recorder's file belongs to, or null when the recorder doesn't say.</summary>
    public static string? GameFor(RecorderImport import, string file)
    {
        var name = System.IO.Path.GetFileNameWithoutExtension(file);
        switch (import.Kind)
        {
            case RecorderKind.XboxGameBar:
                // "Game Name 2024-01-31 18-04-12" (Windows 11) or "Game Name 1_31_2024 6_04_12 PM" (older).
                var m = XboxName().Match(name);
                return m.Success ? Clean(m.Groups[1].Value) : null;
            case RecorderKind.SnippingTool:
            case RecorderKind.MacScreen:
                return null; // the desktop, not a game
            default:
                // NVIDIA files sit in a folder per game; OBS users often sort by game folders too.
                var rel = System.IO.Path.GetRelativePath(import.Path, System.IO.Path.GetDirectoryName(file) ?? import.Path);
                if (rel is "." || rel.StartsWith("..")) return null;
                var first = rel.Split(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar)[0];
                if (first.Equals("Desktop", StringComparison.OrdinalIgnoreCase) || first.Equals("Captures", StringComparison.OrdinalIgnoreCase))
                    return null;
                if (import.Kind == RecorderKind.Steam && first.StartsWith("Steam ", StringComparison.Ordinal)) return null;
                return Clean(first);
        }
    }

    private static string? Clean(string s)
    {
        s = s.Trim().TrimEnd('-', '_').Trim();
        return s.Length is > 0 and <= 40 ? s : null;
    }

    [GeneratedRegex(@"^(.+?)\s+(\d{4}-\d{2}-\d{2}\s+\d{2}-\d{2}-\d{2}|\d{1,2}_\d{1,2}_\d{4}\s+\d{1,2}_\d{2}_\d{2}(\s*[AP]M)?)", RegexOptions.IgnoreCase)]
    private static partial Regex XboxName();

    // ---------- Steam ----------
    [GeneratedRegex(@"^clip_(\d+)_(\d{8})_(\d{6})$")]
    private static partial Regex SteamClipName();

    /// <summary>The Steam game name for an app id, from the appmanifest in any Steam library folder.</summary>
    private static string? SteamGameName(string recordings, string appId)
    {
        // recordings = <steam>/userdata/<account>/gamerecordings
        var steam = Directory.GetParent(recordings)?.Parent?.Parent?.FullName;
        if (steam is null) return null;
        var libraries = new List<string> { System.IO.Path.Combine(steam, "steamapps") };
        try
        {
            var vdf = System.IO.Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            if (File.Exists(vdf))
                foreach (Match m in VdfPath().Matches(File.ReadAllText(vdf)))
                    libraries.Add(System.IO.Path.Combine(m.Groups[1].Value.Replace(@"\\", @"\"), "steamapps"));
            foreach (var lib in libraries.Distinct())
            {
                var acf = System.IO.Path.Combine(lib, $"appmanifest_{appId}.acf");
                if (!File.Exists(acf)) continue;
                var name = VdfName().Match(File.ReadAllText(acf));
                if (name.Success) return name.Groups[1].Value;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return null;
    }

    [GeneratedRegex("\"path\"\\s+\"([^\"]+)\"")]
    private static partial Regex VdfPath();
    [GeneratedRegex("\"name\"\\s+\"([^\"]+)\"")]
    private static partial Regex VdfName();

    public sealed record SteamClip(string Manifest, string OutputPath, string? Game, string Id = "");

    /// <summary>Steam clips not converted yet. Each becomes &lt;output&gt;/&lt;Game&gt;/&lt;Game&gt; YYYY-MM-DD HH-MM-SS.mp4.</summary>
    /// <param name="converted">Clip folders copied before; ones found already copied are added to it.</param>
    public static List<SteamClip> PendingSteamClips(string recordings, string output, ISet<string> converted)
    {
        var list = new List<SteamClip>();
        var clips = System.IO.Path.Combine(recordings, "clips");
        if (!Directory.Exists(clips)) return list;
        var names = new Dictionary<string, string?>();
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(clips))
            {
                var id = System.IO.Path.GetFileName(dir);
                var m = SteamClipName().Match(id);
                if (!m.Success || converted.Contains(id)) continue;
                var mpd = Directory.EnumerateFiles(dir, "session.mpd", SearchOption.AllDirectories).FirstOrDefault();
                if (mpd is null) continue;
                var app = m.Groups[1].Value;
                if (!names.TryGetValue(app, out var game)) names[app] = game = SteamGameName(recordings, app);
                var d = m.Groups[2].Value;
                var t = m.Groups[3].Value;
                var label = game is null ? "Steam " + app : SafeName(game);
                var file = $"{label} {d[..4]}-{d[4..6]}-{d[6..]} {t[..2]}-{t[2..4]}-{t[4..]}.mp4";
                var outPath = System.IO.Path.Combine(output, label, file);
                if (File.Exists(outPath)) converted.Add(id);
                else list.Add(new SteamClip(mpd, outPath, game, id));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Steam clips: {ex.Message}");
        }
        return list;
    }

    private static string SafeName(string s)
    {
        foreach (var c in System.IO.Path.GetInvalidFileNameChars().Concat(new[] { ':', '/', '\\', '?', '*', '"', '<', '>', '|' }))
            s = s.Replace(c, ' ');
        return s.Trim().Length == 0 ? "Steam" : s.Trim();
    }

    /// <summary>Copies one Steam clip's video and sound into an MP4 without re-encoding. Returns false if it failed.</summary>
    public static async Task<bool> ConvertSteamClipAsync(SteamClip clip, CancellationToken ct)
    {
        if (ToolLocator.Ffmpeg is not { } ffmpeg) return false;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(clip.OutputPath)!);
        var temp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cnema-steam-" + Guid.NewGuid().ToString("N") + ".mp4");
        try
        {
            var r = await ProcessRunner.RunAsync(ffmpeg, new[]
            {
                "-hide_banner", "-loglevel", "error", "-y", "-i", clip.Manifest, "-map", "0:v:0?", "-map", "0:a?",
                "-c", "copy", "-movflags", "+faststart", temp,
            }, ct: ct, workingDirectory: System.IO.Path.GetDirectoryName(clip.Manifest));
            if (r.ExitCode != 0 || !File.Exists(temp) || new FileInfo(temp).Length == 0)
            {
                Log.Warn($"Steam clip {clip.Manifest}: {r.StdErr.Trim()}");
                if (File.Exists(temp)) File.Delete(temp);
                return false;
            }
            File.Move(temp, clip.OutputPath, true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Log.Warn($"Steam clip {clip.Manifest}: {ex.Message}");
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            return false;
        }
    }
}
