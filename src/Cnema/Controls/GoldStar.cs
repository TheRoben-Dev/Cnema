using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace Cnema.Controls;

/// <summary>
/// A favourite star that looks like gold: a metallic fill, a glint that sweeps across it when it's turned on,
/// and now and then a quiet glint and a small sparkle while it stays a favourite. Still (Reduced motion) keeps
/// the gold and drops the movement.
/// </summary>
public sealed class GoldStar : Control
{
    public static readonly StyledProperty<bool> StillProperty = AvaloniaProperty.Register<GoldStar, bool>(nameof(Still));
    public bool Still { get => GetValue(StillProperty); set => SetValue(StillProperty, value); }

    private static readonly Geometry Star = Geometry.Parse("M12 17.27L18.18 21l-1.64-7.03L22 9.24l-7.19-.61L12 2 9.19 8.63 2 9.24l5.46 4.73L5.82 21z");

    private static readonly IBrush Gold = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0.15, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0.85, 1, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Color.Parse("#FFF4C4"), 0),
            new GradientStop(Color.Parse("#FFD166"), 0.32),
            new GradientStop(Color.Parse("#F2B33D"), 0.58),
            new GradientStop(Color.Parse("#D9952A"), 0.78),
            new GradientStop(Color.Parse("#FFCF6B"), 1),
        },
    };
    private static readonly IPen Edge = new Pen(new SolidColorBrush(Color.Parse("#B9781C")), 0.7);
    private static readonly IBrush Glint = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Color.Parse("#00FFFFFF"), 0),
            new GradientStop(Color.Parse("#E6FFFFFF"), 0.5),
            new GradientStop(Color.Parse("#00FFFFFF"), 1),
        },
    };
    private static readonly IBrush Spark = new SolidColorBrush(Color.Parse("#FFC93D"));
    private static readonly IBrush SparkCore = new SolidColorBrush(Color.Parse("#FFFDF2"));

    private const double SweepMs = 650, SparkFrom = 380, SparkMs = 520;
    private DispatcherTimer? _frame, _idle;
    private DateTime _start = DateTime.MinValue;
    private bool _big;
    private readonly Random _rng = new();

    static GoldStar() => AffectsRender<GoldStar>(StillProperty);

    /// <summary>The full shine, for when it's just been made a favourite.</summary>
    public void Shine() => Begin(true);

    private void Begin(bool big)
    {
        if (Still || VisualRoot is null) return;
        _big = big;
        _start = DateTime.UtcNow;
        _frame ??= new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) => Tick());
        _frame.Start();
    }

    private void Tick()
    {
        InvalidateVisual();
        if ((DateTime.UtcNow - _start).TotalMilliseconds > SparkFrom + SparkMs) { _frame?.Stop(); _start = DateTime.MinValue; InvalidateVisual(); }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ScheduleIdle();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _idle?.Stop();
        _frame?.Stop();
    }

    /// <summary>Every 8 to 14 seconds, while it's on screen: a quieter glint.</summary>
    private void ScheduleIdle()
    {
        if (_idle is null)
        {
            _idle = new DispatcherTimer(DispatcherPriority.Background);
            _idle.Tick += (_, _) => { if (IsEffectivelyVisible) Begin(false); ScheduleIdle(); };
        }
        _idle.Stop();
        _idle.Interval = TimeSpan.FromSeconds(8 + _rng.NextDouble() * 6);
        _idle.Start();
    }

    public override void Render(DrawingContext ctx)
    {
        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size <= 0) return;
        var s = size / 24;
        var m = Matrix.CreateScale(s, s) * Matrix.CreateTranslation((Bounds.Width - size) / 2, (Bounds.Height - size) / 2);
        using var _ = ctx.PushTransform(m);
        ctx.DrawGeometry(Gold, Edge, Star);

        if (_start == DateTime.MinValue || Still) return;
        var ms = (DateTime.UtcNow - _start).TotalMilliseconds;
        if (ms < SweepMs)
        {
            var p = Ease(ms / SweepMs);
            using (ctx.PushGeometryClip(Star))
            using (ctx.PushOpacity(_big ? 1 : 0.7))
            {
                var x = -8 + p * 40; // across the star, top left to bottom right
                var band = new Rect(-2.5, -6, 5, 36);
                using (ctx.PushTransform(Matrix.CreateRotation(Math.PI / 7) * Matrix.CreateTranslation(x, 0)))
                    ctx.FillRectangle(Glint, band);
            }
        }
        var q = (ms - SparkFrom) / SparkMs;
        if (q is > 0 and < 1)
        {
            var r = Math.Sin(Math.PI * q) * (_big ? 4.2 : 2.8);
            DrawSparkle(ctx, _big ? new Point(19.5, 4.5) : new Point(7, 8), r);
            if (_big) DrawSparkle(ctx, new Point(4, 17), r * 0.55);
        }
    }

    private static double Ease(double t) => 1 - Math.Pow(1 - Math.Clamp(t, 0, 1), 3);

    /// <summary>A four-pointed twinkle.</summary>
    private static void DrawSparkle(DrawingContext ctx, Point c, double r)
    {
        DrawTwinkle(ctx, c, r, Spark);
        DrawTwinkle(ctx, c, r * 0.5, SparkCore);
    }

    private static void DrawTwinkle(DrawingContext ctx, Point c, double r, IBrush brush)
    {
        if (r <= 0.05) return;
        var w = r * 0.28;
        var g = new StreamGeometry();
        using (var g2 = g.Open())
        {
            g2.BeginFigure(new Point(c.X, c.Y - r), true);
            g2.LineTo(new Point(c.X + w, c.Y - w));
            g2.LineTo(new Point(c.X + r, c.Y));
            g2.LineTo(new Point(c.X + w, c.Y + w));
            g2.LineTo(new Point(c.X, c.Y + r));
            g2.LineTo(new Point(c.X - w, c.Y + w));
            g2.LineTo(new Point(c.X - r, c.Y));
            g2.LineTo(new Point(c.X - w, c.Y - w));
            g2.EndFigure(true);
        }
        ctx.DrawGeometry(brush, null, g);
    }
}
