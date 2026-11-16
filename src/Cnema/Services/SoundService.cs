using Cnema.Models;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Cnema.Services;

/// <summary>
/// Hears laughter and hype (cheering, shouting, screaming) with YAMNet, a small sound classifier, on this
/// computer. The sound is turned into the log-mel picture YAMNet was trained on (16 kHz, 25 ms windows every
/// 10 ms, 64 bands from 125 to 7500 Hz) and judged a second at a time. Needs the optional download.
/// </summary>
public sealed class SoundService : IDisposable
{
    /// <summary>Bumped when the listening changes; older results are redone.</summary>
    public const int Version = 1;
    private const int Rate = 16000;
    private const int Window = 400, Hop = 160, Fft = 512, Bands = 64, PatchFrames = 96, PatchHop = 48;
    private const double MinHz = 125, MaxHz = 7500, LogOffset = 0.001;

    // AudioSet classes, by their row in YAMNet's output.
    private static readonly int[] LaughClasses = { 13, 14, 15, 16, 17, 18 }; // Laughter, Baby laughter, Giggle, Snicker, Belly laugh, Chuckle/chortle
    private static readonly int[] HypeClasses = { 6, 8, 9, 11, 61, 62 };     // Shout, Whoop, Yell, Screaming, Cheering, Applause

    /// <summary>A moment counts when a class group scores this much (0 to 1). Real laughter in game noise scores 0.15–0.4; speech and music stay at 0.</summary>
    internal const float LaughAt = 0.15f, HypeAt = 0.3f;

    private readonly InferenceSession _model;
    private readonly string _input;
    private static readonly float[] Hann = Enumerable.Range(0, Window).Select(i => (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / Window))).ToArray();
    private static readonly float[][] Mel = MelMatrix();

    private SoundService(InferenceSession model)
    {
        _model = model;
        _input = model.InputMetadata.Keys.First();
    }

    public static SoundService Load()
    {
        try
        {
            OrtNative.Ensure();
            return new SoundService(new InferenceSession(ModelStore.PathOf(ModelStore.Sound[0]), OrtNative.Options(2)));
        }
        catch (Exception ex)
        {
            Log.Warn($"Sound model didn't load: {ex}");
            throw new InvalidOperationException(F("Laughter and hype can't be heard on this computer: {0}", ex.Message));
        }
    }

    public void Dispose() => _model.Dispose();

    /// <summary>Where people laugh and where it gets loud with excitement, in five-minute stretches.</summary>
    public async Task<SoundInfo> ListenAsync(string path, long durationMs, CancellationToken ct)
    {
        var info = new SoundInfo { Version = Version };
        var tracks = (await MediaProbe.InfoAsync(path, ct))?.AudioStreams ?? 0;
        if (tracks == 0 || ToolLocator.Ffmpeg is not { } ffmpeg) return info;
        var chunk = TimeSpan.FromMinutes(5);
        var length = durationMs > 0 ? TimeSpan.FromMilliseconds(durationMs) : TimeSpan.MaxValue;
        for (var at = TimeSpan.Zero; at < length; at += chunk)
        {
            var samples = await SpeechService.ReadAudioAsync(ffmpeg, path, at, tracks, ct);
            if (samples.Length < Rate) break;
            foreach (var (offset, laugh, hype) in Judge(samples, ct))
            {
                var ms = (long)(at.TotalMilliseconds + offset);
                info.Laugh.Add(new SoundMoment { At = ms, Score = MathF.Round(laugh, 3) });
                info.Hype.Add(new SoundMoment { At = ms, Score = MathF.Round(hype, 3) });
            }
            if (samples.Length < chunk.TotalSeconds * Rate) break;
        }
        // Only what stands out is kept, so the library stays small.
        info.Laugh.RemoveAll(m => m.Score < LaughAt);
        info.Hype.RemoveAll(m => m.Score < HypeAt);
        return info;
    }

    /// <summary>Every 0.48 s: the strongest laughter and hype scores in the 0.96 s from there.</summary>
    public IEnumerable<(double OffsetMs, float Laugh, float Hype)> Judge(float[] samples, CancellationToken ct)
    {
        var mel = LogMel(samples);
        var patch = new float[PatchFrames * Bands];
        for (var start = 0; start + PatchFrames <= mel.Length; start += PatchHop)
        {
            ct.ThrowIfCancellationRequested();
            // Quiet stretches can't hold laughter or cheering: skip the model.
            var loud = false;
            for (var f = start; f < start + PatchFrames && !loud; f++)
                if (mel[f].Max() > -6) loud = true;
            if (!loud) continue;
            for (var f = 0; f < PatchFrames; f++) Array.Copy(mel[start + f], 0, patch, f * Bands, Bands);
            var input = new DenseTensor<float>(patch, new[] { 1, 1, PatchFrames, Bands });
            using var result = _model.Run(new[] { NamedOnnxValue.CreateFromTensor(_input, input) });
            var scores = result.First().AsEnumerable<float>().ToArray();
            // The model gives logits; a sigmoid turns them into 0-to-1 scores.
            yield return (start * 10.0, Sigmoid(LaughClasses.Max(i => scores[i])), Sigmoid(HypeClasses.Max(i => scores[i])));
        }
    }

    private static float Sigmoid(float x) => 1f / (1f + MathF.Exp(-x));

    /// <summary>All 521 scores for one 0.96 s patch starting at <paramref name="frame"/> (tests and tuning).</summary>
    public float[] Scores(float[] samples, int frame = 0)
    {
        var mel = LogMel(samples);
        var patch = new float[PatchFrames * Bands];
        for (var f = 0; f < PatchFrames && frame + f < mel.Length; f++) Array.Copy(mel[frame + f], 0, patch, f * Bands, Bands);
        using var result = _model.Run(new[] { NamedOnnxValue.CreateFromTensor(_input, new DenseTensor<float>(patch, new[] { 1, 1, PatchFrames, Bands })) });
        return result.First().AsEnumerable<float>().ToArray();
    }

    /// <summary>log(mel magnitude + 0.001) for each 10 ms frame.</summary>
    internal static float[][] LogMel(float[] samples)
    {
        var frames = samples.Length < Window ? 0 : 1 + (samples.Length - Window) / Hop;
        var result = new float[frames][];
        var re = new double[Fft];
        var im = new double[Fft];
        var mag = new double[Fft / 2 + 1];
        for (var f = 0; f < frames; f++)
        {
            Array.Clear(re);
            Array.Clear(im);
            var o = f * Hop;
            for (var i = 0; i < Window; i++) re[i] = samples[o + i] * Hann[i];
            FftInPlace(re, im);
            for (var k = 0; k <= Fft / 2; k++) mag[k] = Math.Sqrt(re[k] * re[k] + im[k] * im[k]);
            var row = new float[Bands];
            for (var b = 0; b < Bands; b++)
            {
                double sum = 0;
                var w = Mel[b];
                for (var k = 0; k < w.Length; k++) sum += w[k] * mag[k];
                row[b] = (float)Math.Log(sum + LogOffset);
            }
            result[f] = row;
        }
        return result;
    }

    /// <summary>Triangular mel filters (HTK scale), as TensorFlow's linear_to_mel_weight_matrix builds them.</summary>
    private static float[][] MelMatrix()
    {
        static double ToMel(double hz) => 1127.0 * Math.Log(1 + hz / 700.0);
        var bins = Fft / 2 + 1;
        var lo = ToMel(MinHz);
        var hi = ToMel(MaxHz);
        var edges = Enumerable.Range(0, Bands + 2).Select(i => lo + (hi - lo) * i / (Bands + 1)).ToArray();
        var m = new float[Bands][];
        for (var b = 0; b < Bands; b++)
        {
            m[b] = new float[bins];
            for (var k = 1; k < bins; k++) // the DC bin stays zero
            {
                var mel = ToMel((double)k * Rate / 2 / (bins - 1));
                var up = (mel - edges[b]) / (edges[b + 1] - edges[b]);
                var down = (edges[b + 2] - mel) / (edges[b + 2] - edges[b + 1]);
                m[b][k] = (float)Math.Max(0, Math.Min(up, down));
            }
        }
        return m;
    }

    internal static void FftInPlace(double[] re, double[] im)
    {
        var n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }
        for (var len = 2; len <= n; len <<= 1)
        {
            var ang = -2 * Math.PI / len;
            double wr = Math.Cos(ang), wi = Math.Sin(ang);
            for (var i = 0; i < n; i += len)
            {
                double cr = 1, ci = 0;
                for (var k = 0; k < len / 2; k++)
                {
                    var a = i + k;
                    var b = a + len / 2;
                    var tr = re[b] * cr - im[b] * ci;
                    var ti = re[b] * ci + im[b] * cr;
                    re[b] = re[a] - tr; im[b] = im[a] - ti;
                    re[a] += tr; im[a] += ti;
                    var ncr = cr * wr - ci * wi;
                    ci = cr * wi + ci * wr;
                    cr = ncr;
                }
            }
        }
    }
}
