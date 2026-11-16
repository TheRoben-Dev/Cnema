using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace Cnema.Controls;

/// <summary>
/// The present the blob holds out from 1 November: a flat violet box with a white ribbon that gives a small
/// wiggle now and then. Opened, the lid leans against the side and a little card of the finished video sticks
/// out, with a shine running over it every few seconds (a thin bar while it is still being made). Everything
/// is flat shapes; nothing ticks between the wiggles and shines, and it holds still with Reduced motion.
/// </summary>
public sealed class GiftBox : Control
{
    public static readonly StyledProperty<bool> OpenedProperty = AvaloniaProperty.Register<GiftBox, bool>(nameof(Opened));
    public static readonly StyledProperty<bool> StillProperty = AvaloniaProperty.Register<GiftBox, bool>(nameof(Still));
    public static readonly StyledProperty<IImage?> PictureProperty = AvaloniaProperty.Register<GiftBox, IImage?>(nameof(Picture));
    public static readonly StyledProperty<double> ProgressProperty = AvaloniaProperty.Register<GiftBox, double>(nameof(Progress), -1);
    public static readonly StyledProperty<bool> TinselProperty = AvaloniaProperty.Register<GiftBox, bool>(nameof(Tinsel));

    /// <summary>Unwrapped: lid off, the video's card showing. Switching it on plays the unwrap.</summary>
    public bool Opened { get => GetValue(OpenedProperty); set => SetValue(OpenedProperty, value); }
    /// <summary>No movement at all (Reduced motion).</summary>
    public bool Still { get => GetValue(StillProperty); set => SetValue(StillProperty, value); }
    /// <summary>A frame of the finished video, for the card in the open box.</summary>
    public IImage? Picture { get => GetValue(PictureProperty); set => SetValue(PictureProperty, value); }
    /// <summary>0..1 while the video is being made, else below 0.</summary>
    public double Progress { get => GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }
    /// <summary>A string of tinsel beads draped over the front (Mochi dressing up for December).</summary>
    public bool Tinsel { get => GetValue(TinselProperty); set => SetValue(TinselProperty, value); }

    private const double WiggleEvery = 4.5, WiggleFor = 0.7, ShineEvery = 3.2, ShineFor = 0.9, UnwrapFor = 0.75;
    private readonly DateTime _start = DateTime.UtcNow;
    private double _openedAt = -10;
    private DispatcherTimer? _timer;
    private static readonly IBrush Ribbon = new SolidColorBrush(Color.FromArgb(240, 255, 255, 255));
    private static readonly IBrush ShineBrush = new SolidColorBrush(Color.FromArgb(120, 255, 255, 255));
    private static readonly IBrush Shade = new SolidColorBrush(Color.FromArgb(70, 0, 0, 0));
    private static readonly IBrush Light = new SolidColorBrush(Color.FromArgb(46, 255, 255, 255));

    static GiftBox()
    {
        AffectsRender<GiftBox>(OpenedProperty, StillProperty, PictureProperty, ProgressProperty, TinselProperty);
    }

    private double Now => (DateTime.UtcNow - _start).TotalSeconds;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == OpenedProperty && change.NewValue is true && IsLoaded) _openedAt = Now;
        if (change.Property == OpenedProperty || change.Property == StillProperty || change.Property == IsVisibleProperty) Schedule();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Schedule();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _timer?.Stop();
    }

    /// <summary>Ticks at frame rate only while something moves, then sleeps until the next wiggle or shine.</summary>
    private void Schedule()
    {
        _timer ??= new DispatcherTimer(DispatcherPriority.Render);
        _timer.Tick -= OnTick;
        _timer.Tick += OnTick;
        if (Still || !IsVisible || VisualRoot is null) { _timer.Stop(); InvalidateVisual(); return; }
        var t = Now;
        var every = Opened ? ShineEvery : WiggleEvery;
        var lasts = Opened ? ShineFor : WiggleFor;
        var phase = t % every;
        var moving = t - _openedAt < UnwrapFor + 0.1 || phase < lasts + 0.05;
        _timer.Interval = moving ? TimeSpan.FromMilliseconds(16) : TimeSpan.FromSeconds(every - phase);
        _timer.Start();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        InvalidateVisual();
        Schedule();
    }

    public override void Render(DrawingContext ctx)
    {
        var w = Bounds.Width; var h = Bounds.Height;
        if (w < 4 || h < 4) return;
        var brand = this.TryFindResource("Brand", ActualThemeVariant, out var b) && b is IBrush br ? br : Brushes.BlueViolet;
        var line = this.TryFindResource("Line", ActualThemeVariant, out var l) && l is IBrush ln ? ln : Brushes.Gray;
        var card = this.TryFindResource("Bg2", ActualThemeVariant, out var c) && c is IBrush cb ? cb : Brushes.DimGray;
        var t = Still ? 100 : Now;
        var s = Math.Min(w, h * 1.1);
        var ox = (w - s) / 2; var oy = h - s / 1.1;
        Point P(double x, double y) => new(ox + x * s, oy + y * s / 1.1);
        Rect R(double x0, double y0, double x1, double y1) => new(P(x0, y0), P(x1, y1));

        // the shadow
        ctx.DrawEllipse(line, null, P(0.5, 0.965), s * 0.36, s * 0.03);

        if (!Opened)
        {
            // A little wiggle, like something inside wants out.
            var phase = t % WiggleEvery;
            var angle = phase < WiggleFor ? Math.Sin(phase / WiggleFor * Math.PI * 3) * (1 - phase / WiggleFor) * 0.12 : 0;
            var pivot = P(0.5, 0.94);
            using (ctx.PushTransform(Matrix.CreateTranslation(-pivot.X, -pivot.Y) * Matrix.CreateRotation(angle) * Matrix.CreateTranslation(pivot.X, pivot.Y)))
            {
                ctx.DrawRectangle(brand, null, R(0.16, 0.50, 0.84, 0.94), 4, 4);
                ctx.DrawRectangle(Ribbon, null, R(0.45, 0.50, 0.55, 0.94));
                DrawLid(ctx, brand, R(0.10, 0.38, 0.90, 0.52));
                ctx.DrawRectangle(Ribbon, null, R(0.45, 0.38, 0.55, 0.52));
                DrawBow(ctx, P(0.5, 0.38), s);
                if (Tinsel) DrawTinsel(ctx, brand, P, s, 0.53, t);
            }
            return;
        }

        // Unwrapping: the lid flies up and to the side, the card rises out of the box.
        var u = Still ? 1 : Math.Clamp((t - _openedAt) / UnwrapFor, 0, 1);
        var ease = 1 - Math.Pow(1 - u, 3);
        var rise = (1 - ease) * 0.45;

        // the lid, flying off and coming to lean against the left side, behind the box
        var lidAngle = -1.2 * ease;
        var from = P(0.5, 0.45); var to = P(0.13, 0.66);
        var at = new Point(from.X + (to.X - from.X) * ease, from.Y + (to.Y - from.Y) * ease - Math.Sin(u * Math.PI) * s * 0.3);
        var k = 1 - 0.3 * ease;
        using (ctx.PushTransform(Matrix.CreateTranslation(-from.X, -from.Y) * Matrix.CreateScale(k, k)
                                 * Matrix.CreateRotation(lidAngle) * Matrix.CreateTranslation(at.X, at.Y)))
        {
            DrawLid(ctx, brand, R(0.10, 0.38, 0.90, 0.52));
            ctx.DrawRectangle(Ribbon, null, R(0.45, 0.38, 0.55, 0.52));
            DrawBow(ctx, P(0.5, 0.38), s);
        }

        // the card, behind the front of the box
        var cardRect = R(0.13, 0.12 + rise, 0.91, 0.66 + rise);
        using (ctx.PushClip(R(0, -1, 1, 0.70)))
        {
            ctx.DrawRectangle(Ribbon, null, cardRect.Inflate(1.5), 4, 4);
            using (ctx.PushClip(new RoundedRect(cardRect, 3)))
            {
                ctx.DrawRectangle(card, null, cardRect);
                if (Picture is { } pic)
                {
                    var size = pic.Size;
                    var scale = Math.Max(cardRect.Width / size.Width, cardRect.Height / size.Height);
                    var dw = size.Width * scale; var dh = size.Height * scale;
                    ctx.DrawImage(pic, new Rect(size), new Rect(cardRect.Center.X - dw / 2, cardRect.Center.Y - dh / 2, dw, dh));
                }
                else
                {
                    // a play mark until there's a picture
                    var m = cardRect.Center; var r = cardRect.Height * 0.16;
                    var tri = new StreamGeometry();
                    using (var g = tri.Open())
                    {
                        g.BeginFigure(new Point(m.X - r * 0.6, m.Y - r), true);
                        g.LineTo(new Point(m.X + r, m.Y));
                        g.LineTo(new Point(m.X - r * 0.6, m.Y + r));
                        g.EndFigure(true);
                    }
                    ctx.DrawGeometry(brand, null, tri);
                }
                var making = Progress is >= 0 and < 1;
                if (making)
                {
                    var bar = new Rect(cardRect.X + 3, cardRect.Bottom - s * 0.2, cardRect.Width - 6, Math.Max(2, s * 0.035));
                    ctx.DrawRectangle(Shade, null, bar, 2, 2);
                    ctx.DrawRectangle(brand, null, bar.WithWidth(Math.Max(bar.Height, bar.Width * Progress)), 2, 2);
                }
                else if (!Still && u >= 1)
                {
                    // the shine: a pale band sweeping across now and then
                    var phase = t % ShineEvery;
                    if (phase < ShineFor)
                    {
                        var x = cardRect.X - cardRect.Width * 0.5 + phase / ShineFor * cardRect.Width * 2;
                        var band = new StreamGeometry();
                        using (var g = band.Open())
                        {
                            var bw = cardRect.Width * 0.22; var tilt = cardRect.Height * 0.5;
                            g.BeginFigure(new Point(x, cardRect.Bottom), true);
                            g.LineTo(new Point(x + tilt, cardRect.Top));
                            g.LineTo(new Point(x + tilt + bw, cardRect.Top));
                            g.LineTo(new Point(x + bw, cardRect.Bottom));
                            g.EndFigure(true);
                        }
                        ctx.DrawGeometry(ShineBrush, null, band);
                    }
                }
                else if (Still)
                {
                    // a still sparkle instead of the sweep
                    var sp = new Point(cardRect.Right - cardRect.Width * 0.2, cardRect.Top + cardRect.Height * 0.25);
                    DrawSparkle(ctx, sp, s * 0.06);
                }
            }
        }

        // the inside, then the front of the box
        ctx.DrawRectangle(Shade, null, R(0.18, 0.50, 0.86, 0.56));
        ctx.DrawRectangle(brand, null, R(0.18, 0.54, 0.86, 0.94), 4, 4);
        ctx.DrawRectangle(Ribbon, null, R(0.47, 0.54, 0.57, 0.94));
        if (Tinsel) DrawTinsel(ctx, brand, P, s, 0.57, t);

        // a few flecks burst out while it opens
        if (u is > 0 and < 1 && !Still)
        {
            for (var i = 0; i < 7; i++)
            {
                var a = -Math.PI / 2 + (i - 3) * 0.38;
                var d = s * (0.12 + 0.45 * ease);
                var p = new Point(P(0.5, 0.45).X + Math.Cos(a) * d, P(0.5, 0.45).Y + Math.Sin(a) * d + ease * ease * s * 0.15);
                ctx.DrawEllipse(i % 2 == 0 ? brand : Ribbon, null, p, s * 0.025 * (1 - u), s * 0.025 * (1 - u));
            }
        }
    }

    private static readonly IBrush[] Beads =
    {
        new SolidColorBrush(Color.Parse("#F5C451")), new SolidColorBrush(Color.Parse("#E5484D")),
        new SolidColorBrush(Color.FromArgb(240, 255, 255, 255)), new SolidColorBrush(Color.Parse("#3FB27F")),
    };

    /// <summary>Beads on a drooping string across the front; one twinkles brighter now and then.</summary>
    private static void DrawTinsel(DrawingContext ctx, IBrush brand, Func<double, double, Point> at, double s, double y, double t)
    {
        const int n = 11;
        var lit = (int)(t * 2.5) % n;
        for (var i = 0; i < n; i++)
        {
            var f = i / (double)(n - 1);
            var p = at(0.17 + f * 0.66, y + Math.Sin(f * Math.PI) * 0.09);
            var r = s * (i == lit ? 0.034 : 0.027);
            ctx.DrawEllipse(Beads[i % Beads.Length], null, p, r, r);
        }
    }

    private static void DrawLid(DrawingContext ctx, IBrush brand, Rect r)
    {
        ctx.DrawRectangle(brand, null, r, 4, 4);
        ctx.DrawRectangle(Light, null, r, 4, 4);
    }

    private static void DrawBow(DrawingContext ctx, Point top, double s)
    {
        var loop = s * 0.11;
        using (ctx.PushTransform(Matrix.CreateTranslation(-top.X, -top.Y) * Matrix.CreateRotation(-0.5) * Matrix.CreateTranslation(top.X - loop * 0.75, top.Y - loop * 0.35)))
            ctx.DrawEllipse(Ribbon, null, top, loop, loop * 0.55);
        using (ctx.PushTransform(Matrix.CreateTranslation(-top.X, -top.Y) * Matrix.CreateRotation(0.5) * Matrix.CreateTranslation(top.X + loop * 0.75, top.Y - loop * 0.35)))
            ctx.DrawEllipse(Ribbon, null, top, loop, loop * 0.55);
        ctx.DrawEllipse(Ribbon, null, new Point(top.X, top.Y - loop * 0.1), loop * 0.38, loop * 0.38);
    }

    private static void DrawSparkle(DrawingContext ctx, Point m, double r)
    {
        var g = new StreamGeometry();
        using (var o = g.Open())
        {
            o.BeginFigure(new Point(m.X, m.Y - r), true);
            o.LineTo(new Point(m.X + r * 0.25, m.Y - r * 0.25));
            o.LineTo(new Point(m.X + r, m.Y));
            o.LineTo(new Point(m.X + r * 0.25, m.Y + r * 0.25));
            o.LineTo(new Point(m.X, m.Y + r));
            o.LineTo(new Point(m.X - r * 0.25, m.Y + r * 0.25));
            o.LineTo(new Point(m.X - r, m.Y));
            o.LineTo(new Point(m.X - r * 0.25, m.Y - r * 0.25));
            o.EndFigure(true);
        }
        ctx.DrawGeometry(ShineBrush, null, g);
    }
}
