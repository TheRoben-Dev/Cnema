using System.Collections.Concurrent;
using System.Diagnostics;

namespace Cnema.Services;

/// <summary>
/// Finds the loud moments in a video's audio (gunfire, shouting, a crowd) so the player can mark them
/// on the timeline. ffmpeg decodes the first audio track to 8 kHz mono; the rest is arithmetic.
/// </summary>
public static class HighlightService
{
    private const int Rate = 8000;
    internal const double WindowSeconds = 0.5;
    private const int MaxMarkers = 12;
    private static readonly ConcurrentDictionary<string, IReadOnlyList<double>> Cache = new();

    /// <summary>Positions in milliseconds, in time order. Empty when the audio is flat or missing.</summary>
    public static async Task<IReadOnlyList<double>> DetectAsync(string path, CancellationToken ct)
    {
        string key;
        try { var f = new FileInfo(path); key = $"{path}|{f.Length}|{f.LastWriteTimeUtc.Ticks}"; }
        catch { return Array.Empty<double>(); }
        if (Cache.TryGetValue(key, out var cached)) return cached;
        if (ToolLocator.Ffmpeg is not { } ffmpeg) return Array.Empty<double>();

        // This reads the whole file while the video starts playing: give the player its first seconds, and
        // let it have the CPU and the disk first after that.
        await Task.Delay(1500, ct);
        var levels = await Task.Run(() => ReadLevels(ffmpeg, path, ct, gentle: true), ct);
        var found = Pick(levels);
        Cache[key] = found;
        return found;
    }

    /// <summary>The middle of the loudest few seconds, in milliseconds; null when there is no audio to go by.</summary>
    public static async Task<double?> BestMomentAsync(string path, double windowSeconds, CancellationToken ct)
    {
        if (ToolLocator.Ffmpeg is not { } ffmpeg) return null;
        var levels = await Task.Run(() => ReadLevels(ffmpeg, path, ct), ct);
        var n = Math.Max(1, (int)(windowSeconds / WindowSeconds));
        if (levels.Count < 2 || levels.Max() < -45) return null;
        double best = double.MinValue;
        var at = 0;
        for (var i = 0; i + n <= levels.Count; i++)
        {
            // Mean power (not mean dB), so one loud bang counts for what it is.
            var power = 0.0;
            for (var k = i; k < i + n; k++) power += Math.Pow(10, levels[k] / 10);
            if (power > best) { best = power; at = i; }
        }
        return (at + n / 2.0) * WindowSeconds * 1000;
    }

    /// <summary>RMS level in dB for every half second of audio.</summary>
    /// <param name="background">Indexing work: lowest priority, frozen while a video plays.</param>
    /// <param name="gentle">Below normal priority, but never frozen (the player's own markers).</param>
    internal static List<double> ReadLevels(string ffmpeg, string path, CancellationToken ct, bool background = false, bool gentle = false)
    {
        var levels = new List<double>();
        var psi = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var a in new[] { "-hide_banner", "-v", "quiet", "-i", path, "-map", "0:a:0", "-vn",
                                  "-ac", "1", "-ar", Rate.ToString(), "-f", "s16le", "-" })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi);
        if (p is null) return levels;
        if (background) ProcessRunner.Lower(p);
        else if (gentle)
            try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { /* gone already */ }
        p.ErrorDataReceived += (_, _) => { };
        p.BeginErrorReadLine();
        try
        {
            using var reg = ct.Register(() => { try { p.Kill(); } catch { /* gone */ } });
            var window = (int)(Rate * WindowSeconds);
            var buffer = new byte[window * 2];
            var stream = p.StandardOutput.BaseStream;
            while (true)
            {
                var got = 0;
                while (got < buffer.Length)
                {
                    var n = stream.Read(buffer, got, buffer.Length - got);
                    if (n <= 0) break;
                    got += n;
                }
                if (got < buffer.Length / 2) break;
                double sum = 0;
                var samples = got / 2;
                for (var i = 0; i < samples; i++)
                {
                    var v = (short)(buffer[i * 2] | buffer[i * 2 + 1] << 8) / 32768.0;
                    sum += v * v;
                }
                levels.Add(10 * Math.Log10(sum / samples + 1e-10));
                if (got < buffer.Length) break;
            }
            p.WaitForExit(2000);
        }
        catch { /* treat as no audio */ }
        ct.ThrowIfCancellationRequested();
        return levels;
    }

    /// <summary>
    /// A highlight is a window well above the video's usual loudness: at least 8 dB over the median
    /// and 1.5 standard deviations over the mean. Peaks closer than five seconds count once.
    /// </summary>
    internal static IReadOnlyList<double> Pick(List<double> levels)
    {
        if (levels.Count < 8) return Array.Empty<double>();
        var sorted = levels.OrderBy(x => x).ToList();
        var median = sorted[sorted.Count / 2];
        var mean = levels.Average();
        var std = Math.Sqrt(levels.Sum(x => (x - mean) * (x - mean)) / levels.Count);
        var threshold = Math.Max(median + 8, mean + 1.5 * std);
        if (sorted[^1] < -45) return Array.Empty<double>(); // effectively silent

        var gap = (int)(5 / WindowSeconds);
        var picked = new List<(int Index, double Level)>();
        foreach (var (level, index) in levels.Select((l, i) => (l, i)).Where(x => x.l >= threshold).OrderByDescending(x => x.l))
        {
            if (picked.Any(p => Math.Abs(p.Index - index) < gap)) continue;
            picked.Add((index, level));
            if (picked.Count == MaxMarkers) break;
        }
        // Mark the start of the loud window's run-up rather than its middle: a second earlier.
        return picked.OrderBy(p => p.Index)
            .Select(p => Math.Max(0, p.Index * WindowSeconds - 1) * 1000).ToList();
    }
}
