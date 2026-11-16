using System.Globalization;
using Cnema.Models;

namespace Cnema.Services;

/// <summary>
/// Make room: big videos nobody watches any more are encoded again as HEVC at the same look, in place.
/// The new file is checked (same length, every audio track still there, clearly smaller) before it takes
/// the old one's place; the original goes to Savefile's bin, so it can be put back for 24 hours. The
/// path stays the same, so favourites, tags, friends, notes, edits and backup marks all stay with it.
/// </summary>
public static class ShrinkService
{
    /// <summary>Codecs that are already compact: nothing to win by encoding them again.</summary>
    private static readonly string[] Compact = { "hevc", "h265", "av1", "vp9" };

    public const long MinBytes = 150L * 1024 * 1024;

    /// <summary>What's left of an H.264 video at the same look as HEVC, roughly (only shown as "about").</summary>
    public const double Guess = 0.45;

    /// <summary>Big, not a favourite, at least a month old, not watched for three months (or ever), not yet compact.</summary>
    public static bool IsCandidate(VideoEntry e, DateTime nowUtc) =>
        e.Size >= MinBytes && !e.Favorite
        && (nowUtc - e.ModifiedUtc).TotalDays >= 30
        && (e.LastPlayedUtc is null || (nowUtc - e.LastPlayedUtc.Value).TotalDays >= 90)
        && !Compact.Any(c => (e.VideoCodec ?? "").Contains(c, StringComparison.OrdinalIgnoreCase))
        && !BinService.IsBinPath(e.Path);

    public sealed record Encoder(string Name, string[] Args);

    private static Encoder? _encoder;
    private static bool _looked;

    /// <summary>The HEVC encoder to use on this machine, or null when there's none.</summary>
    public static async Task<Encoder?> PickAsync(string ffmpeg)
    {
        if (_looked) return _encoder;
        _looked = true;
        // Constant quality, never a bitrate: the point is the same look, not a target size.
        var candidates = OperatingSystem.IsMacOS()
            ? new[] { new Encoder("hevc_videotoolbox", new[] { "-c:v", "hevc_videotoolbox", "-q:v", "62" }) }
            : new[]
            {
                new Encoder("libx265", new[] { "-c:v", "libx265", "-crf", "23", "-preset", "medium", "-x265-params", "log-level=error" }),
                new Encoder("hevc_nvenc", new[] { "-c:v", "hevc_nvenc", "-rc", "vbr", "-cq", "26", "-b:v", "0", "-preset", "p5" }),
            };
        foreach (var c in candidates)
            if (await CaptureProbe.EncoderWorksAsync(ffmpeg, c.Name)) return _encoder = c;
        return null;
    }

    /// <summary>Encodes one video next to itself, checks it, and swaps it in. Returns the new size.</summary>
    public static async Task<long> ShrinkAsync(VideoEntry e, Encoder enc, Func<VideoEntry, BinItem> toBin, Action<BinItem> undo, Action<double>? progress, CancellationToken ct)
    {
        if (ToolLocator.Ffmpeg is not { } ffmpeg) throw new InvalidOperationException(T("ffmpeg not found"));
        var src = e.Path;
        var info = new FileInfo(src);
        if (!info.Exists) throw new FileNotFoundException(T("The file isn't there any more."), src);
        var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(src))!);
        if (drive.AvailableFreeSpace < info.Length + 512L * 1024 * 1024)
            throw new IOException(T("Not enough free space on that drive to make the smaller copy."));
        var before = await MediaProbe.InfoAsync(src, ct) ?? throw new InvalidOperationException(T("Couldn't read the video."));

        var ext = Path.GetExtension(src);
        var temp = Path.Combine(Path.GetDirectoryName(src)!, $".savefile-smaller-{Guid.NewGuid():N}{ext}");
        var args = new List<string> { "-hide_banner", "-nostdin", "-y", "-loglevel", "error", "-progress", "pipe:1", "-i", src,
            "-map", "0:v:0", "-map", "0:a?", "-map_metadata", "0", "-map_chapters", "0" };
        args.AddRange(enc.Args);
        args.AddRange(new[] { "-c:a", "copy" });
        if (ext.Equals(".mp4", StringComparison.OrdinalIgnoreCase) || ext.Equals(".mov", StringComparison.OrdinalIgnoreCase))
            args.AddRange(new[] { "-tag:v", "hvc1", "-movflags", "+faststart" });
        args.Add(temp);
        var total = Math.Max(1, before.DurationMs);
        try
        {
            var r = await ProcessRunner.RunAsync(ffmpeg, args, line =>
            {
                if (line.StartsWith("out_time_us=", StringComparison.Ordinal)
                    && long.TryParse(line.AsSpan(12), NumberStyles.Integer, CultureInfo.InvariantCulture, out var us))
                    progress?.Invoke(Math.Clamp(us / 1000.0 / total, 0, 1));
            }, ct, background: true);
            if (r.ExitCode != 0 || !File.Exists(temp))
                throw new InvalidOperationException(r.StdErr.Trim().Split('\n').LastOrDefault() ?? "ffmpeg failed");

            // The checks: same length, all the sound, and worth it.
            var after = await MediaProbe.InfoAsync(temp, ct) ?? throw new InvalidOperationException(T("The smaller copy couldn't be read."));
            if (Math.Abs(after.DurationMs - before.DurationMs) > Math.Max(1500, before.DurationMs / 100))
                throw new InvalidOperationException(T("The smaller copy came out a different length, so the original was kept."));
            if (after.AudioStreams != before.AudioStreams)
                throw new InvalidOperationException(T("The smaller copy lost a sound track, so the original was kept."));
            var newSize = new FileInfo(temp).Length;
            if (newSize > info.Length * 0.85)
                throw new SkipException(T("Already about as small as it gets; kept as it was."));

            // Same dates as before: the library sorts and remembers by them.
            File.SetCreationTimeUtc(temp, info.CreationTimeUtc);
            File.SetLastWriteTimeUtc(temp, info.LastWriteTimeUtc);
            var binned = toBin(e);     // the original, into the bin for 24 hours
            try { File.Move(temp, src); } // the smaller one takes its place
            catch
            {
                undo(binned);          // never leave the video missing
                throw;
            }
            File.SetLastWriteTimeUtc(src, info.LastWriteTimeUtc);
            return newSize;
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* left for the next look */ }
        }
    }

    /// <summary>Not a failure: the video is fine as it is.</summary>
    public sealed class SkipException(string message) : Exception(message);
}
