using System.Collections.ObjectModel;
using Cnema.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cnema.ViewModels;

/// <summary>One recorder on the "Other recorders" panel.</summary>
public sealed partial class RecorderRow : ObservableObject
{
    public RecorderKind Kind { get; init; }
    public string Name { get; init; } = "";
    public bool IsSupported { get; init; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(PathText), nameof(CanAdd))] private string? _path;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanAdd), nameof(StatusText), nameof(HasPending))] private bool _isAdded;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasPending), nameof(PendingText))] private int _pending;
    [ObservableProperty] private bool _isConverting;

    public bool IsSteam => Kind == RecorderKind.Steam;
    public bool CanAdd => !IsAdded && IsSupported && Path is not null;
    public bool HasPending => IsSteam && IsAdded && Pending > 0;
    public string PathText => !IsSupported ? T("Windows only")
        : Path ?? (Kind == RecorderKind.Custom ? T("Pick any folder with Change…") : T("Not found on this computer. Use Change… if it records somewhere else."));
    public string PendingText => P(Pending, "Copy {0} new clip", "Copy {0} new clips");
    public string StatusText => IsAdded ? T("In your library") : "";

    public string Description => Kind switch
    {
        RecorderKind.Obs => T("Recordings and replay buffer saves. Videos in a subfolder are tagged with the folder's name."),
        RecorderKind.Nvidia => T("ShadowPlay / NVIDIA app clips, tagged with the game from their folder."),
        RecorderKind.XboxGameBar => T("Game Bar captures, tagged with the game from their file name."),
        RecorderKind.Steam => T("Steam keeps clips in a streaming format; Savefile copies them into MP4 files, tagged with the game."),
        RecorderKind.MacScreen => T("Recordings made with Screenshot (Shift-Command-5) or QuickTime."),
        RecorderKind.SnippingTool => T("Screen recordings from the Snipping Tool."),
        RecorderKind.Custom => T("Any other recorder or folder. Videos in a subfolder are tagged with the folder's name."),
        _ => T("Clips sorted into a folder per game, tagged with the game."),
    };
}

/// <summary>The "Other recorders" panel: bring in OBS, NVIDIA, Xbox Game Bar and Steam folders.</summary>
public sealed partial class RecordersViewModel : ObservableObject
{
    public ObservableCollection<RecorderRow> Rows { get; } = new();
    [ObservableProperty] private bool _tagByGame;

    public RecordersViewModel(IReadOnlyList<RecorderImport> imports, bool tagByGame)
    {
        _tagByGame = tagByGame;
        // Recorders that only exist on another system are left out.
        foreach (var info in RecorderService.Detect().Where(i => i.Supported))
        {
            var added = imports.FirstOrDefault(i => i.Kind == info.Kind);
            Rows.Add(new RecorderRow
            {
                Kind = info.Kind, Name = info.Name, IsSupported = info.Supported,
                Path = added?.Path ?? info.Path, IsAdded = added is not null,
            });
        }
    }
}
