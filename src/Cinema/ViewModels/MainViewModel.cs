using System.Collections.ObjectModel;
using Avalonia.Threading;
using Cinema.Models;
using Cinema.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cinema.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly SettingsStore _settings;
    private readonly LibraryService _library;
    private readonly Dictionary<string, VideoItemViewModel> _items = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _refreshTimer;
    private CancellationTokenSource? _scanCts;

    public static string[] SortModes { get; } = { "Name", "Recently modified", "Recently played", "Longest", "Largest" };

    public ObservableCollection<FolderViewModel> Folders { get; } = new();
    public ObservableCollection<FolderViewModel> Tags { get; } = new();
    public PlayerViewModel Player { get; }

    /// <summary>Set by the view: opens a native folder picker.</summary>
    public Func<Task<string?>>? PickFolder { get; set; }

    [ObservableProperty] private ObservableCollection<VideoItemViewModel> _videos = new();
    [ObservableProperty] private FolderViewModel? _selectedFolder;
    [ObservableProperty] private FolderViewModel? _selectedTag;
    [ObservableProperty] private EditViewModel? _editor;
    public bool IsEditing => Editor is not null;
    public bool HasTags => Tags.Count > 0;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private int _sortIndex;
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private string _scanStatus = "";
    [ObservableProperty] private double _scanProgress;
    [ObservableProperty] private bool _isPlayerOpen;

    public string? VlcError => VlcBootstrap.Error;
    public bool HasVlcError => VlcBootstrap.Error is not null;
    public bool FfmpegMissing => ToolLocator.Ffmpeg is null;
    public string FfmpegHint => OperatingSystem.IsMacOS() ? "brew install ffmpeg"
        : OperatingSystem.IsWindows() ? "winget install ffmpeg" : "sudo apt install ffmpeg";

    /// <summary>Only animate while a scan is actually enumerating; an indeterminate bar animates even when hidden.</summary>
    public bool IsScanIndeterminate => IsScanning && ScanProgress <= 0;
    partial void OnScanProgressChanged(double value) => OnPropertyChanged(nameof(IsScanIndeterminate));

    public bool HasNoFolders => _settings.Current.Folders.Count == 0;
    public bool HasNoResults => !HasNoFolders && Videos.Count == 0 && !IsScanning;
    private FolderViewModel? ActiveNav => SelectedTag ?? SelectedFolder;
    public string HeaderTitle => ActiveNav is { Kind: NavKind.Tag } t ? "#" + t.Name : ActiveNav?.Name ?? "All videos";
    public string HeaderCount => Videos.Count == 1 ? "1 video" : $"{Videos.Count:N0} videos";

    public MainViewModel(SettingsStore settings, LibraryService library)
    {
        _settings = settings;
        _library = library;
        Player = new PlayerViewModel(settings);
        Player.Exported += path => { _ = RescanAsync(); };
        _sortIndex = Math.Clamp(settings.Current.SortIndex, 0, SortModes.Length - 1);

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _refreshTimer.Tick += (_, _) => { _refreshTimer.Stop(); RefreshView(); };

        _library.FoldersChanged += () => Dispatcher.UIThread.Post(() => _ = RescanAsync());
        RebuildFolders();
    }

    /// <summary>Show the cached library instantly, then rescan in the background.</summary>
    public async Task InitializeAsync()
    {
        foreach (var e in _library.CachedEntries.Where(e => _settings.Current.Folders.Any(f => Under(e.Path, f))))
            _items[e.Path] = new VideoItemViewModel(e);
        RefreshView();
        await RescanAsync();
    }

    // ---------- folders ----------
    private void RebuildFolders(string? select = null)
    {
        Folders.Clear();
        Folders.Add(new FolderViewModel(NavKind.All));
        Folders.Add(new FolderViewModel(NavKind.Favorites));
        foreach (var f in _settings.Current.Folders) Folders.Add(new FolderViewModel(f));
        SelectedFolder = (select is null ? null : Folders.FirstOrDefault(f => f.Path == select)) ?? Folders[0];
        _library.Watch(_settings.Current.Folders);
        OnPropertyChanged(nameof(HasNoFolders));
        UpdateCounts();
    }

    [RelayCommand]
    private async Task AddFolderAsync()
    {
        if (PickFolder is null) return;
        var path = await PickFolder();
        if (string.IsNullOrWhiteSpace(path)) return;
        path = Path.GetFullPath(path).TrimEnd('/', '\\');
        if (path.Length == 0) path = "/";
        if (_settings.Current.Folders.Any(f => string.Equals(f, path, StringComparison.OrdinalIgnoreCase))) return;
        _settings.Current.Folders.Add(path);
        _settings.Save();
        RebuildFolders(path);
        await RescanAsync();
    }

    [RelayCommand]
    private async Task RemoveFolderAsync(FolderViewModel? folder)
    {
        if (folder?.Path is null) return;
        _settings.Current.Folders.RemoveAll(f => string.Equals(f, folder.Path, StringComparison.OrdinalIgnoreCase));
        _settings.Save();
        foreach (var key in _items.Keys.Where(k => !_settings.Current.Folders.Any(f => Under(k, f))).ToList())
            _items.Remove(key);
        RebuildFolders();
        RefreshView();
        await RescanAsync();
    }

    [RelayCommand]
    private void RevealFolder(FolderViewModel? folder)
    {
        if (folder?.Path is { } p) PlatformShell.Reveal(p);
    }

    // ---------- scanning ----------
    [RelayCommand]
    private async Task RescanAsync()
    {
        _scanCts?.Cancel();
        var cts = _scanCts = new CancellationTokenSource();
        var folders = _settings.Current.Folders.ToList();
        IsScanning = true;
        ScanStatus = "Scanning folders…";
        ScanProgress = 0;
        try
        {
            var progress = new Progress<(int done, int total)>(p =>
            {
                if (p.total == 0) return;
                ScanStatus = $"Indexing {p.done}/{p.total}";
                ScanProgress = 100.0 * p.done / p.total;
            });
            var found = await _library.ScanAsync(folders, e => Dispatcher.UIThread.Post(() => Upsert(e)), progress, cts.Token);
            if (cts.IsCancellationRequested) return;

            var live = new HashSet<string>(found.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
            foreach (var gone in _items.Keys.Where(k => !live.Contains(k)).ToList()) _items.Remove(gone);
            RefreshView();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Player.ShowToast("Scan failed: " + ex.Message); }
        finally
        {
            if (_scanCts == cts)
            {
                IsScanning = false;
                ScanStatus = "";
                OnPropertyChanged(nameof(HasNoResults));
            }
        }
    }

    private void Upsert(VideoEntry e)
    {
        if (_items.TryGetValue(e.Path, out var vm)) { if (!ReferenceEquals(vm.Entry, e)) vm.Update(e); }
        else _items[e.Path] = new VideoItemViewModel(e);
        if (!_refreshTimer.IsEnabled) _refreshTimer.Start();
    }

    // ---------- filtering / sorting ----------
    // The sidebar is two lists (library + tags) that behave as one selection.
    private bool _rebuildingTags;

    partial void OnSelectedFolderChanged(FolderViewModel? value)
    {
        if (_rebuildingTags) return;
        if (value is not null) SelectedTag = null;
        if (value is null && SelectedTag is null) return; // transient: the other list is about to take over
        RefreshView();
        OnPropertyChanged(nameof(HeaderTitle));
    }

    partial void OnSelectedTagChanged(FolderViewModel? value)
    {
        if (_rebuildingTags) return;
        if (value is not null) SelectedFolder = null;
        else if (SelectedFolder is null && Folders.Count > 0) { SelectedFolder = Folders[0]; return; }
        if (value is null) return;
        RefreshView();
        OnPropertyChanged(nameof(HeaderTitle));
    }

    partial void OnEditorChanged(EditViewModel? value) => OnPropertyChanged(nameof(IsEditing));
    partial void OnSearchTextChanged(string value) => RefreshView();
    partial void OnSortIndexChanged(int value)
    {
        _settings.Current.SortIndex = value;
        _settings.Save();
        RefreshView();
    }
    partial void OnIsScanningChanged(bool value)
    {
        OnPropertyChanged(nameof(HasNoResults));
        OnPropertyChanged(nameof(IsScanIndeterminate));
    }

    private void RefreshView()
    {
        IEnumerable<VideoItemViewModel> q = _items.Values;
        if (ActiveNav is { IsAll: false } nav) q = q.Where(nav.Matches);
        var search = SearchText.Trim();
        if (search.Length > 0)
        {
            var terms = search.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            // A term matches the path or a tag; "#clutch" matches tags only.
            q = q.Where(v => terms.All(t => t.StartsWith('#')
                ? t.Length > 1 && v.Entry.Tags.Any(tag => tag.Contains(t[1..], StringComparison.OrdinalIgnoreCase))
                : v.Entry.Path.Contains(t, StringComparison.OrdinalIgnoreCase) ||
                  v.Entry.Tags.Any(tag => tag.Contains(t, StringComparison.OrdinalIgnoreCase))));
        }
        q = SortIndex switch
        {
            1 => q.OrderByDescending(v => v.Entry.ModifiedUtc),
            2 => q.OrderByDescending(v => v.Entry.LastPlayedUtc ?? DateTime.MinValue).ThenByDescending(v => v.Entry.ModifiedUtc),
            3 => q.OrderByDescending(v => v.Entry.DurationMs),
            4 => q.OrderByDescending(v => v.Entry.Size),
            _ => q.OrderBy(v => v.Title, StringComparer.CurrentCultureIgnoreCase),
        };
        Videos = new ObservableCollection<VideoItemViewModel>(q);
        OnPropertyChanged(nameof(HeaderCount));
        OnPropertyChanged(nameof(HasNoResults));
        UpdateCounts();
    }

    private void UpdateCounts()
    {
        foreach (var f in Folders)
            f.Count = f.Kind switch
            {
                NavKind.All => _items.Count,
                NavKind.Favorites => _items.Values.Count(v => v.Entry.Favorite),
                _ => _items.Keys.Count(k => f.Contains(k)),
            };
        RebuildTags();
    }

    /// <summary>Keeps the sidebar tag list in sync with the tags actually in use.</summary>
    private void RebuildTags()
    {
        var counts = _items.Values.SelectMany(v => v.Entry.Tags)
            .GroupBy(t => t, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase)
            .Select(g => (Name: g.First(), Count: g.Count())).ToList();

        var same = counts.Count == Tags.Count && counts.Zip(Tags).All(p => p.First.Name == p.Second.Name);
        if (same)
        {
            foreach (var (c, vm) in counts.Zip(Tags)) vm.Count = c.Count;
            return;
        }
        // Clearing the list makes the ListBox null its selection; hold the handlers off while we swap.
        var selected = SelectedTag?.Name;
        _rebuildingTags = true;
        try
        {
            Tags.Clear();
            foreach (var c in counts) Tags.Add(new FolderViewModel(NavKind.Tag, c.Name) { Count = c.Count });
            SelectedTag = selected is null ? null
                : Tags.FirstOrDefault(t => string.Equals(t.Name, selected, StringComparison.OrdinalIgnoreCase));
        }
        finally { _rebuildingTags = false; }
        OnPropertyChanged(nameof(HasTags));
        // The selected tag no longer exists (its last video was untagged): fall back to All videos.
        if (selected is not null && SelectedTag is null) SelectedFolder = Folders[0];
    }

    private IEnumerable<string> AllTags => Tags.Select(t => t.Name);

    // ---------- organising ----------
    [RelayCommand]
    private void ToggleFavorite(VideoItemViewModel? item)
    {
        if (item is null) return;
        item.Entry.Favorite = !item.Entry.Favorite;
        item.RefreshAll();
        _library.SaveCache();
        if (ActiveNav?.Kind == NavKind.Favorites) RefreshView(); else UpdateCounts();
    }

    [RelayCommand]
    private void BeginEdit(VideoItemViewModel? item)
    {
        if (item is not null) Editor = new EditViewModel(item, Folders, AllTags);
    }

    [RelayCommand] private void CancelEdit() => Editor = null;

    [RelayCommand]
    private void SaveEdit()
    {
        if (Editor is not { } ed) return;
        if (ed.TagInput.Trim().Length > 0) ed.AddTagCommand.Execute(null); // don't lose a half-typed tag
        var item = ed.Item;
        var entry = item.Entry;
        var oldPath = entry.Path;
        try
        {
            // Moving to another library folder puts the file at that folder's top level.
            var targetDir = ed.Folder is not null && !ReferenceEquals(ed.Folder, ed.OriginalFolder) ? ed.Folder.Path : null;
            var newPath = _library.Relocate(entry, ed.Name, targetDir);
            if (!string.Equals(newPath, oldPath, StringComparison.Ordinal))
            {
                _items.Remove(oldPath);
                _items[newPath] = item;
            }
        }
        catch (InvalidOperationException ex)
        {
            ed.Error = ex.Message;
            return;
        }
        entry.Tags = ed.Tags.ToList();
        entry.Favorite = ed.Favorite;
        _library.SaveCache();
        item.RefreshAll();
        Editor = null;
        RefreshView();
    }

    private static bool Under(string file, string folder) => new FolderViewModel(folder).Contains(file);

    // ---------- player ----------
    [RelayCommand]
    private async Task OpenAsync(VideoItemViewModel? item)
    {
        if (item is null) return;
        IsPlayerOpen = true;
        await Player.OpenAsync(item);
    }

    [RelayCommand]
    private void ClosePlayer()
    {
        if (!IsPlayerOpen) return;
        Player.Close();
        IsPlayerOpen = false;
        _library.SaveCache();
        if (SortIndex == 2) RefreshView();
    }

    [RelayCommand]
    private void Reveal(VideoItemViewModel? item)
    {
        if (item is not null) PlatformShell.Reveal(item.Entry.Path);
    }

    public void Dispose()
    {
        _scanCts?.Cancel();
        Player.Dispose();
        _library.SaveCache();
        _library.Dispose();
    }
}
