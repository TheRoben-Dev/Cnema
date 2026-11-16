using System.Globalization;
using System.Text.RegularExpressions;
using Cnema.Models;

namespace Cnema.Services;

/// <summary>
/// Names for clips from what Savefile knows about them: the game, who was there, when, and how long.
/// "Late-night Valorant with Alex and Sam · 16 Nov 2026". Nothing is made up: no "epic clutch" for a
/// clip nobody looked at.
/// </summary>
public static partial class SmartTitles
{
    /// <summary>The suggestion, or null when there's nothing better to say than the file name.</summary>
    public static string? Suggest(VideoEntry e, Func<string, bool> isGame, bool withTime = false)
    {
        var game = e.Tags.FirstOrDefault(isGame) is { } g ? Pretty(g) : null;
        var people = e.People.Where(p => p.Trim().Length > 0).Select(p => p.Trim()).ToList();
        if (game is null && people.Count == 0) return null;

        var local = e.ModifiedUtc.ToLocalTime();
        var session = e.DurationMs >= 10 * 60_000;
        var lateNight = local.Hour < 5;
        string what;
        if (game is not null)
        {
            what = game + (session ? " " + T("session") : "");
            if (lateNight) what = F("Late-night {0}", what);
        }
        else what = session ? (lateNight ? T("Late-night session") : T("Session")) : (lateNight ? T("Late-night moment") : T("Moment"));
        if (people.Count > 0) what = F("{0} with {1}", what, Names(people));

        var culture = CultureInfo.CurrentCulture;
        var date = local.ToString(local.Year == DateTime.Now.Year ? "d MMM" : "d MMM yyyy", culture);
        if (withTime) date += " " + local.ToString("t", culture);
        return $"{what} · {date}";
    }

    private static string Names(List<string> people) => people.Count switch
    {
        1 => people[0],
        2 => F("{0} and {1}", people[0], people[1]),
        3 => F("{0}, {1} and {2}", people[0], people[1], people[2]),
        _ => F("{0}, {1} and {2} others", people[0], people[1], people.Count - 2),
    };

    /// <summary>"rocket league" → "Rocket League"; tags written with capitals stay as they are.</summary>
    public static string Pretty(string tag)
    {
        var t = tag.Trim();
        if (t.Any(char.IsUpper)) return t;
        return CultureInfo.CurrentCulture.TextInfo.ToTitleCase(t);
    }

    /// <summary>
    /// A name a recorder made up rather than a person: "Clip 2026-11-16 23-01-02", "Replay_20251114_221503",
    /// "Valorant 2026.11.16 - 23.01.02.03.DVR", "2026-11-16 23-01-02", "Session 3", "VID_20250101".
    /// </summary>
    public static bool IsGeneric(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        if (GenericWord().IsMatch(stem)) return true;
        var digits = stem.Count(char.IsDigit);
        var letters = stem.Count(char.IsLetter);
        // Mostly a date and time: lots of digits, few letters (a game name at most).
        return digits >= 8 && letters <= Math.Max(12, digits);
    }

    [GeneratedRegex(@"^(clip|session|replay|recording|video|vid|movie|capture|untitled|savefile|cnema)[\s_\-]*\d*(\s*\(\d+\))?$", RegexOptions.IgnoreCase)]
    private static partial Regex GenericWord();
}
