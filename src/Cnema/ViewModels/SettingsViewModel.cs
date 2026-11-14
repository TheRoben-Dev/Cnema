using System.Collections.ObjectModel;
using Cnema.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cnema.ViewModels;

/// <summary>One entry of the Controller mode button legend. Kind picks the face-button colour.</summary>
public sealed record PadHint(string Glyph, string Label, string Kind)
{
    public bool IsA => Kind == "a";
    public bool IsB => Kind == "b";
    public bool IsX => Kind == "x";
    public bool IsY => Kind == "y";
}

public sealed record Choice(string Label, int Value)
{
    public override string ToString() => Label;
}

/// <summary>Editable copy of the background + clipping settings; nothing changes until Save.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    public const string NoAudio = "None";

    public static Choice[] Lengths { get; } =
    {
        new("15 seconds", 15), new("30 seconds", 30), new("1 minute", 60), new("1.5 minutes", 90),
        new("2 minutes", 120), new("3 minutes", 180), new("5 minutes", 300),
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

    public ObservableCollection<string> AudioDevices { get; } = new() { NoAudio };
    public ObservableCollection<string> Screens { get; } = new();
    public ObservableCollection<CodecOption> Codecs { get; } = new();

    /// <summary>Set by the view: opens a native folder picker.</summary>
    public Func<Task<string?>>? PickFolder { get; set; }

    [ObservableProperty] private bool _launchAtLogin;
    [ObservableProperty] private bool _keepInBackground;
    [ObservableProperty] private bool _controllerMode;
    [ObservableProperty] private bool _controllerFullscreen;
    [ObservableProperty] private string _controllerStatus = "";
    [ObservableProperty] private bool _clipEnabled;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Footprint))] private Choice _length;
    [ObservableProperty] private Choice _resolution;
    [ObservableProperty] private Choice _frameRate;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Footprint))] private Choice _bitrate;
    [ObservableProperty] private Choice _audioBitrate;
    [ObservableProperty] private bool _hardwareEncoder;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CodecDetail))] private CodecOption? _codec;
    private string _codecWanted = "h264";
    public string CodecDetail => Codec is null ? "Checking what this computer can encode…"
        : Codec.Hardware ? $"Encoded on the graphics chip ({Codec.Encoder})." : "Encoded on the CPU (x264).";
    partial void OnHardwareEncoderChanged(bool value) => _ = LoadCodecsAsync();
    [ObservableProperty] private int _screenIndex;
    [ObservableProperty] private string _audio1 = NoAudio;
    [ObservableProperty] private string _audio2 = NoAudio;
    [ObservableProperty] private bool _notify;
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
        ? "Keep running in the menu bar when the window is closed"
        : "Keep running in the system tray when the window is closed";

    public string AudioHint => OperatingSystem.IsMacOS()
        ? CaptureProbe.SystemAudioHelper is not null
            ? "\"System audio\" records everything you hear (games included) and is covered by the Screen Recording permission. Pick your microphone as the second device to add your voice."
            : "System audio needs macOS 13 or newer. Microphones work as they are; for game sound, install a loopback device such as BlackHole and pick it here."
        : OperatingSystem.IsWindows()
            ? "Microphones work as they are. For game sound, enable \"Stereo Mix\" in Windows sound settings (or install a loopback device) and pick it here."
            : "Sources ending in \".monitor\" record what the speakers play.";

    /// <summary>Temp disk the rolling buffer needs; nothing else is written until a clip is saved.</summary>
    public string Footprint =>
        $"Keeps the last {Length.Label} in a temporary buffer of about {Bitrate.Value * (Length.Value + 6) / 8:N0} MB. " +
        "Nothing is kept unless you save a clip.";

    public SettingsViewModel(AppSettings settings)
    {
        var c = settings.Clip;
        _launchAtLogin = AutostartService.IsEnabled;
        _keepInBackground = settings.KeepInBackground;
        _controllerMode = settings.ControllerMode;
        _controllerFullscreen = settings.ControllerFullscreen;
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
        _hotkeyCtrl = c.HotkeyCtrl;
        _hotkeyAlt = c.HotkeyAlt;
        _hotkeyShift = c.HotkeyShift;
        _hotkeyMeta = c.HotkeyMeta;
        _hotkeyKey = Keys.Contains(c.HotkeyKey) ? c.HotkeyKey : "S";
        _folder = c.ResolvedFolder;
        // Show saved devices straight away; the real list replaces them once probed.
        foreach (var name in new[] { c.Audio1, c.Audio2 })
            if (!string.IsNullOrWhiteSpace(name) && !AudioDevices.Contains(name)) AudioDevices.Add(name);
        _audio1 = string.IsNullOrWhiteSpace(c.Audio1) ? NoAudio : c.Audio1!;
        _audio2 = string.IsNullOrWhiteSpace(c.Audio2) ? NoAudio : c.Audio2!;
        Screens.Add("Primary display");
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

    /// <summary>Copies the edits into the settings object. Returns a message if something is invalid.</summary>
    public string? WriteTo(AppSettings settings)
    {
        if (ClipEnabled && !(HotkeyCtrl || HotkeyAlt || HotkeyShift || HotkeyMeta) && HotkeyKey.Length == 1)
            return "Add at least one modifier to the hotkey, or a plain letter would stop working everywhere else.";
        var c = settings.Clip;
        settings.KeepInBackground = KeepInBackground;
        settings.ControllerMode = ControllerMode;
        settings.ControllerFullscreen = ControllerFullscreen;
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
        c.Notify = Notify;
        c.HotkeyCtrl = HotkeyCtrl;
        c.HotkeyAlt = HotkeyAlt;
        c.HotkeyShift = HotkeyShift;
        c.HotkeyMeta = HotkeyMeta;
        c.HotkeyKey = HotkeyKey;
        c.Folder = string.IsNullOrWhiteSpace(Folder) || Folder == AppPaths.DefaultClipsDir ? null : Folder.Trim();
        return null;
    }
}
