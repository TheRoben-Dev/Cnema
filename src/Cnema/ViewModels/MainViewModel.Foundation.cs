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

    /// <summary>Raised when scale, motion or glow change, so the window can restyle itself.</summary>
    public event Action? AppearanceChanged;

    private void RaiseFeatures()
    {
        VideoItemViewModel.HoverPreview = Features.HoverPreview;
        foreach (var name in new[] { nameof(ShowClipping), nameof(ShowSessionButton), nameof(ShowController) })
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
            ShowToast("Settings exported");
        }
        catch (Exception ex)
        {
            Log.Warn("Exporting settings failed: " + ex.Message);
            ShowToast("Could not export the settings: " + ex.Message);
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
            ShowToast("That file is not a Cnema settings file.");
            Log.Warn("Importing settings failed: " + ex.Message);
            return;
        }
        SettingsEditor = null;
        _settings.ReplaceWith(imported);
        ApplyAllSettings();
        RebuildFolders(SelectedFolder?.Path);
        _ = RescanAsync();
        await ApplyClipSettingsAsync();
        ShowToast("Settings imported");
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
    [ObservableProperty] private bool _setupClipping = true;
    [ObservableProperty] private bool _setupSessions = true;
    [ObservableProperty] private bool _setupController = true;
    [ObservableProperty] private bool _setupHoverPreview = true;
    [ObservableProperty] private bool _setupLoudMarkers = true;
    [ObservableProperty] private bool _setupHoverGlow = true;
    public string SetupFolders => _settings.Current.Folders.Count switch
    {
        0 => "No folders yet. Cnema only shows videos from folders you add.",
        1 => "1 folder: " + _settings.Current.Folders[0],
        var n => $"{n} folders added.",
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
        s.Features = new FeatureSettings
        {
            Clipping = SetupClipping, Sessions = SetupSessions, Controller = SetupController,
            HoverPreview = SetupHoverPreview, LoudMarkers = SetupLoudMarkers, HoverGlow = SetupHoverGlow,
        };
        _settings.Save();
        IsSetupOpen = false;
        ApplyAllSettings();
    }
}
