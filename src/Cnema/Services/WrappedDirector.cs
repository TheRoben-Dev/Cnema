using Cnema.Models;

namespace Cnema.Services;

/// <summary>A moment that could go in the year's video: where it is, its peak, how good it is, where people talk or laugh.</summary>
public sealed record WrappedMoment(string Path, long DurationMs, long FocusMs, double Score, IReadOnlyList<(long From, long To)> Voice)
{
    public string? Game { get; init; }
    public IReadOnlyList<string> People { get; init; } = Array.Empty<string>();
    public DateTime Local { get; init; }
    /// <summary>Seconds of laughter heard in it.</summary>
    public double Laugh { get; init; }
    /// <summary>The biggest laugh (ms), from its first chuckle to its last; -1 when nobody laughs.</summary>
    public long LaughFromMs { get; init; } = -1;
    public long LaughToMs { get; init; } = -1;
    /// <summary>Where the moment that gets the laugh starts (ms): the line or the play before it.</summary>
    public long SetupMs { get; init; } = -1;
    public bool HasLaugh => LaughToMs > LaughFromMs && LaughFromMs >= 0;
    /// <summary>What the smart passes say about where to cut it (null: smart cuts off, or nothing known).</summary>
    public CutGuide? Guide { get; init; }
    /// <summary>What it looks like to the visual model (the frames averaged), so look-alikes aren't put side by side.</summary>
    public float[]? Look { get; init; }
}

public enum WrappedShotKind { Card, Clip }

/// <summary>How the picture moves during a shot: a slow push in, a quick settle back, or still.</summary>
public enum WrappedMove { None, PushIn, PullOut }

/// <summary>One shot of the video: a stat card or a stretch of a clip, placed on the timeline in seconds.</summary>
public sealed class WrappedShot
{
    public WrappedShotKind Kind { get; set; }
    public int Card { get; set; } = -1;
    public string? Path { get; set; }
    public long StartMs { get; set; }
    public double At { get; set; }
    public double Length { get; set; }
    /// <summary>Someone talks or laughs here: the clip's sound comes up and the music steps back.</summary>
    public bool Voice { get; set; }
    /// <summary>A moment that plays with its own sound (the music stays under it).</summary>
    public bool Story { get; set; }
    /// <summary>How loud the music is under this shot, 0 to 1.</summary>
    public float Energy { get; set; }
    /// <summary>How the shot comes in from the one before (an ffmpeg xfade name), or null for a straight cut.</summary>
    public string? Transition { get; set; }
    public double TransitionSeconds { get; set; }
    /// <summary>A line written over the picture ("March", "#3", "Your first clip of 2026").</summary>
    public string? Caption { get; set; }
    public WrappedMove Move { get; set; }
    public double End => At + Length;
}

/// <summary>One song's part of the soundtrack: from <see cref="In"/> to <see cref="Out"/> in the song, starting at <see cref="At"/>.</summary>
public sealed class WrappedSongPart
{
    public required SongInfo Song { get; init; }
    public double In { get; init; }
    public double Out { get; init; }
    public double At { get; set; }
    /// <summary>Overlap with the next song (an equal-power crossfade), 0 for the last.</summary>
    public double CrossfadeOut { get; set; }
    /// <summary>Fade at the very end: short when the song ends by itself, two bars when it's cut.</summary>
    public double FadeOut { get; set; }
    public double GainDb { get; set; }
    public bool EndsNaturally { get; set; }
    public double Length => Out - In;
}

public sealed class WrappedPlan
{
    public List<WrappedShot> Shots { get; set; } = new();
    public List<WrappedSongPart> Songs { get; set; } = new();
    public double Seconds { get; set; }
    public int Moments { get; set; }
    public double Bpm { get; set; }
    /// <summary>The soundtrack's beats on the video's timeline (seconds), and which of them start a bar.</summary>
    public List<double> Beats { get; set; } = new();
    public List<double> Downbeats { get; set; } = new();
    /// <summary>Each beat's loudness in the song (0 quiet to 1 loudest), lined up with <see cref="Beats"/>.</summary>
    public List<float> BeatEnergy { get; set; } = new();
    /// <summary>Where the music drops (a bar where it suddenly gets much louder), on the video's timeline.</summary>
    public List<double> Drops { get; set; } = new();
}

/// <summary>What a part of the video does with its clips.</summary>
public enum WrappedActStyle
{
    /// <summary>Only its card.</summary>
    Card,
    /// <summary>Quick cuts on the bar.</summary>
    Burst,
    /// <summary>Quick cuts with a longer moment, with its own sound, every few shots.</summary>
    Mix,
    /// <summary>One quick shot per clip, each with its caption (month by month).</summary>
    Flash,
    /// <summary>Each clip as a longer moment with its own sound and caption.</summary>
    Story,
}

/// <summary>
/// One part of the video: a card (or none), then clips from its own pool. <see cref="Weight"/> shares out the
/// time left once the fixed parts are placed; <see cref="Captions"/> line up with <see cref="Pool"/>.
/// </summary>
public sealed record WrappedAct(int Card, WrappedActStyle Style, IReadOnlyList<WrappedMoment> Pool, double Weight = 1,
    IReadOnlyList<string?>? Captions = null, double StorySeconds = 10);

/// <summary>
/// The "director" of the year's video: plain rules, no AI. It lays the songs end to end (ordered so tempo,
/// key and energy flow, each one entering on a phrase boundary with an equal-power crossfade whose downbeats
/// line up), then cuts the picture on bar lines: stat cards first, then the best moments, the strongest ones
/// on the loudest stretches of music, each placed so its peak lands on a beat, and a cut on the downbeat where
/// one song has taken over from the other.
/// </summary>
public static class WrappedDirector
{
    public const double IntroSeconds = 6, CardSeconds = 4.5, ChapterSeconds = 3.2, OutroSeconds = 7;
    /// <summary>The video aims for 5 to 10 minutes when there are enough clips for it.</summary>
    public const double MinSeconds = 300, MaxSeconds = 600;

    /// <summary>
    /// How long the video wants to be, in seconds: the cards plus what the clips can fill without repeating,
    /// between 5 and 10 minutes when the year has enough clips (shorter when it doesn't).
    /// </summary>
    public static double TargetSeconds(IReadOnlyList<WrappedAct> acts, IReadOnlyList<double> cardSeconds)
    {
        var cards = cardSeconds.Sum();
        var footage = acts.SelectMany(a => a.Pool).Distinct().Sum(m => Math.Min(m.DurationMs / 1000.0, 12));
        if (cards + footage * 0.7 < MinSeconds) return Math.Max(45, cards + footage * 0.7);
        return Math.Clamp(cards + footage * 0.4, MinSeconds, MaxSeconds);
    }

    /// <summary>How much music to bring, rounded up to the next half minute.</summary>
    public static double RecommendedMusicSeconds(double target) => Math.Ceiling(target / 30) * 30;

    internal sealed record Beat(double Time, bool Down, float Energy, float Drive);

    public static WrappedPlan Plan(IReadOnlyList<WrappedAct> acts, IReadOnlyList<double> cardSeconds, IReadOnlyList<SongInfo> songs)
    {
        var plan = new WrappedPlan();
        var usable = songs.Where(s => s.Usable).ToList();
        var target = TargetSeconds(acts, cardSeconds);
        var beats = new List<Beat>();
        var mustCut = new List<double>();
        if (usable.Count == 0)
        {
            // No music: a steady 120 BPM grid so the cuts still have a rhythm.
            plan.Seconds = target;
            plan.Bpm = 120;
            for (var i = 0; i * 0.5 < target; i++) beats.Add(new Beat(i * 0.5, i % 4 == 0, 0.5f, 1));
        }
        else
        {
            LaySongs(plan, usable, target);
            beats = Grid(plan, mustCut);
            plan.Bpm = plan.Songs[0].Song.Bpm;
        }
        plan.Beats = beats.Select(b => b.Time).ToList();
        plan.Downbeats = beats.Where(b => b.Down).Select(b => b.Time).ToList();
        if (usable.Count > 0)
        {
            plan.BeatEnergy = beats.Select(b => b.Energy).ToList();
            plan.Drops = Drops(beats);
            // A cut right on the drop is the moment the whole video hits.
            mustCut.AddRange(plan.Drops.Where(d => !mustCut.Any(c => Math.Abs(c - d) < 1)));
            mustCut.Sort();
        }
        CutPicture(plan, beats, mustCut, acts, cardSeconds);
        Transitions(plan, mustCut, plan.Drops);
        return plan;
    }

    // ---------- the soundtrack ----------

    /// <summary>The order of the songs with the smoothest run of tempo, key and energy (all orders tried for up to 6 songs).</summary>
    internal static List<SongInfo> Order(List<SongInfo> songs)
    {
        if (songs.Count <= 1 || songs.Count > 6) return songs.ToList();
        static double Step(SongInfo a, SongInfo b)
        {
            var r = b.Bpm / a.Bpm;
            var tempo = new[] { r, r * 2, r / 2 }.Min(x => Math.Abs(Math.Log(x)));
            var drop = Math.Max(0, a.Drive - b.Drive); // building up feels better than winding down
            return tempo * 8 + MusicAnalyzer.KeyDistance(a.Key, b.Key) * 0.4 + drop * 1.5;
        }
        List<SongInfo>? best = null;
        var bestCost = double.MaxValue;
        foreach (var perm in Permutations(songs))
        {
            var cost = 0.0;
            for (var i = 1; i < perm.Count; i++) cost += Step(perm[i - 1], perm[i]);
            if (perm[^1].FadesOut) cost -= 0.5; // a song that ends by itself makes the best ending
            if (cost < bestCost) { bestCost = cost; best = perm; }
        }
        return best!;
    }

    private static IEnumerable<List<SongInfo>> Permutations(List<SongInfo> items)
    {
        if (items.Count == 1) { yield return items.ToList(); yield break; }
        for (var i = 0; i < items.Count; i++)
        {
            var rest = items.Where((_, j) => j != i).ToList();
            foreach (var p in Permutations(rest)) { p.Insert(0, items[i]); yield return p; }
        }
    }

    /// <summary>Times (in the song) where a 4-bar section starts: the only places a song is entered or left.</summary>
    private static List<double> Sections(SongInfo s)
    {
        var list = new List<double>();
        for (var i = s.FirstDownbeat; i < s.Beats.Count; i += 16) list.Add(s.Beats[i]);
        return list;
    }

    private static double MeanEnergy(SongInfo s, float[] energy, double from, double to)
    {
        double sum = 0;
        var n = 0;
        for (var i = 0; i < s.Beats.Count; i++)
            if (s.Beats[i] >= from && s.Beats[i] < to) { sum += energy[i]; n++; }
        return n == 0 ? 0 : sum / n;
    }

    private static void LaySongs(WrappedPlan plan, List<SongInfo> songs, double target)
    {
        var ordered = Order(songs);
        // Use songs until there's enough music; with too little, the video is as long as the music.
        var use = new List<SongInfo>();
        double have = 0;
        foreach (var s in ordered)
        {
            if (have >= target) break;
            use.Add(s);
            have += s.Seconds - (use.Count > 1 ? 4 : 0);
        }
        var total = Math.Min(target, have);
        // Each song's share, in proportion to its length.
        var lengths = use.Select(s => s.Seconds).ToList();
        var slots = lengths.Select(l => total * l / lengths.Sum()).ToList();

        for (var i = 0; i < use.Count; i++)
        {
            var s = use[i];
            var energy = s.Energy();
            var first = i == 0;
            var last = i == use.Count - 1;
            var slot = slots[i] + (last ? 0 : 3); // the overlap with the next song comes on top
            var sections = Sections(s);
            double bestIn = 0, bestOut = s.Seconds, bestCost = double.MaxValue;
            var natural = false;
            // Where it can start: the very beginning for the first song (its intro opens the video), else a section.
            var starts = first ? new List<double> { 0 } : sections.Where(t => t < s.Seconds - 8).ToList();
            if (starts.Count == 0) starts.Add(0);
            foreach (var a in starts)
            {
                // Where it can end: a section, or (for the last song) the song's own ending.
                var ends = sections.Where(t => t > a + 6).Select(t => (t, false)).ToList();
                if (last) ends.Add((s.Seconds, true));
                foreach (var (b, isEnd) in ends)
                {
                    var len = b - a;
                    var cost = Math.Abs(len - slot) / Math.Max(slot, 1) * 4
                               - MeanEnergy(s, energy, a, b)
                               // Entering where it gets louder (a chorus, a drop) sounds like it was meant.
                               - (first ? 0 : 0.5 * Math.Max(0, MeanEnergy(s, energy, a, a + s.BarSeconds * 4) - MeanEnergy(s, energy, a - s.BarSeconds * 4, a)))
                               - (isEnd ? (s.FadesOut ? 0.8 : 0.5) : 0);
                    if (len > slot * 1.35 + 8) cost += 3; // far too long
                    if (cost < bestCost) { bestCost = cost; bestIn = a; bestOut = b; natural = isEnd; }
                }
            }
            plan.Songs.Add(new WrappedSongPart { Song = s, In = bestIn, Out = bestOut, EndsNaturally = natural });
        }

        // Crossfades: two bars when the tempos are close enough for the beats to stay together, else one.
        double at = 0;
        for (var i = 0; i < plan.Songs.Count; i++)
        {
            var p = plan.Songs[i];
            p.At = at;
            var meanDb = MeanDb(p.Song, p.In, p.Out);
            p.GainDb = Math.Clamp(-14 - meanDb, -15, 10); // every song about as loud as the others
            if (i + 1 < plan.Songs.Count)
            {
                var next = plan.Songs[i + 1];
                var close = Math.Abs(next.Song.Bpm / p.Song.Bpm - 1) < 0.04;
                p.CrossfadeOut = Math.Clamp((close ? 2 : 1) * p.Song.BarSeconds, 1.5, Math.Min(6, Math.Min(p.Length, next.Length) / 4));
                at += p.Length - p.CrossfadeOut;
            }
            else
            {
                p.FadeOut = p.EndsNaturally ? Math.Min(0.5, p.Length / 10) : Math.Min(4, 2 * p.Song.BarSeconds);
                at += p.Length;
            }
        }
        plan.Seconds = at;
    }

    private static double MeanDb(SongInfo s, double from, double to)
    {
        double power = 0;
        var n = 0;
        for (var i = 0; i < s.Beats.Count && i < s.BeatDb.Count; i++)
            if (s.Beats[i] >= from && s.Beats[i] < to) { power += Math.Pow(10, s.BeatDb[i] / 10); n++; }
        return n == 0 ? -20 : 10 * Math.Log10(power / n);
    }

    /// <summary>The beats of the soundtrack on the video's timeline, each song's own while it leads.</summary>
    private static List<Beat> Grid(WrappedPlan plan, List<double> mustCut)
    {
        var grid = new List<Beat>();
        for (var i = 0; i < plan.Songs.Count; i++)
        {
            var p = plan.Songs[i];
            var energy = p.Song.Energy();
            var from = i == 0 ? 0 : p.At + plan.Songs[i - 1].CrossfadeOut / 2;
            var to = i + 1 < plan.Songs.Count ? plan.Songs[i + 1].At + p.CrossfadeOut / 2 : plan.Seconds;
            for (var b = 0; b < p.Song.Beats.Count; b++)
            {
                var t = p.At + p.Song.Beats[b] - p.In;
                if (t < from || t >= to) continue;
                var down = b >= p.Song.FirstDownbeat && (b - p.Song.FirstDownbeat) % 4 == 0;
                grid.Add(new Beat(t, down, b < energy.Length ? energy[b] : 0.5f, b < p.Song.BeatDrive.Count ? p.Song.BeatDrive[b] : 1));
            }
            // The cut that sells the change of song: on the downbeat where the new one has fully taken over.
            if (i > 0) mustCut.Add(p.At + plan.Songs[i - 1].CrossfadeOut);
        }
        return grid;
    }

    /// <summary>The drops of one song on its own timeline (for Auto montage, where the song simply plays from the start).</summary>
    public static List<double> DropsOf(SongInfo song)
    {
        var energy = song.Energy();
        var beats = song.Beats.Select((t, i) => new Beat(t, i >= song.FirstDownbeat && (i - song.FirstDownbeat) % 4 == 0,
            i < energy.Length ? energy[i] : 0.5f, i < song.BeatDrive.Count ? song.BeatDrive[i] : 1)).ToList();
        return Drops(beats);
    }

    /// <summary>
    /// The drops: bars where the music gets much louder than the four bars before (a chorus or a drop coming in),
    /// at least eight bars apart.
    /// </summary>
    internal static List<double> Drops(IReadOnlyList<Beat> beats)
    {
        var drops = new List<double>();
        var last = -1000;
        for (var i = 0; i < beats.Count; i++)
        {
            if (!beats[i].Down || i < 16) continue;
            var before = Enumerable.Range(i - 16, 16).Average(k => beats[k].Energy);
            var after = Enumerable.Range(i, Math.Min(8, beats.Count - i)).Average(k => beats[k].Energy);
            if (after - before < 0.25 || after < 0.55) continue;
            if (i - last < 32) continue;
            drops.Add(beats[i].Time);
            last = i;
        }
        return drops;
    }

    // ---------- the picture ----------

    private static void CutPicture(WrappedPlan plan, List<Beat> beats, List<double> mustCut, IReadOnlyList<WrappedAct> acts, IReadOnlyList<double> cardSeconds)
    {
        var downs = beats.Where(b => b.Down).Select(b => b.Time).ToList();
        if (downs.Count < 4) downs = beats.Select(b => b.Time).ToList();
        var gaps = downs.Zip(downs.Skip(1), (a, b) => b - a).Where(d => d > 0.3).OrderBy(d => d).ToList();
        var bar = gaps.Count > 0 ? gaps[gaps.Count / 2] : 2;
        double Snap(double t, double atLeast) => downs.FirstOrDefault(d => d >= Math.Max(t, atLeast) - 0.05, Math.Max(t, atLeast));
        float EnergyAt(double from, double to)
        {
            var inside = beats.Where(b => b.Time >= from && b.Time < to).ToList();
            return inside.Count == 0 ? 0.5f : inside.Average(b => b.Energy);
        }
        // A quick shot is a bar long, or two when the bars are short, or half when they're long.
        var quick = bar < 1.4 ? bar * 2 : bar > 2.8 ? bar / 2 : bar;
        double Bars(double seconds) => Math.Max(1, Math.Round(seconds / bar)) * bar;

        var total = plan.Seconds;
        var cards = cardSeconds.Count;
        var last = acts.Count - 1;
        var outroMin = acts.Count > 0 && acts[last].Card >= 0 ? cardSeconds[acts[last].Card] : 0;
        var outroAt = downs.LastOrDefault(t => t <= total - outroMin + 0.05, total - outroMin);

        // The fixed parts first (cards, month flashes, story moments), then the quick-cut parts share what's left.
        double cardTime = acts.Where(a => a.Card >= 0).Sum(a => Bars(cardSeconds[a.Card]));
        double flashTime = acts.Where(a => a.Style == WrappedActStyle.Flash).Sum(a => a.Pool.Count * quick);
        double storyTime = acts.Where(a => a.Style == WrappedActStyle.Story).Sum(a => a.Pool.Sum(m => Bars(StoryWant(a, m))));
        var flex = total - cardTime - flashTime - storyTime;
        var squeeze = flex >= 0 ? 1 : Math.Clamp((total - cardTime) / Math.Max(1, flashTime + storyTime), 0.25, 1);
        flex = Math.Max(0, flex);
        var share = new double[acts.Count];
        var open = Enumerable.Range(0, acts.Count).Where(i => acts[i].Style is WrappedActStyle.Burst or WrappedActStyle.Mix && acts[i].Pool.Count > 0).ToList();
        // Share by weight, but no part longer than its clips can fill; what a part can't use goes to the others.
        for (var round = 0; round < 4 && open.Count > 0 && flex > 0.5; round++)
        {
            var weights = open.Sum(i => acts[i].Weight);
            var spare = 0.0;
            foreach (var i in open.ToList())
            {
                var cap = acts[i].Pool.Sum(m => Math.Min(m.DurationMs / 1000.0, 10)) * 0.8 - share[i];
                var give = flex * acts[i].Weight / weights;
                if (give >= cap) { share[i] += Math.Max(0, cap); spare += give - Math.Max(0, cap); open.Remove(i); }
                else share[i] += give;
            }
            flex = spare;
        }

        var used = new Dictionary<WrappedMoment, int>();
        WrappedMoment? lastPick = null;
        var shots = plan.Shots;
        double cursor = 0;
        var picked = 0;
        for (var k = 0; k < acts.Count; k++)
        {
            var act = acts[k];
            var isLast = k == last;
            var limit = isLast ? total : outroAt;
            if (cursor >= limit - 0.5 && !isLast) continue;
            if (act.Card >= 0)
            {
                var end = isLast ? total : Math.Min(Snap(cursor + cardSeconds[act.Card], cursor + 1), limit);
                if (end - cursor > 0.4)
                    shots.Add(new WrappedShot { Kind = WrappedShotKind.Card, Card = act.Card, At = cursor, Length = end - cursor, Energy = EnergyAt(cursor, end) });
                cursor = end;
                if (isLast) break;
            }
            switch (act.Style)
            {
                case WrappedActStyle.Flash:
                {
                    var n = (int)Math.Round(act.Pool.Count * squeeze);
                    for (var i = 0; i < act.Pool.Count && n > 0; i++)
                    {
                        if (squeeze < 1 && i % Math.Max(1, act.Pool.Count / n) != 0) continue;
                        var end = Math.Min(Snap(cursor + quick, cursor + 0.6), limit);
                        if (end - cursor < 0.5) break;
                        var shot = Clip(cursor, end, act.Pool[i], story: false);
                        shot.Caption = act.Captions?.ElementAtOrDefault(i);
                        shot.Move = WrappedMove.PushIn;
                        shots.Add(shot);
                        cursor = end;
                    }
                    break;
                }
                case WrappedActStyle.Story:
                {
                    for (var i = 0; i < act.Pool.Count; i++)
                    {
                        var m = act.Pool[i];
                        var want = Math.Min(Bars(StoryWant(act, m) * squeeze), Math.Max(quick, m.DurationMs / 1000.0 - 0.2));
                        var end = Math.Min(BestEnd(cursor, want, m, limit), limit);
                        if (end - cursor < 1) break;
                        var shot = Clip(cursor, end, m, story: true);
                        shot.Caption = act.Captions?.ElementAtOrDefault(i);
                        shots.Add(shot);
                        cursor = end;
                    }
                    break;
                }
                case WrappedActStyle.Burst or WrappedActStyle.Mix:
                {
                    var stop = Math.Min(limit, cursor + share[k]);
                    var n = 0;
                    while (stop - cursor > quick * 0.6 && act.Pool.Count > 0)
                    {
                        var story = act.Style == WrappedActStyle.Mix && n % 4 == 3;
                        var len = story ? Bars(9) : quick;
                        var end = Snap(cursor + len, cursor + 0.6);
                        var cut = story ? -1 : mustCut.FirstOrDefault(c => c > cursor + 0.8 && c < end - 0.3, -1);
                        if (cut > 0) end = cut;
                        if (stop - end < quick * 0.6) end = Math.Max(end, stop);
                        end = Math.Min(end, limit);
                        if (end - cursor < 0.5) break;
                        var m = Pick(act.Pool, (long)((end - cursor) * 1000), story);
                        if (story && m.Guide is not null)
                        {
                            // A longer moment ends on the bar line where what's being said (or laughed at) is over.
                            var want = m.HasLaugh ? Math.Clamp(StoryWant(act, m), 6, 14) : end - cursor;
                            var better = BestEnd(cursor, want, m, Math.Min(limit, Math.Max(stop, end) + bar));
                            if (better - cursor >= 1.5 && better <= limit) end = better;
                        }
                        shots.Add(Clip(cursor, end, m, story));
                        cursor = end;
                        n++;
                    }
                    break;
                }
            }
        }
        plan.Moments = used.Count;

        // The bar line to end a longer moment on: about as long as it wants, where the cut hurts least
        // (not mid-sentence, not in the middle of a laugh). Without a guide, the next bar line.
        double BestEnd(double at, double want, WrappedMoment m, double max)
        {
            var plainEnd = Snap(at + want, at + 1.5);
            if (m.Guide is null) return plainEnd;
            var room = m.DurationMs / 1000.0 - 0.2;
            var choices = downs.Where(d => d - at >= 1.5 && d <= max + 0.05 && Math.Abs(d - at - want) <= bar * 1.1 && d - at <= Math.Max(room, quick)).ToList();
            if (choices.Count == 0) return plainEnd;
            return choices.MinBy(d => Aim(m, at, d - at, story: true).Cost + 0.15 * Math.Abs(d - at - want) / bar);
        }

        WrappedMoment Pick(IReadOnlyList<WrappedMoment> pool, long needMs, bool story)
        {
            var fresh = pool.Where(m => !used.ContainsKey(m)).ToList();
            if (story)
            {
                // A longer moment is best when people talk or laugh in it.
                var talk = fresh.Where(m => m.DurationMs >= needMs - 500)
                    .OrderByDescending(m => (m.HasLaugh ? 3 : m.Voice.Count > 0 ? 2 : 0) + m.Score + (m.Guide is { Voices.Count: > 0 } ? 0.5 : 0)).FirstOrDefault();
                if (talk is not null) return talk;
            }
            else
            {
                // A laugh needs its setup, which a quick cut doesn't have time for: those wait for a longer slot.
                if (CutGuide.Enabled)
                {
                    var some = fresh.Where(m => !m.HasLaugh && m.DurationMs >= needMs - 100).Take(6).ToList();
                    if (some.Count > 0) return Varied(some);
                }
                var plain = fresh.FirstOrDefault(m => !m.HasLaugh && m.DurationMs >= needMs - 100);
                if (plain is not null) return plain;
            }
            return fresh.FirstOrDefault(m => m.DurationMs >= needMs - 100)
                   ?? fresh.OrderByDescending(m => m.DurationMs).FirstOrDefault()
                   ?? pool.OrderBy(m => used.GetValueOrDefault(m)).ThenByDescending(m => m.Score).First();
        }

        // Of the best few, the one that looks least like the shot before (the visual model), and another game if it can.
        WrappedMoment Varied(List<WrappedMoment> some)
        {
            if (lastPick is null || some.Count == 1) return some[0];
            var otherGame = some.Any(m => !string.Equals(m.Game, lastPick.Game, StringComparison.OrdinalIgnoreCase));
            return some.Select((m, i) =>
            {
                var value = -i * 0.25;
                if (m.Look is { } look && lastPick.Look is { } before) value -= Math.Max(0, VisualInfo.Dot(look, before) - 0.8) * 7.5;
                if (otherGame && m.Game is not null && string.Equals(m.Game, lastPick.Game, StringComparison.OrdinalIgnoreCase)) value -= 0.2;
                return (m, value);
            }).MaxBy(x => x.value).m;
        }

        // Where in the clip a shot starts, and how much its cuts hurt (0: both in a pause).
        (long Start, double Cost) Aim(WrappedMoment m, double at, double length, bool story)
        {
            var needMs = (long)(length * 1000);
            var round = used.GetValueOrDefault(m);
            var g = m.Guide;
            long start;
            if (story && m.HasLaugh)
            {
                // The joke and the laugh: from what set it up, and the laugh plays out before the cut.
                start = Math.Max(m.SetupMs, m.LaughToMs + 1200 - needMs);
                if (g is not null && round == 0)
                {
                    var lo = m.LaughToMs + 600 - needMs;
                    return lo <= m.SetupMs ? g.Place(needMs, start, lo, m.SetupMs)
                        : g.Place(needMs, start, start - needMs / 8, start + needMs / 8);
                }
            }
            else if (story)
            {
                // Lead in a little before the peak so the moment has room to land.
                start = m.FocusMs - (long)(needMs * 0.35);
                if (g is not null && round == 0)
                    return g.Place(needMs, start, m.FocusMs - (long)(needMs * 0.8), m.FocusMs - (long)(needMs * 0.12));
            }
            else
            {
                // The peak lands on the strongest beat in the first part of the shot.
                var hits = beats.Where(b => b.Time >= at && b.Time < at + length * 0.6).ToList();
                var hit = hits.OrderByDescending(b => b.Drive).Select(b => b.Time - at).FirstOrDefault(length * 0.4);
                start = m.FocusMs - (long)(hit * 1000);
                if (g is not null && round == 0 && hits.Count > 0)
                {
                    // Any beat will do for the peak, so take the one that keeps both cuts out of a sentence or a laugh.
                    var drive = Math.Max(0.001, hits.Max(b => b.Drive));
                    var best = hits.Select(b =>
                    {
                        var s = Math.Clamp(m.FocusMs - (long)((b.Time - at) * 1000), 0, Math.Max(0, m.DurationMs - needMs));
                        var cost = g.CutCost(s, needMs);
                        return (s, cost, score: cost - b.Drive / drive * 0.3);
                    }).MinBy(x => x.score);
                    return (best.s, best.cost);
                }
            }
            if (round > 0) start += round * (long)(needMs * 1.2) * (round % 2 == 1 ? -1 : 1); // a reused clip shows another part
            start = Math.Clamp(start, 0, Math.Max(0, m.DurationMs - needMs));
            if (g is not null) return g.Place(needMs, start, start - needMs / 4, start + needMs / 4);
            return (start, 0);
        }

        WrappedShot Clip(double at, double end, WrappedMoment m, bool story)
        {
            var shot = new WrappedShot { Kind = WrappedShotKind.Clip, At = at, Length = end - at, Energy = EnergyAt(at, end), Story = story };
            var needMs = (long)(shot.Length * 1000);
            var start = Aim(m, at, shot.Length, story).Start;
            used[m] = used.GetValueOrDefault(m) + 1;
            lastPick = m;
            picked++;
            start = Math.Clamp(start, 0, Math.Max(0, m.DurationMs - needMs));
            if (!story && m.HasLaugh && start < m.LaughToMs && start + needMs > m.LaughFromMs)
            {
                // A quick cut never chops a laugh in half: show what came before it, or what came after.
                var before = m.LaughFromMs - needMs;
                start = before >= 0 ? before : Math.Min(m.LaughToMs, Math.Max(0, m.DurationMs - needMs));
            }
            shot.Path = m.Path;
            shot.StartMs = start;
            var talk = m.Voice.Sum(v => Math.Max(0, Math.Min(v.To, start + needMs) - Math.Max(v.From, start)));
            shot.Voice = talk >= Math.Min(800, needMs * 0.15);
            // A quick shot that still catches half a sentence keeps the music up over it: chopped words aren't heard.
            if (!story && m.Guide is { } g && g.CutsTalk(start, needMs)) shot.Voice = false;
            shot.Move = story ? WrappedMove.PushIn : WrappedMove.PullOut;
            return shot;
        }
    }

    /// <summary>How long a longer moment wants to be: a laugh with its setup gets what it needs (6 to 18 s).</summary>
    private static double StoryWant(WrappedAct act, WrappedMoment m) =>
        m.HasLaugh ? Math.Clamp((m.LaughToMs + 1200 - m.SetupMs) / 1000.0, 6, Math.Max(18, act.StorySeconds)) : act.StorySeconds;

    /// <summary>
    /// How each shot comes in: cards slide and zoom, longer moments dissolve, the quick cuts stay straight cuts
    /// (with a white flash at the top of a phrase), and a change of song gets a short blur.
    /// </summary>
    private static void Transitions(WrappedPlan plan, List<double> mustCut, List<double> drops)
    {
        var quickCount = 0;
        for (var i = 1; i < plan.Shots.Count; i++)
        {
            var prev = plan.Shots[i - 1];
            var cur = plan.Shots[i];
            var organic = PulseFx.Enabled && plan.Songs.Count > 0;
            (string? name, double d) = organic
                // Savefile's own: liquid and ink instead of slides and wipes, a burst on the drop, a light leak now and then.
                ? (prev.Kind, cur.Kind) switch
                {
                    (WrappedShotKind.Card, WrappedShotKind.Card) => ("sf:blob", 0.6),
                    (_, WrappedShotKind.Card) => ("sf:wave", 0.6),
                    (WrappedShotKind.Card, _) => ("sf:blob", 0.55),
                    _ when drops.Any(dr => Math.Abs(dr - cur.At) < 0.1) => ("sf:burst", 0.45),
                    _ when cur.Story || prev.Story => ("sf:ink", 0.5),
                    _ when cur.Caption is not null => ("sf:ink", 0.35),
                    _ when mustCut.Any(c => Math.Abs(c - cur.At) < 0.1) => ("sf:wave", 0.45),
                    _ => (++quickCount % 6 == 0 ? "sf:leak" : null, 0.5),
                }
                : (prev.Kind, cur.Kind) switch
            {
                (WrappedShotKind.Card, WrappedShotKind.Card) => ("slideleft", 0.45),
                (_, WrappedShotKind.Card) => ("smoothup", 0.5),
                (WrappedShotKind.Card, _) => ("zoomin", 0.45),
                _ when drops.Any(dr => Math.Abs(dr - cur.At) < 0.1) => ("fadewhite", 0.22),
                _ when cur.Story || prev.Story => ("fade", 0.4),
                _ when cur.Caption is not null => ("smoothleft", 0.3),
                _ when mustCut.Any(c => Math.Abs(c - cur.At) < 0.1) => ("hblur", 0.35),
                _ => (++quickCount % 8 == 0 ? "fadewhite" : null, 0.14),
            };
            if (name is null) continue;
            cur.Transition = name;
            cur.TransitionSeconds = Math.Min(d, Math.Min(prev.Length, cur.Length) * 0.4);
        }
    }
}
