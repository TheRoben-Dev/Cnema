using System.Text.RegularExpressions;

namespace Cnema.Services;

public sealed record CaptureDevices(IReadOnlyList<string> Screens, IReadOnlyList<string> Audio);

/// <summary>
/// Answers "what can this machine capture, and with which ffmpeg?". Everything OS-specific about
/// screen/audio capture discovery lives here; ClipService turns the answers into a command line.
/// </summary>
public static class CaptureProbe
{
    private static readonly object Gate = new();
    private static string? _ffmpeg;
    private static bool _ffmpegResolved;
    private static bool? _ddagrab;
    private static readonly Dictionary<string, bool> EncoderOk = new();

    /// <summary>The capture source ffmpeg must support on this OS.</summary>
    public static string RequiredInput =>
        OperatingSystem.IsMacOS() ? "avfoundation" : OperatingSystem.IsWindows() ? "gdigrab" : "x11grab";

    /// <summary>
    /// First ffmpeg (bundled, then system) that can capture the screen here. Checked rather than
    /// assumed, because a source run may find an ffmpeg built without the capture device.
    /// </summary>
    public static async Task<string?> FindCaptureFfmpegAsync()
    {
        lock (Gate) if (_ffmpegResolved) return _ffmpeg;
        string? found = null;
        foreach (var candidate in ToolLocator.FindAll("ffmpeg"))
        {
            var r = await RunQuiet(candidate, "-hide_banner", "-devices");
            if (r is not null && Regex.IsMatch(r.StdOut, $@"^\s*D[E ]?\s+{RequiredInput}\b", RegexOptions.Multiline))
            {
                found = candidate;
                break;
            }
        }
        lock (Gate) { _ffmpeg = found; _ffmpegResolved = true; }
        return found;
    }

    /// <summary>Windows: Desktop Duplication capture (GPU frames, far cheaper than gdigrab).</summary>
    public static async Task<bool> HasDdagrabAsync(string ffmpeg)
    {
        if (!OperatingSystem.IsWindows()) return false;
        if (_ddagrab is { } known) return known;
        var r = await RunQuiet(ffmpeg, "-hide_banner", "-filters");
        var has = r is not null && r.StdOut.Contains(" ddagrab ");
        _ddagrab = has;
        return has;
    }

    /// <summary>
    /// Picks the cheapest H.264 encoder that actually works on this machine by running a tiny
    /// test encode: a hardware encoder can be compiled in but unusable (no such GPU, old driver).
    /// </summary>
    public static async Task<string> PickEncoderAsync(string ffmpeg, bool allowHardware)
    {
        if (allowHardware)
        {
            string[] candidates = OperatingSystem.IsMacOS() ? new[] { "h264_videotoolbox" }
                : OperatingSystem.IsWindows() ? new[] { "h264_nvenc", "h264_amf", "h264_qsv" }
                : new[] { "h264_nvenc" };
            foreach (var enc in candidates)
                if (await EncoderWorksAsync(ffmpeg, enc)) return enc;
        }
        return "libx264";
    }

    private static async Task<bool> EncoderWorksAsync(string ffmpeg, string encoder)
    {
        lock (Gate) if (EncoderOk.TryGetValue(encoder, out var hit)) return hit;
        var r = await RunQuiet(ffmpeg, "-hide_banner", "-loglevel", "error", "-f", "lavfi",
            "-i", "color=black:s=640x360:r=30", "-frames:v", "5", "-pix_fmt", "nv12",
            "-c:v", encoder, "-f", "null", "-");
        var ok = r is { ExitCode: 0 };
        lock (Gate) EncoderOk[encoder] = ok;
        return ok;
    }

    public static async Task<CaptureDevices> ListDevicesAsync()
    {
        var ffmpeg = await FindCaptureFfmpegAsync();
        try
        {
            if (OperatingSystem.IsMacOS() && ffmpeg is not null)
            {
                var r = await RunQuiet(ffmpeg, "-hide_banner", "-f", "avfoundation", "-list_devices", "true", "-i", "");
                return ParseAvFoundation(r?.StdErr ?? "");
            }
            if (OperatingSystem.IsWindows() && ffmpeg is not null)
            {
                var r = await RunQuiet(ffmpeg, "-hide_banner", "-list_devices", "true", "-f", "dshow", "-i", "dummy");
                return new CaptureDevices(new[] { "Primary display" }, ParseDshowAudio(r?.StdErr ?? ""));
            }
            if (OperatingSystem.IsLinux())
            {
                var audio = new List<string>();
                var r = await RunQuiet("pactl", "list", "short", "sources");
                if (r is { ExitCode: 0 })
                    audio.AddRange(r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                        .Select(l => l.Split('\t')).Where(c => c.Length > 1).Select(c => c[1]));
                return new CaptureDevices(new[] { "Whole desktop" }, audio);
            }
        }
        catch { /* fall through to "nothing found" */ }
        return new CaptureDevices(new[] { "Primary display" }, Array.Empty<string>());
    }

    /// <summary>
    /// avfoundation prints "[idx] name" lines under a video and an audio heading. Screens show up
    /// as video devices named "Capture screen N".
    /// </summary>
    public static CaptureDevices ParseAvFoundation(string stderr)
    {
        var screens = new List<string>();
        var audio = new List<string>();
        var inAudio = false;
        foreach (var line in stderr.Split('\n'))
        {
            if (line.Contains("AVFoundation audio devices")) { inAudio = true; continue; }
            if (line.Contains("AVFoundation video devices")) { inAudio = false; continue; }
            var m = Regex.Match(line, @"\]\s*\[(\d+)\]\s*(.+?)\s*$");
            if (!m.Success) continue;
            var name = m.Groups[2].Value;
            if (inAudio) audio.Add(name);
            else if (name.StartsWith("Capture screen", StringComparison.OrdinalIgnoreCase)) screens.Add(name);
        }
        if (screens.Count == 0) screens.Add("Capture screen 0");
        return new CaptureDevices(screens, audio);
    }

    public static IReadOnlyList<string> ParseDshowAudio(string stderr) =>
        Regex.Matches(stderr, "\"([^\"]+)\"\\s*\\(audio\\)").Select(m => m.Groups[1].Value).Distinct().ToList();

    private static async Task<ProcessResult?> RunQuiet(string file, params string[] args)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            return await ProcessRunner.RunAsync(file, args, null, cts.Token);
        }
        catch
        {
            return null;
        }
    }
}
