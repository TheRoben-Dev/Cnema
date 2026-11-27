using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;

namespace Cnema.Controls;

/// <summary>What Mochi is up to, which changes how it moves and what it holds.</summary>
public enum MochiMood
{
    /// <summary>Idling: breathing, looking about, now and then a stretch, a wiggle or a little hop.</summary>
    Normal,
    /// <summary>Flipping through a little stack of clips (indexing).</summary>
    Reading,
    /// <summary>Droopy eyes, slow breaths and a z now and then (after midnight).</summary>
    Sleepy,
    /// <summary>Head tilted, one eye squinting, a question mark (next to a problem).</summary>
    Puzzled,
    /// <summary>A box balanced on its head, hopping it upwards (a backup running).</summary>
    Carrying,
    /// <summary>Holding a badge up high, beaming (a milestone).</summary>
    Proud,
    /// <summary>Only the top of it shows, peeking over the bottom edge (an empty page).</summary>
    Peek,
    /// <summary>Leaning in to hear, eyes towards the sound, sound waves coming in (the voice game).</summary>
    Listening,
}

/// <summary>A hat for the season.</summary>
public enum MochiHat { None, Santa, Party }

/// <summary>
/// Mochi, Savefile's mascot: a flat violet blob with white eyes and no mouth, animated like a little liquid
/// character. Its outline is a ring of points on springs joined by a smooth curve: ripples run round the edge,
/// a hop crouches first (anticipation), stretches on take-off and squashes on landing (its area stays the same),
/// then wobbles until it settles (follow-through), with drops splashing off. The eyes lead and the body sways
/// after them (secondary motion). When idle it picks something different to do every few seconds (a glance, a
/// stretch, a wiggle, a double blink, a look up, a small hop), never the same twice running, so it doesn't loop.
/// It can follow the pointer with its eyes, be poked (it wobbles like jelly; ten quick pokes and it spins and
/// gets dizzy), wear a seasonal hat, and act out a <see cref="MochiMood"/>. A few dozen points per frame while on
/// screen, nothing when it isn't, capped by <see cref="MaxFps"/>, and it holds still with Reduced motion.
/// </summary>
public sealed class BlobMascot : Control
{
    /// <summary>The shadow under the blob; the theme's Line colour when not set.</summary>
    public IBrush? Shadow { get; set; }
    /// <summary>Draws a glossy highlight over the body (the Wrapped video's glossy look); off in the app.</summary>
    public bool Gloss { get; set; }

    public static readonly StyledProperty<IBrush?> BodyProperty = AvaloniaProperty.Register<BlobMascot, IBrush?>(nameof(Body));
    public static readonly StyledProperty<bool> StillProperty = AvaloniaProperty.Register<BlobMascot, bool>(nameof(Still));
    public static readonly StyledProperty<bool> ReadingProperty = AvaloniaProperty.Register<BlobMascot, bool>(nameof(Reading));
    public static readonly StyledProperty<MochiMood> MoodProperty = AvaloniaProperty.Register<BlobMascot, MochiMood>(nameof(Mood));
    public static readonly StyledProperty<MochiHat> HatProperty = AvaloniaProperty.Register<BlobMascot, MochiHat>(nameof(Hat));
    public static readonly StyledProperty<int> CheerTickProperty = AvaloniaProperty.Register<BlobMascot, int>(nameof(CheerTick));
    public static readonly StyledProperty<int> BigCheerTickProperty = AvaloniaProperty.Register<BlobMascot, int>(nameof(BigCheerTick));
    public static readonly StyledProperty<int> SparkleTickProperty = AvaloniaProperty.Register<BlobMascot, int>(nameof(SparkleTick));
    public static readonly StyledProperty<bool> FollowPointerProperty = AvaloniaProperty.Register<BlobMascot, bool>(nameof(FollowPointer));
    public static readonly StyledProperty<bool> PokeableProperty = AvaloniaProperty.Register<BlobMascot, bool>(nameof(Pokeable));
    public static readonly StyledProperty<double> MaxFpsProperty = AvaloniaProperty.Register<BlobMascot, double>(nameof(MaxFps), 60);

    /// <summary>The body colour; the brand violet when unset.</summary>
    public IBrush? Body { get => GetValue(BodyProperty); set => SetValue(BodyProperty, value); }
    /// <summary>No movement at all (Reduced motion): a still, smiling blob.</summary>
    public bool Still { get => GetValue(StillProperty); set => SetValue(StillProperty, value); }
    /// <summary>Bump it to make Mochi hop and squint happily (a new line to say).</summary>
    public int CheerTick { get => GetValue(CheerTickProperty); set => SetValue(CheerTickProperty, value); }
    /// <summary>Bump it for a really big hop with sparkles (a really good moment).</summary>
    public int BigCheerTick { get => GetValue(BigCheerTickProperty); set => SetValue(BigCheerTickProperty, value); }
    /// <summary>Bump it for a happy squish and a burst of sparkles (a clip was saved).</summary>
    public int SparkleTick { get => GetValue(SparkleTickProperty); set => SetValue(SparkleTickProperty, value); }
    /// <summary>Same as <see cref="Mood"/> = Reading (kept for the indexing card).</summary>
    public bool Reading { get => GetValue(ReadingProperty); set => SetValue(ReadingProperty, value); }
    public MochiMood Mood { get => GetValue(MoodProperty); set => SetValue(MoodProperty, value); }
    public MochiHat Hat { get => GetValue(HatProperty); set => SetValue(HatProperty, value); }
    /// <summary>The eyes follow the pointer while it moves anywhere in the window.</summary>
    public bool FollowPointer { get => GetValue(FollowPointerProperty); set => SetValue(FollowPointerProperty, value); }
    /// <summary>A click wobbles it like jelly; ten quick ones and it spins (<see cref="EasterEgg"/>).</summary>
    public bool Pokeable { get => GetValue(PokeableProperty); set => SetValue(PokeableProperty, value); }
    /// <summary>Frames a second at most (lower for the small ones that run for a long time).</summary>
    public double MaxFps { get => GetValue(MaxFpsProperty); set => SetValue(MaxFpsProperty, value); }

    /// <summary>Ten pokes in a few seconds: it spun round and is dizzy.</summary>
    public event Action? EasterEgg;

    private MochiMood ActualMood => Reading ? MochiMood.Reading : Mood;

    private static readonly IBrush Eye = Brushes.White;
    private static readonly IBrush SantaRed = new SolidColorBrush(Color.FromRgb(0xE5, 0x48, 0x4D));
    private static readonly IBrush Gold = new SolidColorBrush(Color.FromRgb(0xFF, 0xC5, 0x3D));
    private static readonly IBrush GoldDark = new SolidColorBrush(Color.FromRgb(0xE0, 0x9B, 0x1A));
    private const int N = 30;              // points around the edge
    private const double Gravity = 9.5;    // in heights per second² (a heavy slime: short, weighty hops)

    private readonly Mochi3D _body3D = new();
    private readonly DateTime _start = Clock();
    private readonly Random _rng = new();
    private bool _running;
    private DispatcherTimer? _wake;

    // The soft body. Edge points move in and out from their rest place (fractions of the radius).
    private readonly double[] _d = new double[N], _v = new double[N];
    private double _q, _qv;         // squash (+ taller, − wider), area kept
    private double _lean, _leanV;   // the top sways sideways (fraction of the width)
    private double _y, _yv;         // height off the ground (fraction of the height)
    private double _look, _lookTo, _nextLook = 1.5;
    private double _lookY, _lookYTo;
    private double _nextBlink = 2.5, _secondBlink = -1, _cheerAt = -10, _happyFor = 1.8, _last = -1, _lastDraw = -1;
    private double _crouchUntil = -1, _pendingHop;   // anticipation: a crouch, then the take-off
    private double _stretchUntil = -1;                // an idle stretch: taller for a moment
    private double _idleNext = 3, _idleUntil = -1;
    private int _idleLast = -1;
    private double _squintUntil = -1;                 // poked: eyes squeezed shut
    private double _spinAt = -10, _dizzyUntil = -1;
    private double _nextZ = 1, _nextBeat;
    private double _pointerAt = -10;
    private Point _pointer;
    private readonly Queue<double> _pokes = new();
    private readonly List<Drop> _drops = new();
    private readonly List<Spark> _sparks = new();
    private readonly List<double> _zs = new();

    private sealed class Drop
    {
        public double X, Y, Vx, Vy, R, Age;
        public IBrush? Brush;
    }

    private sealed class Spark
    {
        public double X, Y, Vx, Vy, Size, Age, Life;
        public bool White;
    }

    static BlobMascot()
    {
        AffectsRender<BlobMascot>(BodyProperty, StillProperty, ReadingProperty, MoodProperty, HatProperty);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == CheerTickProperty) Cheer();
        else if (change.Property == BigCheerTickProperty) Cheer(big: true);
        else if (change.Property == SparkleTickProperty) Sparkle();
        else if (change.Property == MoodProperty || change.Property == ReadingProperty)
        {
            ClipToBounds = ActualMood == MochiMood.Peek;
            if (ActualMood == MochiMood.Proud) _cheerAt = Now - 0.5;
            Kick();
        }
        else if (change.Property == StillProperty || change.Property == IsVisibleProperty) Kick();
    }

    /// <summary>A hop with ^ ^ eyes: a crouch, a stretch up, a squash and a splash on landing, then a jiggle.</summary>
    public void Cheer() => Cheer(false);

    private void Cheer(bool big)
    {
        _cheerAt = Now;
        _happyFor = big ? 2.6 : 1.8;
        if (!Still)
        {
            Hop(big ? 1.75 : 1.25);
            if (big) Burst(10);
        }
        Kick();
    }

    /// <summary>A clip was saved: a happy squish down and back, and sparkles.</summary>
    private void Sparkle()
    {
        _cheerAt = Now;
        _happyFor = 1.4;
        if (!Still)
        {
            _qv -= 2.4;
            Ripple(-Math.PI / 2, 0.35);
            Burst(8);
        }
        Kick();
    }

    /// <summary>Anticipation first: a quick crouch, and the take-off when it's done.</summary>
    private void Hop(double speed)
    {
        if (_y > 0.01 || _crouchUntil > Now) return;
        _crouchUntil = Now + 0.2 + speed * 0.05; // a longer, deeper wind-up: it's heavy
        _pendingHop = speed;
        _qv -= 1.8 + speed * 0.8;
    }

    private void Burst(int count)
    {
        for (var k = 0; k < count; k++)
        {
            var a = -Math.PI / 2 + (k - (count - 1) / 2.0) * (Math.PI * 1.3 / count) + (_rng.NextDouble() - 0.5) * 0.3;
            var speed = 0.55 + _rng.NextDouble() * 0.45;
            _sparks.Add(new Spark
            {
                X = Math.Cos(a) * 0.25, Y = 0.55 - Math.Sin(a) * 0.1,
                Vx = Math.Cos(a) * speed, Vy = -Math.Sin(a) * speed + 0.3,
                Size = 0.05 + _rng.NextDouble() * 0.04, Life = 0.7 + _rng.NextDouble() * 0.4, White = k % 3 != 0,
            });
        }
    }

    /// <summary>The clock it runs on (a test can drive it frame by frame).</summary>
    internal static Func<DateTime> Clock = () => DateTime.UtcNow;

    private double? _manual;
    private double Now => _manual ?? (Clock() - _start).TotalSeconds;

    /// <summary>
    /// Runs the blob on a clock of its own, for drawing it frame by frame off screen (the Wrapped video):
    /// moves the springs on to <paramref name="seconds"/> and redraws.
    /// </summary>
    public void DriveTo(double seconds)
    {
        _manual ??= seconds;
        var from = _last >= 0 ? _last : seconds;
        _manual = seconds;
        for (var t = from; t < seconds - 1e-9;)
        {
            var dt = Math.Min(1 / 20.0, seconds - t);
            t += dt;
            Step(t, dt);
        }
        _last = seconds;
        InvalidateVisual();
    }

    private TopLevel? _top;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // The frame loop stops while the blob is hidden or scrolled off; this looks in now and then to restart it.
        _wake = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Kick());
        _wake.Start();
        _top = TopLevel.GetTopLevel(this);
        _top?.AddHandler(PointerMovedEvent, OnWindowPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        EffectiveViewportChanged += OnViewportChanged;
        Mochi3D.Changed += InvalidateVisual;
        ClipToBounds = ActualMood == MochiMood.Peek;
        Kick();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        // The window it was added to: once detached, the blob can't look its top level up any more.
        _top?.RemoveHandler(PointerMovedEvent, OnWindowPointerMoved);
        EffectiveViewportChanged -= OnViewportChanged;
        Mochi3D.Changed -= InvalidateVisual;
        _inView = true;
        _top = null;
        _wake?.Stop();
        _wake = null;
        _running = false;
    }

    private void OnWindowPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!FollowPointer || Still || _manual is not null || !IsEffectivelyVisible) return;
        _pointer = e.GetPosition(this);
        _pointerAt = Now;
        Kick();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!Pokeable || Still) return;
        var t = Now;
        var p = e.GetPosition(this);
        var angle = Math.Atan2(p.Y - Bounds.Height * 0.55, p.X - Bounds.Width / 2);
        // Jelly: the edge dents where it was poked, it squashes, leans away and squeezes its eyes shut.
        Ripple(angle, -0.9);
        _qv -= 2.2;
        _leanV -= Math.Sign(p.X - Bounds.Width / 2) * 1.6;
        _squintUntil = t + 0.28;
        _pokes.Enqueue(t);
        while (_pokes.Count > 0 && t - _pokes.Peek() > 4) _pokes.Dequeue();
        if (_pokes.Count >= 10)
        {
            _pokes.Clear();
            _spinAt = t + 0.12;
            _dizzyUntil = t + 3.4;
            Hop(1.6);
            Confetti();
            EasterEgg?.Invoke();
        }
        Kick();
    }

    private void Confetti()
    {
        IBrush Res(string key, IBrush fallback) => this.TryFindResource(key, ActualThemeVariant, out var v) && v is IBrush b ? b : fallback;
        var tints = new[] { Gold, Res("Highlight", Brushes.MediumPurple), Eye, SantaRed };
        for (var k = 0; k < 12; k++)
        {
            var side = k % 2 == 0 ? -1 : 1;
            _drops.Add(new Drop
            {
                X = side * (0.1 + _rng.NextDouble() * 0.2), Y = 0.6,
                Vx = side * (0.3 + _rng.NextDouble() * 0.6), Vy = 1.2 + _rng.NextDouble() * 0.9,
                R = 0.022 + _rng.NextDouble() * 0.015, Brush = tints[k % tints.Length],
            });
        }
    }

    // False while scrolled out of sight (the library's shelf when the grid is scrolled down): no frames are drawn then.
    private bool _inView = true;

    private void OnViewportChanged(object? sender, Avalonia.Layout.EffectiveViewportChangedEventArgs e)
    {
        var v = e.EffectiveViewport;
        // Scrolled out of a clipping parent, the viewport comes back empty.
        var inView = v.Width > 0 && v.Height > 0 && v.Intersects(new Rect(Bounds.Size).Inflate(8));
        if (inView == _inView) return;
        _inView = inView;
        if (inView) Kick();
    }

    private void Kick()
    {
        if (!_inView) return;
        if (_running || Still || _manual is not null || !IsEffectivelyVisible || TopLevel.GetTopLevel(this) is not { } top) { InvalidateVisual(); return; }
        if (!Shown(top)) return; // the wake timer starts it again once the window is back
        _running = true;
        _last = -1;
        top.RequestAnimationFrame(Frame);
    }

    private void Frame(TimeSpan _)
    {
        if (Still || !IsEffectivelyVisible || TopLevel.GetTopLevel(this) is not { } top) { _running = false; InvalidateVisual(); return; }
        if (!_inView || !Shown(top)) { _running = false; return; }
        var now = Now;
        // The small ones run for hours: a lower frame rate there is plenty and costs little.
        if (_lastDraw >= 0 && now - _lastDraw < 1 / Math.Max(10, MaxFps) - 0.002) { top.RequestAnimationFrame(Frame); return; }
        if (_last >= 0) Step(now, Math.Min(now - _last, 1 / 15.0));
        _last = now;
        _lastDraw = now;
        InvalidateVisual();
        top.RequestAnimationFrame(Frame);
    }

    /// <summary>
    /// The window is on screen: not hidden to the tray or minimised. Savefile can sit in the tray for a whole
    /// gaming session, and a blob animating in a hidden window would keep the CPU busy all that time.
    /// </summary>
    private static bool Shown(TopLevel top) => top is not Window w || (w.IsVisible && w.WindowState != WindowState.Minimized);

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

    /// <summary>Something to do while idle, never the same thing twice running.</summary>
    private void Idle(double t)
    {
        var mood = ActualMood;
        if (mood is MochiMood.Reading or MochiMood.Carrying or MochiMood.Proud) return;
        if (t < _idleNext) return;
        // 0 glance, 1 stretch, 2 wiggle, 3 double blink, 4 look up, 5 small hop
        var weights = mood switch
        {
            MochiMood.Sleepy => new[] { 1.0, 2.5, 0, 0.5, 0, 0 },   // a big slow stretch, like a yawn
            MochiMood.Puzzled => new[] { 2.0, 0, 1.5, 1.0, 2.0, 0 },
            MochiMood.Listening => new[] { 0.5, 0, 1.0, 2.0, 0.5, 0 },
            MochiMood.Peek => new[] { 3.0, 0, 1.0, 1.5, 1.0, 0 },
            _ => new[] { 2.0, 1.4, 1.2, 1.2, 1.0, 0.8 },
        };
        if (_idleLast >= 0) weights[_idleLast] = 0;
        var pick = _rng.NextDouble() * weights.Sum();
        var kind = 0;
        for (; kind < weights.Length - 1; kind++)
        {
            pick -= weights[kind];
            if (pick <= 0) break;
        }
        _idleLast = kind;
        switch (kind)
        {
            case 0: _lookTo = (_rng.NextDouble() < 0.5 ? -1 : 1) * (0.7 + _rng.NextDouble() * 0.3); _idleUntil = t + 0.6 + _rng.NextDouble() * 0.6; break;
            case 1: _stretchUntil = t + (mood == MochiMood.Sleepy ? 1.3 : 0.7); _lookYTo = -0.6; _idleUntil = _stretchUntil; break;
            case 2: _leanV += 1.6; _idleUntil = t + 0.45; break;
            case 3: _nextBlink = t; _secondBlink = t + 0.28; break;
            case 4: _lookYTo = -1; _lookTo = (_rng.NextDouble() - 0.5) * 0.6; _idleUntil = t + 1.0; break;
            case 5: Hop(0.7); break;
        }
        _idleNext = t + (mood == MochiMood.Sleepy ? 6 : 3.5) + _rng.NextDouble() * 4.5;
    }

    /// <summary>Moves the springs on, in small fixed steps so it behaves the same at any frame rate.</summary>
    private void Step(double t, double dt)
    {
        var mood = ActualMood;
        var pointerRecent = FollowPointer && t - _pointerAt < 2.5;

        // Where the eyes look: reading along a card, the pointer, an idle glance, or a quick dart now and then.
        if (mood == MochiMood.Reading)
        {
            var sweep = t % 1.8 / 1.8;
            _lookTo = sweep < 0.8 ? -0.7 + 1.4 * (sweep / 0.8) : 0.7 - 1.4 * ((sweep - 0.8) / 0.2);
            _lookYTo = 0.4;
        }
        else if (pointerRecent && Bounds.Width > 0)
        {
            var dx = (_pointer.X - Bounds.Width / 2) / Math.Max(120, Bounds.Width * 3);
            var dy = (_pointer.Y - Bounds.Height * 0.45) / Math.Max(120, Bounds.Height * 3);
            _lookTo = Math.Clamp(dx, -1, 1);
            _lookYTo = Math.Clamp(dy, -1, 1);
        }
        else
        {
            Idle(t);
            if (t > _idleUntil && _idleUntil > 0) { _idleUntil = -1; _lookTo = 0; _lookYTo = mood == MochiMood.Peek ? -0.5 : 0; }
            if (_idleUntil < 0 && t >= _nextLook)
            {
                _lookTo = _rng.NextDouble() < 0.4 ? 0 : (_rng.NextDouble() * 2 - 1) * 0.6;
                _nextLook = t + 1.6 + _rng.NextDouble() * 3.2;
            }
            if (mood == MochiMood.Puzzled && _idleUntil < 0) _lookYTo = -0.7;
            if (mood == MochiMood.Listening && _idleUntil < 0) { _lookTo = -0.75; _lookYTo = -0.15; }
            if (mood == MochiMood.Proud) _lookYTo = -0.8;
        }

        // Rhythms of the moods: hopping a box upwards, bouncing with pride, a z now and then when sleepy.
        if (mood == MochiMood.Carrying && t >= _nextBeat) { Hop(0.55); _nextBeat = t + 1.05; }
        if (mood == MochiMood.Proud && t >= _nextBeat) { Hop(0.45); _nextBeat = t + 0.9; _cheerAt = t; _happyFor = 1.2; }
        if (mood == MochiMood.Sleepy && t >= _nextZ) { _zs.Add(t); _nextZ = t + 2.2 + _rng.NextDouble(); }
        _zs.RemoveAll(z => t - z > 2.2);
        if (_secondBlink > 0 && t >= _secondBlink) { _nextBlink = t; _secondBlink = -1; }

        // The take-off after the crouch.
        if (_crouchUntil > 0 && t >= _crouchUntil)
        {
            _crouchUntil = -1;
            _yv = _pendingHop;
            _qv += 1.6 + _pendingHop * 0.9; // stretch on take-off
            Ripple(_rng.NextDouble() * 2 * Math.PI, 0.4);
        }

        var leanTarget = mood switch
        {
            MochiMood.Puzzled => 0.05 + Math.Sin(t * 0.9) * 0.012,
            MochiMood.Listening => -0.06 + Math.Sin(t * 1.3) * 0.01,
            MochiMood.Carrying => 0.02,
            MochiMood.Sleepy => Math.Sin(t * 0.5) * 0.015,
            _ => 0,
        };

        const double h = 1 / 240.0;
        for (var left = dt; left > 1e-6; left -= h)
        {
            var s = Math.Min(h, left);
            var before = _look;
            _look += (_lookTo - _look) * Math.Min(1, s * (pointerRecent ? 10 : 18));
            _lookY += (_lookYTo - _lookY) * Math.Min(1, s * 9);
            _leanV += (_look - before) * 1.6; // a dart of the eyes tips the body after them

            // Breathing pulls the squash gently in and out; the springs give it overshoot and a settling wobble.
            var breathe = mood == MochiMood.Sleepy ? Math.Sin(t * 2 * Math.PI / 5.0) * 0.05 - 0.04 : Math.Sin(t * 2 * Math.PI / 3.4) * 0.035 - 0.03; // settled low, slow heavy breaths
            var airborne = _y > 0 || _yv > 0;
            var crouching = _crouchUntil > 0;
            var qTarget = airborne ? 0.08 : crouching ? -0.24 : t < _stretchUntil ? 0.13 : breathe;
            _qv += (-(_q - qTarget) * 95 - _qv * 3.0) * s; // slow and heavy: big squash, a lazy overshoot
            _q += _qv * s;
            _leanV += (-(_lean - leanTarget) * 70 - _leanV * 2.8) * s; // the top lags behind, then sloshes back
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
                _v[i] += (-(_d[i] - drift) * 80 - _v[i] * 2.2 + lap * 380) * s; // gel: slower, longer jiggle
            }
            for (var i = 0; i < N; i++) _d[i] = Math.Clamp(_d[i] + _v[i] * s, -0.18, 0.18);

            for (var k = _drops.Count - 1; k >= 0; k--)
            {
                var p = _drops[k];
                p.Vy -= Gravity * 1.2 * s;
                p.X += p.Vx * s;
                p.Y += p.Vy * s;
                p.Age += s;
                if ((p.Y <= 0 && p.Vy < 0) || p.Age > 1.4) _drops.RemoveAt(k);
            }
            for (var k = _sparks.Count - 1; k >= 0; k--)
            {
                var p = _sparks[k];
                p.Vy -= 0.9 * s;          // they drift, barely falling
                p.Vx *= 1 - 1.5 * s;
                p.X += p.Vx * s;
                p.Y += p.Vy * s;
                p.Age += s;
                if (p.Age > p.Life) _sparks.RemoveAt(k);
            }
        }
    }

    /// <summary>Touch-down: a squash, a ripple from the bottom, and a couple of drops thrown off.</summary>
    private void Land(double speed)
    {
        _qv -= speed * 2.3; // lands with a heavy splat
        Ripple(Math.PI / 2, -speed * 0.75); // the bottom (angles grow downwards on screen)
        var count = speed > 1 ? 3 : speed > 0.5 ? 2 : 0;
        for (var k = 0; k < count; k++)
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

    private IBrush Res(string key, IBrush fallback) => this.TryFindResource(key, ActualThemeVariant, out var v) && v is IBrush b ? b : fallback;

    public override void Render(DrawingContext ctx)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w < 4 || h < 4) return;
        var body = Body ?? Res("Brand", Brushes.BlueViolet);
        var shadow = Shadow ?? Res("Line", Brushes.Gray);
        var still = Still;
        var t = still ? 0 : Now;
        var mood = ActualMood;

        var q = still ? -0.03 : Math.Clamp(_q, -0.27, 0.3); // heavy: squashes a lot, sits a little low
        var sy = 1 + q;
        var sx = 1 / sy; // the same amount of blob, just squashed or stretched
        // Peeking: the same blob sitting below the bottom edge, so only its top shows (and a bit more on hover).
        var peek = mood == MochiMood.Peek;
        var ground = peek ? h * (IsPointerOver ? 1.0 : 1.1) : h * 0.94;
        // A little smaller when something sits on its head, so the hat or the box stays in the frame.
        var size = mood == MochiMood.Carrying ? 0.8 : Hat != MochiHat.None && !peek ? 0.9 : 1;
        var rx = w * 0.39 * sx * size; // narrower at rest: the base pools out wider than this
        var ry = h * 0.41 * sy * size;
        var cx = w / 2;
        var lift = still ? 0 : _y * h;
        var cy = ground - ry - lift;
        var lean = still ? 0 : _lean * w;

        // The easter egg: one full turn in the air.
        var spin = still ? 0 : Math.Clamp((t - _spinAt) / 0.85, 0, 1);
        var spinAngle = spin is > 0 and < 1 ? (1 - Math.Pow(1 - spin, 3)) * 2 * Math.PI : 0;
        using var _ = spinAngle != 0
            ? ctx.PushTransform(Matrix.CreateTranslation(-cx, -cy) * Matrix.CreateRotation(spinAngle) * Matrix.CreateTranslation(cx, cy))
            : default(DrawingContext.PushedState?);

        if (!peek)
        {
            // A flat shadow that shrinks while it's in the air.
            var air = Math.Clamp(_y / 0.12, 0, 1);
            ctx.DrawEllipse(shadow, null, new Point(cx, ground), rx * (1.12 - air * 0.35), h * 0.04);
        }

        // The edge points: a soft squircle (a round dome on top, flatter where it sits), each moved by its spring.
        var pts = new Point[N];
        for (var i = 0; i < N; i++)
        {
            var a = i * 2 * Math.PI / N;
            var c = Math.Cos(a);
            var s = Math.Sin(a);
            var ex = s > 0 ? 0.78 : 1.0; // a round dome on top, a soft flat base where it sits
            var r = 1 + (still ? 0 : _d[i]);
            var y = ry * r * Math.Sign(s) * Math.Pow(Math.Abs(s), ex);
            var x = rx * r * Math.Sign(c) * Math.Pow(Math.Abs(c), ex) * Mochi3D.SlimeWidth(y / ry);
            if (y < 0) y *= Mochi3D.SlimePeak(x / rx); // a soft, rounded crown
            // The top sways more than the bottom, which stays put on the ground.
            var height = Math.Clamp((ry - y) / (2 * ry), 0, 1);
            pts[i] = new Point(cx + x + lean * height * height, cy + y);
        }
        var outline = Smooth(pts);
        // 3D Mochi (anime cel shading) when it's on and the shader runs; the flat body otherwise.
        var drawn3D = !Gloss && body is ISolidColorBrush solid && _body3D.Draw(ctx, new Mochi3D.Frame(
            new Point(cx, cy), rx, ry, lean, still ? 0 : -_look * 0.45, _d, still, solid.Color,
            _drops.Select(p => { var r = p.R * w * Math.Max(0.2, 1 - p.Age * 0.8); return (new Point(cx + p.X * w, ground - p.Y * h - r), r); }).ToList()),
            TopLevel.GetTopLevel(this)?.RenderScaling ?? 1);
        if (!drawn3D) ctx.DrawGeometry(body, null, outline);
        if (Gloss)
        {
            // Glossy like a drop of liquid, following its own shape: a soft sheen over the top that fades out
            // towards the middle, one small soft highlight, and warm light bouncing up along the bottom edge.
            using (ctx.PushGeometryClip(outline))
            {
                var sheenAt = new Point(cx - rx * 0.08, cy - ry * 0.32);
                var sheenPts = pts.Select(p => new Point(sheenAt.X + (p.X - cx) * 0.8, sheenAt.Y + (p.Y - cy) * 0.62)).ToArray();
                var sheen = new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0.3, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0.55, 1, RelativeUnit.Relative),
                    GradientStops =
                    {
                        new GradientStop(Color.FromArgb(120, 255, 255, 255), 0), new GradientStop(Color.FromArgb(40, 255, 255, 255), 0.35),
                        new GradientStop(Color.FromArgb(0, 255, 255, 255), 0.62),
                    },
                };
                ctx.DrawGeometry(sheen, null, Smooth(sheenPts));
                var spot = new RadialGradientBrush
                {
                    GradientStops = { new GradientStop(Color.FromArgb(200, 255, 255, 255), 0), new GradientStop(Color.FromArgb(60, 255, 255, 255), 0.45), new GradientStop(Color.FromArgb(0, 255, 255, 255), 1) },
                };
                ctx.DrawEllipse(spot, null, new Point(cx - rx * 0.38, cy - ry * 0.5), rx * 0.16, ry * 0.11);
                var bounce = new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0.5, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0.5, 1, RelativeUnit.Relative),
                    GradientStops = { new GradientStop(Color.FromArgb(0, 255, 140, 220), 0.55), new GradientStop(Color.FromArgb(90, 255, 150, 210), 1) },
                };
                ctx.DrawGeometry(null, new Pen(bounce, rx * 0.12), outline);
            }
        }

        foreach (var p in drawn3D ? Enumerable.Empty<Drop>() : _drops)
        {
            var r = p.R * w * Math.Max(0.2, 1 - p.Age * 0.8);
            ctx.DrawEllipse(p.Brush ?? body, null, new Point(cx + p.X * w, ground - p.Y * h - r), r, r);
        }

        var top = new Point(cx + lean, cy - ry); // the top of the dome, where hats and boxes sit
        if (Hat != MochiHat.None && mood != MochiMood.Carrying) DrawHat(ctx, top, rx, ry, lean / w);
        if (mood == MochiMood.Carrying) DrawBox(ctx, top, rx, ry, lean / w, body);
        if (mood == MochiMood.Reading) DrawCards(ctx, w, h, ground, t, still);

        DrawEyes(ctx, w, h, cx, cy, rx, ry, sy, lean, t, still, mood);
        if (mood == MochiMood.Proud) DrawBadge(ctx, w, h, body, cx, cy, rx, ry, lean, t, still);

        if (mood == MochiMood.Puzzled) DrawQuestion(ctx, w, h, top, rx, t, still);
        if (mood == MochiMood.Sleepy) DrawZs(ctx, w, h, top, rx, t);
        if (mood == MochiMood.Listening) DrawWaves(ctx, h, cx + lean, cy, rx, t, still);
        foreach (var p in _sparks) DrawSpark(ctx, w, h, cx, ground, p);
    }

    private void DrawEyes(DrawingContext ctx, double w, double h, double cx, double cy, double rx, double ry, double sy,
        double lean, double t, bool still, MochiMood mood)
    {
        // Eyes ride along with the body: they squash with it and sway with the top.
        var look = still ? 0 : _look * w * 0.035;
        var lookY = still ? 0 : _lookY * h * 0.03;
        var eyeY = cy - ry * (mood == MochiMood.Reading ? 0.22 : 0.08) + lookY;
        var eyeLean = lean * 0.45;
        var gap = rx * 0.4;
        var ew = w * 0.085;
        var eh = h * 0.13 * Math.Sqrt(sy);

        // Dizzy after the spin: little spirals.
        if (!still && t < _dizzyUntil && t > _spinAt + 0.5)
        {
            var pen = new Pen(Eye, Math.Max(1.2, w * 0.03), lineCap: PenLineCap.Round);
            foreach (var side in new[] { -1, 1 })
            {
                var c = new Point(cx + side * gap + eyeLean, eyeY);
                var g = new StreamGeometry();
                using (var gc = g.Open())
                {
                    var turn = t * 7 * side;
                    for (var k = 0; k <= 28; k++)
                    {
                        var a = turn + k * 0.42;
                        var r = ew * 1.1 * k / 28.0;
                        var p = new Point(c.X + Math.Cos(a) * r, c.Y + Math.Sin(a) * r * 0.9);
                        if (k == 0) gc.BeginFigure(p, false); else gc.LineTo(p);
                    }
                    gc.EndFigure(false);
                }
                ctx.DrawGeometry(null, pen, g);
            }
            return;
        }

        var happy = !still && (t - _cheerAt is >= 0 && t - _cheerAt < _happyFor || t < _squintUntil);
        if (happy || (still && mood == MochiMood.Proud))
        {
            // ^ ^ : two white arcs with round ends (squeezed flatter when poked).
            var squint = t < _squintUntil;
            var pen = new Pen(Eye, Math.Max(1.5, w * 0.055), lineCap: PenLineCap.Round);
            foreach (var side in new[] { -1, 1 })
            {
                var ex0 = cx + side * gap + look + eyeLean;
                var arc = new StreamGeometry();
                using (var g = arc.Open())
                {
                    if (squint)
                    {
                        // > < : squeezed shut
                        g.BeginFigure(new Point(ex0 - side * ew, eyeY - eh * 0.45), false);
                        g.LineTo(new Point(ex0 + side * ew * 0.6, eyeY));
                        g.LineTo(new Point(ex0 - side * ew, eyeY + eh * 0.45));
                    }
                    else
                    {
                        g.BeginFigure(new Point(ex0 - ew, eyeY + eh * 0.25), false);
                        g.QuadraticBezierTo(new Point(ex0, eyeY - eh * 0.9), new Point(ex0 + ew, eyeY + eh * 0.25));
                    }
                    g.EndFigure(false);
                }
                ctx.DrawGeometry(null, pen, arc);
            }
            return;
        }

        // Blinks every few seconds, 140 ms each (slower and heavier when sleepy).
        var open = 1.0;
        var blinkLength = mood == MochiMood.Sleepy ? 0.45 : 0.14;
        if (!still)
        {
            if (t > _nextBlink + blinkLength) _nextBlink = t + (mood == MochiMood.Sleepy ? 1.8 : 2.8) + _rng.NextDouble() * 3.2;
            if (t >= _nextBlink) open = Math.Max(0.1, 1 - Math.Sin((t - _nextBlink) / blinkLength * Math.PI));
        }
        if (mood == MochiMood.Sleepy) open *= 0.4;
        // Smiling eyes: a rounded dome on top and a gentle upward curve below, like a cheek pushing up. Outlined
        // with a round join as well as filled, so both tips are round, never points.
        var tip = Math.Max(1.2, ew * 0.42);
        var join = new Pen(Eye, tip, lineJoin: PenLineJoin.Round);
        foreach (var side in new[] { -1, 1 })
        {
            var ex0 = cx + side * gap + look + eyeLean;
            // Puzzled: one eye narrowed, the other wide, like a raised eyebrow.
            var o = mood == MochiMood.Puzzled ? open * (side < 0 ? 0.55 : 1.15) : open;
            var top = eh * o;
            var lift2 = eh * 0.26 * o;
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

    /// <summary>A seasonal hat on the dome, tipping with the sway: a Santa hat, or a striped party hat.</summary>
    private void DrawHat(DrawingContext ctx, Point top, double rx, double ry, double leanFrac)
    {
        var tilt = 0.18 + leanFrac * 2.5;
        using var _ = ctx.PushTransform(Matrix.CreateTranslation(-top.X, -top.Y) * Matrix.CreateRotation(tilt) * Matrix.CreateTranslation(top.X, top.Y + ry * 0.06));
        var bw = rx * 1.05;
        var bh = ry * 0.85;
        if (Hat == MochiHat.Santa)
        {
            var hat = new StreamGeometry();
            using (var g = hat.Open())
            {
                g.BeginFigure(new Point(top.X - bw / 2, top.Y + bh * 0.05), true);
                g.CubicBezierTo(new Point(top.X - bw * 0.35, top.Y - bh * 0.7), new Point(top.X + bw * 0.1, top.Y - bh * 1.05), new Point(top.X + bw * 0.62, top.Y - bh * 0.62));
                g.CubicBezierTo(new Point(top.X + bw * 0.3, top.Y - bh * 0.55), new Point(top.X + bw * 0.42, top.Y - bh * 0.2), new Point(top.X + bw / 2, top.Y + bh * 0.05));
                g.EndFigure(true);
            }
            ctx.DrawGeometry(SantaRed, null, hat);
            var band = bh * 0.24;
            // A hairline round the white trim, so it still reads on a white page.
            var trim = new Pen(Res("Line", Brushes.LightGray), Math.Max(0.8, band * 0.08));
            ctx.DrawRectangle(Eye, trim, new Rect(top.X - bw * 0.56, top.Y - band * 0.55, bw * 1.12, band), band / 2, band / 2);
            var pom = bh * 0.17;
            ctx.DrawEllipse(Eye, trim, new Point(top.X + bw * 0.62, top.Y - bh * 0.62), pom, pom);
        }
        else
        {
            var w2 = bw * 0.62;
            var hgt = bh * 1.05;
            var cone = new StreamGeometry();
            using (var g = cone.Open())
            {
                g.BeginFigure(new Point(top.X - w2 / 2, top.Y + hgt * 0.04), true);
                g.LineTo(new Point(top.X, top.Y - hgt));
                g.LineTo(new Point(top.X + w2 / 2, top.Y + hgt * 0.04));
                g.QuadraticBezierTo(new Point(top.X, top.Y + hgt * 0.14), new Point(top.X - w2 / 2, top.Y + hgt * 0.04));
                g.EndFigure(true);
            }
            ctx.DrawGeometry(Gold, null, cone);
            // Stripes, clipped to the cone.
            using (ctx.PushGeometryClip(cone))
            {
                var stripe = new Pen(Res("Highlight", Brushes.MediumPurple), Math.Max(1.5, w2 * 0.16));
                for (var k = 0; k < 4; k++)
                {
                    var y = top.Y - hgt + hgt * (0.25 + k * 0.27);
                    ctx.DrawLine(stripe, new Point(top.X - w2, y + w2 * 0.35), new Point(top.X + w2, y - w2 * 0.35));
                }
            }
            var pom = hgt * 0.13;
            ctx.DrawEllipse(Eye, null, new Point(top.X, top.Y - hgt), pom, pom);
        }
    }

    /// <summary>A parcel balanced on the dome (taped in the accent colour), for backups.</summary>
    private static readonly IBrush Cardboard = new SolidColorBrush(Color.FromRgb(0xD9, 0xA0, 0x66));
    private static readonly IBrush CardboardDark = new SolidColorBrush(Color.FromRgb(0xB9, 0x7F, 0x45));

    /// <summary>A cardboard box balanced on its head, steadied by two little hands.</summary>
    private void DrawBox(DrawingContext ctx, Point top, double rx, double ry, double leanFrac, IBrush body)
    {
        var bw = rx * 1.3;
        var bh = ry * 0.8;
        using var _ = ctx.PushTransform(Matrix.CreateTranslation(-top.X, -top.Y) * Matrix.CreateRotation(leanFrac * 3) * Matrix.CreateTranslation(top.X, top.Y));
        var rect = new Rect(top.X - bw / 2, top.Y - bh + ry * 0.1, bw, bh);
        var radius = Math.Max(1.5, bw * 0.07);
        ctx.DrawRectangle(Cardboard, null, rect, radius, radius);
        // the flaps' seam and a strip of tape over it
        ctx.DrawRectangle(CardboardDark, null, new Rect(rect.X, rect.Y + bh * 0.22, bw, Math.Max(1, bh * 0.07)));
        ctx.DrawRectangle(Res("Highlight", Brushes.MediumPurple), null, new Rect(rect.Center.X - bw * 0.08, rect.Top, bw * 0.16, bh * 0.55));
        // hands on the lower corners
        var hand = rx * 0.17;
        foreach (var side in new[] { -1, 1 })
            ctx.DrawEllipse(body, null, new Point(rect.Center.X + side * bw * 0.47, rect.Bottom - bh * 0.3), hand, hand * 1.15);
    }

    /// <summary>A gold medal with a white star, held proudly in front in two little hands.</summary>
    private void DrawBadge(DrawingContext ctx, double w, double h, IBrush body, double cx, double cy, double rx, double ry, double lean, double t, bool still)
    {
        var bob = still ? 0 : Math.Sin(t * 5) * h * 0.012;
        var r = rx * 0.3;
        var c = new Point(cx + lean * 0.3, cy + ry * 0.58 + bob);
        // the ribbon, two short tails peeking out from under the medal
        var tail = Res("Highlight", Brushes.MediumPurple);
        foreach (var side in new[] { -1, 1 })
        {
            var g = new StreamGeometry();
            using (var gc = g.Open())
            {
                gc.BeginFigure(new Point(c.X + side * r * 0.15, c.Y), true);
                gc.LineTo(new Point(c.X + side * r * 0.7, c.Y + r * 1.05));
                gc.LineTo(new Point(c.X + side * r * 0.48, c.Y + r * 0.9));
                gc.LineTo(new Point(c.X + side * r * 0.28, c.Y + r * 1.1));
                gc.EndFigure(true);
            }
            ctx.DrawGeometry(tail, null, g);
        }
        ctx.DrawEllipse(GoldDark, null, c, r * 1.12, r * 1.12);
        ctx.DrawEllipse(Gold, null, c, r, r);
        ctx.DrawGeometry(Eye, null, Star(c, r * 0.62, r * 0.27, 5));
        var hand = rx * 0.15;
        foreach (var side in new[] { -1, 1 })
            ctx.DrawEllipse(body, null, new Point(c.X + side * r * 1.0, c.Y + r * 0.25), hand, hand * 1.1);
    }

    /// <summary>A question mark bobbing above its head.</summary>
    private void DrawQuestion(DrawingContext ctx, double w, double h, Point top, double rx, double t, bool still)
    {
        var bob = still ? 0 : Math.Sin(t * 2.4) * h * 0.02;
        var text = new FormattedText("?", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.Bold), Math.Max(8, h * 0.32), Res("TextDim", Brushes.Gray));
        ctx.DrawText(text, new Point(top.X + rx * 0.55, top.Y - text.Height * 0.85 + bob));
    }

    /// <summary>Sound coming in from the left: three arcs that travel towards Mochi and fade as they arrive.</summary>
    private void DrawWaves(DrawingContext ctx, double h, double cx, double cy, double rx, double t, bool still)
    {
        var brush = Res("Highlight", Brushes.MediumPurple);
        var thick = Math.Max(1.2, h * 0.035);
        for (var i = 0; i < 3; i++)
        {
            // Each arc starts far out and moves in; with Reduced motion they simply stand still, evenly spaced.
            var phase = still ? i / 3.0 : (t * 0.7 + i / 3.0) % 1.0;
            var r = rx * (1.45 - phase * 0.35);
            var dist = rx * (0.55 + (1 - phase) * 0.55);
            var centre = new Point(cx - rx - dist + r * 0.9, cy);
            var a = Math.PI * 0.78;
            var b = Math.PI * 1.22;
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                g.BeginFigure(new Point(centre.X + r * 0.32 * Math.Cos(a), centre.Y + r * 0.5 * Math.Sin(a)), false);
                g.ArcTo(new Point(centre.X + r * 0.32 * Math.Cos(b), centre.Y + r * 0.5 * Math.Sin(b)), new Size(r * 0.32, r * 0.5), 0, false, SweepDirection.Clockwise);
                g.EndFigure(false);
            }
            var fade = still ? 0.8 : Math.Sin(phase * Math.PI);
            using (ctx.PushOpacity(fade))
                ctx.DrawGeometry(null, new Pen(brush, thick, lineCap: PenLineCap.Round), geo);
        }
    }

    /// <summary>Little z's floating up and away while it dozes.</summary>
    private void DrawZs(DrawingContext ctx, double w, double h, Point top, double rx, double t)
    {
        foreach (var z0 in _zs)
        {
            var age = (t - z0) / 2.2;
            var text = new FormattedText("z", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.Bold), Math.Max(6, h * (0.14 + age * 0.12)), Res("TextDim", Brushes.Gray));
            using (ctx.PushOpacity(Math.Sin(age * Math.PI)))
                ctx.DrawText(text, new Point(top.X + rx * (0.5 + age * 0.5) + Math.Sin(age * 6) * w * 0.03, top.Y - age * h * 0.45));
        }
    }

    private void DrawSpark(DrawingContext ctx, double w, double h, double cx, double ground, Spark p)
    {
        var life = p.Age / p.Life;
        var scale = Math.Sin(Math.Min(1, life * 1.4) * Math.PI);
        if (scale <= 0.01) return;
        var c = new Point(cx + p.X * w, ground - p.Y * h);
        var r = p.Size * w * scale;
        ctx.DrawGeometry(p.White ? Eye : Res("Highlight", Brushes.MediumPurple), null, Star(c, r, r * 0.32, 4));
    }

    private static StreamGeometry Star(Point c, double outer, double inner, int points)
    {
        var g = new StreamGeometry();
        using var gc = g.Open();
        for (var k = 0; k < points * 2; k++)
        {
            var a = -Math.PI / 2 + k * Math.PI / points;
            var r = k % 2 == 0 ? outer : inner;
            var p = new Point(c.X + Math.Cos(a) * r, c.Y + Math.Sin(a) * r);
            if (k == 0) gc.BeginFigure(p, true); else gc.LineTo(p);
        }
        gc.EndFigure(true);
        return g;
    }

    /// <summary>
    /// The little stack of clips it's looking through, held in front: two cards behind and one on top that
    /// flips over every 1.8 seconds to show the next clip.
    /// </summary>
    private void DrawCards(DrawingContext ctx, double w, double h, double ground, double t, bool still)
    {
        var card = Res("Bg1", Brushes.White);
        var line = new Pen(Res("Line", Brushes.Gray), Math.Max(1, w * 0.012));
        var tints = new[] { Res("Brand", Brushes.BlueViolet), Res("TextDim", Brushes.Gray), Res("Highlight", Brushes.MediumPurple) };
        var cw = w * 0.46;
        var ch = h * 0.27;
        var x0 = w * 0.5 - cw / 2 + w * 0.04;
        var y0 = ground - ch * 0.9;
        var radius = Math.Max(2, w * 0.05);
        for (var k = 2; k >= 1; k--)
        {
            var off = k * w * 0.035;
            ctx.DrawRectangle(card, line, new Rect(x0 + off, y0 - off, cw, ch), radius, radius);
        }
        var flips = still ? 0 : (int)(t / 1.8);
        var phase = still ? 1 : Math.Min(1, t % 1.8 / 0.32);
        // Eased, so the card snaps over and settles rather than turning at an even speed.
        var eased = phase < 0.5 ? 2 * phase * phase : 1 - Math.Pow(-2 * phase + 2, 2) / 2;
        var sx = Math.Abs(Math.Cos(eased * Math.PI)); // 1 → 0 (edge on) → 1, the next clip showing
        var showing = phase < 0.5 ? flips : flips + 1;
        var cxCard = x0 + cw / 2;
        var rise = still ? 0 : Math.Sin(phase * Math.PI) * ch * 0.18; // lifted a little as it turns
        using (ctx.PushTransform(Matrix.CreateTranslation(-cxCard, 0) * Matrix.CreateScale(Math.Max(0.02, sx), 1) * Matrix.CreateTranslation(cxCard, -rise)))
        {
            var rect = new Rect(x0, y0, cw, ch);
            ctx.DrawRectangle(card, line, rect, radius, radius);
            var pic = rect.Deflate(Math.Max(1.5, w * 0.03));
            using (ctx.PushOpacity(0.55))
                ctx.DrawRectangle(tints[showing % tints.Length], null, pic, radius * 0.6, radius * 0.6);
            // A tiny play mark, so it reads as a video.
            var c = pic.Center;
            var s2 = pic.Height * 0.22;
            var tri = new StreamGeometry();
            using (var g = tri.Open())
            {
                g.BeginFigure(new Point(c.X - s2 * 0.6, c.Y - s2), true);
                g.LineTo(new Point(c.X + s2, c.Y));
                g.LineTo(new Point(c.X - s2 * 0.6, c.Y + s2));
                g.EndFigure(true);
            }
            ctx.DrawGeometry(Eye, null, tri);
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
