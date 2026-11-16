using System.Globalization;
using System.Text.RegularExpressions;
using Cnema.Models;

namespace Cnema.Services;

/// <summary>
/// Best moments, without any model: listens for loud spikes and watches how much the picture changes,
/// on this computer, at idle priority. One small ffmpeg pass for the sound and one for the picture
/// (two frames a second, 64 pixels wide), so a 30-second clip takes about a second.
/// </summary>
public static partial class SmartAnalyzer
{
    public const int Version = 1;

    public static async Task<SmartInfo?> AnalyzeAsync(string path, long durationMs, CancellationToken ct)
    {
        if (ToolLocator.Ffmpeg is not { } ffmpeg || !File.Exists(path)) return null;
        var levels = await Task.Run(() => HighlightService.ReadLevels(ffmpeg, path, ct, background: true), ct);
        var scenes = await SceneScoresAsync(ffmpeg, path, ct);
        return Score(levels, scenes, durationMs);
    }

    /// <summary>How different each frame (two a second) is from the one before, 0 to 1.</summary>
    private static async Task<List<double>> SceneScoresAsync(string ffmpeg, string path, CancellationToken ct)
    {
        var scores = new List<double>();
        try
        {
            var r = await ProcessRunner.RunAsync(ffmpeg, new[]
            {
                "-hide_banner", "-nostats", "-v", "info", "-an", "-sn", "-dn", "-i", path,
                "-vf", "fps=2,scale=64:-2,select='gte(scene\\,0)',metadata=print:key=lavfi.scene_score",
                "-f", "null", "-",
            }, ct: ct, background: true);
            foreach (Match m in SceneLine().Matches(r.StdErr))
                if (double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) scores.Add(v);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Log.Warn($"Scene scores for {Path.GetFileName(path)}: {ex.Message}"); }
        return scores;
    }

    [GeneratedRegex(@"lavfi\.scene_score=([0-9.eE+-]+)")]
    private static partial Regex SceneLine();

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
