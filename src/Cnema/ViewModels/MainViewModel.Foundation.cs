using Cnema.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cnema.ViewModels;

// Features page, accessibility, shortcut sheet, settings backup and first-run setup.
public sealed partial class MainViewModel
{
    // ---------- features ----------
    public FeatureSettings Features => _settings.Current.Features;
    public AccessibilitySettings Accessibility => _settings.Current.Accessibility;
    public bool ShowClipping => Features.Clipping;
    public bool ShowSessionButton => Features.Sessions && CanToggleSession;
    public bool ShowController => Features.Controller;
    public bool ShowUnwatchedFilter => Features.Unwatched;

    /// <summary>Raised when scale, motion or glow change, so the window can restyle itself.</summary>
    public event Action? AppearanceChanged;

    private void RaiseFeatures()
    {
        VideoItemViewModel.HoverPreview = Features.HoverPreview;
        VideoItemViewModel.UnwatchedBadges = Features.Unwatched;
        VideoItemViewModel.NotesEnabled = Features.Notes;
        Chapters.Enabled = Features.Chapters;
        Player.RefreshChapters();
        Player.Queue = Features.ExportQueue ? Exports : null;
        Player.RefreshEffectFeatures();
        if (IsMiniPlayerOpen && !UseMiniPlayer) ExpandMiniPlayer();
        foreach (var v in _items.Values) v.RefreshAll();
        if (!Features.Collections && SelectedCollection is not null) SelectedFolder = Folders.FirstOrDefault();
        if (!Features.Unwatched) FilterUnwatched = false;
        foreach (var name in new[]
                 {
                     nameof(ShowClipping), nameof(ShowSessionButton), nameof(ShowController), nameof(ShowCollections),
                     nameof(ShowCleanUp), nameof(ShowStats), nameof(ShowTools), nameof(ShowUnwatchedFilter),
                     nameof(ShowMiniPlayerButton), nameof(ShowRecorders), nameof(ShowNotes),
                 })
            OnPropertyChanged(name);
        AppearanceChanged?.Invoke();
    }

    // ---------- keyboard shortcut sheet ----------
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsShowingShortcuts))] private ShortcutsViewModel? _shortcutSheet;
    public bool IsShowingShortcuts => ShortcutSheet is not null;

    [RelayCommand]
    private void ShowShortcuts()
    {
        SettingsEditor = null;
        ThemeService.Apply(_settings.Current.ColorScheme, Accessibility.HighContrast);
        ShortcutSheet = new ShortcutsViewModel(_settings.Save);
    }

    [RelayCommand] private void CloseShortcuts() => ShortcutSheet = null;

    // ---------- settings backup ----------
    /// <summary>Set by the view: a save dialog for a settings file. Returns the chosen path.</summary>
    public Func<Task<string?>>? PickSettingsSavePath { get; set; }
    /// <summary>Set by the view: an open dialog for a settings file.</summary>
    public Func<Task<string?>>? PickSettingsFile { get; set; }

    [RelayCommand]
    private async Task ExportSettingsAsync()
    {
        if (PickSettingsSavePath is null || await PickSettingsSavePath() is not { } path) return;
        try
        {
            _settings.Save();
            File.Copy(AppPaths.SettingsFile, path, overwrite: true);
            ShowToast(T("Settings exported"));
        }
        catch (Exception ex)
        {
            Log.Warn("Exporting settings failed: " + ex.Message);
            ShowToast(T("Could not export the settings: ") + ex.Message);
        }
    }

    [RelayCommand]
    private async Task ImportSettingsAsync()
    {
        if (PickSettingsFile is null || await PickSettingsFile() is not { } path) return;
        AppSettings imported;
        try
        {
            imported = JsonStore.Read<AppSettings>(path);
        }
        catch (Exception ex)
        {
            ShowToast(T("That file is not a Cnema settings file."));
            Log.Warn("Importing settings failed: " + ex.Message);
            return;
        }
        SettingsEditor = null;
        _settings.ReplaceWith(imported);
        ApplyAllSettings();
        RebuildFolders(SelectedFolder?.Path);
        _ = RescanAsync();
        await ApplyClipSettingsAsync();
        ShowToast(T("Settings imported"));
    }

    /// <summary>Puts look, keys, features and controller mode in line with the stored settings.</summary>
    private void ApplyAllSettings()
    {
        ThemeService.Apply(_settings.Current.ColorScheme, Accessibility.HighContrast);
        Shortcuts.Load(_settings.Current.Shortcuts);
        OnPropertyChanged(nameof(IsControllerMode));
        ControllerModeChanged?.Invoke();
        RaiseFeatures();
    }

    // ---------- first-run setup ----------
    [ObservableProperty] private bool _isSetupOpen;
    public IReadOnlyList<ColorScheme> ColorSchemes => ThemeService.Schemes;
    [ObservableProperty] private ColorScheme _setupScheme = ThemeService.Current;
    partial void OnSetupSchemeChanged(ColorScheme value) { if (value is not null) ThemeService.Apply(value.Id, Accessibility.HighContrast); }
    public IReadOnlyList<LanguageOption> SetupLanguages { get; } = Loc.Languages.Select(l => new LanguageOption(l.Code, l.Name)).ToList();
    [ObservableProperty] private LanguageOption? _setupLanguage;
    partial void OnSetupLanguageChanged(LanguageOption? value) { if (value is not null) Loc.Apply(value.Code); }
    [ObservableProperty] private bool _setupClipping = true;
    [ObservableProperty] private bool _setupSessions = true;
    [ObservableProperty] private bool _setupController = true;
    [ObservableProperty] private bool _setupHoverPreview = true;
    [ObservableProperty] private bool _setupLoudMarkers = true;
    [ObservableProperty] private bool _setupHoverGlow = true;
    public string SetupFolders => _settings.Current.Folders.Count switch
    {
        0 => T("No folders yet. Cnema only shows videos from folders you add."),
        1 => T("1 folder: ") + _settings.Current.Folders[0],
        var n => F("{0} folders added.", n),
    };

    [RelayCommand]
    private async Task SetupAddFolderAsync()
    {
        await AddFolderCommand.ExecuteAsync(null);
        OnPropertyChanged(nameof(SetupFolders));
    }

    [RelayCommand]
    private void FinishSetup()
    {
        var s = _settings.Current;
        s.ColorScheme = SetupScheme.Id;
        // Picking the system's own language keeps following the system.
        if (SetupLanguage is { } lang) s.Language = lang.Code == Loc.SystemLanguage ? "" : lang.Code;
        // Only the switches shown in setup; everything else (other features, folders, hotkeys) stays.
        var f = s.Features;
        f.Clipping = SetupClipping; f.Sessions = SetupSessions; f.Controller = SetupController;
        f.HoverPreview = SetupHoverPreview; f.LoudMarkers = SetupLoudMarkers; f.HoverGlow = SetupHoverGlow;
        _settings.Save();
        IsSetupOpen = false;
        ApplyAllSettings();
    }

    /// <summary>
    /// Settings, General: go through the welcome setup again. It starts from the current choices,
    /// and folders, hotkeys and everything it doesn't show are left alone.
    /// </summary>
    [RelayCommand]
    private void RestartSetup()
    {
        CancelSettings();
        var s = _settings.Current;
        var f = s.Features;
        SetupScheme = ThemeService.Find(s.ColorScheme);
        SetupLanguage = SetupLanguages.FirstOrDefault(l => l.Code == Loc.Current);
        SetupClipping = f.Clipping; SetupSessions = f.Sessions; SetupController = f.Controller;
        SetupHoverPreview = f.HoverPreview; SetupLoudMarkers = f.LoudMarkers; SetupHoverGlow = f.HoverGlow;
        OnPropertyChanged(nameof(SetupFolders));
        IsSetupOpen = true;
    }
}
