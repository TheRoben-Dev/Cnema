using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace Cnema.Controls;

/// <summary>
/// Savefile's mascot: a flat violet blob with white eyes and no mouth. Its smile is in the eyes: they sit
/// as happy crescents, blink now and then, and turn into ^ ^ when it has something to say (<see cref="Cheer"/>).
/// It breathes with a gentle squash and stretch. Drawn in code, so it costs one small redraw per frame while
/// on screen, nothing when it isn't, and it holds still with Reduced motion.
/// </summary>
public sealed class BlobMascot : Control
{
    public static readonly StyledProperty<IBrush?> BodyProperty = AvaloniaProperty.Register<BlobMascot, IBrush?>(nameof(Body));
    public static readonly StyledProperty<bool> StillProperty = AvaloniaProperty.Register<BlobMascot, bool>(nameof(Still));
    public static readonly StyledProperty<int> CheerTickProperty = AvaloniaProperty.Register<BlobMascot, int>(nameof(CheerTick));

    /// <summary>The body colour; the brand violet when unset.</summary>
    public IBrush? Body { get => GetValue(BodyProperty); set => SetValue(BodyProperty, value); }
    /// <summary>No movement at all (Reduced motion): a still, smiling blob.</summary>
    public bool Still { get => GetValue(StillProperty); set => SetValue(StillProperty, value); }
    /// <summary>Bump it to make the blob hop and squint happily (a new line to say).</summary>
    public int CheerTick { get => GetValue(CheerTickProperty); set => SetValue(CheerTickProperty, value); }

    private static readonly IBrush Eye = Brushes.White;
    private readonly DateTime _start = DateTime.UtcNow;
    private readonly Random _rng = new();
    private double _nextBlink = 2.5, _cheerAt = -10;
    private bool _running;
    private DispatcherTimer? _wake;

    static BlobMascot()
    {
        AffectsRender<BlobMascot>(BodyProperty, StillProperty);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == CheerTickProperty) Cheer();
        else if (change.Property == StillProperty || change.Property == IsVisibleProperty) Kick();
    }

    /// <summary>A little hop with ^ ^ eyes.</summary>
    public void Cheer()
    {
        _cheerAt = Now;
        Kick();
    }

    private double Now => (DateTime.UtcNow - _start).TotalSeconds;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // The frame loop stops while the blob is hidden or scrolled off; this looks in now and then to restart it.
        _wake = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Kick());
        _wake.Start();
        Kick();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _wake?.Stop();
        _wake = null;
        _running = false;
    }

    private void Kick()
    {
        if (_running || Still || !IsEffectivelyVisible || TopLevel.GetTopLevel(this) is not { } top) { InvalidateVisual(); return; }
        _running = true;
        top.RequestAnimationFrame(Frame);
    }

    private void Frame(TimeSpan _)
    {
        if (Still || !IsEffectivelyVisible || TopLevel.GetTopLevel(this) is not { } top) { _running = false; InvalidateVisual(); return; }
        InvalidateVisual();
        top.RequestAnimationFrame(Frame);
    }

    public override void Render(DrawingContext ctx)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w < 4 || h < 4) return;
        var body = Body ?? (this.TryFindResource("Brand", ActualThemeVariant, out var b) && b is IBrush brand ? brand : Brushes.BlueViolet);
        var shadow = this.TryFindResource("Line", ActualThemeVariant, out var l) && l is IBrush line ? line : Brushes.Gray;

        var t = Still ? 0 : Now;
        // Breathing: wider and shorter, then narrower and taller, about every 2.6 s.
        var breathe = Still ? 0 : Math.Sin(t * 2 * Math.PI / 2.6) * 0.035;
        // A hop when it cheers: up and down in half a second, landing with a squash.
        var since = t - _cheerAt;
        var hop = !Still && since is >= 0 and < 0.5 ? Math.Sin(since / 0.5 * Math.PI) : 0;
        var land = !Still && since is >= 0.5 and < 0.75 ? Math.Sin((since - 0.5) / 0.25 * Math.PI) * 0.08 : 0;
        var happy = !Still && since is >= 0 and < 1.8;

        var ground = h * 0.94;
        var rx = w * 0.44 * (1 + breathe + land);
        var ry = h * 0.42 * (1 - breathe - land + hop * 0.04);
        var cx = w / 2;
        var lift = hop * h * 0.10;
        var cy = ground - ry - lift;

        // A flat shadow that shrinks while it's in the air.
        var sw = rx * (0.9 - hop * 0.25);
        ctx.DrawEllipse(shadow, null, new Point(cx, ground), sw, h * 0.035);

        // The body: a soft squircle, a little flatter at the bottom where it sits.
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            const int n = 72;
            for (var i = 0; i <= n; i++)
            {
                var a = i * 2 * Math.PI / n;
                var c = Math.Cos(a);
                var s = Math.Sin(a);
                var ex = s > 0 ? 2 / 3.6 : 1.0; // a round dome on top, a soft flat base where it sits
                var wobble = Still ? 0 : 0.012 * Math.Sin(3 * a + t * 1.7);
                var px = cx + rx * (1 + wobble) * Math.Sign(c) * Math.Pow(Math.Abs(c), ex);
                var py = cy + ry * (1 + wobble) * Math.Sign(s) * Math.Pow(Math.Abs(s), ex);
                if (i == 0) g.BeginFigure(new Point(px, py), true);
                else g.LineTo(new Point(px, py));
            }
            g.EndFigure(true);
        }
        ctx.DrawGeometry(body, null, geo);

        // Eyes, looking about a little.
        var look = Still ? 0 : Math.Sin(t * 0.45) * w * 0.025;
        var eyeY = cy - ry * 0.05;
        var gap = rx * 0.38;
        var ew = w * 0.09;
        var eh = h * 0.14;

        if (happy)
        {
            // ^ ^ : two white arcs.
            var pen = new Pen(Eye, Math.Max(1.5, w * 0.055), lineCap: PenLineCap.Round);
            foreach (var side in new[] { -1, 1 })
            {
                var ex0 = cx + side * gap + look;
                var arc = new StreamGeometry();
                using (var g = arc.Open())
                {
                    g.BeginFigure(new Point(ex0 - ew, eyeY + eh * 0.25), false);
                    g.QuadraticBezierTo(new Point(ex0, eyeY - eh * 0.9), new Point(ex0 + ew, eyeY + eh * 0.25));
                    g.EndFigure(false);
                }
                ctx.DrawGeometry(null, pen, arc);
            }
            return;
        }

        // Blinks every few seconds, 140 ms each.
        var open = 1.0;
        if (!Still)
        {
            if (t > _nextBlink + 0.14) _nextBlink = t + 2.8 + _rng.NextDouble() * 3.2;
            if (t >= _nextBlink) open = Math.Max(0.08, 1 - Math.Sin((t - _nextBlink) / 0.14 * Math.PI));
        }
        foreach (var side in new[] { -1, 1 })
        {
            var ex0 = cx + side * gap + look;
            ctx.DrawEllipse(Eye, null, new Point(ex0, eyeY), ew, eh * open);
            // Smiling eyes: the cheek rises into the bottom of each eye, leaving a happy crescent.
            if (open > 0.5)
                ctx.DrawEllipse(body, null, new Point(ex0, eyeY + eh * 1.3), ew * 1.6, eh * 0.72);
        }
    }
}
