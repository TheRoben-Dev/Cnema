using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cnema.Services;

/// <summary>
/// What Savefile hears in a song, for cutting a video to it: the beats, where the bars start, how loud and
/// busy each beat is, the key, and whether it fades out at the end. Plain signal processing on this computer
/// (an onset envelope, its autocorrelation for the tempo, dynamic programming for the beats); no model.
/// </summary>
public sealed class SongInfo
{
    public int Version { get; set; }
    public string Path { get; set; } = "";
    public long Size { get; set; }
    public DateTime ModifiedUtc { get; set; }
    public double Seconds { get; set; }
    public double Bpm { get; set; }
    /// <summary>Beat times in seconds.</summary>
    public List<double> Beats { get; set; } = new();
    /// <summary>Index into <see cref="Beats"/> of the first downbeat (the "one" of a bar, 0 to 3).</summary>
    public int FirstDownbeat { get; set; }
    /// <summary>How loud each beat is (dB, RMS), from that beat to the next.</summary>
    public List<float> BeatDb { get; set; } = new();
    /// <summary>How busy each beat is (onset strength, about 0 to 3).</summary>
    public List<float> BeatDrive { get; set; } = new();
    /// <summary>0 to 11 C..B major, 12 to 23 C..B minor; -1 unknown.</summary>
    public int Key { get; set; } = -1;
    /// <summary>The last bars fade out or get quiet: ending on the song's own ending sounds natural.</summary>
    public bool FadesOut { get; set; }

    [JsonIgnore] public double BeatSeconds => Bpm > 0 ? 60 / Bpm : 0.5;
    [JsonIgnore] public double BarSeconds => BeatSeconds * 4;
    [JsonIgnore] public bool Usable => Bpm > 0 && Beats.Count >= 32;

    /// <summary>How energetic the song is overall (for ordering songs so the video builds up).</summary>
    [JsonIgnore] public double Drive => BeatDrive.Count == 0 ? 0 : BeatDrive.Average();

    /// <summary>Each beat's loudness from 0 (the song's quiet parts) to 1 (its loudest).</summary>
    public float[] Energy()
    {
        if (BeatDb.Count == 0) return Array.Empty<float>();
        var sorted = BeatDb.OrderBy(x => x).ToList();
        var lo = sorted[(int)(sorted.Count * 0.10)];
        var hi = sorted[(int)Math.Min(sorted.Count - 1, sorted.Count * 0.95)];
        var span = Math.Max(3, hi - lo);
        return BeatDb.Select(d => Math.Clamp((d - lo) / span, 0f, 1f)).ToArray();
    }

    /// <summary>Beat indices where a bar starts.</summary>
    public IEnumerable<int> Downbeats()
    {
        for (var i = FirstDownbeat; i < Beats.Count; i += 4) yield return i;
    }

    /// <summary>Key in words ("A minor"), or null.</summary>
    [JsonIgnore]
    public string? KeyName => Key < 0 ? null
        : new[] { "C", "C♯", "D", "E♭", "E", "F", "F♯", "G", "A♭", "A", "B♭", "B" }[Key % 12] + (Key >= 12 ? " minor" : " major");
}

public static class MusicAnalyzer
{
    public const int Version = 1;
    private const int Rate = 22_050, Frame = 2048, Hop = 512;
    private const double FramesPerSecond = (double)Rate / Hop;

    private static readonly string CacheFile = System.IO.Path.Combine(AppPaths.DataDir, "music.json");
    private static readonly SemaphoreSlim CacheGate = new(1, 1);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    /// <summary>The song's analysis, from the cache when the file hasn't changed. Throws with a message fit to show.</summary>
    public static async Task<SongInfo> GetAsync(string path, CancellationToken ct)
    {
        var file = new FileInfo(path);
        if (!file.Exists) throw new FileNotFoundException(Loc.F("The song is missing: {0}", System.IO.Path.GetFileName(path)));
        await CacheGate.WaitAsync(ct);
        Dictionary<string, SongInfo> cache;
        try { cache = Load(); }
        finally { CacheGate.Release(); }
        if (cache.TryGetValue(path, out var known) && known.Version == Version && known.Size == file.Length && known.ModifiedUtc == file.LastWriteTimeUtc)
            return known;

        var watch = Stopwatch.StartNew();
        var samples = await DecodeAsync(path, ct);
        if (samples.Length < Rate * 5) throw new InvalidOperationException(Loc.F("{0} is too short to cut a video to.", System.IO.Path.GetFileName(path)));
        var info = await Task.Run(() => Analyze(samples, ct), ct);
        info.Path = path;
        info.Size = file.Length;
        info.ModifiedUtc = file.LastWriteTimeUtc;
        Log.Info($"Song analysed in {watch.ElapsedMilliseconds} ms: {info.Bpm:0.#} BPM, {info.Beats.Count} beats, key {info.KeyName ?? "?"}, fades out {info.FadesOut}");

        await CacheGate.WaitAsync(ct);
        try
        {
            cache = Load();
            cache[path] = info;
            // Only songs still on disk are kept.
            foreach (var gone in cache.Keys.Where(k => !File.Exists(k)).ToList()) cache.Remove(gone);
            Directory.CreateDirectory(AppPaths.DataDir);
            await File.WriteAllTextAsync(CacheFile, JsonSerializer.Serialize(cache, Json), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { Log.Warn($"Music cache: {ex.Message}"); }
        finally { CacheGate.Release(); }
        return info;
    }

    /// <summary>Already analysed and unchanged (no work), else null.</summary>
    public static SongInfo? Cached(string path)
    {
        try
        {
            var file = new FileInfo(path);
            return file.Exists && Load().TryGetValue(path, out var s) && s.Version == Version && s.Size == file.Length && s.ModifiedUtc == file.LastWriteTimeUtc ? s : null;
        }
        catch { return null; }
    }

    private static Dictionary<string, SongInfo> Load()
    {
        try
        {
            if (File.Exists(CacheFile))
                return JsonSerializer.Deserialize<Dictionary<string, SongInfo>>(File.ReadAllText(CacheFile), Json) is { } d
                    ? new Dictionary<string, SongInfo>(d, StringComparer.Ordinal) : new(StringComparer.Ordinal);
        }
        catch (Exception ex) { Log.Warn($"Music cache unreadable: {ex.Message}"); }
        return new(StringComparer.Ordinal);
    }

    private static async Task<float[]> DecodeAsync(string path, CancellationToken ct)
    {
        var ffmpeg = ToolLocator.Ffmpeg ?? throw new InvalidOperationException(T("ffmpeg not found."));
        var psi = new ProcessStartInfo(ffmpeg) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-hide_banner", "-nostdin", "-loglevel", "error", "-i", path, "-vn", "-ac", "1", "-ar", Rate.ToString(), "-f", "f32le", "pipe:1" })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg didn't start");
        ProcessRunner.Lower(p);
        var err = p.StandardError.ReadToEndAsync(ct);
        using var ms = new MemoryStream();
        try
        {
            await p.StandardOutput.BaseStream.CopyToAsync(ms, ct);
            await p.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch { /* gone */ }
            throw;
        }
        if (ms.Length == 0) throw new InvalidOperationException(Loc.F("Savefile couldn't read {0}: {1}", System.IO.Path.GetFileName(path), (await err).Trim()));
        var samples = new float[ms.Length / 4];
        Buffer.BlockCopy(ms.GetBuffer(), 0, samples, 0, samples.Length * 4);
        return samples;
    }

    /// <summary>The analysis itself, on decoded 22.05 kHz mono samples.</summary>
    public static SongInfo Analyze(float[] x, CancellationToken ct = default)
    {
        var frames = (x.Length - Frame) / Hop + 1;
        var bins = Frame / 2 + 1;
        var window = new double[Frame];
        for (var i = 0; i < Frame; i++) window[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / Frame);
        var re = new double[Frame];
        var im = new double[Frame];
        var prev = new double[bins];
        var cur = new double[bins];
        var onset = new double[frames];
        var bassEnergy = new double[frames];
        var frameChroma = new float[frames * 12];
        var rms = new double[frames];
        var chroma = new double[12];
        var hz = (double)Rate / Frame;
        var top = (int)(8000 / hz);
        var bassTop = (int)(160 / hz);
        // The pitch class of every bin between 55 Hz and 2 kHz, for the key.
        var pitch = new int[bins];
        for (var k = 0; k < bins; k++)
        {
            var f = k * hz;
            pitch[k] = f is < 55 or > 2000 ? -1 : ((int)Math.Round(12 * Math.Log2(f / 440) + 69) % 12 + 12) % 12;
        }

        for (var n = 0; n < frames; n++)
        {
            if ((n & 1023) == 0) ct.ThrowIfCancellationRequested();
            var off = n * Hop;
            double energy = 0;
            for (var i = 0; i < Frame; i++)
            {
                var s = x[off + i];
                energy += s * s;
                re[i] = s * window[i];
                im[i] = 0;
            }
            rms[n] = Math.Sqrt(energy / Frame);
            Fft(re, im);
            double flux = 0, bass = 0;
            var fc = frameChroma.AsSpan(n * 12, 12);
            for (var k = 1; k < bins; k++)
            {
                var mag = Math.Sqrt(re[k] * re[k] + im[k] * im[k]);
                cur[k] = Math.Log(1 + 100 * mag);
                var rise = cur[k] - prev[k];
                if (rise > 0)
                {
                    if (k <= top) flux += rise;
                }
                if (k <= bassTop) bass += mag;
                if (pitch[k] >= 0) { chroma[pitch[k]] += mag; fc[pitch[k]] += (float)mag; }
            }
            onset[n] = flux;
            bassEnergy[n] = bass;
            (prev, cur) = (cur, prev);
        }

        var info = new SongInfo { Version = Version, Seconds = x.Length / (double)Rate };
        var o = Detrend(onset);
        var period = Tempo(o);
        if (period <= 0) return info;
        var beats = TrackBeats(o, period);
        if (beats.Count < 8) return info;

        // Tempo from the beats found: the slope of beat position against beat number (finer than a frame).
        double mi = (beats.Count - 1) / 2.0, mb = beats.Average(), sxy = 0, sxx = 0;
        for (var i = 0; i < beats.Count; i++) { sxy += (i - mi) * (beats[i] - mb); sxx += (i - mi) * (i - mi); }
        info.Bpm = Math.Round(60 * FramesPerSecond / (sxy / sxx), 1);
        var offset = Frame / 2.0 / Rate;
        info.Beats = beats.Select(b => Math.Round(b / FramesPerSecond + offset, 3)).ToList();

        // The "one" of the bar: kick drums land on it (bass energy right at the beat) and chords change on it
        // (the notes heard over this beat differ from the beat before).
        var bassAt = new double[beats.Count];
        var change = new double[beats.Count];
        var last = new double[12];
        for (var i = 0; i < beats.Count; i++)
        {
            var from = beats[i];
            var to = i + 1 < beats.Count ? beats[i + 1] : Math.Min(frames, from + (int)period);
            for (var n = from; n < Math.Min(to, from + 4); n++) bassAt[i] += bassEnergy[n];
            var c = new double[12];
            for (var n = from; n < to; n++) for (var k = 0; k < 12; k++) c[k] += frameChroma[n * 12 + k];
            change[i] = i == 0 ? 0 : 1 - Cosine(last, c);
            last = c;
        }
        Standardize(bassAt);
        Standardize(change);
        var best = double.MinValue;
        for (var phase = 0; phase < 4; phase++)
        {
            double sum = 0;
            var count = 0;
            for (var i = phase; i < beats.Count; i += 4) { sum += bassAt[i] + change[i]; count++; }
            if (count > 0 && sum / count > best) { best = sum / count; info.FirstDownbeat = phase; }
        }

        for (var i = 0; i < beats.Count; i++)
        {
            var from = beats[i];
            var to = i + 1 < beats.Count ? beats[i + 1] : Math.Min(frames, from + (int)period);
            double sq = 0, drive = 0;
            for (var n = from; n < to; n++) { sq += rms[n] * rms[n]; drive += o[n]; }
            var len = Math.Max(1, to - from);
            info.BeatDb.Add((float)Math.Round(10 * Math.Log10(sq / len + 1e-10), 1));
            info.BeatDrive.Add((float)Math.Round(drive / len, 3));
        }

        // Quiet at the end compared with the song's middle: it fades out or ends softly.
        var sortedDb = info.BeatDb.OrderBy(d => d).ToList();
        var median = sortedDb[sortedDb.Count / 2];
        var tail = info.BeatDb.Skip(Math.Max(0, info.BeatDb.Count - 8)).Average();
        info.FadesOut = tail < median - 6;
        info.Key = KeyOf(chroma);
        return info;
    }

    /// <summary>The onset envelope minus its running average, rectified and scaled to unit spread.</summary>
    private static double[] Detrend(double[] v)
    {
        var o = new double[v.Length];
        const int w = 16;
        double sum = 0;
        for (var i = 0; i < v.Length; i++)
        {
            sum += v[i];
            if (i >= w) sum -= v[i - w];
            var mean = sum / Math.Min(i + 1, w);
            o[i] = Math.Max(0, v[i] - mean);
        }
        var mu = o.Average();
        var sd = Math.Sqrt(o.Select(a => (a - mu) * (a - mu)).Average());
        if (sd > 0) for (var i = 0; i < o.Length; i++) o[i] /= sd;
        return o;
    }

    private static double Cosine(double[] a, double[] b)
    {
        double ab = 0, aa = 0, bb = 0;
        for (var i = 0; i < a.Length; i++) { ab += a[i] * b[i]; aa += a[i] * a[i]; bb += b[i] * b[i]; }
        return aa <= 0 || bb <= 0 ? 1 : ab / Math.Sqrt(aa * bb);
    }

    private static void Standardize(double[] v)
    {
        if (v.Length == 0) return;
        var mu = v.Average();
        var sd = Math.Sqrt(v.Select(a => (a - mu) * (a - mu)).Average());
        for (var i = 0; i < v.Length; i++) v[i] = sd > 1e-9 ? (v[i] - mu) / sd : 0;
    }

    /// <summary>The beat period in frames: the autocorrelation peak, weighted towards 120 BPM, with octave checks.</summary>
    private static double Tempo(double[] o)
    {
        var minLag = (int)(FramesPerSecond * 60 / 200);
        var maxLag = (int)(FramesPerSecond * 60 / 60);
        var n = Math.Min(o.Length, (int)(FramesPerSecond * 90)); // the first 90 s are plenty
        var start = Math.Max(0, Math.Min(o.Length - n, (int)(FramesPerSecond * 20))); // skip a quiet intro if there's room
        var ac = new double[maxLag * 2 + 2];
        for (var lag = minLag / 2; lag < ac.Length; lag++)
        {
            double s = 0;
            for (var i = start; i + lag < start + n && i + lag < o.Length; i++) s += o[i] * o[i + lag];
            ac[lag] = s;
        }
        double best = 0;
        var bestLag = 0;
        for (var lag = minLag; lag <= maxLag; lag++)
        {
            var bpm = 60 * FramesPerSecond / lag;
            var weight = Math.Exp(-0.5 * Math.Pow(Math.Log2(bpm / 120) / 0.9, 2));
            // A true beat period also lines up at twice the lag.
            var score = weight * (ac[lag] + 0.5 * ac[Math.Min(ac.Length - 1, lag * 2)]);
            if (score > best) { best = score; bestLag = lag; }
        }
        if (bestLag == 0) return 0;
        // Parabolic refinement between neighbouring lags.
        var a = ac[bestLag - 1];
        var b = ac[bestLag];
        var c = ac[bestLag + 1];
        var den = a - 2 * b + c;
        return den < 0 ? bestLag + 0.5 * (a - c) / den : bestLag;
    }

    /// <summary>Dynamic-programming beat tracker (Ellis 2007): beats on strong onsets, about one period apart.</summary>
    private static List<int> TrackBeats(double[] o, double period)
    {
        const double tightness = 100;
        var n = o.Length;
        var score = new double[n];
        var back = new int[n];
        for (var t = 0; t < n; t++)
        {
            var from = (int)Math.Round(t - 2 * period);
            var to = (int)Math.Round(t - period / 2);
            double best = double.MinValue;
            var arg = -1;
            for (var p = Math.Max(0, from); p <= to; p++)
            {
                var r = Math.Log((t - p) / period);
                var s = score[p] - tightness * r * r;
                if (s > best) { best = s; arg = p; }
            }
            score[t] = o[t] + (arg >= 0 ? Math.Max(0, best) : 0);
            back[t] = arg >= 0 && best > 0 ? arg : -1;
        }
        // End on the best-scoring beat near the end, then follow the links back.
        var last = n - 1;
        double top = double.MinValue;
        for (var t = Math.Max(0, n - (int)(2 * period)); t < n; t++)
            if (score[t] > top) { top = score[t]; last = t; }
        var beats = new List<int>();
        for (var t = last; t >= 0; t = back[t]) beats.Add(t);
        beats.Reverse();
        // Fill a quiet intro with beats at the same period, so bars line up from the very start.
        while (beats.Count > 0 && beats[0] - period >= 0) beats.Insert(0, (int)Math.Round(beats[0] - period));
        return beats;
    }

    private static readonly double[] Major = { 6.35, 2.23, 3.48, 2.33, 4.38, 4.09, 2.52, 5.19, 2.39, 3.66, 2.29, 2.88 };
    private static readonly double[] Minor = { 6.33, 2.68, 3.52, 5.38, 2.60, 3.53, 2.54, 4.75, 3.98, 2.69, 3.34, 3.17 };

    /// <summary>Krumhansl–Kessler key profiles against the song's pitch-class totals.</summary>
    private static int KeyOf(double[] chroma)
    {
        if (chroma.Sum() <= 0) return -1;
        var best = double.MinValue;
        var key = -1;
        for (var tonic = 0; tonic < 12; tonic++)
            foreach (var (profile, minor) in new[] { (Major, 0), (Minor, 12) })
            {
                var r = Correlation(chroma, profile, tonic);
                if (r > best) { best = r; key = tonic + minor; }
            }
        return best > 0.3 ? key : -1;
    }

    private static double Correlation(double[] chroma, double[] profile, int tonic)
    {
        var mx = chroma.Average();
        var my = profile.Average();
        double sxy = 0, sxx = 0, syy = 0;
        for (var i = 0; i < 12; i++)
        {
            var dx = chroma[(i + tonic) % 12] - mx;
            var dy = profile[i] - my;
            sxy += dx * dy; sxx += dx * dx; syy += dy * dy;
        }
        return sxy / Math.Sqrt(sxx * syy + 1e-12);
    }

    /// <summary>
    /// How well two keys go one after the other, 0 (same key) up: neighbours on the Camelot wheel (a fifth
    /// apart, or relative major and minor) score 1, unknown keys 2.
    /// </summary>
    public static int KeyDistance(int a, int b)
    {
        if (a < 0 || b < 0) return 2;
        static (int Number, bool Minor) Camelot(int k) =>
            k < 12 ? ((k * 7 % 12 + 7) % 12 + 1, false) : (((k - 12 + 3) % 12 * 7 % 12 + 7) % 12 + 1, true);
        var (na, ma) = Camelot(a);
        var (nb, mb) = Camelot(b);
        var d = Math.Abs(na - nb);
        d = Math.Min(d, 12 - d);
        return d + (ma == mb ? 0 : 1);
    }

    private static void Fft(double[] re, double[] im)
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
            var wr = Math.Cos(ang);
            var wi = Math.Sin(ang);
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
                    var nr = cr * wr - ci * wi;
                    ci = cr * wi + ci * wr;
                    cr = nr;
                }
            }
        }
    }
}
