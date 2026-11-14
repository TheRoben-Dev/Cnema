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

    private WriteableBitmap? _bitmap;
    private int _pending;
    private long _presentedId = -1;

    static VideoSurface()
    {
        AffectsRender<VideoSurface>(FramesProperty);
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
        var scale = Math.Min(bounds.Width / src.Width, bounds.Height / src.Height);
        var w = src.Width * scale;
        var h = src.Height * scale;
        var dest = new Rect((bounds.Width - w) / 2, (bounds.Height - h) / 2, w, h);
        context.DrawImage(_bitmap, new Rect(src), dest);
    }
}
