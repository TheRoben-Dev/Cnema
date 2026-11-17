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
    /// <summary>The extra save-clip hotkeys, each with its own length.</summary>
    private readonly List<GlobalHotkey> _extraHotkeys = new();
    /// <summary>Extra hotkeys another app already holds, as shown text.</summary>
    private readonly List<string> _refusedExtras = new();

    public ClipService Clips { get; }

    public static LocalizedText[] SortModes { get; } = LocalizedText.List("Name", "Recently modified", "Recently played", "Longest", "Largest", "Best moments");

    public ObservableCollection<FolderViewModel> Folders { get; } = new();
    public ObservableCollection<FolderViewModel> Tags { get; } = new();
    /// <summary>Everyone tagged under "Who was there", as sidebar rows (Settings, Features, Memories).</summary>
    public ObservableCollection<FolderViewModel> Friends { get; } = new();
    public bool HasFriends => Features.People && Friends.Count > 0;
    [ObservableProperty] private FolderViewModel? _selectedFriend;
    public PlayerViewModel Player { get; }
    /// <summary>Exports running in the background, one at a time (Settings, Features, Export queue).</summary>
    public ExportQueueViewModel Exports { get; } = new();

    /// <summary>Set by the view: opens a native folder picker.</summary>
    public Func<Task<string?>>? PickFolder { get; set; }
    /// <summary>Set by the view: (title, image or video) to a picked file.</summary>
    public Func<string, bool, Task<string?>>? PickFile { get; set; }

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
    public string UpdateText => Update is null ? ""
        : IsInstallingUpdate ? F("Updating to {0}…", Update.Version.ToString(3))
        : F("Savefile {0} is available", Update.Version.ToString(3));
    public string UpdateAction => IsInstallingUpdate
        ? (UpdateProgress >= 1 ? T("Installing, Savefile will restart") : F("Downloading {0:0}%", UpdateProgress * 100))
        : UpdateService.WhyNoSelfUpdate() is null && Update?.AssetUrl is not null ? T("Install and restart") : T("Get the update");

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(UpdateText), nameof(UpdateAction))] private bool _isInstallingUpdate;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(UpdateAction))] private double _updateProgress;
    /// <summary>Raised when the app has to exit so the update can replace it.</summary>
    public event Action? QuitRequested;

    /// <summary>Downloads the update, hands over to the installer script and asks the app to quit.</summary>
    public async Task<bool> InstallUpdateAsync(bool background)
    {
        if (Update is not { AssetUrl: not null } info || IsInstallingUpdate) return false;
        if (UpdateService.WhyNoSelfUpdate() is { } why)
        {
            UpdateStatus = F("Can't install automatically: {0}.", why);
            return false;
        }
        IsInstallingUpdate = true;
        UpdateProgress = 0;
        try
        {
            var progress = new Progress<double>(p => UpdateProgress = Math.Min(p, 0.999));
            var staged = await UpdateService.DownloadAsync(info, progress, CancellationToken.None);
            // A session recording is saved by the normal shutdown path before the swap happens.
            UpdateProgress = 1;
            UpdateService.ApplyAndRestart(staged, background);
            QuitRequested?.Invoke();
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("Installing the update failed: " + ex.Message);
            UpdateStatus = T("The update could not be installed: ") + ex.Message;
            ShowToast(T("The update could not be installed. Settings has the details."));
            IsInstallingUpdate = false;
            return false;
        }
    }

    // ---------- what's new ----------
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsShowingWhatsNew))] private IReadOnlyList<ChangelogEntry>? _whatsNew;
    public bool IsShowingWhatsNew => WhatsNew is { Count: > 0 };
    public string WhatsNewTitle => F("What's new in Savefile {0}", UpdateService.CurrentText);

    [RelayCommand] private void CloseWhatsNew() => WhatsNew = null;

    /// <summary>The whole changelog, from the button in Settings.</summary>
    [RelayCommand]
    private void ShowChangelog()
    {
        SettingsEditor = null;
        WhatsNew = ChangelogService.All.Where(e => e.Version <= UpdateService.Current).ToList();
    }

    /// <summary>After an update, shows the entries for every version since the one that ran last.</summary>
    private void ShowWhatsNewAfterUpdate(bool firstRunEver)
    {
        var last = UpdateService.Parse(_settings.Current.LastRunVersion);
        if (last == UpdateService.Current) return;
        // A first install has nothing to compare with; an older build that never stored its
        // version did, so it gets the current version's notes.
        if (!firstRunEver && (last is null || last < UpdateService.Current))
        {
            var entries = ChangelogService.Since(last);
            if (entries.Count > 0) WhatsNew = entries;
        }
        _settings.Current.LastRunVersion = UpdateService.CurrentText;
        _settings.Current.LastUpdateAttempt = null;
        _settings.Save();
    }
    public string VersionText => F("Version {0}", UpdateService.CurrentText);

    /// <summary>The update card: install in place when that is possible, otherwise open the download page.</summary>
    [RelayCommand]
    private async Task OpenUpdateAsync()
    {
        if (IsInstallingUpdate) return;
        if (Update?.AssetUrl is not null && UpdateService.WhyNoSelfUpdate() is null) await InstallUpdateAsync(background: false);
        else PlatformShell.OpenUrl(Update?.Url ?? UpdateService.ReleasesPage);
    }

    private async Task CheckForUpdatesAsync()
    {
        if (!_settings.Current.CheckForUpdates) return;
        await Task.Delay(TimeSpan.FromSeconds(4)); // let startup finish first
        await CheckForUpdatesCoreAsync(announce: true);
        // At launch a newer version is installed straight away. Each version is tried once: if the
        // install did not take, the card stays and the next launch does not loop.
        if (_settings.Current.AutoUpdate && Update is { AssetUrl: not null } found &&
            UpdateService.WhyNoSelfUpdate() is null && !Clips.IsSession &&
            _settings.Current.LastUpdateAttempt != found.Version.ToString(3))
        {
            _settings.Current.LastUpdateAttempt = found.Version.ToString(3);
            _settings.Save();
            if (await InstallUpdateAsync(background: Environment.GetCommandLineArgs().Contains("--background"))) return;
        }
        // Cnema usually stays running in the background for days, so one check at launch is not
        // enough: a release published after login would never be seen.
        if (_updateTimer is null)
        {
            _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(3) };
            _updateTimer.Tick += async (_, _) =>
            {
                if (_settings.Current.CheckForUpdates) await CheckForUpdatesCoreAsync(announce: true);
            };
            _updateTimer.Start();
        }
    }

    private DispatcherTimer? _updateTimer;
    private Version? _announced;
    [ObservableProperty] private string _updateStatus = "";

    private async Task<string?> CheckForUpdatesCoreAsync(bool announce)
    {
        var (found, error) = await UpdateService.CheckDetailedAsync();
        if (error is null)
        {
            Update = found;
            // A later check that went through replaces the message of one that failed.
            if (UpdateStatus.StartsWith(T("Couldn't check for updates: "), StringComparison.Ordinal)) UpdateStatus = "";
        }
        else if (announce) UpdateStatus = T("Couldn't check for updates: ") + error;
        // One system notification per new version, so it is noticed while Cnema sits in the tray.
        OnPropertyChanged(nameof(UpdateAction));
        if (announce && found is not null && found.Version != _announced)
        {
            _announced = found.Version;
            PlatformShell.Notify(T("Savefile update"), F("Version {0} is available. Open Savefile to get it.", found.Version.ToString(3)));
        }
        BackgroundStateChanged?.Invoke();
        return error;
    }

    /// <summary>The "Check now" button in Settings: always says what it found.</summary>
    [RelayCommand]
    private async Task CheckForUpdatesNowAsync()
    {
        UpdateStatus = T("Checking…");
        var error = await CheckForUpdatesCoreAsync(announce: false);
        UpdateStatus = error is not null ? T("Couldn't check for updates: ") + error
            : Update is { } u ? F("Version {0} is available.", u.Version.ToString(3))
            : F("You have the latest version ({0}).", UpdateService.CurrentText);
    }

    /// <summary>Set by the view: puts text on the clipboard.</summary>
    public Func<string, Task>? CopyText { get; set; }

    [RelayCommand]
    private async Task CopyDiagnosticsAsync()
    {
        var report = await BuildDiagnosticsAsync();
        if (CopyText is not null) await CopyText(report);
        if (SettingsEditor is { } editor) editor.Error = null;
        ShowToast(T("Diagnostics copied. Paste them wherever you report the problem."));
    }

    /// <summary>Everything needed to debug a report in one paste. No file names from the library.</summary>
    public async Task<string> BuildDiagnosticsAsync()
    {
        var clip = _settings.Current.Clip;
        var ffmpeg = await CaptureProbe.FindCaptureFfmpegAsync();
        var codecs = ffmpeg is null ? "-" : string.Join(", ", (await CaptureProbe.ListCodecsAsync(ffmpeg)).Select(c => $"{c.Codec}={c.Encoder}"));
        var lines = new List<string>
        {
            $"Savefile {UpdateService.CurrentText}",
            $"OS: {System.Runtime.InteropServices.RuntimeInformation.OSDescription} ({System.Runtime.InteropServices.RuntimeInformation.OSArchitecture})",
            $".NET: {Environment.Version}",
            $"Install: {(AppContext.BaseDirectory.Contains("AppTranslocation") ? "TRANSLOCATED (move the app to /Applications)" : "ok")}",
            $"libVLC: {VlcBootstrap.LibVLC?.Version ?? "MISSING: " + VlcBootstrap.Error}",
            $"ffmpeg: {(ToolLocator.Ffmpeg is null ? "MISSING" : "found")}, capture: {(ffmpeg is null ? "MISSING" : "found")}",
            $"Encoders: {codecs}",
            $"System audio helper: {(CaptureProbe.SystemAudioHelper is null ? "no" : "yes")}",
            $"Library: {_items.Count} videos in {_settings.Current.Folders.Count} folders",
            $"Clipping: enabled={clip.Enabled} state={Clips.State} encoder={Clips.Encoder ?? "-"} error={Clips.Error ?? "-"}",
            $"Clip settings: {clip.Seconds}s {clip.Fps}fps height={clip.Height} {clip.BitrateMbps}Mbps codec={clip.Codec} hw={clip.HardwareEncoder} audio1={(clip.Audio1 is null ? "-" : "set")} audio2={(clip.Audio2 is null ? "-" : "set")} hotkey={clip.HotkeyText} hotkeyRefused={_hotkeyRefused} extraHotkeys={clip.ExtraHotkeys.Count} extraRefused={_refusedExtras.Count}",
            SmartDiagnostics(),
            "",
            "--- log ---",
            Log.Tail(80),
        };
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>The smart features in diagnostics: switched on, model there, how far, what they're doing now, errors.</summary>
    private string SmartDiagnostics()
    {
        var all = _items.Values.Select(v => v.Entry).ToList();
        string failed;
        lock (_workFailed) failed = string.Join(" ", _workFailed.Select(kv => $"{kv.Key}={kv.Value}"));
        return string.Join(Environment.NewLine,
            $"Smart: moments={Features.Highlights} {all.Count(e => e.Smart is { Version: SmartAnalyzer.Version })}/{all.Count}" +
            $" | speech={Features.Speech} model={SpeechModelReady} {all.Count(e => e.Speech is { Version: SpeechService.Version })}/{all.Count} with words {all.Count(e => e.Speech is { Lines.Count: > 0 })}" +
            $" | visual={Features.Visual} model={VisualModelReady} {all.Count(e => e.Visual is { Version: VisualService.Version })}/{all.Count} with frames {all.Count(e => e.Visual is { Vectors.Length: > 0 })}" +
            $" | sound={Features.Laughs} model={SoundModelReady} {all.Count(e => e.Sound is { Version: SoundService.Version })}/{all.Count}" +
            $" | screen={Features.ScreenText} model={ScreenModelReady} {all.Count(e => e.Screen is { Version: ScreenService.Version })}/{all.Count} with text {all.Count(e => e.Screen is { Lines.Count: > 0 })}",
            $"Smart now: moments=[{SmartActivity}] speech=[{SpeechActivity}] visual=[{VisualActivity}] sound=[{SoundActivity}] screen=[{ScreenActivity}] wait=[{WaitReason}]",
            $"Smart errors: speech={SpeechModelError ?? "-"} visual={VisualModelError ?? "-"} sound={SoundModelError ?? "-"} screen={ScreenModelError ?? "-"} failed={(failed.Length == 0 ? "-" : failed)}");
    }

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsConfirmingDelete), nameof(DeletePrompt))] private VideoItemViewModel? _pendingDelete;
    [ObservableProperty] private string? _deleteError;
    public bool IsConfirmingDelete => PendingDelete is not null || PendingBulkDelete is not null;
    public string DeletePrompt => PendingBulkDelete is { } many
        ? F(many.Count == 1 ? "Move {0} video to the {1}?" : "Move {0} videos to the {1}?", many.Count, TrashName)
        : PendingDelete is null ? "" : F("Move “{0}” to the {1}?", PendingDelete.Title, TrashName);

    [RelayCommand]
    private void RequestDelete(VideoItemViewModel? item)
    {
        if (item is null) return;
        Editor = null;
        DeleteError = null;
        PendingDelete = item;
    }

    [RelayCommand] private void CancelDelete() { PendingDelete = null; PendingBulkDelete = null; }

    [RelayCommand]
    private void ConfirmDelete()
    {
        if (PendingBulkDelete is { } many)
        {
            var failed = DeleteMany(many);
            PendingBulkDelete = null;
            IsSelecting = false;
            RefreshView();
            ShowToast(failed == 0 ? F(many.Count == 1 ? "Moved {0} video to the {1}" : "Moved {0} videos to the {1}", many.Count, TrashName)
                : F(failed == 1 ? "{0} video could not be moved to the {1}" : "{0} videos could not be moved to the {1}", failed, TrashName));
            return;
        }
        if (PendingDelete is not { } item) return;
        try
        {
            if (ReferenceEquals(Player.Current, item))
            {
                // Let go of the file before it moves.
                Player.Close();
                IsMiniPlayerOpen = false;
                IsPlayerOpen = false;
            }
            RemoveVideo(item);
            PendingDelete = null;
            RefreshView();
            ShowToast(F("Moved to the {0}: {1}", TrashName, item.Title));
        }
        catch (Exception ex)
        {
            Log.Warn($"Delete failed: {ex.Message}");
            DeleteError = F("Couldn't move it to the {0}: {1}", TrashName, ex.Message);
        }
    }

    // ---------- clipping status (sidebar + tray) ----------
    public bool IsClipEnabled => _settings.Current.Clip.Enabled;
    public bool IsClipRecording => Clips.State == ClipState.Recording;
    public bool IsClipStarting => Clips.State == ClipState.Starting;
    public bool IsClipError => Clips.State == ClipState.Error;
    public bool HasClipDetail => ClipDetail.Length > 0;
    /// <summary>Close-to-tray applies while clipping is on, or when asked for explicitly.</summary>
    public bool StaysInBackground => _settings.Current.Clip.Enabled || _settings.Current.KeepInBackground;
    /// <summary>Clip settings with the active game's profile applied.</summary>
    private ClipSettings EffectiveClip => _settings.Current.Clip.For(Clips.ActiveGame);
    public string SaveClipLabel => IsSession ? T("Add bookmark") : F("Save last {0}", LengthText(EffectiveClip.Seconds));

    // ---------- session recording ----------
    public bool IsSession => Clips.IsSession;
    public bool CanToggleSession => IsClipRecording || IsSession;
    public string SessionLabel => IsSession
        ? F("Stop session · {0}", Controls.TrimTimeline.FormatTime(Clips.SessionElapsed.TotalMilliseconds))
        : T("Record session");
    private readonly Dictionary<string, IReadOnlyList<long>> _pendingBookmarks = new(StringComparer.OrdinalIgnoreCase);
    private DispatcherTimer? _sessionTimer;
    private bool _savingSession;

    [RelayCommand(CanExecute = nameof(CanToggleSession))]
    private async Task ToggleSessionAsync()
    {
        if (_savingSession) return;
        if (!IsSession)
        {
            if (!Features.Sessions) return;
            var game = _settings.Current.Clip.TagWithGame ? PlatformShell.FrontmostAppName() : null;
            if (Clips.StartSession(game))
                ShowToast(F("Session recording started. {0} adds a bookmark.", _settings.Current.Clip.HotkeyText));
            return;
        }
        _savingSession = true;
        ShowToast(T("Saving the session…"));
        try
        {
            if (await Clips.StopSessionAsync() is null) ShowToast(T("The session could not be saved."));
        }
        finally
        {
            _savingSession = false;
        }
    }

    private void OnSessionSaved(string path, IReadOnlyList<long> bookmarks)
    {
        _pendingBookmarks[path] = bookmarks;
        QueueSquad(path);
        QueueAutoBackup();
        var name = Path.GetFileNameWithoutExtension(path);
        var tag = name.EndsWith(" session", StringComparison.Ordinal) ? null : SessionGame(name);
        if (tag is not null) _pendingTags[path] = tag;
        if (_settings.Current.Clip.Sound) PlatformShell.PlayConfirmSound();
        var marks = bookmarks.Count == 0 ? "" : ", " + P(bookmarks.Count, "{0} bookmark", "{0} bookmarks");
        ShowToast(F("Session saved{0}: {1}", marks, name));
        if (_settings.Current.Clip.Notify) PlatformShell.Notify(T("Session saved"), Path.GetFileName(path));
        _ = RescanAsync();
    }

    /// <summary>"Warframe session 2026-11-14 21-03-10" gives "warframe"; a plain "Session ..." gives null.</summary>
    private static string? SessionGame(string fileName)
    {
        var i = fileName.IndexOf(" session ", StringComparison.Ordinal);
        return i > 0 ? fileName[..i].ToLowerInvariant() : null;
    }

    // ---------- per-game profiles ----------
    private DispatcherTimer? _profileTimer;
    private DateTime _lastProfileMatch = DateTime.MinValue;
    private bool _checkingProfile;

    /// <summary>Watches which app is in front while profiles exist, and switches the capture to its profile.</summary>
    private void UpdateProfileWatch()
    {
        var wanted = _settings.Current.Clip.Enabled && _settings.Current.Clip.Profiles.Any(p => p.Game.Trim().Length > 0);
        if (!wanted)
        {
            _profileTimer?.Stop();
            Clips.ActiveGame = null;
            return;
        }
        if (_profileTimer is null)
        {
            _profileTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            _profileTimer.Tick += async (_, _) => await CheckProfileAsync();
        }
        _profileTimer.Start();
    }

    private async Task CheckProfileAsync()
    {
        if (_checkingProfile || Clips.IsSession) return; // never restart capture in the middle of a session
        _checkingProfile = true;
        try
        {
            var clip = _settings.Current.Clip;
            var front = await Task.Run(PlatformShell.FrontmostAppName);
            var match = clip.ProfileFor(front);
            var current = clip.ProfileFor(Clips.ActiveGame);
            if (match is not null) _lastProfileMatch = DateTime.UtcNow;
            // Sticky: alt-tabbing to a browser or to Cnema keeps the game's profile, because switching
            // bitrate or format restarts the capture and empties the replay buffer.
            if (match is null && (current is null || DateTime.UtcNow - _lastProfileMatch < TimeSpan.FromMinutes(2))) return;
            if (ReferenceEquals(match, current)) return;

            var before = clip.For(Clips.ActiveGame);
            Clips.ActiveGame = match is null ? null : front;
            var after = clip.For(Clips.ActiveGame);
            Log.Info(match is null ? "Game profile off" : $"Game profile on: {match.Game}");
            if (before.BitrateMbps != after.BitrateMbps || before.Codec != after.Codec) await Clips.ApplyAsync();
            RaiseClipState();
        }
        catch (Exception ex)
        {
            Log.Warn($"Profile check failed: {ex.Message}");
        }
        finally
        {
            _checkingProfile = false;
        }
    }

    public string ClipTitle => Clips.State switch
    {
        ClipState.Recording when IsSession => T("Recording session"),
        ClipState.Recording => T("Clipping is on"),
        ClipState.Starting => T("Starting capture…"),
        ClipState.Error => T("Clipping can't run"),
        _ => T("Clipping is off"),
    };
    public string ClipDetail => Clips.State switch
    {
        ClipState.Recording when IsSession =>
            F("Recording a session. {0} adds a bookmark ({1} so far).", _settings.Current.Clip.HotkeyText, Clips.BookmarkCount),
        ClipState.Recording => _hotkeyRefused || _refusedExtras.Count > 0
            ? F(RefusedHotkeys.Count == 1 ? "{0} is taken by another app. Pick a different one in settings." : "{0} are taken by another app. Pick different ones in settings.",
                string.Join(T(" and "), RefusedHotkeys))
            : Clips.CameraNote is { } note ? $"{HotkeySummary}. {note}" : HotkeySummary,
        ClipState.Error => Clips.Error ?? "",
        ClipState.Starting => "",
        _ => T("Record in the background and save moments after they happen."),
    };
    /// <summary>Raised when tray-relevant state changed (clipping on/off, background mode).</summary>
    public event Action? BackgroundStateChanged;

    private List<string> RefusedHotkeys =>
        (_hotkeyRefused ? new[] { _settings.Current.Clip.HotkeyText } : Array.Empty<string>()).Concat(_refusedExtras).ToList();

    /// <summary>"Ctrl+Alt+S saves the last 30 seconds, Ctrl+Alt+D the last 2 minutes".</summary>
    private string HotkeySummary
    {
        get
        {
            var c = _settings.Current.Clip;
            var text = F("{0} saves the last {1}", c.HotkeyText, LengthText(EffectiveClip.Seconds));
            foreach (var h in c.ExtraHotkeys) text += F(", {0} the last {1}", h.Text, LengthText(h.Seconds));
            return text;
        }
    }

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
    private FolderViewModel? ActiveNav => SelectedTag ?? SelectedFriend ?? SelectedCollection ?? SelectedFolder;
    public string HeaderTitle => ActiveNav switch
    {
        _ when SimilarTo is { } like => F("Like {0}", like.Title),
        { Kind: NavKind.Tag } t => "#" + t.Name,
        { Kind: NavKind.Person } p => F("With {0}", p.Name),
        { } n => n.Name,
        null => T("All videos"),
    };
    public string HeaderCount => FriendlyCount();

    public MainViewModel(SettingsStore settings, LibraryService library, ClipService clips)
    {
        _settings = settings;
        CostMeter.Attach(settings);
        _library = library;
        Clips = clips;
        _hotkey = new GlobalHotkey(() => Dispatcher.UIThread.Post(() => SaveClipCommand.Execute(null)));
        Clips.Changed += () => Dispatcher.UIThread.Post(RaiseClipState);
        Clips.ClipSaved += (path, seconds) => Dispatcher.UIThread.Post(() => OnClipSaved(path, seconds));
        Clips.SessionSaved += (path, marks) => Dispatcher.UIThread.Post(() => OnSessionSaved(path, marks));
        Player = new PlayerViewModel(settings);
        Player.Exported += path => { _ = RescanAsync(); };
        Exports.Finished += OnExportFinished;
        _sortIndex = Math.Clamp(settings.Current.SortIndex, 0, SortModes.Length - 1);

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _refreshTimer.Tick += (_, _) => { _refreshTimer.Stop(); RefreshView(); };

        _library.FoldersChanged += () => Dispatcher.UIThread.Post(() => _ = RescanAsync());
        _library.GameTagger = GameTagFor;
        MontageService.KnownBestMoment = path => Features.Highlights && _items.TryGetValue(path, out var v) && v.Entry.Smart is { BestMs: >= 0 } sm ? sm.BestMs : null;
        Player.Finished += OnPlayerFinished;
        VideoItemViewModel.BackedUp = e => Features.Backup && _backup.IsBackedUp(e);
        // A file renamed or moved outside the app keeps its collections and remembered edits.
        _library.EntryMoved += (from, to) => Dispatcher.UIThread.Post(() =>
        {
            CollectionPathChanged(from, to);
            EditStore.Move(from, to);
            _backup.Moved(from, to);
        });
        MigrateFolderMode();
        RebuildFolders();
        Loc.Changed += OnLanguageChanged;
        _setupLanguage = SetupLanguages.FirstOrDefault(l => l.Code == Loc.Current);
    }

    /// <summary>Re-reads every text property so the window follows a language change straight away.</summary>
    private void OnLanguageChanged()
    {
        foreach (var f in Folders) f.Refresh();
        OnPropertyChanged(string.Empty);
        Player.RefreshTexts();
        Exports.RefreshTexts();
        SettingsEditor?.RefreshTexts();
        BackgroundStateChanged?.Invoke();
    }

    /// <summary>Show the cached library instantly, then rescan in the background.</summary>
    public async Task InitializeAsync()
    {
        foreach (var e in _library.CachedEntries.Where(e => _settings.Current.Folders.Any(f => Under(e.Path, f))))
            _items[e.Path] = new VideoItemViewModel(e);
        RefreshView();
        ShowWhatsNewAfterUpdate(firstRunEver: _settings.IsFirstRun);
        VideoItemViewModel.HoverPreview = Features.HoverPreview;
        VideoItemViewModel.UnwatchedBadges = Features.Unwatched;
        VideoItemViewModel.NotesEnabled = Features.Notes;
        VideoItemViewModel.PeopleEnabled = Features.People;
        PlayerViewModel.TrimSuggestions = Features.TrimSuggestions;
        ThumbnailService.SmartThumbs = Features.SmartThumbs;
        SmartCrop.Enabled = Features.SmartCrop;
        ScreenWords.Enabled = Features.ScreenText;
        Chapters.Enabled = Features.Chapters;
        Player.Queue = Features.ExportQueue ? Exports : null;
        RebuildCollections();
        IsSetupOpen = _settings.IsFirstRun || Environment.GetEnvironmentVariable("CNEMA_SETUP") == "1";
        _ = RecoverThenApplyClipsAsync();
        _ = Task.Run(TrimDragCopies);
        StartBin();
        _ = CheckForUpdatesAsync();
        LoadWrappedSongs();
        StartWrappedSeason();
        ResumeWrapped();
        await RescanAsync();
        await RunStorageCleanupAsync();
        await ImportRecordersAtLaunchAsync();
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
        RaiseMergeSuggestion();
        UpdateCounts();
    }

    [RelayCommand]
    private async Task AddFolderAsync()
    {
        if (PickFolder is null) return;
        var path = await PickFolder();
        if (string.IsNullOrWhiteSpace(path)) return;
        path = NormalizeFolder(path);
        if (path.Length == 0) path = "/";
        if (_settings.Current.Folders.Any(f => string.Equals(f, path, StringComparison.OrdinalIgnoreCase))) return;
        // One library folder (the default): the picked folder replaces it, and clips are saved there too.
        if (!SeveralFolders && LibraryFolders().Count > 0) { await UseLibraryFolderAsync(path, clipsToo: true); return; }
        if (!SeveralFolders) _settings.Current.Clip.Folder = path;
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
        ScanStatus = T("Scanning folders…");
        ScanProgress = 0;
        try
        {
            var progress = new Progress<(int done, int total)>(p =>
            {
                if (p.total == 0) return;
                ScanStatus = F("Indexing {0}/{1}", p.done, p.total);
                ScanProgress = 100.0 * p.done / p.total;
            });
            var found = await _library.ScanAsync(folders, e => Dispatcher.UIThread.Post(() => Upsert(e)), progress, cts.Token);
            if (cts.IsCancellationRequested) return;

            var live = new HashSet<string>(found.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
            foreach (var gone in _items.Keys.Where(k => !live.Contains(k)).ToList()) _items.Remove(gone);
            RefreshView();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Player.ShowToast(T("Scan failed: ") + ex.Message); }
        finally
        {
            if (_scanCts == cts)
            {
                IsScanning = false;
                ScanStatus = "";
                OnPropertyChanged(nameof(HasNoResults));
                RaiseEmpty();
                StartSmartPass();
                StartSpeechPass();
                StartVisualPass();
                StartSoundPass();
                StartVoicePass();
            }
        }
    }

    private void Upsert(VideoEntry e)
    {
        if (_items.TryGetValue(e.Path, out var vm)) { if (!ReferenceEquals(vm.Entry, e)) vm.Update(e); }
        else _items[e.Path] = new VideoItemViewModel(e);
        if (_pendingBookmarks.Remove(e.Path, out var marks))
        {
            e.Bookmarks = marks.ToList();
            _library.SaveCache();
        }
        if (_pendingPeople.Remove(e.Path, out var squad))
        {
            foreach (var name in squad)
                if (!e.People.Contains(name, StringComparer.OrdinalIgnoreCase)) e.People.Add(name);
            _items[e.Path].RefreshAll();
            _library.SaveCache();
        }
        if (_pendingTags.Remove(e.Path, out var game))
        {
            // A clip just saved from this game: tag it so the sidebar groups clips per game.
            if (!e.Tags.Contains(game, StringComparer.OrdinalIgnoreCase)) e.Tags.Add(game);
            RememberGame(game);
            _items[e.Path].RefreshAll();
            _library.SaveCache();
        }
        // A new clip with a recorder's made-up name: a smart title, once its game and friends are on it.
        if (Features.AutoTitles && _items.TryGetValue(e.Path, out var fresh) && fresh.Entry.Title is null) AutoTitle(fresh);
        if (!_refreshTimer.IsEnabled) _refreshTimer.Start();
    }

    // ---------- filtering / sorting ----------
    // The sidebar is two lists (library + tags) that behave as one selection.
    private bool _rebuildingTags;

    // Three lists (library, collections, tags) act as one selection: picking in one clears the others.
    private bool _navSwitching;

    partial void OnSelectedFolderChanged(FolderViewModel? value) => NavChanged(value);
    partial void OnSelectedTagChanged(FolderViewModel? value) => NavChanged(value);
    partial void OnSelectedFriendChanged(FolderViewModel? value) => NavChanged(value);
    partial void OnSelectedCollectionChanged(FolderViewModel? value)
    {
        OnPropertyChanged(nameof(IsInCollectionView));
        NavChanged(value);
    }

    private void NavChanged(FolderViewModel? value)
    {
        if (_rebuildingTags || _navSwitching) return;
        if (value is not null) SimilarTo = null;
        if (value is null)
        {
            // A list let go of its selection; unless another list holds one, fall back to All videos.
            if (SelectedFolder is null && SelectedTag is null && SelectedFriend is null && SelectedCollection is null && Folders.Count > 0)
                SelectedFolder = Folders[0];
            return;
        }
        _navSwitching = true;
        try
        {
            if (value.Kind != NavKind.Tag) SelectedTag = null;
            if (value.Kind != NavKind.Person) SelectedFriend = null;
            if (value.Kind != NavKind.Collection) SelectedCollection = null;
            if (value.Kind is NavKind.Tag or NavKind.Collection or NavKind.Person) SelectedFolder = null;
        }
        finally { _navSwitching = false; }
        RefreshView();
        OnPropertyChanged(nameof(HeaderTitle));
    }

    partial void OnEditorChanged(EditViewModel? value) => OnPropertyChanged(nameof(IsEditing));
    partial void OnSearchTextChanged(string value)
    {
        if (value.Length > 0) SimilarTo = null;
        RefreshView();
    }
    partial void OnSortIndexChanged(int value)
    {
        _settings.Current.SortIndex = value;
        _settings.Save();
        RefreshView();
    }
    partial void OnIsScanningChanged(bool value)
    {
        OnPropertyChanged(nameof(HasNoResults));
        RaiseEmpty();
        OnPropertyChanged(nameof(IsScanIndeterminate));
    }

    private void RefreshView()
    {
        if (SimilarTo is { } like)
        {
            // Videos like this one: their own order (most alike first), no other filtering.
            if (!_items.ContainsKey(like.Entry.Path) || !CanFindSimilar(like)) { SimilarTo = null; }
            else
            {
                Videos = new ObservableCollection<VideoItemViewModel>(SimilarVideos(like));
                MarkSaid(Array.Empty<VideoItemViewModel>(), "");
                MarkSeen(new());
                AfterRefresh();
                return;
            }
        }
        IEnumerable<VideoItemViewModel> q = _items.Values;
        if (ActiveNav is { IsAll: false } nav) q = q.Where(nav.Matches);
        var pool = q;
        var search = SearchText.Trim();
        if (search.Length > 0)
        {
            var terms = search.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            // A term matches the path, a tag, the note or a friend; "#clutch" matches tags only, "@sam" friends only.
            var notes = Features.Notes;
            var people = Features.People;
            var said = Features.Speech;
            var screen = Features.ScreenText;
            q = q.Where(v => terms.All(t => t.StartsWith('#')
                ? t.Length > 1 && v.Entry.Tags.Any(tag => TextMatch.Has(tag, t[1..]))
                : t.StartsWith('@')
                ? t.Length > 1 && people && v.Entry.People.Any(p => TextMatch.Has(p, t[1..]))
                : TextMatch.Has(v.Entry.Path, t) ||
                  (v.Entry.Title is { } title && TextMatch.Has(title, t)) ||
                  v.Entry.Tags.Any(tag => TextMatch.Has(tag, t)) ||
                  (notes && v.Entry.Note is { } n && TextMatch.Has(n, t)) ||
                  (people && v.Entry.People.Any(p => TextMatch.Has(p, t))) ||
                  MatchesWhen(v.Entry, t) ||
                  (said && SaidMatches(v.Entry, t)) ||
                  (screen && ScreenMatches(v.Entry, t))));
        }
        q = ApplyFilters(q);
        q = SortIndex switch
        {
            1 => q.OrderByDescending(v => v.Entry.ModifiedUtc),
            2 => q.OrderByDescending(v => v.Entry.LastPlayedUtc ?? DateTime.MinValue).ThenByDescending(v => v.Entry.ModifiedUtc),
            3 => q.OrderByDescending(v => v.Entry.DurationMs),
            4 => q.OrderByDescending(v => v.Entry.Size),
            5 => q.OrderByDescending(v => v.Entry.Smart?.Excitement ?? -1).ThenByDescending(v => v.Entry.ModifiedUtc),
            _ => q.OrderBy(v => v.Title, StringComparer.CurrentCultureIgnoreCase),
        };
        var list = q.ToList();
        // Then the videos that only look like the words (visual search), best first.
        var seen = search.Length > 0 ? SeenMatches(ApplyFilters(pool), search) : new();
        var have = new HashSet<VideoItemViewModel>(list);
        seen = seen.Where(s => !have.Contains(s.Item)).ToList();
        list.AddRange(seen.Select(s => s.Item));
        Videos = new ObservableCollection<VideoItemViewModel>(list);
        MarkSaid(search.Length > 0 ? Videos : Array.Empty<VideoItemViewModel>(), search);
        MarkSeen(seen);
        AfterRefresh();
    }

    private void AfterRefresh()
    {
        SelectionChanged();
        RefreshMemories();
        OnPropertyChanged(nameof(HeaderCount));
        OnPropertyChanged(nameof(HasNoResults));
        RaiseEmpty();
        OnPropertyChanged(nameof(CanPlayAll));
        OnPropertyChanged(nameof(ShowFriendMontage));
        OnPropertyChanged(nameof(ShowFriendWrapped));
        OnPropertyChanged(nameof(FriendWrappedLabel));
        UpdateCounts();
    }

    /// <summary>
    /// Search words about when and what: a year ("2025"), a month ("march", "mar"), "today",
    /// "yesterday", and "favourite" / "fav".
    /// </summary>
    private static bool MatchesWhen(Models.VideoEntry e, string term)
    {
        var t = term.ToLowerInvariant();
        var when = e.ModifiedUtc.ToLocalTime();
        if (t.Length == 4 && int.TryParse(t, out var year) && year is >= 1990 and <= 2100) return when.Year == year;
        if (t is "fav" or "favs" or "favourite" or "favourites" or "favorite" or "favorites") return e.Favorite;
        // Laughter and hype, once the sound model has listened.
        if (t is "laugh" or "laughs" or "laughing" or "laughter" or "funny" or "lol") return LaughSeconds(e) > 0;
        if (t is "hype" or "cheering" or "cheer" or "shouting" or "screaming") return HypeSeconds(e) > 0;
        if (t == T("today").ToLowerInvariant() || t == "today") return when.Date == DateTime.Today;
        if (t == T("yesterday").ToLowerInvariant() || t == "yesterday") return when.Date == DateTime.Today.AddDays(-1);
        if (t.Length >= 3 && MonthFor(t) is { } month) return when.Month == month;
        return false;
    }

    private static int? MonthFor(string t)
    {
        foreach (var names in new[] { System.Globalization.CultureInfo.CurrentCulture.DateTimeFormat, System.Globalization.CultureInfo.InvariantCulture.DateTimeFormat })
            for (var m = 1; m <= 12; m++)
            {
                var full = names.GetMonthName(m).ToLowerInvariant();
                if (full.Length > 0 && (full == t || (t.Length == 3 && full.StartsWith(t, StringComparison.Ordinal)))) return m;
            }
        return null;
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
        UpdateCollectionCounts();
    }

    /// <summary>Keeps the sidebar tag list in sync with the tags actually in use.</summary>
    private void RebuildTags()
    {
        RebuildNamedRows(Tags, NavKind.Tag, v => v.Entry.Tags, () => SelectedTag, x => SelectedTag = x, byCount: false);
        OnPropertyChanged(nameof(HasTags));
        // Friends: most often tagged first, so the people you play with most sit on top.
        RebuildNamedRows(Friends, NavKind.Person, v => Features.People ? v.Entry.People : Enumerable.Empty<string>(),
            () => SelectedFriend, x => SelectedFriend = x, byCount: true);
        OnPropertyChanged(nameof(HasFriends));
    }

    /// <summary>Keeps a sidebar list (tags or friends) in sync with the names actually in use.</summary>
    private void RebuildNamedRows(ObservableCollection<FolderViewModel> rows, NavKind kind, Func<VideoItemViewModel, IEnumerable<string>> names,
        Func<FolderViewModel?> getSelected, Action<FolderViewModel?> setSelected, bool byCount)
    {
        var groups = _items.Values.SelectMany(names).GroupBy(t => t, StringComparer.OrdinalIgnoreCase);
        var ordered = byCount
            ? groups.OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase)
            : groups.OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase);
        var counts = ordered.Select(g => (Name: g.First(), Count: g.Count())).ToList();

        var same = counts.Count == rows.Count && counts.Zip(rows).All(p => p.First.Name == p.Second.Name);
        if (same)
        {
            foreach (var (c, vm) in counts.Zip(rows)) vm.Count = c.Count;
            return;
        }
        // Clearing the list makes the ListBox null its selection; hold the handlers off while we swap.
        var selected = getSelected()?.Name;
        _rebuildingTags = true;
        try
        {
            rows.Clear();
            foreach (var c in counts) rows.Add(new FolderViewModel(kind, c.Name) { Count = c.Count });
            setSelected(selected is null ? null
                : rows.FirstOrDefault(t => string.Equals(t.Name, selected, StringComparison.OrdinalIgnoreCase)));
        }
        finally { _rebuildingTags = false; }
        // The selected row no longer exists (its last video was untagged): fall back to All videos.
        if (selected is not null && getSelected() is null && Folders.Count > 0) SelectedFolder = Folders[0];
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
        if (item.Entry.Favorite) QueueAutoBackup();
    }

    [RelayCommand]
    private void BeginEdit(VideoItemViewModel? item)
    {
        if (item is not null)
            Editor = new EditViewModel(item, Folders, AllTags)
            {
                ShowNote = Features.Notes, ShowPeople = Features.People, AllPeople = AllPeople,
                Suggest = Features.SmartTitles ? e => SuggestTitle(e) : null,
            };
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
            // A video with a title in Savefile: the name box edits that title (empty goes back to the file
            // name) and the file keeps its name. Otherwise it renames the file, as always.
            var fileName = Path.GetFileNameWithoutExtension(oldPath);
            var hadTitle = entry.Title is { Length: > 0 };
            var newPath = _library.Relocate(entry, hadTitle ? fileName : ed.Name, targetDir);
            // Cleared: "" (not null), so Name new clips by themselves doesn't name it again.
            if (hadTitle) entry.Title = ed.Name.Trim() is { Length: > 0 } t && t != fileName ? t : "";
            entry.TitleAuto = false;
            if (!string.Equals(newPath, oldPath, StringComparison.Ordinal))
            {
                _items.Remove(oldPath);
                _items[newPath] = item;
                CollectionPathChanged(oldPath, newPath);
                _backup.Moved(oldPath, newPath);
            }
        }
        catch (InvalidOperationException ex)
        {
            ed.Error = ex.Message;
            return;
        }
        entry.Tags = ed.Tags.ToList();
        entry.Favorite = ed.Favorite;
        if (ed.ShowNote) entry.Note = string.IsNullOrWhiteSpace(ed.Note) ? null : ed.Note.Trim();
        if (ed.ShowPeople) entry.People = ed.PeopleList();
        _library.SaveCache();
        item.RefreshAll();
        Editor = null;
        RefreshView();
    }

    /// <summary>Full path without a trailing separator, except for a drive or the root ("D:\\" stays, not "D:").</summary>
    private static string NormalizeFolder(string path)
    {
        var full = Path.GetFullPath(path);
        return Path.GetPathRoot(full) == full ? full : full.TrimEnd('/', '\\');
    }

    private static bool Under(string file, string folder) => new FolderViewModel(folder).Contains(file);

    // ---------- player ----------
    [RelayCommand]
    private async Task OpenAsync(VideoItemViewModel? item)
    {
        if (item is null) return;
        if (IsSelecting) { ToggleSelected(item); return; }
        if (item.Said is { } said && item.SaidAtMs >= 0)
        {
            Player.StartAt = item.SaidAtMs;
            Player.StartAtNote = F("Said here: “{0}”", said);
        }
        else if (item.HasSeen && item.SeenAtMs >= 0)
        {
            // Visual search: start a little before the frame that matched.
            Player.StartAt = Math.Max(0, item.SeenAtMs - 2000) + 1000;
            Player.StartAtNote = F("Looks like “{0}” around here", SearchText.Trim());
        }
        if (UseMiniPlayer && !IsPlayerOpen)
        {
            // The mini player plays over the library; clicking the video it already plays leaves it be.
            var same = IsMiniPlayerOpen && ReferenceEquals(Player.Current, item);
            IsMiniPlayerOpen = true;
            if (!same) await Player.OpenAsync(item);
            else if (Player.StartAt is { } at)
            {
                Player.StartAt = null;
                Player.StartAtNote = null;
                Player.SeekCommand.Execute(Math.Max(0, at - 1000.0));
            }
            return;
        }
        IsMiniPlayerOpen = false;
        IsPlayerOpen = true;
        await Player.OpenAsync(item);
    }

    [RelayCommand]
    private void ClosePlayer()
    {
        if (!IsPlayerOpen) return;
        var watched = Player.Current;
        Player.Close();
        IsPlayerOpen = false;
        watched?.RefreshAll();
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
                    if (!SeveralFolders && LibraryFolders() is { Count: > 0 } library)
                    {
                        // One library folder: clips go into it, unless a clips folder was picked in Settings,
                        // which then becomes the library.
                        if (clip.Folder is null) { clip.Folder = library[0]; _settings.Save(); }
                        else _ = UseLibraryFolderAsync(folder, clipsToo: false);
                    }
                    else
                    {
                        _settings.Current.Folders.Add(folder);
                        _settings.Save();
                        RebuildFolders(SelectedFolder?.Path);
                        _ = RescanAsync();
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"Clips folder: {ex.Message}");
            }
            _hotkeyRefused = !_hotkey.Register(clip.HotkeyCtrl, clip.HotkeyAlt, clip.HotkeyShift, clip.HotkeyMeta, clip.HotkeyKey);
            RegisterExtraHotkeys(clip);
        }
        else
        {
            _hotkey.Unregister();
            _hotkeyRefused = false;
            RegisterExtraHotkeys(null);
        }
        RaiseClipState();
        await Clips.ApplyAsync();
    }

    /// <summary>A session an earlier run never saved is joined first, then capture starts as usual.</summary>
    private async Task RecoverThenApplyClipsAsync()
    {
        await Clips.RecoverAtLaunchAsync();
        await ApplyClipSettingsAsync();
    }

    private void RaiseClipState()
    {
        foreach (var name in new[]
                 {
                     nameof(IsClipEnabled), nameof(ShowPlayingWith), nameof(IsClipRecording), nameof(IsClipStarting), nameof(IsClipError),
                     nameof(ClipTitle), nameof(ClipDetail), nameof(HasClipDetail), nameof(SaveClipLabel),
                     nameof(StaysInBackground), nameof(IsSession), nameof(SessionLabel), nameof(CanToggleSession), nameof(ShowSessionButton),
                 })
            OnPropertyChanged(name);
        SaveClipCommand.NotifyCanExecuteChanged();
        ToggleSessionCommand.NotifyCanExecuteChanged();
        if (IsSession && _sessionTimer is null)
        {
            _sessionTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _sessionTimer.Tick += (_, _) => { OnPropertyChanged(nameof(SessionLabel)); BackgroundStateChanged?.Invoke(); };
        }
        if (IsSession) _sessionTimer!.Start(); else _sessionTimer?.Stop();
        UpdateProfileWatch();
        BackgroundStateChanged?.Invoke();
    }

    private void RegisterExtraHotkeys(ClipSettings? clip)
    {
        foreach (var h in _extraHotkeys) h.Dispose();
        _extraHotkeys.Clear();
        _refusedExtras.Clear();
        if (clip is null) return;
        foreach (var extra in clip.ExtraHotkeys)
        {
            var seconds = extra.Seconds;
            var hotkey = new GlobalHotkey(() => Dispatcher.UIThread.Post(() =>
            {
                if (IsClipRecording) _ = SaveClipCoreAsync(seconds);
            }));
            if (hotkey.Register(extra.Ctrl, extra.Alt, extra.Shift, extra.Meta, extra.Key)) _extraHotkeys.Add(hotkey);
            else
            {
                hotkey.Dispose();
                _refusedExtras.Add(extra.Text);
            }
        }
    }

    private readonly Dictionary<string, string> _pendingTags = new(StringComparer.OrdinalIgnoreCase);

    [RelayCommand(CanExecute = nameof(IsClipRecording))]
    private Task SaveClipAsync() => SaveClipCoreAsync(null);

    /// <summary>Saves the last <paramref name="seconds"/> (the clip length setting when null).</summary>
    private async Task SaveClipCoreAsync(int? seconds)
    {
        if (Clips.AddBookmark() is { } at)
        {
            // During a session the hotkey marks the moment instead of cutting a separate clip.
            if (_settings.Current.Clip.Sound) PlatformShell.PlayConfirmSound();
            ShowToast(F("Bookmark added at {0}", Controls.TrimTimeline.FormatTime(at.TotalMilliseconds)));
            return;
        }
        // Ask now, while the game is still in front; saving takes a moment.
        var game = _settings.Current.Clip.TagWithGame ? PlatformShell.FrontmostAppName() : null;
        var path = await Clips.SaveClipAsync(game, seconds);
        if (path is null) ShowToast(T("The clip could not be saved."));
        else if (game is not null)
        {
            _pendingTags[path] = game.ToLowerInvariant();
            // The folder watcher may already have added the clip; tag it now in that case.
            if (_items.TryGetValue(path, out var existing)) Upsert(existing.Entry);
        }
    }

    private void OnClipSaved(string path, int seconds)
    {
        if (_settings.Current.Clip.Sound) PlatformShell.PlayConfirmSound();
        QueueSquad(path);
        QueueAutoBackup();
        var (savedText, milestone) = SavedToastText(path);
        ShowSavedToast(savedText, milestone);
        if (_settings.Current.Clip.Notify)
            PlatformShell.Notify(T("Clip saved"), F("Last {0} · {1}", LengthText(seconds), Path.GetFileName(path)));
        _ = RescanAsync();
        _ = CleanupAfterSaveAsync();
    }

    private async Task CleanupAfterSaveAsync()
    {
        // Let the "Clip saved" toast be read before a storage message can replace it.
        await Task.Delay(4000);
        await RunStorageCleanupAsync();
    }

    public void ShowToast(string text) => ShowToast(text, null);

    /// <summary>A toast, with Mochi in it when <paramref name="mochi"/> is given (it reacts; otherwise a tick).</summary>
    public void ShowToast(string text, Controls.MochiMood? mochi, double seconds = 4)
    {
        _toastCts?.Cancel();
        var cts = _toastCts = new CancellationTokenSource();
        Toast = text;
        ToastMochi = mochi;
        _ = Task.Delay(TimeSpan.FromSeconds(seconds), cts.Token).ContinueWith(_ => Dispatcher.UIThread.Post(() => Toast = null),
            TaskContinuationOptions.OnlyOnRanToCompletion);
    }

    [RelayCommand]
    private async Task ToggleClippingAsync()
    {
        // Switched off under Settings, Features: the tray must not turn it back on behind the hidden sidebar box.
        if (!Features.Clipping && !_settings.Current.Clip.Enabled) return;
        _settings.Current.Clip.Enabled = !_settings.Current.Clip.Enabled;
        _settings.Save();
        await ApplyClipSettingsAsync();
    }

    [RelayCommand]
    private void OpenSettings()
    {
        var editor = new SettingsViewModel(_settings.Current) { PickFolder = PickFolder, PickFile = PickFile };
        SettingsEditor = editor;
        OnCostsChanged();
        _ = editor.LoadDevicesAsync();
        _ = editor.LoadStorageUsageAsync();
    }

    /// <summary>From the editor's Effects panel: Settings, opened on the Editing page.</summary>
    [RelayCommand]
    private void OpenEditingSettings()
    {
        OpenSettings();
        if (SettingsEditor is { } editor) editor.Page = 2;
    }

    [RelayCommand]
    private void CancelSettings()
    {
        SettingsEditor = null;
        ThemeService.Mode = _settings.Current.ThemeMode;
        ThemeService.Apply(_settings.Current.ColorScheme, Accessibility.HighContrast); // drop a previewed look
        Loc.Apply(_settings.Current.Language); // and a previewed language
    }

    [RelayCommand]
    private async Task SaveSettingsAsync()
    {
        if (SettingsEditor is not { } editor) return;
        // Written into a copy first: if anything is refused, the live settings stay untouched
        // (they would otherwise be saved half-changed by the next unrelated save).
        var draft = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(System.Text.Json.JsonSerializer.Serialize(_settings.Current))!;
        if (editor.WriteTo(draft) is { } problem)
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
        _settings.ReplaceWith(draft); // saves
        // The shortcut sheet edits the live dictionary; point it at the one that is now saved.
        Shortcuts.Load(_settings.Current.Shortcuts);
        SettingsEditor = null;
        RaiseFeatures();
        // Sessions switched off mid-session: save it now, or it would record on with no stop button.
        if (!Features.Sessions && IsSession) await ToggleSessionAsync();
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
            var entries = _items.Values.ToDictionary(i => i.Entry.Path, i => i.Entry, StringComparer.OrdinalIgnoreCase);
            var forget = new System.Collections.Concurrent.ConcurrentBag<Models.VideoEntry>();
            var result = await Task.Run(() =>
            {
                var plan = StorageService.Plan(c, favourites.Contains, DateTime.UtcNow);
                if (plan.Count == 0) return null;
                // With the bin on, each clip waits 24 hours there like any other delete.
                Action<string>? toBin = UseBin ? path =>
                {
                    var entry = entries.TryGetValue(path, out var e) ? e : new Models.VideoEntry { Path = path };
                    _bin.Add(entry);
                    forget.Add(entry);
                } : null;
                return StorageService.Apply(c, plan, toBin);
            });
            foreach (var e in forget) _library.Forget(e);
            if (result is { Removed: > 0 }) _ = RescanAsync();
            if (result is { Removed: > 0 })
                ShowToast(F(result.Archived
                        ? (result.Removed == 1 ? "Storage limit: {0} old clip ({1}) moved to the archive." : "Storage limit: {0} old clips ({1}) moved to the archive.")
                        : (result.Removed == 1 ? "Storage limit: {0} old clip ({1}) moved to the {2}." : "Storage limit: {0} old clips ({1}) moved to the {2}."),
                    result.Removed, VideoItemViewModel.FormatSize(result.Bytes), TrashName));
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

    private void OnExportFinished(ExportJobViewModel job)
    {
        if (job.State == ExportState.Cancelled) return;
        if (job.IsDone) _ = RescanAsync();
        if (IsPlayerOpen || IsMiniPlayerOpen) Player.ShowToast(job.Status);
        else ShowToast(job.Status);
    }

    /// <summary>Set while Savefile closes: work cancelled now is meant to carry on next time (a Wrapped video).</summary>
    private volatile bool _quitting;

    public void Dispose()
    {
        SavePlaytime();
        _quitting = true;
        Exports.CancelAll();
        _hotkey.Dispose();
        RegisterExtraHotkeys(null);
        Clips.Dispose();
        _scanCts?.Cancel();
        Player.Dispose();
        _voiceCts?.Cancel();
        _voicePlayer?.Dispose();
        _library.SaveCache();
        _library.Dispose();
    }
}
