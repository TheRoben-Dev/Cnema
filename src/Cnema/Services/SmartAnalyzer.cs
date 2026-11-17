using System.Globalization;
using System.Text.RegularExpressions;
using Cnema.Models;

namespace Cnema.Services;

/// <summary>
/// Best moments, without any model: listens for loud spikes and watches how much the picture changes,
/// on this computer, at idle priority. One small ffmpeg pass for the sound and one for the picture.
/// The picture pass decodes only keyframes (a recorder writes one every second or two), sampled at two a
/// second and 64 pixels wide: a fraction of the work of decoding every frame, so a 2-minute 1080p60 clip
/// costs about a second of one core instead of most of a minute.
/// </summary>
public static partial class SmartAnalyzer
{
    /// <summary>2: also a thumbnail frame and the dead air at the start and end (v0.34).</summary>
    public const int Version = 2;

    public static async Task<SmartInfo?> AnalyzeAsync(string path, long durationMs, CancellationToken ct)
    {
        if (ToolLocator.Ffmpeg is not { } ffmpeg || !File.Exists(path)) return null;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var levels = await Task.Run(() => HighlightService.ReadLevels(ffmpeg, path, ct, background: true), ct);
        var sound = watch.Elapsed.TotalSeconds;
        var frames = await FramesAsync(ffmpeg, path, durationMs, ct);
        if (watch.Elapsed.TotalSeconds > 30)
            Log.Info($"Best moments: {Path.GetFileName(path)}: sound {sound:0.0} s, picture {watch.Elapsed.TotalSeconds - sound:0.0} s, {frames.Count} samples for {durationMs / 1000} s");
        var info = Score(levels, frames.Select(f => f.Scene).ToList(), durationMs);
        Look(info, levels, frames, durationMs);
        return info;
    }

    /// <summary>One picture sample, two a second: how much it changed, how bright it is, how much contrast it has.</summary>
    internal readonly record struct Frame(double Scene, double Brightness, double Spread);

    /// <summary>
    /// Two samples a second (taken from keyframes), 64 pixels wide: how different each is from the one before
    /// (0 to 1), its average brightness and the spread between its dark and light parts (0 to 255).
    /// </summary>
    private static async Task<List<Frame>> FramesAsync(string ffmpeg, string path, long durationMs, CancellationToken ct)
    {
        var frames = new List<Frame>();
        // Some recorders mark every frame as a keyframe (or none), and then "keyframes only" means decoding the
        // whole video on one core: minutes per clip on a Mac. The Mac's own video decoder does that part
        // nearly for free, and a picture pass that still runs long is cut off: the sound alone still finds the moments.
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        BackgroundHold.CancelAfterActive(budget, TimeSpan.FromSeconds(30) + TimeSpan.FromMilliseconds(Math.Max(0, durationMs) / 4));
        try
        {
            var args = new List<string> { "-hide_banner", "-nostats", "-v", "info", "-threads", "1", "-skip_frame", "nokey" };
            if (OperatingSystem.IsMacOS()) args.AddRange(new[] { "-hwaccel", "videotoolbox" });
            args.AddRange(new[]
            {
                "-an", "-sn", "-dn", "-i", path,
                "-vf", "fps=2,scale=64:-2,signalstats,select='gte(scene\\,0)',metadata=print",
                "-f", "null", "-",
            });
            var r = await ProcessRunner.RunAsync(ffmpeg, args, ct: budget.Token, background: true);
            double low = 0, avg = 0, high = 0;
            foreach (Match m in StatLine().Matches(r.StdErr))
            {
                if (!double.TryParse(m.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) continue;
                switch (m.Groups[1].Value)
                {
                    case "signalstats.YLOW": low = v; break;
                    case "signalstats.YAVG": avg = v; break;
                    case "signalstats.YHIGH": high = v; break;
                    case "scene_score": frames.Add(new Frame(v, avg, high - low)); break; // the last key printed for a frame
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Log.Warn($"Picture pass for {Path.GetFileName(path)} took too long; its best moments come from the sound only.");
            frames.Clear();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Log.Warn($"Picture pass for {Path.GetFileName(path)}: {ex.Message}"); }
        return frames;
    }

    [GeneratedRegex(@"lavfi\.(scene_score|signalstats\.Y(?:LOW|AVG|HIGH))=([0-9.eE+-]+)")]
    private static partial Regex StatLine();

    /// <summary>A sample shows nothing: black (a fade, a loading screen) or one flat colour.</summary>
    internal static bool Blank(Frame f) => f.Brightness < 30 || f.Spread < 10;

    /// <summary>
    /// Picks a thumbnail frame (lively, not black, not a flat loading screen, near the best moment) and the
    /// dead air at the ends: blank or frozen picture with no sound worth hearing, at least 2 seconds of it.
    /// </summary>
    internal static void Look(SmartInfo info, List<double> levels, List<Frame> frames, long durationMs)
    {
        if (frames.Count < 4) return;
        const double step = 500; // ms between samples
        var lengthMs = durationMs > 0 ? durationMs : frames.Count * (long)step;

        // Thumbnail: the best-looking sample, preferring the best moment and the middle over the ends.
        var best = -1;
        double bestScore = double.MinValue;
        for (var i = 0; i < frames.Count; i++)
        {
            var f = frames[i];
            if (Blank(f)) continue;
            var t = i * step;
            var score = Math.Min(f.Spread, 120) / 120.0                               // contrast: something to see
                        - Math.Abs(f.Brightness - 110) / 255.0                           // neither murky nor washed out
                        - (t < lengthMs * 0.05 || t > lengthMs * 0.95 ? 0.4 : 0)        // not the very start or end
                        - (info.BestMs >= 0 ? Math.Min(1, Math.Abs(t - info.BestMs) / 15000.0) * 0.5 : 0);
            if (score > bestScore) { bestScore = score; best = i; }
        }
        if (best >= 0) info.ThumbMs = Math.Max(1, (long)(best * step));

        // Dead air. Sound counts when it's within 18 dB of the clip's loud parts.
        var loud = levels.Count > 0 ? levels.OrderBy(x => x).ElementAt((int)(levels.Count * 0.9)) : -100;
        bool Heard(double ms)
        {
            if (levels.Count == 0) return false;
            var k = (int)(ms / 1000 / HighlightService.WindowSeconds);
            return k >= 0 && k < levels.Count && levels[k] > Math.Max(-50, loud - 18);
        }
        // Only a blank picture counts: samples come from keyframes, so "nothing moved" can't be told reliably.
        bool Dead(int i) => Blank(frames[i]) && !Heard(i * step);

        var start = 0;
        while (start < frames.Count && Dead(start)) start++;
        var end = frames.Count;
        while (end > start && Dead(end - 1)) end--;
        var startMs = (long)(start * step);
        var endMs = (long)(end * step);
        // Worth suggesting only if it saves 2 seconds or more and leaves at least 3 seconds.
        if (startMs >= 2000 && endMs - startMs >= 3000) info.TrimStartMs = Math.Max(0, startMs - 250);
        // Samples stop at the last keyframe, which can be a second or two before the end: only blank samples count.
        if ((frames.Count - end) * step >= 2000 && endMs - startMs >= 3000) info.TrimEndMs = Math.Min(lengthMs, endMs + 250);
    }

    /// <summary>
    /// Excitement mixes how far the loudest moments rise above the usual level, how many sudden spikes
    /// there are for the length, and how much the picture moves and cuts.
    /// </summary>
    internal static SmartInfo Score(List<double> levels, List<double> scenes, long durationMs)
    {
        var info = new SmartInfo { Version = Version };
        double audio = 0;
        if (levels.Count >= 4 && levels.Max() > -45)
        {
            var sorted = levels.OrderBy(x => x).ToList();
            var median = sorted[sorted.Count / 2];
            var peak = sorted[(int)(sorted.Count * 0.97)];
            // 3 dB over the usual level is nothing; 25 dB over is a roar.
            audio = Math.Clamp((peak - median - 3) / 22, 0, 1);
            info.Spikes = HighlightService.Pick(levels).Count;
            // Best moment: the loudest four seconds (power, not dB, so one bang counts for what it is).
            var n = Math.Max(1, (int)(4 / HighlightService.WindowSeconds));
            double best = double.MinValue;
            var at = 0;
            for (var i = 0; i + n <= levels.Count; i++)
            {
                var power = 0.0;
                for (var k = i; k < i + n; k++) power += Math.Pow(10, levels[k] / 10);
                if (power > best) { best = power; at = i; }
            }
            if (audio > 0.15) info.BestMs = (long)((at + n / 2.0) * HighlightService.WindowSeconds * 1000);
        }
        double motion = 0, cuts = 0;
        if (scenes.Count > 2)
        {
            motion = Math.Clamp(scenes.Average() * 6, 0, 1);
            cuts = scenes.Count(s => s > 0.35) / (double)scenes.Count;
            // No telling sound: the busiest stretch is the best moment.
            if (info.BestMs < 0 && motion > 0.15)
            {
                var n = Math.Min(8, scenes.Count);
                var bestAt = 0;
                double bestSum = -1;
                for (var i = 0; i + n <= scenes.Count; i++)
                {
                    var sum = 0.0;
                    for (var k = i; k < i + n; k++) sum += scenes[k];
                    if (sum > bestSum) { bestSum = sum; bestAt = i; }
                }
                info.BestMs = (long)((bestAt + n / 2.0) * 500);
            }
        }
        info.Motion = Math.Round(motion, 3);
        var minutes = Math.Max(0.25, durationMs / 60000.0);
        var spikesRate = Math.Clamp(info.Spikes / minutes / 6, 0, 1);
        info.Excitement = Math.Round(Math.Clamp(0.5 * audio + 0.2 * spikesRate + 0.2 * motion + 0.1 * Math.Clamp(cuts * 4, 0, 1), 0, 1), 3);
        if (durationMs > 0 && info.BestMs > durationMs) info.BestMs = durationMs / 2;
        return info;
    }
}
