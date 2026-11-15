using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace Cnema.Controls;

/// <summary>Draws frames from a <see cref="VideoFrameBuffer"/>, letterboxed (uniform fit) on black.</summary>
public sealed class VideoSurface : Control
{
    public static readonly StyledProperty<VideoFrameBuffer?> FramesProperty =
        AvaloniaProperty.Register<VideoSurface, VideoFrameBuffer?>(nameof(Frames));
    /// <summary>The file's picture size, when known; see <see cref="Render"/>.</summary>
    public static readonly StyledProperty<int> SourceWidthProperty =
        AvaloniaProperty.Register<VideoSurface, int>(nameof(SourceWidth));
    public static readonly StyledProperty<int> SourceHeightProperty =
        AvaloniaProperty.Register<VideoSurface, int>(nameof(SourceHeight));
    /// <summary>Zoom and pan preview: the part of the picture to show.</summary>
    public static readonly StyledProperty<Cnema.Services.ZoomView> ViewProperty =
        AvaloniaProperty.Register<VideoSurface, Cnema.Services.ZoomView>(nameof(View), Cnema.Services.ZoomView.Whole);
    /// <summary>Areas hidden in the export, previewed blurred or pixelated.</summary>
    public static readonly StyledProperty<IReadOnlyList<Cnema.Services.BlurSpec>?> BlursProperty =
        AvaloniaProperty.Register<VideoSurface, IReadOnlyList<Cnema.Services.BlurSpec>?>(nameof(Blurs));

    private WriteableBitmap? _bitmap;
    private int _pending;
    private long _presentedId = -1;

    static VideoSurface()
    {
        AffectsRender<VideoSurface>(FramesProperty, SourceWidthProperty, SourceHeightProperty, ViewProperty, BlursProperty);
    }

    public VideoSurface()
    {
        RenderOptions.SetBitmapInterpolationMode(this, Avalonia.Media.Imaging.BitmapInterpolationMode.HighQuality);
    }

    public VideoFrameBuffer? Frames
    {
        get => GetValue(FramesProperty);
        set => SetValue(FramesProperty, value);
    }

    public int SourceWidth { get => GetValue(SourceWidthProperty); set => SetValue(SourceWidthProperty, value); }
    public int SourceHeight { get => GetValue(SourceHeightProperty); set => SetValue(SourceHeightProperty, value); }
    public Cnema.Services.ZoomView View { get => GetValue(ViewProperty); set => SetValue(ViewProperty, value); }
    public IReadOnlyList<Cnema.Services.BlurSpec>? Blurs { get => GetValue(BlursProperty); set => SetValue(BlursProperty, value); }
    private readonly List<WriteableBitmap> _cells = new();

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == FramesProperty)
        {
            if (change.OldValue is VideoFrameBuffer o) { o.FrameReady -= OnFrameReady; o.Cleared -= OnCleared; }
            if (change.NewValue is VideoFrameBuffer n) { n.FrameReady += OnFrameReady; n.Cleared += OnCleared; }
        }
    }

    /// <summary>Forget the last frame (e.g. when switching videos) so stale content isn't shown.</summary>
    public void Clear()
    {
        _bitmap?.Dispose();
        _bitmap = null;
        InvalidateVisual();
    }

    // Reset runs on the UI thread when a video is opened: clear right away, so a posted clear can't
    // land after (and wipe) the first frame of the new video.
    private void OnCleared()
    {
        if (Dispatcher.UIThread.CheckAccess()) Clear();
        else Dispatcher.UIThread.Post(Clear);
    }

    private void OnFrameReady()
    {
        // Coalesce: at most one pending UI post, so a slow UI thread just drops frames.
        if (Interlocked.Exchange(ref _pending, 1) == 1) return;
        Dispatcher.UIThread.Post(() =>
        {
            Volatile.Write(ref _pending, 0);
            // A hidden surface (the full player while the mini player shows) skips the copy.
            if (IsEffectivelyVisible) Refresh();
        }, DispatcherPriority.Render);
    }

    /// <summary>Draws the newest frame, e.g. right after this surface became visible on a paused video.</summary>
    public void Refresh()
    {
        if (Frames?.Present(ref _bitmap, ref _presentedId) == true) InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(Brushes.Black, bounds);
        if (_bitmap is null) return;

        var src = new Size(_bitmap.PixelSize.Width, _bitmap.PixelSize.Height);
        var aspect = src.Width / src.Height;
        // libVLC can hand over a frame a few lines taller than the picture (1280x738 for a 1280x720
        // file), scaled to fit. Drawn at the file's own shape, the picture isn't stretched and lines
        // up with the Effects overlay. A big difference (a rotated phone video) keeps the frame's shape.
        if (SourceWidth > 0 && SourceHeight > 0)
        {
            var known = (double)SourceWidth / SourceHeight;
            if (Math.Abs(known / aspect - 1) < 0.05) aspect = known;
        }
        var (w, h) = bounds.Width / bounds.Height > aspect
            ? (bounds.Height * aspect, bounds.Height)
            : (bounds.Width, bounds.Width / aspect);
        var dest = new Rect((bounds.Width - w) / 2, (bounds.Height - h) / 2, w, h);
        var (vl, vt, vw, vh) = View.Window;
        var view = new Rect(vl * src.Width, vt * src.Height, vw * src.Width, vh * src.Height);
        context.DrawImage(_bitmap, view, dest);
        if (Blurs is { Count: > 0 } blurs)
        {
            using (context.PushClip(dest))
                for (var i = 0; i < blurs.Count; i++) DrawBlur(context, blurs[i], i, src, view, dest);
        }
    }

    /// <summary>
    /// A cheap stand-in for the export's blur: the area sampled into a few cells, then drawn smoothly
    /// stretched (blur) or with hard edges (pixelate).
    /// </summary>
    private void DrawBlur(DrawingContext context, Cnema.Services.BlurSpec b, int index, Size src, Rect view, Rect dest)
    {
        if (_bitmap is null) return;
        var area = new Rect(b.X * src.Width, b.Y * src.Height, b.W * src.Width, b.H * src.Height).Intersect(new Rect(src));
        if (area.Width < 2 || area.Height < 2) return;
        var pixelate = b.Style == Cnema.Services.BlurStyle.Pixelate;
        var shortSide = pixelate ? 10 : 7;
        var (cw, ch) = area.Width >= area.Height
            ? ((int)Math.Clamp(Math.Round(shortSide * area.Width / area.Height), 1, 64), shortSide)
            : (shortSide, (int)Math.Clamp(Math.Round(shortSide * area.Height / area.Width), 1, 64));
        while (_cells.Count <= index) _cells.Add(null!);
        var cells = _cells[index];
        if (cells is null || cells.PixelSize.Width != cw || cells.PixelSize.Height != ch)
        {
            cells?.Dispose();
            cells = _cells[index] = new WriteableBitmap(new PixelSize(cw, ch), new Vector(96, 96),
                Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Premul);
        }
        using (var from = _bitmap.Lock())
        using (var to = cells.Lock())
        {
            unsafe
            {
                var fp = (byte*)from.Address;
                var tp = (byte*)to.Address;
                for (var y = 0; y < ch; y++)
                for (var x = 0; x < cw; x++)
                {
                    // Average a small cross of pixels around each cell's centre, so thin text doesn't flicker through.
                    int sb = 0, sg = 0, sr = 0, n = 0;
                    foreach (var (dx, dy) in new[] { (0.5, 0.5), (0.25, 0.25), (0.75, 0.25), (0.25, 0.75), (0.75, 0.75) })
                    {
                        var px = (int)Math.Clamp(area.X + (x + dx) * area.Width / cw, 0, from.Size.Width - 1);
                        var py = (int)Math.Clamp(area.Y + (y + dy) * area.Height / ch, 0, from.Size.Height - 1);
                        var p = fp + py * from.RowBytes + px * 4;
                        sb += p[0]; sg += p[1]; sr += p[2]; n++;
                    }
                    var q = tp + y * to.RowBytes + x * 4;
                    q[0] = (byte)(sb / n); q[1] = (byte)(sg / n); q[2] = (byte)(sr / n); q[3] = 255;
                }
            }
        }
        var sx = dest.Width / view.Width;
        var sy = dest.Height / view.Height;
        var target = new Rect(dest.X + (area.X - view.X) * sx, dest.Y + (area.Y - view.Y) * sy, area.Width * sx, area.Height * sy);
        if (!pixelate)
        {
            using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = Avalonia.Media.Imaging.BitmapInterpolationMode.HighQuality }))
                context.DrawImage(cells, new Rect(0, 0, cw, ch), target);
            return;
        }
        // Pixelate: hard-edged blocks, drawn one by one so no smoothing can soften them.
        using var blocks = cells.Lock();
        unsafe
        {
            var bp = (byte*)blocks.Address;
            var bw = target.Width / cw;
            var bh = target.Height / ch;
            for (var y = 0; y < ch; y++)
            for (var x = 0; x < cw; x++)
            {
                var q = bp + y * blocks.RowBytes + x * 4;
                var brush = new SolidColorBrush(Color.FromRgb(q[2], q[1], q[0]));
                context.FillRectangle(brush, new Rect(target.X + x * bw, target.Y + y * bh, bw + 0.5, bh + 0.5));
            }
        }
    }
}
