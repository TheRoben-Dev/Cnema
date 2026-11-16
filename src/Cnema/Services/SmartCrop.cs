using System.Globalization;
using System.Text;

namespace Cnema.Services;

/// <summary>
/// Smart crop for the vertical (9:16) export: instead of always the middle slice, the slice follows where
/// things move. A small grey copy of the exported part (128 pixels wide, 4 frames a second) is compared
/// frame to frame, column by column. A HUD that never changes counts for nothing, a camera turn moves every
/// column alike and so cancels out, and what's left is where the action is. The path is smoothed so the
/// slice glides rather than jitters, and it stays in the middle when nothing stands out (a first-person game
/// looking around). Local, a few seconds of one core at export time, no model.
/// </summary>
public static class SmartCrop
{
    /// <summary>The Settings toggle (Features.SmartCrop), copied here so the export services can read it.</summary>
    public static bool Enabled { get; set; } = true;

    private const int Width = 128;
    private const double Rate = 4;          // samples a second
    private const double MaxMinutes = 15;   // longer exports keep the middle slice

    /// <summary>Where the slice's middle should be (0 = left edge, 1 = right edge), a sample every quarter second.</summary>
    public sealed class CropPath
    {
        internal CropPath(double[] centers) => Centers = centers;
        public double[] Centers { get; }

        /// <summary>The slice's middle at <paramref name="seconds"/> from the start of the analysed part.</summary>
        public double At(double seconds)
        {
            if (Centers.Length == 0) return 0.5;
            var k = seconds * Rate;
            if (k <= 0) return Centers[0];
            if (k >= Centers.Length - 1) return Centers[^1];
            var i = (int)k;
            return Centers[i] + (Centers[i + 1] - Centers[i]) * (k - i);
        }
    }

    /// <summary>
    /// Looks at <paramref name="fromMs"/>..<paramref name="toMs"/> of the source. Null when it's off, too long,
    /// unreadable, or the middle slice is best anyway.
    /// </summary>
    public static async Task<CropPath?> AnalyzeAsync(string source, long fromMs, long toMs, double sliceFraction, CancellationToken ct)
    {
        if (!Enabled || ToolLocator.Ffmpeg is not { } ffmpeg || toMs - fromMs < 1000 || sliceFraction >= 0.95) return null;
        if (toMs - fromMs > MaxMinutes * 60_000) return null;
        var raw = Path.Combine(Path.GetTempPath(), $"cnema-crop-{Guid.NewGuid():N}.gray");
        try
        {
            var args = new List<string>
            {
                "-hide_banner", "-nostats", "-v", "error", "-threads", "2",
                "-ss", S(fromMs), "-t", S(toMs - fromMs), "-an", "-sn", "-dn", "-i", source,
                "-vf", $"fps={Rate.ToString(CultureInfo.InvariantCulture)},scale={Width}:-2:flags=area,format=gray",
                "-f", "rawvideo", "-y", raw,
            };
            var r = await ProcessRunner.RunAsync(ffmpeg, args, ct: ct, background: true);
            if (r.ExitCode != 0 || !File.Exists(raw)) return null;
            var info = await MediaProbe.InfoAsync(source, ct);
            if (info is null || info.Width <= 0 || info.Height <= 0) return null;
            var height = (int)Math.Round(Width * (double)info.Height / info.Width / 2) * 2;
            var bytes = await File.ReadAllBytesAsync(raw, ct);
            return Plan(bytes, Width, height, sliceFraction);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Warn($"Smart crop: {ex.Message}");
            return null;
        }
        finally
        {
            try { File.Delete(raw); } catch { /* temp */ }
        }
    }

    /// <summary>The path from raw grey frames. Internal for tests.</summary>
    internal static CropPath? Plan(byte[] bytes, int w, int h, double slice)
    {
        var frameSize = w * h;
        if (frameSize <= 0) return null;
        var n = bytes.Length / frameSize;
        if (n < 3) return null;
        var half = slice / 2;

        // Per-column motion for each step, with the even part (camera turns, fades) taken out.
        var energy = new double[n][];
        energy[0] = new double[w];
        var col = new double[w];
        for (var f = 1; f < n; f++)
        {
            Array.Clear(col);
            int a = (f - 1) * frameSize, b = f * frameSize;
            for (var y = 0; y < h; y++)
            {
                var row = y * w;
                for (var x = 0; x < w; x++)
                {
                    var d = Math.Abs(bytes[b + row + x] - bytes[a + row + x]);
                    if (d > 10) col[x] += d; // sensor noise and compression shimmer don't count
                }
            }
            var sorted = (double[])col.Clone();
            Array.Sort(sorted);
            var even = sorted[w / 2];
            var e = new double[w];
            for (var x = 0; x < w; x++) e[x] = Math.Max(0, col[x] - even * 1.15);
            energy[f] = e;
        }
        energy[0] = energy[1];

        // A second of motion around each sample, so one flicker doesn't pull the slice.
        var raw = new double[n];
        var sure = new double[n];
        var window = (int)Rate;
        var total = new double[w];
        var scale = 255.0 * h * 0.02; // 2 % of a column changing fully is "something"
        for (var f = 0; f < n; f++)
        {
            Array.Clear(total);
            for (var k = Math.Max(0, f - window / 2); k <= Math.Min(n - 1, f + window / 2); k++)
                for (var x = 0; x < w; x++) total[x] += energy[k][x];
            double sum = 0, moment = 0;
            for (var x = 0; x < w; x++) { sum += total[x]; moment += total[x] * (x + 0.5); }
            if (sum <= 0) { raw[f] = 0.5; sure[f] = 0; continue; }
            var c = moment / sum / w;
            // How bunched up the motion is: spread over the whole picture means "no one place".
            double spread = 0;
            for (var x = 0; x < w; x++) spread += total[x] * Math.Abs((x + 0.5) / w - c);
            spread /= sum;
            var strength = Math.Clamp(sum / (scale * (window + 1)), 0, 1);
            var focus = Math.Clamp(1 - spread / 0.25, 0, 1); // 0.25 ~ even over the picture
            raw[f] = c;
            sure[f] = strength * focus;
        }
        if (sure.Average() < 0.08) return null; // nothing ever stands out: the middle slice is the right one

        // Lean towards the middle when unsure, then smooth (both ways, so it doesn't lag behind).
        var target = new double[n];
        for (var f = 0; f < n; f++) target[f] = 0.5 + (raw[f] - 0.5) * Math.Min(1, sure[f] * 1.6);
        target = Gaussian(target, sigma: Rate * 0.5);

        // Hold still until the action nears the slice's edge, then glide at most a third of the picture a second.
        var path = new double[n];
        var pos = Math.Clamp(target[0], half, 1 - half);
        var dead = slice * 0.12;
        var maxStep = 0.33 / Rate;
        for (var f = 0; f < n; f++)
        {
            var want = Math.Clamp(target[f], half, 1 - half);
            var off = want - pos;
            if (Math.Abs(off) > dead) pos += Math.Clamp(off - Math.Sign(off) * dead, -maxStep, maxStep);
            path[f] = Math.Clamp(pos, half, 1 - half);
        }
        path = Gaussian(path, sigma: Rate * 0.35);
        for (var f = 0; f < n; f++) path[f] = Math.Clamp(path[f], half, 1 - half);
        return new CropPath(path);
    }

    private static double[] Gaussian(double[] v, double sigma)
    {
        var r = (int)Math.Ceiling(sigma * 2.5);
        var k = new double[r * 2 + 1];
        for (var i = -r; i <= r; i++) k[i + r] = Math.Exp(-i * i / (2 * sigma * sigma));
        var o = new double[v.Length];
        for (var i = 0; i < v.Length; i++)
        {
            double s = 0, wsum = 0;
            for (var j = -r; j <= r; j++)
            {
                var t = Math.Clamp(i + j, 0, v.Length - 1);
                s += v[t] * k[j + r];
                wsum += k[j + r];
            }
            o[i] = s / wsum;
        }
        return o;
    }

    /// <summary>
    /// A sendcmd script that moves the crop named crop@sc: one line per output frame step (30 a second),
    /// <paramref name="sourceAt"/> giving the analysed-part time (seconds) for an output time, or null in a gap.
    /// </summary>
    public static string Commands(CropPath path, double outputSeconds, int sourceWidth, int cropWidth, Func<double, double?> sourceAt)
    {
        var sb = new StringBuilder();
        var last = -1;
        for (var t = 0.0; t <= outputSeconds + 0.05; t += 1.0 / 30)
        {
            if (sourceAt(t) is not { } s) continue;
            var x = (int)Math.Round(path.At(s) * sourceWidth - cropWidth / 2.0);
            x = Math.Clamp(x, 0, Math.Max(0, sourceWidth - cropWidth));
            if (x == last) continue;
            last = x;
            sb.Append(t.ToString("0.000", CultureInfo.InvariantCulture)).Append(" crop@sc x ").Append(x.ToString(CultureInfo.InvariantCulture)).Append(";\n");
        }
        return sb.ToString();
    }

    /// <summary>The first x, for the crop filter's own starting value.</summary>
    public static int StartX(CropPath path, int sourceWidth, int cropWidth) =>
        Math.Clamp((int)Math.Round(path.At(0) * sourceWidth - cropWidth / 2.0), 0, Math.Max(0, sourceWidth - cropWidth));

    private static string S(long ms) => (ms / 1000.0).ToString("0.000", CultureInfo.InvariantCulture);
}
