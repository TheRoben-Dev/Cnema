using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Cnema.Controls;

/// <summary>
/// The library grid's layout: equal columns, every card the same size, and only the rows on screen are
/// built. Unlike UniformGridLayout it never sizes cards from a card that isn't on screen, so the grid can't
/// deform after scrolling far down, and it never builds and throws away card 0 on every pass.
/// </summary>
public sealed class CardGridLayout : VirtualizingLayout
{
    public static readonly StyledProperty<double> MinItemWidthProperty =
        AvaloniaProperty.Register<CardGridLayout, double>(nameof(MinItemWidth), 250);
    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<CardGridLayout, double>(nameof(Spacing), 6);

    public double MinItemWidth { get => GetValue(MinItemWidthProperty); set => SetValue(MinItemWidthProperty, value); }
    public double Spacing { get => GetValue(SpacingProperty); set => SetValue(SpacingProperty, value); }

    private sealed class State
    {
        public readonly Dictionary<int, Layoutable> Realized = new();
        public double ItemWidth, ItemHeight;
        public int Columns = 1;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == MinItemWidthProperty || change.Property == SpacingProperty) InvalidateMeasure();
    }

    protected override void InitializeForContextCore(VirtualizingLayoutContext context) => context.LayoutState = new State();

    protected override void UninitializeForContextCore(VirtualizingLayoutContext context) => context.LayoutState = null;

    protected override void OnItemsChangedCore(VirtualizingLayoutContext context, object? source, NotifyCollectionChangedEventArgs args)
    {
        if (context.LayoutState is State st)
        {
            // Hand every card back and lay out afresh: a card kept by index would show the old video.
            foreach (var el in st.Realized.Values)
                try { context.RecycleElement(el); } catch { /* already taken back */ }
            st.Realized.Clear();
        }
        base.OnItemsChangedCore(context, source, args);
    }

    protected override Size MeasureOverride(VirtualizingLayoutContext context, Size availableSize)
    {
        var st = context.LayoutState as State ?? (State)(context.LayoutState = new State());
        var width = double.IsFinite(availableSize.Width) && availableSize.Width > 0 ? availableSize.Width : 1000;
        var gap = Spacing;
        var cols = Math.Max(1, (int)((width + gap) / (MinItemWidth + gap)));
        var itemW = Math.Floor((width - gap * (cols - 1)) / cols);
        if (cols != st.Columns || Math.Abs(itemW - st.ItemWidth) > 0.5)
        {
            // Another column count moves every card: start again from what's on screen.
            st.ItemHeight = 0;
            if (cols != st.Columns) RecycleAll(context, st);
        }
        st.Columns = cols;
        st.ItemWidth = itemW;

        var count = context.ItemCount;
        if (count == 0)
        {
            RecycleAll(context, st);
            return new Size(width, 0);
        }
        var rows = (count + cols - 1) / cols;
        var itemH = st.ItemHeight > 0 ? st.ItemHeight : itemW * 9 / 16 + 90;
        var rect = context.RealizationRect;

        // Twice at most: the first pass may learn the real card height from an estimate.
        for (var pass = 0; pass < 2; pass++)
        {
            var rowH = itemH + gap;
            var firstRow = Math.Clamp((int)Math.Floor(rect.Y / rowH), 0, rows - 1);
            var lastRow = Math.Clamp((int)Math.Floor(rect.Bottom / rowH), firstRow, rows - 1);
            var first = firstRow * cols;
            var last = Math.Min(count - 1, (lastRow + 1) * cols - 1);

            foreach (var i in st.Realized.Keys.Where(i => i < first || i > last || i >= count).ToList())
            {
                context.RecycleElement(st.Realized[i]);
                st.Realized.Remove(i);
            }
            var measured = 0.0;
            for (var i = first; i <= last; i++)
            {
                var el = context.GetOrCreateElementAt(i);
                st.Realized[i] = el;
                el.Measure(new Size(itemW, double.PositiveInfinity));
                measured = Math.Max(measured, el.DesiredSize.Height);
            }
            if (measured <= 0 || Math.Abs(measured - itemH) < 0.5) break;
            itemH = Math.Ceiling(measured);
        }
        st.ItemHeight = itemH;
        return new Size(width, rows * itemH + (rows - 1) * gap);
    }

    protected override Size ArrangeOverride(VirtualizingLayoutContext context, Size finalSize)
    {
        if (context.LayoutState is not State st) return finalSize;
        var gap = Spacing;
        foreach (var (i, el) in st.Realized)
        {
            var row = i / st.Columns;
            var col = i % st.Columns;
            el.Arrange(new Rect(col * (st.ItemWidth + gap), row * (st.ItemHeight + gap), st.ItemWidth, st.ItemHeight));
        }
        return finalSize;
    }

    private static void RecycleAll(VirtualizingLayoutContext context, State st)
    {
        foreach (var el in st.Realized.Values) context.RecycleElement(el);
        st.Realized.Clear();
    }
}

/// <summary>Sizes its children to a fixed aspect of the width it's given (16:9 by default), never from its own past bounds.</summary>
public sealed class AspectPanel : Panel
{
    public static readonly StyledProperty<double> RatioProperty =
        AvaloniaProperty.Register<AspectPanel, double>(nameof(Ratio), 9.0 / 16.0);

    public double Ratio { get => GetValue(RatioProperty); set => SetValue(RatioProperty, value); }

    static AspectPanel() => AffectsMeasure<AspectPanel>(RatioProperty);

    protected override Size MeasureOverride(Size availableSize)
    {
        var w = double.IsFinite(availableSize.Width) ? availableSize.Width : 250;
        var size = new Size(w, Math.Round(w * Ratio));
        foreach (var child in Children) child.Measure(size);
        return size;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var rect = new Rect(0, 0, finalSize.Width, Math.Round(finalSize.Width * Ratio));
        foreach (var child in Children) child.Arrange(rect);
        return new Size(finalSize.Width, rect.Height);
    }
}
