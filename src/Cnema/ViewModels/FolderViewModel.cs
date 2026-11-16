using CommunityToolkit.Mvvm.ComponentModel;

namespace Cnema.ViewModels;

public enum NavKind { All, Favorites, Folder, Tag, Collection, Person }

/// <summary>One row in the sidebar: a built-in view, a library folder, or a tag.</summary>
public sealed partial class FolderViewModel : ObservableObject
{
    public NavKind Kind { get; }
    /// <summary>Folder path (Kind == Folder), otherwise null.</summary>
    public string? Path { get; }
    /// <summary>Tag name (Kind == Tag), otherwise null.</summary>
    public string? Tag { get; }
    private readonly string _name;
    public string Name => Kind switch { NavKind.All => T("All videos"), NavKind.Favorites => T("Favourites"), _ => _name };

    public string Tooltip => Kind switch
    {
        NavKind.All => T("Every video in every folder"),
        NavKind.Favorites => T("Videos you starred"),
        NavKind.Tag => F("Videos tagged \"{0}\"", Tag),
        NavKind.Collection => F("Your collection \"{0}\"", Name),
        NavKind.Person => F("Clips with {0}", Name),
        _ => Path!,
    };
    public bool IsAll => Kind == NavKind.All;
    /// <summary>First letter for a friend's round badge in the sidebar.</summary>
    public string Initial => _name.Length > 0 ? char.ToUpper(_name[0], System.Globalization.CultureInfo.CurrentCulture).ToString() : "?";
    public bool IsRemovable => Kind == NavKind.Folder;
    /// <summary>The collection behind a Collection row.</summary>
    public Cnema.Models.VideoCollection? Collection { get; }

    public FolderViewModel(Cnema.Models.VideoCollection collection) : this(NavKind.Collection, collection.Name) => Collection = collection;

    [ObservableProperty] private int _count;

    public FolderViewModel(string? path) : this(path is null ? NavKind.All : NavKind.Folder, path) { }

    public FolderViewModel(NavKind kind, string? value = null)
    {
        Kind = kind;
        Path = kind == NavKind.Folder ? value : null;
        Tag = kind == NavKind.Tag ? value : null;
        _name = kind switch
        {
            NavKind.All or NavKind.Favorites => "",
            NavKind.Tag or NavKind.Collection or NavKind.Person => value!,
            _ => System.IO.Path.GetFileName(value!.TrimEnd('/', '\\')) is { Length: > 0 } n ? n : value!,
        };
    }

    /// <summary>Re-reads the translated name and tooltip after a language change.</summary>
    public void Refresh() => OnPropertyChanged(string.Empty);

    /// <summary>Folder containment test (only meaningful for All / Folder rows).</summary>
    public bool Contains(string file) =>
        Path is null || file.StartsWith(Path.TrimEnd('/', '\\') + System.IO.Path.DirectorySeparatorChar,
            OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    public bool Matches(VideoItemViewModel v) => Kind switch
    {
        NavKind.All => true,
        NavKind.Favorites => v.Entry.Favorite,
        NavKind.Tag => v.Entry.Tags.Contains(Tag!, StringComparer.OrdinalIgnoreCase),
        NavKind.Person => v.Entry.People.Contains(_name, StringComparer.OrdinalIgnoreCase),
        NavKind.Collection => Collection!.Paths.Contains(v.Entry.Path, StringComparer.OrdinalIgnoreCase),
        _ => Contains(v.Entry.Path),
    };
}
