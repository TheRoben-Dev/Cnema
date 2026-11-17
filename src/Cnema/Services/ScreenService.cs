using System.Diagnostics;
using System.Globalization;
using Cnema.Models;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Cnema.Services;

/// <summary>
/// Reads the text on screen (kill feeds, scoreboards, banners like VICTORY ROYALE or ACE) in a handful of
/// frames per video, on this computer: PaddleOCR's small text finder (PP-OCRv3 detection) marks where text is,
/// and its Latin-script reader (PP-OCRv5 mobile, Swedish letters included) writes it down. Needs the optional
/// download. The frames are the big moment, the end (where scoreboards and banners show) and a few in between.
/// </summary>
public sealed class ScreenService : IDisposable
{
    /// <summary>Bumped when the reading changes; older results are redone.</summary>
    public const int Version = 1;

    private const int DetSide = 960;     // the text finder looks at the frame this big (long side)
    private const int GrabWidth = 1920;  // the reader reads from the frame this wide at most
    private const int RecHeight = 48;
    private const float MinSure = 0.75f;

    private readonly InferenceSession _det, _rec;
    private readonly string _detIn, _recIn;
    private readonly string[] _chars;
    private readonly object _gate = new();

    private ScreenService(InferenceSession det, InferenceSession rec, string[] chars)
    {
        _det = det;
        _rec = rec;
        _detIn = det.InputMetadata.Keys.First();
        _recIn = rec.InputMetadata.Keys.First();
        _chars = chars;
    }

    public static ScreenService Load()
    {
        try
        {
            OrtNative.Ensure();
            // CTC classes: 0 is "nothing", then the dictionary in order, then a space.
            var dict = File.ReadAllText(ModelStore.PathOf(ModelStore.Screen[2])).Replace("\r", "").Split('\n');
            var count = dict.Length > 0 && dict[^1].Length == 0 ? dict.Length - 1 : dict.Length;
            var chars = new[] { "" }.Concat(dict.Take(count)).Append(" ").ToArray();
            var det = new InferenceSession(ModelStore.PathOf(ModelStore.Screen[0]), OrtNative.Options(2));
            var rec = new InferenceSession(ModelStore.PathOf(ModelStore.Screen[1]), OrtNative.Options(2));
            return new ScreenService(det, rec, chars);
        }
        catch (Exception ex)
        {
            Log.Warn($"Screen text model didn't load: {ex}");
            throw new InvalidOperationException(F("Text on screen can't be read on this computer: {0}", ex.Message));
        }
    }

    public void Dispose()
    {
        _det.Dispose();
        _rec.Dispose();
    }

    /// <summary>Where to look: the big moment, the end, and a few evenly in between (at most 11 frames).</summary>
    public static long[] FrameTimes(long durationMs, long bestMs)
    {
        if (durationMs <= 0) return new long[] { 0 };
        var times = new SortedSet<long>();
        var even = (int)Math.Clamp(durationMs / 5_000, 1, 8);
        for (var i = 0; i < even; i++) times.Add(durationMs * (2 * i + 1) / (2 * even));
        if (bestMs >= 0 && bestMs < durationMs)
        {
            times.Add(bestMs);
            if (bestMs + 1500 < durationMs) times.Add(bestMs + 1500);
        }
        times.Add(Math.Max(0, durationMs - 1500));
        // Frames closer than a second add nothing.
        var list = new List<long>();
        foreach (var t in times)
            if (list.Count == 0 || t - list[^1] >= 1000) list.Add(t);
        return list.ToArray();
    }

    public async Task<ScreenInfo> ReadAsync(string path, long durationMs, long bestMs, CancellationToken ct)
    {
        var info = new ScreenInfo { Version = Version };
        if (ToolLocator.Ffmpeg is not { } ffmpeg) return info;
        var probe = await MediaProbe.InfoAsync(path, ct);
        if (probe is null || probe.Width <= 0 || probe.Height <= 0) return info;
        var w = Math.Min(GrabWidth, probe.Width) / 2 * 2;
        var h = Math.Max(2, (int)Math.Round((double)probe.Height * w / probe.Width / 2) * 2);
        var seen = new HashSet<string>();
        foreach (var at in FrameTimes(durationMs, bestMs))
        {
            ct.ThrowIfCancellationRequested();
            var rgb = await GrabAsync(ffmpeg, path, at, w, h, ct);
            if (rgb is null) continue;
            foreach (var text in Read(rgb, w, h))
            {
                var key = Key(text);
                if (key.Length < 2 || !seen.Add(key)) continue;
                info.Lines.Add(new ScreenLine { At = at, Text = text });
                if (info.Lines.Count >= 80) return info;
            }
        }
        return info;
    }

    /// <summary>Upper case, letters and digits only, single spaces: for matching and for spotting repeats.</summary>
    internal static string Key(string text)
    {
        var chars = text.ToUpperInvariant().Select(c => char.IsLetterOrDigit(c) || c == '#' ? c : ' ').ToArray();
        return string.Join(' ', new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static async Task<byte[]?> GrabAsync(string ffmpeg, string path, long atMs, int w, int h, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(ffmpeg)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in new[] { "-hide_banner", "-nostdin", "-loglevel", "error", "-ss", (atMs / 1000.0).ToString("0.###", CultureInfo.InvariantCulture),
                     "-i", path, "-frames:v", "1", "-an", "-sn", "-dn",
                     "-vf", $"scale={w}:{h}:flags=bicubic", "-f", "rawvideo", "-pix_fmt", "rgb24", "pipe:1" })
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
        return ms.Length == (long)w * h * 3 ? ms.ToArray() : null;
    }

    /// <summary>The text in one frame (rgb24, w×h), top to bottom, left to right.</summary>
    internal List<string> Read(byte[] rgb, int w, int h)
    {
        var boxes = Find(rgb, w, h);
        var lines = new List<string>();
        foreach (var b in boxes.OrderBy(b => b.Y / 12).ThenBy(b => b.X))
        {
            var (text, sure) = Recognise(rgb, w, h, b);
            text = text.Trim();
            if (sure < MinSure || text.Length < 2 || !text.Any(char.IsLetterOrDigit)) continue;
            lines.Add(text);
        }
        return lines;
    }

    private readonly record struct Box(int X, int Y, int W, int H);

    private static readonly float[] DetMean = { 0.485f, 0.456f, 0.406f }, DetStd = { 0.229f, 0.224f, 0.225f };

    /// <summary>Text boxes (in frame pixels): the finder's map, joined into blobs, each grown a little to take the whole word.</summary>
    private List<Box> Find(byte[] rgb, int w, int h)
    {
        var s = (double)DetSide / Math.Max(w, h);
        var dw = Math.Max(32, (int)Math.Round(w * s / 32) * 32);
        var dh = Math.Max(32, (int)Math.Round(h * s / 32) * 32);
        var input = new DenseTensor<float>(new[] { 1, 3, dh, dw });
        var span = input.Buffer.Span;
        var plane = dw * dh;
        for (var y = 0; y < dh; y++)
        {
            var sy = Math.Min(h - 1, (int)((y + 0.5) * h / dh));
            for (var x = 0; x < dw; x++)
            {
                var sx = Math.Min(w - 1, (int)((x + 0.5) * w / dw));
                var o = (sy * w + sx) * 3;
                for (var c = 0; c < 3; c++) span[c * plane + y * dw + x] = (rgb[o + c] / 255f - DetMean[c]) / DetStd[c];
            }
        }
        float[] map;
        lock (_gate)
        {
            using var r = _det.Run(new[] { NamedOnnxValue.CreateFromTensor(_detIn, input) });
            map = r.First().AsEnumerable<float>().ToArray();
        }
        if (map.Length != plane) return new();

        // Blobs of "text here" (above 0.3), kept when the finder is sure on average (0.6).
        var label = new int[plane];
        var stack = new Stack<int>();
        var boxes = new List<Box>();
        var next = 0;
        for (var i = 0; i < plane; i++)
        {
            if (map[i] <= 0.3f || label[i] != 0) continue;
            next++;
            int x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1, n = 0;
            double sum = 0;
            label[i] = next;
            stack.Push(i);
            while (stack.Count > 0)
            {
                var k = stack.Pop();
                int kx = k % dw, ky = k / dw;
                n++;
                sum += map[k];
                if (kx < x0) x0 = kx;
                if (kx > x1) x1 = kx;
                if (ky < y0) y0 = ky;
                if (ky > y1) y1 = ky;
                if (kx > 0 && map[k - 1] > 0.3f && label[k - 1] == 0) { label[k - 1] = next; stack.Push(k - 1); }
                if (kx < dw - 1 && map[k + 1] > 0.3f && label[k + 1] == 0) { label[k + 1] = next; stack.Push(k + 1); }
                if (ky > 0 && map[k - dw] > 0.3f && label[k - dw] == 0) { label[k - dw] = next; stack.Push(k - dw); }
                if (ky < dh - 1 && map[k + dw] > 0.3f && label[k + dw] == 0) { label[k + dw] = next; stack.Push(k + dw); }
            }
            if (n < 10 || sum / n < 0.6) continue;
            double bw = x1 - x0 + 1, bh = y1 - y0 + 1;
            if (bh < 3) continue;
            var grow = bw * bh * 1.5 / (2 * (bw + bh)); // the finder marks the core of the text; this takes in the edges
            var fx0 = Math.Max(0, (x0 - grow) * w / dw);
            var fy0 = Math.Max(0, (y0 - grow) * h / dh);
            var fx1 = Math.Min(w, (x1 + 1 + grow) * w / dw);
            var fy1 = Math.Min(h, (y1 + 1 + grow) * h / dh);
            var box = new Box((int)fx0, (int)fy0, (int)(fx1 - fx0), (int)(fy1 - fy0));
            if (box.W >= 4 && box.H >= 6) boxes.Add(box);
        }
        return boxes;
    }

    /// <summary>One box written out (CTC: the likeliest character per step, repeats and blanks dropped) and how sure it is.</summary>
    private (string Text, float Sure) Recognise(byte[] rgb, int w, int h, Box b)
    {
        var rw = Math.Clamp((int)Math.Round((double)b.W * RecHeight / b.H), 8, 1280);
        var input = new DenseTensor<float>(new[] { 1, 3, RecHeight, rw });
        var span = input.Buffer.Span;
        var plane = RecHeight * rw;
        for (var y = 0; y < RecHeight; y++)
        {
            var fy = b.Y + (y + 0.5) * b.H / RecHeight - 0.5;
            var y0 = Math.Clamp((int)Math.Floor(fy), 0, h - 1);
            var y1 = Math.Min(h - 1, y0 + 1);
            var ty = (float)Math.Clamp(fy - y0, 0, 1);
            for (var x = 0; x < rw; x++)
            {
                var fx = b.X + (x + 0.5) * b.W / rw - 0.5;
                var x0 = Math.Clamp((int)Math.Floor(fx), 0, w - 1);
                var x1 = Math.Min(w - 1, x0 + 1);
                var tx = (float)Math.Clamp(fx - x0, 0, 1);
                for (var c = 0; c < 3; c++)
                {
                    var top = rgb[(y0 * w + x0) * 3 + c] * (1 - tx) + rgb[(y0 * w + x1) * 3 + c] * tx;
                    var bottom = rgb[(y1 * w + x0) * 3 + c] * (1 - tx) + rgb[(y1 * w + x1) * 3 + c] * tx;
                    span[c * plane + y * rw + x] = ((top * (1 - ty) + bottom * ty) / 255f - 0.5f) / 0.5f;
                }
            }
        }
        float[] probs;
        int steps, classes;
        lock (_gate)
        {
            using var r = _rec.Run(new[] { NamedOnnxValue.CreateFromTensor(_recIn, input) });
            var t = r.First().AsTensor<float>();
            steps = t.Dimensions[1];
            classes = t.Dimensions[2];
            probs = t.ToArray();
        }
        var sb = new System.Text.StringBuilder();
        var last = 0;
        double sure = 0;
        var count = 0;
        for (var s = 0; s < steps; s++)
        {
            int best = 0;
            var bestP = float.MinValue;
            for (var c = 0; c < classes; c++)
            {
                var p = probs[s * classes + c];
                if (p > bestP) { bestP = p; best = c; }
            }
            if (best != 0 && best != last && best < _chars.Length)
            {
                sb.Append(_chars[best]);
                sure += bestP;
                count++;
            }
            last = best;
        }
        return (string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries)), count == 0 ? 0 : (float)(sure / count));
    }
}
