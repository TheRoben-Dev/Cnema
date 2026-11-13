using CommunityToolkit.Mvvm.ComponentModel;

namespace Cinema.ViewModels;

public sealed partial class FolderViewModel : ObservableObject
{
    /// <summary>Null = the "All videos" pseudo-folder.</summary>
    public string? Path { get; }
    public string Name { get; }
    public string Tooltip => Path ?? "Every video in every folder";
    public bool IsAll => Path is null;
    public bool IsRemovable => Path is not null;
    public bool Exists => Path is null || Directory.Exists(Path);

    [ObservableProperty] private int _count;

    public FolderViewModel(string? path)
    {
        Path = path;
        Name = path is null ? "All videos" : (System.IO.Path.GetFileName(path.TrimEnd('/', '\\')) is { Length: > 0 } n ? n : path);
    }

    public bool Contains(string file) =>
        Path is null || file.StartsWith(Path.TrimEnd('/', '\\') + System.IO.Path.DirectorySeparatorChar,
            OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
}
