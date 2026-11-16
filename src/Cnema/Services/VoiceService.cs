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
    public const int Version = 1;
    private const int Rate = 16000, Window = 400, Hop = 160, Fft = 512, Bins = 80;
    /// <summary>Lines shorter than this don't say enough about a voice; longer ones are cut to the middle 8 s.</summary>
    private const int MinMs = 1200, MaxMs = 8000;
    /// <summary>Two lines are the same voice from about here (cosine of their voiceprints).</summary>
    public const float SameVoice = 0.5f;

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

    /// <summary>The voices in a video, from its transcript's lines (so it needs Voice search to have run).</summary>
    public async Task<VoiceInfo> ListenAsync(VideoEntry e, CancellationToken ct)
    {
        var info = new VoiceInfo { Version = Version };
        if (e.Speech is not { Lines.Count: > 0 } speech || ToolLocator.Ffmpeg is not { } ffmpeg) return info;
        var lines = speech.Lines.Where(l => l.End - l.Start >= MinMs && l.Text.Trim().Length > 0).OrderBy(l => l.Start).ToList();
        if (lines.Count == 0) return info;
        var tracks = (await MediaProbe.InfoAsync(e.Path, ct))?.AudioStreams ?? 0;
        if (tracks == 0) return info;

        var prints = new List<(SpeechLine Line, float[] Vec)>();
        var chunk = SpeechService.Chunk;
        float[]? audio = null;
        var audioAt = TimeSpan.MinValue;
        foreach (var l in lines.Take(80))
        {
            ct.ThrowIfCancellationRequested();
            // Read the five-minute stretch this line sits in, once.
            var at = TimeSpan.FromTicks(TimeSpan.FromMilliseconds(l.Start).Ticks / chunk.Ticks * chunk.Ticks);
            if (at != audioAt) { audio = await SpeechService.ReadAudioAsync(ffmpeg, e.Path, at, tracks, ct); audioAt = at; }
            var from = l.Start - (long)at.TotalMilliseconds;
            var to = Math.Min(l.End - (long)at.TotalMilliseconds, from + 300_000);
            if (to - from > MaxMs) { var mid = (from + to) / 2; from = mid - MaxMs / 2; to = mid + MaxMs / 2; }
            var a = (int)(from * Rate / 1000);
            var b = (int)Math.Min(audio!.Length, to * Rate / 1000);
            if (b - a < MinMs * Rate / 1000) continue;
            var vec = Embed(audio.AsSpan(a, b - a));
            if (vec is not null) prints.Add((l, vec));
        }
        foreach (var group in Group(prints.Select(p => p.Vec).ToList(), SameVoice))
        {
            var members = group.Select(i => prints[i]).ToList();
            var mean = Normalize(Mean(members.Select(m => m.Vec)));
            var best = members.OrderByDescending(m => Math.Min(m.Line.End - m.Line.Start, 6000) * (m.Line.Text.Length > 8 ? 1.2 : 1)).First();
            info.Speakers.Add(new VoiceSpeaker
            {
                Print = VoiceSpeaker.Pack(mean),
                Ms = members.Sum(m => m.Line.End - m.Line.Start),
                Lines = members.OrderByDescending(m => m.Line.End - m.Line.Start).Take(12).Select(m => new[] { m.Line.Start, m.Line.End }).ToList(),
                Quote = best.Line.Text.Trim(),
            });
        }
        info.Speakers.Sort((x, y) => y.Ms.CompareTo(x.Ms));
        return info;
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
        var groups = vecs.Select((_, i) => new List<int> { i }).ToList();
        var sim = new float[vecs.Count, vecs.Count];
        for (var i = 0; i < vecs.Count; i++)
            for (var j = i + 1; j < vecs.Count; j++) sim[i, j] = sim[j, i] = Cosine(vecs[i], vecs[j]);
        while (groups.Count > 1)
        {
            var best = float.MinValue;
            int bi = -1, bj = -1;
            for (var i = 0; i < groups.Count; i++)
                for (var j = i + 1; j < groups.Count; j++)
                {
                    double s = 0;
                    foreach (var a in groups[i]) foreach (var b in groups[j]) s += sim[a, b];
                    var avg = (float)(s / (groups[i].Count * groups[j].Count));
                    if (avg > best) { best = avg; bi = i; bj = j; }
                }
            if (best < threshold) break;
            groups[bi].AddRange(groups[bj]);
            groups.RemoveAt(bj);
        }
        return groups;
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
