using System.Globalization;

namespace Cnema.Services;

public enum TrimMode
{
    /// <summary>Stream copy: instant and lossless, but the start snaps to the previous keyframe.</summary>
    Lossless,
    /// <summary>Re-encode (H.264/AAC): frame-accurate, slower, small quality loss.</summary>
    Precise,
}

public static class TrimService
{
    public static string SuggestedExtension(string source, TrimMode mode) =>
        mode == TrimMode.Precise ? ".mp4" : Path.GetExtension(source);

    public static async Task ExportAsync(
        string source, string output, long inMs, long outMs, TrimMode mode,
        IProgress<double>? progress, CancellationToken ct)
    {
        var ffmpeg = ToolLocator.Ffmpeg ?? throw new InvalidOperationException(
            "ffmpeg not found. Install it (macOS: brew install ffmpeg) and restart Cnema.");
        if (outMs <= inMs) throw new ArgumentException("Out point must be after in point.");

        var durationMs = outMs - inMs;
        string S(long ms) => (ms / 1000.0).ToString("0.000", CultureInfo.InvariantCulture);

        var args = new List<string> { "-hide_banner", "-v", "error", "-nostats", "-progress", "pipe:1",
                                      "-ss", S(inMs), "-i", source, "-t", S(durationMs),
                                      "-map", "0:v:0?", "-map", "0:a?" };
        if (mode == TrimMode.Lossless)
        {
            args.AddRange(new[] { "-c", "copy", "-avoid_negative_ts", "make_zero" });
        }
        else
        {
            args.AddRange(new[] { "-c:v", "libx264", "-preset", "medium", "-crf", "18", "-pix_fmt", "yuv420p",
                                  "-c:a", "aac", "-b:a", "192k" });
        }
        if (output.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ||
            output.EndsWith(".mov", StringComparison.OrdinalIgnoreCase))
            args.AddRange(new[] { "-movflags", "+faststart" });
        args.AddRange(new[] { "-y", output });

        var r = await ProcessRunner.RunAsync(ffmpeg, args, line =>
        {
            // -progress emits key=value lines; out_time_us is the encoded position.
            if (line.StartsWith("out_time_us=", StringComparison.Ordinal) &&
                long.TryParse(line.AsSpan(12), out var us) && durationMs > 0)
                progress?.Report(Math.Clamp(us / 1000.0 / durationMs, 0, 1));
        }, ct);

        if (r.ExitCode != 0)
        {
            try { File.Delete(output); } catch { /* ignore */ }
            var msg = r.StdErr.Trim();
            throw new InvalidOperationException(string.IsNullOrEmpty(msg) ? $"ffmpeg exited with {r.ExitCode}" : msg);
        }
        progress?.Report(1);
    }
}
