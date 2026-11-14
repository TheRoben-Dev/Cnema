using System.Collections.ObjectModel;
using Avalonia;
using Cnema.Controls;
using Cnema.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cnema.ViewModels;

/// <summary>A hidden area on the Blur page. Times in source milliseconds, the area in shares of the picture.</summary>
public sealed partial class BlurViewModel : ObservableObject
{
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(RangeText))] private double _start;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(RangeText))] private double _end;
    [ObservableProperty] private Rect _area = new(0.35, 0.35, 0.3, 0.2);
    [ObservableProperty] private bool _isSelected;
    public ChipGroup Look { get; } = new(BlurStyle.Blur, ("Blur", BlurStyle.Blur), ("Pixelate", BlurStyle.Pixelate));
    public string RangeText => $"{PlayerViewModel.FormatPrecise(Start)} – {PlayerViewModel.FormatPrecise(End)}";
    public int Number { get; set; }
    public string Title => F("Area {0}", Number);

    public event Action? Changed;
    public void NotifyTitle() => OnPropertyChanged(nameof(Title));

    public BlurViewModel()
    {
        Look.PropertyChanged += (_, _) => Changed?.Invoke();
        PropertyChanged += (_, e) => { if (e.PropertyName is nameof(Start) or nameof(End) or nameof(Area)) Changed?.Invoke(); };
    }

    public BlurSpec ToSpec() => new((long)Start, (long)End, Area.X, Area.Y, Area.Width, Area.Height, (BlurStyle)Look.Value);
}

/// <summary>A zoom keyframe on the Zoom page.</summary>
public sealed partial class ZoomKeyViewModel : ObservableObject
{
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(TimeText))] private double _at;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ZoomText))] private double _zoom = 1;
    [ObservableProperty] private double _x = 0.5;
    [ObservableProperty] private double _y = 0.5;
    [ObservableProperty] private bool _isSelected;
    public string TimeText => PlayerViewModel.FormatPrecise(At);
    public string ZoomText => Zoom <= 1.001 ? T("Whole picture") : $"{Zoom:0.0}×";

    public event Action? Changed;

    public ZoomKeyViewModel()
    {
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(At) or nameof(Zoom) or nameof(X) or nameof(Y)) Changed?.Invoke();
        };
    }

    public ZoomKey ToSpec() => new((long)At, Zoom, X, Y);
}

// Blur and Zoom pages: hide parts of the picture, and punch in on the action with keyframes.
public sealed partial class PlayerViewModel
{
    public ObservableCollection<BlurViewModel> Blurs { get; } = new();
    public ObservableCollection<ZoomKeyViewModel> ZoomKeys { get; } = new();
    [ObservableProperty] private ZoomView _previewView = ZoomView.Whole;
    [ObservableProperty] private IReadOnlyList<BlurSpec> _previewBlurs = Array.Empty<BlurSpec>();
    [ObservableProperty] private IReadOnlyList<FrameRegion> _frameRegions = Array.Empty<FrameRegion>();
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasSelectedBlur))] private BlurViewModel? _selectedBlur;

    public bool ShowZoomTools => Features.ZoomPan;
    public bool ShowBlurTools => Features.Blur;
    public bool HasSelectedBlur => SelectedBlur is not null;
    public bool HasZoomKeys => ZoomKeys.Count > 0;

    /// <summary>The Zoom slider: the zoom at the playhead. Moving it sets (or adds) the key there.</summary>
    public double ZoomAtPlayhead
    {
        get => ZoomView.At(ZoomSpecs(), Position).Zoom;
        set
        {
            var key = KeyAtPlayhead(create: true);
            if (key is null) return;
            key.Zoom = Math.Round(Math.Clamp(value, 1, ZoomView.MaxZoom), 2);
        }
    }

    public string ZoomAtPlayheadText => $"{ZoomAtPlayhead:0.0}×";

    private void InitFrameTools()
    {
        Blurs.CollectionChanged += (_, _) => { Renumber(); EffectsChanged(); };
        ZoomKeys.CollectionChanged += (_, _) => { OnPropertyChanged(nameof(HasZoomKeys)); EffectsChanged(); };
    }

    private void Renumber()
    {
        for (var i = 0; i < Blurs.Count; i++)
        {
            Blurs[i].Number = i + 1;
            Blurs[i].NotifyTitle();
        }
    }

    private List<ZoomKey> ZoomSpecs() => ZoomKeys.Select(k => k.ToSpec()).ToList();

    /// <summary>Picture preview: zoom applies except on the pages where you edit the whole frame; blurred areas always.</summary>
    private void UpdateFramePreview()
    {
        var t = Position;
        var editingFrame = IsZoomPage || IsBlurPage;
        var view = ShowZoomTools && ZoomKeys.Count > 0 && !editingFrame ? ZoomView.At(ZoomSpecs(), t) : ZoomView.Whole;
        if (view != PreviewView) PreviewView = view;

        var blurs = ShowBlurTools
            ? Blurs.Where(b => t >= b.Start && t < b.End).Select(b => b.ToSpec()).ToList()
            : new List<BlurSpec>();
        if (!blurs.SequenceEqual(PreviewBlurs)) PreviewBlurs = blurs;

        var regions = new List<FrameRegion>();
        if (IsBlurPage && ShowBlurTools)
            regions.AddRange(Blurs.Where(b => t >= b.Start && t < b.End || b.IsSelected)
                .Select(b => new FrameRegion(b, b.Area, b.IsSelected, false, b.Title)));
        if (IsZoomPage && ShowZoomTools)
        {
            var v = ZoomView.At(ZoomSpecs(), t);
            var (l, top, w, h) = v.Window;
            regions.Add(new FrameRegion(ZoomFrameTag, new Rect(l, top, w, h), true, true, F("Zoom {0:0.0}×", v.Zoom)));
        }
        if (!regions.SequenceEqual(FrameRegions)) FrameRegions = regions;
        OnPropertyChanged(nameof(ZoomAtPlayhead));
        OnPropertyChanged(nameof(ZoomAtPlayheadText));
    }

    private static readonly object ZoomFrameTag = new();

    /// <summary>The view moved a rectangle on the picture.</summary>
    public void MoveFrameRegion(object tag, Rect area)
    {
        if (tag is BlurViewModel b) { b.Area = area; return; }
        if (!ReferenceEquals(tag, ZoomFrameTag) || KeyAtPlayhead(create: true) is not { } key) return;
        key.Zoom = Math.Round(Math.Clamp(1 / Math.Max(area.Width, 0.01), 1, ZoomView.MaxZoom), 2);
        key.X = area.X + area.Width / 2;
        key.Y = area.Y + area.Height / 2;
    }

    public void PickFrameRegion(object tag)
    {
        if (tag is BlurViewModel b) SelectBlur(b);
    }

    /// <summary>The key at the playhead (within a frame or two), or a new one there with the current view.</summary>
    private ZoomKeyViewModel? KeyAtPlayhead(bool create)
    {
        if (!ShowZoomTools || Duration <= 0) return null;
        var t = Math.Round(Position);
        var key = ZoomKeys.FirstOrDefault(k => Math.Abs(k.At - t) <= 60);
        if (key is not null || !create) return Select(key);
        var v = ZoomView.At(ZoomSpecs(), t);
        // The very first key starts zoomed in, since a lone 1× key does nothing.
        key = new ZoomKeyViewModel { At = t, Zoom = ZoomKeys.Count == 0 ? 2 : v.Zoom, X = v.X, Y = v.Y };
        AddKey(key);
        return Select(key);
    }

    private ZoomKeyViewModel? Select(ZoomKeyViewModel? key)
    {
        foreach (var k in ZoomKeys) k.IsSelected = ReferenceEquals(k, key);
        return key;
    }

    private void AddKey(ZoomKeyViewModel key)
    {
        key.Changed += EffectsChanged;
        var at = 0;
        while (at < ZoomKeys.Count && ZoomKeys[at].At < key.At) at++;
        ZoomKeys.Insert(at, key);
    }

    // ---------- zoom commands ----------
    [RelayCommand]
    private void AddZoomKey()
    {
        if (!ShowZoomTools || Duration <= 0) return;
        ShowPage(ToolPage.Zoom);
        if (KeyAtPlayhead(create: false) is not null) { ShowToast(T("There is already a key here. Drag the frame on the video to change it.")); return; }
        KeyAtPlayhead(create: true);
    }

    /// <summary>Zoom in on the playhead and back out: whole picture, 2× for 1.5 s, whole picture again.</summary>
    [RelayCommand]
    private void PunchIn()
    {
        if (!ShowZoomTools || Duration <= 0) return;
        ShowPage(ToolPage.Zoom);
        var t = Math.Round(Position);
        var v = ZoomView.At(ZoomSpecs(), t);
        var (x, y) = v.IsWhole ? (0.5, 0.5) : (v.X, v.Y);
        var times = new[] { t - 350, t, t + 1500, t + 1850 };
        if (times[0] < 0 || times[^1] > Duration)
        {
            var shift = times[0] < 0 ? -times[0] : Duration - times[^1];
            for (var i = 0; i < times.Length; i++) times[i] += shift;
        }
        foreach (var k in ZoomKeys.Where(k => k.At >= times[0] - 60 && k.At <= times[^1] + 60).ToList()) RemoveZoomKey(k);
        AddKey(new ZoomKeyViewModel { At = Math.Max(0, times[0]), Zoom = 1, X = x, Y = y });
        var inKey = new ZoomKeyViewModel { At = times[1], Zoom = 2, X = x, Y = y };
        AddKey(inKey);
        AddKey(new ZoomKeyViewModel { At = times[2], Zoom = 2, X = x, Y = y });
        AddKey(new ZoomKeyViewModel { At = Math.Min(Duration, times[3]), Zoom = 1, X = x, Y = y });
        Select(inKey);
        Seek(times[1]);
        ShowToast(T("Zoomed in for 1.5 seconds. Drag the frame to aim it."));
    }

    [RelayCommand]
    private void RemoveZoomKey(ZoomKeyViewModel? k)
    {
        if (k is null) return;
        k.Changed -= EffectsChanged;
        ZoomKeys.Remove(k);
    }

    [RelayCommand]
    private void JumpToZoomKey(ZoomKeyViewModel? k)
    {
        if (k is null) return;
        Select(k);
        Seek(k.At);
    }

    [RelayCommand]
    private void ClearZoom()
    {
        foreach (var k in ZoomKeys) k.Changed -= EffectsChanged;
        ZoomKeys.Clear();
    }

    // ---------- blur commands ----------
    [RelayCommand]
    private void AddBlur()
    {
        if (!ShowBlurTools || Duration <= 0) return;
        // The selection if there is one, otherwise the whole video: names and HUD tend to stay put.
        var (start, end) = HasIn || HasOut ? (Math.Max(0, InPoint), OutPoint > 0 ? OutPoint : Duration) : (0, Duration);
        var offset = Blurs.Count % 4 * 0.05;
        var b = new BlurViewModel { Start = start, End = end, Area = new Rect(0.35 + offset, 0.35 + offset, 0.3, 0.2) };
        b.Changed += EffectsChanged;
        Blurs.Add(b);
        SelectBlur(b);
        ShowPage(ToolPage.Blur);
    }

    [RelayCommand]
    private void SelectBlur(BlurViewModel? b)
    {
        foreach (var x in Blurs) x.IsSelected = ReferenceEquals(x, b);
        SelectedBlur = b;
        if (b is not null && (Position < b.Start || Position >= b.End)) Seek(b.Start);
        UpdateFramePreview();
    }

    [RelayCommand]
    private void RemoveBlur(BlurViewModel? b)
    {
        if (b is null) return;
        b.Changed -= EffectsChanged;
        Blurs.Remove(b);
        if (ReferenceEquals(SelectedBlur, b)) SelectedBlur = null;
    }

    [RelayCommand]
    private void BlurStartHere(BlurViewModel? b)
    {
        if (b is null) return;
        b.Start = Position;
        if (b.End <= b.Start + 100) b.End = Duration;
    }

    [RelayCommand]
    private void BlurEndHere(BlurViewModel? b)
    {
        if (b is null) return;
        if (Position <= b.Start + 100) { ShowToast(T("The area has to end after it starts.")); return; }
        b.End = Position;
    }

    [RelayCommand]
    private void BlurWholeVideo(BlurViewModel? b)
    {
        if (b is null) return;
        b.Start = 0;
        b.End = Duration;
    }

    private void ClearFrameTools()
    {
        foreach (var b in Blurs) b.Changed -= EffectsChanged;
        Blurs.Clear();
        SelectedBlur = null;
        ClearZoom();
    }
}
