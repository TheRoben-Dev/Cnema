using System.Diagnostics;
using System.Runtime.InteropServices;
using Cnema.Models;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Cnema.Services;

/// <summary>
/// Looks at videos with CLIP (ViT-B/32), on this computer: a few frames of each video become vectors that
/// can be compared with words ("snow", "a boss fight") or with other videos. Needs the optional download.
/// </summary>
public sealed class VisualService : IDisposable
{
    /// <summary>Bumped when the frames or the model change; older results are redone.</summary>
    public const int Version = 1;
    public const int Dim = 512;
    private const int Size = 224;
    private static readonly float[] Mean = { 0.48145466f, 0.4578275f, 0.40821073f };
    private static readonly float[] Std = { 0.26862954f, 0.26130258f, 0.27577711f };

    private readonly InferenceSession _vision;
    private readonly InferenceSession _text;
    private readonly ClipTokenizer _tokenizer;
    private readonly object _gate = new();
    private float[][]? _neutral;

    private VisualService(InferenceSession vision, InferenceSession text, ClipTokenizer tokenizer)
    {
        _vision = vision;
        _text = text;
        _tokenizer = tokenizer;
    }

    private static bool _resolverSet;

    /// <summary>Loads the models, or says why it can't.</summary>
    public static VisualService Load()
    {
        try
        {
            if (!_resolverSet)
            {
                // ONNX Runtime's library comes with the download, not with the app.
                var lib = ModelStore.PathOf(ModelStore.OnnxRuntime);
                NativeLibrary.SetDllImportResolver(typeof(InferenceSession).Assembly,
                    (name, _, _) => name.Contains("onnxruntime", StringComparison.OrdinalIgnoreCase) ? NativeLibrary.Load(lib) : IntPtr.Zero);
                _resolverSet = true;
            }
            var threads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
            var opts = new SessionOptions { IntraOpNumThreads = threads, InterOpNumThreads = 1, ExecutionMode = ExecutionMode.ORT_SEQUENTIAL };
            var vision = new InferenceSession(ModelStore.PathOf(ModelStore.Visual[0]), opts);
            var text = new InferenceSession(ModelStore.PathOf(ModelStore.Visual[1]), opts);
            var tok = new ClipTokenizer(ModelStore.PathOf(ModelStore.Visual[2]), ModelStore.PathOf(ModelStore.Visual[3]));
            return new VisualService(vision, text, tok);
        }
        catch (Exception ex)
        {
            Log.Warn($"Visual model didn't load: {ex}");
            throw new InvalidOperationException(F("Visual search can't run on this computer: {0}", ex.Message));
        }
    }

    /// <summary>Where to look: one frame every 10 seconds, at least 3 and at most 8, away from the very ends.</summary>
    public static long[] FrameTimes(long durationMs)
    {
        if (durationMs <= 0) return new long[] { 0 };
        var n = (int)Math.Clamp(durationMs / 10_000, 3, 8);
        return Enumerable.Range(0, n).Select(i => (long)(durationMs * (i + 0.5) / n)).ToArray();
    }

    /// <summary>Vectors for a video's frames (null for a video ffmpeg can't read a frame from).</summary>
    public async Task<VisualInfo?> LookAsync(string path, long durationMs, CancellationToken ct)
    {
        if (ToolLocator.Ffmpeg is not { } ffmpeg) return null;
        var times = new List<long>();
        var vectors = new List<float[]>();
        foreach (var at in FrameTimes(durationMs))
        {
            ct.ThrowIfCancellationRequested();
            var rgb = await GrabAsync(ffmpeg, path, at, ct);
            if (rgb is null) continue;
            vectors.Add(EmbedImage(rgb));
            times.Add(at);
        }
        return vectors.Count == 0 ? null : VisualInfo.From(times, vectors);
    }

    private static async Task<byte[]?> GrabAsync(string ffmpeg, string path, long atMs, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(ffmpeg)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in new[] { "-hide_banner", "-nostdin", "-loglevel", "error", "-ss", (atMs / 1000.0).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                     "-i", path, "-frames:v", "1", "-an", "-sn",
                     "-vf", $"scale={Size}:{Size}:force_original_aspect_ratio=increase:flags=bicubic,crop={Size}:{Size}",
                     "-f", "rawvideo", "-pix_fmt", "rgb24", "pipe:1" })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi);
        if (p is null) return null;
        ProcessRunner.Lower(p);
        _ = p.StandardError.ReadToEndAsync(ct);
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
        return ms.Length == Size * Size * 3 ? ms.ToArray() : null;
    }

    private float[] EmbedImage(byte[] rgb)
    {
        var t = new DenseTensor<float>(new[] { 1, 3, Size, Size });
        var span = t.Buffer.Span;
        const int plane = Size * Size;
        for (var i = 0; i < plane; i++)
            for (var c = 0; c < 3; c++)
                span[c * plane + i] = (rgb[i * 3 + c] / 255f - Mean[c]) / Std[c];
        lock (_gate)
        {
            using var r = _vision.Run(new[] { NamedOnnxValue.CreateFromTensor("pixel_values", t) });
            return Normalize(r.First(o => o.Name == "image_embeds").AsEnumerable<float>().ToArray());
        }
    }

    public float[] EmbedText(string text)
    {
        var ids = _tokenizer.Encode(text);
        var t = new DenseTensor<long>(ids, new[] { 1, ids.Length });
        lock (_gate)
        {
            using var r = _text.Run(new[] { NamedOnnxValue.CreateFromTensor("input_ids", t) });
            return Normalize(r.First(o => o.Name == "text_embeds").AsEnumerable<float>().ToArray());
        }
    }

    /// <summary>
    /// What any frame looks like to words that say nothing in particular. A search counts a frame
    /// only when it matches the words clearly better than these.
    /// </summary>
    public float[][] Neutral => _neutral ??= new[]
    {
        "a screenshot", "a video game", "a picture", "a video frame", "a screenshot of a video game", "something",
    }.Select(EmbedText).ToArray();

    internal static float[] Normalize(float[] v)
    {
        var n = MathF.Sqrt(v.Sum(x => x * x));
        if (n > 0) for (var i = 0; i < v.Length; i++) v[i] /= n;
        return v;
    }

    public void Dispose()
    {
        _vision.Dispose();
        _text.Dispose();
    }
}
