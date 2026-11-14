using System.Collections.ObjectModel;
using Cnema.Controls;
using Cnema.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cnema.ViewModels;

/// <summary>One caption in the Effects panel. Times are in source milliseconds.</summary>
public sealed partial class CaptionViewModel : ObservableObject
{
    [ObservableProperty] private string _text = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(RangeText))] private double _start;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(RangeText))] private double _end;
    public ChipGroup Position { get; } = new(CaptionPosition.Bottom,
        ("Top", CaptionPosition.Top), ("Middle", CaptionPosition.Middle), ("Bottom", CaptionPosition.Bottom));
    public ChipGroup Size { get; } = new(CaptionSize.Medium,
        ("S", CaptionSize.Small, "Small"), ("M", CaptionSize.Medium, "Medium"), ("L", CaptionSize.Large, "Large"));
    public ChipGroup Look { get; } = new(CaptionStyle.Outline,
        ("Outline", CaptionStyle.Outline), ("Box", CaptionStyle.Box));
    public string RangeText => $"{PlayerViewModel.FormatPrecise(Start)} – {PlayerViewModel.FormatPrecise(End)}";

    /// <summary>Anything about the caption changed.</summary>
    public event Action? Changed;

    public CaptionViewModel()
    {
        Position.PropertyChanged += (_, _) => Changed?.Invoke();
        Size.PropertyChanged += (_, _) => Changed?.Invoke();
        Look.PropertyChanged += (_, _) => Changed?.Invoke();
        PropertyChanged += (_, e) => { if (e.PropertyName is nameof(Text) or nameof(Start) or nameof(End)) Changed?.Invoke(); };
    }

    public CaptionSpec ToSpec() => new((long)Start, (long)End, Text,
        (CaptionPosition)Position.Value, (CaptionSize)Size.Value, (CaptionStyle)Look.Value);
}

/// <summary>A part of the clip at another speed: slow motion or fast forward.</summary>
public sealed partial class SpeedSectionViewModel : ObservableObject
{
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(RangeText))] private double _start;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(RangeText))] private double _end;
    [ObservableProperty] private bool _ramp = true;
    public ChipGroup Rate { get; } = new(0.5,
        ("¼×", 0.25, "Quarter speed"), ("½×", 0.5, "Half speed"), ("¾×", 0.75, "Three quarters"),
        ("1.5×", 1.5, "One and a half times"), ("2×", 2.0, "Double speed"), ("4×", 4.0, "Four times"));
    public string RangeText => $"{PlayerViewModel.FormatPrecise(Start)} – {PlayerViewModel.FormatPrecise(End)}";
    public string Title => (double)Rate.Value < 1 ? T("Slow motion") : T("Fast forward");

    public event Action? Changed;

    public SpeedSectionViewModel()
    {
        Rate.PropertyChanged += (_, _) => { OnPropertyChanged(nameof(Title)); Changed?.Invoke(); };
        PropertyChanged += (_, e) => { if (e.PropertyName is nameof(Start) or nameof(End) or nameof(Ramp)) Changed?.Invoke(); };
    }

    public SpeedSpec ToSpec() => new((long)Start, (long)End, (double)Rate.Value, Ramp);
}

// Effects in the tool panel: captions, speed, sound tools and branding, previewed live and applied on export.
public sealed partial class PlayerViewModel
{
    public ObservableCollection<CaptionViewModel> Captions { get; } = new();
    public ObservableCollection<SpeedSectionViewModel> SpeedSections { get; } = new();
    [ObservableProperty] private IReadOnlyList<CaptionSpec> _previewCaptions = Array.Empty<CaptionSpec>();
    [ObservableProperty] private WatermarkSpec? _previewWatermark;
    [ObservableProperty] private IReadOnlyList<EffectRange> _effectRanges = Array.Empty<EffectRange>();
    [ObservableProperty] private string? _speedPreviewText;
    [ObservableProperty] private ChipGroup? _voiceTrack;
    public ChipGroup VoiceBoost { get; private set; } = null!;
    private double _previewRate = 1;

    /// <summary>Raised after a caption is added, so the view can put the cursor in its text box.</summary>
    public event Action<CaptionViewModel>? CaptionAdded;

    private FeatureSettings Features => _settings.Current.Features;
    private EffectSettings Fx => _settings.Current.Effects;
    public bool ShowCaptionTools => Features.Captions;
    public bool ShowSpeedTools => Features.SpeedEffects;
    public bool ShowAudioTools => Features.AudioTools;
    public bool ShowBranding => Features.Branding;
    public bool ShowEffectsButton => ShowCaptionTools || ShowSpeedTools || ShowAudioTools || ShowBranding || ShowZoomTools || ShowBlurTools || ShowMusicTools;
    public bool HasVoiceTrackChoice => VoiceTrack is not null;
    public bool HasClearableEffects => Captions.Count + SpeedSections.Count + Blurs.Count + ZoomKeys.Count > 0;
    /// <summary>The picture's size, so the preview draws captions where the export will.</summary>
    public int VideoWidth => Current?.Entry.Width ?? 0;
    public int VideoHeight => Current?.Entry.Height ?? 0;

    private void InitEffects()
    {
        VoiceBoost = new ChipGroup(Nearest(Fx.VoiceBoostDb, 0, 3, 6, 9), ("Off", 0.0), ("+3 dB", 3.0), ("+6 dB", 6.0), ("+9 dB", 9.0));
        VoiceBoost.PropertyChanged += (_, _) =>
        {
            Fx.VoiceBoostDb = (double)VoiceBoost.Value;
            _settings.Save();
            EffectsChanged();
        };
        Captions.CollectionChanged += (_, _) => EffectsChanged();
        SpeedSections.CollectionChanged += (_, _) => EffectsChanged();
    }

    private static double Nearest(double v, params double[] options) => options.OrderBy(o => Math.Abs(o - v)).First();

    /// <summary>After a feature was switched on or off in Settings.</summary>
    public void RefreshEffectFeatures()
    {
        foreach (var name in new[]
                 {
                     nameof(ShowCaptionTools), nameof(ShowSpeedTools), nameof(ShowAudioTools), nameof(ShowBranding),
                     nameof(ShowEffectsButton), nameof(ShowZoomTools), nameof(ShowBlurTools), nameof(ShowMusicTools), nameof(HasWatermark), nameof(HasIntro), nameof(HasOutro),
                     nameof(UseWatermark), nameof(UseIntro), nameof(UseOutro), nameof(WatermarkName), nameof(IntroName), nameof(OutroName),
                 })
            OnPropertyChanged(name);
        RefreshToolPages();
        EffectsChanged();
    }

    // ---------- sound (kept between exports) ----------
    public bool Normalize
    {
        get => Fx.Normalize;
        set { if (Fx.Normalize == value) return; Fx.Normalize = value; _settings.Save(); OnPropertyChanged(); EffectsChanged(); }
    }

    public bool Duck
    {
        get => Fx.Duck;
        set { if (Fx.Duck == value) return; Fx.Duck = value; _settings.Save(); OnPropertyChanged(); EffectsChanged(); }
    }

    partial void OnVoiceTrackChanged(ChipGroup? value)
    {
        OnPropertyChanged(nameof(HasVoiceTrackChoice));
        if (value is not null) value.PropertyChanged += (_, _) => EffectsChanged();
        EffectsChanged();
    }

    /// <summary>With two or more audio tracks, one of them can be the voice (the last one, to start with).</summary>
    private void BuildVoiceTrackChoice()
    {
        VoiceTrack = AudioTracks.Count < 2
            ? null
            : new ChipGroup(AudioTracks[^1].Index, AudioTracks.Select(t => (t.Label, (object)t.Index)).ToArray());
    }

    // ---------- branding (kept between exports, set up in Settings) ----------
    public bool HasWatermark => File.Exists(Fx.WatermarkPath);
    public bool HasIntro => File.Exists(Fx.IntroPath);
    public bool HasOutro => File.Exists(Fx.OutroPath);
    public string WatermarkName => HasWatermark ? Path.GetFileName(Fx.WatermarkPath!) : T("Pick an image in Settings, Editing");
    public string IntroName => HasIntro ? Path.GetFileName(Fx.IntroPath!) : T("Pick a clip in Settings, Editing");
    public string OutroName => HasOutro ? Path.GetFileName(Fx.OutroPath!) : T("Pick a clip in Settings, Editing");

    public bool UseWatermark
    {
        get => Fx.UseWatermark && HasWatermark;
        set { if (Fx.UseWatermark == value) return; Fx.UseWatermark = value; _settings.Save(); OnPropertyChanged(); EffectsChanged(); }
    }

    public bool UseIntro
    {
        get => Fx.UseIntro && HasIntro;
        set { if (Fx.UseIntro == value) return; Fx.UseIntro = value; _settings.Save(); OnPropertyChanged(); EffectsChanged(); }
    }

    public bool UseOutro
    {
        get => Fx.UseOutro && HasOutro;
        set { if (Fx.UseOutro == value) return; Fx.UseOutro = value; _settings.Save(); OnPropertyChanged(); EffectsChanged(); }
    }

    // ---------- the effects as a whole ----------
    /// <summary>What an export adds, with switched-off features left out.</summary>
    public EffectsSpec BuildEffects()
    {
        var captions = ShowCaptionTools
            ? Captions.Where(c => c.Text.Trim().Length > 0 && c.End > c.Start).Select(c => c.ToSpec()).ToList()
            : new List<CaptionSpec>();
        var speed = ShowSpeedTools
            ? SpeedSections.Where(s => s.End - s.Start >= 100).Select(s => s.ToSpec()).ToList()
            : new List<SpeedSpec>();
        var audio = ShowAudioTools
            ? new AudioSpec(Normalize, VoiceTrack is { } v ? (int)v.Value : -1, VoiceTrack is null ? 0 : (double)VoiceBoost.Value,
                VoiceTrack is not null && Duck)
            : AudioSpec.None;
        WatermarkSpec? watermark = null;
        string? intro = null, outro = null;
        if (ShowBranding)
        {
            if (UseWatermark) watermark = new WatermarkSpec(Fx.WatermarkPath!, Fx.WatermarkCorner, Fx.WatermarkSize, Fx.WatermarkOpacity);
            if (UseIntro) intro = Fx.IntroPath;
            if (UseOutro) outro = Fx.OutroPath;
        }
        return new EffectsSpec(captions, speed, audio, watermark, intro, outro)
        {
            Blurs = ShowBlurTools ? Blurs.Where(b => b.End - b.Start >= 100).Select(b => b.ToSpec()).ToList() : new List<BlurSpec>(),
            Zoom = ShowZoomTools ? ZoomKeys.Select(k => k.ToSpec()).ToList() : new List<ZoomKey>(),
            Music = BuildMusic(),
        };
    }

    public int EffectsCount
    {
        get
        {
            var fx = BuildEffects();
            return fx.Captions.Count + fx.Speed.Count + fx.Blurs.Count + (fx.HasZoom ? 1 : 0) + (fx.Music is null ? 0 : 1) + (fx.Audio.Normalize ? 1 : 0) + (fx.Audio.VoiceBoostDb != 0 ? 1 : 0) +
                   (fx.Audio.Duck ? 1 : 0) + (fx.Watermark is null ? 0 : 1) + (fx.Intro is null ? 0 : 1) + (fx.Outro is null ? 0 : 1);
        }
    }

    public bool HasEffects => EffectsCount > 0;
    public string EffectsButtonText => EffectsCount is var n && n > 0 ? F("Effects · {0}", n) : T("Effects");
    public string EffectsNote => HasEffects
        ? T("Exports and Share re-encode the clip to apply these, so Lossless is skipped.")
        : T("Nothing added yet. Effects only change exports; the original video stays as it is.");

    private void EffectsChanged()
    {
        OnPropertyChanged(nameof(EffectsCount));
        OnPropertyChanged(nameof(HasEffects));
        OnPropertyChanged(nameof(EffectsButtonText));
        OnPropertyChanged(nameof(EffectsNote));
        OnPropertyChanged(nameof(HasClearableEffects));
        OnPropertyChanged(nameof(ShowClearEffects));
        var ranges = new List<EffectRange>();
        if (ShowCaptionTools) ranges.AddRange(Captions.Select(c => new EffectRange(c.Start, c.End, false)));
        if (ShowSpeedTools) ranges.AddRange(SpeedSections.Select(s => new EffectRange(s.Start, s.End, true)));
        if (ShowBlurTools) ranges.AddRange(Blurs.Where(b => b.End - b.Start < Duration - 50).Select(b => new EffectRange(b.Start, b.End, false)));
        EffectRanges = ranges;
        UpdateEffectsPreview(force: true);
    }

    /// <summary>Captions and watermark over the picture, and speed sections played at their speed.</summary>
    private void UpdateEffectsPreview(bool force = false)
    {
        var t = Position;
        var now = ShowCaptionTools
            ? Captions.Where(c => c.Text.Trim().Length > 0 && t >= c.Start && t < c.End).Select(c => c.ToSpec()).ToList()
            : new List<CaptionSpec>();
        if (force || !now.SequenceEqual(PreviewCaptions)) PreviewCaptions = now;
        if (force)
            PreviewWatermark = ShowBranding && UseWatermark
                ? new WatermarkSpec(Fx.WatermarkPath!, Fx.WatermarkCorner, Fx.WatermarkSize, Fx.WatermarkOpacity)
                : null;

        UpdateFramePreview();
        var rate = ShowSpeedTools && SpeedSections.Count > 0 ? TimeMap.RateAt(SpeedSections.Select(s => s.ToSpec()).ToList(), t) : 1;
        if (Math.Abs(rate - _previewRate) < 0.01) return;
        _previewRate = rate;
        _player?.SetRate((float)(Rate * rate));
        SpeedPreviewText = Math.Abs(rate - 1) < 0.01 ? null : rate < 1 ? F("Slow motion {0:0.##}×", rate) : F("Fast forward {0:0.##}×", rate);
    }

    private void ResetEffects()
    {
        Captions.Clear();
        SpeedSections.Clear();
        ClearFrameTools();
        MusicPath = null;
        _previewRate = 1;
        SpeedPreviewText = null;
        VoiceTrack = null;
        EffectsChanged();
    }

    // ---------- commands ----------
    private (double Start, double End) NewRange(double length)
    {
        var start = Math.Max(0, Position);
        var end = Math.Min(Duration, start + length);
        if (end - start < 300) start = Math.Max(0, end - length);
        return (start, end);
    }

    [RelayCommand]
    private void AddCaption()
    {
        if (!ShowCaptionTools || Duration <= 0) return;
        var (start, end) = NewRange(3000);
        var c = new CaptionViewModel { Start = start, End = end };
        c.Changed += EffectsChanged;
        Captions.Add(c);
        ShowPage(ToolPage.Captions);
        CaptionAdded?.Invoke(c);
    }

    [RelayCommand]
    private void RemoveCaption(CaptionViewModel? c)
    {
        if (c is null) return;
        c.Changed -= EffectsChanged;
        Captions.Remove(c);
    }

    [RelayCommand]
    private void CaptionStartHere(CaptionViewModel? c)
    {
        if (c is null) return;
        c.Start = Position;
        if (c.End <= c.Start + 100) c.End = Math.Min(Duration, c.Start + 3000);
    }

    [RelayCommand]
    private void CaptionEndHere(CaptionViewModel? c)
    {
        if (c is null) return;
        if (Position <= c.Start + 100) { ShowToast(T("The caption has to end after it starts.")); return; }
        c.End = Position;
    }

    [RelayCommand]
    private void JumpToCaption(CaptionViewModel? c)
    {
        if (c is not null) Seek(c.Start);
    }

    [RelayCommand]
    private void AddSpeedSection()
    {
        if (!ShowSpeedTools || Duration <= 0) return;
        var (start, end) = NewRange(2000);
        var s = new SpeedSectionViewModel { Start = start, End = end };
        s.Changed += EffectsChanged;
        SpeedSections.Add(s);
        ShowPage(ToolPage.Speed);
    }

    [RelayCommand]
    private void RemoveSpeedSection(SpeedSectionViewModel? s)
    {
        if (s is null) return;
        s.Changed -= EffectsChanged;
        SpeedSections.Remove(s);
    }

    [RelayCommand]
    private void SpeedStartHere(SpeedSectionViewModel? s)
    {
        if (s is null) return;
        s.Start = Position;
        if (s.End <= s.Start + 100) s.End = Math.Min(Duration, s.Start + 2000);
    }

    [RelayCommand]
    private void SpeedEndHere(SpeedSectionViewModel? s)
    {
        if (s is null) return;
        if (Position <= s.Start + 100) { ShowToast(T("The section has to end after it starts.")); return; }
        s.End = Position;
    }

    [RelayCommand]
    private void JumpToSpeedSection(SpeedSectionViewModel? s)
    {
        if (s is null) return;
        Seek(s.Start);
        if (_player is { IsPlaying: false }) _player.SetPause(false);
    }

    [RelayCommand]
    private void ClearEffects()
    {
        foreach (var c in Captions) c.Changed -= EffectsChanged;
        foreach (var s in SpeedSections) s.Changed -= EffectsChanged;
        Captions.Clear();
        SpeedSections.Clear();
        ClearFrameTools();
        MusicPath = null;
        ShowToast(T("Captions, speed changes, blurred areas and zoom removed."));
    }
}
