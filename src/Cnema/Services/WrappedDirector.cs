namespace Cnema.Services;

/// <summary>A moment that could go in the year's video: where it is, its peak, how good it is, where people talk or laugh.</summary>
public sealed record WrappedMoment(string Path, long DurationMs, long FocusMs, double Score, IReadOnlyList<(long From, long To)> Voice);

public enum WrappedShotKind { Card, Clip }

/// <summary>One shot of the video: a stat card or a stretch of a clip, placed on the timeline in seconds.</summary>
public sealed class WrappedShot
{
    public WrappedShotKind Kind { get; init; }
    public int Card { get; init; } = -1;
    public string? Path { get; set; }
    public long StartMs { get; set; }
    public double At { get; init; }
    public double Length { get; init; }
    /// <summary>Someone talks or laughs here: the clip's sound comes up and the music steps back.</summary>
    public bool Voice { get; set; }
    /// <summary>How loud the music is under this shot, 0 to 1.</summary>
    public float Energy { get; init; }
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
    public List<WrappedShot> Shots { get; } = new();
    public List<WrappedSongPart> Songs { get; } = new();
    public double Seconds { get; set; }
    public int Moments { get; set; }
    public double Bpm { get; set; }
}

/// <summary>
/// The "director" of the year's video: plain rules, no AI. It lays the songs end to end (ordered so tempo,
/// key and energy flow, each one entering on a phrase boundary with an equal-power crossfade whose downbeats
/// line up), then cuts the picture on bar lines: stat cards first, then the best moments, the strongest ones
/// on the loudest stretches of music, each placed so its peak lands on a beat, and a cut on the downbeat where
/// one song has taken over from the other.
/// </summary>
public static class WrappedDirector
{
    public const double IntroSeconds = 4.0, CardSeconds = 3.2, OutroSeconds = 4.5;
    private const double SecondsPerMoment = 3.4;

    /// <summary>How long the video wants to be, in seconds: the cards plus a few seconds per moment.</summary>
    public static double TargetSeconds(int moments, int cards)
    {
        var cardTime = IntroSeconds + OutroSeconds + Math.Max(0, cards - 2) * CardSeconds;
        return cardTime + Math.Clamp(moments * SecondsPerMoment, 20, 150);
    }

    /// <summary>How much music to bring, rounded up to the next half minute.</summary>
    public static double RecommendedMusicSeconds(int moments, int cards) => Math.Ceiling(TargetSeconds(moments, cards) / 30) * 30;

    private sealed record Beat(double Time, bool Down, float Energy, float Drive);

    public static WrappedPlan Plan(IReadOnlyList<WrappedMoment> moments, IReadOnlyList<double> cardSeconds, IReadOnlyList<SongInfo> songs)
    {
        var plan = new WrappedPlan();
        var usable = songs.Where(s => s.Usable).ToList();
        var target = TargetSeconds(moments.Count, cardSeconds.Count);
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
        CutPicture(plan, beats, mustCut, moments, cardSeconds);
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

    // ---------- the picture ----------

    private static void CutPicture(WrappedPlan plan, List<Beat> beats, List<double> mustCut, IReadOnlyList<WrappedMoment> moments, IReadOnlyList<double> cardSeconds)
    {
        var downs = beats.Where(b => b.Down).Select(b => b.Time).ToList();
        if (downs.Count < 4) downs = beats.Select(b => b.Time).ToList();
        double NextDown(double atLeast) => downs.FirstOrDefault(t => t >= atLeast - 0.05, atLeast);
        float EnergyAt(double from, double to)
        {
            var inside = beats.Where(b => b.Time >= from && b.Time < to).ToList();
            return inside.Count == 0 ? 0.5f : inside.Average(b => b.Energy);
        }

        var total = plan.Seconds;
        var cards = cardSeconds.Count;
        // The outro card closes the video: from the last bar line that leaves it enough time.
        var outroMin = cards > 0 ? cardSeconds[^1] : 0;
        var outroAt = cards > 0 ? downs.LastOrDefault(t => t <= total - outroMin + 0.05, total - outroMin) : total;
        double cursor = 0;
        for (var c = 0; c < cards - 1; c++)
        {
            var end = Math.Min(NextDown(cursor + cardSeconds[c]), outroAt);
            plan.Shots.Add(new WrappedShot { Kind = WrappedShotKind.Card, Card = c, At = cursor, Length = end - cursor, Energy = EnergyAt(cursor, end) });
            cursor = end;
        }

        // The reel: one bar on loud stretches (when bars are long enough), two or three when it's calmer.
        var reel = new List<(double At, double End)>();
        while (outroAt - cursor > 1.2 && moments.Count > 0)
        {
            var bar = downs.Count > 1 ? Math.Max(0.3, downs.Zip(downs.Skip(1), (a, b) => b - a).Where(d => d > 0.3).DefaultIfEmpty(2).Average()) : 2;
            var e = EnergyAt(cursor, cursor + bar);
            var want = e >= 0.66 ? 2.2 : e >= 0.33 ? 3.2 : 4.4;
            var end = downs.Where(t => t > cursor + 1.4 && t < cursor + 6.5).OrderBy(t => Math.Abs(t - cursor - want)).Cast<double?>().FirstOrDefault()
                      ?? Math.Min(outroAt, cursor + want);
            var cut = mustCut.FirstOrDefault(m => m > cursor + 1.2 && m < end - 0.3, -1);
            if (cut > 0) end = cut;
            if (outroAt - end < 1.4) end = outroAt;
            reel.Add((cursor, end));
            cursor = end;
        }
        // More shots than moments: join the shortest neighbours (up to 7 s), and reuse moments only if still short.
        while (reel.Count > Math.Max(1, moments.Count))
        {
            var bestI = -1;
            var bestLen = double.MaxValue;
            for (var i = 0; i + 1 < reel.Count; i++)
            {
                var len = reel[i + 1].End - reel[i].At;
                if (len <= 7 && len < bestLen) { bestLen = len; bestI = i; }
            }
            if (bestI < 0) break;
            reel[bestI] = (reel[bestI].At, reel[bestI + 1].End);
            reel.RemoveAt(bestI + 1);
        }

        // The best moments go to the loudest music; each clip once while there are enough.
        var shots = reel.Select(r => new WrappedShot { Kind = WrappedShotKind.Clip, At = r.At, Length = r.End - r.At, Energy = EnergyAt(r.At, r.End) }).ToList();
        var pool = moments.OrderByDescending(m => m.Score).ToList();
        var used = new Dictionary<WrappedMoment, int>();
        foreach (var shot in shots.OrderByDescending(s => s.Energy).ThenBy(s => s.At))
        {
            var needMs = (long)(shot.Length * 1000);
            var fresh = pool.Where(m => !used.ContainsKey(m)).ToList();
            var choice = fresh.FirstOrDefault(m => m.DurationMs >= needMs - 100)
                         ?? fresh.OrderByDescending(m => m.DurationMs).FirstOrDefault()
                         ?? pool.OrderBy(m => used.GetValueOrDefault(m)).ThenByDescending(m => m.DurationMs).First();
            var round = used.GetValueOrDefault(choice);
            used[choice] = round + 1;
            // The peak lands on the strongest beat in the first part of the shot.
            var hit = beats.Where(b => b.Time >= shot.At && b.Time < shot.At + shot.Length * 0.6).OrderByDescending(b => b.Drive).Select(b => b.Time - shot.At).FirstOrDefault(shot.Length * 0.4);
            var focus = choice.FocusMs + round * (long)(needMs * 1.2) * (round % 2 == 1 ? -1 : 1); // a reused clip shows another part
            var start = Math.Clamp(focus - (long)(hit * 1000), 0, Math.Max(0, choice.DurationMs - needMs));
            shot.Path = choice.Path;
            shot.StartMs = start;
            var talk = choice.Voice.Sum(v => Math.Max(0, Math.Min(v.To, start + needMs) - Math.Max(v.From, start)));
            shot.Voice = talk >= Math.Min(800, needMs * 0.15);
        }
        // No two shots in a row from the same clip when it can be avoided.
        for (var i = 1; i < shots.Count; i++)
        {
            if (shots[i].Path != shots[i - 1].Path) continue;
            var j = shots.FindIndex(i + 1, s => s.Path != shots[i].Path && (i + 1 >= shots.Count || s.Path != shots[i + 1].Path));
            if (j < 0) continue;
            (shots[i].Path, shots[j].Path) = (shots[j].Path, shots[i].Path);
            (shots[i].StartMs, shots[j].StartMs) = (shots[j].StartMs, shots[i].StartMs);
            (shots[i].Voice, shots[j].Voice) = (shots[j].Voice, shots[i].Voice);
        }
        plan.Shots.AddRange(shots);
        plan.Moments = used.Count;
        if (cards > 0)
            plan.Shots.Add(new WrappedShot { Kind = WrappedShotKind.Card, Card = cards - 1, At = cursor, Length = total - cursor, Energy = EnergyAt(cursor, total) });
    }
}
