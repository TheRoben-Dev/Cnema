using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cnema.ViewModels;

// Renaming or removing a tag or a friend across the whole library, from the sidebar.
public sealed partial class MainViewModel
{
    private FolderViewModel? _renamingRow;
    [ObservableProperty] private string _renameText = "";
    [ObservableProperty] private bool _isRenamingTag;
    [ObservableProperty] private bool _isRenamingFriend;

    public void BeginRenameName(FolderViewModel? row)
    {
        if (row is not { Kind: NavKind.Tag or NavKind.Person }) return;
        _renamingRow = row;
        RenameText = row.Name;
        IsRenamingTag = row.Kind == NavKind.Tag;
        IsRenamingFriend = row.Kind == NavKind.Person;
    }

    [RelayCommand]
    private void CancelRename()
    {
        _renamingRow = null;
        IsRenamingTag = IsRenamingFriend = false;
    }

    /// <summary>Renames the tag or friend on every video. A name that already exists merges the two (fixes typos).</summary>
    [RelayCommand]
    private void ConfirmRename()
    {
        if (_renamingRow is not { } row) return;
        var from = row.Name;
        var wasActive = ReferenceEquals(ActiveNav, row);
        var to = RenameText.Trim().TrimStart(row.Kind == NavKind.Tag ? '#' : '@').Trim();
        CancelRename();
        if (to.Length == 0 || to.Length > 40 || to == from) return;
        var n = ReplaceName(row.Kind, from, to);
        _library.SaveCache();
        if (row.Kind == NavKind.Person)
        {
            var squad = _settings.Current.PlayingWith;
            for (var i = 0; i < squad.Count; i++)
                if (string.Equals(squad[i], from, StringComparison.OrdinalIgnoreCase)) squad[i] = to;
            _settings.Current.PlayingWith = squad.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            _settings.Save();
            RaiseSquad();
            // Mochi's ear follows the name too.
            RenameVoice(from, to);
            foreach (var v in _items.Values)
                if (v.Entry.Voices?.Added is { } added)
                    for (var i = 0; i < added.Count; i++)
                        if (string.Equals(added[i], from, StringComparison.OrdinalIgnoreCase)) added[i] = to;
        }
        UpdateCounts();
        // Stay on the renamed row if it was the one being viewed.
        if (!wasActive) { }
        else if (row.Kind == NavKind.Tag) SelectedTag = Tags.FirstOrDefault(t => string.Equals(t.Name, to, StringComparison.OrdinalIgnoreCase));
        else SelectedFriend = Friends.FirstOrDefault(t => string.Equals(t.Name, to, StringComparison.OrdinalIgnoreCase));
        RefreshView();
        ShowToast(F("Renamed {0} to {1} on {2}", from, to, P(n, "{0} video", "{0} videos")));
    }

    /// <summary>Takes a tag or friend off every video; the videos themselves stay.</summary>
    public void RemoveName(FolderViewModel? row)
    {
        if (row is not { Kind: NavKind.Tag or NavKind.Person }) return;
        var n = ReplaceName(row.Kind, row.Name, null);
        _library.SaveCache();
        if (row.Kind == NavKind.Person && _settings.Current.PlayingWith.RemoveAll(p => string.Equals(p, row.Name, StringComparison.OrdinalIgnoreCase)) > 0)
        {
            _settings.Save();
            RaiseSquad();
        }
        UpdateCounts();
        RefreshView();
        ShowToast(row.Kind == NavKind.Tag
            ? F("Removed the tag {0} from {1}. The videos are still here.", row.Name, P(n, "{0} video", "{0} videos"))
            : F("Untagged {0} from {1}. The videos are still here.", row.Name, P(n, "{0} video", "{0} videos")));
    }

    private int ReplaceName(NavKind kind, string from, string? to)
    {
        var n = 0;
        foreach (var v in _items.Values)
        {
            var list = kind == NavKind.Tag ? v.Entry.Tags : v.Entry.People;
            var i = list.FindIndex(x => string.Equals(x, from, StringComparison.OrdinalIgnoreCase));
            if (i < 0) continue;
            list.RemoveAt(i);
            if (to is not null && !list.Contains(to, StringComparer.OrdinalIgnoreCase)) list.Insert(i, to);
            v.RefreshAll();
            n++;
        }
        return n;
    }
}
