using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Cnema.Models;

namespace Cnema.Services;

/// <summary>Grabs one representative frame per video with ffmpeg into the thumbnail cache.</summary>
public static class ThumbnailService
{
    public const int ThumbWidth = 400;

    /// <summary>Settings, Features, Smart thumbnails: use the frame the best moments pass picked.</summary>
    public static bool SmartThumbs { get; set; } = true;

    /// <summary>The frame to show: the one the person picked, else the smart pick, else 0 (Savefile chooses).</summary>
    public static long FrameFor(VideoEntry e) =>
        e.ThumbAtMs > 0 ? e.ThumbAtMs : SmartThumbs && e.Smart is { ThumbMs: > 0 } s ? s.ThumbMs : 0;

    public static string PathFor(VideoEntry e)
    {
        var at = FrameFor(e);
        var key = $"{e.Path}|{e.Size}|{e.ModifiedUtc.Ticks}" + (at > 0 ? $"|at{at}" : "");
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key)))[..20].ToLowerInvariant();
        return Path.Combine(AppPaths.ThumbDir, hash + ".jpg");
    }

    public static async Task<string?> EnsureAsync(VideoEntry e, CancellationToken ct)
    {
        var target = PathFor(e);
        if (File.Exists(target) && new FileInfo(target).Length > 0) return target;
        if (ToolLocator.Ffmpeg is not { } ffmpeg) return null;
        // Written beside it and moved in when complete: a scan cancelled mid-write must not leave
        // a broken thumbnail that every later scan would reuse.
        // Its own temp name: a scan and the smart-thumbnail pass can make the same thumbnail at once.
        var temp = target[..^4] + $".{Guid.NewGuid():N}.tmp.jpg";

        // ~12% in avoids black intros/fade-ins; clamp for very short clips. A frame the user picked
        // is taken as it is, without the "most representative of the next 12 frames" filter.
        var at = FrameFor(e);
        var picked = at > 0;
        var seekSec = picked ? at / 1000.0 : e.DurationMs > 0 ? Math.Min(e.DurationMs * 0.12 / 1000.0, 300) : 1.0;
        var ss = seekSec.ToString("0.###", CultureInfo.InvariantCulture);
        var filter = picked ? $"scale={ThumbWidth}:-2" : $"thumbnail=12,scale={ThumbWidth}:-2";

        try
        {
            foreach (var attempt in new[] { ss, "0" })
            {
                var r = await ProcessRunner.RunAsync(ffmpeg, new[]
                {
                    "-v", "error", "-ss", attempt, "-i", e.Path, "-frames:v", "1",
                    "-vf", filter, "-q:v", "4", "-y", temp,
                }, ct: ct);
                if (r.ExitCode == 0 && File.Exists(temp) && new FileInfo(temp).Length > 0)
                {
                    try { File.Move(temp, target, overwrite: true); }
                    catch (IOException) when (File.Exists(target)) { /* the other one got there first */ }
                    return target;
                }
            }
            return null;
        }
        finally { if (File.Exists(temp)) TryDelete(temp); } // also when cancelled half-way
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* nothing written, or still in use */ }
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
            // Once started it's finished and kept, even if the pointer has left the card by then: the next
            // hover shows it at once. Only the wait for a turn is given up (ct), so a quick sweep over many
            // cards doesn't leave a queue of filmstrips nobody is looking at.
            var r = await ProcessRunner.RunAsync(ffmpeg, args);
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
