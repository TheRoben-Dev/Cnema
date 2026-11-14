using Avalonia.Input;
using Cnema.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cnema.ViewModels;

public sealed partial class ShortcutRow : ObservableObject
{
    public string? Id { get; init; }
    public string Label { get; init; } = "";
    public bool IsFixed => Id is null;
    [ObservableProperty] private string _keys = "";
    [ObservableProperty] private bool _isCapturing;
    [ObservableProperty] private bool _isCustom;
}

public sealed record ShortcutGroup(string Name, IReadOnlyList<ShortcutRow> Rows);

/// <summary>The "?" sheet: every shortcut, and clicking one to give it a different key.</summary>
public sealed partial class ShortcutsViewModel : ObservableObject
{
    private readonly Action _save;
    public IReadOnlyList<ShortcutGroup> Groups { get; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsCapturing))] private ShortcutRow? _capturing;
    [ObservableProperty] private string? _message;
    public bool IsCapturing => Capturing is not null;

    public ShortcutsViewModel(Action save)
    {
        _save = save;
        var groups = new List<ShortcutGroup>();
        foreach (var name in Shortcuts.Fixed.Select(f => f.Group).Distinct())
            groups.Add(new ShortcutGroup(T(name), Shortcuts.Fixed.Where(f => f.Group == name)
                .Select(f => new ShortcutRow { Label = T(f.Label), Keys = f.Keys }).ToList()));
        foreach (var name in new[] { Shortcuts.Library, Shortcuts.Player })
            groups.Add(new ShortcutGroup(T(name), Shortcuts.All.Where(a => a.Group == name)
                .Select(a => new ShortcutRow { Id = a.Id, Label = T(a.Label) }).ToList()));
        Groups = groups;
        Refresh();
    }

    private void Refresh()
    {
        foreach (var row in Groups.SelectMany(g => g.Rows).Where(r => r.Id is not null))
        {
            row.Keys = Shortcuts.Display(row.Id!);
            row.IsCustom = Shortcuts.IsCustom(row.Id!);
        }
    }

    [RelayCommand]
    private void Capture(ShortcutRow? row)
    {
        if (row?.Id is null) return;
        if (Capturing is { } old) old.IsCapturing = false;
        Capturing = row;
        row.IsCapturing = true;
        row.Keys = T("Press a key…");
        Message = F("Press the new key for \"{0}\". Esc cancels, Backspace removes the key.", row.Label);
    }

    /// <summary>While waiting for a key, every key press comes here first. Returns true when used.</summary>
    public bool HandleKey(KeyEventArgs e)
    {
        if (Capturing is not { Id: { } id } row) return false;
        if (Shortcuts.IsModifierOnly(e.Key)) return true; // wait for the real key
        row.IsCapturing = false;
        Capturing = null;
        if (e.Key == Key.Escape)
        {
            Message = null;
        }
        else if (e.Key is Key.Back or Key.Delete && e.KeyModifiers == KeyModifiers.None)
        {
            Shortcuts.Unbind(id);
            Message = F("\"{0}\" has no key now.", row.Label);
            _save();
        }
        else
        {
            var bumped = Shortcuts.Assign(id, new KeyGesture(e.Key, e.KeyModifiers));
            Message = bumped is null ? null : F("That key was used for \"{0}\", which has no key now.", bumped);
            _save();
        }
        Refresh();
        return true;
    }

    [RelayCommand]
    private void Reset(ShortcutRow? row)
    {
        if (row?.Id is null) return;
        Shortcuts.Reset(row.Id);
        _save();
        Refresh();
    }

    [RelayCommand]
    private void ResetAll()
    {
        Shortcuts.ResetAll();
        Message = T("All shortcuts are back to their defaults.");
        _save();
        Refresh();
    }
}
