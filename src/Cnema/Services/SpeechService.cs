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

    private readonly WhisperFactory _factory;

    private SpeechService(WhisperFactory factory) => _factory = factory;

    /// <summary>Loads the model, or says why it can't (no model, or the speech library won't load here).</summary>
    public static SpeechService Load(string modelPath)
    {
        PreloadBundled();
        // CPU only: steady, light on a computer that may be gaming, and no GPU shader warm-up at start.
        try { return new SpeechService(WhisperFactory.FromPath(modelPath, new WhisperFactoryOptions { UseGpu = false })); }
        catch (Exception ex)
        {
            Log.Warn($"Speech model didn't load: {ex}");
            throw new InvalidOperationException(F("Voice search can't run on this computer: {0}", ex.Message));
        }
    }

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
        var info = new SpeechInfo { Version = Version };
        var tracks = (await MediaProbe.InfoAsync(path, ct))?.AudioStreams ?? 0;
        if (tracks == 0 || ToolLocator.Ffmpeg is not { } ffmpeg) return info;
        var languages = new Dictionary<string, int>();
        var length = durationMs > 0 ? TimeSpan.FromMilliseconds(durationMs) : TimeSpan.MaxValue;
        for (var at = TimeSpan.Zero; at < length; at += Chunk)
        {
            var samples = await ReadAudioAsync(ffmpeg, path, at, tracks, ct);
            if (samples.Length < Rate / 2) break; // the end, or less than half a second left
            if (Peak(samples) < 0.01f) continue;  // silence: Whisper would only make things up
            await using var processor = _factory.CreateBuilder()
                .WithLanguage("auto")
                .WithThreads(Threads)
                .WithNoSpeechThreshold(0.6f)
                .Build();
            await foreach (var s in processor.ProcessAsync(samples, ct))
            {
                var text = Clean(s.Text);
                if (text is null || s.NoSpeechProbability > 0.6f) continue;
                var start = (long)(at + s.Start).TotalMilliseconds;
                var end = (long)(at + s.End).TotalMilliseconds;
                if (info.Lines.Count > 0 && info.Lines[^1].Text == text) { info.Lines[^1].End = end; continue; } // a repeat loop
                info.Lines.Add(new SpeechLine { Start = start, End = Math.Max(end, start + 300), Text = text });
                if (s.Language is { Length: > 0 } lang) languages[lang] = languages.GetValueOrDefault(lang) + text.Length;
            }
            if (samples.Length < Chunk.TotalSeconds * Rate) break;
        }
        info.Language = languages.Count == 0 ? null : languages.MaxBy(kv => kv.Value).Key;
        return info;
    }

    /// <summary>A stretch of the sound as 16 kHz mono floats, all tracks mixed.</summary>
    internal static async Task<float[]> ReadAudioAsync(string ffmpeg, string path, TimeSpan at, int tracks, CancellationToken ct, TimeSpan? length = null)
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
        ProcessRunner.Lower(p);
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

    private static float Peak(float[] samples)
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

    public void Dispose() => _factory.Dispose();
}
