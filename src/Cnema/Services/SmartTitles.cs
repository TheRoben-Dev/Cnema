using System.Globalization;
using System.Text.RegularExpressions;
using Cnema.Models;

namespace Cnema.Services;

/// <summary>
/// Names for clips from what Savefile knows about them: what was said and heard at the big moment, the game,
/// who was there, when. "Alex: “Behind you!” · Valorant", "Laughing at “did he just jump” · Minecraft with Sam",
/// or "Late-night Valorant with Alex and Sam · 16 Nov". Nothing is made up: no "epic clutch" for a clip
/// nobody looked at.
/// </summary>
public static partial class SmartTitles
{
    /// <summary>
    /// The suggestion, or null when there's nothing better to say than the file name. When the smart features have
    /// listened to the clip, the title says what happened: a line someone said at the big moment ("Alex: “Behind
    /// you!”"), what set off a laugh ("Laughing at “did he just jump”"), or a big laugh or a loud moment. Only what
    /// was heard: a line is quoted as it was written down, and a name is only put to it when the voice is known.
    /// </summary>
    public static string? Suggest(VideoEntry e, Func<string, bool> isGame, bool withTime = false, Func<float[], string?>? voiceName = null,
        Func<VideoEntry, string?>? scene = null)
    {
        // The game: a tag that names one, or the game's own words on screen (SPIKE PLANTED is Valorant).
        var game = e.Tags.FirstOrDefault(isGame) is { } g ? Pretty(g) : ScreenWords.Enabled ? ScreenWords.Game(e.Screen) : null;
        var people = e.People.Where(p => p.Trim().Length > 0).Select(p => p.Trim()).ToList();
        var local = e.ModifiedUtc.ToLocalTime();
        var culture = CultureInfo.CurrentCulture;
        string Stamp() => local.ToString(local.Year == DateTime.Now.Year ? "d MMM" : "d MMM yyyy", culture) + (withTime ? " " + local.ToString("t", culture) : "");

        if (Moment(e, voiceName, people, scene) is { } headline)
        {
            // What happened, then where and with whom, when it still reads as a title.
            var context = game ?? "";
            var named = people.Where(p => !headline.Contains(p, StringComparison.OrdinalIgnoreCase)).ToList();
            if (named.Count > 0 && headline.Length + context.Length < 56)
                context = context.Length > 0 ? F("{0} with {1}", context, Names(named)) : F("with {0}", Names(named));
            var title = context.Length > 0 ? $"{headline} · {context}" : headline;
            return withTime ? $"{title} · {Stamp()}" : title;
        }

        if (game is null && people.Count == 0) return null;
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
        return $"{what} · {Stamp()}";
    }

    /// <summary>Seconds of laughter that count as a big laugh, and of cheering or shouting as a loud moment.</summary>
    private const double BigLaugh = 3, LoudMoment = 2.5;

    /// <summary>What happened in the clip, from what was heard, or null when nothing stands out.</summary>
    internal static string? Moment(VideoEntry e, Func<float[], string?>? voiceName, IReadOnlyList<string>? people = null,
        Func<VideoEntry, string?>? scene = null)
    {
        var laughs = e.Sound?.Laugh ?? new();
        var hype = e.Sound?.Hype ?? new();
        var laughAt = FirstCluster(laughs);
        var best = e.Smart is { BestMs: >= 0 } sm ? sm.BestMs : -1;

        // The line worth quoting: short, said near the big moment or just before a laugh, and not just "yeah".
        (SpeechLine Line, string Text, double Score, bool Setup)? pick = null;
        foreach (var l in e.Speech?.Lines ?? new())
        {
            var text = Clean(l.Text);
            var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length is < 2 or > 8 || text.Length > 48) continue;
            if (words.All(w => Filler.Contains(w.Trim(Punct).ToLowerInvariant()))) continue;
            double score = 0;
            if (text.EndsWith('!') || text.EndsWith('?')) score += 1.5;
            if (words.Length is >= 3 and <= 6) score += 0.5;
            var setup = laughAt >= 0 && l.End <= laughAt + 800 && laughAt - l.End < 4000;
            if (setup) score += 2;
            if (best >= 0 && Math.Abs((l.Start + l.End) / 2 - best) < 3000) score += 1.5;
            if (hype.Any(h => h.At >= l.Start - 500 && h.At <= l.End + 1500)) score += 1;
            if (pick is null || score > pick.Value.Score) pick = (l, text, score, setup);
        }
        // A banner the game put on screen (VICTORY ROYALE, ACE): a big one beats a quote, unless the quote set off a laugh.
        var banner = ScreenWords.Event(e.Screen);
        var funny = pick is { Score: >= 1.5, Setup: true } && laughs.Count * 0.48 >= BigLaugh;
        if (banner is { Rank: >= 7 } big && !funny) return WithName(big, e, people);
        if (pick is { Score: >= 1.5 } q)
        {
            var who = Speaker(e, q.Line, voiceName);
            if (funny) return who is null ? F("Laughing at “{0}”", q.Text) : F("{0} cracks everyone up: “{1}”", who, q.Text);
            return who is null ? F("“{0}”", q.Text) : F("{0}: “{1}”", who, q.Text);
        }
        if (banner is { } b) return WithName(b, e, people);
        if (laughs.Count * 0.48 >= BigLaugh) return T("Cracking up");
        // What the picture shows, when the visual model is sure (underwater, a car crash, out in space).
        if (scene?.Invoke(e) is { } seen) return seen;
        if (hype.Count * 0.48 >= LoudMoment && e.Smart is not { Excitement: < 0.3 }) return T("Everyone yelling");
        return null;
    }

    /// <summary>
    /// "Ace by Alex" when a friend's name is read in the same line as the banner ("ALEX ACED THE ROUND"):
    /// the kill feed and the banner name the player. Only a name on the same line counts.
    /// </summary>
    private static string WithName((string Headline, int Rank, long At) banner, VideoEntry e, IReadOnlyList<string>? people)
    {
        if (people is not { Count: > 0 } || e.Screen is not { } screen) return banner.Headline;
        foreach (var line in screen.Lines.Where(l => l.At == banner.At))
        {
            var key = " " + ScreenService.Key(line.Text) + " ";
            if (people.FirstOrDefault(p => p.Length >= 3 && key.Contains(" " + ScreenService.Key(p) + " ", StringComparison.Ordinal)) is { } name)
                return F("{0} by {1}", banner.Headline, name);
        }
        return banner.Headline;
    }

    /// <summary>The friend whose voice says this line, when Mochi knows it.</summary>
    private static string? Speaker(VideoEntry e, SpeechLine line, Func<float[], string?>? voiceName)
    {
        if (voiceName is null || e.Voices is not { } v) return null;
        var s = v.Speakers.FirstOrDefault(sp => sp.Lines.Any(r => r.Length == 2 && r[0] <= line.Start + 200 && r[1] >= line.End - 200));
        return s is null || s.Vec.Length == 0 ? null : voiceName(s.Vec);
    }

    /// <summary>Where the first real laugh starts (ms): two or more laughing moments close together; -1 when none.</summary>
    private static long FirstCluster(List<SoundMoment> laughs)
    {
        var at = laughs.Select(m => m.At).OrderBy(x => x).ToList();
        for (var i = 0; i + 1 < at.Count; i++)
            if (at[i + 1] - at[i] <= 1500) return at[i];
        return -1;
    }

    private static readonly char[] Punct = { '.', ',', '!', '?', '…', '"', '\'', '“', '”', '-' };
    private static readonly HashSet<string> Filler = new(StringComparer.OrdinalIgnoreCase)
    {
        "yeah", "yes", "no", "okay", "ok", "oh", "uh", "um", "hmm", "what", "so", "and", "the", "a", "i", "it", "is", "that", "like",
        "right", "yep", "nope", "wait", "hey", "go", "ja", "nej", "okej", "åh", "va", "så", "och", "det", "är", "jag", "vad", "hm",
    };

    /// <summary>A spoken line tidied for a title: no stray quotes, dashes or "...", one space, a capital first letter.</summary>
    private static string Clean(string text)
    {
        var t = string.Join(' ', text.Replace('"', ' ').Replace('“', ' ').Replace('”', ' ').Replace("...", "…")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim().TrimStart('-', '–', ' ').TrimEnd(',', ';', ':', '…', ' ');
        if (t.EndsWith('.') && !t.EndsWith("..")) t = t[..^1];
        return t.Length == 0 ? t : char.ToUpper(t[0], CultureInfo.CurrentCulture) + t[1..];
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
        if (t.Length <= 3 && !t.Contains(' ')) return t.ToUpperInvariant(); // ark → ARK, cs2 → CS2
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
