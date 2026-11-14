namespace Cnema.Services;

public sealed class AppSettings
{
    public List<string> Folders { get; set; } = new();
    public int SortIndex { get; set; }
    public bool PreciseExport { get; set; }
    public int Volume { get; set; } = 80;
    /// <summary>The player clock shows the frame (m:ss f12) instead of tenths of a second.</summary>
    public bool ShowFrames { get; set; }

    /// <summary>Stay alive in the tray / menu bar when the window is closed.</summary>
    public bool KeepInBackground { get; set; }
    public ClipSettings Clip { get; set; } = new();
    public bool CheckForUpdates { get; set; } = true;
    /// <summary>Id of the colour scheme, see ThemeService.</summary>
    public string ColorScheme { get; set; } = ThemeService.DefaultId;
    /// <summary>UI language code ("en", "sv"), or "" to follow the system language.</summary>
    public string Language { get; set; } = "";
    /// <summary>Download and install a newer version at launch, without asking.</summary>
    public bool AutoUpdate { get; set; } = true;
    /// <summary>The version an automatic install was last tried for, so a failing one is not retried forever.</summary>
    public string? LastUpdateAttempt { get; set; }
    /// <summary>The version that ran last time; a newer running version shows "What's new".</summary>
    public string? LastRunVersion { get; set; }

    /// <summary>Bigger layout, gamepad navigation and on-screen button hints (handhelds, TV PCs).</summary>
    public bool ControllerMode { get; set; }
    /// <summary>In Controller mode, open full screen like a console app.</summary>
    public bool ControllerFullscreen { get; set; } = true;

    /// <summary>Which optional parts of Cnema are switched on (Settings, Features).</summary>
    public FeatureSettings Features { get; set; } = new();
    public AccessibilitySettings Accessibility { get; set; } = new();
    /// <summary>Keyboard shortcuts changed from their defaults: action id to gesture, "" for none.</summary>
    public Dictionary<string, string> Shortcuts { get; set; } = new();
    /// <summary>The editor's sound tools, watermark and intro / outro.</summary>
    public EffectSettings Effects { get; set; } = new();
    /// <summary>Recording folders of other apps (OBS, NVIDIA, Xbox Game Bar, Steam) brought into the library.</summary>
    public List<RecorderImport> Recorders { get; set; } = new();
    /// <summary>Tag videos from other recorders with their game, when the recorder tells.</summary>
    public bool RecorderTags { get; set; } = true;
}

/// <summary>Effects that stay as you left them from one export to the next.</summary>
public sealed class EffectSettings
{
    /// <summary>Bring exports to a steady loudness (see RenderService.TargetLufs).</summary>
    public bool Normalize { get; set; }
    /// <summary>Extra volume for the voice track, in dB, for files with a separate voice track.</summary>
    public double VoiceBoostDb { get; set; }
    /// <summary>Lower the other tracks while the voice track is talking.</summary>
    public bool Duck { get; set; }

    /// <summary>The editor's tool panel: open or folded away, and the page it shows.</summary>
    public bool ToolsOpen { get; set; } = true;
    public string ToolPage { get; set; } = "Trim";

    public string? WatermarkPath { get; set; }
    public Corner WatermarkCorner { get; set; } = Corner.BottomRight;
    /// <summary>Width as a share of the video's width.</summary>
    public double WatermarkSize { get; set; } = 0.14;
    public double WatermarkOpacity { get; set; } = 0.8;
    public bool UseWatermark { get; set; }

    public string? IntroPath { get; set; }
    public bool UseIntro { get; set; }
    public string? OutroPath { get; set; }
    public bool UseOutro { get; set; }

    /// <summary>Music track: how loud (dB), and lowered while the clip is loud.</summary>
    public double MusicVolumeDb { get; set; } = -12;
    public bool MusicDuck { get; set; } = true;
}

/// <summary>
/// Optional modules. Roben's rule: no feature is forced on anyone, so each one can be switched off,
/// and its buttons and settings disappear with it.
/// </summary>
public sealed class FeatureSettings
{
    public bool Clipping { get; set; } = true;
    public bool Sessions { get; set; } = true;
    public bool Controller { get; set; } = true;
    public bool HoverPreview { get; set; } = true;
    public bool LoudMarkers { get; set; } = true;
    public bool HoverGlow { get; set; } = true;
    public bool Collections { get; set; } = true;
    /// <summary>A dot on videos you haven't opened yet, and the Unwatched filter.</summary>
    public bool Unwatched { get; set; } = true;
    public bool CleanUp { get; set; } = true;
    public bool Stats { get; set; } = true;
    /// <summary>Videos open in a small player over the library; Edit opens the full editor.</summary>
    public bool MiniPlayer { get; set; } = true;
    /// <summary>Session bookmarks become chapters in the player, in session files and in exports.</summary>
    public bool Chapters { get; set; } = true;
    /// <summary>Text over the video, burned into exports.</summary>
    public bool Captions { get; set; } = true;
    /// <summary>Slow motion and fast forward for parts of a clip, with speed ramps.</summary>
    public bool SpeedEffects { get; set; } = true;
    /// <summary>Even out loudness, voice boost and lowering the game while you talk.</summary>
    public bool AudioTools { get; set; } = true;
    /// <summary>Watermark and intro / outro clips.</summary>
    public bool Branding { get; set; } = true;
    /// <summary>Exports run one after another in the background while you keep working.</summary>
    public bool ExportQueue { get; set; } = true;
    /// <summary>Trim marks, parts and effects are kept when the editor closes (until you start over).</summary>
    public bool KeepEdits { get; set; }
    /// <summary>A line of text per video that search also finds.</summary>
    public bool Notes { get; set; } = true;
    /// <summary>Drag videos out of the library into other apps (Discord, a browser, Finder).</summary>
    public bool DragOut { get; set; } = true;
    /// <summary>Bring in videos from OBS, NVIDIA, Xbox Game Bar and Steam.</summary>
    public bool Recorders { get; set; } = true;
    /// <summary>Zoom and pan with keyframes in the editor.</summary>
    public bool ZoomPan { get; set; } = true;
    /// <summary>Blur or pixelate parts of the picture (names, chat, HUD).</summary>
    public bool Blur { get; set; } = true;
    /// <summary>A music track under exports, with auto-ducking.</summary>
    public bool Music { get; set; } = true;
    /// <summary>A session that was cut short by a crash or power loss is saved at the next start.</summary>
    public bool RecoverSessions { get; set; } = true;
    /// <summary>Auto montage: the best moments of several videos stitched with transitions.</summary>
    public bool Montage { get; set; } = true;

    public FeatureSettings Clone() => (FeatureSettings)MemberwiseClone();
}

public sealed class AccessibilitySettings
{
    /// <summary>Size of the whole interface, 1 = 100 %.</summary>
    public double UiScale { get; set; } = 1;
    /// <summary>Black background, white text and a bright accent, whatever the colour scheme.</summary>
    public bool HighContrast { get; set; }
    /// <summary>No animations, zooms or glows.</summary>
    public bool ReducedMotion { get; set; }
}

/// <summary>Clip length, bitrate and format for one game; 0 or null means "same as the general setting".</summary>
public sealed class ClipProfile
{
    public string Game { get; set; } = "";
    public int Seconds { get; set; }
    public int BitrateMbps { get; set; }
    public string? Codec { get; set; }
}

/// <summary>A second (third, ...) save-clip hotkey that saves its own length.</summary>
public sealed class ClipHotkey
{
    public bool Ctrl { get; set; } = true;
    public bool Alt { get; set; } = true;
    public bool Shift { get; set; }
    public bool Meta { get; set; }
    public string Key { get; set; } = "D";
    public int Seconds { get; set; } = 120;

    public string Text => ClipSettings.FormatHotkey(Ctrl, Alt, Shift, Meta, Key);
}

/// <summary>Settings for the background clipping service (see ClipService).</summary>
public sealed class ClipSettings
{
    public bool Enabled { get; set; }
    /// <summary>How much of the past a saved clip contains.</summary>
    public int Seconds { get; set; } = 30;
    public int Fps { get; set; } = 60;
    /// <summary>Output height in pixels; 0 keeps the screen's native resolution.</summary>
    public int Height { get; set; } = 1080;
    public int BitrateMbps { get; set; } = 12;
    public int AudioBitrateKbps { get; set; } = 160;
    /// <summary>"h264", "hevc" or "av1"; unsupported choices fall back to H.264.</summary>
    public string Codec { get; set; } = "h264";
    /// <summary>False forces the software (x264) encoder.</summary>
    public bool HardwareEncoder { get; set; } = true;
    public int Screen { get; set; }
    /// <summary>Capture device names as the OS reports them; null/empty = not recorded.</summary>
    public string? Audio1 { get; set; }
    public string? Audio2 { get; set; }
    public string? Folder { get; set; }
    public bool Notify { get; set; } = true;
    public bool Sound { get; set; } = true;
    /// <summary>Name and tag each clip after the app that was in front when it was saved.</summary>
    public bool TagWithGame { get; set; } = true;
    /// <summary>With two audio devices: keep them as two tracks instead of mixing them into one.</summary>
    public bool SeparateTracks { get; set; }
    /// <summary>A webcam to put in a corner of every clip; null = none.</summary>
    public string? Camera { get; set; }
    /// <summary>"top-left", "top-right", "bottom-left" or "bottom-right".</summary>
    public string CameraCorner { get; set; } = "bottom-right";
    /// <summary>The camera picture's height as a percentage of the video's.</summary>
    public int CameraSize { get; set; } = 25;

    /// <summary>Per-game overrides, matched against the app in front.</summary>
    public List<ClipProfile> Profiles { get; set; } = new();

    /// <summary>These settings with the profile for <paramref name="game"/> applied, if there is one.</summary>
    public ClipSettings For(string? game)
    {
        var p = ProfileFor(game);
        if (p is null) return this;
        var c = (ClipSettings)MemberwiseClone();
        if (p.Seconds > 0) c.Seconds = p.Seconds;
        if (p.BitrateMbps > 0) c.BitrateMbps = p.BitrateMbps;
        if (!string.IsNullOrEmpty(p.Codec)) c.Codec = p.Codec!;
        return c;
    }

    public ClipSettings WithoutCamera()
    {
        var c = (ClipSettings)MemberwiseClone();
        c.Camera = null;
        return c;
    }

    public ClipProfile? ProfileFor(string? game) => string.IsNullOrWhiteSpace(game)
        ? null
        : Profiles.FirstOrDefault(p => p.Game.Trim().Length > 0 &&
                                       game.Contains(p.Game.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Storage limits for the clips folder; 0 = no limit. Favourites are always kept.</summary>
    public int KeepDays { get; set; }
    public int MaxFolderGb { get; set; }
    /// <summary>false: removed clips go to the trash. true: they move to the archive folder.</summary>
    public bool CleanupArchive { get; set; }
    public string? ArchiveFolder { get; set; }

    /// <summary>More hotkeys, each saving a different length (a short one and a long one, say).</summary>
    public List<ClipHotkey> ExtraHotkeys { get; set; } = new();

    /// <summary>How much the rolling buffer has to hold: the longest of all the hotkeys' lengths.</summary>
    public int BufferSeconds => ExtraHotkeys.Select(h => h.Seconds).Append(Seconds).Max();

    public bool HotkeyCtrl { get; set; } = true;
    public bool HotkeyAlt { get; set; } = true;
    public bool HotkeyShift { get; set; }
    public bool HotkeyMeta { get; set; }
    public string HotkeyKey { get; set; } = "S";

    public string ResolvedArchiveFolder => string.IsNullOrWhiteSpace(ArchiveFolder)
        ? ResolvedFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + " Archive"
        : ArchiveFolder!;
    public string ResolvedFolder => string.IsNullOrWhiteSpace(Folder) ? AppPaths.DefaultClipsDir : Folder!;

    public string HotkeyText => FormatHotkey(HotkeyCtrl, HotkeyAlt, HotkeyShift, HotkeyMeta, HotkeyKey);

    public static string FormatHotkey(bool ctrl, bool alt, bool shift, bool meta, string key)
    {
        var mac = OperatingSystem.IsMacOS();
        var parts = new List<string>();
        if (ctrl) parts.Add(mac ? "⌃" : "Ctrl");
        if (alt) parts.Add(mac ? "⌥" : "Alt");
        if (shift) parts.Add(mac ? "⇧" : "Shift");
        if (meta) parts.Add(mac ? "⌘" : OperatingSystem.IsWindows() ? "Win" : "Super");
        parts.Add(key);
        return string.Join(mac ? "" : "+", parts);
    }
}

public sealed class SettingsStore
{
    /// <summary>True when there was no settings file yet: a fresh install, nothing to compare versions with.</summary>
    public bool IsFirstRun { get; } = !File.Exists(AppPaths.SettingsFile);
    public AppSettings Current { get; } = JsonStore.Load<AppSettings>(AppPaths.SettingsFile);
    public void Save() => JsonStore.Save(AppPaths.SettingsFile, Current);

    /// <summary>Takes over every setting from an imported file, except the bookkeeping of updates.</summary>
    public void ReplaceWith(AppSettings imported)
    {
        foreach (var prop in typeof(AppSettings).GetProperties())
        {
            if (!prop.CanWrite || prop.Name is nameof(AppSettings.LastRunVersion) or nameof(AppSettings.LastUpdateAttempt)) continue;
            prop.SetValue(Current, prop.GetValue(imported));
        }
        Save();
    }
}
