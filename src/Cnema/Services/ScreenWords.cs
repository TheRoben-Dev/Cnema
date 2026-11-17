using Cnema.Models;

namespace Cnema.Services;

/// <summary>
/// What the text on screen says happened: a banner (VICTORY ROYALE, ACE, PENTA KILL) for titles, and the game,
/// when its own words give it away (SPIKE PLANTED is Valorant). Only exact phrases count: a stray word in chat
/// or a name in the kill feed shouldn't turn a clip into a "Clutch".
/// </summary>
public static class ScreenWords
{
    /// <summary>The Settings toggle (Features.ScreenText), copied here so titles can read it.</summary>
    public static bool Enabled { get; set; }

    /// <summary>A banner the game shows, its headline (English; shown through T) and how big a deal it is.</summary>
    private static readonly (string[] Phrases, string Headline, int Rank, bool Alone)[] Events =
    {
        (new[] { "VICTORY ROYALE" }, "Victory Royale", 10, false),
        (new[] { "WINNER WINNER CHICKEN DINNER" }, "Chicken dinner", 10, false),
        (new[] { "CHAMPION SQUAD", "YOU ARE THE CHAMPION", "YOU ARE THE CHAMPIONS" }, "Champions", 10, false),
        (new[] { "PENTA KILL", "PENTAKILL" }, "Penta kill", 9, false),
        (new[] { "ACE" }, "Ace", 9, true),
        (new[] { "QUADRA KILL", "QUADRAKILL", "QUAD KILL" }, "Quadra kill", 8, false),
        (new[] { "CLUTCH" }, "Clutch", 8, true),
        (new[] { "TRIPLE KILL" }, "Triple kill", 7, false),
        (new[] { "PLAY OF THE GAME" }, "Play of the game", 7, false),
        (new[] { "FLAWLESS" }, "Flawless", 6, true),
        (new[] { "VICTORY", "SEGER" }, "Victory", 5, true),
        (new[] { "DOUBLE KILL" }, "Double kill", 4, false),
        (new[] { "FIRST BLOOD" }, "First blood", 4, false),
    };

    /// <summary>Phrases only one game shows. Every phrase in a row has to be there (Minecraft: YOU DIED and RESPAWN).</summary>
    private static readonly (string[] AllOf, string Game)[] Games =
    {
        (new[] { "VICTORY ROYALE" }, "Fortnite"),
        (new[] { "STORM EYE SHRINKING" }, "Fortnite"),
        (new[] { "SPIKE PLANTED" }, "Valorant"),
        (new[] { "SPIKE DEFUSED" }, "Valorant"),
        (new[] { "SPIKE DETONATED" }, "Valorant"),
        (new[] { "KILL LEADER" }, "Apex Legends"),
        (new[] { "CHAMPION SQUAD" }, "Apex Legends"),
        (new[] { "YOU ARE THE CHAMPION" }, "Apex Legends"),
        (new[] { "BOMB HAS BEEN PLANTED" }, "Counter-Strike 2"),
        (new[] { "BOMB HAS BEEN DEFUSED" }, "Counter-Strike 2"),
        (new[] { "COUNTER TERRORISTS WIN" }, "Counter-Strike 2"),
        (new[] { "PLAY OF THE GAME" }, "Overwatch 2"),
        (new[] { "WINNER WINNER CHICKEN DINNER" }, "PUBG"),
        (new[] { "AN ALLY HAS BEEN SLAIN" }, "League of Legends"),
        (new[] { "YOU HAVE BEEN SLAIN" }, "League of Legends"),
        (new[] { "WAS THE IMPOSTOR" }, "Among Us"),
        (new[] { "WAS NOT THE IMPOSTOR" }, "Among Us"),
        (new[] { "YOU DIED", "RESPAWN" }, "Minecraft"),
    };

    /// <summary>The biggest banner read on screen, or null.</summary>
    public static (string Headline, int Rank, long At)? Event(ScreenInfo? screen)
    {
        if (!Enabled || screen is not { Lines.Count: > 0 }) return null;
        (string, int, long)? best = null;
        foreach (var line in screen.Lines)
        {
            var key = ScreenService.Key(line.Text);
            foreach (var (phrases, headline, rank, alone) in Events)
            {
                if (best is { } b && b.Item2 >= rank) continue;
                // A one-word banner (ACE, CLUTCH) only counts as the whole line; a phrase counts anywhere in it.
                if (phrases.Any(p => alone ? key == p || key == p + " " + p : Has(key, p)))
                    best = (T(headline), rank, line.At);
            }
        }
        return best;
    }

    /// <summary>The game whose own words are on screen, or null.</summary>
    public static string? Game(ScreenInfo? screen)
    {
        if (screen is not { Lines.Count: > 0 }) return null;
        var keys = screen.Lines.Select(l => ScreenService.Key(l.Text)).ToList();
        foreach (var (allOf, game) in Games)
            if (allOf.All(p => keys.Any(k => Has(k, p)))) return game;
        return null;
    }

    /// <summary>Whether the screen text has the search word.</summary>
    public static bool Matches(ScreenInfo? screen, string term) =>
        screen is { Lines.Count: > 0 } && screen.Lines.Any(l => TextMatch.Has(l.Text, term));

    /// <summary>The phrase as whole words in the line.</summary>
    private static bool Has(string key, string phrase)
    {
        var i = key.IndexOf(phrase, StringComparison.Ordinal);
        while (i >= 0)
        {
            var startOk = i == 0 || key[i - 1] == ' ';
            var end = i + phrase.Length;
            if (startOk && (end == key.Length || key[end] == ' ')) return true;
            i = key.IndexOf(phrase, i + 1, StringComparison.Ordinal);
        }
        return false;
    }
}
