using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Cnema.Models;

namespace Cnema.Services;

/// <summary>Grabs one representative frame per video with ffmpeg into the thumbnail cache.</summary>
public static class ThumbnailService
{
    public const int ThumbWidth = 400;

    public static string PathFor(VideoEntry e)
    {
        var key = $"{e.Path}|{e.Size}|{e.ModifiedUtc.Ticks}";
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key)))[..20].ToLowerInvariant();
        return Path.Combine(AppPaths.ThumbDir, hash + ".jpg");
    }

    public static async Task<string?> EnsureAsync(VideoEntry e, CancellationToken ct)
    {
        var target = PathFor(e);
        if (File.Exists(target)) return target;
        if (ToolLocator.Ffmpeg is not { } ffmpeg) return null;

        // ~12% in avoids black intros/fade-ins; clamp for very short clips.
        var seekSec = e.DurationMs > 0 ? Math.Min(e.DurationMs * 0.12 / 1000.0, 300) : 1.0;
        var ss = seekSec.ToString("0.###", CultureInfo.InvariantCulture);

        foreach (var attempt in new[] { ss, "0" })
        {
            var r = await ProcessRunner.RunAsync(ffmpeg, new[]
            {
                "-v", "error", "-ss", attempt, "-i", e.Path, "-frames:v", "1",
                "-vf", $"thumbnail=12,scale={ThumbWidth}:-2", "-q:v", "4", "-y", target,
            }, ct: ct);
            if (r.ExitCode == 0 && File.Exists(target)) return target;
        }
        return null;
    }
    public const int StripFrames = 10;
    private static readonly SemaphoreSlim StripGate = new(1, 1);

    public static string StripPathFor(VideoEntry e) => PathFor(e)[..^4] + "-strip.jpg";

    /// <summary>
    /// One wide image of <see cref="StripFrames"/> evenly spaced frames, for scrubbing over a card.
    /// Each frame is its own keyframe seek, so a two-hour file costs the same as a ten-second clip.
    /// </summary>
    public static async Task<string?> EnsureStripAsync(VideoEntry e, CancellationToken ct)
    {
        var target = StripPathFor(e);
        if (File.Exists(target)) return target;
        if (ToolLocator.Ffmpeg is not { } ffmpeg || e.DurationMs < 2000) return null;

        await StripGate.WaitAsync(ct);
        try
        {
            if (File.Exists(target)) return target;
            var args = new List<string> { "-v", "error" };
            var graph = new StringBuilder();
            for (var i = 0; i < StripFrames; i++)
            {
                var t = e.DurationMs / 1000.0 * (i + 0.5) / StripFrames;
                args.AddRange(new[] { "-ss", t.ToString("0.###", CultureInfo.InvariantCulture), "-i", e.Path });
                graph.Append(CultureInfo.InvariantCulture,
                    $"[{i}:v:0]scale=256:144:force_original_aspect_ratio=increase,crop=256:144,setsar=1[v{i}];");
            }
            for (var i = 0; i < StripFrames; i++) graph.Append(CultureInfo.InvariantCulture, $"[v{i}]");
            graph.Append(CultureInfo.InvariantCulture, $"hstack=inputs={StripFrames}[out]");
            var temp = target + ".tmp.jpg";
            args.AddRange(new[] { "-filter_complex", graph.ToString(), "-map", "[out]", "-frames:v", "1", "-q:v", "5", "-y", temp });
            var r = await ProcessRunner.RunAsync(ffmpeg, args, ct: ct);
            if (r.ExitCode != 0 || !File.Exists(temp))
            {
                try { File.Delete(temp); } catch { /* nothing written */ }
                return null;
            }
            File.Move(temp, target, overwrite: true);
            return target;
        }
        finally
        {
            StripGate.Release();
        }
    }
}
