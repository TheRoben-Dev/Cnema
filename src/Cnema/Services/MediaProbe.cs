using System.Globalization;
using System.Text.Json;
using Cnema.Models;
using LibVLCSharp.Shared;

namespace Cnema.Services;

/// <summary>
/// Reads duration / resolution / codecs. ffprobe when one is installed, otherwise the bundled ffmpeg's
/// own description of the file (ffprobe is not shipped, it would add ~70 MB), libVLC as the last resort.
/// </summary>
public static class MediaProbe
{
    public static async Task ProbeAsync(VideoEntry e, CancellationToken ct)
    {
        if (ToolLocator.Ffprobe is { } ffprobe && await TryFfprobe(ffprobe, e, ct)) return;
        if (ToolLocator.Ffmpeg is { } ffmpeg)
        {
            // A file ffmpeg cannot read is usually still being written. Do not hand it to libVLC:
            // parsing a growing file there has crashed the whole process.
            await TryFfmpeg(ffmpeg, e, ct);
            return;
        }
        await TryLibVlc(e);
    }

    private static async Task<bool> TryFfprobe(string ffprobe, VideoEntry e, CancellationToken ct)
    {
        try
        {
            var r = await ProcessRunner.RunAsync(ffprobe,
                new[] { "-v", "error", "-print_format", "json", "-show_format", "-show_streams", e.Path }, ct: ct);
            if (r.ExitCode != 0) return false;

            using var doc = JsonDocument.Parse(r.StdOut);
            var root = doc.RootElement;
            if (root.TryGetProperty("format", out var fmt) && fmt.TryGetProperty("duration", out var d) &&
                double.TryParse(d.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var secs))
                e.DurationMs = (long)(secs * 1000);

            if (root.TryGetProperty("streams", out var streams))
            {
                foreach (var s in streams.EnumerateArray())
                {
                    var type = s.TryGetProperty("codec_type", out var t) ? t.GetString() : null;
                    var codec = s.TryGetProperty("codec_name", out var c) ? c.GetString() : null;
                    var isCover = s.TryGetProperty("disposition", out var disp) &&
                                  disp.TryGetProperty("attached_pic", out var ap) && ap.GetInt32() == 1;
                    if (type == "video" && e.VideoCodec is null && !isCover)
                    {
                        e.VideoCodec = codec;
                        e.Width = s.TryGetProperty("width", out var w) ? w.GetInt32() : 0;
                        e.Height = s.TryGetProperty("height", out var h) ? h.GetInt32() : 0;
                        e.FrameRate = ParseRate(s.TryGetProperty("avg_frame_rate", out var fr) ? fr.GetString() : null);
                    }
                    else if (type == "audio" && e.AudioCodec is null)
                    {
                        e.AudioCodec = codec;
                    }
                }
            }
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch { return false; }
    }

    private static readonly System.Text.RegularExpressions.Regex DurationRx =
        new(@"Duration: (\d+):(\d\d):(\d\d(?:\.\d+)?)", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex StreamRx =
        new(@"^\s*Stream #\d+:\d+[^:]*: (Video|Audio): (\w+)(.*)$", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex SizeRx =
        new(@", (\d{2,5})x(\d{2,5})(?:[ ,\[]|$)", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex FpsRx =
        new(@", ([\d.]+) fps", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>"ffmpeg -i file" with no output prints the same facts ffprobe would, on stderr.</summary>
    private static async Task<bool> TryFfmpeg(string ffmpeg, VideoEntry e, CancellationToken ct)
    {
        try
        {
            var r = await ProcessRunner.RunAsync(ffmpeg, new[] { "-hide_banner", "-i", e.Path }, ct: ct);
            return ParseFfmpegInfo(r.StdErr, e);
        }
        catch (OperationCanceledException) { throw; }
        catch { return false; }
    }

    internal static bool ParseFfmpegInfo(string text, VideoEntry e)
    {
        var found = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (DurationRx.Match(line) is { Success: true } d)
            {
                var secs = int.Parse(d.Groups[1].Value, CultureInfo.InvariantCulture) * 3600
                         + int.Parse(d.Groups[2].Value, CultureInfo.InvariantCulture) * 60
                         + double.Parse(d.Groups[3].Value, CultureInfo.InvariantCulture);
                e.DurationMs = (long)(secs * 1000);
                found = true;
                continue;
            }
            if (StreamRx.Match(line) is not { Success: true } m) continue;
            var codec = m.Groups[2].Value;
            var rest = m.Groups[3].Value;
            if (m.Groups[1].Value == "Video")
            {
                // Cover art is listed as a video stream too.
                if (e.VideoCodec is not null || rest.Contains("(attached pic)")) continue;
                e.VideoCodec = codec;
                if (SizeRx.Match(rest) is { Success: true } sz)
                {
                    e.Width = int.Parse(sz.Groups[1].Value, CultureInfo.InvariantCulture);
                    e.Height = int.Parse(sz.Groups[2].Value, CultureInfo.InvariantCulture);
                }
                if (FpsRx.Match(rest) is { Success: true } fps)
                    e.FrameRate = double.Parse(fps.Groups[1].Value, CultureInfo.InvariantCulture);
                found = true;
            }
            else if (e.AudioCodec is null)
            {
                e.AudioCodec = codec;
            }
        }
        return found;
    }

    private static async Task TryLibVlc(VideoEntry e)
    {
        if (VlcBootstrap.LibVLC is not { } lib) return;
        try
        {
            using var media = new Media(lib, e.Path, FromType.FromPath);
            await media.Parse(MediaParseOptions.ParseLocal, timeout: 5000);
            e.DurationMs = Math.Max(0, media.Duration);
            foreach (var t in media.Tracks)
            {
                if (t.TrackType == TrackType.Video && e.VideoCodec is null)
                {
                    e.VideoCodec = FourCc(t.Codec);
                    e.Width = (int)t.Data.Video.Width;
                    e.Height = (int)t.Data.Video.Height;
                    if (t.Data.Video.FrameRateDen > 0)
                        e.FrameRate = (double)t.Data.Video.FrameRateNum / t.Data.Video.FrameRateDen;
                }
                else if (t.TrackType == TrackType.Audio && e.AudioCodec is null)
                {
                    e.AudioCodec = FourCc(t.Codec);
                }
            }
        }
        catch { /* leave metadata empty; file is still playable */ }
    }

    private static double ParseRate(string? r)
    {
        if (string.IsNullOrEmpty(r)) return 0;
        var parts = r.Split('/');
        if (parts.Length == 2 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var n) &&
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var dd) && dd > 0)
            return n / dd;
        return 0;
    }

    private static string FourCc(uint v) =>
        new string(new[] { (char)(v & 0xFF), (char)((v >> 8) & 0xFF), (char)((v >> 16) & 0xFF), (char)(v >> 24) }).Trim();
}
