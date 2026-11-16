using Cnema.Models;

namespace Cnema.Services;

/// <summary>
/// Badges earned from the clips themselves (never from guessed hours): how, when and with whom you save
/// moments. Each one is checked against the whole library, so it can't be earned from half the data.
/// </summary>
public static class Achievements
{
    public sealed record Badge(string Id, string Name, string Description, Func<Facts, bool> Earned);

    /// <summary>What the badges are worked out from, gathered once per check.</summary>
    public sealed class Facts
    {
        public required IReadOnlyList<VideoEntry> All { get; init; }
        public required Func<VideoEntry, string?> GameOf { get; init; }
        public bool Highlights { get; init; }
        public bool Laughs { get; init; }
        public bool People { get; init; }
    }

    private static DateTime Local(VideoEntry e) => e.ModifiedUtc.ToLocalTime();

    public static readonly IReadOnlyList<Badge> All = new Badge[]
    {
        new("first", "First save", "Saved your first moment.", f => f.All.Count >= 1),
        new("nightowl", "Night owl", "10 clips saved between midnight and 5 in the morning.",
            f => f.All.Count(e => Local(e).Hour < 5) >= 10),
        new("marathon", "Marathon night", "10 clips on a single day.",
            f => f.All.GroupBy(e => Local(e).Date).Any(g => g.Count() >= 10)),
        new("streak", "On a roll", "Clips on 7 days in a row.", f => LongestRun(f.All) >= 7),
        new("explorer", "Explorer", "Clips from 5 different games.",
            f => f.All.Select(f.GameOf).Where(g => g is not null).Distinct(StringComparer.OrdinalIgnoreCase).Count() >= 5),
        new("squad", "Full squad", "A clip with 3 or more friends in it.",
            f => f.People && f.All.Any(e => e.People.Count(p => p.Trim().Length > 0) >= 3)),
        new("regulars", "The regulars", "25 clips with the same friend.",
            f => f.People && f.All.SelectMany(e => e.People.Select(p => p.Trim())).Where(p => p.Length > 0)
                .GroupBy(p => p, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() >= 25)),
        new("keeper", "Keeper", "25 favourites.", f => f.All.Count(e => e.Favorite) >= 25),
        new("bigmoment", "Big moment", "A clip Savefile rates as one of its very best moments.",
            f => f.Highlights && f.All.Any(e => e.Smart is { Excitement: >= 0.9 })),
        new("laughs", "Comedy gold", "10 videos where someone couldn't stop laughing.",
            f => f.Laughs && f.All.Count(e => e.Sound is { Laugh.Count: > 1 } s && s.LaughSeconds >= 2) >= 10),
        new("year", "A year of saves", "Your library goes back a whole year.",
            f => f.All.Count > 0 && (DateTime.UtcNow - f.All.Min(e => e.ModifiedUtc)).TotalDays >= 365),
        new("throwback", "Throwback", "Watched a clip that's more than a year old.",
            f => f.All.Any(e => e.LastPlayedUtc is { } p && (p - e.ModifiedUtc).TotalDays >= 365)),
    };

    private static int LongestRun(IReadOnlyList<VideoEntry> all)
    {
        var days = all.Select(e => Local(e).Date).Distinct().OrderBy(d => d).ToList();
        int best = 0, run = 0;
        for (var i = 0; i < days.Count; i++)
        {
            run = i > 0 && (days[i] - days[i - 1]).Days == 1 ? run + 1 : 1;
            best = Math.Max(best, run);
        }
        return best;
    }
}
