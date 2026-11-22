using System.Collections.ObjectModel;
using Cnema.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cnema.ViewModels;

/// <summary>One entry of the language picker; Code "" follows the system.</summary>
public sealed record LanguageOption(string Code, string Name)
{
    public override string ToString() => Name;
}

public sealed record Choice(string English, int Value)
{
    /// <summary>The label in the current language.</summary>
    public string Label => T(English);
    public override string ToString() => Label;
}

/// <summary>One row of the per-game profile list in Settings.</summary>
public sealed partial class ProfileRow : ObservableObject
{
    public static Choice[] Lengths { get; } =
        new[] { new Choice("Same length", 0) }.Concat(SettingsViewModel.Lengths).ToArray();
    public static Choice[] Bitrates { get; } =
        new[] { new Choice("Same bitrate", 0) }.Concat(SettingsViewModel.Bitrates).ToArray();
    public static string[] Formats { get; } = { T("Same format"), "H.264", "HEVC", "AV1" };
    private static readonly string?[] FormatIds = { null, "h264", "hevc", "av1" };

    [ObservableProperty] private string _game = "";
    [ObservableProperty] private Choice _length = Lengths[0];
    [ObservableProperty] private Choice _bitrate = Bitrates[0];
    [ObservableProperty] private string _format = Formats[0];

    public ProfileRow() { }

    public ProfileRow(ClipProfile p)
    {
        _game = p.Game;
        _length = Lengths.FirstOrDefault(c => c.Value == p.Seconds) ?? Lengths[0];
        _bitrate = Bitrates.FirstOrDefault(c => c.Value == p.BitrateMbps) ?? Bitrates[0];
        _format = Formats[Math.Max(0, Array.IndexOf(FormatIds, p.Codec))];
    }

    public ClipProfile ToProfile() => new()
    {
        Game = Game.Trim(),
        Seconds = Length.Value,
        BitrateMbps = Bitrate.Value,
        Codec = FormatIds[Math.Max(0, Array.IndexOf(Formats, Format))],
    };
}

/// <summary>One extra save-clip hotkey in Settings: its keys and the length it saves.</summary>
public sealed partial class HotkeyRow : ObservableObject
{
    [ObservableProperty] private bool _ctrl;
    [ObservableProperty] private bool _alt;
    [ObservableProperty] private bool _shift;
    [ObservableProperty] private bool _meta;
    [ObservableProperty] private string _key;
    [ObservableProperty] private Choice _length;

    public HotkeyRow(ClipHotkey h)
    {
        _ctrl = h.Ctrl;
        _alt = h.Alt;
        _shift = h.Shift;
        _meta = h.Meta;
        _key = GlobalHotkey.Keys.Contains(h.Key) ? h.Key : "D";
        _length = SettingsViewModel.Lengths.OrderBy(c => Math.Abs(c.Value - h.Seconds)).First();
    }

    public ClipHotkey ToHotkey() => new()
    {
        Ctrl = Ctrl, Alt = Alt, Shift = Shift, Meta = Meta, Key = Key, Seconds = Length.Value,
    };

    public string Combo => ClipSettings.FormatHotkey(Ctrl, Alt, Shift, Meta, Key);
    public bool HasModifier => Ctrl || Alt || Shift || Meta;
}

/// <summary>Editable copy of the background + clipping settings; nothing changes until Save.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    /// <summary>Re-reads every text property after a language change.</summary>
    public void RefreshTexts()
    {
        OnPropertyChanged(string.Empty);
    }

    public static readonly string NoAudio = T("None");

    public static Choice[] Lengths { get; } =
    {
        new("15 seconds", 15), new("30 seconds", 30), new("1 minute", 60), new("1.5 minutes", 90),
        new("2 minutes", 120), new("3 minutes", 180), new("5 minutes", 300), new("10 minutes", 600),
    };
    public static Choice[] Resolutions { get; } =
    {
        new("Native (screen resolution)", 0), new("1440p", 1440), new("1080p", 1080), new("720p", 720),
    };
    public static Choice[] FrameRates { get; } = { new("60 fps", 60), new("30 fps", 30) };
    public static Choice[] Bitrates { get; } =
        new[] { 4, 6, 8, 12, 16, 24, 35, 50 }.Select(m => new Choice($"{m} Mbps", m)).ToArray();
    public static Choice[] AudioBitrates { get; } =
        new[] { 96, 128, 160, 192, 256 }.Select(k => new Choice($"{k} kbps", k)).ToArray();
    public static IReadOnlyList<string> Keys => GlobalHotkey.Keys;
    public static Choice[] KeepDaysOptions { get; } =
    {
        new("Keep everything", 0), new("7 days", 7), new("14 days", 14), new("30 days", 30),
        new("90 days", 90), new("1 year", 365),
    };
    public static Choice[] FolderCaps { get; } =
        new[] { new Choice("No limit", 0) }
            .Concat(new[] { 5, 10, 25, 50, 100, 250 }.Select(g => new Choice($"{g} GB", g))).ToArray();
    public static Choice[] CleanupTargets { get; } = { new("Trash", 0), new("Archive folder", 1) };

    public ObservableCollection<string> AudioDevices { get; } = new() { NoAudio };
    public ObservableCollection<string> Screens { get; } = new();

    public static readonly string NoCamera = T("None");
    public ObservableCollection<string> Cameras { get; } = new() { NoCamera };
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasCamera))] private string _camera = NoCamera;
    public bool HasCamera => Camera != NoCamera;
    public static Choice[] CameraCorners { get; } =
        { new("Bottom right", 0), new("Bottom left", 1), new("Top right", 2), new("Top left", 3) };
    private static readonly string[] CornerIds = { "bottom-right", "bottom-left", "top-right", "top-left" };
    [ObservableProperty] private Choice _cameraCorner = CameraCorners[0];
    public static Choice[] CameraSizes { get; } = { new("Small", 18), new("Medium", 25), new("Large", 33) };
    [ObservableProperty] private Choice _cameraSize = CameraSizes[1];
    public ObservableCollection<CodecOption> Codecs { get; } = new();
    public ObservableCollection<ProfileRow> Profiles { get; } = new();
    [ObservableProperty] private bool _separateTracks;

    [RelayCommand] private void AddProfile() => Profiles.Add(new ProfileRow());

    /// <summary>More save-clip hotkeys, each with its own length.</summary>
    public ObservableCollection<HotkeyRow> ExtraHotkeys { get; } = new();
    public const int MaxExtraHotkeys = 4;
    public bool CanAddHotkey => ExtraHotkeys.Count < MaxExtraHotkeys;

    [RelayCommand]
    private void AddHotkey()
    {
        if (!CanAddHotkey) return;
        // Suggest a free letter next to the main one, and a longer length than the main clip.
        var used = ExtraHotkeys.Select(h => h.Key).Append(HotkeyKey).ToHashSet();
        var key = new[] { "D", "F", "G", "H", "A", "W", "E", "R" }.FirstOrDefault(k => !used.Contains(k)) ?? "D";
        var longer = Lengths.FirstOrDefault(c => c.Value > Length.Value) ?? Lengths[^1];
        AddHotkeyRow(new HotkeyRow(new ClipHotkey
        {
            Ctrl = HotkeyCtrl, Alt = HotkeyAlt, Shift = HotkeyShift, Meta = HotkeyMeta, Key = key, Seconds = longer.Value,
        }));
    }

    [RelayCommand]
    private void RemoveHotkey(HotkeyRow? row)
    {
        if (row is null) return;
        ExtraHotkeys.Remove(row);
        OnPropertyChanged(nameof(CanAddHotkey));
        OnPropertyChanged(nameof(Footprint));
    }

    private void AddHotkeyRow(HotkeyRow row)
    {
        row.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(HotkeyRow.Length)) OnPropertyChanged(nameof(Footprint));
        };
        ExtraHotkeys.Add(row);
        OnPropertyChanged(nameof(CanAddHotkey));
        OnPropertyChanged(nameof(Footprint));
    }
    [RelayCommand] private void RemoveProfile(ProfileRow? row) { if (row is not null) Profiles.Remove(row); }

    /// <summary>Set by the view: opens a native folder picker.</summary>
    public Func<Task<string?>>? PickFolder { get; set; }

    [ObservableProperty] private bool _launchAtLogin;
    [ObservableProperty] private bool _keepInBackground;
    [ObservableProperty] private bool _clipEnabled;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Footprint))] private Choice _length;
    [ObservableProperty] private Choice _resolution;
    [ObservableProperty] private Choice _frameRate;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Footprint))] private Choice _bitrate;
    [ObservableProperty] private Choice _audioBitrate;
    [ObservableProperty] private Choice _keepDays;
    [ObservableProperty] private Choice _folderCap;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(UsesArchive))] private Choice _cleanupTarget;
    [ObservableProperty] private string _archiveFolder = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ExportFolderText), nameof(HasExportFolder))] private string? _exportFolder;
    public bool HasExportFolder => !string.IsNullOrWhiteSpace(ExportFolder);
    public string ExportFolderText => HasExportFolder ? ExportFolder! : T("Next to the original video");
    [ObservableProperty] private string _storageUsage = "";
    public bool UsesArchive => CleanupTarget.Value == 1;
    [ObservableProperty] private bool _hardwareEncoder;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CodecDetail))] private CodecOption? _codec;
    private string _codecWanted = "h264";
    public string CodecDetail => Codec is null ? T("Checking what this computer can encode…")
        : Codec.Hardware ? F("Encoded on the graphics chip ({0}).", Codec.Encoder) : T("Encoded on the CPU (x264).");
    partial void OnHardwareEncoderChanged(bool value) => _ = LoadCodecsAsync();
    [ObservableProperty] private int _screenIndex;
    [ObservableProperty] private string _audio1 = NoAudio;
    [ObservableProperty] private string _audio2 = NoAudio;
    [ObservableProperty] private bool _notify;
    [ObservableProperty] private bool _sound;
    /// <summary>Index into SaveSounds.</summary>
    [ObservableProperty] private int _soundStyle;
    public static LocalizedText[] SaveSounds { get; } = LocalizedText.List(PlatformShell.SaveSounds.Select(s => s.Name).ToArray());
    /// <summary>Plays the picked sound, so it can be heard before saving; picking another one plays it too.</summary>
    [RelayCommand] private void HearSaveSound() => PlatformShell.PlayConfirmSound(PlatformShell.SaveSounds[Math.Clamp(SoundStyle, 0, PlatformShell.SaveSounds.Length - 1)].Id);
    partial void OnSoundStyleChanged(int value) => HearSaveSound();
    [ObservableProperty] private bool _tagWithGame;
    [ObservableProperty] private bool _checkForUpdates;
    [ObservableProperty] private bool _autoUpdate;
    public IReadOnlyList<ColorScheme> ColorSchemes => ThemeService.Schemes;
    [ObservableProperty] private ColorScheme _colorScheme = ThemeService.Current;
    // Shown straight away as a preview; cancelling Settings puts the saved scheme back.
    partial void OnColorSchemeChanged(ColorScheme value) { if (value is not null) ThemeService.Apply(value.Id, HighContrast); }
    public IReadOnlyList<ThemeModeOption> ThemeModes => ThemeService.Modes;
    [ObservableProperty] private ThemeModeOption _themeMode = ThemeService.FindMode("");
    partial void OnThemeModeChanged(ThemeModeOption value)
    {
        if (value is null) return;
        ThemeService.Mode = value.Id;
        ThemeService.Apply(ColorScheme.Id, HighContrast);
    }

    public IReadOnlyList<LanguageOption> LanguageOptions { get; } =
        new[] { new LanguageOption("", SystemLanguageLabel()) }
            .Concat(Loc.Languages.Select(l => new LanguageOption(l.Code, l.Name))).ToList();
    [ObservableProperty] private LanguageOption _language = null!;
    // Also a live preview, like the colour scheme.
    partial void OnLanguageChanged(LanguageOption value) { if (value is not null) Loc.Apply(value.Code); }

    private static string SystemLanguageLabel()
    {
        var sys = Loc.SystemLanguage;
        var name = Loc.Languages.FirstOrDefault(l => l.Code == sys).Name ?? "English";
        return F("Same as the system ({0})", name);
    }

    // ---------- pages ----------
    public static LocalizedText[] PageNames { get; } = LocalizedText.List("General", "Library", "Clipping", "Editing", "Memories", "Smart features", "Backup", "Accessibility");
    public static SettingsPage[] Pages { get; } = PageNames.Zip(new[] { "IcSettings", "IcLibrary", "IcFilm", "IcEdit", "IcStar", "IcSparkle", "IcCloud", "IcEye" },
        (name, icon) => new SettingsPage(name, icon)).ToArray();
    public const int EditingPage = 3;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGeneralPage), nameof(IsLibraryPage), nameof(IsClippingPage), nameof(IsEditingPage), nameof(IsMemoriesPage),
        nameof(IsSmartPage), nameof(IsBackupPage), nameof(IsAccessibilityPage), nameof(ShowClippingSettings), nameof(ShowClippingOffNotice))]
    private int _page;

    /// <summary>Words typed in the search box: every page shows, with only the settings that match (filtered by the view).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSearching), nameof(IsGeneralPage), nameof(IsLibraryPage), nameof(IsClippingPage), nameof(IsEditingPage), nameof(IsMemoriesPage),
        nameof(IsSmartPage), nameof(IsBackupPage), nameof(IsAccessibilityPage), nameof(ShowClippingSettings), nameof(ShowClippingOffNotice))]
    private string _search = "";
    public bool IsSearching => Search.Trim().Length > 0;

    private bool On(int page) => Page == page || IsSearching;
    public bool IsGeneralPage => On(0);
    public bool IsLibraryPage => On(1);
    public bool IsClippingPage => On(2);
    public bool IsEditingPage => On(EditingPage);
    public bool IsMemoriesPage => On(4);
    public bool IsSmartPage => On(5);
    public bool IsBackupPage => On(6);
    public bool IsAccessibilityPage => On(7);
    public bool ShowClippingSettings => IsClippingPage && FeatureClipping;
    public bool ShowClippingOffNotice => IsClippingPage && !FeatureClipping;

    /// <summary>Picking a page ends a search.</summary>
    partial void OnPageChanged(int value) => Search = "";
    /// <summary>Ctrl+Tab / Ctrl+Shift+Tab.</summary>
    public void CyclePage(int delta) => Page = ((Page + delta) % PageNames.Length + PageNames.Length) % PageNames.Length;

    // ---------- features ----------
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowClippingSettings), nameof(ShowClippingOffNotice))] private bool _featureClipping;
    [ObservableProperty] private bool _featureSessions;
    [ObservableProperty] private bool _featureHoverPreview;
    [ObservableProperty] private bool _featureLoudMarkers;
    [ObservableProperty] private bool _featureHoverGlow;
    [ObservableProperty] private bool _featureCollections;
    [ObservableProperty] private bool _featureUnwatched;
    [ObservableProperty] private bool _featureCleanUp;
    [ObservableProperty] private bool _featureStats;
    [ObservableProperty] private bool _featureMiniPlayer;
    [ObservableProperty] private bool _featureChapters;

    // ---------- accessibility ----------
    public static Choice[] UiScales { get; } =
        new[] { 90, 100, 110, 125, 150 }.Select(p => new Choice($"{p} %", p)).ToArray();
    [ObservableProperty] private Choice _uiScale;
    [ObservableProperty] private bool _highContrast;
    [ObservableProperty] private bool _reducedMotion;
    // Previewed straight away like the colour scheme; Cancel puts the saved look back.
    partial void OnHighContrastChanged(bool value) => ThemeService.Apply(ColorScheme.Id, value);

    // ---------- portable mode ----------
    public bool IsPortable => AppPaths.IsPortable;
    /// <summary>Portable mode belongs to the zip download; an installed copy keeps its data in the user's folder.</summary>
    public bool CanMakePortable => !AppPaths.IsPortable && !UpdateService.IsInstalled;
    public string PortableStatus => AppPaths.IsPortable
        ? T("On: settings, library and thumbnails are kept in \"Savefile Data\" next to the app. Delete or rename that folder to turn it off.")
        : UpdateService.IsInstalled
            ? T("For running Savefile from a USB stick, use the zip download instead of the installer. This copy is installed, so it keeps its data in your user folder.")
            : T("Keep settings, library and thumbnails next to the app, for running Savefile from a USB stick.");
    [ObservableProperty] private string? _portableMessage;

    [RelayCommand]
    private void MakePortable()
    {
        try
        {
            AppPaths.MakePortable();
            PortableMessage = T("Done. Savefile uses the \"Savefile Data\" folder from the next start.");
        }
        catch (Exception ex)
        {
            PortableMessage = T("Could not create the folder next to the app: ") + ex.Message;
        }
    }

    [RelayCommand] private void ShowPortableFolder() => PlatformShell.OpenUrl(AppPaths.DataDir);
    [ObservableProperty] private bool _hotkeyCtrl;
    [ObservableProperty] private bool _hotkeyAlt;
    [ObservableProperty] private bool _hotkeyShift;
    [ObservableProperty] private bool _hotkeyMeta;
    [ObservableProperty] private string _hotkeyKey;
    [ObservableProperty] private string _folder;
    [ObservableProperty] private bool _hasSeveralScreens;
    [ObservableProperty] private string? _error;

    public string AltLabel => OperatingSystem.IsMacOS() ? "⌥ Option" : "Alt";
    public string CtrlLabel => OperatingSystem.IsMacOS() ? "⌃ Control" : "Ctrl";
    public string ShiftLabel => OperatingSystem.IsMacOS() ? "⇧ Shift" : "Shift";
    public string MetaLabel => OperatingSystem.IsMacOS() ? "⌘ Command" : OperatingSystem.IsWindows() ? "Win" : "Super";
    public string BackgroundLabel => OperatingSystem.IsMacOS()
        ? T("Keep running in the menu bar when the window is closed")
        : T("Keep running in the system tray when the window is closed");

    public string AudioHint => OperatingSystem.IsMacOS()
        ? CaptureProbe.SystemAudioHelper is not null
            ? T("\"System audio\" records everything you hear (games included) and is covered by the Screen Recording permission. Pick your microphone as the second device to add your voice.")
            : T("System audio needs macOS 13 or newer. Microphones work as they are; for game sound, install a loopback device such as BlackHole and pick it here.")
        : OperatingSystem.IsWindows()
            ? WindowsLoopback.Supported
                ? T("\"System audio\" records everything you hear, games included, without Savefile's own sounds. Pick your microphone as the second device to add your voice.")
                : T("Microphones work as they are. For game sound, enable \"Stereo Mix\" in Windows sound settings (or install a loopback device) and pick it here.")
            : T("Sources ending in \".monitor\" record what the speakers play.");

    /// <summary>Temp disk the rolling buffer needs; nothing else is written until a clip is saved.</summary>
    public string Footprint
    {
        get
        {
            var longest = ExtraHotkeys.Select(h => h.Length).Append(Length).MaxBy(c => c.Value)!;
            return F("Keeps the last {0} in a temporary buffer of about {1:N0} MB. Nothing is kept unless you save a clip.",
                longest.Label, Bitrate.Value * (longest.Value + 6) / 8);
        }
    }

    public SettingsViewModel(AppSettings settings)
    {
        var c = settings.Clip;
        _launchAtLogin = AutostartService.IsEnabled;
        _keepInBackground = settings.KeepInBackground;
        _clipEnabled = c.Enabled;
        _length = Nearest(Lengths, c.Seconds);
        _resolution = Resolutions.FirstOrDefault(r => r.Value == c.Height) ?? Resolutions[0];
        _frameRate = FrameRates.FirstOrDefault(f => f.Value == c.Fps) ?? FrameRates[0];
        _bitrate = Nearest(Bitrates, c.BitrateMbps);
        _audioBitrate = Nearest(AudioBitrates, c.AudioBitrateKbps);
        _hardwareEncoder = c.HardwareEncoder;
        _codecWanted = c.Codec;
        _screenIndex = Math.Max(0, c.Screen);
        _notify = c.Notify;
        _sound = c.Sound;
        _soundStyle = Math.Max(0, Array.FindIndex(PlatformShell.SaveSounds, s => s.Id == c.SoundStyle));
        _tagWithGame = c.TagWithGame;
        _checkForUpdates = settings.CheckForUpdates;
        _autoUpdate = settings.AutoUpdate;
        _exportFolder = settings.ExportFolder;
        _colorScheme = ThemeService.Find(settings.ColorScheme);
        _themeMode = ThemeService.FindMode(settings.ThemeMode);
        _language = LanguageOptions.FirstOrDefault(l => l.Code == settings.Language) ?? LanguageOptions[0];
        var f = settings.Features;
        _featureClipping = f.Clipping;
        _featureSessions = f.Sessions;
        _featureHoverPreview = f.HoverPreview;
        _featureLoudMarkers = f.LoudMarkers;
        _featureHoverGlow = f.HoverGlow;
        _featureCollections = f.Collections;
        _featureUnwatched = f.Unwatched;
        _featureCleanUp = f.CleanUp;
        _featureStats = f.Stats;
        _featureMiniPlayer = f.MiniPlayer;
        _featureChapters = f.Chapters;
        LoadEditing(settings);
        var a = settings.Accessibility;
        _uiScale = UiScales.OrderBy(o => Math.Abs(o.Value - a.UiScale * 100)).First();
        _highContrast = a.HighContrast;
        _reducedMotion = a.ReducedMotion;
        _hotkeyCtrl = c.HotkeyCtrl;
        _hotkeyAlt = c.HotkeyAlt;
        _hotkeyShift = c.HotkeyShift;
        _hotkeyMeta = c.HotkeyMeta;
        _hotkeyKey = Keys.Contains(c.HotkeyKey) ? c.HotkeyKey : "S";
        _folder = c.ResolvedFolder;
        _separateTracks = c.SeparateTracks;
        foreach (var profile in c.Profiles) Profiles.Add(new ProfileRow(profile));
        foreach (var extra in c.ExtraHotkeys.Take(MaxExtraHotkeys)) AddHotkeyRow(new HotkeyRow(extra));
        _keepDays = Nearest(KeepDaysOptions, c.KeepDays);
        _folderCap = Nearest(FolderCaps, c.MaxFolderGb);
        _cleanupTarget = CleanupTargets[c.CleanupArchive ? 1 : 0];
        _archiveFolder = c.ResolvedArchiveFolder;
        // Show saved devices straight away; the real list replaces them once probed.
        foreach (var name in new[] { c.Audio1, c.Audio2 })
            if (!string.IsNullOrWhiteSpace(name) && !AudioDevices.Contains(name)) AudioDevices.Add(name);
        _audio1 = string.IsNullOrWhiteSpace(c.Audio1) ? NoAudio : c.Audio1!;
        _audio2 = string.IsNullOrWhiteSpace(c.Audio2) ? NoAudio : c.Audio2!;
        Screens.Add("Primary display");
        if (!string.IsNullOrWhiteSpace(c.Camera))
        {
            Cameras.Add(c.Camera!);
            _camera = c.Camera!;
        }
        _cameraCorner = CameraCorners[Math.Max(0, Array.IndexOf(CornerIds, c.CameraCorner))];
        _cameraSize = Nearest(CameraSizes, c.CameraSize);
    }

    private static Choice Nearest(Choice[] options, int value) =>
        options.OrderBy(o => Math.Abs(o.Value - value)).First();

    /// <summary>Asks ffmpeg / the sound server which devices exist. Slow-ish, so done after opening.</summary>
    public async Task LoadDevicesAsync()
    {
        _ = LoadCodecsAsync();
        var devices = await CaptureProbe.ListDevicesAsync();
        var (a1, a2) = (Audio1, Audio2);
        foreach (var name in devices.Audio)
            if (!AudioDevices.Contains(name)) AudioDevices.Add(name);
        Audio1 = a1;
        Audio2 = a2;

        var screen = ScreenIndex;
        Screens.Clear();
        foreach (var s in devices.Screens) Screens.Add(s);
        ScreenIndex = Math.Clamp(screen, 0, Math.Max(0, Screens.Count - 1));
        HasSeveralScreens = Screens.Count > 1;

        var camera = Camera;
        foreach (var name in devices.Cameras)
            if (!Cameras.Contains(name)) Cameras.Add(name);
        Camera = camera;
    }

    private async Task LoadCodecsAsync()
    {
        if (Codec is not null) _codecWanted = Codec.Codec;
        var ffmpeg = await CaptureProbe.FindCaptureFfmpegAsync();
        var options = ffmpeg is null
            ? new[] { new CodecOption("h264", "libx264", false) }
            : await CaptureProbe.ListCodecsAsync(ffmpeg, HardwareEncoder);
        Codecs.Clear();
        foreach (var o in options) Codecs.Add(o);
        Codec = Codecs.FirstOrDefault(o => o.Codec == _codecWanted) ?? Codecs.FirstOrDefault();
    }

    [RelayCommand]
    private async Task BrowseFolderAsync()
    {
        if (PickFolder is null) return;
        var picked = await PickFolder();
        if (!string.IsNullOrWhiteSpace(picked)) Folder = picked;
    }

    [RelayCommand]
    private async Task BrowseExportFolderAsync()
    {
        if (PickFolder is null) return;
        var picked = await PickFolder();
        if (!string.IsNullOrWhiteSpace(picked)) ExportFolder = picked;
    }

    [RelayCommand] private void ResetExportFolder() => ExportFolder = null;

    [RelayCommand]
    private async Task BrowseArchiveAsync()
    {
        if (PickFolder is null) return;
        var picked = await PickFolder();
        if (!string.IsNullOrWhiteSpace(picked)) ArchiveFolder = picked;
    }

    /// <summary>Fills the "clips folder now holds" line; counting files stays off the UI thread.</summary>
    public async Task LoadStorageUsageAsync()
    {
        var folder = Folder;
        var u = await Task.Run(() => StorageService.Measure(folder));
        StorageUsage = u.Count == 0
            ? T("The clips folder is empty.")
            : F(u.Count == 1 ? "The clips folder holds {0} clip, {1}." : "The clips folder holds {0} clips, {1}.", u.Count, VideoItemViewModel.FormatSize(u.Bytes));
    }

    /// <summary>Copies the edits into the settings object. Returns a message if something is invalid.</summary>
    public string? WriteTo(AppSettings settings)
    {
        if (ClipEnabled && !(HotkeyCtrl || HotkeyAlt || HotkeyShift || HotkeyMeta) && HotkeyKey.Length == 1)
            return T("Add at least one modifier to the hotkey, or a plain letter would stop working everywhere else.");
        if (ClipEnabled && ExtraHotkeys.Any(h => !h.HasModifier && h.Key.Length == 1))
            return T("Add at least one modifier to each extra hotkey, or a plain letter would stop working everywhere else.");
        var combos = ExtraHotkeys.Select(h => h.Combo)
            .Prepend(ClipSettings.FormatHotkey(HotkeyCtrl, HotkeyAlt, HotkeyShift, HotkeyMeta, HotkeyKey)).ToList();
        if (combos.Distinct().Count() != combos.Count)
            return T("Two save-clip hotkeys use the same keys. Give each one its own.");
        // Every check comes before the first change, so a refused save leaves the settings as they were.
        if (UsesArchive && ArchiveFolder.Trim().Length > 0)
        {
            var clips = Path.GetFullPath(string.IsNullOrWhiteSpace(Folder) ? AppPaths.DefaultClipsDir : Folder.Trim())
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var archive = Path.GetFullPath(ArchiveFolder.Trim()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(archive, clips, StringComparison.OrdinalIgnoreCase))
                return T("The archive folder has to be a different folder from the clips folder.");
            // Inside the clips folder, archived clips would be found and archived again on every run.
            if (archive.StartsWith(clips + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return T("The archive folder can't be inside the clips folder.");
        }
        var c = settings.Clip;
        settings.KeepInBackground = KeepInBackground;
        c.Enabled = ClipEnabled;
        c.Seconds = Length.Value;
        c.Height = Resolution.Value;
        c.Fps = FrameRate.Value;
        c.BitrateMbps = Bitrate.Value;
        c.AudioBitrateKbps = AudioBitrate.Value;
        c.HardwareEncoder = HardwareEncoder;
        c.Codec = Codec?.Codec ?? _codecWanted;
        c.Screen = Math.Max(0, ScreenIndex);
        c.Audio1 = Audio1 == NoAudio ? null : Audio1;
        c.Audio2 = Audio2 == NoAudio || Audio2 == Audio1 ? null : Audio2;
        c.Camera = Camera == NoCamera ? null : Camera;
        c.CameraCorner = CornerIds[CameraCorner.Value];
        c.CameraSize = CameraSize.Value;
        c.Notify = Notify;
        c.Sound = Sound;
        c.SoundStyle = PlatformShell.SaveSounds[Math.Clamp(SoundStyle, 0, PlatformShell.SaveSounds.Length - 1)].Id;
        c.TagWithGame = TagWithGame;
        settings.CheckForUpdates = CheckForUpdates;
        settings.AutoUpdate = AutoUpdate;
        settings.ExportFolder = HasExportFolder ? ExportFolder!.Trim() : null;
        settings.ColorScheme = ColorScheme.Id;
        settings.ThemeMode = ThemeMode.Id;
        settings.Language = Language.Code;
        settings.Features = new FeatureSettings
        {
            Clipping = FeatureClipping, Sessions = FeatureSessions,
            HoverPreview = FeatureHoverPreview, LoudMarkers = FeatureLoudMarkers, HoverGlow = FeatureHoverGlow,
            Collections = FeatureCollections, Unwatched = FeatureUnwatched, CleanUp = FeatureCleanUp, Stats = FeatureStats,
            MiniPlayer = FeatureMiniPlayer, Chapters = FeatureChapters,
            Captions = FeatureCaptions, SpeedEffects = FeatureSpeedEffects, AudioTools = FeatureAudioTools,
            Branding = FeatureBranding, ExportQueue = FeatureExportQueue, KeepEdits = FeatureKeepEdits,
            Notes = FeatureNotes, DragOut = FeatureDragOut, DragDiscord = FeatureDragDiscord, DiscordNitro = FeatureDiscordNitro, Bin = FeatureBin, Recorders = FeatureRecorders,
            ZoomPan = FeatureZoomPan, Blur = FeatureBlur, Music = FeatureMusic, Montage = FeatureMontage,
            RecoverSessions = FeatureRecoverSessions,
            Rewatch = FeatureRewatch, FriendShelf = FeatureFriendShelf, OnThisDay = FeatureOnThisDay, People = FeaturePeople, Milestones = FeatureMilestones, Recap = FeatureRecap, PlayAll = FeaturePlayAll, Backup = FeatureBackup, SeveralFolders = FeatureSeveralFolders,
            Highlights = FeatureHighlights, SmartTitles = FeatureSmartTitles, AutoTitles = FeatureAutoTitles && FeatureSmartTitles,
            Speech = FeatureSpeech, Visual = FeatureVisual, Mascot = FeatureMascot, MakeRoom = FeatureMakeRoom, RewatchReminders = FeatureRewatchReminders, Badges = FeatureBadges, Playtime = FeaturePlaytime, MochiSaves = FeatureMochiSaves, MochiEmpty = FeatureMochiEmpty, MochiBadges = FeatureMochiBadges, MochiSeasonal = FeatureMochiSeasonal, MochiSleepy = FeatureMochiSleepy, MochiFollow = FeatureMochiFollow, MochiPoke = FeatureMochiPoke, MochiBackup = FeatureMochiBackup, MochiHelper = FeatureMochiHelper,
            Splash = FeatureSplash, Laughs = FeatureLaughs, ScreenText = FeatureScreenText, Voices = FeatureVoices, VoicePoints = FeatureVoicePoints, Wrapped = FeatureWrapped,
            SmartThumbs = FeatureSmartThumbs, TrimSuggestions = FeatureTrimSuggestions, SmartCrop = FeatureSmartCrop, SmartCuts = FeatureSmartCuts,
        };
        WriteEditing(settings);
        // Switching a module off switches off what it was doing, not only its buttons.
        if (!FeatureClipping) c.Enabled = false;
        settings.Accessibility = new AccessibilitySettings
        {
            UiScale = UiScale.Value / 100.0, HighContrast = HighContrast, ReducedMotion = ReducedMotion,
        };
        c.HotkeyCtrl = HotkeyCtrl;
        c.HotkeyAlt = HotkeyAlt;
        c.HotkeyShift = HotkeyShift;
        c.HotkeyMeta = HotkeyMeta;
        c.HotkeyKey = HotkeyKey;
        c.SeparateTracks = SeparateTracks;
        c.Profiles = Profiles.Where(p => p.Game.Trim().Length > 0).Select(p => p.ToProfile()).ToList();
        c.ExtraHotkeys = ExtraHotkeys.Select(h => h.ToHotkey()).ToList();
        c.KeepDays = KeepDays.Value;
        c.MaxFolderGb = FolderCap.Value;
        c.CleanupArchive = UsesArchive;
        c.Folder = string.IsNullOrWhiteSpace(Folder) || Folder == AppPaths.DefaultClipsDir ? null : Folder.Trim();
        c.ArchiveFolder = string.IsNullOrWhiteSpace(ArchiveFolder) || ArchiveFolder.Trim() == c.ResolvedFolder + " Archive"
            ? null : ArchiveFolder.Trim();
        return null;
    }
}
