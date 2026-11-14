using System.Text.RegularExpressions;

namespace Cnema.Services;

public sealed record CaptureDevices(IReadOnlyList<string> Screens, IReadOnlyList<string> Audio)
{
    /// <summary>Webcams and other video devices that are not screens, as Settings lists them.</summary>
    public IReadOnlyList<string> Cameras { get; init; } = Array.Empty<string>();
}

/// <summary>A video format the clipping service can record in, and the encoder that does it here.</summary>
public sealed record CodecOption(string Codec, string Encoder, bool Hardware)
{
    public string Label => Codec switch
    {
        "hevc" => T("HEVC / H.265 · half the size"),
        "av1" => T("AV1 · smallest files"),
        _ => Hardware ? T("H.264 · plays everywhere") : T("H.264 (software) · plays everywhere"),
    };
    public override string ToString() => Label;
}

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

    /// <summary>Hardware encoders worth trying per codec, best first. Software AV1/HEVC are left out:
    /// far too heavy for an always-on buffer next to a game.</summary>
    private static string[] HardwareCandidates(string codec) => codec switch
    {
        "h264" => OperatingSystem.IsMacOS() ? new[] { "h264_videotoolbox" }
            : OperatingSystem.IsWindows() ? new[] { "h264_nvenc", "h264_amf", "h264_qsv" } : new[] { "h264_nvenc" },
        "hevc" => OperatingSystem.IsMacOS() ? new[] { "hevc_videotoolbox" }
            : OperatingSystem.IsWindows() ? new[] { "hevc_nvenc", "hevc_amf", "hevc_qsv" } : new[] { "hevc_nvenc" },
        // No Mac (M1 to M4) encodes AV1 in hardware. NVENC on RTX 40+, AMD RDNA3+, Intel Arc.
        "av1" => OperatingSystem.IsMacOS() ? Array.Empty<string>()
            : OperatingSystem.IsWindows() ? new[] { "av1_nvenc", "av1_amf", "av1_qsv" } : new[] { "av1_nvenc" },
        _ => Array.Empty<string>(),
    };

    /// <summary>
    /// Every codec this machine can record in, each with the encoder that will be used. Found by a
    /// tiny test encode, since an encoder can be compiled in but unusable (no such GPU, old driver).
    /// H.264 is always present: x264 is the fallback.
    /// </summary>
    public static async Task<IReadOnlyList<CodecOption>> ListCodecsAsync(string ffmpeg, bool allowHardware = true)
    {
        var options = new List<CodecOption>();
        foreach (var codec in new[] { "h264", "hevc", "av1" })
        {
            if (!allowHardware) break;
            foreach (var enc in HardwareCandidates(codec))
                if (await EncoderWorksAsync(ffmpeg, enc))
                {
                    options.Add(new CodecOption(codec, enc, true));
                    break;
                }
        }
        if (!options.Any(o => o.Codec == "h264")) options.Insert(0, new CodecOption("h264", "libx264", false));
        return options;
    }

    /// <summary>The encoder for the chosen codec, falling back to H.264 when this machine can't do it.</summary>
    public static async Task<CodecOption> PickAsync(string ffmpeg, bool allowHardware, string? codec)
    {
        var options = await ListCodecsAsync(ffmpeg, allowHardware);
        return options.FirstOrDefault(o => o.Codec == codec) ?? options.First(o => o.Codec == "h264");
    }

    public static async Task<string> PickEncoderAsync(string ffmpeg, bool allowHardware) =>
        (await PickAsync(ffmpeg, allowHardware, "h264")).Encoder;

    // ---------- macOS system audio ----------
    public const string SystemAudio = "System audio (everything you hear)";

    /// <summary>The ScreenCaptureKit helper bundled with the macOS app, if this Mac can run it.</summary>
    public static string? SystemAudioHelper =>
        OperatingSystem.IsMacOSVersionAtLeast(13) ? ToolLocator.Find("cnema-audio") : null;

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
                var found = ParseAvFoundation(r?.StdErr ?? "");
                if (SystemAudioHelper is null) return found;
                return found with { Audio = found.Audio.Prepend(SystemAudio).ToList() };
            }
            if (OperatingSystem.IsWindows() && ffmpeg is not null)
            {
                var r = await RunQuiet(ffmpeg, "-hide_banner", "-list_devices", "true", "-f", "dshow", "-i", "dummy");
                return new CaptureDevices(new[] { "Primary display" }, ParseDshowAudio(r?.StdErr ?? ""))
                {
                    Cameras = ParseDshowVideo(r?.StdErr ?? ""),
                };
            }
            if (OperatingSystem.IsLinux())
            {
                var audio = new List<string>();
                var r = await RunQuiet("pactl", "list", "short", "sources");
                if (r is { ExitCode: 0 })
                    audio.AddRange(r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                        .Select(l => l.Split('\t')).Where(c => c.Length > 1).Select(c => c[1]));
                return new CaptureDevices(new[] { "Whole desktop" }, audio) { Cameras = ListV4l2Cameras() };
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
        var cameras = new List<string>();
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
            else cameras.Add(name);
        }
        if (screens.Count == 0) screens.Add("Capture screen 0");
        return new CaptureDevices(screens, audio) { Cameras = cameras };
    }

    public static IReadOnlyList<string> ParseDshowVideo(string stderr) =>
        Regex.Matches(stderr, "\"([^\"]+)\"\\s*\\(video\\)").Select(m => m.Groups[1].Value).Distinct().ToList();

    /// <summary>
    /// Video4Linux capture nodes as "Name (/dev/videoN)". A webcam usually has a second node for
    /// metadata; only the first (index 0) of each device delivers pictures.
    /// </summary>
    private static IReadOnlyList<string> ListV4l2Cameras()
    {
        var found = new List<string>();
        const string sys = "/sys/class/video4linux";
        if (!Directory.Exists(sys)) return found;
        foreach (var dir in Directory.GetDirectories(sys, "video*").OrderBy(d => d.Length).ThenBy(d => d, StringComparer.Ordinal))
        {
            try
            {
                var index = File.Exists(Path.Combine(dir, "index")) ? File.ReadAllText(Path.Combine(dir, "index")).Trim() : "0";
                if (index != "0") continue;
                var name = File.ReadAllText(Path.Combine(dir, "name")).Trim();
                found.Add($"{(name.Length > 0 ? name : "Camera")} (/dev/{Path.GetFileName(dir)})");
            }
            catch { /* unreadable node: skip */ }
        }
        return found;
    }

    /// <summary>The device ffmpeg opens for a camera as Settings lists it ("Name (/dev/video0)" on Linux).</summary>
    public static string CameraInput(string camera)
    {
        var m = Regex.Match(camera, @"\((/dev/video\d+)\)$");
        return m.Success ? m.Groups[1].Value : camera;
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
