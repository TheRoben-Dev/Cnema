using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cnema.ViewModels;

/// <summary>Backs the "Edit details" panel: rename, move between library folders, tags, favourite.</summary>
public sealed partial class EditViewModel : ObservableObject
{
    public VideoItemViewModel Item { get; }
    public string Extension { get; }
    public ObservableCollection<string> Tags { get; }
    public ObservableCollection<string> Suggestions { get; } = new();
    public IReadOnlyList<FolderViewModel> Folders { get; }
    private readonly List<string> _allTags;

    [ObservableProperty] private string _name;
    [ObservableProperty] private string _tagInput = "";
    [ObservableProperty] private bool _favorite;
    [ObservableProperty] private FolderViewModel? _folder;
    [ObservableProperty] private string? _error;

    /// <summary>The library folder the file currently lives under (null if none matches).</summary>
    public FolderViewModel? OriginalFolder { get; }
    public bool CanMove => Folders.Count > 1;
    public string Location => Path.GetDirectoryName(Item.Entry.Path) ?? "";

    public EditViewModel(VideoItemViewModel item, IEnumerable<FolderViewModel> folders, IEnumerable<string> allTags)
    {
        Item = item;
        _name = item.Title;
        Extension = Path.GetExtension(item.Entry.Path);
        _favorite = item.Entry.Favorite;
        Tags = new ObservableCollection<string>(item.Entry.Tags);
        _allTags = allTags.ToList();
        Folders = folders.Where(f => f.Kind == NavKind.Folder).ToList();
        // Longest matching root wins when library folders are nested.
        OriginalFolder = Folders.Where(f => f.Contains(item.Entry.Path)).OrderByDescending(f => f.Path!.Length).FirstOrDefault();
        _folder = OriginalFolder;
        RefreshSuggestions();
    }

    partial void OnTagInputChanged(string value)
    {
        // Typing a comma commits the tag, like most tag editors. Deferred: clearing the text from
        // inside its own change notification would be ignored by the TextBox mid-edit.
        if (value.Contains(',')) Avalonia.Threading.Dispatcher.UIThread.Post(AddTag);
        else RefreshSuggestions();
    }

    [RelayCommand]
    private void AddTag()
    {
        foreach (var raw in TagInput.Split(',', StringSplitOptions.RemoveEmptyEntries))
            AddOne(raw);
        TagInput = "";
        RefreshSuggestions();
    }

    [RelayCommand]
    private void AddSuggested(string? tag)
    {
        if (tag is null) return;
        AddOne(tag);
        TagInput = "";
        RefreshSuggestions();
    }

    [RelayCommand]
    private void RemoveTag(string? tag)
    {
        if (tag is null) return;
        Tags.Remove(tag);
        RefreshSuggestions();
    }

    private void AddOne(string raw)
    {
        var tag = raw.Trim().TrimStart('#').Trim();
        if (tag.Length == 0 || tag.Length > 40) return;
        if (Tags.Contains(tag, StringComparer.OrdinalIgnoreCase)) return;
        // Reuse the existing spelling so "Funny" and "funny" don't become two tags.
        Tags.Add(_allTags.FirstOrDefault(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase)) ?? tag);
    }

    private void RefreshSuggestions()
    {
        var typed = TagInput.Trim().TrimStart('#');
        var next = _allTags
            .Where(t => !Tags.Contains(t, StringComparer.OrdinalIgnoreCase))
            .Where(t => typed.Length == 0 || t.Contains(typed, StringComparison.OrdinalIgnoreCase))
            .Take(12).ToList();
        Suggestions.Clear();
        foreach (var t in next) Suggestions.Add(t);
        OnPropertyChanged(nameof(HasSuggestions));
    }

    public bool HasSuggestions => Suggestions.Count > 0;
}
