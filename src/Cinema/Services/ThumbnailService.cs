using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Cinema.Models;

namespace Cinema.Services;

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
}
