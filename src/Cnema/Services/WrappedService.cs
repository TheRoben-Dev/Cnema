using System.Globalization;
using Cnema.Models;

namespace Cnema.Services;

/// <summary>The numbers on the year's stat cards.</summary>
public sealed record WrappedStats(
    int Year, int Moments, long TotalMs, int Days,
    IReadOnlyList<(string Name, int Count)> Games,
    IReadOnlyList<(string Name, int Count)> Friends,
    DateTime? BiggestNight, int BiggestNightCount, IReadOnlyList<string> BiggestNightWho,
    double LaughSeconds, string? LaughFriend);

/// <summary>One stat card: what it says, and how long it stays at least.</summary>
public sealed record WrappedCard(string Kind, string Kicker, string Big, string Small, double MinSeconds, string Quote = "");

/// <summary>The year's video before it meets the music: its cards and its parts, in order.</summary>
public sealed record WrappedScript(IReadOnlyList<WrappedCard> Cards, IReadOnlyList<WrappedAct> Acts)
{
    public double TargetSeconds => WrappedDirector.TargetSeconds(Acts, Cards.Select(c => c.MinSeconds).ToList());
    public int Clips => Acts.SelectMany(a => a.Pool).Distinct().Count();
}

/// <summary>
/// "Wrapped": the year in one video, made on this computer. Picks the year's best moments, lets the director
/// cut them to the person's own songs, draws the stat cards, and renders with the bundled ffmpeg at low
/// priority: every shot to a small intermediate first (so only one video is open at a time), then joined
/// without re-encoding the picture while the soundtrack is mixed.
/// </summary>
public static class WrappedService
{
    public const int Fps = 30;
    /// <summary>Marks a finished part (bumped when parts are drawn differently, so a resumed render redoes old ones).</summary>
    private static string Done => PulseFx.Enabled ? ".v3" : ".v2";
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Videos Savefile made itself (montages, earlier Wrapped videos) don't go in.</summary>
    public static bool IsOwnOutput(string path)
    {
        var dir = Path.GetFileName(Path.GetDirectoryName(path) ?? "");
        return dir is "Montages" or "Wrapped" || Path.GetFileName(path).StartsWith("Savefile Wrapped", StringComparison.OrdinalIgnoreCase);
    }

    public static List<VideoEntry> YearClips(IEnumerable<VideoEntry> all, int year) => all
        .Where(e => e.ModifiedUtc.ToLocalTime().Year == year && e.DurationMs >= 2000 && !IsOwnOutput(e.Path))
        .ToList();

    /// <summary>How good a clip is for the reel: best moments, favourites, laughter, hype, friends, a note.</summary>
    public static double Score(VideoEntry e)
    {
        var laugh = e.Sound is { Laugh.Count: > 1 } s ? s.LaughSeconds : 0;
        var hype = e.Sound is { Hype.Count: > 1 } h ? h.HypeSeconds : 0;
        var score = (e.Smart?.Excitement ?? 0.3) * 3 + (e.Favorite ? 2 : 0) + Math.Min(2.5, laugh / 2) + Math.Min(1, hype / 3)
                    + (e.People.Count > 0 ? 0.6 : 0) + (string.IsNullOrWhiteSpace(e.Note) ? 0 : 0.3);
        if (!CutGuide.Enabled) return score;
        // With smart cuts: a banner on screen (a win, an ace), a moment you marked, friends heard for sure.
        if (ScreenWords.Event(e.Screen) is { } banner) score += banner.Rank * 0.12;
        if (e.Bookmarks.Count > 0) score += 0.4;
        if (CutGuide.NameOf is { } nameOf && e.Voices is { Speakers.Count: > 0 } v)
            score += Math.Min(0.6, v.Speakers.Count(s => s.Ms >= 2500 && nameOf(s) is not null) * 0.2);
        return score;
    }

    /// <summary>The year's moments for the reel, best first: at most <paramref name="max"/>.</summary>
    public static List<WrappedMoment> Moments(IEnumerable<VideoEntry> clips, Func<VideoEntry, string?> gameOf, int max = 400) => clips
        .Select(e => (e, Score: Score(e)))
        .OrderByDescending(x => x.Score).Take(max)
        .Select(x => (x.e, x.Score, W: LaughWindow(x.e), G: CutGuide.For(x.e)))
        .Select(x => new WrappedMoment(x.e.Path, x.e.DurationMs, Focus(x.e, x.G), x.Score, Voice(x.e))
        {
            Guide = x.G is { HasSignals: true } ? x.G : null,
            Look = x.G is not null && x.e.Visual is { Frames.Length: > 0 } vis ? vis.Mean : null,
            Game = gameOf(x.e), People = x.e.People.ToList(), Local = x.e.ModifiedUtc.ToLocalTime(),
            Laugh = x.e.Sound is { Laugh.Count: > 1 } so ? so.LaughSeconds : 0,
            LaughFromMs = x.W?.From ?? -1, LaughToMs = x.W?.To ?? -1, SetupMs = x.W?.Setup ?? -1,
        })
        .ToList();

    /// <summary>
    /// The biggest laugh in a clip and what led up to it: a joke lands only with its setup. The laugh is the run
    /// of laughter around the densest moment; the setup starts 4 to 10 seconds before it, at the start of the
    /// lines spoken just before (never mid-sentence), or 6 seconds before when nothing was transcribed.
    /// </summary>
    internal static (long From, long To, long Setup)? LaughWindow(VideoEntry e)
    {
        if (e.Sound is not { Laugh.Count: >= 2 } s) return null;
        var laughs = s.Laugh.Select(m => m.At).OrderBy(t => t).ToList();
        var focus = laughs.MaxBy(t => laughs.Count(o => Math.Abs(o - t) <= 3000));
        var i = laughs.IndexOf(focus);
        var a = i;
        var b = i;
        while (a > 0 && laughs[a] - laughs[a - 1] <= 1500) a--;
        while (b < laughs.Count - 1 && laughs[b + 1] - laughs[b] <= 1500) b++;
        var from = Math.Max(0, laughs[a] - 300);
        var to = laughs[b] + 600;
        if (e.DurationMs > 0) to = Math.Min(to, e.DurationMs);
        var setup = from - 6000;
        if (e.Speech is { Lines.Count: > 0 } sp)
        {
            // Walk back through the lines said just before the laugh while they run on from each other.
            var lines = sp.Lines.Where(l => l.Start < from + 500 && l.End > from - 10000).OrderByDescending(l => l.Start).ToList();
            long? start = null;
            long next = from;
            foreach (var l in lines)
            {
                if (next - l.End > 2000 || from - l.Start > 10000) break;
                start = l.Start;
                next = l.Start;
            }
            if (start is { } st) setup = Math.Min(st - 300, from - 4000);
            // Never start in the middle of something someone is saying.
            if (sp.Lines.FirstOrDefault(l => l.Start < setup && l.End > setup && from - l.Start <= 12000) is { } mid) setup = mid.Start - 300;
        }
        setup = Math.Clamp(setup, Math.Max(0, from - 12000), from);
        return (from, to, setup);
    }

    /// <summary>The peak of a clip: the densest laughter when there is some, else the best moment, else a third in.</summary>
    private static long Focus(VideoEntry e, CutGuide? guide = null)
    {
        // With smart cuts, every pass has a say: laughs, cheers, the best moment, a banner, a marked moment.
        if (guide is { Peak: >= 0 }) return guide.Peak;
        if (e.Sound is { Laugh.Count: >= 3 } s)
        {
            var best = s.Laugh.MaxBy(m => s.Laugh.Count(o => Math.Abs(o.At - m.At) <= 3000))!;
            return best.At;
        }
        if (e.Smart is { BestMs: >= 0 } smart) return smart.BestMs;
        return e.DurationMs / 3;
    }

    /// <summary>Where people talk or laugh in a clip (ms), so the music can make room.</summary>
    private static List<(long, long)> Voice(VideoEntry e)
    {
        var list = new List<(long, long)>();
        if (e.Speech is { } sp) list.AddRange(sp.Lines.Select(l => (l.Start, l.End)));
        if (e.Sound is { } so) list.AddRange(so.Laugh.Concat(so.Hype).Select(m => (m.At - 400, m.At + 600)));
        return list;
    }

    public static WrappedStats Stats(IReadOnlyList<VideoEntry> year, int y, bool people, Func<VideoEntry, string?> gameOf)
    {
        var days = year.Select(e => e.ModifiedUtc.ToLocalTime().Date).Distinct().Count();
        var games = year.Select(gameOf).OfType<string>().GroupBy(g => g, StringComparer.OrdinalIgnoreCase)
            .Select(g => (g.First(), g.Count())).OrderByDescending(g => g.Item2).Take(3).ToList();
        var friends = people
            ? year.SelectMany(e => e.People).GroupBy(p => p, StringComparer.OrdinalIgnoreCase)
                .Select(g => (g.First(), g.Count())).OrderByDescending(g => g.Item2).Take(3).ToList()
            : new List<(string, int)>();
        var night = year.GroupBy(e => NightOf(e.ModifiedUtc.ToLocalTime())).OrderByDescending(g => g.Count()).ThenByDescending(g => g.Key).FirstOrDefault();
        var who = night is null || !people ? new List<string>()
            : night.SelectMany(e => e.People).GroupBy(p => p, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).Select(g => g.First()).Take(3).ToList();
        double Laugh(VideoEntry e) => e.Sound is { Laugh.Count: > 1 } s ? s.LaughSeconds : 0;
        var laugh = year.Sum(Laugh);
        var laughFriend = people
            ? year.Where(e => Laugh(e) > 0).SelectMany(e => e.People.Select(p => (p, Laugh(e))))
                .GroupBy(x => x.p, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Sum(x => x.Item2)).Select(g => g.First().p).FirstOrDefault()
            : null;
        return new WrappedStats(y, year.Count, year.Sum(e => e.DurationMs), days, games, friends,
            night is { } n && n.Count() > 1 ? n.Key : null, night?.Count() ?? 0, who, laugh, laughFriend);
    }

    /// <summary>A night runs until 6 in the morning: a 2 AM clip belongs to the evening before.</summary>
    internal static DateTime NightOf(DateTime local) => (local.Hour < 6 ? local.AddDays(-1) : local).Date;

    /// <summary>
    /// The year's video, part by part: the intro and a quick burst of the best, the year in numbers, month by
    /// month, then chapters (your games, your people with a card per friend, your biggest night, best laughs),
    /// your clip personality, then and now, a hidden gem, your top five, and the funniest moment to close.
    /// Every number comes from the clips themselves: Savefile knows what you saved, not how long you played.
    /// </summary>
    /// <summary>
    /// The year's video. With <paramref name="friend"/> it's a shorter cut of only the clips with that friend:
    /// what you played together, your biggest night and laughs together, your best clips together.
    /// </summary>
    public static WrappedScript Script(IReadOnlyList<VideoEntry> clips, int year, bool people, Func<VideoEntry, string?> gameOf, string? friend = null)
    {
        if (friend is not null)
            clips = clips.Where(e => e.People.Any(p => string.Equals(p.Trim(), friend, StringComparison.OrdinalIgnoreCase))).ToList();
        var s = Stats(clips, year, people, gameOf);
        var moments = Moments(clips, gameOf);
        var byPath = clips.ToDictionary(c => c.Path, StringComparer.Ordinal);
        var c = CultureInfo.CurrentCulture;
        var cards = new List<WrappedCard>();
        var acts = new List<WrappedAct>();
        var chapter = 0;
        int Card(WrappedCard card) { cards.Add(card); return cards.Count - 1; }
        void Chapter(string title)
        {
            chapter++;
            acts.Add(new WrappedAct(Card(new("chapter", F("Chapter {0}", chapter), title, "", WrappedDirector.ChapterSeconds)), WrappedActStyle.Card, Array.Empty<WrappedMoment>()));
        }
        var best = moments.Take(24).ToList();

        // Hello, and a taste of the best.
        acts.Add(new WrappedAct(Card(new("intro", "savefile", friend is null ? F("Your {0}", year) : F("Your {0} with {1}", year, friend), T("wrapped"),
            WrappedDirector.IntroSeconds)), WrappedActStyle.Burst, best, 0.6));
        acts.Add(new WrappedAct(Card(new("moments", friend is null ? T("This year you saved") : F("This year you and {0} saved", friend), P(s.Moments, "{0} clip", "{0:N0} clips"),
            F("{0} of memories, across {1}.", Span(s.TotalMs), P(s.Days, "{0} day", "{0:N0} days")), WrappedDirector.CardSeconds)), WrappedActStyle.Card, Array.Empty<WrappedMoment>()));

        // Month by month: the best clip of each month with clips.
        var months = moments.GroupBy(m => m.Local.Month).OrderBy(g => g.Key).Select(g => g.OrderByDescending(m => m.Score).First()).ToList();
        if (months.Count >= 3 && friend is null)
        {
            acts.Add(new WrappedAct(Card(new("chapter", T("Your year"), T("Month by month"), "", WrappedDirector.ChapterSeconds)), WrappedActStyle.Flash, months, 0,
                months.Select(m => c.DateTimeFormat.GetMonthName(m.Local.Month)).ToList<string?>()));
        }

        // Your games.
        if (s.Games.Count > 0)
        {
            Chapter(T("Your games"));
            var (game, count) = s.Games[0];
            var share = s.Moments > 0 ? (int)Math.Round(100.0 * count / s.Moments) : 0;
            var others = s.Games.Skip(1).Select(g => F("{0} ({1})", SmartTitles.Pretty(g.Name), g.Count)).ToList();
            acts.Add(new WrappedAct(Card(new("game", friend is null ? T("Your most-clipped game") : T("What you clipped most together"), SmartTitles.Pretty(game),
                    friend is null ? F("{0} · {1}% of your clips", P(count, "{0} clip", "{0:N0} clips"), share)
                        : F("{0} · {1}% of your clips together", P(count, "{0} clip", "{0:N0} clips"), share), WrappedDirector.CardSeconds,
                    others.Count > 0 ? F("Then {0}", string.Join(", ", others)) : "")),
                WrappedActStyle.Mix, moments.Where(m => s.Games.Any(g => string.Equals(g.Name, m.Game, StringComparison.OrdinalIgnoreCase))).ToList(), 2));
        }

        // Your people: a card per friend in the crew, then your clips together.
        if (friend is not null)
        {
            // One card for the friend: the clips together and something they said.
            var quote = Quote(clips);
            var others = s.Friends.Where(f => !string.Equals(f.Name, friend, StringComparison.OrdinalIgnoreCase)).Select(f => f.Name).Take(3).ToList();
            acts.Add(new WrappedAct(Card(new("crew", T("Your crew"), friend,
                    others.Count > 0 ? F("Often with {0} too", Join(others)) : P(s.Moments, "{0} clip together", "{0:N0} clips together"),
                    WrappedDirector.CardSeconds, quote is null ? "" : F("“{0}”, heard in a clip with {1}", quote, friend))),
                WrappedActStyle.Mix, moments.Skip(24).ToList(), 1.3));
        }
        else if (s.Friends.Count > 0)
        {
            Chapter(T("Your people"));
            foreach (var (name, count) in s.Friends)
            {
                var together = moments.Where(m => m.People.Contains(name, StringComparer.OrdinalIgnoreCase)).ToList();
                var game = together.Select(m => m.Game).OfType<string>().GroupBy(g => g, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key;
                var quote = Quote(together.Select(m => byPath.GetValueOrDefault(m.Path)).OfType<VideoEntry>());
                acts.Add(new WrappedAct(Card(new("crew", T("Your crew"), name,
                        game is null ? P(count, "{0} clip together", "{0:N0} clips together")
                            : F("{0}, most of them in {1}", P(count, "{0} clip together", "{0:N0} clips together"), SmartTitles.Pretty(game)),
                        WrappedDirector.CardSeconds, quote is null ? "" : F("“{0}”, heard in a clip with {1}", quote, name))),
                    WrappedActStyle.Mix, together, 1.3));
            }
        }

        // Your biggest night.
        if (s.BiggestNight is { } night)
        {
            Chapter(T("Your biggest night"));
            var that = moments.Where(m => NightOf(m.Local) == night).OrderBy(m => m.Local).ToList();
            acts.Add(new WrappedAct(Card(new("night", friend is null ? T("The night you saved the most clips") : F("Your biggest night with {0}", friend), night.ToString("dddd d MMMM", c),
                    s.BiggestNightWho.Count > 0 ? F("{0} with {1}", P(s.BiggestNightCount, "{0} clip", "{0} clips"), Join(s.BiggestNightWho))
                        : P(s.BiggestNightCount, "{0} clip", "{0} clips"), WrappedDirector.CardSeconds)),
                WrappedActStyle.Mix, that, 1));
        }

        // Best laughs.
        var laughs = moments.Where(m => m.Laugh >= 2).OrderByDescending(m => m.Laugh).ToList();
        if (s.LaughSeconds >= 20 && laughs.Count >= 2)
        {
            Chapter(T("Best laughs"));
            acts.Add(new WrappedAct(Card(new("laughs", T("Laughter caught on your clips"), Span((long)(s.LaughSeconds * 1000)),
                    s.LaughFriend is { } f ? F("Most of it in clips with {0}.", f) : T("Only what your clips caught, so probably a lot more."), WrappedDirector.CardSeconds)),
                WrappedActStyle.Story, laughs.Skip(1).Take(5).ToList(), 0, null, 12));
        }

        // Your clip personality, then everything else that was good.
        if (friend is null && Personality(clips, s) is { } who)
            acts.Add(new WrappedAct(Card(new("personality", T("Your clip personality"), who.Label, who.Why, WrappedDirector.CardSeconds, T("Based on when and what you clipped."))),
                WrappedActStyle.Burst, moments.Skip(24).Take(120).ToList(), 1.2));

        // Then and now.
        var ordered = moments.OrderBy(m => m.Local).ToList();
        if (ordered.Count >= 6)
        {
            var first = ordered[0];
            var lastOne = ordered[^1];
            acts.Add(new WrappedAct(Card(new("chapter", T("Then and now"), T("Where it started"), "", WrappedDirector.ChapterSeconds)), WrappedActStyle.Story,
                new[] { first, lastOne }, 0, new string?[]
                {
                    F("Your first clip of {0} · {1}", year, first.Local.ToString("d MMMM", c)),
                    F("Your latest · {0}", lastOne.Local.ToString("d MMMM", c)),
                }, 7));
        }

        // A hidden gem: one of the best, never opened in Savefile.
        var gem = friend is not null ? null : moments.Take(60).FirstOrDefault(m => byPath.GetValueOrDefault(m.Path) is { LastPlayedUtc: null, Favorite: false });
        if (gem is not null)
            acts.Add(new WrappedAct(Card(new("gem", T("A hidden gem"), T("You haven't watched this one yet"),
                T("One of your best clips this year, and you've never opened it in Savefile."), WrappedDirector.CardSeconds)), WrappedActStyle.Story, new[] { gem }, 0,
                new string?[] { null }, 9));

        // Your top five, and the funniest moment to close.
        var closer = laughs.FirstOrDefault();
        var top = moments.Where(m => m != closer && m != gem).Take(5).Reverse().ToList();
        if (top.Count >= 3)
            acts.Add(new WrappedAct(Card(new("chapter", T("The best of the best"), F("Your top {0}", top.Count), "", WrappedDirector.ChapterSeconds)), WrappedActStyle.Story,
                top, 0, top.Select((_, i) => (string?)F("#{0}", top.Count - i)).ToList(), 8));
        if (closer is not null)
            acts.Add(new WrappedAct(Card(new("chapter", T("And to close"), T("Your funniest moment"), "", WrappedDirector.ChapterSeconds)), WrappedActStyle.Story,
                new[] { closer }, 0, new string?[] { null }, 16));

        acts.Add(new WrappedAct(Card(new("outro", friend is null ? F("That was your {0}.", year) : F("That was {0} with {1}.", year, friend),
                T("Some saves you never overwrite."), F("See you in {0}.", year + 1), WrappedDirector.OutroSeconds)),
            WrappedActStyle.Card, Array.Empty<WrappedMoment>()));
        return new WrappedScript(cards, acts);
    }

    /// <summary>A short line someone said in these clips, for a friend's card (3 to 8 words, from the best clips first).</summary>
    private static string? Quote(IEnumerable<VideoEntry> clips)
    {
        foreach (var e in clips.OrderByDescending(Score))
        {
            if (e.Speech is not { Lines.Count: > 0 } sp) continue;
            var line = sp.Lines.Select(l => l.Text.Trim().Trim('.', ' ')).Where(t =>
            {
                var words = t.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
                return words is >= 3 and <= 8 && !t.Contains('[') && !t.Contains('(');
            }).OrderByDescending(t => t.Contains('!')).FirstOrDefault();
            if (line is not null) return line;
        }
        return null;
    }

    /// <summary>A playful label from the clips themselves: when they were saved, who's in them, what's in them.</summary>
    internal static (string Label, string Why)? Personality(IReadOnlyList<VideoEntry> clips, WrappedStats s)
    {
        if (clips.Count < 10) return null;
        var n = (double)clips.Count;
        var traits = new List<(double Strength, string Label, string Why)>();
        var late = clips.Count(e => e.ModifiedUtc.ToLocalTime().Hour is >= 22 or < 5) / n;
        if (late >= 0.35) traits.Add((late, T("Night owl"), F("{0:0}% of your clips were saved after 10 PM.", late * 100)));
        var weekend = clips.Count(e => e.ModifiedUtc.ToLocalTime().DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday or DayOfWeek.Friday) / n;
        if (weekend >= 0.6) traits.Add((weekend * 0.9, T("Weekend warrior"), F("{0:0}% of your clips are from Friday to Sunday.", weekend * 100)));
        if (s.Friends.Count > 0 && s.Friends[0].Count / n >= 0.35)
            traits.Add((s.Friends[0].Count / n + 0.1, F("Duo with {0}", s.Friends[0].Name), F("{0} is in {1:0}% of your clips.", s.Friends[0].Name, s.Friends[0].Count / n * 100)));
        var laughing = clips.Count(e => e.Sound is { Laugh.Count: > 2 }) / n;
        if (laughing >= 0.25) traits.Add((laughing + 0.2, T("Chaos gremlin"), F("There's laughter in {0:0}% of your clips.", laughing * 100)));
        if (s.Games.Count > 0 && s.Games[0].Count / n >= 0.55)
            traits.Add((s.Games[0].Count / n, F("{0} loyalist", SmartTitles.Pretty(s.Games[0].Name)), F("{0:0}% of your clips are {1}.", s.Games[0].Count / n * 100, SmartTitles.Pretty(s.Games[0].Name))));
        if (s.Days > 0 && n / s.Days >= 5) traits.Add((Math.Min(1, n / s.Days / 10), T("Clip machine"), F("{0:0.#} clips on a day you clipped, on average.", n / s.Days)));
        if (traits.Count == 0) return (T("Moment collector"), F("{0} clips worth keeping this year.", clips.Count));
        var pick = traits.OrderByDescending(t => t.Strength).ToList();
        return pick.Count >= 2 && pick[1].Strength > 0.45
            ? ($"{pick[0].Label} · {pick[1].Label}", pick[0].Why + " " + pick[1].Why)
            : (pick[0].Label, pick[0].Why);
    }

    private static string Join(IReadOnlyList<string> names) =>
        names.Count switch { 1 => names[0], 2 => F("{0} and {1}", names[0], names[1]), _ => F("{0}, {1} and {2}", names[0], names[1], names[2]) };

    public static string Span(long ms)
    {
        var t = TimeSpan.FromMilliseconds(ms);
        return t.TotalHours >= 1 ? F("{0} h {1} min", (int)t.TotalHours, t.Minutes)
            : t.TotalMinutes >= 1 ? F("{0} min {1} s", (int)t.TotalMinutes, t.Seconds)
            : F("{0} s", (int)Math.Round(t.TotalSeconds));
    }

    // ---------- rendering ----------

    /// <summary>Draws card <c>n</c> as a video of the given length (beats are relative to its start).</summary>
    public delegate Task CardDrawer(int card, double seconds, IReadOnlyList<double> beats, IReadOnlyList<double> downbeats, string path, CancellationToken ct);

    /// <summary>Draws a caption as a see-through picture at the video's size.</summary>
    public delegate Task CaptionDrawer(string caption, string path, CancellationToken ct);

    /// <summary>Extra card frames after its end, for the transition into the next shot.</summary>
    private const double Tail = 1.0;

    /// <summary>
    /// Renders the plan to <paramref name="output"/>, working in <paramref name="work"/>: every shot becomes a
    /// finished part there (with its transition in, camera move and caption), so a render that stops (the app
    /// quit, the computer slept) picks up at the first part that isn't done. Progress 0..1.
    /// </summary>
    public static async Task RenderAsync(WrappedPlan plan, string work, int width, int height, string output,
        CardDrawer drawCard, CaptionDrawer drawCaption, IProgress<double>? progress, CancellationToken ct)
    {
        // ffmpeg reads the parts list relative to its own folder, so every path is made absolute.
        work = Path.GetFullPath(work);
        output = Path.GetFullPath(output);
        var ffmpeg = ToolLocator.Ffmpeg ?? throw new InvalidOperationException(T("ffmpeg not found."));
        Directory.CreateDirectory(work);
        var music = plan.Songs.Count > 0;
        // The beat effects need the song's beats: without music, the video looks as it did.
        var pulse = PulseFx.Enabled && music;
        var threads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4).ToString(Inv);
        var parts = new List<string>();
        var infos = new Dictionary<string, StreamInfo?>(StringComparer.Ordinal);
        async Task<StreamInfo?> Info(string path) => infos.TryGetValue(path, out var i) ? i : infos[path] = await MediaProbe.InfoAsync(path, ct);
        string Fit(StreamInfo? info)
        {
            // Fill the frame when the clip is about 16:9; letterbox anything else (a phone clip).
            var aspect = info is { Width: > 0, Height: > 0 } ? (double)info.Width / info.Height : 16 / 9.0;
            return aspect is > 1.6 and < 1.95
                ? $"scale={width}:{height}:force_original_aspect_ratio=increase,crop={width}:{height}"
                : $"scale={width}:{height}:force_original_aspect_ratio=decrease,pad={width}:{height}:(ow-iw)/2:(oh-ih)/2:color=black";
        }
        string Zoom(double z) => z <= 1.0005 ? "" : $",scale={Even(width * z)}:{Even(height * z)},crop={width}:{height}";
        int Even(double v) => (int)Math.Ceiling(v / 2) * 2;
        const string Norm = "fps=30,setsar=1,format=yuv420p,settb=AVTB,setpts=PTS-STARTPTS";
        (double From, double To) ZoomOf(WrappedShot s) => s.Move switch
        {
            WrappedMove.PushIn => (1, s.Story ? 1.045 : 1.06),
            WrappedMove.PullOut => (1.08, 1),
            _ => (1, 1),
        };
        string CardPath(int i) => Path.Combine(work, $"card{i:000}.mp4");

        for (var i = 0; i < plan.Shots.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var shot = plan.Shots[i];
            // Whole frames from the timeline's own clock, so nothing drifts against the music.
            var frames = (int)Math.Round(shot.End * Fps) - (int)Math.Round(shot.At * Fps);
            if (frames <= 0) continue;
            var len = frames / (double)Fps;
            var d = S(len);
            var part = Path.Combine(work, $"{i:000}.mkv");
            parts.Add(part);
            if (File.Exists(part + Done) && File.Exists(part)) { progress?.Report(0.7 * (i + 1) / plan.Shots.Count); continue; }

            if (shot.Kind == WrappedShotKind.Card && !File.Exists(CardPath(i) + ".done"))
            {
                var beats = plan.Beats.Where(b => b >= shot.At && b < shot.End + Tail).Select(b => b - shot.At).ToList();
                var downs = plan.Downbeats.Where(b => b >= shot.At && b < shot.End + Tail).Select(b => b - shot.At).ToList();
                await drawCard(shot.Card, len + Tail, beats, downs, CardPath(i), ct);
                await File.WriteAllTextAsync(CardPath(i) + ".done", "", ct);
            }

            var args = new List<string> { "-hide_banner", "-v", "error", "-nostdin", "-y" };
            var g = new List<string>();
            var n = 0;
            // The shot itself.
            string? audioIn = null;
            if (shot.Kind == WrappedShotKind.Card)
            {
                args.AddRange(new[] { "-t", d, "-i", CardPath(i) });
                g.Add($"[{n}:v:0]{Norm},tpad=stop_mode=clone:stop_duration={d},trim=duration={d}[cur]");
                n++;
            }
            else
            {
                var info = await Info(shot.Path!);
                args.AddRange(new[] { "-ss", S(shot.StartMs / 1000.0), "-t", d, "-i", shot.Path! });
                var (z0, z1) = ZoomOf(shot);
                string move;
                if (pulse)
                {
                    // Eased moves plus a punch on every beat of the song (softer where people talk).
                    var hits = PulseFx.Hits(plan.Beats, plan.Downbeats, plan.Drops, plan.BeatEnergy, shot.At, shot.At + len);
                    move = PulseFx.Move(width, height, Fps, frames, z0, z1, shot.Move == WrappedMove.PullOut, hits, shot.Story || shot.Voice ? 0.45 : 1)
                           + PulseFx.Flash(hits, shot.Story ? 0.5 : 1);
                }
                else
                {
                    // Moves are drawn from a frame twice the size, so the slow push in stays smooth.
                    move = z0 == z1 ? ""
                        : $",scale={width * 2}:{height * 2}:flags=bicubic,zoompan=z='{S(z0)}+({S(z1 - z0)})*min(1,on/{(shot.Move == WrappedMove.PullOut ? Math.Min(frames, 18) : frames)})':" +
                          $"x='iw/2-(iw/zoom/2)':y='ih/2-(ih/zoom/2)':d=1:s={width}x{height}:fps={Fps}";
                }
                g.Add($"[{n}:v:0]fps={Fps},{Fit(info)}{move},{Norm},tpad=stop_mode=clone:stop_duration={d},trim=duration={d}[cur]");
                if (info is { AudioStreams: > 0 }) audioIn = $"[{n}:a:0]";
                n++;
            }
            var video = "[cur]";
            // The transition in: the shot before carries on underneath for a moment and hands over.
            if (shot.Transition is { } tr && i > 0 && shot.TransitionSeconds > 0.05)
            {
                var prev = plan.Shots[i - 1];
                var td = S(Math.Min(shot.TransitionSeconds, len * 0.45));
                if (prev.Kind == WrappedShotKind.Card)
                {
                    var prevFrames = (int)Math.Round(prev.End * Fps) - (int)Math.Round(prev.At * Fps);
                    args.AddRange(new[] { "-ss", S(prevFrames / (double)Fps), "-t", td, "-i", CardPath(i - 1) });
                    g.Add($"[{n}:v:0]{Norm},tpad=stop_mode=clone:stop_duration={td},trim=duration={td}[prev]");
                }
                else
                {
                    var info = await Info(prev.Path!);
                    args.AddRange(new[] { "-ss", S(prev.StartMs / 1000.0 + prev.Length), "-t", td, "-i", prev.Path! });
                    g.Add($"[{n}:v:0]fps={Fps},{Fit(info)}{Zoom(ZoomOf(prev).To)},{Norm},tpad=stop_mode=clone:stop_duration={td},trim=duration={td}[prev]");
                }
                n++;
                g.Add($"[prev]{video}xfade=transition={tr}:duration={td}:offset=0[x]");
                video = "[x]";
            }
            // The caption, fading in and out over the picture.
            if (shot.Caption is { Length: > 0 } caption)
            {
                var capPath = Path.Combine(work, $"cap{i:000}.png");
                if (!File.Exists(capPath))
                {
                    // Drawn aside and moved in, so a quit halfway never leaves half a picture for the resume to trip on.
                    var drawing = Path.Combine(work, $"cap{i:000}.drawing.png");
                    await drawCaption(caption, drawing, ct);
                    File.Move(drawing, capPath, overwrite: true);
                }
                args.AddRange(new[] { "-loop", "1", "-framerate", Fps.ToString(Inv), "-t", d, "-i", capPath });
                var outAt = S(Math.Max(0.4, len - 0.45));
                g.Add($"[{n}:v:0]format=rgba,fade=t=in:st=0.15:d=0.3:alpha=1,fade=t=out:st={outAt}:d=0.3:alpha=1[cap]");
                g.Add($"{video}[cap]overlay=format=auto,format=yuv420p[ov]");
                video = "[ov]";
                n++;
            }
            if (pulse)
            {
                // The glow on whatever is bright, cards included (their white type gets a soft halo); cards flash on drops.
                var src = video;
                if (shot.Kind == WrappedShotKind.Card)
                {
                    var flash = PulseFx.Flash(PulseFx.Hits(plan.Beats, plan.Downbeats, plan.Drops, plan.BeatEnergy, shot.At, shot.At + len).Where(h => h.Power > 1.5).ToList());
                    if (flash.Length > 0) { g.Add($"{video}null{flash}[cf]"); src = "[cf]"; }
                }
                g.AddRange(PulseFx.Glow(src, "[gl]", width, height, "g", shot.Kind == WrappedShotKind.Card ? 0.7 : 0.45));
                video = "[gl]";
            }
            if (i == 0) { g.Add($"{video}fade=t=in:st=0:d=0.5[fi]"); video = "[fi]"; }
            if (i == plan.Shots.Count - 1) { g.Add($"{video}fade=t=out:st={S(Math.Max(0, len - 1.5))}:d=1.5[fo]"); video = "[fo]"; }
            if (pulse && (i == 0 || i == plan.Shots.Count - 1))
            {
                // The flowing glow along the bottom edge as the video opens, and again as it ends (over the fade to black).
                var glowPath = Path.Combine(work, "edge-glow.rgba");
                var strip = PulseFx.WriteEdgeGlow(glowPath, width, height);
                args.AddRange(new[] { "-f", "rawvideo", "-pix_fmt", "rgba", "-s", $"{strip.Width}x{strip.Height}", "-i", glowPath });
                var start = i == 0;
                g.AddRange(PulseFx.EdgeGlow(video, "[eg]", n, strip, width, height, len,
                    start ? 0.2 : Math.Max(0, len - 4), start ? Math.Min(len, 3.6) : len, "e"));
                video = "[eg]";
                n++;
            }
            g.Add($"{video}null[v]");

            // Sound: talking and laughing come through; longer moments keep theirs under the music; the rest sits low.
            if (audioIn is null)
            {
                args.AddRange(new[] { "-f", "lavfi", "-t", d, "-i", "anullsrc=r=48000:cl=stereo" });
                audioIn = $"[{n}:a]";
                n++;
            }
            var mix = !music || shot.Voice ? "0dB" : shot.Story ? "-7dB" : "-16dB";
            var key = shot.Voice ? "0.6" : "0";
            g.Add($"{audioIn}aformat=sample_rates=48000:channel_layouts=stereo,atrim=duration={d},asetpts=PTS-STARTPTS,apad=whole_dur={d},atrim=duration={d}," +
                  $"afade=t=in:d={(shot.Transition is not null ? "0.12" : "0.02")},afade=t=out:st={S(Math.Max(0, len - 0.03))}:d=0.03,volume={mix}[a]");
            // The key that pushes the music down: a steady level while someone talks or laughs, nothing otherwise.
            // (Keying on the game sound itself pumped the music, and left a hole after a loud shot.)
            g.Add($"aevalsrc=exprs={key}|{key}:s=48000:d={d},afade=t=in:d=0.15,afade=t=out:st={S(Math.Max(0, len - 0.15))}:d=0.15[k]");
            args.AddRange(new[]
            {
                "-filter_complex", string.Join(";", g), "-map", "[v]", "-map", "[a]", "-map", "[k]",
                "-c:v", "libx264", "-preset", "veryfast", "-crf", "19", "-r", Fps.ToString(Inv), "-g", (Fps * 2).ToString(Inv), "-threads", threads,
                "-c:a", "pcm_s16le", "-ar", "48000", "-frames:v", frames.ToString(Inv), part,
            });
            var r = await ProcessRunner.RunAsync(ffmpeg, args, ct: ct, background: true);
            if (r.ExitCode != 0 || !File.Exists(part))
                throw new InvalidOperationException(Loc.F("Couldn't prepare part {0} of the video: {1}", i + 1, r.StdErr.Trim()));
            await File.WriteAllTextAsync(part + Done, "", ct);
            progress?.Report(0.7 * (i + 1) / plan.Shots.Count);
        }

        // Join the parts as they are and lay the soundtrack under them.
        var list = Path.Combine(work, "parts.txt");
        await File.WriteAllLinesAsync(list, parts.Select(p => $"file '{p.Replace("'", "'\\''")}'"), ct);
        var total = plan.Shots.Count == 0 ? 0 : Math.Round(plan.Shots[^1].End * Fps) / Fps;
        var final = new List<string> { "-hide_banner", "-v", "error", "-nostdin", "-nostats", "-progress", "pipe:1", "-y", "-f", "concat", "-safe", "0", "-i", list };
        var fg = new List<string>();
        if (music)
        {
            for (var i = 0; i < plan.Songs.Count; i++)
            {
                var s = plan.Songs[i];
                final.AddRange(new[] { "-ss", S(s.In), "-t", S(s.Length), "-i", s.Song.Path });
                fg.Add($"[{i + 1}:a:0]aformat=sample_rates=48000:channel_layouts=stereo,asetpts=PTS-STARTPTS,volume={S(s.GainDb)}dB" +
                       (i == 0 && s.In > 0.05 ? ",afade=t=in:d=0.3" : "") + $"[m{i}]");
            }
            var m = "[m0]";
            for (var i = 1; i < plan.Songs.Count; i++)
            {
                // Equal-power: the sum stays as loud through the change.
                fg.Add($"{m}[m{i}]acrossfade=d={S(plan.Songs[i - 1].CrossfadeOut)}:c1=qsin:c2=qsin[x{i}]");
                m = $"[x{i}]";
            }
            var fade = plan.Songs[^1].FadeOut;
            fg.Add($"{m}afade=t=out:st={S(Math.Max(0, total - fade))}:d={S(fade)},apad,atrim=duration={S(total)}[mus]");
            // Voices and laughs push the music down; the rest of the game sound sits under it.
            fg.Add("[mus][0:a:1]sidechaincompress=threshold=0.1:ratio=4:attack=80:release=350[duck]");
            fg.Add("[duck][0:a:0]amix=inputs=2:normalize=0:duration=first,alimiter=limit=0.9[aout]");
        }
        else
        {
            fg.Add("[0:a:0]alimiter=limit=0.9[aout]");
        }
        var temp = TrimService.TempFor(output);
        final.AddRange(new[]
        {
            "-filter_complex", string.Join(";", fg), "-map", "0:v:0", "-map", "[aout]",
            // One clean encode at the end: a keyframe every 2 seconds, a steady 30 fps and a sensible size, so
            // every player can seek in it (the parts joined as they were froze the picture after a seek).
            "-c:v", "libx264", "-preset", "fast", "-crf", "21", "-maxrate", Rate(width, height), "-bufsize", Rate(width, height, 2),
            "-g", (Fps * 2).ToString(Inv), "-keyint_min", (Fps * 2).ToString(Inv), "-sc_threshold", "0", "-r", Fps.ToString(Inv), "-fps_mode", "cfr",
            "-pix_fmt", "yuv420p", "-profile:v", "high", "-threads", threads,
            "-c:a", "aac", "-b:a", "192k", "-ar", "48000", "-t", S(total), "-movflags", "+faststart", temp,
        });
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var done = await ProcessRunner.RunAsync(ffmpeg, final, line =>
        {
            if (line.StartsWith("out_time_us=", StringComparison.Ordinal) && long.TryParse(line.AsSpan(12), out var us) && total > 0)
                progress?.Report(0.7 + 0.3 * Math.Clamp(us / 1e6 / total, 0, 1));
        }, ct, background: true);
        if (done.ExitCode != 0 || !File.Exists(temp))
        {
            try { File.Delete(temp); } catch { /* nothing written */ }
            throw new InvalidOperationException(done.StdErr.Trim() is { Length: > 0 } e ? e : $"ffmpeg exited with {done.ExitCode}");
        }
        File.Move(temp, output, overwrite: true);
        progress?.Report(1);
    }

    private static string S(double v) => v.ToString("0.###", Inv);

    /// <summary>The most the picture may use: about 6 Mbit/s at 1080p (a 7-minute video stays near 300 MB).</summary>
    private static string Rate(int width, int height, double times = 1) =>
        $"{Math.Max(1500, 6000 * (width * (double)height) / (1920 * 1080) * times):0}k";
}
