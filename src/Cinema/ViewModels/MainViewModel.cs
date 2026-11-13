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
    public PlayerViewModel Player { get; }

    /// <summary>Set by the view: opens a native folder picker.</summary>
    public Func<Task<string?>>? PickFolder { get; set; }

    [ObservableProperty] private ObservableCollection<VideoItemViewModel> _videos = new();
    [ObservableProperty] private FolderViewModel? _selectedFolder;
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

    public bool HasNoFolders => Folders.Count <= 1;
    public bool HasNoResults => !HasNoFolders && Videos.Count == 0 && !IsScanning;
    public string HeaderTitle => SelectedFolder?.Name ?? "All videos";
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
        Folders.Add(new FolderViewModel(null));
        foreach (var f in _settings.Current.Folders) Folders.Add(new FolderViewModel(f));
        SelectedFolder = Folders.FirstOrDefault(f => f.Path == select) ?? Folders[0];
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
    partial void OnSelectedFolderChanged(FolderViewModel? value) { RefreshView(); OnPropertyChanged(nameof(HeaderTitle)); }
    partial void OnSearchTextChanged(string value) => RefreshView();
    partial void OnSortIndexChanged(int value)
    {
        _settings.Current.SortIndex = value;
        _settings.Save();
        RefreshView();
    }
    partial void OnIsScanningChanged(bool value) => OnPropertyChanged(nameof(HasNoResults));

    private void RefreshView()
    {
        IEnumerable<VideoItemViewModel> q = _items.Values;
        if (SelectedFolder is { IsAll: false } folder) q = q.Where(v => folder.Contains(v.Entry.Path));
        var search = SearchText.Trim();
        if (search.Length > 0)
        {
            var terms = search.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            q = q.Where(v => terms.All(t => v.Entry.Path.Contains(t, StringComparison.OrdinalIgnoreCase)));
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
            f.Count = f.IsAll ? _items.Count : _items.Keys.Count(k => f.Contains(k));
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
