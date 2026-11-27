using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Cnema.Controls;

/// <summary>
/// Mochi's body in 3D with light anime-style cel shading. No 3D engine, shader or GPU feature: the body is small,
/// so it is shaded straight into a bitmap on the CPU (a few thousand pixels a frame) and drawn like a picture.
/// The shape is the flat Mochi's (a round dome on top, a soft flat base, each edge point on its spring) given
/// depth: for every pixel the front of the blob is worked out directly (|x|ⁿ + |y|ⁿ + |z|ⁿ = rⁿ, with the spring
/// wobble as r), so the outline matches the 2D Mochi and the eyes, hats and props still sit where they always did.
/// Shading: two flat tones (the body violet and a cooler shadow violet) split by a crisp terminator, a pale
/// lavender bounce light along the bottom, one small white highlight, and a thin darker ink line round the edge.
/// Looking about turns the light over the body as if it rotates; drops are little shaded spheres.
/// </summary>
internal sealed class Mochi3D
{
    /// <summary>The 3D look is on (Settings › Mochi › 3D Mochi). Off draws the original flat Mochi.</summary>
    public static bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            _enabled = value;
            Changed?.Invoke();
        }
    }
    private static bool _enabled = true;

    /// <summary>Raised when <see cref="Enabled"/> changes, so a still Mochi redraws.</summary>
    public static event Action? Changed;

    /// <summary>What one frame needs. Wobble: the edge springs' offsets (fractions of the radius), evenly round.</summary>
    public readonly record struct Frame(Point Center, double Rx, double Ry, double Lean, double Yaw,
        double[] Wobble, bool Still, Color Body, IReadOnlyList<(Point At, double R)> Drops);

    private const int Table = 256;
    private readonly double[] _wob = new double[Table + 1];
    private WriteableBitmap? _bitmap;
    private int[] _pixels = [];
    private Color? _toneFor;
    private (double R, double G, double B)[] _rgb = new (double, double, double)[4];

    /// <summary>Draws the body. Returns false when 3D is off (draw the flat body instead).</summary>
    public bool Draw(DrawingContext ctx, Frame f, double scale)
    {
        if (!Enabled) return false;
        // The area the body (and its drops) can cover, in control units.
        var left = f.Center.X - f.Rx * 1.45 - Math.Abs(f.Lean);
        var right = f.Center.X + f.Rx * 1.45 + Math.Abs(f.Lean);
        var top = f.Center.Y - f.Ry * 1.4;
        var bottom = f.Center.Y + f.Ry * 1.25;
        foreach (var (at, r) in f.Drops)
        {
            left = Math.Min(left, at.X - r - 1); right = Math.Max(right, at.X + r + 1);
            top = Math.Min(top, at.Y - r - 1); bottom = Math.Max(bottom, at.Y + r + 1);
        }
        left = Math.Floor(left * scale) / scale;
        top = Math.Floor(top * scale) / scale;
        var pw = Math.Max(1, (int)Math.Ceiling((right - left) * scale));
        var ph = Math.Max(1, (int)Math.Ceiling((bottom - top) * scale));
        if (pw * ph > 1_600_000) return false; // a huge Mochi: the flat one is cheaper

        if (_bitmap is null || _bitmap.PixelSize.Width != pw || _bitmap.PixelSize.Height != ph)
        {
            _bitmap?.Dispose();
            _key = 0;
            _bitmap = new WriteableBitmap(new PixelSize(pw, ph), new Vector(96 * scale, 96 * scale), PixelFormat.Bgra8888, AlphaFormat.Premul);
            _pixels = new int[pw * ph];
        }
        // A still Mochi (Reduced motion, or a small one that isn't moving) is shaded once.
        var key = HashCode.Combine(HashCode.Combine(f.Center, f.Rx, f.Ry, f.Lean, f.Yaw, f.Body, left, top), scale, pw, ph,
            f.Still ? 0 : Wobble(f.Wobble), f.Drops.Count);
        if (key != _key || f.Drops.Count > 0)
        {
            _key = key;
            Shade(f, left, top, 1 / scale, pw, ph);
            using var fb = _bitmap.Lock();
            for (var y = 0; y < ph; y++)
                System.Runtime.InteropServices.Marshal.Copy(_pixels, y * pw, fb.Address + y * fb.RowBytes, pw);
        }
        ctx.DrawImage(_bitmap, new Rect(0, 0, pw, ph), new Rect(left, top, pw / scale, ph / scale));
        return true;
    }

    /// <summary>
    /// The slime silhouette: how much wider than the round blob it is at a height (−1 top … 1 bottom). The dome stays
    /// fully round; only the base spreads out where it sits, so it looks heavy and grounded, pooling on the floor.
    /// </summary>
    public static double SlimeWidth(double uy) => 1 + 0.15 * SmoothStep(-0.95, 0.75, uy);

    private int _key;
    private Coarse[] _coarse = [];

    private readonly record struct Coarse(double Sd, (double X, double Y, double Z) N);

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;

    private static int Wobble(double[] d)
    {
        var h = new HashCode();
        foreach (var v in d) h.Add(Math.Round(v, 4));
        return h.ToHashCode();
    }

    private void Shade(Frame f, double left, double top, double px, int pw, int ph)
    {
        // The wobble round the edge as a lookup table (smooth: the springs pull on their neighbours).
        var n = f.Wobble.Length;
        for (var i = 0; i <= Table; i++)
        {
            var a = i * (double)n / Table;
            var k = (int)a % n;
            var frac = a - Math.Floor(a);
            var d0 = f.Still ? 0 : f.Wobble[k];
            var d1 = f.Still ? 0 : f.Wobble[(k + 1) % n];
            // Smoothstep between neighbours keeps the outline free of corners.
            var s = frac * frac * (3 - 2 * frac);
            _wob[i] = 1 + d0 + (d1 - d0) * s;
        }
        if (_toneFor != f.Body)
        {
            _toneFor = f.Body;
            var (shade, bounce, line) = Tones(f.Body);
            _rgb = [Rgb(f.Body), Rgb(shade), Rgb(bounce), Rgb(line)];
        }

        var depth = f.Rx * 0.82;
        var ink = Math.Clamp(f.Rx * 0.035, 0.6, 3.0) / px; // in pixels
        var cy = Math.Cos(f.Yaw);
        var sy = Math.Sin(f.Yaw);
        var drops = f.Drops.Count == 0 ? [] : f.Drops.ToArray();
        var light = new Light(Norm(-0.62, -0.7, 0.42), Norm(-0.58, -0.68, 0.62), Norm(0.3, 0.95, 0.1), cy, sy, Norm(-0.62, -0.42, 0.66));

        // A coarse pass first (every third pixel): deep inside the body the surface is smooth, so normals there are
        // interpolated from it; only pixels near the edge get their own evaluation. About a quarter of the work.
        const int C = 3;
        var cw = (pw + C - 1) / C + 1;
        var ch = (ph + C - 1) / C + 1;
        if (_coarse.Length < cw * ch) _coarse = new Coarse[cw * ch];
        for (var cj = 0; cj < ch; cj++)
        {
            var y = top + (cj * C + 0.5) * px;
            for (var ci = 0; ci < cw; ci++)
            {
                var x = left + (ci * C + 0.5) * px;
                var g = Body(f, x, y, out var nrm, out var gx, out var gy);
                var sd = g / Math.Max(Math.Sqrt(gx * gx + gy * gy) * px, 1e-9);
                _coarse[cj * cw + ci] = new Coarse(sd, nrm);
            }
        }
        var safe = ink * 1.5 + C * 1.5; // this far inside, no edge, ink line or anti-aliasing can be near

        for (var j = 0; j < ph; j++)
        {
            var y = top + (j + 0.5) * px;
            var cj = j / C;
            var fy = (j - cj * C) / (double)C;
            for (var i = 0; i < pw; i++)
            {
                var x = left + (i + 0.5) * px;
                double r = 0, gg = 0, b = 0, alpha = 0;
                var ci = i / C;
                var c00 = _coarse[cj * cw + ci];
                var c10 = _coarse[cj * cw + ci + 1];
                var c01 = _coarse[(cj + 1) * cw + ci];
                var c11 = _coarse[(cj + 1) * cw + ci + 1];
                if (c00.Sd < -safe && c10.Sd < -safe && c01.Sd < -safe && c11.Sd < -safe && drops.Length == 0)
                {
                    _pixels[j * pw + i] = 0; // well outside
                    continue;
                }
                if (c00.Sd > safe && c10.Sd > safe && c01.Sd > safe && c11.Sd > safe)
                {
                    var fx = (i - ci * C) / (double)C;
                    var nrm = Norm(
                        Lerp(Lerp(c00.N.X, c10.N.X, fx), Lerp(c01.N.X, c11.N.X, fx), fy),
                        Lerp(Lerp(c00.N.Y, c10.N.Y, fx), Lerp(c01.N.Y, c11.N.Y, fx), fy),
                        Lerp(Lerp(c00.N.Z, c10.N.Z, fx), Lerp(c01.N.Z, c11.N.Z, fx), fy));
                    (r, gg, b) = ShadeAt(nrm, 0, light);
                    alpha = 1;
                }
                else
                {
                    var g = Body(f, x, y, out var nrm, out var gx, out var gy);
                    if (g > -0.25)
                    {
                        var sd = g / Math.Max(Math.Sqrt(gx * gx + gy * gy) * px, 1e-9); // pixels inside the edge
                        if (sd > -0.5)
                        {
                            var edge = 1 - SmoothStep(ink * 0.5, ink * 1.5, sd);
                            (r, gg, b) = ShadeAt(nrm, edge, light);
                            alpha = Math.Clamp(sd + 0.5, 0, 1);
                        }
                    }
                }
                // Drops in front of the body (they fly out of it, so they're drawn over it).
                foreach (var (at, rad) in drops)
                {
                    var qx = (x - at.X) / rad;
                    var qy = (y - at.Y) / rad;
                    var q2 = qx * qx + qy * qy;
                    if (q2 >= 1.2) continue;
                    var dpx = (1 - Math.Sqrt(q2)) * rad / px;
                    var cover = Math.Clamp(dpx + 0.5, 0, 1);
                    if (cover <= 0) continue;
                    var edge = 1 - SmoothStep(ink * 0.35, ink * 1.1, dpx);
                    var (dr, dg, db) = ShadeAt(Norm(qx, qy, Math.Sqrt(Math.Max(0, 1 - q2))), edge, light);
                    r = dr * cover + r * (1 - cover);
                    gg = dg * cover + gg * (1 - cover);
                    b = db * cover + b * (1 - cover);
                    alpha = cover + alpha * (1 - cover);
                }
                _pixels[j * pw + i] = alpha <= 0 ? 0 : Pack(r, gg, b, alpha);
            }
        }
    }

    /// <summary>
    /// Inside measure of the body at a point: &gt; 0 inside (about 0..1), with its gradient (per control unit) and
    /// the surface normal there, both worked out analytically so each pixel costs one evaluation.
    /// </summary>
    private double Body(in Frame f, double x, double y, out (double X, double Y, double Z) normal, out double gx, out double gy)
    {
        var qx = x - f.Center.X;
        var qy = y - f.Center.Y;
        // The top sways more than the bottom (the same shear as the flat outline).
        var h = Math.Clamp((f.Ry - qy) / (2 * f.Ry), 0, 1);
        qx -= f.Lean * h * h;
        var shear = h is > 0 and < 1 ? f.Lean * h / f.Ry : 0; // d(qx)/dy
        var uy = qy / f.Ry;
        var width = SlimeWidth(uy);
        var ux = qx / f.Rx / width;
        if (ux * ux + uy * uy > 1.75)
        {
            normal = (0, 0, 1); gx = gy = 0;
            return -1;
        }
        var a = (double)MathF.Atan2((float)uy, (float)ux);
        if (a < 0) a += 2 * Math.PI;
        var t = a / (2 * Math.PI) * Table;
        var k = Math.Min((int)t, Table - 1);
        var r = _wob[k] + (_wob[k + 1] - _wob[k]) * (t - k);
        // A round dome on top easing into a flatter base where it sits (the flat outline's squircle,
        // exponent 2 / 0.78 below the middle), blended so the shading has no seam.
        double n, rn, px, py, dux, duy; // px = |ux|^n, dux = d(|ux|^n)/dux
        if (uy <= -0.2)
        {
            n = 2; rn = r * r; px = ux * ux; py = uy * uy; dux = 2 * ux; duy = 2 * uy;
        }
        else
        {
            n = 2 + 0.78 * SmoothStep(-0.2, 0.35, uy); // 2 / 0.72: the flat outline's broad base
            // Single precision is plenty here and twice as quick.
            var nf = (float)n;
            rn = MathF.Pow((float)r, nf);
            var ax = Math.Abs(ux);
            var ay = Math.Abs(uy);
            px = MathF.Pow((float)ax, nf);
            py = MathF.Pow((float)ay, nf);
            dux = ax > 1e-9 ? n * px / ux : 0;
            duy = ay > 1e-9 ? n * py / uy : 0;
        }
        var g = rn - px - py;
        // Gradient in control units (the shear tilts it a little with the sway).
        var dgx = -dux / (f.Rx * width);
        var dgy = -duy / f.Ry - dux / f.Rx * -shear;
        gx = dgx / rn;
        gy = dgy / rn;
        if (g > 0)
        {
            // Front depth z (|z|ⁿ = g) and the normal ∝ (∂F/∂x, ∂F/∂y, ∂F/∂z) in pixels.
            var z = n == 2 ? Math.Sqrt(g) : MathF.Pow((float)g, 1 / (float)n);
            var dz = n * g / Math.Max(z, 1e-9) / (f.Rx * 0.82);
            normal = Norm(-dgx, -dgy, dz);
        }
        else normal = Norm(-dgx, -dgy, 0.05);
        return g / rn;
    }

    private readonly record struct Light((double X, double Y, double Z) Key, (double X, double Y, double Z) Half,
        (double X, double Y, double Z) Bounce, double Cos, double Sin, (double X, double Y, double Z) Glint);

    private (double, double, double) ShadeAt((double X, double Y, double Z) n, double edge, in Light l)
    {
        // Turning: the normal rotates round the vertical axis.
        var nx = l.Cos * n.X + l.Sin * n.Z;
        var nz = -l.Sin * n.X + l.Cos * n.Z;
        var ny = n.Y;
        var ndl = nx * l.Key.X + ny * l.Key.Y + nz * l.Key.Z;
        var t = SmoothStep(0.16, 0.22, ndl); // two flat tones, crisp but anti-aliased
        var (lr, lg, lb) = _rgb[0];
        var (sr, sg, sb) = _rgb[1];
        var r = sr + (lr - sr) * t;
        var g = sg + (lg - sg) * t;
        var b = sb + (lb - sb) * t;
        // Bounce light along the bottom edge, only in the shadow tone.
        var bo = SmoothStep(0.55, 0.68, nx * l.Bounce.X + ny * l.Bounce.Y + nz * l.Bounce.Z) * (1 - t) * 0.75;
        var (br, bg, bb) = _rgb[2];
        r += (br - r) * bo; g += (bg - g) * bo; b += (bb - b) * bo;
        // Gel: light shining through lights up the lower rim, and the middle reads deeper, so it has a core.
        var rim = SmoothStep(0.5, 0.8, 1 - nz) * SmoothStep(-0.15, 0.55, ny) * 0.6;
        r += (br - r) * rim; g += (bg - g) * rim; b += (bb - b) * rim;
        var core = SmoothStep(0.55, 0.95, nz) * SmoothStep(-0.5, 0.3, ny) * 0.32;
        r += (sr - r) * core; g += (sg - g) * core; b += (sb - b) * core;
        // The anime highlight: one big soft spot up on the dome.
        var sp = SmoothStep(0.93, 0.975, nx * l.Half.X + ny * l.Half.Y + nz * l.Half.Z) * 0.85;
        // A second, smaller glint just below it: wet, like gel.
        var sp2 = SmoothStep(0.986, 0.991, nx * l.Glint.X + ny * l.Glint.Y + nz * l.Glint.Z) * 0.75;
        sp = Math.Max(sp, sp2);
        r += (1 - r) * sp; g += (1 - g) * sp; b += (1 - b) * sp;
        // The ink line round the edge.
        var (ir, ig, ib) = _rgb[3];
        r += (ir - r) * edge; g += (ig - g) * edge; b += (ib - b) * edge;
        return (r, g, b);
    }

    /// <summary>Tones from the body colour: a cooler, darker shadow; a pale bounce; a deep ink line.</summary>
    internal static (Color Shade, Color Bounce, Color Line) Tones(Color body)
    {
        var hsl = body.ToHsl();
        var shade = HslColor.ToRgb(hsl.H + 10, hsl.S * 0.62, hsl.L * 0.74, 1);
        var bounce = HslColor.ToRgb(hsl.H - 10, hsl.S * 0.7, Math.Min(0.84, hsl.L * 1.08), 1);
        var line = HslColor.ToRgb(hsl.H + 10, hsl.S * 0.55, hsl.L * 0.42, 1);
        return (shade, bounce, line);
    }

    private static (double, double, double) Rgb(Color c) => (c.R / 255.0, c.G / 255.0, c.B / 255.0);

    private static int Pack(double r, double g, double b, double a)
    {
        static int B(double v) => v <= 0 ? 0 : v >= 1 ? 255 : (int)(v * 255 + 0.5);
        return (B(a) << 24) | (B(r * a) << 16) | (B(g * a) << 8) | B(b * a);
    }

    private static (double, double, double) Norm(double x, double y, double z)
    {
        var l = Math.Sqrt(x * x + y * y + z * z);
        return l < 1e-9 ? (0, 0, 1) : (x / l, y / l, z / l);
    }

    private static double SmoothStep(double a, double b, double x)
    {
        var t = Math.Clamp((x - a) / (b - a), 0, 1);
        return t * t * (3 - 2 * t);
    }
}
