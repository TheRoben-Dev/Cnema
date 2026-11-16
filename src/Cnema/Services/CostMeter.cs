using System.Collections.Concurrent;

namespace Cnema.Services;

/// <summary>
/// What each smart feature costs while it works, so Settings can say so before and after it's switched on.
/// The time per video starts from measurements on a modest 4-core computer and is replaced by what this
/// computer actually takes once it has done a few videos (kept in settings, so it survives a restart).
/// </summary>
public static class CostMeter
{
    public const string Moments = "moments";
    public const string Speech = "speech";
    public const string Visual = "visual";

    /// <summary>How a feature spends the computer, apart from time.</summary>
    public sealed record Profile(string Key, double SecondsPerMinute, double SecondsPerVideo, string Uses, int ExtraMemoryMb);

    // Measured with a 2-minute 1080p60 clip on a 4-core cloud computer (see the v0.33 notes); a desktop
    // or an Apple silicon Mac is usually two to four times quicker.
    public static readonly Profile[] Profiles =
    {
        new(Moments, 0.5, 0.4, "one CPU core at low priority, no GPU", 30),
        new(Speech, 22, 1.5, "half your CPU cores (at most 4) at low priority", 400),
        new(Visual, 0, 3.5, "half your CPU cores (at most 4) at low priority, a few seconds per video", 250),
    };

    public static Profile Of(string key) => Profiles.First(p => p.Key == key);

    /// <summary>How much slower (above 1) or quicker (below 1) this computer is than the measurements, per feature.</summary>
    private static ConcurrentDictionary<string, double> _pace = new();
    private static SettingsStore? _store;

    public static void Attach(SettingsStore store)
    {
        _store = store;
        _pace = new ConcurrentDictionary<string, double>(store.Current.WorkRates);
    }

    private static double Baseline(Profile p, long durationMs) =>
        p.SecondsPerVideo + p.SecondsPerMinute * Math.Max(0, durationMs) / 60000.0;

    /// <summary>Seconds of work for one video of this length on this computer.</summary>
    public static double SecondsFor(string key, long durationMs)
    {
        var p = Of(key);
        return Baseline(p, durationMs) * (_pace.TryGetValue(key, out var pace) ? pace : 1);
    }

    /// <summary>True once this computer's own pace is known (otherwise the figure is an estimate).</summary>
    public static bool IsMeasured(string key) => _pace.ContainsKey(key);

    /// <summary>One video done in <paramref name="seconds"/>: folds it into this computer's pace.</summary>
    public static void Record(string key, double seconds, long durationMs)
    {
        if (seconds <= 0 || seconds > 3600 || durationMs <= 0) return;
        var sample = Math.Clamp(seconds / Baseline(Of(key), durationMs), 0.05, 20);
        var pace = _pace.AddOrUpdate(key, sample, (_, old) => old * 0.8 + sample * 0.2);
        // Saved with the next settings save; not worth a write per video.
        if (_store is { } s) s.Current.WorkRates[key] = Math.Round(pace, 3);
    }

    /// <summary>"about 25 min", "about 40 s", "about 3 h".</summary>
    public static string Span(double seconds) =>
        seconds < 1 ? Loc.T("under a second")
        : seconds < 90 ? Loc.F("about {0:0} s", Math.Max(1, Math.Round(seconds)))
        : seconds < 5400 ? Loc.F("about {0:0} min", Math.Round(seconds / 60))
        : Loc.F("about {0:0.#} h", seconds / 3600);
}
