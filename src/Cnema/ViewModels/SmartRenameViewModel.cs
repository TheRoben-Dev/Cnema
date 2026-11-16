using CommunityToolkit.Mvvm.ComponentModel;

namespace Cnema.ViewModels;

/// <summary>One video in "Smart rename": its name now and the suggested one (which can be edited).</summary>
public sealed partial class SmartRenameRow : ObservableObject
{
    public VideoItemViewModel Item { get; }
    public string OldTitle { get; }
    [ObservableProperty] private string _newTitle;
    [ObservableProperty] private bool _isChecked;

    public SmartRenameRow(VideoItemViewModel item, string suggestion)
    {
        Item = item;
        OldTitle = item.Title;
        _newTitle = suggestion;
    }
}

/// <summary>Smart rename: names for the whole library from game, friends and date, checked before anything changes.</summary>
public sealed partial class SmartRenameViewModel : ObservableObject
{
    public List<SmartRenameRow> Rows { get; }
    public bool HasRows => Rows.Count > 0;
    /// <summary>Also rename the files (default: only the name shown in Savefile).</summary>
    [ObservableProperty] private bool _renameFiles;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsIdle))] private bool _isBusy;
    public bool IsIdle => !IsBusy;
    [ObservableProperty] private bool _isDone;
    [ObservableProperty] private bool _canUndo;
    [ObservableProperty] private string _status = "";
    public string Intro => Rows.Count == 0
        ? T("Nothing to suggest yet. Smart titles come from a video's game tag and who was there, so tag a few and come back.")
        : F("{0}. Ticked are the ones with a name a recorder made up; tick any others you like, or edit a suggestion first.",
            P(Rows.Count, "{0} suggestion", "{0:N0} suggestions"));

    public SmartRenameViewModel(List<SmartRenameRow> rows, bool canUndo)
    {
        Rows = rows;
        _canUndo = canUndo;
    }

    public void CheckAll(bool on)
    {
        foreach (var r in Rows) r.IsChecked = on;
    }
}
