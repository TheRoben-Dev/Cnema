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
    public static string[] ResolutionFilters { get; } = { "Any resolution", "720p and up", "1080p and up", "4K" };
    public static string[] LengthFilters { get; } = { "Any length", "Under 30 seconds", "30 seconds to 5 minutes", "Over 5 minutes" };
    public static string[] DateFilters { get; } = { "Any date", "Today", "Past 7 days", "Past 30 days" };

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
    public string FilterButtonText => HasFilters ? $"Filters · {ActiveFilterCount}" : "Filters";

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
        Player.ShowToast("Thumbnail set to this frame");
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
        if (added > 0) ShowToast($"Added {added} video{(added == 1 ? "" : "s")} to {name}");
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
        ShowToast(n == 0 ? "Already in " + c.Name : $"Added {n} video{(n == 1 ? "" : "s")} to {c.Name}");
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
        ShowToast($"Removed the collection {c.Name}. The videos are still in your library.");
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
    public string SelectionText => SelectedCount switch { 0 => "Click videos to select them", 1 => "1 selected", var n => $"{n} selected" };
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
        ShowToast($"Tagged {items.Count} video{(items.Count == 1 ? "" : "s")} #{tag}");
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
    public string DeleteTitle => PendingBulkDelete is { Count: > 1 } ? "Delete videos" : "Delete video";
    public string DeleteHint => PendingBulkDelete is { Count: > 1 }
        ? "You can restore them from the trash if you change your mind."
        : "You can restore it from the trash if you change your mind.";
    private static string TrashName => OperatingSystem.IsWindows() ? "Recycle Bin" : "Trash";

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
        await vm.ScanAsync(_items.Values.Select(v => v.Entry).ToList(), p => _items.TryGetValue(p, out var v) ? v : null);
    }

    [RelayCommand] private void CloseCleanup() { Cleanup?.Cancel(); Cleanup = null; }

    [RelayCommand]
    private void RemoveCleanup()
    {
        if (Cleanup is not { } c) return;
        var chosen = c.Checked.Select(r => r.Item).OfType<VideoItemViewModel>().ToList();
        if (chosen.Count == 0) return;
        var failed = DeleteMany(chosen);
        Cleanup = null;
        RefreshView();
        ShowToast(failed == 0 ? $"Moved {chosen.Count} file{(chosen.Count == 1 ? "" : "s")} to the {TrashName}"
            : $"Moved {chosen.Count - failed} to the {TrashName}; {failed} could not be moved");
    }

    // ---------- stats ----------
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsShowingStats))] private StatsViewModel? _stats;
    public bool IsShowingStats => Stats is not null;
    public bool ShowStats => Features.Stats;

    [RelayCommand]
    private void OpenStats() =>
        Stats = new StatsViewModel(_items.Values.Select(v => v.Entry).ToList(), _settings.Current.Clip.ResolvedFolder);

    [RelayCommand] private void CloseStats() => Stats = null;

    public bool ShowTools => Features.CleanUp || Features.Stats;
}
