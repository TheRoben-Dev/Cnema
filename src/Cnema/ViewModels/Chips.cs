using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cnema.ViewModels;

/// <summary>One button of a small "pick one" row, like Top / Middle / Bottom.</summary>
public sealed partial class OptionChip : ObservableObject
{
    private readonly ChipGroup _group;
    public string Label { get; }
    public string? Tip { get; }
    public object Value { get; }
    [ObservableProperty] private bool _isOn;

    internal OptionChip(ChipGroup group, string label, object value, string? tip)
    {
        _group = group;
        Label = label;
        Value = value;
        Tip = tip;
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
