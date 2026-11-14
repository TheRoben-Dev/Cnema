using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Cnema.Services;

namespace Cnema.Controls;

/// <summary>
/// Draws the Effects panel's captions and watermark over the player, inside the letterboxed picture,
/// sized like the export will be (the same rules as CaptionWriter and RenderService).
/// </summary>
public sealed class EffectsOverlay : Control
{
    public static readonly StyledProperty<IReadOnlyList<CaptionSpec>?> CaptionsProperty =
        AvaloniaProperty.Register<EffectsOverlay, IReadOnlyList<CaptionSpec>?>(nameof(Captions));
    public static readonly StyledProperty<WatermarkSpec?> WatermarkProperty =
        AvaloniaProperty.Register<EffectsOverlay, WatermarkSpec?>(nameof(Watermark));
    public static readonly StyledProperty<int> VideoWidthProperty =
        AvaloniaProperty.Register<EffectsOverlay, int>(nameof(VideoWidth));
    public static readonly StyledProperty<int> VideoHeightProperty =
        AvaloniaProperty.Register<EffectsOverlay, int>(nameof(VideoHeight));

    // libass sizes text by the font's full height (ascent + descent); Inter's is about 1.21 em.
    private const double AssToEm = 1 / 1.21;
    private static readonly Typeface Face = new(new FontFamily("fonts:Inter#Inter"), FontStyle.Normal, FontWeight.Bold);
    private static readonly IBrush Shadow = new SolidColorBrush(Color.FromArgb(128, 0, 0, 0));
    private static readonly IBrush BoxFill = new SolidColorBrush(Color.FromArgb(0xB3, 0, 0, 0));

    private Bitmap? _mark;
    private string? _markPath;

    static EffectsOverlay()
    {
        AffectsRender<EffectsOverlay>(CaptionsProperty, WatermarkProperty, VideoWidthProperty, VideoHeightProperty);
        IsHitTestVisibleProperty.OverrideDefaultValue<EffectsOverlay>(false);
    }

    public IReadOnlyList<CaptionSpec>? Captions { get => GetValue(CaptionsProperty); set => SetValue(CaptionsProperty, value); }
    public WatermarkSpec? Watermark { get => GetValue(WatermarkProperty); set => SetValue(WatermarkProperty, value); }
    public int VideoWidth { get => GetValue(VideoWidthProperty); set => SetValue(VideoWidthProperty, value); }
    public int VideoHeight { get => GetValue(VideoHeightProperty); set => SetValue(VideoHeightProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WatermarkProperty) LoadMark();
    }

    private void LoadMark()
    {
        var path = Watermark?.Path;
        if (path == _markPath) return;
        _mark?.Dispose();
        _mark = null;
        _markPath = path;
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
        try { _mark = new Bitmap(path); }
        catch { /* not an image Avalonia can read; the export still tries */ }
    }

    public override void Render(DrawingContext ctx)
    {
        if (Bounds.Width < 10 || Bounds.Height < 10) return;
        var picture = PictureRect();

        if (Watermark is { } w && _mark is { } mark)
        {
            var width = picture.Width * Math.Clamp(w.Width, 0.02, 0.6);
            var height = width * mark.PixelSize.Height / Math.Max(1, mark.PixelSize.Width);
            var m = picture.Width * 0.025;
            var x = w.Corner is Corner.TopLeft or Corner.BottomLeft ? picture.X + m : picture.Right - m - width;
            var y = w.Corner is Corner.TopLeft or Corner.TopRight ? picture.Y + m : picture.Bottom - m - height;
            using (ctx.PushOpacity(Math.Clamp(w.Opacity, 0.05, 1)))
                ctx.DrawImage(mark, new Rect(x, y, width, height));
        }

        if (Captions is not { Count: > 0 } captions) return;
        var marginH = picture.Width * 0.05;
        var marginV = picture.Height * 0.06;
        // Captions at the same position stack, the way libass keeps them from overlapping.
        var used = new Dictionary<CaptionPosition, double>();
        foreach (var c in captions)
        {
            var text = c.Text.Replace("\r\n", "\n").Trim();
            if (text.Length == 0) continue;
            var assSize = picture.Height * CaptionWriter.SizeFactor(c.Size);
            var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, Math.Max(4, assSize * AssToEm), Brushes.White)
            {
                MaxTextWidth = Math.Max(10, picture.Width - 2 * marginH),
                TextAlignment = TextAlignment.Center,
            };
            var blockW = ft.MaxTextWidth;
            var h = ft.Height;
            used.TryGetValue(c.Position, out var offset);
            var top = c.Position switch
            {
                CaptionPosition.Top => picture.Y + marginV + offset,
                CaptionPosition.Middle => picture.Y + (picture.Height - h) / 2 + offset,
                _ => picture.Bottom - marginV - h - offset,
            };
            used[c.Position] = offset + h + 4;
            var origin = new Point(picture.X + marginH, top);

            if (c.Style == CaptionStyle.Box)
            {
                var pad = assSize * 0.22;
                var textW = Math.Min(blockW, ft.WidthIncludingTrailingWhitespace);
                var box = new Rect(origin.X + (blockW - textW) / 2 - pad, top - pad, textW + 2 * pad, h + 2 * pad);
                ctx.DrawRectangle(BoxFill, null, box);
                ctx.DrawText(ft, origin);
                continue;
            }
            var geo = ft.BuildGeometry(origin);
            if (geo is null) { ctx.DrawText(ft, origin); continue; }
            var outline = assSize * 0.07;
            var shadow = assSize * 0.035;
            using (ctx.PushTransform(Matrix.CreateTranslation(shadow, shadow)))
                ctx.DrawGeometry(Shadow, new Pen(Shadow, outline * 2, lineJoin: PenLineJoin.Round), geo);
            ctx.DrawGeometry(null, new Pen(Brushes.Black, outline * 2, lineJoin: PenLineJoin.Round), geo);
            ctx.DrawGeometry(Brushes.White, null, geo);
        }
    }

    /// <summary>Where the picture sits inside the control, letterboxed like VideoSurface draws it.</summary>
    private Rect PictureRect()
    {
        var bounds = new Rect(Bounds.Size);
        if (VideoWidth <= 0 || VideoHeight <= 0) return bounds;
        var scale = Math.Min(bounds.Width / VideoWidth, bounds.Height / VideoHeight);
        var w = VideoWidth * scale;
        var h = VideoHeight * scale;
        return new Rect((bounds.Width - w) / 2, (bounds.Height - h) / 2, w, h);
    }
}
