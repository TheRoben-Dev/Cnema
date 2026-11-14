using CommunityToolkit.Mvvm.ComponentModel;

namespace Cnema.ViewModels;

public enum NavKind { All, Favorites, Folder, Tag }

/// <summary>One row in the sidebar: a built-in view, a library folder, or a tag.</summary>
public sealed partial class FolderViewModel : ObservableObject
{
    public NavKind Kind { get; }
    /// <summary>Folder path (Kind == Folder), otherwise null.</summary>
    public string? Path { get; }
    /// <summary>Tag name (Kind == Tag), otherwise null.</summary>
    public string? Tag { get; }
    public string Name { get; }

    public string Tooltip => Kind switch
    {
        NavKind.All => "Every video in every folder",
        NavKind.Favorites => "Videos you starred",
        NavKind.Tag => $"Videos tagged \"{Tag}\"",
        _ => Path!,
    };
    public bool IsAll => Kind == NavKind.All;
    public bool IsRemovable => Kind == NavKind.Folder;

    [ObservableProperty] private int _count;

    public FolderViewModel(string? path) : this(path is null ? NavKind.All : NavKind.Folder, path) { }

    public FolderViewModel(NavKind kind, string? value = null)
    {
        Kind = kind;
        Path = kind == NavKind.Folder ? value : null;
        Tag = kind == NavKind.Tag ? value : null;
        Name = kind switch
        {
            NavKind.All => "All videos",
            NavKind.Favorites => "Favourites",
            NavKind.Tag => value!,
            _ => System.IO.Path.GetFileName(value!.TrimEnd('/', '\\')) is { Length: > 0 } n ? n : value!,
        };
    }

    /// <summary>Folder containment test (only meaningful for All / Folder rows).</summary>
    public bool Contains(string file) =>
        Path is null || file.StartsWith(Path.TrimEnd('/', '\\') + System.IO.Path.DirectorySeparatorChar,
            OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    public bool Matches(VideoItemViewModel v) => Kind switch
    {
        NavKind.All => true,
        NavKind.Favorites => v.Entry.Favorite,
        NavKind.Tag => v.Entry.Tags.Contains(Tag!, StringComparer.OrdinalIgnoreCase),
        _ => Contains(v.Entry.Path),
    };
}
