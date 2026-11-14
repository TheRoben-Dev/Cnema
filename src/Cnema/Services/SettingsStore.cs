namespace Cnema.Services;

public sealed class AppSettings
{
    public List<string> Folders { get; set; } = new();
    public int SortIndex { get; set; }
    public bool PreciseExport { get; set; }
    public int Volume { get; set; } = 80;

    /// <summary>Stay alive in the tray / menu bar when the window is closed.</summary>
    public bool KeepInBackground { get; set; }
    public ClipSettings Clip { get; set; } = new();
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
    /// <summary>False forces the software (x264) encoder.</summary>
    public bool HardwareEncoder { get; set; } = true;
    public int Screen { get; set; }
    /// <summary>Capture device names as the OS reports them; null/empty = not recorded.</summary>
    public string? Audio1 { get; set; }
    public string? Audio2 { get; set; }
    public string? Folder { get; set; }
    public bool Notify { get; set; } = true;

    public bool HotkeyCtrl { get; set; } = true;
    public bool HotkeyAlt { get; set; } = true;
    public bool HotkeyShift { get; set; }
    public bool HotkeyMeta { get; set; }
    public string HotkeyKey { get; set; } = "S";

    public string ResolvedFolder => string.IsNullOrWhiteSpace(Folder) ? AppPaths.DefaultClipsDir : Folder!;

    public string HotkeyText
    {
        get
        {
            var mac = OperatingSystem.IsMacOS();
            var parts = new List<string>();
            if (HotkeyCtrl) parts.Add(mac ? "⌃" : "Ctrl");
            if (HotkeyAlt) parts.Add(mac ? "⌥" : "Alt");
            if (HotkeyShift) parts.Add(mac ? "⇧" : "Shift");
            if (HotkeyMeta) parts.Add(mac ? "⌘" : OperatingSystem.IsWindows() ? "Win" : "Super");
            parts.Add(HotkeyKey);
            return string.Join(mac ? "" : "+", parts);
        }
    }
}

public sealed class SettingsStore
{
    public AppSettings Current { get; } = JsonStore.Load<AppSettings>(AppPaths.SettingsFile);
    public void Save() => JsonStore.Save(AppPaths.SettingsFile, Current);
}
