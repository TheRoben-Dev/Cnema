using System.Diagnostics;
using System.Text.RegularExpressions;
using Cnema.Models;
using Whisper.net;

namespace Cnema.Services;

/// <summary>
/// Writes down what's said in a video with Whisper (whisper.cpp), on this computer. The sound goes to the
/// model a few minutes at a time, every audio track mixed together so voice chat on its own track counts.
/// </summary>
public sealed partial class SpeechService : IDisposable
{
    /// <summary>Bumped when the way speech is written down changes; older results are redone.</summary>
    public const int Version = 1;
    private const int Rate = 16_000;
    internal static readonly TimeSpan Chunk = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How the sound is cut up and handed to the model. Bumped when that changes; clips written down the old way
    /// keep their words, and Settings offers to redo them (it's never done behind your back: it takes a while).
    /// </summary>
    public const int Pipeline = 2;

    private readonly WhisperFactory _factory;
    /// <summary>The standard model, when another one writes: it tells the languages apart and writes what isn't Swedish.</summary>
    private readonly WhisperFactory? _general;
    private readonly string _modelId;
    private readonly bool _swedishOnly;
    /// <summary>The model only writes Swedish well (KB-Whisper): other languages go to the standard model.</summary>
    private bool _leaveOthers;

    /// <summary>What wrote a transcript: the model and the way the sound was cut up ("speech-sv/2").</summary>
    public string Id => $"{_modelId}/{Pipeline}";

    private SpeechService(WhisperFactory factory, WhisperFactory? general, string modelId, bool swedishOnly)
    {
        _factory = factory;
        _general = general;
        _modelId = modelId;
        _swedishOnly = swedishOnly;
    }

    /// <summary>
    /// Loads the model, or says why it can't (no model, or the speech library won't load here). A model other than
    /// the standard one comes with the standard one: it picks the language of each stretch of speech, cheaply, and
    /// writes the stretches the Swedish model can't (KB-Whisper turns English into Swedish).
    /// </summary>
    public static SpeechService Load(ModelInfo model, ModelInfo? general = null, bool swedishOnly = false)
    {
        PreloadBundled();
        // CPU only: steady, light on a computer that may be gaming, and no GPU shader warm-up at start.
        var options = new WhisperFactoryOptions { UseGpu = false };
        WhisperFactory? main = null;
        try
        {
            main = WhisperFactory.FromPath(ModelStore.PathOf(model), options);
            var helper = general is not null && general.Id != model.Id && ModelStore.IsInstalled(general)
                ? WhisperFactory.FromPath(ModelStore.PathOf(general), options) : null;
            return new SpeechService(main, helper, model.Id, swedishOnly) { _leaveOthers = model.Id == ModelStore.SpeechSwedish.Id };
        }
        catch (Exception ex)
        {
            main?.Dispose();
            Log.Warn($"Speech model didn't load: {ex}");
            throw new InvalidOperationException(F("Voice search can't run on this computer: {0}", ex.Message));
        }
    }

    /// <summary>Back-compat for the self-test: one model file, nothing else.</summary>
    public static SpeechService Load(string modelPath)
    {
        PreloadBundled();
        try { return new SpeechService(WhisperFactory.FromPath(modelPath, new WhisperFactoryOptions { UseGpu = false }), null, Path.GetFileNameWithoutExtension(modelPath), false); }
        catch (Exception ex)
        {
            Log.Warn($"Speech model didn't load: {ex}");
            throw new InvalidOperationException(F("Voice search can't run on this computer: {0}", ex.Message));
        }
    }

    /// <summary>
    /// The languages speech is written down in: English, the language Savefile is shown in and the computer's own.
    /// Telling the model which ones to expect is most of what makes it right: left to guess, the small models hear
    /// Swedish as Norwegian, or decide on one language for minutes of mixed Swedish and English.
    /// </summary>
    public static string[] Languages =>
        new[] { "en", Loc.Current, System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName,
                System.Globalization.CultureInfo.InstalledUICulture.TwoLetterISOLanguageName, ModelStore.SpeechChoice == "swedish" ? "sv" : "en" }
            .Select(c => (c ?? "").ToLowerInvariant()).Where(Known.Contains).Distinct().ToArray();

    /// <summary>Languages Whisper writes (the common ones; a computer set to another one just gets English).</summary>
    private static readonly HashSet<string> Known = new(StringComparer.Ordinal)
    {
        "en", "sv", "no", "da", "fi", "de", "nl", "fr", "es", "pt", "it", "pl", "cs", "sk", "hu", "ro", "el", "tr", "ru", "uk",
        "ar", "he", "fa", "hi", "ja", "ko", "zh", "th", "vi", "id", "ms", "is", "et", "lv", "lt", "bg", "hr", "sr", "sl", "ca",
    };

    /// <summary>
    /// Linux: the OpenMP runtime the speech library needs ships in runtimes/linux-x64. Loading it first by
    /// its full path lets the speech library find it without anything installed on the system.
    /// </summary>
    private static void PreloadBundled()
    {
        if (!OperatingSystem.IsLinux()) return;
        var gomp = Path.Combine(AppContext.BaseDirectory, "runtimes", "linux-x64", "libgomp.so.1");
        if (File.Exists(gomp)) System.Runtime.InteropServices.NativeLibrary.TryLoad(gomp, out _);
    }

    /// <summary>The speech library's build info, or null when its native part doesn't load (self-test).</summary>
    public static string? RuntimeCheck()
    {
        PreloadBundled();
        try { return WhisperFactory.GetRuntimeInfo()?.Trim() is { Length: > 0 } s ? s.Split('\n')[0] : "loaded"; }
        catch (Exception ex) { Console.Error.WriteLine(ex.Message); return null; }
    }

    /// <summary>
    /// Gets the speech model ready for the loading screen: reads the model file once, so the first video
    /// it listens to doesn't wait on the disk. Holds no memory afterwards.
    /// </summary>
    public static void Warm(string modelPath)
    {
        if (!File.Exists(modelPath)) return;
        using var f = new FileStream(modelPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        var buffer = new byte[1 << 20];
        while (f.Read(buffer, 0, buffer.Length) > 0) { }
    }

    /// <summary>Threads for the model: some, never all; this is background work.</summary>
    private static int Threads => Math.Clamp(Environment.ProcessorCount / 2, 1, 4);

    public async Task<SpeechInfo> TranscribeAsync(string path, long durationMs, CancellationToken ct)
    {
        var info = new SpeechInfo { Version = Version, Model = Id };
        var tracks = (await MediaProbe.InfoAsync(path, ct))?.AudioStreams ?? 0;
        if (tracks == 0 || ToolLocator.Ffmpeg is not { } ffmpeg) return info;
        var languages = new Dictionary<string, int>();
        var length = durationMs > 0 ? TimeSpan.FromMilliseconds(durationMs) : TimeSpan.MaxValue;
        var allowed = _swedishOnly ? new[] { "sv" } : Languages.Distinct().ToArray();
        string? last = null;
        for (var at = TimeSpan.Zero; at < length; at += Chunk)
        {
            var samples = await ReadAudioAsync(ffmpeg, path, at, tracks, ct);
            if (samples.Length < Rate / 2) break; // the end, or less than half a second left
            if (Peak(samples) < 0.01f) { if (samples.Length < Chunk.TotalSeconds * Rate) break; continue; } // silence: Whisper would only make things up
            // A stretch of speech at a time, cut where it's quietest, each in its own language: a sentence in
            // Swedish and the next in English are both written down as said, not translated or garbled.
            foreach (var (from, to) in Stretches(samples))
            {
                ct.ThrowIfCancellationRequested();
                var part = samples.AsMemory(from, to - from);
                if (Peak(part.Span) < 0.01f) continue;
                var lang = Language(part.Span, allowed, last);
                last = lang;
                var factory = lang != "sv" && _leaveOthers && _general is not null ? _general : _factory;
                await using var processor = factory.CreateBuilder()
                    .WithLanguage(lang)
                    .WithThreads(Threads)
                    .WithNoSpeechThreshold(0.6f)
                    .Build();
                var offset = at + TimeSpan.FromSeconds((double)from / Rate);
                await foreach (var s in processor.ProcessAsync(part.ToArray(), ct))
                {
                    var text = Clean(s.Text);
                    if (text is null || s.NoSpeechProbability > 0.6f) continue;
                    var start = (long)(offset + s.Start).TotalMilliseconds;
                    var end = (long)(offset + s.End).TotalMilliseconds;
                    if (info.Lines.Count > 0 && info.Lines[^1].Text == text) { info.Lines[^1].End = end; continue; } // a repeat loop
                    info.Lines.Add(new SpeechLine { Start = start, End = Math.Max(end, start + 300), Text = text });
                    languages[lang] = languages.GetValueOrDefault(lang) + text.Length;
                }
            }
            if (samples.Length < Chunk.TotalSeconds * Rate) break;
        }
        info.Language = languages.Count == 0 ? null : languages.MaxBy(kv => kv.Value).Key;
        return info;
    }

    /// <summary>
    /// The language of a stretch, out of the ones expected. With one expected there's nothing to ask; a guess the
    /// model isn't sure of keeps the language of the stretch before (people don't switch every sentence).
    /// </summary>
    private string Language(ReadOnlySpan<float> part, string[] allowed, string? last)
    {
        if (allowed.Length == 1) return allowed[0];
        try
        {
            using var detector = (_general ?? _factory).CreateBuilder().WithThreads(Threads).Build();
            var (lang, p) = detector.DetectLanguageWithProbability(part, allowed);
            if (lang is null || (p < 0.5f && last is not null)) return last ?? allowed[0];
            return lang;
        }
        catch (Exception ex)
        {
            Log.Warn($"Speech: telling the language apart failed: {ex.Message}");
            return last ?? allowed[0];
        }
    }

    /// <summary>
    /// Where to cut the sound into stretches of 8 to 28 seconds (the model hears 30 at a time): at a pause when there
    /// is one, else at the quietest moment of the last few seconds, so no word is cut in half.
    /// </summary>
    internal static IEnumerable<(int From, int To)> Stretches(float[] a)
    {
        const int frame = Rate / 50; // 20 ms
        int Min(double s) => (int)(s * Rate);
        var rms = new float[a.Length / frame];
        for (var f = 0; f < rms.Length; f++)
        {
            double e = 0;
            for (var k = f * frame; k < (f + 1) * frame; k++) e += a[k] * a[k];
            rms[f] = (float)Math.Sqrt(e / frame);
        }
        var sorted = rms.Where(x => x > 0).OrderBy(x => x).ToArray();
        // Quiet: well under the usual level of this stretch of sound (a game never goes fully silent).
        var quiet = sorted.Length == 0 ? 0.01f : Math.Max(0.004f, sorted[sorted.Length / 4] * 0.8f);
        var start = 0;
        var still = 0;
        for (var f = 0; f < rms.Length; f++)
        {
            still = rms[f] < quiet ? still + 1 : 0;
            var len = f * frame - start;
            if (len >= Min(8) && still >= 15)
            {
                var cut = (f - still / 2) * frame;
                yield return (start, cut);
                start = cut;
                still = 0;
            }
            else if (len >= Min(28))
            {
                // No pause: the quietest 20 ms of the last 6 seconds.
                var from = f - Min(6) / frame;
                var best = from;
                for (var g = from; g <= f; g++) if (rms[g] < rms[best]) best = g;
                var cut = best * frame;
                yield return (start, cut);
                start = cut;
                still = 0;
            }
        }
        if (a.Length - start >= Rate / 2) yield return (start, a.Length);
        else if (start > 0 && a.Length > start) { /* a sliver at the end: nothing worth hearing */ }
    }

    /// <summary>A stretch of the sound as 16 kHz mono floats, all tracks mixed.</summary>
    internal static async Task<float[]> ReadAudioAsync(string ffmpeg, string path, TimeSpan at, int tracks, CancellationToken ct, TimeSpan? length = null, bool background = true)
    {
        var psi = new ProcessStartInfo(ffmpeg)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in new[] { "-hide_banner", "-nostdin", "-loglevel", "error", "-ss", at.TotalSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                     "-t", (length ?? Chunk).TotalSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture), "-i", path, "-vn", "-sn", "-dn" })
            psi.ArgumentList.Add(a);
        if (tracks > 1)
        {
            psi.ArgumentList.Add("-filter_complex");
            psi.ArgumentList.Add(string.Concat(Enumerable.Range(0, tracks).Select(i => $"[0:a:{i}]")) + $"amix=inputs={tracks}:duration=longest:normalize=0[a]");
            psi.ArgumentList.Add("-map");
            psi.ArgumentList.Add("[a]");
        }
        foreach (var a in new[] { "-ac", "1", "-ar", Rate.ToString(), "-f", "f32le", "pipe:1" }) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg didn't start");
        if (background) ProcessRunner.Lower(p); // indexing; someone waiting on it (the voice game) runs at once
        var errTask = p.StandardError.ReadToEndAsync(ct);
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
        if (p.ExitCode != 0 && ms.Length == 0) throw new InvalidOperationException((await errTask).Trim());
        var bytes = ms.GetBuffer();
        var samples = new float[ms.Length / 4];
        Buffer.BlockCopy(bytes, 0, samples, 0, samples.Length * 4);
        return samples;
    }

    private static float Peak(ReadOnlySpan<float> samples)
    {
        var peak = 0f;
        foreach (var s in samples) peak = Math.Max(peak, Math.Abs(s));
        return peak;
    }

    /// <summary>The text worth keeping, or null for nothing, noise tags and the phrases Whisper invents over music.</summary>
    internal static string? Clean(string raw)
    {
        var t = Bracketed().Replace(raw, " ");
        t = Spaces().Replace(t, " ").Trim().Trim('-', '♪', '♫', '*', ' ');
        if (t.Length < 2 || !t.Any(char.IsLetterOrDigit)) return null;
        var bare = t.TrimEnd('.', '!', '?', ' ').ToLowerInvariant();
        if (Invented.Contains(bare) || bare.StartsWith("subtitles by", StringComparison.Ordinal) ||
            bare.StartsWith("translated by", StringComparison.Ordinal) || bare.Contains("amara.org")) return null;
        return t;
    }

    private static readonly HashSet<string> Invented = new(StringComparer.Ordinal)
    {
        "thank you", "thanks for watching", "thank you for watching", "thank you so much for watching", "you",
        "bye", "bye-bye", "please subscribe", "like and subscribe", "so", "okay", "oh", "music", "applause",
        "tack för att du tittade", "tack", "danke", "merci", "gracias",
    };

    [GeneratedRegex(@"\[[^\]]*\]|\([^)]*\)|\*[^*]*\*")]
    private static partial Regex Bracketed();
    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    public void Dispose()
    {
        _factory.Dispose();
        _general?.Dispose();
    }
}
