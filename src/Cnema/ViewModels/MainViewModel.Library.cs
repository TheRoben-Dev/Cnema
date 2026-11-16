using System.Collections.ObjectModel;
using Cnema.Models;
using Cnema.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cnema.ViewModels;

// Filters, collections, bulk selection, watched state, thumbnails, clean-up and stats.
public sealed partial class MainViewModel
{
    // ---------- filters ----------
    public static LocalizedText[] ResolutionFilters { get; } = LocalizedText.List("Any resolution", "720p and up", "1080p and up", "4K");
    public static LocalizedText[] LengthFilters { get; } = LocalizedText.List("Any length", "Under 30 seconds", "30 seconds to 5 minutes", "Over 5 minutes");
    public static LocalizedText[] DateFilters { get; } = LocalizedText.List("Any date", "Today", "Past 7 days", "Past 30 days");

    [ObservableProperty] private int _filterResolution;
    [ObservableProperty] private int _filterLength;
    [ObservableProperty] private int _filterDate;
    [ObservableProperty] private bool _filterUnwatched;
    partial void OnFilterResolutionChanged(int value) => FiltersChanged();
    partial void OnFilterLengthChanged(int value) => FiltersChanged();
    partial void OnFilterDateChanged(int value) => FiltersChanged();
    partial void OnFilterUnwatchedChanged(bool value) => FiltersChanged();

    public int ActiveFilterCount =>
        (FilterResolution > 0 ? 1 : 0) + (FilterLength > 0 ? 1 : 0) + (FilterDate > 0 ? 1 : 0) + (FilterUnwatched ? 1 : 0);
    public bool HasFilters => ActiveFilterCount > 0;
    public string FilterButtonText => HasFilters ? F("Filters · {0}", ActiveFilterCount) : T("Filters");

    private void FiltersChanged()
    {
        OnPropertyChanged(nameof(ActiveFilterCount));
        OnPropertyChanged(nameof(HasFilters));
        OnPropertyChanged(nameof(FilterButtonText));
        RefreshView();
    }

    [RelayCommand]
    private void ClearFilters()
    {
        FilterResolution = FilterLength = FilterDate = 0;
        FilterUnwatched = false;
    }

    private IEnumerable<VideoItemViewModel> ApplyFilters(IEnumerable<VideoItemViewModel> q)
    {
        var minHeight = FilterResolution switch { 1 => 700, 2 => 1000, 3 => 2000, _ => 0 };
        if (minHeight > 0) q = q.Where(v => v.Entry.Height >= minHeight);
        q = FilterLength switch
        {
            1 => q.Where(v => v.Entry.DurationMs > 0 && v.Entry.DurationMs < 30_000),
            2 => q.Where(v => v.Entry.DurationMs is >= 30_000 and <= 300_000),
            3 => q.Where(v => v.Entry.DurationMs > 300_000),
            _ => q,
        };
        var since = FilterDate switch
        {
            1 => DateTime.Today.ToUniversalTime(),
            2 => DateTime.UtcNow.AddDays(-7),
            3 => DateTime.UtcNow.AddDays(-30),
            _ => (DateTime?)null,
        };
        if (since is { } s) q = q.Where(v => v.Entry.ModifiedUtc >= s);
        if (FilterUnwatched && Features.Unwatched) q = q.Where(v => v.IsUnwatched);
        return q;
    }

    // ---------- watched ----------
    [RelayCommand]
    private void ToggleWatched(VideoItemViewModel? item)
    {
        if (item is null) return;
        SetWatched(new[] { item }, item.IsUnwatched);
    }

    private void SetWatched(IEnumerable<VideoItemViewModel> items, bool watched)
    {
        foreach (var v in items)
        {
            v.Entry.LastPlayedUtc = watched ? v.Entry.LastPlayedUtc ?? DateTime.UtcNow : null;
            if (!watched) v.Entry.LastPositionMs = 0;
            v.RefreshAll();
        }
        _library.SaveCache();
        if (FilterUnwatched) RefreshView();
    }

    // ---------- thumbnails ----------
    /// <summary>Makes the frame on screen in the player the video's thumbnail.</summary>
    [RelayCommand]
    private async Task UseFrameAsThumbnailAsync()
    {
        if (Player.Current is not { } item) return;
        var at = Math.Max(1, (long)Player.Position);
        await SetThumbnailAsync(item, at);
        Player.ShowToast(T("Thumbnail set to this frame"));
    }

    /// <summary>Frame grab: the frame at the playhead, full size, as a PNG next to the video.</summary>
    [RelayCommand]
    private async Task SaveFrameAsync()
    {
        if (Player.Current is not { } item) return;
        if (ToolLocator.Ffmpeg is not { } ffmpeg) { Player.ShowToast(T("Saving a frame needs ffmpeg, which wasn't found.")); return; }
        var at = Math.Max(0, Player.Position);
        var t = TimeSpan.FromMilliseconds(at);
        var name = $"{Path.GetFileNameWithoutExtension(item.Entry.Path)} {(int)t.TotalMinutes}m{t.Seconds:00}.{t.Milliseconds / 100}s";
        var seek = (at / 1000.0).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        // Next to the video, or in Pictures when that folder can't be written to.
        foreach (var dir in new[] { Path.GetDirectoryName(item.Entry.Path), Environment.GetFolderPath(Environment.SpecialFolder.MyPictures) })
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
            var target = Path.Combine(dir, name + ".png");
            for (var n = 2; File.Exists(target); n++) target = Path.Combine(dir, $"{name} {n}.png");
            var r = await ProcessRunner.RunAsync(ffmpeg, new[] { "-v", "error", "-ss", seek, "-i", item.Entry.Path, "-frames:v", "1", "-y", target });
            if (r.ExitCode == 0 && File.Exists(target))
            {
                Player.ShowToast(F("Saved {0} in {1}", Path.GetFileName(target), Path.GetFileName(dir)));
                return;
            }
        }
        Player.ShowToast(T("Couldn't save the frame."));
    }

    [RelayCommand]
    private async Task ResetThumbnailAsync(VideoItemViewModel? item)
    {
        if (item is null || item.Entry.ThumbAtMs == 0) return;
        await SetThumbnailAsync(item, 0);
    }

    private async Task SetThumbnailAsync(VideoItemViewModel item, long atMs)
    {
        var entry = item.Entry;
        entry.ThumbAtMs = atMs;
        var path = await ThumbnailService.EnsureAsync(entry, CancellationToken.None);
        if (path is null) return;
        entry.ThumbPath = path;
        item.ReloadThumbnail();
        _library.SaveCache();
    }

    // ---------- collections ----------
    private readonly CollectionsFile _collections = JsonStore.Load<CollectionsFile>(AppPaths.CollectionsFile);
    public ObservableCollection<FolderViewModel> Collections { get; } = new();
    [ObservableProperty] private FolderViewModel? _selectedCollection;
    public bool ShowCollections => Features.Collections;
    public bool HasCollections => Collections.Count > 0;

    /// <summary>The sidebar box for typing a collection name, used for new ones and for renaming.</summary>
    [ObservableProperty] private bool _isNamingCollection;
    [ObservableProperty] private string _collectionName = "";
    private FolderViewModel? _renaming;
    private List<VideoItemViewModel>? _addAfterNaming;

    private void SaveCollections()
    {
        try { JsonStore.Save(AppPaths.CollectionsFile, _collections); }
        catch (Exception ex) { Log.Warn("Saving collections failed: " + ex.Message); }
    }

    private void RebuildCollections()
    {
        var selected = SelectedCollection?.Collection;
        _rebuildingTags = true;
        try
        {
            Collections.Clear();
            foreach (var c in _collections.Collections.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase))
                Collections.Add(new FolderViewModel(c));
            SelectedCollection = selected is null ? null : Collections.FirstOrDefault(c => c.Collection == selected);
        }
        finally { _rebuildingTags = false; }
        OnPropertyChanged(nameof(HasCollections));
        if (selected is not null && SelectedCollection is null && Folders.Count > 0) SelectedFolder = Folders[0];
        UpdateCollectionCounts();
    }

    private void UpdateCollectionCounts()
    {
        foreach (var c in Collections) c.Count = c.Collection!.Paths.Count(p => _items.ContainsKey(p));
    }

    [RelayCommand]
    private void NewCollection()
    {
        _renaming = null;
        _addAfterNaming = null;
        CollectionName = "";
        IsNamingCollection = true;
    }

    /// <summary>"New collection…" from a menu: the chosen videos go in once it has a name.</summary>
    public void NewCollectionWith(IEnumerable<VideoItemViewModel> items)
    {
        NewCollection();
        _addAfterNaming = items.ToList();
    }

    public void BeginRenameCollection(FolderViewModel? row)
    {
        if (row?.Collection is null) return;
        _renaming = row;
        CollectionName = row.Collection.Name;
        IsNamingCollection = true;
    }

    [RelayCommand]
    private void CancelNaming()
    {
        IsNamingCollection = false;
        _renaming = null;
        _addAfterNaming = null;
    }

    [RelayCommand]
    private void ConfirmCollectionName()
    {
        var name = CollectionName.Trim();
        if (name.Length == 0) { CancelNaming(); return; }
        VideoCollection target;
        if (_renaming?.Collection is { } existing)
        {
            existing.Name = name;
            target = existing;
        }
        else
        {
            target = _collections.Collections.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.CurrentCultureIgnoreCase))
                     ?? new VideoCollection { Name = name };
            if (!_collections.Collections.Contains(target)) _collections.Collections.Add(target);
        }
        if (_addAfterNaming is { } add)
            foreach (var v in add)
                if (!target.Paths.Contains(v.Entry.Path, StringComparer.OrdinalIgnoreCase)) target.Paths.Add(v.Entry.Path);
        var added = _addAfterNaming?.Count ?? 0;
        CancelNaming();
        SaveCollections();
        RebuildCollections();
        OnPropertyChanged(nameof(HeaderTitle));
        if (added > 0) ShowToast(F(added == 1 ? "Added {0} video to {1}" : "Added {0} videos to {1}", added, name));
    }

    public void AddToCollection(IEnumerable<VideoItemViewModel> items, FolderViewModel row)
    {
        if (row.Collection is not { } c) return;
        var n = 0;
        foreach (var v in items)
        {
            if (c.Paths.Contains(v.Entry.Path, StringComparer.OrdinalIgnoreCase)) continue;
            c.Paths.Add(v.Entry.Path);
            n++;
        }
        SaveCollections();
        UpdateCollectionCounts();
        if (ActiveNav == row) RefreshView();
        ShowToast(n == 0 ? F("Already in {0}", c.Name) : F(n == 1 ? "Added {0} video to {1}" : "Added {0} videos to {1}", n, c.Name));
    }

    public void RemoveFromCollection(VideoItemViewModel item)
    {
        if (SelectedCollection?.Collection is not { } c) return;
        c.Paths.RemoveAll(p => string.Equals(p, item.Entry.Path, StringComparison.OrdinalIgnoreCase));
        SaveCollections();
        UpdateCollectionCounts();
        RefreshView();
    }

    public bool IsInCollectionView => SelectedCollection is not null;

    [RelayCommand]
    private void DeleteCollection(FolderViewModel? row)
    {
        if (row?.Collection is not { } c) return;
        _collections.Collections.Remove(c);
        SaveCollections();
        RebuildCollections();
        ShowToast(F("Removed the collection {0}. The videos are still in your library.", c.Name));
    }

    /// <summary>Keeps collections pointing at a video after it was renamed or moved.</summary>
    private void CollectionPathChanged(string oldPath, string newPath)
    {
        var changed = false;
        foreach (var c in _collections.Collections)
            for (var i = 0; i < c.Paths.Count; i++)
                if (string.Equals(c.Paths[i], oldPath, StringComparison.OrdinalIgnoreCase)) { c.Paths[i] = newPath; changed = true; }
        if (changed) SaveCollections();
    }

    // ---------- bulk selection ----------
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(SelectionText))] private bool _isSelecting;
    [ObservableProperty] private string _bulkTag = "";
    public int SelectedCount => _items.Values.Count(v => v.IsSelected);
    public string SelectionText => SelectedCount switch { 0 => T("Click videos to select them"), var n => F("{0} selected", n) };
    private IEnumerable<VideoItemViewModel> Selection => _items.Values.Where(v => v.IsSelected).ToList();

    partial void OnIsSelectingChanged(bool value)
    {
        VideoItemViewModel.Selecting = value;
        foreach (var v in _items.Values)
        {
            if (!value) v.IsSelected = false;
            v.RefreshSelection();
        }
        SelectionChanged();
        OnPropertyChanged(nameof(ShowFeatured));
        OnPropertyChanged(nameof(ShowMemory));
        OnPropertyChanged(nameof(HeaderCount));
        OnPropertyChanged(nameof(CanPlayAll));
        OnPropertyChanged(nameof(ShowFriendMontage));
        OnPropertyChanged(nameof(ShowFriendWrapped));
        OnPropertyChanged(nameof(FriendWrappedLabel));
    }

    private void SelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectionText));
    }

    [RelayCommand] private void ToggleSelectMode() => IsSelecting = !IsSelecting;

    private void ToggleSelected(VideoItemViewModel item)
    {
        item.IsSelected = !item.IsSelected;
        SelectionChanged();
    }

    [RelayCommand]
    private void SelectAll()
    {
        var all = Videos.All(v => v.IsSelected);
        foreach (var v in Videos) v.IsSelected = !all;
        SelectionChanged();
    }

    [RelayCommand]
    private void BulkFavorite()
    {
        var items = Selection.ToList();
        if (items.Count == 0) return;
        var on = !items.All(v => v.Entry.Favorite);
        foreach (var v in items) { v.Entry.Favorite = on; v.RefreshAll(); }
        _library.SaveCache();
        if (ActiveNav?.Kind == NavKind.Favorites) RefreshView(); else UpdateCounts();
    }

    [RelayCommand]
    private void BulkAddTag()
    {
        var tag = BulkTag.Trim().TrimStart('#');
        var items = Selection.ToList();
        if (tag.Length == 0 || items.Count == 0) return;
        foreach (var v in items.Where(v => !v.Entry.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase)))
        {
            v.Entry.Tags.Add(tag);
            v.RefreshAll();
        }
        BulkTag = "";
        _library.SaveCache();
        UpdateCounts();
        ShowToast(F(items.Count == 1 ? "Tagged {0} video #{1}" : "Tagged {0} videos #{1}", items.Count, tag));
    }

    [RelayCommand] private void BulkAddToCollection(FolderViewModel? row) { if (row is not null) AddToCollection(Selection, row); }
    [RelayCommand] private void BulkNewCollection() => NewCollectionWith(Selection);
    [RelayCommand] private void BulkMarkWatched() => SetWatched(Selection, true);
    [RelayCommand] private void BulkMarkUnwatched() => SetWatched(Selection, false);

    [RelayCommand]
    private void BulkDelete()
    {
        var items = Selection.ToList();
        if (items.Count == 0) return;
        DeleteError = null;
        PendingBulkDelete = items;
    }

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsConfirmingDelete), nameof(DeletePrompt), nameof(DeleteTitle), nameof(DeleteHint))]
    private IReadOnlyList<VideoItemViewModel>? _pendingBulkDelete;
    public string DeleteTitle => PendingBulkDelete is { Count: > 1 } ? T("Let these go?") : T("Let this one go?");
    public string DeleteHint => PendingBulkDelete is { Count: > 1 }
        ? F("You can restore them from the {0} if you change your mind.", TrashName)
        : F("You can restore it from the {0} if you change your mind.", TrashName);
    private static string TrashName => T(OperatingSystem.IsWindows() ? "Recycle Bin" : "Trash");

    /// <summary>Moves several videos to the trash; returns how many could not be moved.</summary>
    private int DeleteMany(IReadOnlyList<VideoItemViewModel> items)
    {
        var failed = 0;
        foreach (var item in items)
        {
            try
            {
                _library.Delete(item.Entry);
                _items.Remove(item.Entry.Path);
            }
            catch (Exception ex)
            {
                failed++;
                Log.Warn($"Delete failed for {item.Entry.Path}: {ex.Message}");
            }
        }
        return failed;
    }

    // ---------- clean-up ----------
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsCleaningUp))] private CleanupViewModel? _cleanup;
    public bool IsCleaningUp => Cleanup is not null;
    public bool ShowCleanUp => Features.CleanUp;

    [RelayCommand]
    private async Task OpenCleanupAsync()
    {
        var vm = new CleanupViewModel();
        Cleanup = vm;
        await vm.ScanAsync(_items.Values.Select(v => v.Entry).ToList(), p => _items.TryGetValue(p, out var v) ? v : null,
            Features.Visual ? LooksSame : null);
    }

    private static void MergeDetails(VideoEntry from, VideoEntry into)
    {
        into.Favorite |= from.Favorite;
        foreach (var t in from.Tags.Where(t => !into.Tags.Contains(t, StringComparer.OrdinalIgnoreCase))) into.Tags.Add(t);
        foreach (var p in from.People.Where(p => !into.People.Contains(p, StringComparer.OrdinalIgnoreCase))) into.People.Add(p);
        foreach (var b in from.Bookmarks.Where(b => !into.Bookmarks.Contains(b))) into.Bookmarks.Add(b);
        into.Bookmarks.Sort();
        if (!string.IsNullOrWhiteSpace(from.Note) && into.Note?.Contains(from.Note.Trim(), StringComparison.Ordinal) != true)
            into.Note = string.IsNullOrWhiteSpace(into.Note) ? from.Note : into.Note.TrimEnd() + "\n" + from.Note.Trim();
    }

    [RelayCommand] private void CloseCleanup() { Cleanup?.Cancel(); Cleanup = null; }

    [RelayCommand]
    private void RemoveCleanup()
    {
        if (Cleanup is not { } c) return;
        var chosen = c.Checked.Select(r => r.Item).OfType<VideoItemViewModel>().ToList();
        if (chosen.Count == 0) return;
        // A copy going to the trash hands its favourite, tags, friends, notes and bookmarks to the copy that stays.
        foreach (var group in c.Rows.Where(r => r.Group >= 0 && r.Item is not null).GroupBy(r => r.Group))
        {
            if (group.FirstOrDefault(r => !r.IsChecked)?.Item is not { } keep) continue;
            foreach (var gone in group.Where(r => r.IsChecked).Select(r => r.Item!))
                MergeDetails(gone.Entry, keep.Entry);
            keep.RefreshAll();
        }
        var failed = DeleteMany(chosen);
        Cleanup = null;
        RefreshView();
        ShowToast(failed == 0 ? F(chosen.Count == 1 ? "Moved {0} file to the {1}" : "Moved {0} files to the {1}", chosen.Count, TrashName)
            : F("Moved {0} to the {1}; {2} could not be moved", chosen.Count - failed, TrashName, failed));
    }

    // ---------- stats ----------
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsShowingStats))] private StatsViewModel? _stats;
    public bool IsShowingStats => Stats is not null;
    public bool ShowStats => Features.Stats;

    [RelayCommand]
    private void OpenStats() =>
        Stats = new StatsViewModel(_items.Values.Select(v => v.Entry).ToList(), _settings.Current.Clip.ResolvedFolder);

    [RelayCommand] private void CloseStats() => Stats = null;

    public bool ShowTools => Features.CleanUp || Features.Stats || Features.Recorders || Features.Montage || Features.Recap || Features.SmartTitles || Features.Backup;
}
