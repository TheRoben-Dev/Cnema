using System.Globalization;
using Cnema.Models;

namespace Cnema.ViewModels;

// Mochi's small talk between the lines about your clips: things about your library as a whole, the time of
// day, the season, the games you play, and plain chatter. Each turn draws one at random (the ones that fit
// right now count for more), and none comes back until all the others that fit have been said.
public sealed partial class MainViewModel
{
    internal sealed record ChatterContext(int Hour, DayOfWeek Day, int Month)
    {
        public bool Weekend => Day is DayOfWeek.Saturday or DayOfWeek.Sunday;
    }

    private List<(string Id, double Weight, string Text)> _chatter = new();
    private int _mascotTurn;
    private readonly Random _chatterDice = new();

    /// <summary>A game's name as letters and digits only: "No Man's Sky" → "nomanssky".</summary>
    internal static string GameKey(string name) => new(name.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    private static bool Has(IReadOnlySet<string> games, string key) => games.Any(g => g.Contains(key, StringComparison.Ordinal));

    /// <summary>Everything Mochi could chat about right now (rebuilt with the clip lines).</summary>
    internal List<(string Id, double Weight, string Text)> ChatterLines(DateTime now)
    {
        var list = new List<(string Id, double Weight, string Text)>();
        GeneralChatter(list);
        TimeChatter(new ChatterContext(now.Hour, now.DayOfWeek, now.Month), list);
        var all = _items.Values.Select(v => v.Entry).Where(e => e.DurationMs > 0).ToList();
        var games = all.Select(GameOf).OfType<string>().ToList();
        GameChatter(games.Select(GameKey).ToHashSet(), list);
        LibraryChatter(all, games, now, list);
        return list;
    }

    /// <summary>Things about the whole library, from what's really there.</summary>
    private void LibraryChatter(List<VideoEntry> all, List<string> games, DateTime now, List<(string Id, double Weight, string Text)> add)
    {
        if (all.Count == 0) return;
        DateTime Local(VideoEntry e) => e.ModifiedUtc.ToLocalTime();
        var c = CultureInfo.CurrentCulture;
        if (all.Count >= 10) add.Add(("stat-count", 2, P(all.Count, "{0} clip and counting. Mochi remembers it.", "{0:N0} clips and counting. Mochi remembers every one.")));
        var total = TimeSpan.FromMilliseconds(all.Sum(e => e.DurationMs));
        if (total.TotalHours >= 1) add.Add(("stat-hours", 2, F("That's {0} of clips in here. Mochi hasn't blinked once.", P((long)total.TotalHours, "{0} hour", "{0:N0} hours"))));
        var favs = all.Count(e => e.Favorite);
        if (favs >= 3) add.Add(("stat-favs", 2, F("{0} favourites so far. Mochi has its own favourites too.", favs)));
        var first = all.MinBy(e => e.ModifiedUtc)!;
        if ((now - Local(first)).TotalDays > 60)
            add.Add(("stat-first", 2, F("Your oldest clip here is from {0}. Look how far you've come.", Local(first).ToString("MMMM yyyy", c))));
        var longest = all.MaxBy(e => e.DurationMs)!;
        if (longest.DurationMs >= 5 * 60_000)
            add.Add(("stat-long", 1.5, F("Your longest clip runs {0}. Bring snacks.", Services.WrappedService.Span(longest.DurationMs))));
        if (Features.People)
        {
            var mate = all.SelectMany(e => e.People.Select(p => p.Trim())).Where(p => p.Length > 0)
                .GroupBy(p => p, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).FirstOrDefault();
            if (mate is not null && mate.Count() >= 5)
            {
                add.Add(("stat-mate", 2, F("{0} shows up in more of your clips than anyone. Squad leader material.", mate.First())));
                add.Add(("stat-duo", 2, F("{0} clips with {1}. That's a proper duo.", mate.Count(), mate.First())));
            }
        }
        var top = games.GroupBy(g => g, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).FirstOrDefault();
        if (top is not null && top.Count() >= 5)
        {
            add.Add(("stat-game", 2, F("{0} is your most clipped game. No surprise there.", top.Key)));
            add.Add(("stat-game2", 2, F("{0} again? Mochi approves.", top.Key)));
            add.Add(("stat-game3", 1.5, F("Mochi has watched so much {0} it knows the menus by heart.", top.Key)));
        }
        var kinds = games.Distinct(StringComparer.OrdinalIgnoreCase).Count();
        if (kinds >= 4) add.Add(("stat-kinds", 2, F("You've clipped {0} different games. Mochi likes variety.", kinds)));
        var today = all.Count(e => Local(e).Date == now.Date);
        if (today >= 2) add.Add(("stat-today", 3, F("{0} clips saved today already. Busy day!", today)));
        else if (today == 0 && now.Hour >= 12) add.Add(("stat-none", 1, T("Nothing new today yet. Mochi is patiently wobbling.")));
        if (all.Count >= 20)
        {
            var day = all.GroupBy(e => Local(e).DayOfWeek).OrderByDescending(g => g.Count()).First().Key;
            add.Add(("stat-day", 1.5, F("Most of your clips are from a {0}. Mochi has noticed.", c.DateTimeFormat.GetDayName(day))));
        }
        var unseen = all.Count(e => e.LastPlayedUtc is null && (now - Local(e)).TotalDays > 7);
        if (unseen >= 10) add.Add(("stat-unseen", 1.5, F("{0} of your clips have never been rewatched. Mochi sees an opportunity.", unseen)));
    }

    /// <summary>One line of small talk: weighted, and nothing twice until the rest have had their turn.</summary>
    private string? DrawChatter()
    {
        if (_chatter.Count == 0) return null;
        var said = _settings.Current.MochiSaid;
        var fresh = _chatter.Where(l => !said.Contains(l.Id) && l.Text != MascotLine).ToList();
        if (fresh.Count == 0)
        {
            // All said: start over (keeping the one just shown from coming straight back).
            foreach (var l in _chatter) said.Remove(l.Id);
            fresh = _chatter.Where(l => l.Text != MascotLine).ToList();
            if (fresh.Count == 0) return null;
        }
        var roll = _chatterDice.NextDouble() * fresh.Sum(l => l.Weight);
        var pick = fresh[^1];
        foreach (var l in fresh)
        {
            roll -= l.Weight;
            if (roll <= 0) { pick = l; break; }
        }
        said.Add(pick.Id);
        if (said.Count > 400) said.RemoveRange(0, said.Count - 400);
        if (said.Count % 8 == 0) _settings.Save(); // remembered across restarts, written now and then
        return pick.Text;
    }
}
