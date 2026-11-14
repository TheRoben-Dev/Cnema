using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Cnema.Controls;

/// <summary>A rectangle on the picture that can be moved and resized, in shares of the picture (0..1).</summary>
public sealed record FrameRegion(object Tag, Rect Area, bool Selected, bool KeepShape, string? Label);

/// <summary>
/// Draws the Blur and Zoom pages' rectangles over the letterboxed picture and lets the user drag them:
/// grab inside to move, grab a corner to resize. Zoom frames keep the picture's shape.
/// </summary>
public sealed class FrameEditor : Control, Avalonia.Rendering.ICustomHitTest
{
    public static readonly StyledProperty<IReadOnlyList<FrameRegion>?> RegionsProperty =
        AvaloniaProperty.Register<FrameEditor, IReadOnlyList<FrameRegion>?>(nameof(Regions));
    public static readonly StyledProperty<int> VideoWidthProperty =
        AvaloniaProperty.Register<FrameEditor, int>(nameof(VideoWidth));
    public static readonly StyledProperty<int> VideoHeightProperty =
        AvaloniaProperty.Register<FrameEditor, int>(nameof(VideoHeight));

    private const double Handle = 9;
    private FrameRegion? _drag;
    private int _corner = -1; // -1 = move, 0..3 = TL, TR, BL, BR
    private Point _start;
    private Rect _startArea;

    /// <summary>A region was moved or resized: (its tag, the new area).</summary>
    public event Action<object, Rect>? RegionChanged;
    /// <summary>A region was clicked (to select it).</summary>
    public event Action<object>? RegionPicked;

    static FrameEditor()
    {
        AffectsRender<FrameEditor>(RegionsProperty, VideoWidthProperty, VideoHeightProperty);
    }

    public FrameEditor()
    {
        Cursor = new Cursor(StandardCursorType.Arrow);
    }

    public IReadOnlyList<FrameRegion>? Regions { get => GetValue(RegionsProperty); set => SetValue(RegionsProperty, value); }
    public int VideoWidth { get => GetValue(VideoWidthProperty); set => SetValue(VideoWidthProperty, value); }
    public int VideoHeight { get => GetValue(VideoHeightProperty); set => SetValue(VideoHeightProperty, value); }

    /// <summary>Only the rectangles (and their handles) take the pointer; clicks elsewhere reach the video below.</summary>
    public bool HitTest(Point point)
    {
        if (_drag is not null) return true;
        if (Regions is not { Count: > 0 } regions) return false;
        var pic = Picture();
        return regions.Any(r => ToView(r.Area, pic).Inflate(Handle).Contains(point));
    }

    private Rect Picture()
    {
        var bounds = new Rect(Bounds.Size);
        if (VideoWidth <= 0 || VideoHeight <= 0) return bounds;
        var scale = Math.Min(bounds.Width / VideoWidth, bounds.Height / VideoHeight);
        var w = VideoWidth * scale;
        var h = VideoHeight * scale;
        return new Rect((bounds.Width - w) / 2, (bounds.Height - h) / 2, w, h);
    }

    private static Rect ToView(Rect area, Rect pic) =>
        new(pic.X + area.X * pic.Width, pic.Y + area.Y * pic.Height, area.Width * pic.Width, area.Height * pic.Height);

    public override void Render(DrawingContext ctx)
    {
        if (Regions is not { Count: > 0 } regions) return;
        var pic = Picture();
        var accent = this.TryFindResource("Brand", ActualThemeVariant, out var r) && r is IBrush b ? b : Brushes.White;
        var dim = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0));
        foreach (var reg in regions)
        {
            var rect = ToView(reg.Area, pic);
            if (reg.KeepShape)
            {
                // Zoom frame: darken what falls outside the view.
                var outside = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(pic), new RectangleGeometry(rect));
                ctx.DrawGeometry(dim, null, outside);
            }
            var pen = new Pen(reg.Selected ? accent : Brushes.White, reg.Selected ? 2 : 1.2, reg.Selected ? null : new DashStyle(new double[] { 4, 3 }, 0));
            ctx.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(120, 0, 0, 0)), 3.5), rect);
            ctx.DrawRectangle(null, pen, rect);
            if (reg.Selected)
                foreach (var c in Corners(rect))
                    ctx.DrawRectangle(Brushes.White, new Pen(accent, 1.5), new Rect(c.X - Handle / 2, c.Y - Handle / 2, Handle, Handle), 2, 2);
            if (reg.Label is { Length: > 0 } label)
            {
                var ft = new FormattedText(label, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                    Typeface.Default, 11, Brushes.White);
                var at = new Point(rect.X + 6, rect.Y + 5);
                ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(170, 0, 0, 0)), null,
                    new Rect(at.X - 4, at.Y - 2, ft.Width + 8, ft.Height + 4), 4, 4);
                ctx.DrawText(ft, at);
            }
        }
    }

    private static Point[] Corners(Rect r) => new[] { r.TopLeft, r.TopRight, r.BottomLeft, r.BottomRight };

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Regions is not { Count: > 0 } regions || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var pic = Picture();
        var p = e.GetPosition(this);
        // Selected first, so its handles win over a region underneath.
        foreach (var reg in regions.OrderByDescending(r => r.Selected))
        {
            var rect = ToView(reg.Area, pic);
            var corners = Corners(rect);
            var corner = Array.FindIndex(corners, c => Math.Abs(c.X - p.X) <= Handle && Math.Abs(c.Y - p.Y) <= Handle);
            if (corner < 0 && !rect.Inflate(2).Contains(p)) continue;
            _drag = reg;
            _corner = reg.Selected ? corner : -1;
            _start = p;
            _startArea = reg.Area;
            if (!reg.Selected) RegionPicked?.Invoke(reg.Tag);
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var pic = Picture();
        var p = e.GetPosition(this);
        if (_drag is null)
        {
            UpdateCursor(p, pic);
            return;
        }
        var dx = (p.X - _start.X) / Math.Max(1, pic.Width);
        var dy = (p.Y - _start.Y) / Math.Max(1, pic.Height);
        var a = _startArea;
        Rect next;
        if (_corner < 0)
        {
            var x = Math.Clamp(a.X + dx, 0, 1 - a.Width);
            var y = Math.Clamp(a.Y + dy, 0, 1 - a.Height);
            next = new Rect(x, y, a.Width, a.Height);
        }
        else
        {
            double l = a.Left, t = a.Top, r = a.Right, btm = a.Bottom;
            if (_corner is 0 or 2) l += dx; else r += dx;
            if (_corner is 0 or 1) t += dy; else btm += dy;
            if (_drag.KeepShape)
            {
                // Square in picture shares = the picture's own shape. Follow the bigger change.
                var size = Math.Max(r - l, btm - t);
                size = Math.Clamp(size, 1 / Cnema.Services.ZoomView.MaxZoom, 1);
                if (_corner is 0 or 2) l = r - size; else r = l + size;
                if (_corner is 0 or 1) t = btm - size; else btm = t + size;
                var sx = Math.Clamp(l, 0, 1 - size) - l;
                var sy = Math.Clamp(t, 0, 1 - size) - t;
                l += sx; r += sx; t += sy; btm += sy;
            }
            l = Math.Clamp(l, 0, 1); r = Math.Clamp(r, 0, 1); t = Math.Clamp(t, 0, 1); btm = Math.Clamp(btm, 0, 1);
            if (r - l < 0.02 || btm - t < 0.02) return;
            next = new Rect(l, t, r - l, btm - t);
        }
        RegionChanged?.Invoke(_drag.Tag, next);
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_drag is null) return;
        _drag = null;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void UpdateCursor(Point p, Rect pic)
    {
        var type = StandardCursorType.Arrow;
        foreach (var reg in Regions ?? Array.Empty<FrameRegion>())
        {
            var rect = ToView(reg.Area, pic);
            var corner = Array.FindIndex(Corners(rect), c => Math.Abs(c.X - p.X) <= Handle && Math.Abs(c.Y - p.Y) <= Handle);
            if (reg.Selected && corner >= 0) { type = corner is 0 or 3 ? StandardCursorType.TopLeftCorner : StandardCursorType.TopRightCorner; break; }
            if (rect.Contains(p)) type = StandardCursorType.SizeAll;
        }
        Cursor = new Cursor(type);
    }
}
