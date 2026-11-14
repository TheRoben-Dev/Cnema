using System.Globalization;

namespace Cnema.Services;

public enum TrimMode
{
    /// <summary>Stream copy: instant and lossless, but the start snaps to the previous keyframe.</summary>
    Lossless,
    /// <summary>Re-encode (H.264/AAC): frame-accurate, slower, small quality loss.</summary>
    Precise,
}

/// <summary>Ready-made exports for posting a clip somewhere.</summary>
public enum SharePreset
{
    /// <summary>H.264/AAC MP4 that fits in 10 MB (Discord's free upload limit).</summary>
    Discord10,
    /// <summary>H.264/AAC MP4 that fits in 50 MB (Discord Nitro Basic).</summary>
    Discord50,
    /// <summary>Centre-cropped 9:16 MP4 for Shorts, Reels and TikTok.</summary>
    Vertical,
    /// <summary>Animated GIF, 15 fps, 480 px wide, at most 20 seconds.</summary>
    Gif,
}

public static class TrimService
{
    public const int GifMaxMs = 20_000;

    public static (string Suffix, string Extension, string Label) Describe(SharePreset p) => p switch
    {
        SharePreset.Discord10 => ("discord 10MB", ".mp4", "10 MB for Discord"),
        SharePreset.Discord50 => ("discord 50MB", ".mp4", "50 MB for Discord"),
        SharePreset.Vertical => ("vertical", ".mp4", "Vertical 9:16"),
        _ => ("gif", ".gif", "GIF"),
    };

    /// <summary>Picks resolution, frame rate and bitrates so <paramref name="durationMs"/> fits in the cap.</summary>
    internal static (int Height, int Fps, int VideoKbps, int AudioKbps) PlanForSize(long capBytes, long durationMs)
    {
        var seconds = Math.Max(1, durationMs / 1000.0);
        // 6% headroom for the container and for the encoder missing its target slightly.
        var totalKbps = capBytes * 8 * 0.94 / seconds / 1000;
        var audio = totalKbps > 1500 ? 128 : totalKbps > 500 ? 96 : 64;
        var video = (int)Math.Clamp(totalKbps - audio, 80, 12000);
        var (height, fps) = video switch
        {
            >= 5000 => (1080, 60),
            >= 2500 => (1080, 30),
            >= 1200 => (720, 30),
            >= 500 => (480, 30),
            _ => (360, 24),
        };
        return (height, fps, video, audio);
    }

    public static async Task ShareAsync(
        string source, string output, long inMs, long outMs, SharePreset preset,
        IProgress<double>? progress, CancellationToken ct, IReadOnlyList<int>? audio = null)
    {
        // Shared files get one audio track: the chosen one, or the chosen ones mixed together,
        // because Discord and phones only ever play the first track.
        string[] AudioMap() => audio switch
        {
            null => new[] { "-map", "0:a:0?" },
            { Count: 0 } => new[] { "-an" },
            { Count: 1 } => new[] { "-map", $"0:a:{audio[0]}?" },
            _ => new[]
            {
                "-filter_complex",
                string.Concat(audio.Select(i => $"[0:a:{i}]")) + $"amix=inputs={audio.Count}:duration=longest:normalize=0[mixed]",
                "-map", "[mixed]",
            },
        };

        var ffmpeg = ToolLocator.Ffmpeg ?? throw new InvalidOperationException("ffmpeg not found.");
        if (outMs <= inMs) throw new ArgumentException("Out point must be after in point.");
        if (preset == SharePreset.Gif) outMs = Math.Min(outMs, inMs + GifMaxMs);
        var durationMs = outMs - inMs;
        string S(long ms) => (ms / 1000.0).ToString("0.000", CultureInfo.InvariantCulture);
        var input = new[] { "-hide_banner", "-v", "error", "-nostats", "-progress", "pipe:1",
                            "-ss", S(inMs), "-i", source, "-t", S(durationMs) };

        var temp = TempFor(output);
        async Task Run(IEnumerable<string> tail, double from, double span)
        {
            var r = await ProcessRunner.RunAsync(ffmpeg, input.Concat(tail).ToList(), line =>
            {
                if (line.StartsWith("out_time_us=", StringComparison.Ordinal) &&
                    long.TryParse(line.AsSpan(12), out var us) && durationMs > 0)
                    progress?.Report(from + span * Math.Clamp(us / 1000.0 / durationMs, 0, 1));
            }, ct);
            if (r.ExitCode == 0) return;
            try { File.Delete(temp); } catch { /* ignore */ }
            var msg = r.StdErr.Trim();
            throw new InvalidOperationException(string.IsNullOrEmpty(msg) ? $"ffmpeg exited with {r.ExitCode}" : msg);
        }

        using var cleanup = ct.Register(() => { try { File.Delete(temp); } catch { /* temp dir */ } });
        switch (preset)
        {
            case SharePreset.Gif:
                await Run(new[]
                {
                    "-an", "-filter_complex",
                    "[0:v:0]fps=15,scale=480:-2:flags=lanczos,split[a][b];[a]palettegen=stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=4",
                    "-loop", "0", "-y", temp,
                }, 0, 1);
                break;

            case SharePreset.Vertical:
                await Run(new[]
                {
                    "-map", "0:v:0",
                    // The middle 9:16 slice of the picture, at most 1080x1920.
                    "-vf", "crop=trunc(ih*9/32)*2:ih,scale=-2:'min(1920,ih)'",
                    "-c:v", "libx264", "-preset", "medium", "-crf", "20", "-pix_fmt", "yuv420p",
                }.Concat(AudioMap()).Concat(new[] { "-c:a", "aac", "-b:a", "160k", "-movflags", "+faststart", "-y", temp }), 0, 1);
                break;

            default:
                var cap = (preset == SharePreset.Discord10 ? 10L : 50L) * 1000 * 1000;
                var (height, fps, video, audioKbps) = PlanForSize(cap, durationMs);
                var log = Path.Combine(Path.GetTempPath(), $"cnema-pass-{Environment.ProcessId}-{Environment.TickCount64}");
                var common = new[]
                {
                    "-map", "0:v:0",
                    "-vf", $"scale=-2:'min({height},ih)',fps='min({fps},source_fps)'",
                    "-c:v", "libx264", "-preset", "medium", "-pix_fmt", "yuv420p",
                    "-b:v", $"{video}k", "-maxrate", $"{video * 3 / 2}k", "-bufsize", $"{video * 2}k",
                    "-passlogfile", log,
                };
                try
                {
                    // Two passes: the only way to land on a file size instead of a quality level.
                    await Run(common.Concat(new[] { "-pass", "1", "-an", "-f", "null", "-y", OperatingSystem.IsWindows() ? "NUL" : "/dev/null" }), 0, 0.45);
                    await Run(common.Concat(new[] { "-pass", "2" }).Concat(AudioMap()).Concat(new[]
                    {
                        "-c:a", "aac", "-b:a", $"{audioKbps}k", "-ac", "2", "-movflags", "+faststart", "-y", temp,
                    }), 0.45, 0.55);
                }
                finally
                {
                    foreach (var f in Directory.EnumerateFiles(Path.GetTempPath(), Path.GetFileName(log) + "*"))
                        try { File.Delete(f); } catch { /* temp */ }
                }
                break;
        }
        File.Move(temp, output, overwrite: true);
        progress?.Report(1);
    }

    public static string SuggestedExtension(string source, TrimMode mode) =>
        mode == TrimMode.Precise ? ".mp4" : Path.GetExtension(source);

    public static async Task ExportAsync(
        string source, string output, long inMs, long outMs, TrimMode mode,
        IProgress<double>? progress, CancellationToken ct, IReadOnlyList<int>? audio = null,
        IReadOnlyList<Chapter>? chapters = null)
    {
        var ffmpeg = ToolLocator.Ffmpeg ?? throw new InvalidOperationException(
            "ffmpeg not found. Install it (macOS: brew install ffmpeg) and restart Cnema.");
        if (outMs <= inMs) throw new ArgumentException("Out point must be after in point.");

        var durationMs = outMs - inMs;
        string S(long ms) => (ms / 1000.0).ToString("0.000", CultureInfo.InvariantCulture);

        // Chapters (from session bookmarks) come in as a second input, right after the source so
        // that -t stays an output option.
        var meta = chapters is { Count: > 1 } ? Chapters.WriteMetadataFile(chapters) : null;
        var args = new List<string> { "-hide_banner", "-v", "error", "-nostats", "-progress", "pipe:1",
                                      "-ss", S(inMs), "-i", source };
        if (meta is not null) args.AddRange(new[] { "-i", meta });
        args.AddRange(new[] { "-t", S(durationMs), "-map", "0:v:0?" });
        if (meta is not null) args.AddRange(new[] { "-map_chapters", "1" });
        // audio: null keeps every track; otherwise only the listed ones (none = silent).
        if (audio is null) args.AddRange(new[] { "-map", "0:a?" });
        else foreach (var i in audio) args.AddRange(new[] { "-map", $"0:a:{i}?" });
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
        // Written outside the library and moved in when complete, so the library never scans half a file.
        var temp = TempFor(output);
        args.AddRange(new[] { "-y", temp });

        using var cleanup = ct.Register(() => { try { File.Delete(temp); } catch { /* still open; temp dir */ } });
        ProcessResult r;
        try
        {
            r = await ProcessRunner.RunAsync(ffmpeg, args, line =>
            {
                // -progress emits key=value lines; out_time_us is the encoded position.
                if (line.StartsWith("out_time_us=", StringComparison.Ordinal) &&
                    long.TryParse(line.AsSpan(12), out var us) && durationMs > 0)
                    progress?.Report(Math.Clamp(us / 1000.0 / durationMs, 0, 1));
            }, ct);
        }
        finally
        {
            if (meta is not null) try { File.Delete(meta); } catch { /* temp file */ }
        }

        if (r.ExitCode != 0)
        {
            try { File.Delete(temp); } catch { /* ignore */ }
            var msg = r.StdErr.Trim();
            throw new InvalidOperationException(string.IsNullOrEmpty(msg) ? $"ffmpeg exited with {r.ExitCode}" : msg);
        }
        File.Move(temp, output, overwrite: true);
        progress?.Report(1);
    }

    /// <summary>
    /// Cuts several parts out of one video and joins them, in the order given, into one file.
    /// Each part is exported exactly like a single trim, then the parts are joined without re-encoding.
    /// </summary>
    public static async Task ExportSegmentsAsync(
        string source, string output, IReadOnlyList<(long In, long Out)> parts, TrimMode mode,
        IProgress<double>? progress, CancellationToken ct, IReadOnlyList<int>? audio = null)
    {
        var ffmpeg = ToolLocator.Ffmpeg ?? throw new InvalidOperationException("ffmpeg not found.");
        if (parts.Count == 0) throw new ArgumentException("Nothing to export.");
        var ext = Path.GetExtension(output);
        var files = new List<string>();
        var list = Path.Combine(Path.GetTempPath(), $"cnema-join-{Guid.NewGuid():N}.txt");
        var joined = TempFor(output);
        try
        {
            for (var i = 0; i < parts.Count; i++)
            {
                var piece = Path.Combine(Path.GetTempPath(), $"cnema-part-{Guid.NewGuid():N}{ext}");
                files.Add(piece);
                var index = i;
                var each = new Progress<double>(p => progress?.Report((index + p) / parts.Count * 0.95));
                await ExportAsync(source, piece, parts[i].In, parts[i].Out, mode, each, ct, audio);
            }
            await File.WriteAllLinesAsync(list, files.Select(f => $"file '{f.Replace("'", "'\\''")}'"), ct);
            var args = new List<string> { "-hide_banner", "-v", "error", "-f", "concat", "-safe", "0", "-i", list,
                                          "-map", "0", "-c", "copy" };
            if (ext.Equals(".mp4", StringComparison.OrdinalIgnoreCase) || ext.Equals(".mov", StringComparison.OrdinalIgnoreCase))
                args.AddRange(new[] { "-movflags", "+faststart" });
            args.AddRange(new[] { "-y", joined });
            var r = await ProcessRunner.RunAsync(ffmpeg, args, ct: ct);
            if (r.ExitCode != 0)
            {
                var msg = r.StdErr.Trim();
                throw new InvalidOperationException(string.IsNullOrEmpty(msg) ? $"ffmpeg exited with {r.ExitCode}" : msg);
            }
            File.Move(joined, output, overwrite: true);
            progress?.Report(1);
        }
        finally
        {
            foreach (var f in files.Append(list).Append(joined))
                try { File.Delete(f); } catch { /* temp */ }
        }
    }

    private static string TempFor(string output) =>
        Path.Combine(Path.GetTempPath(), $"cnema-export-{Guid.NewGuid():N}{Path.GetExtension(output)}");
}
