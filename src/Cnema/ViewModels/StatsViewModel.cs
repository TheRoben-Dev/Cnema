using System.Globalization;
using Cnema.Models;

namespace Cnema.ViewModels;

public sealed record StatTile(string Value, string Label);
/// <summary>One bar in a chart; <see cref="Fraction"/> is 0..1 of the largest bar.</summary>
public sealed record StatBar(string Label, int Count, double Fraction)
{
    public double Width => 6 + Fraction * 260;
    public double Height => 4 + Fraction * 96;
    /// <summary>The count over the bar, left out for empty bars so a sparse chart stays quiet.</summary>
    public string CountText => Count > 0 ? Count.ToString(System.Globalization.CultureInfo.CurrentCulture) : "";
}

/// <summary>Numbers about the library and the clips folder, worked out once when the panel opens.</summary>
public sealed class StatsViewModel
{
    public IReadOnlyList<StatTile> Tiles { get; }
    public IReadOnlyList<StatBar> Weeks { get; }
    public IReadOnlyList<StatBar> Games { get; }
    public bool HasGames => Games.Count > 0;
    public IReadOnlyList<BadgeRow> Badges { get; }
    public bool HasBadges => Badges.Count > 0;
    public string BadgeCount => F("{0} of {1}", Badges.Count(b => b.Earned), Badges.Count);
    public IReadOnlyList<PlayBar> Playtime { get; }
    public bool HasPlaytime => PlaytimeSince.Length > 0;
    public bool HasPlaytimeBars => Playtime.Count > 0;
    public string PlaytimeSince { get; }

    public StatsViewModel(IReadOnlyList<VideoEntry> all, string clipsFolder, IReadOnlyList<BadgeRow>? badges = null,
        IReadOnlyList<PlayBar>? playtime = null, string playtimeSince = "")
    {
        Badges = badges ?? Array.Empty<BadgeRow>();
        Playtime = playtime ?? Array.Empty<PlayBar>();
        PlaytimeSince = playtimeSince;
        var prefix = clipsFolder.TrimEnd('/', '\\') + Path.DirectorySeparatorChar;
        var clips = all.Where(e => e.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
        var weekStart = DateTime.Today.AddDays(-(((int)DateTime.Today.DayOfWeek + 6) % 7)); // Monday
        Tiles = new[]
        {
            new StatTile(all.Count.ToString("N0", CultureInfo.CurrentCulture), all.Count == 1 ? T("video") : T("videos")),
            new StatTile(Hours(all.Sum(e => e.DurationMs)), T("of video")),
            new StatTile(VideoItemViewModel.FormatSize(all.Sum(e => e.Size)), T("on disk")),
            new StatTile(clips.Count(e => e.ModifiedUtc.ToLocalTime() >= weekStart).ToString(CultureInfo.CurrentCulture), T("clips this week")),
            new StatTile(clips.Count.ToString("N0", CultureInfo.CurrentCulture), T("clips in total")),
            new StatTile(all.Count(e => e.LastPlayedUtc is null).ToString("N0", CultureInfo.CurrentCulture), T("not watched yet")),
        };

        // Clips per week, the last eight weeks, oldest first.
        var weeks = Enumerable.Range(0, 8).Select(i => weekStart.AddDays(-7 * (7 - i))).ToList();
        var perWeek = weeks.Select(w => clips.Count(e => e.ModifiedUtc.ToLocalTime() >= w && e.ModifiedUtc.ToLocalTime() < w.AddDays(7))).ToList();
        var maxWeek = Math.Max(1, perWeek.Max());
        Weeks = weeks.Select((w, i) => new StatBar(w.ToString("d MMM", CultureInfo.CurrentCulture), perWeek[i], (double)perWeek[i] / maxWeek)).ToList();

        // Games come from the tag a clip gets after the app that was in front.
        var games = clips.SelectMany(e => e.Tags).GroupBy(t => t, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Name: g.First(), Count: g.Count())).OrderByDescending(g => g.Count).Take(6).ToList();
        var maxGame = Math.Max(1, games.Select(g => g.Count).DefaultIfEmpty(0).Max());
        Games = games.Select(g => new StatBar(g.Name, g.Count, (double)g.Count / maxGame)).ToList();
    }

    private static string Hours(long ms)
    {
        var t = TimeSpan.FromMilliseconds(ms);
        // Under a minute in all reads as "0 min" otherwise.
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes} min"
            : t.TotalMinutes >= 1 ? $"{(int)Math.Round(t.TotalMinutes)} min"
            : $"{(int)Math.Round(t.TotalSeconds)} s";
    }
}
