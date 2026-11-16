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

    /// <summary>Smart titles (when on): a name from the game tag, who was there and the date.</summary>
    public Func<Cnema.Models.VideoEntry, string?>? Suggest { get; init; }
    public bool CanSuggest => Suggest is not null;
    /// <summary>A title shown in Savefile (Smart rename): the box edits it and the file keeps its name.</summary>
    public bool HasTitle => Item.Entry.Title is not null;
    public string NameLabel => HasTitle ? T("NAME") : T("FILE NAME");
    public string NameHint => HasTitle ? T("The name shown in Savefile. The file keeps its own name; clear the box to show it.") : T("Renames the actual file on disk.");
    public bool ShowExtension => !HasTitle;

    [RelayCommand]
    private void SuggestName()
    {
        if (Suggest is null) return;
        var e = new Cnema.Models.VideoEntry
        {
            Path = Item.Entry.Path, ModifiedUtc = Item.Entry.ModifiedUtc, DurationMs = Item.Entry.DurationMs,
            Tags = Tags.ToList(), People = ShowPeople ? PeopleList() : Item.Entry.People.ToList(),
        };
        if (Suggest(e) is { } s) { Name = s; Error = null; }
        else Error = T("Add the game as a tag, or who was there, and Savefile can suggest a name.");
    }
    [ObservableProperty] private string _tagInput = "";
    [ObservableProperty] private bool _favorite;
    [ObservableProperty] private FolderViewModel? _folder;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private string _note;
    /// <summary>The Notes feature is on.</summary>
    public bool ShowNote { get; init; }

    // ---------- who was there ----------
    /// <summary>The "Who was there" feature is on.</summary>
    public bool ShowPeople { get; init; }
    /// <summary>Names typed as "Alex, Sam".</summary>
    [ObservableProperty] private string _people;
    public ObservableCollection<string> PeopleSuggestions { get; } = new();
    public bool HasPeopleSuggestions => PeopleSuggestions.Count > 0;
    private List<string> _allPeople = new();

    /// <summary>Everyone tagged on any video, for the suggestions under the box.</summary>
    public IEnumerable<string> AllPeople
    {
        init { _allPeople = value.ToList(); RefreshPeopleSuggestions(); }
    }

    partial void OnPeopleChanged(string value) => RefreshPeopleSuggestions();

    /// <summary>The names in the box, trimmed, without duplicates, spelled like earlier tags.</summary>
    public List<string> PeopleList() => People
        .Split(',', StringSplitOptions.RemoveEmptyEntries)
        .Select(n => n.Trim().TrimStart('@').Trim())
        .Where(n => n.Length is > 0 and <= 40)
        .Select(n => _allPeople.FirstOrDefault(p => string.Equals(p, n, StringComparison.OrdinalIgnoreCase)) ?? n)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    [RelayCommand]
    private void AddPerson(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        var list = PeopleList();
        // A half-typed name that this suggestion completes is replaced, not kept beside it.
        var typing = People.Split(',').LastOrDefault()?.Trim() ?? "";
        if (typing.Length > 0 && name.StartsWith(typing, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(typing, name, StringComparison.OrdinalIgnoreCase))
            list.RemoveAll(n => string.Equals(n, typing, StringComparison.OrdinalIgnoreCase));
        if (!list.Contains(name, StringComparer.OrdinalIgnoreCase)) list.Add(name);
        People = string.Join(", ", list);
    }

    private void RefreshPeopleSuggestions()
    {
        var have = PeopleList();
        var typing = People.Split(',').LastOrDefault()?.Trim().TrimStart('@') ?? "";
        // A finished name ("Sam") doesn't narrow the list; a partial one ("Sa") does.
        var narrowing = typing.Length > 0 && !_allPeople.Contains(typing, StringComparer.OrdinalIgnoreCase);
        var next = _allPeople
            .Where(p => !have.Contains(p, StringComparer.OrdinalIgnoreCase))
            .Where(p => !narrowing || p.StartsWith(typing, StringComparison.OrdinalIgnoreCase))
            .Take(10).ToList();
        PeopleSuggestions.Clear();
        foreach (var p in next) PeopleSuggestions.Add(p);
        OnPropertyChanged(nameof(HasPeopleSuggestions));
    }

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
        _note = item.Entry.Note ?? "";
        _people = string.Join(", ", item.Entry.People);
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
