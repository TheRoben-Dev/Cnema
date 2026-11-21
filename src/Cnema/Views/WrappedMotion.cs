using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Cnema.Controls;
using Cnema.Services;

namespace Cnema.Views;

/// <summary>The flat Savefile colours a card is drawn in.</summary>
public sealed record WrappedPalette(Color Bg, Color Shape, Color Fg, Color Big, Color Small, Color Violet);

/// <summary>
/// The Wrapped video's stat cards as motion graphics: words rise in one after another, numbers count up,
/// bars grow, names pop in on the beat, and the background shapes and the blob move with the music.
/// Drawn frame by frame at 1920×1080 layout units and piped to ffmpeg; flat colours only (no gradients).
/// </summary>
public sealed class WrappedMotionCard : Control
{
    public const double W = 1920, H = 1080;
    private static readonly FontFamily Inter = new("fonts:Inter#Inter, $Default");

    private readonly WrappedCard _card;
    private readonly WrappedPalette _p;
    private readonly double[] _beats;
    private readonly double[] _downbeats;
    private double _t;

    public WrappedMotionCard(WrappedCard card, WrappedPalette palette, double length, IReadOnlyList<double> beats, IReadOnlyList<double> downbeats)
    {
        _card = card;
        _p = palette;
        _beats = beats.ToArray();
        _downbeats = downbeats.ToArray();
        Width = W;
        Height = H;
    }

    public double Time
    {
        get => _t;
        set { _t = value; InvalidateVisual(); }
    }

    // ---------- timing helpers ----------

    private static double Clamp01(double x) => Math.Clamp(x, 0, 1);
    private static double OutCubic(double x) { x = Clamp01(x); return 1 - Math.Pow(1 - x, 3); }
    private static double OutExpo(double x) { x = Clamp01(x); return x >= 1 ? 1 : 1 - Math.Pow(2, -10 * x); }
    private static double OutBack(double x)
    {
        x = Clamp01(x);
        const double c1 = 1.70158, c3 = c1 + 1;
        return 1 + c3 * Math.Pow(x - 1, 3) + c1 * Math.Pow(x - 1, 2);
    }
    private double Phase(double start, double length) => Clamp01((_t - start) / length);

    /// <summary>A short kick after each beat (1 on the beat, gone 0.25 s later).</summary>
    private double Pulse(double[] beats, double decay = 0.22)
    {
        var p = 0.0;
        foreach (var b in beats)
        {
            if (b > _t) break;
            var d = _t - b;
            if (d < decay) p = Math.Max(p, 1 - d / decay);
        }
        return p * p;
    }

    /// <summary>The n-th beat at or after <paramref name="from"/> (or a steady fallback when there's no music).</summary>
    private double BeatAfter(double from, int n = 0)
    {
        var list = _beats.Where(b => b >= from - 1e-6).ToList();
        if (list.Count > n) return list[n];
        return from + n * 0.45;
    }

    private static int StableHash(string s)
    {
        var h = 17;
        foreach (var ch in s) h = unchecked(h * 31 + ch);
        return h & 0x7fffffff;
    }

    // ---------- drawing ----------

    private FormattedText Text(string s, double size, Color c, FontWeight weight = FontWeight.Bold) =>
        new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(Inter, FontStyle.Normal, weight), size, new SolidColorBrush(c));

    /// <summary>Draws a line of text that rises in word by word (a slight overshoot), shrunk to fit <paramref name="maxWidth"/>.</summary>
    private double Words(DrawingContext ctx, string s, double x, double y, double size, Color c, FontWeight weight, double start,
        double stagger = 0.07, double maxWidth = W - 240, double scale = 1)
    {
        var whole = Text(s, size, c, weight);
        var fit = Math.Min(1, maxWidth / Math.Max(1, whole.WidthIncludingTrailingWhitespace)) * scale;
        var words = s.Split(' ');
        var cx = 0.0;
        for (var i = 0; i < words.Length; i++)
        {
            var piece = i < words.Length - 1 ? words[i] + " " : words[i];
            var ft = Text(piece, size, c, weight);
            var k = OutBack(Phase(start + i * stagger, 0.5));
            var a = Clamp01(Phase(start + i * stagger, 0.25));
            // Big words sit on a gentle wave and carry a teal offset shadow; small ones stay straight.
            var big = size >= 120;
            var wave = big ? Math.Sin(i * 0.9 + _t * 1.4) * size * 0.035 : 0;
            using (ctx.PushOpacity(a))
            using (ctx.PushTransform(Matrix.CreateScale(fit, fit) * Matrix.CreateTranslation(x + cx * fit, y + wave + (1 - k) * size * 0.6)))
            {
                if (big)
                {
                    var shade = Text(piece, size, Teal, weight);
                    ctx.DrawText(shade, new Point(size * 0.045, size * 0.045));
                }
                ctx.DrawText(ft, new Point(0, 0));
            }
            cx += ft.WidthIncludingTrailingWhitespace;
        }
        return whole.Height * fit;
    }

    /// <summary>A line that slides up and fades in.</summary>
    private void Line(DrawingContext ctx, string s, double x, double y, double size, Color c, FontWeight weight, double start, double opacity = 1, double maxWidth = W - 240)
    {
        if (s.Length == 0) return;
        var ft = Text(s, size, c, weight);
        ft.MaxTextWidth = maxWidth;
        var k = OutCubic(Phase(start, 0.45));
        using (ctx.PushOpacity(k * opacity))
            ctx.DrawText(ft, new Point(x, y + (1 - k) * 40));
    }

    /// <summary>Magenta into orange, left to right: accent bars.</summary>
    private static IBrush Sweep() => new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Magenta, 0), new GradientStop(Orange, 1) },
    };

    private static readonly Regex Number = new(@"^(\d[\d,.  ]*)(.*)$", RegexOptions.Compiled);

    /// <summary>Counts the leading number of <paramref name="s"/> up from zero ("8 moments" → "0 moments" … "8 moments").</summary>
    private string Counting(string s, double start, double length)
    {
        var m = Number.Match(s);
        if (!m.Success) return s;
        var digits = new string(m.Groups[1].Value.Where(char.IsDigit).ToArray());
        if (!long.TryParse(digits, out var n) || n == 0) return s;
        var k = OutExpo(Phase(start, length));
        if (k >= 1) return s;
        var v = (long)Math.Round(n * k);
        var shown = m.Groups[1].Value.Contains(',') || m.Groups[1].Value.Contains(' ') || m.Groups[1].Value.Trim().Contains(' ')
            ? v.ToString("N0", CultureInfo.CurrentCulture) : v.ToString(CultureInfo.InvariantCulture);
        return shown + (m.Groups[1].Value.EndsWith(' ') ? " " : "") + m.Groups[2].Value.TrimStart();
    }

    // ---------- the look ----------

    private static readonly Color Magenta = Color.Parse("#FF3FC5"), Orange = Color.Parse("#FFA54F"), Blue = Color.Parse("#3B33FF"), Teal = Color.Parse("#3FE6D6");

    private static Color Mix(Color a, Color b, double k) =>
        Color.FromArgb((byte)(a.A + (b.A - a.A) * k), (byte)(a.R + (b.R - a.R) * k), (byte)(a.G + (b.G - a.G) * k), (byte)(a.B + (b.B - a.B) * k));
    private static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

    /// <summary>Film grain: a fixed field of light and dark specks, shown from another spot every frame so it lives.</summary>
    private static readonly Lazy<WriteableBitmap> Grain = new(() =>
    {
        const int gw = 1280, gh = 720;
        var bmp = new WriteableBitmap(new PixelSize(gw, gh), new Vector(96, 96), Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Premul);
        using var fb = bmp.Lock();
        var rng = new Random(7);
        var row = new byte[gw * 4];
        for (var y = 0; y < gh; y++)
        {
            for (var x = 0; x < gw; x++)
            {
                var light = rng.Next(2) == 0;
                var a = (byte)rng.Next(0, 46);
                var v = (byte)(light ? a : 0); // premultiplied: white specks or black specks
                row[x * 4] = v; row[x * 4 + 1] = v; row[x * 4 + 2] = v; row[x * 4 + 3] = a;
            }
            System.Runtime.InteropServices.Marshal.Copy(row, 0, fb.Address + y * fb.RowBytes, row.Length);
        }
        return bmp;
    });

    /// <summary>
    /// The background: deep violet with big soft blends of violet, magenta, electric blue and orange drifting through
    /// it, breathing on the bar, under a layer of grain (flat, but never one colour).
    /// </summary>
    private void Backdrop(DrawingContext ctx, int seed)
    {
        ctx.FillRectangle(new SolidColorBrush(_p.Bg), new Rect(0, 0, W, H));
        var rng = new Random(seed);
        var bar = Pulse(_downbeats.Length > 0 ? _downbeats : _beats, 0.4);
        var colors = new[] { _p.Violet, Magenta, Blue, Orange, _p.Violet };
        var shift = rng.Next(colors.Length);
        for (var i = 0; i < 5; i++)
        {
            var c = colors[(i + shift) % colors.Length];
            var bx = rng.NextDouble() * W;
            var by = rng.NextDouble() * H;
            var r = 620 + rng.NextDouble() * 420;
            var sp = 0.08 + rng.NextDouble() * 0.1;
            var x = bx + Math.Sin(_t * sp + i * 1.7) * 260;
            var y = by + Math.Cos(_t * sp * 0.9 + i * 0.6) * 160;
            var rr = r * (1 + 0.05 * bar) * (0.85 + 0.15 * OutCubic(Phase(0.04 * i, 1.2)));
            var brush = new RadialGradientBrush
            {
                GradientStops =
                {
                    new GradientStop(WithAlpha(c, 235), 0), new GradientStop(WithAlpha(c, 140), 0.42), new GradientStop(WithAlpha(c, 0), 1),
                },
            };
            ctx.DrawEllipse(brush, null, new Point(x, y), rr, rr * 0.78);
        }
        // The grain, chunky like printed powder, from another spot every frame.
        var frame = (int)Math.Round(_t * 30);
        var g = Grain.Value;
        var ox = (frame * 7919) % 320;
        var oy = (frame * 104729) % 180;
        ctx.DrawImage(g, new Rect(ox, oy, 960, 540), new Rect(0, 0, W, H));
        if (bar > 0.01) ctx.FillRectangle(new SolidColorBrush(Color.FromArgb((byte)(26 * bar), 255, 255, 255)), new Rect(0, 0, W, H));
    }

    /// <summary>A glossy pearl: lit from the top left, a sharp highlight, a soft shadow under it.</summary>
    private static void Pearl(DrawingContext ctx, Point c, double r, Color color)
    {
        ctx.DrawEllipse(new SolidColorBrush(Color.FromArgb(55, 10, 0, 30)), null, new Point(c.X + r * 0.18, c.Y + r * 0.32), r * 0.95, r * 0.9);
        var body = new RadialGradientBrush
        {
            GradientOrigin = new RelativePoint(0.32, 0.28, RelativeUnit.Relative),
            Center = new RelativePoint(0.42, 0.4, RelativeUnit.Relative),
            RadiusX = new RelativeScalar(0.65, RelativeUnit.Relative), RadiusY = new RelativeScalar(0.65, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Mix(color, Colors.White, 0.75), 0), new GradientStop(color, 0.5), new GradientStop(Mix(color, Color.FromRgb(20, 0, 40), 0.55), 1),
            },
        };
        ctx.DrawEllipse(body, null, c, r, r);
        ctx.DrawEllipse(new SolidColorBrush(Color.FromArgb(225, 255, 255, 255)), null, new Point(c.X - r * 0.36, c.Y - r * 0.42), r * 0.2, r * 0.13);
    }

    /// <summary>Glossy pearls scattered around the edges, bobbing gently and bouncing on the beat.</summary>
    private void Pearls(DrawingContext ctx, int seed)
    {
        var rng = new Random(seed ^ 0x5eed);
        var beat = Pulse(_beats, 0.25);
        var colors = new[] { _p.Violet, Magenta, Blue, Orange, Color.Parse("#F4E9FF") };
        for (var i = 0; i < 18; i++)
        {
            // Mostly along the top, the right and the bottom, leaving the text side clear.
            var edge = rng.Next(3);
            var x = edge == 1 ? W * (0.72 + rng.NextDouble() * 0.26) : rng.NextDouble() * W;
            var y = edge == 0 ? rng.NextDouble() * H * 0.2 : edge == 2 ? H * (0.82 + rng.NextDouble() * 0.16) : rng.NextDouble() * H;
            var r = 14 + Math.Pow(rng.NextDouble(), 2) * 64;
            // Never over the words: they sit on the left (on the right for the closing card).
            var textSide = _card.Kind != "outro" || x > 700 - r;
            if (textSide && y > 300 - r && y < 1010 + r) continue;
            if (_card.Kind != "outro" && x < 700 + r && y < 300 + r && y > 60 - r) continue; // the little "savefile" mark
            var at = 0.1 + rng.NextDouble() * 0.8;
            var k = OutBack(Phase(at, 0.6));
            if (k <= 0) continue;
            var bob = Math.Sin(_t * (0.7 + rng.NextDouble() * 0.5) + i) * 10 - beat * r * 0.25;
            Pearl(ctx, new Point(x, y + bob), r * k * (1 + 0.05 * beat), colors[rng.Next(colors.Length)]);
        }
    }

    public override void Render(DrawingContext ctx)
    {
        var kind = _card.Kind;
        var seed = StableHash(kind + _card.Big);
        Backdrop(ctx, seed);
        if (kind is "intro" or "outro" or "chapter" or "squad" or "gem") Pearls(ctx, seed);
        var beat = Pulse(_downbeats.Length > 0 ? _downbeats : _beats, 0.3);
        const double x = 140;

        switch (kind)
        {
            case "intro":
            {
                Line(ctx, "savefile", x, 110, 40, _p.Fg, FontWeight.SemiBold, 0.1, 0.8);
                Words(ctx, _card.Big, x, 300, 200, _p.Big, FontWeight.Bold, 0.25, 0.12, 1150);
                // "wrapped" is unveiled from the left, on the first downbeat after the title.
                var at = Math.Max(0.9, BeatAfter(0.9));
                var wipe = OutCubic(Phase(at, 0.55));
                var ft = Text(_card.Small, 200, _p.Small);
                using (ctx.PushClip(new Rect(x - 10, 520, (ft.Width + 40) * wipe, 300)))
                using (ctx.PushTransform(Matrix.CreateScale(1 + 0.02 * beat, 1 + 0.02 * beat) * Matrix.CreateTranslation(x, 540)))
                    ctx.DrawText(ft, new Point(0, 0));
                break;
            }
            case "outro":
            {
                const double ox = 820;
                Words(ctx, _card.Kicker, ox, 330, 96, _p.Big, FontWeight.Bold, 0.3, 0.09, W - ox - 120);
                Line(ctx, _card.Big, ox, 480, 56, _p.Small, FontWeight.SemiBold, 1.0);
                Line(ctx, _card.Small, ox, 570, 44, _p.Fg, FontWeight.Normal, 1.5, 0.75);
                Line(ctx, "savefile", W - 300, H - 140, 40, _p.Fg, FontWeight.SemiBold, 1.9, 0.8);
                break;
            }
            case "chapter":
            {
                // A chapter title: a small kicker, the title rising in, an accent bar wiping under it.
                Line(ctx, _card.Kicker, x, 360, 52, _p.Small, FontWeight.SemiBold, 0.1);
                var h = Words(ctx, _card.Big, x, 430, 150, _p.Big, FontWeight.Bold, 0.25, 0.1, 1200);
                var bar = OutCubic(Phase(0.7, 0.6));
                ctx.DrawRectangle(Sweep(), null, new RoundedRect(new Rect(x, 430 + h + 24, 260 * bar, 14), 7));
                break;
            }
            case "squad":
            {
                Line(ctx, _card.Kicker, x, 250, 58, _p.Fg, FontWeight.SemiBold, 0.1, 0.85);
                var names = _card.Big.Split('·', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                var details = _card.Small.Split("   ", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                // Each friend pops in on a beat of their own: a round badge with the initial, the name, the count.
                var y = 380.0;
                var size = names.Length > 3 ? 88 : 110;
                for (var i = 0; i < names.Length; i++)
                {
                    var at = BeatAfter(0.5, i * 2);
                    var k = OutBack(Phase(at, 0.45));
                    var a = Clamp01(Phase(at, 0.2));
                    var r = size * 0.48;
                    using (ctx.PushOpacity(a))
                    {
                        var cy = y + r;
                        using (ctx.PushTransform(Matrix.CreateTranslation(-(x + r), -cy) * Matrix.CreateScale(k, k) * Matrix.CreateTranslation(x + r, cy)))
                        {
                            Pearl(ctx, new Point(x + r, cy), r, i % 3 == 0 ? _p.Violet : i % 3 == 1 ? Magenta : Blue);
                            var initial = Text(names[i][..1].ToUpperInvariant(), size * 0.5, Colors.White);
                            ctx.DrawText(initial, new Point(x + r - initial.Width / 2, cy - initial.Height / 2));
                        }
                        var name = Text(names[i], size, _p.Big);
                        ctx.DrawText(name, new Point(x + r * 2 + 36 + (1 - k) * 60, y + r - name.Height / 2));
                        if (i < details.Length)
                        {
                            var d = details[i];
                            var colon = d.IndexOf(": ", StringComparison.Ordinal);
                            var ft = Text(colon >= 0 ? d[(colon + 2)..] : d, 40, _p.Small, FontWeight.Normal);
                            using (ctx.PushOpacity(Clamp01(Phase(at + 0.25, 0.3))))
                                ctx.DrawText(ft, new Point(x + r * 2 + 36 + name.Width + 36, y + r - ft.Height / 2 + 8));
                        }
                    }
                    y += size * 1.25;
                }
                break;
            }
            default:
            {
                // A kicker, the big line (counting up when it starts with a number), and a smaller line under it.
                Line(ctx, "savefile", x, 110, 40, _p.Fg, FontWeight.SemiBold, 0.05, 0.8);
                Line(ctx, _card.Kicker, x, 330, 58, _p.Fg, FontWeight.SemiBold, 0.1, 0.85);
                var big = Counting(_card.Big, 0.35, 1.3);
                var landed = Phase(0.35 + 1.3, 0.001) >= 1;
                var pop = landed ? 1 + 0.03 * beat : 1;
                Words(ctx, big, x, 420, 200, _p.Big, FontWeight.Bold, 0.3, big == _card.Big ? 0.09 : 0, W - 280, pop);
                if (kind == "game" && Number.Match(_card.Small) is { Success: true })
                {
                    // The game's share of the year, as a bar that grows.
                    var share = Regex.Match(_card.Small, @"(\d+)\s?%");
                    var f = share.Success ? int.Parse(share.Groups[1].Value, CultureInfo.InvariantCulture) / 100.0 : 0.5;
                    var grow = OutCubic(Phase(1.0, 1.1));
                    ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(46, 255, 255, 255)), null, new RoundedRect(new Rect(x, 690, W - 2 * x, 18), 9));
                    ctx.DrawRectangle(Sweep(), null, new RoundedRect(new Rect(x, 690, Math.Max(18, (W - 2 * x) * f * grow), 18), 9));
                    Line(ctx, _card.Small, x, 740, 50, _p.Small, FontWeight.Normal, 1.2, 0.9);
                }
                else
                {
                    Line(ctx, _card.Small, x, 690, 50, _p.Small, FontWeight.Normal, 1.0, 0.9);
                }
                if (_card.Quote.Length > 0)
                    Line(ctx, _card.Quote, x, kind == "game" ? 830 : 790, 40, _p.Fg, FontWeight.Normal, 1.6, 0.7);
                break;
            }
        }
    }
}

/// <summary>Draws the animated cards to video files with the bundled ffmpeg.</summary>
public static class WrappedMotion
{
    public static WrappedPalette PaletteFor(string kind)
    {
        var dark = ThemeService.Resolve(ThemeService.Schemes[0], false);
        var violet = Color.Parse(dark.Accent);
        // Every card on deep violet with colour blended through it: white type, a soft pink for the small lines.
        var deep = Color.FromRgb(0x1C, 0x0A, 0x3C);
        return new(deep, violet, Colors.White, Colors.White, Color.Parse("#FFD6F4"), violet);
    }

    /// <summary>Mochi as a drop of glossy liquid: lighter where the light hits, deep at the edges.</summary>
    private static IBrush GlossyBody(Color violet) => new RadialGradientBrush
    {
        GradientOrigin = new RelativePoint(0.35, 0.3, RelativeUnit.Relative), Center = new RelativePoint(0.45, 0.45, RelativeUnit.Relative),
        RadiusX = new RelativeScalar(0.7, RelativeUnit.Relative), RadiusY = new RelativeScalar(0.7, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Color.FromRgb((byte)(violet.R + (255 - violet.R) * 0.45), (byte)(violet.G + (255 - violet.G) * 0.3), (byte)(violet.B + (255 - violet.B) * 0.45)), 0),
            new GradientStop(violet, 0.5),
            new GradientStop(Color.FromRgb((byte)(violet.R * 0.45), (byte)(violet.G * 0.3), (byte)(violet.B * 0.6)), 1),
        },
    };

    /// <summary>The card, plus the blob on the cards that have one, laid out at 1920×1080.</summary>
    public static (Control Page, WrappedMotionCard Card, BlobMascot? Blob) Build(WrappedCard card, double length, IReadOnlyList<double> beats, IReadOnlyList<double> downbeats)
    {
        var p = PaletteFor(card.Kind);
        var canvas = new WrappedMotionCard(card, p, length, beats, downbeats);
        var root = new Grid { Width = WrappedMotionCard.W, Height = WrappedMotionCard.H };
        root.Children.Add(canvas);
        BlobMascot? blob = null;
        if (card.Kind is "intro" or "outro" or "chapter")
        {
            blob = new BlobMascot
            {
                Width = card.Kind switch { "intro" => 520, "chapter" => 360, _ => 460 },
                Height = card.Kind switch { "intro" => 470, "chapter" => 325, _ => 415 },
                Body = GlossyBody(Color.Parse(ThemeService.Resolve(ThemeService.Schemes[0], false).Accent)),
                Gloss = true,
                Shadow = new SolidColorBrush(Colors.Black, 0.35),
                HorizontalAlignment = card.Kind == "outro" ? HorizontalAlignment.Left : HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = card.Kind switch { "intro" => new Thickness(0, 0, 150, 150), "chapter" => new Thickness(0, 0, 200, 120), _ => new Thickness(170, 0, 0, 200) },
                RenderTransform = new TranslateTransform(),
            };
            root.Children.Add(blob);
        }
        root.Measure(new Size(WrappedMotionCard.W, WrappedMotionCard.H));
        root.Arrange(new Rect(0, 0, WrappedMotionCard.W, WrappedMotionCard.H));
        return (root, canvas, blob);
    }

    /// <summary>
    /// Renders one card, <paramref name="seconds"/> long, to <paramref name="output"/> (H.264, no sound).
    /// Frames are drawn on the UI thread in small batches at background priority, so the app stays responsive.
    /// </summary>
    public static async Task RenderAsync(WrappedCard card, double seconds, IReadOnlyList<double> beats, IReadOnlyList<double> downbeats,
        int width, int height, int fps, string output, CancellationToken ct)
    {
        var ffmpeg = ToolLocator.Ffmpeg ?? throw new InvalidOperationException(Loc.T("ffmpeg not found."));
        var frames = (int)Math.Round(seconds * fps);
        var psi = new ProcessStartInfo(ffmpeg)
        {
            RedirectStandardInput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (var a in new[]
                 {
                     "-hide_banner", "-v", "error", "-y", "-f", "rawvideo", "-pix_fmt", "bgra", "-s", $"{width}x{height}",
                     "-r", fps.ToString(CultureInfo.InvariantCulture), "-i", "pipe:0",
                     "-c:v", "libx264", "-preset", "veryfast", "-crf", "16", "-pix_fmt", "yuv420p", "-threads", "2", output,
                 })
            psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg didn't start.");
        try { proc.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { /* not allowed everywhere */ }
        var errors = proc.StandardError.ReadToEndAsync(ct);
        var stdin = proc.StandardInput.BaseStream;
        try
        {
            Control? page = null;
            WrappedMotionCard? canvas = null;
            BlobMascot? blobControl = null;
            RenderTargetBitmap? bitmap = null;
            var stride = width * 4;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                (page, canvas, blobControl) = Build(card, seconds, beats, downbeats);
                bitmap = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96 * width / WrappedMotionCard.W, 96 * height / WrappedMotionCard.H));
            });
            var cheered = 0;
            for (var f = 0; f < frames;)
            {
                ct.ThrowIfCancellationRequested();
                var batch = Math.Min(6, frames - f);
                var chunk = new byte[batch][];
                var first = f;
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    for (var i = 0; i < batch; i++)
                    {
                        var t = (first + i) / (double)fps;
                        canvas!.Time = t;
                        if (blobControl is { } blob)
                        {
                            // The blob drops in, then hops on downbeats.
                            var enter = Math.Clamp((t - 0.6) / 0.6, 0, 1);
                            ((TranslateTransform)blob.RenderTransform!).Y = (1 - (1 - Math.Pow(1 - enter, 3))) * 700;
                            while (cheered < downbeats.Count && downbeats[cheered] <= t)
                            {
                                if (downbeats[cheered] > 1.2 && cheered % 2 == 0) blob.Cheer();
                                cheered++;
                            }
                            blob.DriveTo(t);
                        }
                        bitmap!.Render(page!);
                        var data = new byte[stride * height];
                        unsafe
                        {
                            fixed (byte* ptr = data) bitmap.CopyPixels(new PixelRect(0, 0, width, height), (nint)ptr, data.Length, stride);
                        }
                        chunk[i] = data;
                    }
                }, DispatcherPriority.Background);
                foreach (var data in chunk) await stdin.WriteAsync(data, ct);
                f += batch;
            }
            stdin.Close();
            await proc.WaitForExitAsync(ct);
            bitmap?.Dispose();
            if (proc.ExitCode != 0 || !File.Exists(output))
                throw new InvalidOperationException(Loc.F("Couldn't draw the {0} card: {1}", card.Kind, (await errors).Trim()));
        }
        catch
        {
            try { if (!proc.HasExited) proc.Kill(); } catch { /* gone */ }
            throw;
        }
    }

    /// <summary>A caption for over a clip: bold white type on a violet tag, bottom left, on a see-through picture.</summary>
    public static void RenderCaption(string caption, int width, int height, string path)
    {
        var violet = Color.Parse(ThemeService.Resolve(ThemeService.Schemes[0], false).Accent);
        var big = caption.StartsWith('#');
        TextBlock Label(IBrush brush, double dx) => new()
        {
            Text = caption, FontSize = big ? 120 : 52, FontWeight = FontWeight.Bold, Foreground = brush,
            FontFamily = new FontFamily("fonts:Inter#Inter, $Default"),
            RenderTransform = new TranslateTransform(dx, dx),
        };
        var size = big ? 120 : 52;
        var text = new Grid { Children = { Label(new SolidColorBrush(Color.Parse("#3FE6D6")), size * 0.045), Label(Brushes.White, 0) } };
        var tag = new Border
        {
            Child = text, CornerRadius = new CornerRadius(big ? 28 : 18),
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(violet, 0), new GradientStop(Color.Parse("#FF3FC5"), 1) },
            },
            BorderBrush = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)), BorderThickness = new Thickness(2),
            Padding = big ? new Thickness(44, 18) : new Thickness(30, 14),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(110, 0, 0, 110),
        };
        var root = new Grid { Width = WrappedMotionCard.W, Height = WrappedMotionCard.H, Background = Brushes.Transparent };
        root.Children.Add(tag);
        root.Measure(new Size(WrappedMotionCard.W, WrappedMotionCard.H));
        root.Arrange(new Rect(0, 0, WrappedMotionCard.W, WrappedMotionCard.H));
        using var bitmap = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96 * width / WrappedMotionCard.W, 96 * height / WrappedMotionCard.H));
        bitmap.Render(root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        bitmap.Save(path);
    }
}
