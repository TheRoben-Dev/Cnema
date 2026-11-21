using Cnema.Models;

namespace Cnema.Services;

/// <summary>
/// What every smart pass found in one clip, turned into advice for whoever cuts it (Auto montage, Wrapped):
/// where it's interesting, and where a cut would hurt. Speech lines are joined into sentences so a cut never
/// lands mid-sentence; a laugh keeps the line that set it up; a cheer or a banner on screen (VICTORY, ACE) plays
/// out; a moment you marked with the hotkey counts most. Voices only count when they're recognised for sure.
/// Plain arithmetic over what the library already knows: nothing is decoded or listened to again, and a pass
/// that's off or hasn't reached the clip simply adds nothing.
/// </summary>
public sealed class CutGuide
{
    /// <summary>The Settings toggle (Features.SmartCuts): off, montages and Wrapped cut as they did before.</summary>
    public static bool Enabled { get; set; } = true;

    /// <summary>The friend a voice belongs to when Savefile is sure (null otherwise). Set by the app.</summary>
    public static Func<VoiceSpeaker, string?>? NameOf { get; set; }

    /// <summary>A run of laughter and where what set it off starts (ms).</summary>
    public sealed record LaughRun(long From, long To, long Setup, double Strength);

    private const int Bin = 250, Edge = 120;
    private readonly List<(long From, long To)> _talk = new();
    private readonly List<(long From, long To, double Weight)> _hold = new();
    private readonly double[] _sum; // running total of interest per bin, for quick sums over a stretch

    public long DurationMs { get; }
    /// <summary>The most interesting moment (ms), or -1 when nothing stands out.</summary>
    public long Peak { get; } = -1;
    /// <summary>Whether any pass told it something (else the caller does what it did before).</summary>
    public bool HasSignals { get; }
    public IReadOnlyList<LaughRun> Laughs { get; }
    /// <summary>Friends heard talking, recognised for sure.</summary>
    public IReadOnlyList<string> Voices { get; }
    /// <summary>Sentences (ms), joined from the transcript or, without one, from where voices were heard.</summary>
    public IReadOnlyList<(long From, long To)> Talk => _talk;

    public CutGuide(VideoEntry e)
    {
        DurationMs = Math.Max(1, e.DurationMs);
        var bins = new double[DurationMs / Bin + 1];
        void Bump(long at, double amount, long spread)
        {
            for (var t = Math.Max(0, at - spread); t <= Math.Min(DurationMs, at + spread); t += Bin)
                bins[t / Bin] += amount * (1 - 0.5 * Math.Abs(t - at) / Math.Max(1, spread));
        }
        void Fill(long from, long to, double amount)
        {
            for (var t = Math.Max(0, from); t < Math.Min(DurationMs, to); t += Bin) bins[t / Bin] += amount;
        }

        // Sentences: lines that run straight on, or on without a full stop, are one thing someone says.
        var lines = e.Speech is { Lines.Count: > 0 } sp
            ? sp.Lines.Where(l => l.End > l.Start).OrderBy(l => l.Start).Select(l => (l.Start, l.End, Text: l.Text.Trim())).ToList()
            : e.Voices is { Speakers.Count: > 0 } vo
                ? vo.Speakers.SelectMany(s => s.Lines).Where(l => l.Length >= 2 && l[1] > l[0]).OrderBy(l => l[0]).Select(l => (l[0], l[1], Text: "")).ToList()
                : new List<(long, long, string)>();
        foreach (var (start, end, text) in lines)
        {
            if (_talk.Count > 0)
            {
                var (from, to) = _talk[^1];
                var gap = start - to;
                if (gap < 300 || (gap < 900 && !EndsSentence(_lastText)))
                {
                    _talk[^1] = (from, Math.Max(to, end));
                    _lastText = text;
                    continue;
                }
            }
            _talk.Add((start, end));
            _lastText = text;
        }
        foreach (var (from, to) in _talk) Fill(from, to, 0.15);

        // Friends recognised for sure: their lines matter a little more. A guess counts for nothing.
        var voices = new List<string>();
        if (NameOf is { } nameOf && e.Voices is { Speakers.Count: > 0 } heard)
            foreach (var s in heard.Speakers)
            {
                if (s.Ms < 2500 || nameOf(s) is not { } name) continue;
                if (!voices.Contains(name, StringComparer.OrdinalIgnoreCase)) voices.Add(name);
                foreach (var l in s.Lines.Where(l => l.Length >= 2)) Fill(l[0], l[1], 0.25);
            }
        Voices = voices;

        // Laughter: every run of it is held whole, and brings what set it off.
        var laughs = new List<LaughRun>();
        if (e.Sound is { } so)
        {
            foreach (var m in so.Laugh) Bump(m.At, 0.6 + m.Score, 500);
            foreach (var m in so.Hype) Bump(m.At, 0.5 + m.Score * 0.8, 500);
            foreach (var (from, to, n) in Runs(so.Laugh.Select(m => m.At), 1500))
            {
                if (n < 2) continue;
                var f = Math.Max(0, from - 300);
                var t = Math.Min(DurationMs, to + 600);
                laughs.Add(new LaughRun(f, t, SetupBefore(f), n));
                _hold.Add((f, t, 2));
            }
            foreach (var (from, to, _) in Runs(so.Hype.Select(m => m.At), 1000))
                _hold.Add((Math.Max(0, from - 300), Math.Min(DurationMs, to + 600), 1));
        }
        Laughs = laughs;

        // The best moments pass, a banner on screen (and the play before it), moments marked with the hotkey.
        if (e.Smart is { BestMs: >= 0 } smart) Bump(smart.BestMs, 1.5, 1500);
        if (ScreenWords.Event(e.Screen) is { } banner)
        {
            Fill(banner.At - 2500, banner.At + 1000, banner.Rank / 4.0);
            _hold.Add((Math.Max(0, banner.At - 300), Math.Min(DurationMs, banner.At + 1500), 1.5));
        }
        foreach (var mark in e.Bookmarks) Bump(mark, 2.5, 2000);

        _sum = new double[bins.Length + 1];
        for (var i = 0; i < bins.Length; i++) _sum[i + 1] = _sum[i] + bins[i];
        var top = 0;
        for (var i = 1; i < bins.Length; i++) if (bins[i] > bins[top]) top = i;
        var peaky = bins.Length > 0 && bins[top] > 0.3;
        Peak = peaky ? Math.Min(DurationMs, top * (long)Bin + Bin / 2) : -1;
        HasSignals = peaky || _talk.Count > 0 || _hold.Count > 0;
    }

    private string _lastText = "";

    /// <summary>The guide for a clip, or null when smart cuts are off.</summary>
    public static CutGuide? For(VideoEntry e) => Enabled ? new CutGuide(e) : null;

    private static bool EndsSentence(string text) => text.Length > 0 && text[^1] is '.' or '?' or '!' or '…' or '"' or '”';

    /// <summary>Runs of moments no more than <paramref name="gap"/> apart: first, last and how many.</summary>
    private static IEnumerable<(long From, long To, int Count)> Runs(IEnumerable<long> times, long gap)
    {
        var list = times.OrderBy(t => t).ToList();
        for (var i = 0; i < list.Count;)
        {
            var j = i;
            while (j + 1 < list.Count && list[j + 1] - list[j] <= gap) j++;
            yield return (list[i], list[j], j - i + 1);
            i = j + 1;
        }
    }

    /// <summary>
    /// Where what led up to a laugh starts: the start of the sentences said just before it while they run on
    /// from each other (4 to 12 seconds back, never mid-sentence), or 6 seconds back when nothing was heard.
    /// </summary>
    public long SetupBefore(long laughFrom)
    {
        var setup = laughFrom - 6000;
        long? start = null;
        var next = laughFrom;
        foreach (var (from, to) in _talk.Where(s => s.From < laughFrom + 500 && s.To > laughFrom - 10000).OrderByDescending(s => s.From))
        {
            if (next - to > 2000 || laughFrom - from > 10000) break;
            start = from;
            next = from;
        }
        if (start is { } st) setup = Math.Min(st - 300, laughFrom - 4000);
        if (_talk.FirstOrDefault(s => s.From < setup && s.To > setup && laughFrom - s.From <= 12000) is { To: > 0 } mid) setup = mid.From - 300;
        return Math.Clamp(setup, Math.Max(0, laughFrom - 12000), laughFrom);
    }

    /// <summary>How much a cut at <paramref name="t"/> hurts: 0 in a pause, 3 mid-sentence, more inside a laugh or a cheer.</summary>
    public double Cost(long t)
    {
        if (t <= Edge || t >= DurationMs - Edge) return 0; // the clip's own start and end are where it was cut anyway
        var cost = 0.0;
        foreach (var (from, to) in _talk)
        {
            if (from > t) break;
            if (t > from + Edge && t < to - Edge) { cost += 3; break; }
        }
        foreach (var (from, to, w) in _hold)
            if (t > from + Edge && t < to - Edge) cost += w;
        return cost;
    }

    public double CutCost(long start, long length) => Cost(start) + Cost(start + length);

    /// <summary>Whether a stretch starts or ends in the middle of a sentence.</summary>
    public bool CutsTalk(long start, long length)
    {
        bool Inside(long t) => t > Edge && t < DurationMs - Edge && _talk.Any(s => t > s.From + Edge && t < s.To - Edge);
        return Inside(start) || Inside(start + length);
    }

    /// <summary>How interesting a stretch is (the sum over it).</summary>
    public double Interest(long from, long to)
    {
        double At(long t)
        {
            var x = Math.Clamp(t, 0, DurationMs) / (double)Bin;
            var i = Math.Min((int)x, _sum.Length - 2);
            return _sum[i] + (_sum[i + 1] - _sum[i]) * (x - i);
        }
        return to <= from ? 0 : At(to) - At(from);
    }

    /// <summary>
    /// The best start for a stretch of <paramref name="length"/> between <paramref name="lo"/> and <paramref name="hi"/>:
    /// as close to <paramref name="want"/> as it can be without cutting through a sentence, a laugh or a cheer.
    /// </summary>
    public (long Start, double Cost) Place(long length, long want, long lo, long hi)
    {
        var max = Math.Max(0, DurationMs - length);
        lo = Math.Clamp(lo, 0, max);
        hi = Math.Clamp(hi, lo, max);
        want = Math.Clamp(want, lo, hi);
        var best = (Start: want, Cost: CutCost(want, length), Score: double.MaxValue);
        best.Score = best.Cost;
        for (var s = lo; s <= hi; s += 100)
        {
            var cost = CutCost(s, length);
            var score = cost + Math.Abs(s - want) / (double)Math.Max(1000, length) * 0.8;
            if (score < best.Score) best = (s, cost, score);
        }
        return (best.Start, best.Cost);
    }

    /// <summary>
    /// The stretch of a clip to use when about <paramref name="length"/> ms of it is wanted: the most interesting
    /// part, grown or shifted (up to <paramref name="stretch"/> longer) so it starts and ends in a pause, and a laugh
    /// in it comes with its setup. <paramref name="fallbackPeak"/> is used when the clip told the guide nothing.
    /// </summary>
    public (long Start, long End) Window(long length, double stretch = 0.5, long fallbackPeak = -1)
    {
        if (length >= DurationMs) return (0, DurationMs);
        var max = (long)Math.Min(DurationMs, length * (1 + stretch));
        var min = (long)(length * 0.8);
        // Where to aim: around the peak with a little more after it than before (the reaction is part of it),
        // or the most interesting stretch when the peak alone doesn't say.
        var peak = Peak >= 0 ? Peak : fallbackPeak >= 0 ? fallbackPeak : DurationMs / 2;
        var s0 = Math.Clamp(peak - (long)(length * 0.45), 0, DurationMs - length);
        if (Peak >= 0)
        {
            var bestI = Interest(s0, s0 + length);
            for (var s = 0L; s + length <= DurationMs; s += Bin)
            {
                var i = Interest(s, s + length);
                if (i > bestI * 1.25) { bestI = i; s0 = s; }
            }
        }
        var e0 = s0 + length;
        var density = Interest(s0, e0) / Math.Max(1, length) * 1000; // per second
        var best = (S: s0, E: e0, Score: double.MinValue);
        for (var s = Math.Max(0, s0 - (long)(length * stretch)); s <= Math.Min(DurationMs - min, s0 + (long)(length * 0.3)); s += 100)
        {
            var cs = Cost(s) * 4;
            for (var e = Math.Max(s + min, e0 - (long)(length * 0.3)); e <= Math.Min(Math.Min(DurationMs, s + max), e0 + (long)(length * stretch)); e += 100)
            {
                var len = e - s;
                var score = Interest(s, e) - Math.Abs(len - length) / (double)length * (density * length / 1000.0 * 0.8 + 0.5) - cs - Cost(e) * 4;
                foreach (var l in Laughs)
                {
                    var overlap = Math.Min(e, l.To) - Math.Max(s, l.From);
                    if (overlap <= Math.Max(300, (l.To - l.From) * 0.3)) continue;
                    if (s > l.Setup + 300) score -= 3 + l.Strength * 0.3; // the laugh without the joke
                    if (e < l.To) score -= 2 + l.Strength * 0.2; // the joke without the laugh
                }
                if (score > best.Score) best = (s, e, score);
            }
        }
        return (best.S, best.E);
    }
}
