using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace Cnema.Controls;

/// <summary>
/// One shared highlight that glides from card to card instead of each card lighting up on its own, like a
/// console menu cursor: a flat accent tint and a crisp accent border round the card under the pointer (or the
/// one with keyboard focus). It follows its card every frame while it moves, so it stays attached while the
/// grid scrolls, and it is clipped to the list the card is in. With Reduced motion it jumps instead of gliding.
/// Lives in an overlay over the library and never takes a click.
/// </summary>
public sealed class GlideRing : Control
{
    public static readonly StyledProperty<bool> SnapProperty = AvaloniaProperty.Register<GlideRing, bool>(nameof(Snap));
    public static readonly StyledProperty<IBrush?> AccentProperty = AvaloniaProperty.Register<GlideRing, IBrush?>(nameof(Accent));

    /// <summary>Jump straight to the card (Reduced motion).</summary>
    public bool Snap { get => GetValue(SnapProperty); set => SetValue(SnapProperty, value); }
    /// <summary>The ring colour; the theme's Accent when unset.</summary>
    public IBrush? Accent { get => GetValue(AccentProperty); set => SetValue(AccentProperty, value); }

    /// <summary>Card classes the ring goes to.</summary>
    private const string CardClass = "card";

    private Control? _target;
    private Rect _shown;
    private Rect _clip;
    private double _alpha;
    private double _press;
    private bool _ticking;
    private TimeSpan? _last;
    private TopLevel? _top;

    public GlideRing()
    {
        IsHitTestVisible = false;
        ClipToBounds = false;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _top = TopLevel.GetTopLevel(this);
        if (_top is null) return;
        _top.AddHandler(PointerMovedEvent, OnPointer, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        _top.AddHandler(PointerExitedEvent, OnLeft, Avalonia.Interactivity.RoutingStrategies.Direct, handledEventsToo: true);
        _top.AddHandler(PointerPressedEvent, OnPressed, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        _top.AddHandler(GotFocusEvent, OnFocus, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_top is null) return;
        _top.RemoveHandler(PointerMovedEvent, OnPointer);
        _top.RemoveHandler(PointerExitedEvent, OnLeft);
        _top.RemoveHandler(PointerPressedEvent, OnPressed);
        _top.RemoveHandler(GotFocusEvent, OnFocus);
        _top = null;
    }

    private static Control? CardOf(object? source)
    {
        for (var v = source as Visual; v is not null; v = v.GetVisualParent())
        {
            if (v is Button b && b.Classes.Contains(CardClass)) return b;
        }
        return null;
    }

    private void OnPointer(object? sender, PointerEventArgs e)
    {
        if (!IsEffectivelyVisible) return;
        // What's under the pointer, not the event's source (a card's child may have captured it).
        var hit = _top?.InputHitTest(e.GetPosition(_top)) as Visual;
        MoveTo(CardOf(hit));
    }

    private void OnLeft(object? sender, PointerEventArgs e) => MoveTo(null);

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_target is not null && CardOf(e.Source) == _target)
        {
            _press = 1;
            Kick();
        }
    }

    private void OnFocus(object? sender, GotFocusEventArgs e)
    {
        if (e.NavigationMethod is NavigationMethod.Tab or NavigationMethod.Directional && CardOf(e.Source) is { } card)
            MoveTo(card);
    }

    private void MoveTo(Control? card)
    {
        if (card == _target) return;
        // Arriving from nowhere: start right on the card (fading in), don't fly in from the last place.
        if (_target is null && card is not null && Measure(card) is { } r) _shown = r.Rect;
        _target = card;
        Kick();
    }

    /// <summary>The card's rectangle in this control's space, and the visible part of the list it sits in.</summary>
    private (Rect Rect, Rect Clip)? Measure(Control card)
    {
        if (!card.IsEffectivelyVisible || card.GetVisualRoot() is null) return null;
        var tl = card.TranslatePoint(default, this);
        if (tl is null) return null;
        var rect = new Rect(tl.Value, card.Bounds.Size);
        var clip = new Rect(Bounds.Size);
        if (card.FindAncestorOfType<ScrollViewer>() is { } sv && sv.TranslatePoint(default, this) is { } o)
            clip = clip.Intersect(new Rect(o, sv.Bounds.Size));
        return (rect, clip);
    }

    private void Kick()
    {
        if (_ticking || _top is null) return;
        _ticking = true;
        _last = null;
        _top.RequestAnimationFrame(Tick);
    }

    private void Tick(TimeSpan now)
    {
        var dt = _last is { } l ? Math.Clamp((now - l).TotalSeconds, 0, 0.05) : 1 / 60.0;
        _last = now;
        var wantAlpha = 0.0;
        if (_target is not null && Measure(_target) is { } m)
        {
            wantAlpha = 1;
            _clip = m.Clip;
            if (Snap) _shown = m.Rect;
            else
            {
                // Critically damped-ish ease: quick to set off, soft on arrival.
                var k = 1 - Math.Exp(-dt * 22);
                _shown = new Rect(
                    _shown.X + (m.Rect.X - _shown.X) * k, _shown.Y + (m.Rect.Y - _shown.Y) * k,
                    _shown.Width + (m.Rect.Width - _shown.Width) * k, _shown.Height + (m.Rect.Height - _shown.Height) * k);
            }
        }
        else if (_target is not null) _target = null;

        _alpha += (wantAlpha - _alpha) * (Snap ? 1 : 1 - Math.Exp(-dt * 16));
        _press = Math.Max(0, _press - dt * 5);
        InvalidateVisual();

        var settled = _target is null ? _alpha < 0.01 : false;
        if (settled)
        {
            _alpha = 0;
            _ticking = false;
            return;
        }
        // Keep following the card while it is shown (it may scroll or grow); a frame is cheap.
        _top?.RequestAnimationFrame(Tick);
    }

    public override void Render(DrawingContext ctx)
    {
        if (_alpha <= 0.01 || _shown.Width < 2) return;
        var accent = Accent ?? (this.TryFindResource("Accent", ActualThemeVariant, out var v) && v is IBrush b ? b : Brushes.MediumPurple);
        var color = accent is ISolidColorBrush s ? s.Color : Colors.MediumPurple;
        // A quick squeeze on press, so a click has a visible answer.
        var inset = _press * 3;
        var r = _shown.Deflate(inset - 1.5);
        using (ctx.PushClip(_clip))
        using (ctx.PushOpacity(_alpha))
        {
            var fill = new SolidColorBrush(color, 0.10);
            ctx.DrawRectangle(fill, new Pen(new SolidColorBrush(color), 3), new RoundedRect(r, 12));
        }
    }
}
