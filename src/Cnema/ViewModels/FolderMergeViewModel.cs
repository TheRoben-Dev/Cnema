using CommunityToolkit.Mvvm.ComponentModel;

namespace Cnema.ViewModels;

/// <summary>One watched folder in "Merge into one folder": what it holds and what happens to it.</summary>
public sealed partial class MergeRow : ObservableObject
{
    public string Path { get; init; } = "";
    public string Name { get; init; } = "";
    public int Videos { get; init; }
    public long Bytes { get; init; }
    /// <summary>Where its videos go ("Savefile Clips/OBS"), or why they stay.</summary>
    [ObservableProperty] private string _note = "";
    /// <summary>False for a drive that isn't plugged in, or a folder holding the library folder.</summary>
    [ObservableProperty] private bool _canMerge;
    [ObservableProperty] private bool _isChecked;
    /// <summary>Inside the library folder already: nothing moves, the extra entry just goes.</summary>
    public bool IsInside { get; init; }
    /// <summary>OBS / NVIDIA keep recording here, so it stays watched either way.</summary>
    public bool IsRecorder { get; init; }
    /// <summary>The subfolder of the library folder its videos go into.</summary>
    public string Into { get; set; } = "";
    public string Details => F("{0} · {1}", P(Videos, "{0} video", "{0:N0} videos"), BackupViewModel.SizeText(Bytes));
}

/// <summary>The "Merge into one folder" dialog: pick folders, see where things go, merge, undo.</summary>
public sealed partial class FolderMergeViewModel : ObservableObject
{
    public List<MergeRow> Rows { get; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(TargetText))] private string _target;
    public string TargetText => F("Everything goes into {0}, each folder as a subfolder there. Names that clash get a number.", Target);
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsIdle))] private bool _isBusy;
    public bool IsIdle => !IsBusy;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _statusIsError;
    /// <summary>After a merge: it can be put back the way it was.</summary>
    [ObservableProperty] private bool _canUndo;
    [ObservableProperty] private bool _isDone;

    public FolderMergeViewModel(string target, List<MergeRow> rows, bool canUndo)
    {
        _target = target;
        Rows = rows;
        _canUndo = canUndo;
    }
}
