using System.Globalization;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Cnema.Controls;

/// <summary>A kept part of the video on the timeline, in milliseconds.</summary>
public readonly record struct TimeRange(double Start, double End);

/// <summary>A point of interest on the timeline: a session bookmark, or a loud moment found in the audio.</summary>
public readonly record struct TimelineMarker(double Ms, bool Bookmark);

/// <summary>A caption (Speed false) or speed section (Speed true) from the Effects panel.</summary>
public readonly record struct EffectRange(double Start, double End, bool Speed);

/// <summary>
/// Scrub bar with a trim range. Click/drag to seek; drag the bracket handles to move in/out points.
/// In/Out are two-way bindable (milliseconds, -1 = unset). Seeking is pushed through <see cref="SeekCommand"/>.
/// </summary>
public sealed class TrimTimeline : Control
{
    public static readonly StyledProperty<double> DurationProperty =
        AvaloniaProperty.Register<TrimTimeline, double>(nameof(Duration));
    public static readonly StyledProperty<double> PositionProperty =
        AvaloniaProperty.Register<TrimTimeline, double>(nameof(Position));
    public static readonly StyledProperty<double> InPointProperty =
        AvaloniaProperty.Register<TrimTimeline, double>(nameof(InPoint), -1, defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);
    public static readonly StyledProperty<double> OutPointProperty =
        AvaloniaProperty.Register<TrimTimeline, double>(nameof(OutPoint), -1, defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);
    public static readonly StyledProperty<ICommand?> SeekCommandProperty =
        AvaloniaProperty.Register<TrimTimeline, ICommand?>(nameof(SeekCommand));
    public static readonly StyledProperty<IBrush> AccentProperty =
        AvaloniaProperty.Register<TrimTimeline, IBrush>(nameof(Accent), new SolidColorBrush(Color.Parse("#8E6BFF")));
    public static readonly StyledProperty<IBrush> RangeBrushProperty =
        AvaloniaProperty.Register<TrimTimeline, IBrush>(nameof(RangeBrush), new SolidColorBrush(Color.Parse("#C2AEFF")));

    public static readonly StyledProperty<IReadOnlyList<TimeRange>?> SegmentsProperty =
        AvaloniaProperty.Register<TrimTimeline, IReadOnlyList<TimeRange>?>(nameof(Segments));
    public static readonly StyledProperty<IReadOnlyList<TimelineMarker>?> MarkersProperty =
        AvaloniaProperty.Register<TrimTimeline, IReadOnlyList<TimelineMarker>?>(nameof(Markers));
    public static readonly StyledProperty<IReadOnlyList<EffectRange>?> EffectsProperty =
        AvaloniaProperty.Register<TrimTimeline, IReadOnlyList<EffectRange>?>(nameof(Effects));

    /// <summary>Segments already added to a multi-part export; drawn as fixed ranges.</summary>
    public IReadOnlyList<TimeRange>? Segments { get => GetValue(SegmentsProperty); set => SetValue(SegmentsProperty, value); }
    public IReadOnlyList<TimelineMarker>? Markers { get => GetValue(MarkersProperty); set => SetValue(MarkersProperty, value); }
    /// <summary>Captions and speed sections, drawn as thin bars under the track.</summary>
    public IReadOnlyList<EffectRange>? Effects { get => GetValue(EffectsProperty); set => SetValue(EffectsProperty, value); }
    public double Duration { get => GetValue(DurationProperty); set => SetValue(DurationProperty, value); }
    public double Position { get => GetValue(PositionProperty); set => SetValue(PositionProperty, value); }
    public double InPoint { get => GetValue(InPointProperty); set => SetValue(InPointProperty, value); }
    public double OutPoint { get => GetValue(OutPointProperty); set => SetValue(OutPointProperty, value); }
    public ICommand? SeekCommand { get => GetValue(SeekCommandProperty); set => SetValue(SeekCommandProperty, value); }
    public IBrush Accent { get => GetValue(AccentProperty); set => SetValue(AccentProperty, value); }
    public IBrush RangeBrush { get => GetValue(RangeBrushProperty); set => SetValue(RangeBrushProperty, value); }

    private const double Pad = 10;
    private static readonly IBrush BookmarkBrush = new SolidColorBrush(Color.Parse("#FFD166"));
    private static readonly IBrush HighlightBrush = new SolidColorBrush(Color.Parse("#FF6B9D"));
    private static readonly IBrush CaptionBrush = new SolidColorBrush(Color.FromArgb(170, 255, 255, 255));
    private static readonly IBrush SpeedBrush = new SolidColorBrush(Color.Parse("#FF9F43"));
    private const double TrackH = 6;
    private enum Drag { None, Seek, In, Out }
    private Drag _drag;
    private double? _hoverX;
    private double _dragPos = -1;

    static TrimTimeline()
    {
        AffectsRender<TrimTimeline>(DurationProperty, PositionProperty, InPointProperty, OutPointProperty, AccentProperty, RangeBrushProperty,
            SegmentsProperty, MarkersProperty, EffectsProperty);
        FocusableProperty.OverrideDefaultValue<TrimTimeline>(false);
    }

    public TrimTimeline()
    {
        Height = 44;
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    private double TrackWidth => Math.Max(1, Bounds.Width - Pad * 2);
    private double XFor(double ms) => Duration <= 0 ? Pad : Pad + Math.Clamp(ms / Duration, 0, 1) * TrackWidth;
    private double MsFor(double x) => Duration <= 0 ? 0 : Math.Clamp((x - Pad) / TrackWidth, 0, 1) * Duration;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Duration <= 0 || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var x = e.GetPosition(this).X;
        if (InPoint >= 0 && Math.Abs(x - XFor(InPoint)) <= 8) _drag = Drag.In;
        else if (OutPoint >= 0 && Math.Abs(x - XFor(OutPoint)) <= 8) _drag = Drag.Out;
        else { _drag = Drag.Seek; Seek(x); }
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var x = e.GetPosition(this).X;
        _hoverX = x;
        // The button came up somewhere we never heard about (the player closed mid-drag): stop dragging.
        if (_drag != Drag.None && !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) { _drag = Drag.None; _dragPos = -1; }
        switch (_drag)
        {
            case Drag.Seek: Seek(x); break;
            case Drag.In:
                var i = MsFor(x);
                InPoint = OutPoint >= 0 ? Math.Min(i, OutPoint - 100) : i;
                break;
            case Drag.Out:
                var o = MsFor(x);
                OutPoint = InPoint >= 0 ? Math.Max(o, InPoint + 100) : o;
                break;
        }
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _drag = Drag.None;
        _dragPos = -1;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _drag = Drag.None;
        _dragPos = -1;
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hoverX = null;
        InvalidateVisual();
    }

    private void Seek(double x)
    {
        var ms = MsFor(x);
        _dragPos = ms;
        if (SeekCommand?.CanExecute(ms) == true) SeekCommand.Execute(ms);
    }

    public override void Render(DrawingContext ctx)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        var cy = h / 2 + 4;
        var accent = Accent;
        var range = RangeBrush;
        var rangeColor = (range as ISolidColorBrush)?.Color ?? Color.Parse("#C2AEFF");

        // Hit area (transparent so the whole control receives pointer input).
        ctx.FillRectangle(Brushes.Transparent, new Rect(0, 0, w, h));

        var track = new Rect(Pad, cy - TrackH / 2, TrackWidth, TrackH);
        ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(46, 255, 255, 255)), null, track, 3, 3);

        // Segments already added to the export.
        if (Duration > 0 && Segments is { Count: > 0 } segments)
        {
            foreach (var seg in segments)
            {
                var sx1 = XFor(seg.Start);
                var sx2 = XFor(seg.End);
                ctx.DrawRectangle(new SolidColorBrush(rangeColor, 0.30), null,
                    new Rect(sx1, cy - 9, Math.Max(2, sx2 - sx1), 18), 3, 3);
            }
        }

        // Effects: captions (white) and speed sections (orange) as thin bars under the track.
        if (Duration > 0 && Effects is { Count: > 0 } effects)
        {
            foreach (var e in effects)
            {
                var ex1 = XFor(e.Start);
                var ex2 = XFor(e.End);
                var y = e.Speed ? cy + 11 : cy + 7;
                ctx.DrawRectangle(e.Speed ? SpeedBrush : CaptionBrush, null, new Rect(ex1, y, Math.Max(3, ex2 - ex1), 3), 1.5, 1.5);
            }
        }

        // Markers: bookmarks (amber) and loud moments (pink), as ticks above the track.
        if (Duration > 0 && Markers is { Count: > 0 } markers)
        {
            foreach (var m in markers)
            {
                var mx = XFor(m.Ms);
                var brush = m.Bookmark ? BookmarkBrush : HighlightBrush;
                ctx.DrawRectangle(brush, null, new Rect(mx - 1, cy - 12, 2, 8), 1, 1);
                ctx.DrawEllipse(brush, null, new Point(mx, cy - 13), 3, 3);
            }
        }

        // Trim range.
        bool hasIn = InPoint >= 0, hasOut = OutPoint >= 0;
        if (Duration > 0 && (hasIn || hasOut))
        {
            var x1 = XFor(hasIn ? InPoint : 0);
            var x2 = XFor(hasOut ? OutPoint : Duration);
            var box = new Rect(x1, cy - 13, Math.Max(0, x2 - x1), 26);
            ctx.DrawRectangle(new SolidColorBrush(rangeColor, 0.14), new Pen(new SolidColorBrush(rangeColor, 0.5), 1), box, 4, 4);
            if (hasIn) DrawHandle(ctx, x1, cy, range, left: true);
            if (hasOut) DrawHandle(ctx, x2, cy, range, left: false);
        }

        // Played portion + playhead.
        var pos = _drag == Drag.Seek && _dragPos >= 0 ? _dragPos : Position;
        var px = XFor(pos);
        ctx.DrawRectangle(accent, null, new Rect(Pad, cy - TrackH / 2, Math.Max(0, px - Pad), TrackH), 3, 3);
        ctx.DrawEllipse(Brushes.White, new Pen(accent, 2), new Point(px, cy), 7, 7);

        // Hover time tooltip.
        if (_hoverX is { } hx && Duration > 0)
        {
            var text = new FormattedText(FormatTime(MsFor(hx)), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold), 11, Brushes.White);
            var bx = Math.Clamp(hx - text.Width / 2 - 6, 0, Math.Max(0, w - text.Width - 12));
            var box = new Rect(bx, 0, text.Width + 12, text.Height + 4);
            ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(230, 24, 24, 30)), null, box, 4, 4);
            ctx.DrawText(text, new Point(bx + 6, 2));
        }
    }

    private static void DrawHandle(DrawingContext ctx, double x, double cy, IBrush brush, bool left)
    {
        // A "[" or "]" bracket.
        var pen = new Pen(brush, 3, lineCap: PenLineCap.Round);
        var dir = left ? 1 : -1;
        ctx.DrawLine(pen, new Point(x, cy - 13), new Point(x, cy + 13));
        ctx.DrawLine(pen, new Point(x, cy - 13), new Point(x + 5 * dir, cy - 13));
        ctx.DrawLine(pen, new Point(x, cy + 13), new Point(x + 5 * dir, cy + 13));
    }

    public static string FormatTime(double ms)
    {
        if (ms < 0) ms = 0;
        var t = TimeSpan.FromMilliseconds(ms);
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }
}
