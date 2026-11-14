using Cnema.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cnema.ViewModels;

// Settings, Editing: the watermark and intro / outro clips, plus the editing features' switches.
public sealed partial class SettingsViewModel
{
    /// <summary>Set by the view: (title, true for an image / false for a video) to the chosen file, or null.</summary>
    public Func<string, bool, Task<string?>>? PickFile { get; set; }

    // ---------- features ----------
    [ObservableProperty] private bool _featureCaptions;
    [ObservableProperty] private bool _featureSpeedEffects;
    [ObservableProperty] private bool _featureAudioTools;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowBrandingOffNotice))] private bool _featureBranding;
    [ObservableProperty] private bool _featureExportQueue;
    [ObservableProperty] private bool _featureKeepEdits;
    [ObservableProperty] private bool _featureNotes;
    [ObservableProperty] private bool _featureDragOut;
    [ObservableProperty] private bool _featureRecorders;
    [ObservableProperty] private bool _featureZoomPan;
    [ObservableProperty] private bool _featureBlur;
    public bool ShowBrandingOffNotice => !FeatureBranding;

    // ---------- watermark ----------
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasWatermarkImage), nameof(WatermarkName))] private string? _watermarkPath;
    public ChipGroup WatermarkCorner { get; private set; } = null!;
    /// <summary>Width in percent of the video's width.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(WatermarkSizeText))] private double _watermarkSize;
    /// <summary>Percent.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(WatermarkOpacityText))] private double _watermarkOpacity;
    [ObservableProperty] private bool _useWatermark;
    public bool HasWatermarkImage => !string.IsNullOrWhiteSpace(WatermarkPath);
    public string WatermarkName => HasWatermarkImage ? Path.GetFileName(WatermarkPath!) : T("No image yet. A PNG with a transparent background works best.");
    public string WatermarkSizeText => $"{WatermarkSize:0} %";
    public string WatermarkOpacityText => $"{WatermarkOpacity:0} %";

    // ---------- intro / outro ----------
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasIntroClip), nameof(IntroName))] private string? _introPath;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasOutroClip), nameof(OutroName))] private string? _outroPath;
    [ObservableProperty] private bool _useIntro;
    [ObservableProperty] private bool _useOutro;
    public bool HasIntroClip => !string.IsNullOrWhiteSpace(IntroPath);
    public bool HasOutroClip => !string.IsNullOrWhiteSpace(OutroPath);
    public string IntroName => HasIntroClip ? Path.GetFileName(IntroPath!) : T("None");
    public string OutroName => HasOutroClip ? Path.GetFileName(OutroPath!) : T("None");

    private void LoadEditing(AppSettings settings)
    {
        var f = settings.Features;
        FeatureCaptions = f.Captions;
        FeatureSpeedEffects = f.SpeedEffects;
        FeatureAudioTools = f.AudioTools;
        FeatureBranding = f.Branding;
        FeatureExportQueue = f.ExportQueue;
        FeatureKeepEdits = f.KeepEdits;
        FeatureNotes = f.Notes;
        FeatureDragOut = f.DragOut;
        FeatureRecorders = f.Recorders;
        FeatureZoomPan = f.ZoomPan;
        FeatureBlur = f.Blur;
        var e = settings.Effects;
        WatermarkPath = e.WatermarkPath;
        WatermarkCorner = new ChipGroup(e.WatermarkCorner,
            ("Top left", Corner.TopLeft), ("Top right", Corner.TopRight),
            ("Bottom left", Corner.BottomLeft), ("Bottom right", Corner.BottomRight));
        WatermarkSize = Math.Clamp(Math.Round(e.WatermarkSize * 100), 5, 40);
        WatermarkOpacity = Math.Clamp(Math.Round(e.WatermarkOpacity * 100), 20, 100);
        UseWatermark = e.UseWatermark;
        IntroPath = e.IntroPath;
        OutroPath = e.OutroPath;
        UseIntro = e.UseIntro;
        UseOutro = e.UseOutro;
    }

    private void WriteEditing(AppSettings settings)
    {
        var e = settings.Effects;
        e.WatermarkPath = HasWatermarkImage ? WatermarkPath : null;
        e.WatermarkCorner = (Corner)WatermarkCorner.Value;
        e.WatermarkSize = WatermarkSize / 100;
        e.WatermarkOpacity = WatermarkOpacity / 100;
        e.UseWatermark = UseWatermark && HasWatermarkImage;
        e.IntroPath = HasIntroClip ? IntroPath : null;
        e.OutroPath = HasOutroClip ? OutroPath : null;
        e.UseIntro = UseIntro && HasIntroClip;
        e.UseOutro = UseOutro && HasOutroClip;
    }

    [RelayCommand]
    private void EnableBranding() => FeatureBranding = true;

    [RelayCommand]
    private async Task ChooseWatermarkAsync()
    {
        if (PickFile is null) return;
        var picked = await PickFile(T("Pick a watermark image"), true);
        if (string.IsNullOrWhiteSpace(picked)) return;
        WatermarkPath = picked;
        UseWatermark = true;
    }

    [RelayCommand]
    private void RemoveWatermark()
    {
        WatermarkPath = null;
        UseWatermark = false;
    }

    [RelayCommand]
    private async Task ChooseIntroAsync()
    {
        if (PickFile is null) return;
        var picked = await PickFile(T("Pick an intro clip"), false);
        if (string.IsNullOrWhiteSpace(picked)) return;
        IntroPath = picked;
        UseIntro = true;
    }

    [RelayCommand]
    private void RemoveIntro()
    {
        IntroPath = null;
        UseIntro = false;
    }

    [RelayCommand]
    private async Task ChooseOutroAsync()
    {
        if (PickFile is null) return;
        var picked = await PickFile(T("Pick an outro clip"), false);
        if (string.IsNullOrWhiteSpace(picked)) return;
        OutroPath = picked;
        UseOutro = true;
    }

    [RelayCommand]
    private void RemoveOutro()
    {
        OutroPath = null;
        UseOutro = false;
    }
}
