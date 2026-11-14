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

    private WriteableBitmap? _bitmap;
    private int _pending;
    private long _presentedId = -1;

    static VideoSurface()
    {
        AffectsRender<VideoSurface>(FramesProperty, SourceWidthProperty, SourceHeightProperty);
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

    private void OnCleared() => Dispatcher.UIThread.Post(Clear);

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
        context.DrawImage(_bitmap, new Rect(src), dest);
    }
}
