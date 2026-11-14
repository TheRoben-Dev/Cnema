using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;

namespace Cnema.Services;

/// <summary>
/// Translated text for XAML: write the English as usual, but through these attached properties, e.g.
/// <c>l:Tr.Text="Add folder"</c> or <c>l:Tr.Tip="Settings"</c>. The real property gets the translation
/// and is updated again when the language changes in Settings.
/// </summary>
public sealed class Tr : AvaloniaObject
{
    public static readonly AttachedProperty<string?> TextProperty =
        AvaloniaProperty.RegisterAttached<Tr, AvaloniaObject, string?>("Text");
    public static readonly AttachedProperty<string?> ContentProperty =
        AvaloniaProperty.RegisterAttached<Tr, AvaloniaObject, string?>("Content");
    public static readonly AttachedProperty<string?> HeaderProperty =
        AvaloniaProperty.RegisterAttached<Tr, AvaloniaObject, string?>("Header");
    public static readonly AttachedProperty<string?> WatermarkProperty =
        AvaloniaProperty.RegisterAttached<Tr, AvaloniaObject, string?>("Watermark");
    public static readonly AttachedProperty<string?> TitleProperty =
        AvaloniaProperty.RegisterAttached<Tr, AvaloniaObject, string?>("Title");
    public static readonly AttachedProperty<string?> OnContentProperty =
        AvaloniaProperty.RegisterAttached<Tr, AvaloniaObject, string?>("OnContent");
    public static readonly AttachedProperty<string?> OffContentProperty =
        AvaloniaProperty.RegisterAttached<Tr, AvaloniaObject, string?>("OffContent");
    public static readonly AttachedProperty<string?> TipProperty =
        AvaloniaProperty.RegisterAttached<Tr, AvaloniaObject, string?>("Tip");
    public static readonly AttachedProperty<string?> NameProperty =
        AvaloniaProperty.RegisterAttached<Tr, AvaloniaObject, string?>("Name");

    public static string? GetText(AvaloniaObject o) => o.GetValue(TextProperty);
    public static void SetText(AvaloniaObject o, string? v) => o.SetValue(TextProperty, v);
    public static string? GetContent(AvaloniaObject o) => o.GetValue(ContentProperty);
    public static void SetContent(AvaloniaObject o, string? v) => o.SetValue(ContentProperty, v);
    public static string? GetHeader(AvaloniaObject o) => o.GetValue(HeaderProperty);
    public static void SetHeader(AvaloniaObject o, string? v) => o.SetValue(HeaderProperty, v);
    public static string? GetWatermark(AvaloniaObject o) => o.GetValue(WatermarkProperty);
    public static void SetWatermark(AvaloniaObject o, string? v) => o.SetValue(WatermarkProperty, v);
    public static string? GetTitle(AvaloniaObject o) => o.GetValue(TitleProperty);
    public static void SetTitle(AvaloniaObject o, string? v) => o.SetValue(TitleProperty, v);
    public static string? GetOnContent(AvaloniaObject o) => o.GetValue(OnContentProperty);
    public static void SetOnContent(AvaloniaObject o, string? v) => o.SetValue(OnContentProperty, v);
    public static string? GetOffContent(AvaloniaObject o) => o.GetValue(OffContentProperty);
    public static void SetOffContent(AvaloniaObject o, string? v) => o.SetValue(OffContentProperty, v);
    public static string? GetTip(AvaloniaObject o) => o.GetValue(TipProperty);
    public static void SetTip(AvaloniaObject o, string? v) => o.SetValue(TipProperty, v);
    public static string? GetName(AvaloniaObject o) => o.GetValue(NameProperty);
    public static void SetName(AvaloniaObject o, string? v) => o.SetValue(NameProperty, v);

    private static readonly List<WeakReference<AvaloniaObject>> Targets = new();
    private static readonly AttachedProperty<string?>[] All;

    static Tr()
    {
        All = new[] { TextProperty, ContentProperty, HeaderProperty, WatermarkProperty, TitleProperty,
            OnContentProperty, OffContentProperty, TipProperty, NameProperty };
        foreach (var p in All)
            p.Changed.AddClassHandler<AvaloniaObject>((o, e) => Changed(o, p, e.NewValue as string));
        Loc.Changed += Refresh;
    }

    private static void Changed(AvaloniaObject o, AttachedProperty<string?> source, string? english)
    {
        Apply(o, source, english);
        lock (Targets)
        {
            if (Targets.Count > 256 && Targets.Count % 256 == 0)
                Targets.RemoveAll(w => !w.TryGetTarget(out _));
            Targets.Add(new WeakReference<AvaloniaObject>(o));
        }
    }

    private static void Apply(AvaloniaObject o, AttachedProperty<string?> source, string? english)
    {
        var target = TargetOf(o, source);
        if (target is null) return;
        o.SetValue(target, english is null ? null : Loc.T(english));
    }

    private static AvaloniaProperty? TargetOf(AvaloniaObject o, AttachedProperty<string?> source)
    {
        if (source == TipProperty) return ToolTip.TipProperty;
        if (source == NameProperty) return AutomationProperties.NameProperty;
        return AvaloniaPropertyRegistry.Instance.FindRegistered(o, source.Name);
    }

    private static void Refresh()
    {
        List<AvaloniaObject> alive = new();
        lock (Targets)
        {
            Targets.RemoveAll(w => !w.TryGetTarget(out _));
            foreach (var w in Targets) if (w.TryGetTarget(out var o)) alive.Add(o);
        }
        foreach (var o in alive.Distinct())
            foreach (var p in All)
                if (o.IsSet(p) || o.GetValue(p) is not null) Apply(o, p, o.GetValue(p));
    }
}
