using System.Collections.ObjectModel;
using Avalonia.Threading;
using Cnema.Models;
using Cnema.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cnema.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly SettingsStore _settings;
    private readonly LibraryService _library;
    private readonly Dictionary<string, VideoItemViewModel> _items = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _refreshTimer;
    private CancellationTokenSource? _scanCts;
    private readonly GlobalHotkey _hotkey;
    private CancellationTokenSource? _toastCts;
    private bool _hotkeyRefused;

    public ClipService Clips { get; }

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
    [ObservableProperty] private SettingsViewModel? _settingsEditor;
    [ObservableProperty] private string? _toast;
    public bool IsSettingsOpen => SettingsEditor is not null;
    partial void OnSettingsEditorChanged(SettingsViewModel? value) => OnPropertyChanged(nameof(IsSettingsOpen));

    // ---------- updates, diagnostics, delete ----------
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasUpdate), nameof(UpdateText))] private UpdateInfo? _update;
    public bool HasUpdate => Update is not null;
    public string UpdateText => Update is null ? "" : $"Cnema {Update.Version.ToString(3)} is available";
    public string VersionText => "Version " + UpdateService.CurrentText;

    [RelayCommand] private void OpenUpdate() => PlatformShell.OpenUrl(Update?.Url ?? UpdateService.ReleasesPage);

    private async Task CheckForUpdatesAsync()
    {
        if (!_settings.Current.CheckForUpdates) return;
        await Task.Delay(TimeSpan.FromSeconds(4)); // let startup finish first
        Update = await UpdateService.CheckAsync();
    }

    /// <summary>Set by the view: puts text on the clipboard.</summary>
    public Func<string, Task>? CopyText { get; set; }

    [RelayCommand]
    private async Task CopyDiagnosticsAsync()
    {
        var report = await BuildDiagnosticsAsync();
        if (CopyText is not null) await CopyText(report);
        if (SettingsEditor is { } editor) editor.Error = null;
        ShowToast("Diagnostics copied. Paste them wherever you report the problem.");
    }

    /// <summary>Everything needed to debug a report in one paste. No file names from the library.</summary>
    public async Task<string> BuildDiagnosticsAsync()
    {
        var clip = _settings.Current.Clip;
        var ffmpeg = await CaptureProbe.FindCaptureFfmpegAsync();
        var codecs = ffmpeg is null ? "-" : string.Join(", ", (await CaptureProbe.ListCodecsAsync(ffmpeg)).Select(c => $"{c.Codec}={c.Encoder}"));
        var lines = new List<string>
        {
            $"Cnema {UpdateService.CurrentText}",
            $"OS: {System.Runtime.InteropServices.RuntimeInformation.OSDescription} ({System.Runtime.InteropServices.RuntimeInformation.OSArchitecture})",
            $".NET: {Environment.Version}",
            $"Install: {(AppContext.BaseDirectory.Contains("AppTranslocation") ? "TRANSLOCATED (move the app to /Applications)" : "ok")}",
            $"libVLC: {VlcBootstrap.LibVLC?.Version ?? "MISSING: " + VlcBootstrap.Error}",
            $"ffmpeg: {(ToolLocator.Ffmpeg is null ? "MISSING" : "found")}, capture: {(ffmpeg is null ? "MISSING" : "found")}",
            $"Encoders: {codecs}",
            $"System audio helper: {(CaptureProbe.SystemAudioHelper is null ? "no" : "yes")}",
            $"Library: {_items.Count} videos in {_settings.Current.Folders.Count} folders",
            $"Clipping: enabled={clip.Enabled} state={Clips.State} encoder={Clips.Encoder ?? "-"} error={Clips.Error ?? "-"}",
            $"Clip settings: {clip.Seconds}s {clip.Fps}fps height={clip.Height} {clip.BitrateMbps}Mbps codec={clip.Codec} hw={clip.HardwareEncoder} audio1={(clip.Audio1 is null ? "-" : "set")} audio2={(clip.Audio2 is null ? "-" : "set")} hotkey={clip.HotkeyText} hotkeyRefused={_hotkeyRefused}",
            $"Controller mode: {_settings.Current.ControllerMode}",
            "",
            "--- log ---",
            Log.Tail(80),
        };
        return string.Join(Environment.NewLine, lines);
    }

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsConfirmingDelete), nameof(DeletePrompt))] private VideoItemViewModel? _pendingDelete;
    [ObservableProperty] private string? _deleteError;
    public bool IsConfirmingDelete => PendingDelete is not null;
    public string DeletePrompt => PendingDelete is null ? "" : $"Move “{PendingDelete.Title}” to the {(OperatingSystem.IsWindows() ? "Recycle Bin" : "Trash")}?";

    [RelayCommand]
    private void RequestDelete(VideoItemViewModel? item)
    {
        if (item is null) return;
        Editor = null;
        DeleteError = null;
        PendingDelete = item;
    }

    [RelayCommand] private void CancelDelete() => PendingDelete = null;

    [RelayCommand]
    private void ConfirmDelete()
    {
        if (PendingDelete is not { } item) return;
        try
        {
            _library.Delete(item.Entry);
            _items.Remove(item.Entry.Path);
            PendingDelete = null;
            RefreshView();
            ShowToast("Moved to the trash: " + item.Title);
        }
        catch (Exception ex)
        {
            Log.Warn($"Delete failed: {ex.Message}");
            DeleteError = "Couldn't move it to the trash: " + ex.Message;
        }
    }

    // ---------- controller mode ----------
    public bool IsControllerMode => _settings.Current.ControllerMode;
    public bool ControllerFullscreen => _settings.Current.ControllerFullscreen;
    /// <summary>Raised after settings were saved; the window re-applies layout and input.</summary>
    public event Action? ControllerModeChanged;
    [ObservableProperty] private string _padStatus = "";

    /// <summary>What the pad buttons do right now; shown along the bottom in Controller mode.</summary>
    public IReadOnlyList<PadHint> PadHints
    {
        get
        {
            if (IsSettingsOpen || IsEditing)
                return new PadHint[] { new("A", "Select", "a"), new("B", "Cancel", "b"), new("☰", "Save", "") };
            if (IsPlayerOpen)
                return new PadHint[]
                {
                    new("A", "Play / pause", "a"), new("B", "Back", "b"), new("X", "Mark in", "x"), new("Y", "Mark out", "y"),
                    new("◀ ▶", "Seek", ""), new("LB RB", "Frame", ""), new("LT RT", "10 s", ""), new("☰", "Export clip", ""),
                };
            var hints = new List<PadHint>
            {
                new("A", "Play", "a"), new("B", "Sidebar / videos", "b"), new("X", "Favourite", "x"), new("Y", "Rename & tags", "y"),
                new("LB RB", "Folder", ""), new("☰", "Settings", ""),
            };
            if (IsClipRecording) hints.Add(new PadHint("⧉", SaveClipLabel, ""));
            return hints;
        }
    }

    /// <summary>LB / RB: step through the sidebar (folders, then tags) without moving focus there.</summary>
    public void CycleNav(int delta)
    {
        var all = Folders.Concat(Tags).ToList();
        if (all.Count == 0) return;
        var index = ActiveNav is null ? 0 : all.IndexOf(ActiveNav);
        var next = all[((index + delta) % all.Count + all.Count) % all.Count];
        if (next.Kind == NavKind.Tag) SelectedTag = next;
        else SelectedFolder = next;
    }

    // ---------- clipping status (sidebar + tray) ----------
    public bool IsClipEnabled => _settings.Current.Clip.Enabled;
    public bool IsClipRecording => Clips.State == ClipState.Recording;
    public bool IsClipStarting => Clips.State == ClipState.Starting;
    public bool IsClipError => Clips.State == ClipState.Error;
    public bool HasClipDetail => ClipDetail.Length > 0;
    /// <summary>Close-to-tray applies while clipping is on, or when asked for explicitly.</summary>
    public bool StaysInBackground => _settings.Current.Clip.Enabled || _settings.Current.KeepInBackground;
    public string SaveClipLabel => $"Save last {LengthText(_settings.Current.Clip.Seconds)}";
    public string ClipTitle => Clips.State switch
    {
        ClipState.Recording => "Clipping is on",
        ClipState.Starting => "Starting capture…",
        ClipState.Error => "Clipping can't run",
        _ => "Clipping is off",
    };
    public string ClipDetail => Clips.State switch
    {
        ClipState.Recording => _hotkeyRefused
            ? "The hotkey is taken by another app. Pick a different one in settings."
            : $"{_settings.Current.Clip.HotkeyText} saves the last {LengthText(_settings.Current.Clip.Seconds)}",
        ClipState.Error => Clips.Error ?? "",
        ClipState.Starting => "",
        _ => "Record in the background and save moments after they happen.",
    };
    /// <summary>Raised when tray-relevant state changed (clipping on/off, background mode).</summary>
    public event Action? BackgroundStateChanged;

    private static string LengthText(int seconds) =>
        seconds < 60 ? $"{seconds} s" : seconds % 60 == 0 ? $"{seconds / 60} min" : $"{seconds / 60.0:0.#} min";

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

    public MainViewModel(SettingsStore settings, LibraryService library, ClipService clips)
    {
        _settings = settings;
        _library = library;
        Clips = clips;
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(IsPlayerOpen) or nameof(Editor) or nameof(SettingsEditor) or nameof(IsClipRecording))
                OnPropertyChanged(nameof(PadHints));
        };
        _hotkey = new GlobalHotkey(() => Dispatcher.UIThread.Post(() => SaveClipCommand.Execute(null)));
        Clips.Changed += () => Dispatcher.UIThread.Post(RaiseClipState);
        Clips.ClipSaved += path => Dispatcher.UIThread.Post(() => OnClipSaved(path));
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
        _ = ApplyClipSettingsAsync();
        _ = CheckForUpdatesAsync();
        await RescanAsync();
        await RunStorageCleanupAsync();
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
        if (_pendingTags.Remove(e.Path, out var game))
        {
            // A clip just saved from this game: tag it so the sidebar groups clips per game.
            if (!e.Tags.Contains(game, StringComparer.OrdinalIgnoreCase)) e.Tags.Add(game);
            _items[e.Path].RefreshAll();
            _library.SaveCache();
        }
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

    // ---------- background clipping ----------
    /// <summary>Brings capture, the hotkey and the clips folder in line with the saved settings.</summary>
    public async Task ApplyClipSettingsAsync()
    {
        var clip = _settings.Current.Clip;
        if (clip.Enabled)
        {
            // Saved clips land in a library folder so they show up here by themselves.
            var folder = clip.ResolvedFolder;
            try
            {
                Directory.CreateDirectory(folder);
                if (!_settings.Current.Folders.Any(f => Under(Path.Combine(folder, "x"), f)))
                {
                    _settings.Current.Folders.Add(folder);
                    _settings.Save();
                    RebuildFolders(SelectedFolder?.Path);
                    _ = RescanAsync();
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"Clips folder: {ex.Message}");
            }
            _hotkeyRefused = !_hotkey.Register(clip.HotkeyCtrl, clip.HotkeyAlt, clip.HotkeyShift, clip.HotkeyMeta, clip.HotkeyKey);
        }
        else
        {
            _hotkey.Unregister();
            _hotkeyRefused = false;
        }
        RaiseClipState();
        await Clips.ApplyAsync();
    }

    private void RaiseClipState()
    {
        foreach (var name in new[]
                 {
                     nameof(IsClipEnabled), nameof(IsClipRecording), nameof(IsClipStarting), nameof(IsClipError),
                     nameof(ClipTitle), nameof(ClipDetail), nameof(HasClipDetail), nameof(SaveClipLabel),
                     nameof(StaysInBackground),
                 })
            OnPropertyChanged(name);
        SaveClipCommand.NotifyCanExecuteChanged();
        BackgroundStateChanged?.Invoke();
    }

    private readonly Dictionary<string, string> _pendingTags = new(StringComparer.OrdinalIgnoreCase);

    [RelayCommand(CanExecute = nameof(IsClipRecording))]
    private async Task SaveClipAsync()
    {
        // Ask now, while the game is still in front; saving takes a moment.
        var game = _settings.Current.Clip.TagWithGame ? PlatformShell.FrontmostAppName() : null;
        var path = await Clips.SaveClipAsync(game);
        if (path is null) ShowToast("The clip could not be saved.");
        else if (game is not null)
        {
            _pendingTags[path] = game.ToLowerInvariant();
            // The folder watcher may already have added the clip; tag it now in that case.
            if (_items.TryGetValue(path, out var existing)) Upsert(existing.Entry);
        }
    }

    private void OnClipSaved(string path)
    {
        if (_settings.Current.Clip.Sound) PlatformShell.PlayConfirmSound();
        ShowToast("Clip saved: " + Path.GetFileNameWithoutExtension(path));
        if (_settings.Current.Clip.Notify)
            PlatformShell.Notify("Clip saved", $"Last {LengthText(_settings.Current.Clip.Seconds)} · {Path.GetFileName(path)}");
        _ = RescanAsync();
        _ = CleanupAfterSaveAsync();
    }

    private async Task CleanupAfterSaveAsync()
    {
        // Let the "Clip saved" toast be read before a storage message can replace it.
        await Task.Delay(4000);
        await RunStorageCleanupAsync();
    }

    private void ShowToast(string text)
    {
        _toastCts?.Cancel();
        var cts = _toastCts = new CancellationTokenSource();
        Toast = text;
        _ = Task.Delay(4000, cts.Token).ContinueWith(_ => Dispatcher.UIThread.Post(() => Toast = null),
            TaskContinuationOptions.OnlyOnRanToCompletion);
    }

    [RelayCommand]
    private async Task ToggleClippingAsync()
    {
        _settings.Current.Clip.Enabled = !_settings.Current.Clip.Enabled;
        _settings.Save();
        await ApplyClipSettingsAsync();
    }

    [RelayCommand]
    private void OpenSettings()
    {
        var editor = new SettingsViewModel(_settings.Current) { PickFolder = PickFolder };
        SettingsEditor = editor;
        _ = editor.LoadDevicesAsync();
        _ = editor.LoadStorageUsageAsync();
    }

    [RelayCommand] private void CancelSettings() => SettingsEditor = null;

    [RelayCommand]
    private async Task SaveSettingsAsync()
    {
        if (SettingsEditor is not { } editor) return;
        if (editor.WriteTo(_settings.Current) is { } problem)
        {
            editor.Error = problem;
            return;
        }
        if (editor.LaunchAtLogin != AutostartService.IsEnabled &&
            AutostartService.Set(editor.LaunchAtLogin) is { } autostartProblem)
        {
            editor.Error = autostartProblem;
            return;
        }
        _settings.Save();
        SettingsEditor = null;
        OnPropertyChanged(nameof(IsControllerMode));
        ControllerModeChanged?.Invoke();
        await ApplyClipSettingsAsync();
        await RunStorageCleanupAsync();
    }

    // ---------- storage limits ----------
    private bool _cleaning;

    /// <summary>Applies the age and size limits from Settings to the clips folder. Favourites stay.</summary>
    public async Task RunStorageCleanupAsync()
    {
        var c = _settings.Current.Clip;
        if (_cleaning || (c.KeepDays <= 0 && c.MaxFolderGb <= 0)) return;
        _cleaning = true;
        try
        {
            var favourites = _items.Values.Where(i => i.Entry.Favorite)
                .Select(i => i.Entry.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var result = await Task.Run(() =>
            {
                var plan = StorageService.Plan(c, favourites.Contains, DateTime.UtcNow);
                return plan.Count == 0 ? null : StorageService.Apply(c, plan);
            });
            if (result is { Removed: > 0 }) _ = RescanAsync();
            if (result is { Removed: > 0 })
                ShowToast($"Storage limit: {result.Removed} old clip{(result.Removed == 1 ? "" : "s")} " +
                          $"({VideoItemViewModel.FormatSize(result.Bytes)}) moved to the {(result.Archived ? "archive" : "trash")}.");
        }
        catch (Exception ex)
        {
            Log.Warn($"Storage cleanup failed: {ex.Message}");
        }
        finally
        {
            _cleaning = false;
        }
    }

    public void Dispose()
    {
        _hotkey.Dispose();
        Clips.Dispose();
        _scanCts?.Cancel();
        Player.Dispose();
        _library.SaveCache();
        _library.Dispose();
    }
}
