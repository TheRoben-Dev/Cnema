using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace Cnema.Controls;

/// <summary>
/// Savefile's mascot: a flat violet blob with white eyes and no mouth, made to feel like it's liquid.
/// Its outline is a ring of points on springs, joined by a smooth curve: ripples run around the edge, a hop
/// stretches it tall, the landing squashes it flat (its area stays the same) and it wobbles until it settles,
/// with a few drops splashing off. When the eyes dart to look somewhere the body sways after them. The smile is
/// in the eyes: happy crescents with round tips, ^ ^ when it has something to say (<see cref="Cheer"/>).
/// A few dozen points per frame while on screen, nothing when it isn't, and it holds still with Reduced motion.
/// </summary>
public sealed class BlobMascot : Control
{
    /// <summary>The shadow under the blob; the theme's Line colour when not set.</summary>
    public IBrush? Shadow { get; set; }

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
    private const int N = 30;              // points around the edge
    private const double Gravity = 7.0;    // in heights per second²

    private readonly DateTime _start = Clock();
    private readonly Random _rng = new();
    private bool _running;
    private DispatcherTimer? _wake;

    // The soft body. Edge points move in and out from their rest place (fractions of the radius).
    private readonly double[] _d = new double[N], _v = new double[N];
    private double _q, _qv;     // squash (+ taller, − wider), area kept
    private double _lean, _leanV; // the top sways sideways (fraction of the width)
    private double _y, _yv;     // height off the ground (fraction of the height)
    private double _look, _lookTo, _nextLook = 1.5;
    private double _nextBlink = 2.5, _cheerAt = -10, _last = -1;
    private readonly List<Drop> _drops = new();

    private sealed class Drop
    {
        public double X, Y, Vx, Vy, R, Age;
    }

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

    /// <summary>A hop with ^ ^ eyes: it stretches up, lands with a squash and a splash, and jiggles.</summary>
    public void Cheer()
    {
        _cheerAt = Now;
        if (!Still)
        {
            _yv = 1.25;
            _qv += 2.2; // stretch on take-off
            Ripple(_rng.NextDouble() * 2 * Math.PI, 0.5);
        }
        Kick();
    }

    /// <summary>The clock it runs on (a test can drive it frame by frame).</summary>
    internal static Func<DateTime> Clock = () => DateTime.UtcNow;

    private double Now => (Clock() - _start).TotalSeconds;

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
        _last = -1;
        top.RequestAnimationFrame(Frame);
    }

    private void Frame(TimeSpan _)
    {
        if (Still || !IsEffectivelyVisible || TopLevel.GetTopLevel(this) is not { } top) { _running = false; InvalidateVisual(); return; }
        var now = Now;
        if (_last >= 0) Step(now, Math.Min(now - _last, 1 / 20.0));
        _last = now;
        InvalidateVisual();
        top.RequestAnimationFrame(Frame);
    }

    /// <summary>A push on the edge around one angle, which then runs round the ring as a ripple.</summary>
    private void Ripple(double at, double strength)
    {
        for (var i = 0; i < N; i++)
        {
            var a = i * 2 * Math.PI / N;
            var dist = Math.Atan2(Math.Sin(a - at), Math.Cos(a - at));
            _v[i] += strength * Math.Exp(-dist * dist * 6);
        }
    }

    /// <summary>Moves the springs on, in small fixed steps so it behaves the same at any frame rate.</summary>
    private void Step(double t, double dt)
    {
        // Where the eyes look: a quick dart now and then, and the body sways after them.
        if (t >= _nextLook)
        {
            _lookTo = _rng.NextDouble() < 0.35 ? 0 : (_rng.NextDouble() * 2 - 1);
            _nextLook = t + 1.4 + _rng.NextDouble() * 2.8;
        }
        const double h = 1 / 240.0;
        for (var left = dt; left > 1e-6; left -= h)
        {
            var s = Math.Min(h, left);
            var before = _look;
            _look += (_lookTo - _look) * Math.Min(1, s * 18);
            _leanV += (_look - before) * 1.6; // a dart of the eyes tips the body after them

            // Breathing pulls the squash gently in and out; the springs give it overshoot and a settling wobble.
            var breathe = Math.Sin(t * 2 * Math.PI / 2.6) * 0.03;
            var airborne = _y > 0 || _yv > 0;
            var qTarget = airborne ? 0.06 : breathe;
            _qv += (-(_q - qTarget) * 170 - _qv * 4.2) * s;
            _q += _qv * s;
            _leanV += (-_lean * 110 - _leanV * 3.5) * s;
            _lean += _leanV * s;

            if (airborne)
            {
                _yv -= Gravity * s;
                _y += _yv * s;
                if (_y <= 0)
                {
                    Land(-_yv);
                    _y = 0;
                    _yv = 0;
                }
            }

            // The edge: each point springs back to its place and pulls on its neighbours, so a push travels.
            for (var i = 0; i < N; i++)
            {
                var a = i * 2 * Math.PI / N;
                var drift = 0.010 * Math.Sin(2 * a + t * 1.3) + 0.007 * Math.Sin(3 * a - t * 1.9 + 1) + 0.004 * Math.Sin(5 * a + t * 2.7);
                var lap = _d[(i + N - 1) % N] + _d[(i + 1) % N] - 2 * _d[i];
                _v[i] += (-(_d[i] - drift) * 140 - _v[i] * 3.2 + lap * 520) * s;
            }
            for (var i = 0; i < N; i++) _d[i] = Math.Clamp(_d[i] + _v[i] * s, -0.18, 0.18);

            for (var k = _drops.Count - 1; k >= 0; k--)
            {
                var p = _drops[k];
                p.Vy -= Gravity * 1.2 * s;
                p.X += p.Vx * s;
                p.Y += p.Vy * s;
                p.Age += s;
                if ((p.Y <= 0 && p.Vy < 0) || p.Age > 1) _drops.RemoveAt(k);
            }
        }
    }

    /// <summary>Touch-down: a squash, a ripple from the bottom, and a couple of drops thrown off.</summary>
    private void Land(double speed)
    {
        _qv -= speed * 1.6;
        Ripple(Math.PI / 2, -speed * 0.5); // the bottom (angles grow downwards on screen)
        for (var k = 0; k < 3; k++)
        {
            var side = k == 2 ? (_rng.NextDouble() < 0.5 ? -1 : 1) : (k == 0 ? -1 : 1);
            _drops.Add(new Drop
            {
                X = side * (0.30 + _rng.NextDouble() * 0.12),
                Y = 0.05,
                Vx = side * (0.35 + _rng.NextDouble() * 0.35),
                Vy = 0.9 + _rng.NextDouble() * 0.6,
                R = 0.035 + _rng.NextDouble() * 0.02,
            });
        }
    }

    public override void Render(DrawingContext ctx)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w < 4 || h < 4) return;
        var body = Body ?? (this.TryFindResource("Brand", ActualThemeVariant, out var b) && b is IBrush brand ? brand : Brushes.BlueViolet);
        var shadow = Shadow ?? (this.TryFindResource("Line", ActualThemeVariant, out var l) && l is IBrush line ? line : Brushes.Gray);
        var still = Still;
        var t = still ? 0 : Now;

        var q = still ? 0 : _q;
        var sy = 1 + q;
        var sx = 1 / sy; // the same amount of blob, just squashed or stretched
        var ground = h * 0.94;
        var rx = w * 0.43 * sx;
        var ry = h * 0.41 * sy;
        var cx = w / 2;
        var lift = still ? 0 : _y * h;
        var cy = ground - ry - lift;
        var lean = still ? 0 : _lean * w;

        // A flat shadow that shrinks while it's in the air.
        var air = Math.Clamp(_y / 0.12, 0, 1);
        ctx.DrawEllipse(shadow, null, new Point(cx, ground), rx * (0.92 - air * 0.3), h * 0.035);

        // The edge points: a soft squircle (a round dome on top, flatter where it sits), each moved by its spring.
        var pts = new Point[N];
        for (var i = 0; i < N; i++)
        {
            var a = i * 2 * Math.PI / N;
            var c = Math.Cos(a);
            var s = Math.Sin(a);
            var ex = s > 0 ? 0.78 : 1.0; // a round dome on top, a soft flat base where it sits
            var r = 1 + (still ? 0 : _d[i]);
            var x = rx * r * Math.Sign(c) * Math.Pow(Math.Abs(c), ex);
            var y = ry * r * Math.Sign(s) * Math.Pow(Math.Abs(s), ex);
            // The top sways more than the bottom, which stays put on the ground.
            var height = Math.Clamp((ry - y) / (2 * ry), 0, 1);
            pts[i] = new Point(cx + x + lean * height * height, cy + y);
        }
        ctx.DrawGeometry(body, null, Smooth(pts));

        foreach (var p in _drops)
        {
            var r = p.R * w * Math.Max(0.2, 1 - p.Age * 0.8);
            ctx.DrawEllipse(body, null, new Point(cx + p.X * w, ground - p.Y * h - r), r, r);
        }

        // Eyes ride along with the body: they squash with it and sway with the top.
        var look = still ? 0 : _look * w * 0.035;
        var eyeY = cy - ry * 0.08;
        var eyeLean = lean * 0.45;
        var gap = rx * 0.4;
        var ew = w * 0.085;
        var eh = h * 0.13 * Math.Sqrt(sy);
        var happy = !still && t - _cheerAt is >= 0 and < 1.8;

        if (happy)
        {
            // ^ ^ : two white arcs with round ends.
            var pen = new Pen(Eye, Math.Max(1.5, w * 0.055), lineCap: PenLineCap.Round);
            foreach (var side in new[] { -1, 1 })
            {
                var ex0 = cx + side * gap + look + eyeLean;
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
        if (!still)
        {
            if (t > _nextBlink + 0.14) _nextBlink = t + 2.8 + _rng.NextDouble() * 3.2;
            if (t >= _nextBlink) open = Math.Max(0.1, 1 - Math.Sin((t - _nextBlink) / 0.14 * Math.PI));
        }
        // Smiling eyes: a rounded dome on top and a gentle upward curve below, like a cheek pushing up. Outlined
        // with a round join as well as filled, so both tips are round, never points.
        var tip = Math.Max(1.2, ew * 0.42);
        var join = new Pen(Eye, tip, lineJoin: PenLineJoin.Round);
        foreach (var side in new[] { -1, 1 })
        {
            var ex0 = cx + side * gap + look + eyeLean;
            var top = eh * open;
            var lift2 = eh * 0.26 * open;
            var hw = ew - tip / 2;
            var y0 = eyeY + top * 0.35;
            var eye = new StreamGeometry();
            using (var g = eye.Open())
            {
                g.BeginFigure(new Point(ex0 - hw, y0), true);
                g.CubicBezierTo(new Point(ex0 - hw, y0 - top * 1.25), new Point(ex0 + hw, y0 - top * 1.25), new Point(ex0 + hw, y0));
                g.CubicBezierTo(new Point(ex0 + hw * 0.5, y0 - lift2), new Point(ex0 - hw * 0.5, y0 - lift2), new Point(ex0 - hw, y0));
                g.EndFigure(true);
            }
            ctx.DrawGeometry(Eye, join, eye);
        }
    }

    /// <summary>A closed curve through every point with no corners (Catmull-Rom, as cubic Béziers).</summary>
    private static StreamGeometry Smooth(Point[] p)
    {
        var geo = new StreamGeometry();
        using var g = geo.Open();
        var n = p.Length;
        g.BeginFigure(p[0], true);
        for (var i = 0; i < n; i++)
        {
            var p0 = p[(i + n - 1) % n];
            var p1 = p[i];
            var p2 = p[(i + 1) % n];
            var p3 = p[(i + 2) % n];
            g.CubicBezierTo(p1 + (p2 - p0) / 6, p2 - (p3 - p1) / 6, p2);
        }
        g.EndFigure(true);
        return geo;
    }
}
