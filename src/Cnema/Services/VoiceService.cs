using Cnema.Models;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Cnema.Services;

/// <summary>
/// Who's talking: a voiceprint for each line the transcript found (WeSpeaker ResNet34, on this computer), and
/// the lines of one video grouped into voices. A voiceprint is 256 numbers; two of the same person point the
/// same way (cosine close to 1), two people apart. Names come later, from the "Who's that?" game.
/// </summary>
public sealed class VoiceService : IDisposable
{
    /// <summary>Bumped when the listening changes; older results are redone.</summary>
    public const int Version = 2;
    private const int Rate = 16000, Window = 400, Hop = 160, Fft = 512, Bins = 80;
    /// <summary>Two voices are the same person from about here (cosine of their voiceprints).</summary>
    public const float SameVoice = 0.5f;

    /// <summary>
    /// Lines are listened to in slices this long, this far apart (overlapping), so a change of voice inside a line
    /// is heard. Lines shorter than <see cref="MinLineMs"/> say too little; a video gives at most <see cref="MaxSlices"/>.
    /// </summary>
    private const int SliceMs = 1500, StepMs = 750, MinLineMs = 900, MaxSlices = 600;
    /// <summary>Slices are grouped into voices from this likeness (average linkage; slices are short, so it's lower).</summary>
    private const float SliceSame = 0.3f;
    /// <summary>A slice counts for its voice when it's at least this alike, and this much closer than to any other voice.</summary>
    private const float MinOwn = 0.4f, Sure = 0.15f;
    /// <summary>Slices quieter than this share of the usual talking are game sound, not someone talking.</summary>
    private const float Hush = 0.4f;
    /// <summary>
    /// A pause is <see cref="PauseMs"/> of sound under <see cref="Quiet"/> of the way from quiet to loud. The ends of a
    /// stretch move in to a pause within <see cref="SnapMs"/>, or by <see cref="Trim"/> when there's none.
    /// </summary>
    private const int PauseMs = 120, SnapMs = 900, Trim = 200;
    private const float Quiet = 0.2f;

    private readonly InferenceSession _model;
    private readonly string _input;
    private static readonly float[] Povey = Enumerable.Range(0, Window).Select(i => (float)Math.Pow(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (Window - 1)), 0.85)).ToArray();
    private static readonly float[][] Mel = MelBanks();

    private VoiceService(InferenceSession model)
    {
        _model = model;
        _input = model.InputMetadata.Keys.First();
    }

    public static VoiceService Load()
    {
        try
        {
            OrtNative.Ensure();
            return new VoiceService(new InferenceSession(ModelStore.PathOf(ModelStore.Voice[0]), OrtNative.Options(2)));
        }
        catch (Exception ex)
        {
            Log.Warn($"Voice model didn't load: {ex}");
            throw new InvalidOperationException(F("Voices can't be told apart on this computer: {0}", ex.Message));
        }
    }

    public void Dispose() => _model.Dispose();

    /// <summary>A short slice of one line and its voiceprint; Who is the voice it was grouped with (-1: unsure).</summary>
    private sealed class Slice
    {
        public required int Line;
        public required long Start, End;
        public required float[] Vec;
        public required double Loud;
        public int Who = -1;
        public bool Clean;
    }

    /// <summary>
    /// The voices in a video, from its transcript's lines (so it needs Voice search to have run). Each line is
    /// listened to in short overlapping slices, so a line where two people talk (one cutting in, or both at
    /// once) is split where the voice changes, and slices that sound like more than one person are left out.
    /// A voice's voiceprint, and the bits "Who's that?" plays, come only from stretches where that one voice talks.
    /// </summary>
    public async Task<VoiceInfo> ListenAsync(VideoEntry e, CancellationToken ct)
    {
        var info = new VoiceInfo { Version = Version };
        if (e.Speech is not { Lines.Count: > 0 } speech || ToolLocator.Ffmpeg is not { } ffmpeg) return info;
        var lines = speech.Lines.Where(l => l.End - l.Start >= MinLineMs && l.Text.Trim().Length > 0).OrderBy(l => l.Start).ToList();
        if (lines.Count == 0) return info;
        var tracks = (await MediaProbe.InfoAsync(e.Path, ct))?.AudioStreams ?? 0;
        if (tracks == 0) return info;
        // A long video: an even spread of its lines, so it takes about as long as a few minutes of talking.
        var talk = lines.Sum(l => Math.Min(l.End - l.Start, 300_000));
        var budget = (long)MaxSlices * StepMs;
        if (talk > budget)
        {
            var keep = (double)budget / talk;
            lines = lines.Where((_, i) => Math.Floor((i + 1) * keep) > Math.Floor(i * keep)).ToList();
        }

        var slices = new List<Slice>();
        var pauses = new Dictionary<int, List<(long From, long To)>>();
        var chunk = SpeechService.Chunk;
        float[]? audio = null;
        var audioAt = TimeSpan.MinValue;
        for (var li = 0; li < lines.Count; li++)
        {
            ct.ThrowIfCancellationRequested();
            var l = lines[li];
            // Read the five-minute stretch this line sits in, once.
            var at = TimeSpan.FromTicks(TimeSpan.FromMilliseconds(l.Start).Ticks / chunk.Ticks * chunk.Ticks);
            if (at != audioAt) { audio = await SpeechService.ReadAudioAsync(ffmpeg, e.Path, at, tracks, ct); audioAt = at; }
            var off = (long)at.TotalMilliseconds;
            var from = l.Start - off;
            var to = Math.Min(Math.Min(l.End - off, from + 300_000), audio!.Length * 1000L / Rate);
            if (to - from < MinLineMs) continue;
            var len = to - from;
            var n = len <= SliceMs * 4 / 3 ? 1 : (int)Math.Ceiling((len - SliceMs) / (double)StepMs) + 1;
            var size = n == 1 ? len : SliceMs;
            for (var k = 0; k < n; k++)
            {
                var s = n == 1 ? from : from + (long)Math.Round(k * (len - size) / (double)(n - 1));
                var (sa, sn) = ((int)(s * Rate / 1000), (int)(size * Rate / 1000));
                var vec = Embed(audio.AsSpan(sa, sn));
                if (vec is not null) slices.Add(new Slice { Line = li, Start = s + off, End = s + size + off, Vec = vec, Loud = Loudness(audio, sa, sn) });
            }
            pauses[li] = Pauses(audio, from, to).Select(p => (p.From + off, p.To + off)).ToList();
        }
        if (slices.Count == 0) return info;

        // Group the slices by voice. A voice heard in a single slice is too little to go on.
        var groups = Group(slices.Select(s => s.Vec).ToList(), SliceSame).Where(g => g.Count >= 2).ToList();
        var centres = groups.Select(g => Normalize(Mean(g.Select(i => slices[i].Vec)))).ToList();
        for (var gi = 0; gi < groups.Count; gi++)
            foreach (var i in groups[gi]) slices[i].Who = gi;
        // A slice counts when it's clearly closer to its own voice than to any other: a slice with two people in
        // it sits between them, and one with only game noise near nobody. One much quieter than the usual talking
        // is game sound the transcript took for words.
        var loudness = slices.Select(s => s.Loud).OrderBy(x => x).ToList();
        var talking = loudness[loudness.Count / 2];
        foreach (var s in slices.Where(s => s.Who >= 0))
        {
            if (s.Loud < Hush * talking) continue;
            var own = Cosine(s.Vec, centres[s.Who]);
            var other = centres.Where((_, i) => i != s.Who).Select(c => Cosine(s.Vec, c)).DefaultIfEmpty(0).Max();
            s.Clean = own >= MinOwn && own - other >= Sure;
        }

        // Stretches of one voice: clean slices in a row, of the same voice, within one line.
        var runs = new List<(int Who, long Start, long End, int Line, List<Slice> Parts)>();
        foreach (var byLine in slices.GroupBy(s => s.Line))
        {
            var row = byLine.OrderBy(s => s.Start).ToList();
            for (var i = 0; i < row.Count;)
            {
                if (!row[i].Clean) { i++; continue; }
                var j = i;
                while (j + 1 < row.Count && row[j + 1].Clean && row[j + 1].Who == row[i].Who) j++;
                // The ends move in to a pause near them, where people take turns (or a little, when there's none):
                // the slice next to it may be another voice, and a line's edge can hold the end of the last one.
                var start = Snap(row[i].Start, true, pauses[byLine.Key]);
                var end = Snap(row[j].End, false, pauses[byLine.Key]);
                if (end - start >= 1000) runs.Add((row[i].Who, start, end, byLine.Key, row.GetRange(i, j - i + 1)));
                i = j + 1;
            }
        }

        foreach (var voice in runs.GroupBy(r => r.Who))
        {
            var mine = voice.ToList();
            var parts = mine.SelectMany(r => r.Parts).ToList();
            var print = Normalize(Mean(parts.Select(p => p.Vec)));
            // Best bits to play first: long enough to hear (up to 6 s), and whole lines, so what was said can be shown.
            bool Whole((int Who, long Start, long End, int Line, List<Slice> Parts) r) =>
                r.End - r.Start >= 0.8 * (lines[r.Line].End - lines[r.Line].Start);
            var ordered = mine.OrderByDescending(r => Math.Min(r.End - r.Start, 6000) * (Whole(r) && lines[r.Line].Text.Trim().Length > 8 ? 1.25 : 1)).ToList();
            var best = ordered[0];
            info.Speakers.Add(new VoiceSpeaker
            {
                Print = VoiceSpeaker.Pack(print),
                Ms = mine.Sum(r => r.End - r.Start),
                Lines = ordered.Take(12).Select(r => new[] { r.Start, r.End }).ToList(),
                Quote = Whole(best) ? lines[best.Line].Text.Trim() : "",
            });
        }
        info.Speakers.Sort((x, y) => y.Ms.CompareTo(x.Ms));
        return info;
    }

    /// <summary>
    /// The pauses in [from, to) (ms into <paramref name="audio"/>): 20 ms steps quieter than a fifth of the way from
    /// the quiet to the loud of this stretch, for at least <see cref="PauseMs"/>.
    /// </summary>
    private static List<(long From, long To)> Pauses(float[] audio, long from, long to)
    {
        const int step = 20, n = Rate * step / 1000;
        var a = (int)(from * Rate / 1000);
        var count = (int)Math.Min((to - from) / step, (audio.Length - a) / n);
        var found = new List<(long, long)>();
        if (count <= 0) return found;
        var rms = new double[count];
        for (var f = 0; f < count; f++)
        {
            double sum = 0;
            for (var i = a + f * n; i < a + (f + 1) * n; i++) sum += audio[i] * audio[i];
            rms[f] = Math.Sqrt(sum / n);
        }
        var sorted = rms.OrderBy(x => x).ToArray();
        var lo = sorted[count / 10];
        var hi = sorted[Math.Min(count - 1, count * 9 / 10)];
        var quiet = lo + Quiet * (hi - lo);
        int? start = null;
        for (var f = 0; f <= count; f++)
        {
            if (f < count && rms[f] <= quiet) { start ??= f; continue; }
            if (start is { } s && (f - s) * step >= PauseMs) found.Add((from + s * step, from + f * step));
            start = null;
        }
        return found;
    }

    private static double Loudness(float[] x, int from, int count)
    {
        double sum = 0;
        for (var i = from; i < from + count; i++) sum += x[i] * x[i];
        return count == 0 ? 0 : Math.Sqrt(sum / count);
    }

    /// <summary>Moves one end of a stretch in to the end of a pause near it, or by <see cref="Trim"/> when there's none.</summary>
    private static long Snap(long at, bool isStart, List<(long From, long To)> pauses)
    {
        if (isStart)
        {
            var p = pauses.Where(p => p.To > at && p.To <= at + SnapMs).Select(p => (long?)p.To).Max();
            return p ?? at + Trim;
        }
        var q = pauses.Where(p => p.From < at && p.From >= at - SnapMs).Select(p => (long?)p.From).Min();
        return q ?? at - Trim;
    }

    /// <summary>The voiceprint of a stretch of 16 kHz audio, length 1; null when it's too short or silent.</summary>
    public float[]? Embed(ReadOnlySpan<float> samples)
    {
        var feats = Fbank(samples);
        if (feats is null) return null;
        var frames = feats.Length / Bins;
        using var result = _model.Run(new[] { NamedOnnxValue.CreateFromTensor(_input, new DenseTensor<float>(feats, new[] { 1, frames, Bins })) });
        return Normalize(result.First().AsEnumerable<float>().ToArray());
    }

    public static float Cosine(float[] a, float[] b)
    {
        if (a.Length != b.Length || a.Length == 0) return 0;
        double dot = 0;
        for (var i = 0; i < a.Length; i++) dot += a[i] * b[i];
        return (float)dot; // both have length 1
    }

    public static float[] Normalize(float[] v)
    {
        var n = Math.Sqrt(v.Sum(x => (double)x * x));
        if (n < 1e-9) return v;
        return v.Select(x => (float)(x / n)).ToArray();
    }

    public static float[] Mean(IEnumerable<float[]> vecs)
    {
        float[]? sum = null;
        var count = 0;
        foreach (var v in vecs)
        {
            sum ??= new float[v.Length];
            for (var i = 0; i < v.Length; i++) sum[i] += v[i];
            count++;
        }
        if (sum is null) return Array.Empty<float>();
        for (var i = 0; i < sum.Length; i++) sum[i] /= count;
        return sum;
    }

    /// <summary>
    /// Groups voiceprints that belong together: average linkage, merging the closest two groups while they're
    /// at least <paramref name="threshold"/> alike. Returns the members of each group (indexes).
    /// </summary>
    public static List<List<int>> Group(IReadOnlyList<float[]> vecs, float threshold)
    {
        var n = vecs.Count;
        var members = vecs.Select((_, i) => new List<int> { i }).ToList();
        var alive = Enumerable.Repeat(true, n).ToArray();
        // The average likeness between two groups, kept up to date as they merge (Lance-Williams).
        var sim = new float[n, n];
        for (var i = 0; i < n; i++)
            for (var j = i + 1; j < n; j++) sim[i, j] = sim[j, i] = Cosine(vecs[i], vecs[j]);
        for (var left = n; left > 1; left--)
        {
            var best = float.MinValue;
            int bi = -1, bj = -1;
            for (var i = 0; i < n; i++)
            {
                if (!alive[i]) continue;
                for (var j = i + 1; j < n; j++)
                    if (alive[j] && sim[i, j] > best) { best = sim[i, j]; bi = i; bj = j; }
            }
            if (best < threshold) break;
            double si = members[bi].Count, sj = members[bj].Count;
            for (var k = 0; k < n; k++)
                if (alive[k] && k != bi && k != bj) sim[bi, k] = sim[k, bi] = (float)((sim[bi, k] * si + sim[bj, k] * sj) / (si + sj));
            members[bi].AddRange(members[bj]);
            alive[bj] = false;
        }
        return members.Where((_, i) => alive[i]).ToList();
    }

    // ---------- Kaldi-style filterbank, as the model was trained on ----------

    /// <summary>80 log mel energies every 10 ms (25 ms Povey windows, pre-emphasis 0.97), mean taken off each band.</summary>
    internal static float[]? Fbank(ReadOnlySpan<float> samples)
    {
        if (samples.Length < Window) return null;
        var frames = 1 + (samples.Length - Window) / Hop;
        if (frames < 50) return null; // under half a second
        var feats = new float[frames * Bins];
        var re = new double[Fft];
        var im = new double[Fft];
        var x = new double[Window];
        for (var f = 0; f < frames; f++)
        {
            var o = f * Hop;
            double mean = 0;
            for (var i = 0; i < Window; i++) { x[i] = samples[o + i] * 32768.0; mean += x[i]; }
            mean /= Window;
            for (var i = 0; i < Window; i++) x[i] -= mean;
            for (var i = Window - 1; i > 0; i--) x[i] -= 0.97 * x[i - 1];
            x[0] -= 0.97 * x[0];
            Array.Clear(re);
            Array.Clear(im);
            for (var i = 0; i < Window; i++) re[i] = x[i] * Povey[i];
            SoundService.FftInPlace(re, im);
            for (var b = 0; b < Bins; b++)
            {
                double e = 0;
                var w = Mel[b];
                for (var k = 0; k < w.Length; k++) if (w[k] != 0) e += w[k] * (re[k] * re[k] + im[k] * im[k]);
                feats[f * Bins + b] = (float)Math.Log(Math.Max(e, 1.1920929e-07));
            }
        }
        // Mean normalisation per band, over the whole stretch.
        for (var b = 0; b < Bins; b++)
        {
            double m = 0;
            for (var f = 0; f < frames; f++) m += feats[f * Bins + b];
            m /= frames;
            for (var f = 0; f < frames; f++) feats[f * Bins + b] -= (float)m;
        }
        return feats;
    }

    /// <summary>Kaldi's triangular mel filters, 20 Hz to 8 kHz, over the first 256 FFT bins.</summary>
    private static float[][] MelBanks()
    {
        static double MelOf(double hz) => 1127.0 * Math.Log(1 + hz / 700.0);
        const int bins = Fft / 2;
        var lo = MelOf(20);
        var hi = MelOf(Rate / 2.0);
        var delta = (hi - lo) / (Bins + 1);
        var banks = new float[Bins][];
        for (var b = 0; b < Bins; b++)
        {
            var left = lo + b * delta;
            var centre = left + delta;
            var right = centre + delta;
            var w = new float[bins];
            for (var k = 0; k < bins; k++)
            {
                var mel = MelOf(Rate * (double)k / Fft);
                if (mel > left && mel < right)
                    w[k] = (float)(mel <= centre ? (mel - left) / (centre - left) : (right - mel) / (right - centre));
            }
            banks[b] = w;
        }
        return banks;
    }
}
