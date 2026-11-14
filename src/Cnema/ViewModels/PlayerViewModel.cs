using Avalonia.Threading;
using Cnema.Controls;
using Cnema.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LibVLCSharp.Shared;

namespace Cnema.ViewModels;

public sealed partial class PlayerViewModel : ObservableObject, IDisposable
{
    private readonly MediaPlayer? _player;
    private readonly SettingsStore _settings;
    private CancellationTokenSource? _exportCts;
    private bool _stopAtOut;
    private Task _pendingStop = Task.CompletedTask;

    public VideoFrameBuffer? Frames { get; }
    public static double[] Rates { get; } = { 0.25, 0.5, 0.75, 1.0, 1.25, 1.5, 2.0 };

    /// <summary>Set by the view: (suggested file name, start folder) -> chosen path or null.</summary>
    public Func<string, string, Task<string?>>? PickSavePath { get; set; }

    /// <summary>Raised after a successful export with the new file's path.</summary>
    public event Action<string>? Exported;

    [ObservableProperty] private VideoItemViewModel? _current;
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private double _duration;
    [ObservableProperty] private double _position;
    [ObservableProperty] private int _volume;
    [ObservableProperty] private bool _isMuted;
    [ObservableProperty] private double _rate = 1.0;
    [ObservableProperty] private bool _loopRange;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasIn), nameof(InText), nameof(HasRange), nameof(RangeText))]
    private double _inPoint = -1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOut), nameof(OutText), nameof(HasRange), nameof(RangeText))]
    private double _outPoint = -1;

    [ObservableProperty] private bool _preciseExport;
    [ObservableProperty] private bool _isExporting;
    [ObservableProperty] private double _exportProgress;
    [ObservableProperty] private string? _toast;

    public PlayerViewModel(SettingsStore settings)
    {
        _settings = settings;
        _volume = settings.Current.Volume;
        _preciseExport = settings.Current.PreciseExport;
        if (VlcBootstrap.LibVLC is not { } lib) return;

        _player = new MediaPlayer(lib) { EnableHardwareDecoding = true };
        Frames = new VideoFrameBuffer(_player);

        // libVLC raises events on its own threads, and calling back into libVLC from inside an
        // event handler can deadlock, so everything is marshalled to the UI thread.
        _player.TimeChanged += (_, e) => Ui(() => OnTime(e.Time));
        _player.LengthChanged += (_, e) => Ui(() => Duration = e.Length);
        _player.Playing += (_, _) => Ui(() => { IsPlaying = true; ApplyAudio(); });
        _player.Paused += (_, _) => Ui(() => IsPlaying = false);
        _player.Stopped += (_, _) => Ui(() => IsPlaying = false);
        _player.EndReached += (_, _) => Ui(() => { IsPlaying = false; Position = Duration; });
        _player.EncounteredError += (_, _) => Ui(() => ShowToast("Playback error: libVLC could not play this file."));
    }

    private static void Ui(Action a) => Dispatcher.UIThread.Post(a);

    // ---------- derived text ----------
    public bool HasIn => InPoint >= 0;
    public bool HasOut => OutPoint >= 0;
    public bool HasRange => HasIn || HasOut;
    public string InText => HasIn ? FormatPrecise(InPoint) : "--:--";
    public string OutText => HasOut ? FormatPrecise(OutPoint) : "--:--";
    public string RangeText => HasRange ? FormatPrecise(EffectiveOut - EffectiveIn) : "";
    public string PositionText => FormatPrecise(Position);
    public string DurationText => TrimTimeline.FormatTime(Duration);
    public string Title => Current?.Title ?? "";
    public string Subtitle => Current?.Details ?? "";
    private double EffectiveIn => HasIn ? InPoint : 0;
    private double EffectiveOut => HasOut ? OutPoint : Duration;

    partial void OnPositionChanged(double value) => OnPropertyChanged(nameof(PositionText));
    partial void OnDurationChanged(double value) { OnPropertyChanged(nameof(DurationText)); OnPropertyChanged(nameof(RangeText)); }
    partial void OnCurrentChanged(VideoItemViewModel? value) { OnPropertyChanged(nameof(Title)); OnPropertyChanged(nameof(Subtitle)); }
    partial void OnVolumeChanged(int value) { ApplyAudio(); _settings.Current.Volume = value; }
    partial void OnIsMutedChanged(bool value) => ApplyAudio();
    partial void OnRateChanged(double value) => _player?.SetRate((float)value);
    partial void OnPreciseExportChanged(bool value) { _settings.Current.PreciseExport = value; _settings.Save(); }

    private void ApplyAudio()
    {
        if (_player is null) return;
        _player.Volume = Volume;
        _player.Mute = IsMuted;
    }

    private void OnTime(long t)
    {
        Position = t;
        if (_stopAtOut && HasOut && t >= OutPoint)
        {
            if (LoopRange) Seek(EffectiveIn);
            else { _stopAtOut = false; _player?.SetPause(true); }
        }
    }

    // ---------- lifecycle ----------
    public async Task OpenAsync(VideoItemViewModel item)
    {
        if (_player is null || VlcBootstrap.LibVLC is not { } lib)
        {
            ShowToast(VlcBootstrap.Error ?? "libVLC is not available.");
            return;
        }
        await _pendingStop;
        Frames?.Reset();
        Current = item;
        InPoint = OutPoint = -1;
        Position = 0;
        Duration = item.Entry.DurationMs;
        Rate = 1.0;
        _stopAtOut = false;

        using var media = new Media(lib, item.Entry.Path, FromType.FromPath);
        var resume = item.Entry.LastPositionMs;
        if (item.HasProgress && resume > 5000)
        {
            media.AddOption($":start-time={resume / 1000.0:0.###}".Replace(',', '.'));
            ShowToast($"Resumed at {TrimTimeline.FormatTime(resume)}");
        }
        _player.Play(media);
    }

    public void Close()
    {
        if (Current is { } cur && _player is not null)
        {
            var pos = _player.Time;
            var dur = Math.Max(Duration, 1);
            cur.Entry.LastPositionMs = pos / dur > 0.97 ? 0 : Math.Max(0, pos);
            cur.Entry.LastPlayedUtc = DateTime.UtcNow;
            cur.RefreshProgress();
        }
        _exportCts?.Cancel();
        // Stop() blocks until the decoder threads exit; keep that off the UI thread.
        var p = _player;
        if (p is not null) _pendingStop = Task.Run(() => p.Stop());
        IsPlaying = false;
        _settings.Save();
    }

    // ---------- transport ----------
    [RelayCommand]
    private void TogglePlay()
    {
        if (_player is null) return;
        if (_player.State is VLCState.Ended or VLCState.Stopped)
        {
            _player.Stop();
            _player.Play();
            if (HasIn) _player.Time = (long)InPoint;
            return;
        }
        _player.SetPause(_player.IsPlaying);
    }

    [RelayCommand]
    private void Seek(double ms)
    {
        if (_player is null) return;
        ms = Math.Clamp(ms, 0, Math.Max(0, Duration - 50));
        if (_player.State == VLCState.Ended) { _player.Stop(); _player.Play(); }
        _player.Time = (long)ms;
        Position = ms;
    }

    [RelayCommand] private void SkipBack() => Seek(Position - 5000);
    [RelayCommand] private void SkipForward() => Seek(Position + 5000);

    [RelayCommand]
    private void StepFrame() => _player?.NextFrame();

    [RelayCommand]
    private void StepFrameBack()
    {
        var fps = Current?.Entry.FrameRate is > 1 and var f ? f : 30;
        _player?.SetPause(true);
        Seek(Position - 1000.0 / fps);
    }

    [RelayCommand] private void ToggleMute() => IsMuted = !IsMuted;

    [RelayCommand]
    private void CycleRate()
    {
        var i = Array.IndexOf(Rates, Rate);
        Rate = Rates[(i + 1) % Rates.Length];
        ShowToast($"Speed {Rate:0.##}×");
    }

    // ---------- trimming ----------
    [RelayCommand]
    private void SetIn()
    {
        InPoint = Position;
        if (HasOut && OutPoint <= InPoint) OutPoint = -1;
    }

    [RelayCommand]
    private void SetOut()
    {
        OutPoint = Position;
        if (HasIn && InPoint >= OutPoint) InPoint = -1;
    }

    [RelayCommand]
    private void ClearRange()
    {
        InPoint = OutPoint = -1;
        _stopAtOut = false;
    }

    [RelayCommand]
    private void JumpToIn() { if (HasIn) Seek(InPoint); }

    [RelayCommand]
    private void JumpToOut() { if (HasOut) Seek(OutPoint); }

    [RelayCommand]
    private void PreviewRange()
    {
        if (_player is null || !HasRange) return;
        Seek(EffectiveIn);
        _stopAtOut = true;
        if (!_player.IsPlaying) _player.SetPause(false);
    }

    [RelayCommand]
    private async Task ExportAsync()
    {
        if (Current is null || IsExporting) return;
        var start = (long)EffectiveIn;
        var end = (long)EffectiveOut;
        if (end - start < 100) { ShowToast("Set an in and out point first (I / O)."); return; }
        if (ToolLocator.Ffmpeg is null)
        {
            ShowToast(OperatingSystem.IsMacOS() ? "ffmpeg not found. Install with: brew install ffmpeg"
                                                : "ffmpeg not found. Install it and make sure it is on PATH.");
            return;
        }

        var mode = PreciseExport ? TrimMode.Precise : TrimMode.Lossless;
        var src = Current.Entry.Path;
        var stamp = $"{TrimTimeline.FormatTime(start)}-{TrimTimeline.FormatTime(end)}".Replace(':', '.');
        var suggested = $"{Current.Title} [{stamp}]{TrimService.SuggestedExtension(src, mode)}";
        var dir = Path.GetDirectoryName(src) ?? "";

        var target = PickSavePath is null ? Path.Combine(dir, suggested) : await PickSavePath(suggested, dir);
        if (string.IsNullOrEmpty(target)) return;
        if (string.Equals(Path.GetFullPath(target), Path.GetFullPath(src), StringComparison.OrdinalIgnoreCase))
        {
            ShowToast("Pick a different file name; the source can't be overwritten.");
            return;
        }

        _exportCts = new CancellationTokenSource();
        IsExporting = true;
        ExportProgress = 0;
        try
        {
            var progress = new Progress<double>(p => ExportProgress = p * 100);
            await TrimService.ExportAsync(src, target, start, end, mode, progress, _exportCts.Token);
            ShowToast($"Exported {Path.GetFileName(target)}");
            Exported?.Invoke(target);
        }
        catch (OperationCanceledException) { ShowToast("Export cancelled."); }
        catch (Exception ex) { ShowToast("Export failed: " + FirstLine(ex.Message)); }
        finally
        {
            IsExporting = false;
            _exportCts.Dispose();
            _exportCts = null;
        }
    }

    /// <summary>Share presets: re-encode the selection (or the whole video) for a place with limits.</summary>
    [RelayCommand]
    private async Task ShareAsync(string? presetName)
    {
        if (Current is null || IsExporting || !Enum.TryParse<SharePreset>(presetName, out var preset)) return;
        if (ToolLocator.Ffmpeg is null) { ShowToast("ffmpeg not found, so there is nothing to convert with."); return; }
        var start = (long)EffectiveIn;
        var end = (long)EffectiveOut;
        if (end - start < 100) { start = 0; end = (long)Duration; }
        if (end - start < 100) return;

        var (suffix, ext, label) = TrimService.Describe(preset);
        var src = Current.Entry.Path;
        var dir = Path.GetDirectoryName(src) ?? "";
        var target = Path.Combine(dir, $"{Current.Title} [{suffix}]{ext}");
        for (var i = 2; File.Exists(target); i++) target = Path.Combine(dir, $"{Current.Title} [{suffix} {i}]{ext}");

        _exportCts = new CancellationTokenSource();
        IsExporting = true;
        ExportProgress = 0;
        try
        {
            var progress = new Progress<double>(p => ExportProgress = p * 100);
            await TrimService.ShareAsync(src, target, start, end, preset, progress, _exportCts.Token);
            var size = VideoItemViewModel.FormatSize(new FileInfo(target).Length);
            var cut = preset == SharePreset.Gif && end - start > TrimService.GifMaxMs ? ", first 20 s" : "";
            ShowToast($"{label} ready: {size}{cut}");
            Exported?.Invoke(target);
            PlatformShell.Reveal(target);
        }
        catch (OperationCanceledException) { ShowToast("Export cancelled."); }
        catch (Exception ex) { ShowToast("Export failed: " + FirstLine(ex.Message)); }
        finally
        {
            IsExporting = false;
            _exportCts.Dispose();
            _exportCts = null;
        }
    }

    [RelayCommand] private void CancelExport() => _exportCts?.Cancel();

    // ---------- helpers ----------
    private CancellationTokenSource? _toastCts;

    public async void ShowToast(string message)
    {
        _toastCts?.Cancel();
        var cts = _toastCts = new CancellationTokenSource();
        Toast = message;
        try { await Task.Delay(3500, cts.Token); Toast = null; } catch (OperationCanceledException) { }
    }

    private static string FirstLine(string s) => s.Split('\n', 2)[0].Trim();

    public static string FormatPrecise(double ms)
    {
        if (ms < 0) ms = 0;
        var t = TimeSpan.FromMilliseconds(ms);
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss\.f") : t.ToString(@"m\:ss\.f");
    }

    public void Dispose()
    {
        _exportCts?.Cancel();
        _player?.Stop();
        _player?.Dispose();
        Frames?.Dispose();
    }
}
