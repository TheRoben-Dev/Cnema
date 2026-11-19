using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cnema.ViewModels;

/// <summary>One button of a small "pick one" row, like Top / Middle / Bottom.</summary>
public sealed partial class OptionChip : ObservableObject
{
    private readonly ChipGroup _group;
    private readonly string _label;
    private readonly string? _tip;
    /// <summary>Translated when read, so the chips follow a language change.</summary>
    public string Label => T(_label);
    public string? Tip => _tip is null ? null : T(_tip);
    public object Value { get; }
    [ObservableProperty] private bool _isOn;

    internal OptionChip(ChipGroup group, string label, object value, string? tip)
    {
        _group = group;
        _label = label;
        Value = value;
        _tip = tip;
    }

    [RelayCommand]
    private void Pick() => _group.Value = Value;
}

/// <summary>A row of chips with one picked. Bind ItemsSource to <see cref="Chips"/>.</summary>
public sealed partial class ChipGroup : ObservableObject
{
    public IReadOnlyList<OptionChip> Chips { get; }
    [ObservableProperty] private object _value;

    public ChipGroup(object initial, params (string Label, object Value)[] options)
        : this(initial, options.Select(o => (o.Label, o.Value, (string?)null)).ToArray()) { }

    public ChipGroup(object initial, params (string Label, object Value, string? Tip)[] options)
    {
        Chips = options.Select(o => new OptionChip(this, o.Label, o.Value, o.Tip)).ToList();
        _value = initial;
        Mark();
    }

    partial void OnValueChanged(object value) => Mark();

    private void Mark()
    {
        foreach (var c in Chips) c.IsOn = Equals(c.Value, Value);
    }
}

/// <summary>A fixed list entry (sort order, filter, tab) whose text follows the language setting live.</summary>
public sealed class LocalizedText : ObservableObject
{
    public string English { get; }
    public string Text => T(English);

    public LocalizedText(string english)
    {
        English = english;
        // These lists live as long as the app, so a plain subscription is fine.
        Cnema.Services.Loc.Changed += () => OnPropertyChanged(nameof(Text));
    }

    public override string ToString() => Text;

    public static LocalizedText[] List(params string[] english) => english.Select(e => new LocalizedText(e)).ToArray();
}

/// <summary>A page in Settings: its name and its icon in the sidebar.</summary>
public sealed class SettingsPage
{
    public LocalizedText Name { get; }
    public Avalonia.Media.Geometry? Icon { get; }

    public SettingsPage(LocalizedText name, string icon)
    {
        Name = name;
        Icon = Avalonia.Application.Current is { } app && Avalonia.Controls.ResourceNodeExtensions.TryFindResource(app, icon, out var g) ? g as Avalonia.Media.Geometry : null;
    }
}
