using Cnema.Services;
using Stats = Cnema.Services.VoiceStore.VoiceGameStats;

namespace Cnema.ViewModels;

/// <summary>One of the game's badges, earned or not yet, for the strip at the bottom of "Who's that?".</summary>
public sealed class VoiceBadgeView(string name, string description, bool earned)
{
    public string Name { get; } = name;
    public string Description { get; } = description;
    public bool Earned { get; } = earned;
    public double Fade => Earned ? 1 : 0.4;
}

/// <summary>
/// Points, levels and badges for "Who's that?". Points come from naming voices (more for a streak, for picking
/// Mochi's first guess, and for a friend's very first voice); skipping costs nothing, it only starts a new streak.
/// Everything is worked out here from <see cref="Stats"/>, which is kept with the voices.
/// </summary>
public static class VoiceGameProgress
{
    public sealed record Badge(string Id, string Name, string Description, Func<Stats, int, bool> Earned);

    /// <summary>The badges, in the order they're shown. The int is the rounds answered this time.</summary>
    public static readonly IReadOnlyList<Badge> Badges = new Badge[]
    {
        new("first", "First voice", "Told Mochi who was talking for the first time.", (s, _) => s.Named >= 1),
        new("me", "That's me!", "Taught Mochi your own voice.", (s, _) => s.NamedMe),
        new("named10", "Good ears", "Named 10 voices.", (s, _) => s.Named >= 10),
        new("crew3", "The crew", "Taught Mochi 3 different friends' voices.", (s, _) => s.Friends.Count >= 3),
        new("streak5", "Hot streak", "Named 5 voices in a row without skipping.", (s, _) => s.BestStreak >= 5),
        new("guess10", "In sync", "Mochi's first guess was right 10 times.", (s, _) => s.GuessedRight >= 10),
        new("filter", "Noise filter", "Marked 10 voices as not a person.", (s, _) => s.NotPeople >= 10),
        new("session25", "Marathon", "Answered 25 voices in one go.", (s, n) => Math.Max(s.BestSession, n) >= 25),
        new("days3", "Regular", "Played on 3 different days.", (s, _) => s.Days.Count >= 3),
        new("named50", "Sharp ears", "Named 50 voices.", (s, _) => s.Named >= 50),
        new("crew8", "Party of eight", "Taught Mochi 8 different friends' voices.", (s, _) => s.Friends.Count >= 8),
        new("streak15", "Unstoppable", "Named 15 voices in a row without skipping.", (s, _) => s.BestStreak >= 15),
        new("level5", "Best friends", "Reached level 5.", (s, _) => LevelOf(s.Points) >= 5),
        new("named200", "Voice legend", "Named 200 voices.", (s, _) => s.Named >= 200),
    };

    private static readonly string[] Titles =
        { "New ears", "Good listener", "Sharp ears", "Voice detective", "Mochi's best friend", "Voice whisperer", "Ear of legend" };

    /// <summary>Points needed for a level: 0, 50, 150, 300, 500, … (each level a little longer than the last).</summary>
    public static int PointsFor(int level) => 25 * level * (level - 1);

    public static int LevelOf(int points)
    {
        var level = 1;
        while (PointsFor(level + 1) <= points) level++;
        return level;
    }

    public static string TitleOf(int level) => T(Titles[Math.Min(level, Titles.Length) - 1]);

    /// <summary>How far through the current level, 0 to 1.</summary>
    public static double LevelFill(int points)
    {
        var level = LevelOf(points);
        var from = PointsFor(level);
        return (points - from) / (double)(PointsFor(level + 1) - from);
    }

    /// <summary>The points one answer is worth. Streak is the named answers in a row, this one included.</summary>
    public static int PointsForAnswer(VoiceAnswerKind kind, int streak, bool guessedRight, bool newFriend) => kind switch
    {
        VoiceAnswerKind.Friend or VoiceAnswerKind.Me => 10 + Math.Min(streak - 1, 5) * 2 + (guessedRight ? 5 : 0) + (newFriend ? 15 : 0),
        VoiceAnswerKind.Ignore => 5,
        _ => 0,
    };

    /// <summary>Badges earned now that weren't before.</summary>
    public static List<Badge> NewlyEarned(Stats s, int session) =>
        Badges.Where(b => !s.Badges.Contains(b.Id) && b.Earned(s, session)).ToList();
}
