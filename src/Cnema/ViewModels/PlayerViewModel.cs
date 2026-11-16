using Avalonia.Threading;
using Cnema.Controls;
using Cnema.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LibVLCSharp.Shared;

namespace Cnema.ViewModels;

/// <summary>One part of a multi-segment export, shown as a chip under the timeline.</summary>
public sealed record SegmentViewModel(double Start, double End)
{
    public string Text => $"{TrimTimeline.FormatTime(Start)} – {TrimTimeline.FormatTime(End)}";
}

/// <summary>One audio track of the open video; unchecked tracks are left out of exports.</summary>
public sealed partial class AudioTrackViewModel : ObservableObject
{
    public int Index { get; }
    public int VlcId { get; }
    public string Label { get; }
    [ObservableProperty] private bool _enabled = true;
    public AudioTrackViewModel(int index, int vlcId, string label) => (Index, VlcId, Label) = (index, vlcId, label);
}

public sealed partial class PlayerViewModel : ObservableObject, IDisposable
{
    /// <summary>Re-reads every text property after a language change.</summary>
    public void RefreshTexts()
    {
        OnPropertyChanged(string.Empty);
    }

    private readonly MediaPlayer? _player;
    private readonly SettingsStore _settings;
    private CancellationTokenSource? _exportCts;
    private bool _stopAtOut;
    private Task _pendingStop = Task.CompletedTask;

    public VideoFrameBuffer? Frames { get; }
    public static double[] Rates { get; } = { 0.25, 0.5, 0.75, 1.0, 1.25, 1.5, 1.75, 2.0, 3.0, 4.0 };

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
    [NotifyPropertyChangedFor(nameof(HasIn), nameof(InText), nameof(HasRange), nameof(RangeText), nameof(ExportLabel))]
    private double _inPoint = -1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOut), nameof(OutText), nameof(HasRange), nameof(RangeText), nameof(ExportLabel))]
    private double _outPoint = -1;

    [ObservableProperty] private bool _preciseExport;
    [ObservableProperty] private bool _isExporting;
    [ObservableProperty] private double _exportProgress;
    [ObservableProperty] private string? _toast;

    // ---------- multi-segment export, audio tracks, markers ----------
    public System.Collections.ObjectModel.ObservableCollection<SegmentViewModel> Segments { get; } = new();
    public System.Collections.ObjectModel.ObservableCollection<AudioTrackViewModel> AudioTracks { get; } = new();
    [ObservableProperty] private IReadOnlyList<TimeRange> _segmentRanges = Array.Empty<TimeRange>();
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(MarkerText))] private IReadOnlyList<TimelineMarker> _markers = Array.Empty<TimelineMarker>();
    public bool HasSegments => Segments.Count > 0;
    /// <summary>Work that opening another video would throw away: trim marks, parts, captions or speed changes.</summary>
    public bool HasEdits => HasIn || HasOut || HasSegments || Captions.Count > 0 || SpeedSections.Count > 0 ||
                            Blurs.Count > 0 || ZoomKeys.Count > 0 || HasMusic;
    public bool HasAudioTracks => AudioTracks.Count > 1;
    public bool HasMarkers => Markers.Count > 0;
    public string ExportLabel => HasSegments ? P(Segments.Count + (HasRange ? 1 : 0), "Export {0} part", "Export {0} parts") : T("Export clip");
    private CancellationTokenSource? _highlightCts;

    private void SegmentsChanged()
    {
        SegmentRanges = Segments.Select(s => new TimeRange(s.Start, s.End)).ToList();
        OnPropertyChanged(nameof(HasSegments));
        OnPropertyChanged(nameof(ExportLabel));
    }

    /// <summary>Keeps the current in/out selection as one part and clears it for the next.</summary>
    [RelayCommand]
    private void AddSegment()
    {
        if (!HasRange || EffectiveOut - EffectiveIn < 100)
        {
            ShowToast(T("Set an in and out point first (I / O), then add it as a part."));
            return;
        }
        Segments.Add(new SegmentViewModel(EffectiveIn, EffectiveOut));
        InPoint = OutPoint = -1;
        _stopAtOut = false;
        SegmentsChanged();
        ShowToast(F("Part {0} added. Mark the next one, or export.", Segments.Count));
    }

    [RelayCommand]
    private void RemoveSegment(SegmentViewModel? segment)
    {
        if (segment is not null && Segments.Remove(segment)) SegmentsChanged();
    }

    [RelayCommand]
    private void JumpToSegment(SegmentViewModel? segment)
    {
        if (segment is not null) Seek(segment.Start);
    }

    /// <summary>Jumps to the next (or previous) bookmark or loud moment.</summary>
    [RelayCommand]
    private void NextMarker() => JumpMarker(forward: true);
    [RelayCommand]
    private void PreviousMarker() => JumpMarker(forward: false);

    private void JumpMarker(bool forward)
    {
        if (Markers.Count == 0) { ShowToast(T("No markers in this video.")); return; }
        var target = forward
            ? Markers.Where(m => m.Ms > Position + 500).Select(m => (double?)m.Ms).FirstOrDefault()
            : Markers.Where(m => m.Ms < Position - 1500).Select(m => (double?)m.Ms).LastOrDefault();
        if (target is { } ms) Seek(ms);
    }

    /// <summary>The audio streams to keep in an export; null means "all of them, untouched".</summary>
    private IReadOnlyList<int>? AudioSelection =>
        AudioTracks.Count < 2 || AudioTracks.All(t => t.Enabled)
            ? (AudioTracks.Count >= 2 ? AudioTracks.Select(t => t.Index).ToList() : null)
            : AudioTracks.Where(t => t.Enabled).Select(t => t.Index).ToList();

    private void LoadAudioTracks()
    {
        if (_player is null || AudioTracks.Count > 0) return;
        var real = _player.AudioTrackDescription.Where(d => d.Id >= 0).ToList();
        if (real.Count < 2) return;
        for (var i = 0; i < real.Count; i++)
        {
            var track = new AudioTrackViewModel(i, real[i].Id, F("Audio {0}", i + 1));
            track.PropertyChanged += (_, _) => ApplyAudioTrack();
            AudioTracks.Add(track);
        }
        OnPropertyChanged(nameof(HasAudioTracks));
        RefreshToolPages();
        BuildVoiceTrackChoice();
    }

    /// <summary>libVLC plays one track at a time: the first one that is switched on.</summary>
    private void ApplyAudioTrack()
    {
        if (_player is null || AudioTracks.Count < 2) return;
        var first = AudioTracks.FirstOrDefault(t => t.Enabled);
        _player.SetAudioTrack(first?.VlcId ?? -1);
    }

    private async void LoadMarkers(VideoItemViewModel item)
    {
        _highlightCts?.Cancel();
        var cts = _highlightCts = new CancellationTokenSource();
        var bookmarks = item.Entry.Bookmarks.Select(b => new TimelineMarker(b, true)).ToList();
        Markers = bookmarks;
        OnPropertyChanged(nameof(HasMarkers));
        if (!_settings.Current.Features.LoudMarkers) return;
        try
        {
            var loud = await HighlightService.DetectAsync(item.Entry.Path, cts.Token);
            if (cts.IsCancellationRequested || !ReferenceEquals(Current, item)) return;
            // A loud moment right next to a bookmark adds nothing.
            var extra = loud.Where(l => !bookmarks.Any(b => Math.Abs(b.Ms - l) < 3000)).Select(l => new TimelineMarker(l, false));
            Markers = bookmarks.Concat(extra).OrderBy(m => m.Ms).ToList();
            OnPropertyChanged(nameof(HasMarkers));
        }
        catch { /* markers are a nicety */ }
    }

    public PlayerViewModel(SettingsStore settings)
    {
        _settings = settings;
        _volume = settings.Current.Volume;
        _preciseExport = settings.Current.PreciseExport;
        InitEffects();
        InitFrameTools();
        InitMusic();
        InitTools();
        if (VlcBootstrap.LibVLC is not { } lib) return;

        _player = new MediaPlayer(lib) { EnableHardwareDecoding = true };
        Frames = new VideoFrameBuffer(_player);

        // libVLC raises events on its own threads, and calling back into libVLC from inside an
        // event handler can deadlock, so everything is marshalled to the UI thread.
        _player.TimeChanged += (_, e) => Ui(() => OnTime(e.Time));
        _player.LengthChanged += (_, e) => Ui(() => Duration = e.Length);
        _player.Playing += (_, _) => Ui(() => { IsPlaying = true; ApplyAudio(); LoadAudioTracks(); });
        _player.Paused += (_, _) => Ui(() => IsPlaying = false);
        _player.Stopped += (_, _) => Ui(() => IsPlaying = false);
        _player.EndReached += (_, _) => Ui(OnEndReached);
        _player.EncounteredError += (_, _) => Ui(() => ShowToast(T("Playback error: libVLC could not play this file.")));
    }

    private static void Ui(Action a) => Dispatcher.UIThread.Post(a);

    // ---------- derived text ----------
    public bool HasIn => InPoint >= 0;
    public bool HasOut => OutPoint >= 0;
    public bool HasRange => HasIn || HasOut;
    public string InText => HasIn ? FormatPrecise(InPoint) : "--:--";
    public string OutText => HasOut ? FormatPrecise(OutPoint) : "--:--";
    public string RangeText => HasRange ? FormatPrecise(EffectiveOut - EffectiveIn) : "";
    public string PositionText => ShowFrames ? FormatFrames(Position) : FormatPrecise(Position);
    public string DurationText => TrimTimeline.FormatTime(Duration);
    public string Title => Current?.Title ?? "";
    public string Subtitle => Current?.Details ?? "";
    private double EffectiveIn => HasIn ? InPoint : 0;
    private double EffectiveOut => HasOut ? OutPoint : Duration;

    partial void OnPositionChanged(double value)
    {
        OnPropertyChanged(nameof(PositionText));
        UpdateCurrentChapter();
        UpdateEffectsPreview();
    }
    partial void OnDurationChanged(double value) { OnPropertyChanged(nameof(DurationText)); OnPropertyChanged(nameof(RangeText)); BuildChapters(); }
    partial void OnCurrentChanged(VideoItemViewModel? value)
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(VideoWidth));
        OnPropertyChanged(nameof(VideoHeight));
    }
    partial void OnVolumeChanged(int value) { ApplyAudio(); _settings.Current.Volume = value; }
    partial void OnIsMutedChanged(bool value) => ApplyAudio();
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
        if (LoopRange && HasRange && LoopTick(t)) return;
        if (_stopAtOut && HasOut && t >= OutPoint)
        {
            _stopAtOut = false;
            _player?.SetPause(true);
        }
    }

    // ---------- lifecycle ----------
    public async Task OpenAsync(VideoItemViewModel item)
    {
        if (_player is null || VlcBootstrap.LibVLC is not { } lib)
        {
            ShowToast(VlcBootstrap.Error ?? T("libVLC is not available."));
            return;
        }
        HideToast();
        // Switching videos in the quick player or with ← / →: remember where the last one was left.
        if (_active && !_openInFlight && Current is { } previous && !ReferenceEquals(previous, item))
        {
            SaveProgress(previous);
            RememberEdits(previous);
        }
        // Stop the previous video before loading the next one (← / → or a new pick in the quick player).
        // Swapping media on a playing player leaves the old decoder and video output to wind down
        // while the new file starts; a clean stop, off the UI thread, gives the new file a fresh start.
        if (_active && _player.State is not (VLCState.Stopped or VLCState.NothingSpecial))
        {
            var p = _player;
            _pendingStop = Task.Run(() => p.Stop());
        }
        _active = true;
        // A close, or another open, while the old video is still stopping wins over this one.
        var gen = ++_openGen;
        _openInFlight = true;
        try { await _pendingStop; }
        finally { if (gen == _openGen) _openInFlight = false; }
        if (gen != _openGen) return;
        Frames?.Reset();
        Current = item;
        InPoint = OutPoint = -1;
        Position = 0;
        Duration = item.Entry.DurationMs;
        Rate = 1.0;
        LoopRange = false;
        _stopAtOut = false;
        Segments.Clear();
        SegmentsChanged();
        AudioTracks.Clear();
        OnPropertyChanged(nameof(HasAudioTracks));
        RefreshToolPages();
        LoadMarkers(item);
        BuildChapters();
        ResetEffects();
        var restored = RestoreEdits(item);

        using var media = new Media(lib, item.Entry.Path, FromType.FromPath);
        var resume = item.Entry.LastPositionMs;
        if (item.HasProgress && resume > 5000)
        {
            media.AddOption($":start-time={resume / 1000.0:0.###}".Replace(',', '.'));
            ShowToast(F("Resumed at {0}", TrimTimeline.FormatTime(resume)));
        }
        if (restored) ShowToast(T("Your edits from last time are back. Start over clears them."));
        _player.Play(media);
    }

    private bool _active;
    private int _openGen;
    /// <summary>An open is waiting for the previous video to stop; its progress was already saved.</summary>
    private bool _openInFlight;

    private void SaveProgress(VideoItemViewModel item)
    {
        if (_player is null) return;
        var pos = _player.Time;
        var dur = Math.Max(Duration, 1);
        item.Entry.LastPositionMs = pos / dur > 0.97 ? 0 : Math.Max(0, pos);
        item.Entry.LastPlayedUtc = DateTime.UtcNow;
        item.RefreshAll();
    }

    public void Close()
    {
        _openGen++;
        _highlightCts?.Cancel(); // the loud-moments pass reads the whole file; no need once closed
        var opening = _openInFlight;
        _openInFlight = false;
        // While a switch was stopping the old video, its position was saved already and the
        // half-stopped player would report 0.
        if (!opening && Current is { } cur && _active)
        {
            SaveProgress(cur);
            RememberEdits(cur);
        }
        _active = false;
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
        _inLoop = LoopRange && ms >= EffectiveIn && ms < EffectiveOut;
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

    /// <summary>Set while the export queue is switched on: exports then run in the background, one at a time.</summary>
    public ExportQueueViewModel? Queue { get; set; }

    [RelayCommand]
    private async Task ExportAsync()
    {
        if (Current is null || IsExporting) return;
        var start = (long)EffectiveIn;
        var end = (long)EffectiveOut;
        // Parts added with "Add part", plus the current selection if there is one, in time order.
        var parts = Segments.Select(s => ((long)s.Start, (long)s.End)).ToList();
        if (parts.Count > 0 && HasRange && end - start >= 100) parts.Add((start, end));
        parts.Sort();
        if (parts.Count == 1) { (start, end) = parts[0]; parts.Clear(); }
        if (parts.Count == 0 && end - start < 100) { ShowToast(T("Set an in and out point first (I / O).")); return; }
        if (ToolLocator.Ffmpeg is null)
        {
            ShowToast(OperatingSystem.IsMacOS() ? T("ffmpeg not found. Install with: brew install ffmpeg")
                                                : T("ffmpeg not found. Install it and make sure it is on PATH."));
            return;
        }

        // Effects mean re-encoding, whatever Lossless / Precise says.
        var fx = BuildEffects();
        var withFx = !fx.IsEmpty;
        var mode = PreciseExport || withFx ? TrimMode.Precise : TrimMode.Lossless;
        var src = Current.Entry.Path;
        var stamp = parts.Count > 0
            ? $"{parts.Count} parts"
            : $"{TrimTimeline.FormatTime(start)}-{TrimTimeline.FormatTime(end)}".Replace(':', '.');
        var suggested = $"{Current.Title} [{stamp}]{TrimService.SuggestedExtension(src, mode)}";
        var dir = Path.GetDirectoryName(src) ?? "";

        var target = PickSavePath is null ? Path.Combine(dir, suggested) : await PickSavePath(suggested, dir);
        if (string.IsNullOrEmpty(target)) return;
        if (string.Equals(Path.GetFullPath(target), Path.GetFullPath(src), StringComparison.OrdinalIgnoreCase))
        {
            ShowToast(T("Pick a different file name; the source can't be overwritten."));
            return;
        }

        var audio = AudioSelection;
        Func<IProgress<double>, CancellationToken, Task> work;
        if (withFx)
        {
            var ranges = parts.Count > 0 ? parts : new List<(long, long)> { (start, end) };
            var job = new RenderJob(src, target, ranges, audio, fx, null, SourceChapters());
            work = (progress, ct) => RenderService.RenderAsync(job, progress, ct);
        }
        else if (parts.Count > 0)
            work = (progress, ct) => TrimService.ExportSegmentsAsync(src, target, parts, mode, progress, ct, audio);
        else
        {
            var chapters = ChaptersFor(start, end);
            work = (progress, ct) => TrimService.ExportAsync(src, target, start, end, mode, progress, ct, audio, chapters);
        }
        var detail = (parts.Count > 0 ? F("{0} parts", parts.Count) : T("Clip")) + (withFx ? " · " + P(EffectsCount, "{0} effect", "{0} effects") : "");
        await RunExportAsync(target, detail, work, () => F("Exported {0}", Path.GetFileName(target)), reveal: false);
    }

    /// <summary>Share presets: re-encode the selection (or the whole video) for a place with limits.</summary>
    [RelayCommand]
    private async Task ShareAsync(string? presetName)
    {
        if (Current is null || IsExporting || !Enum.TryParse<SharePreset>(presetName, out var preset)) return;
        if (ToolLocator.Ffmpeg is null) { ShowToast(T("ffmpeg not found, so there is nothing to convert with.")); return; }
        var start = (long)EffectiveIn;
        var end = (long)EffectiveOut;
        if (end - start < 100) { start = 0; end = (long)Duration; }
        if (end - start < 100) return;

        var (suffix, ext, label) = TrimService.Describe(preset);
        label = T(label);
        var src = Current.Entry.Path;
        var dir = Path.GetDirectoryName(src) ?? "";
        var target = Path.Combine(dir, $"{Current.Title} [{suffix}]{ext}");
        for (var i = 2; File.Exists(target) || Queue?.IsTarget(target) == true; i++)
            target = Path.Combine(dir, $"{Current.Title} [{suffix} {i}]{ext}");

        var fx = BuildEffects();
        var audio = AudioSelection;
        Func<IProgress<double>, CancellationToken, Task> work = fx.IsEmpty
            ? (progress, ct) => TrimService.ShareAsync(src, target, start, end, preset, progress, ct, audio)
            : (progress, ct) => RenderService.RenderAsync(
                new RenderJob(src, target, new[] { (start, end) }, audio, fx, preset, null), progress, ct);
        var cut = preset == SharePreset.Gif && end - start > TrimService.GifMaxMs ? T(", first 20 s") : "";
        await RunExportAsync(target, label + (fx.IsEmpty ? "" : " · " + P(EffectsCount, "{0} effect", "{0} effects")), work,
            () => F("{0} ready: {1}", label, VideoItemViewModel.FormatSize(new FileInfo(target).Length)) + cut, reveal: true);
    }

    /// <summary>Runs an export here with a progress bar, or hands it to the export queue.</summary>
    private async Task RunExportAsync(
        string target, string detail, Func<IProgress<double>, CancellationToken, Task> work, Func<string> doneText, bool reveal)
    {
        if (Queue is { } queue)
        {
            queue.Add(target, detail, work, doneText);
            ShowToast(queue.PendingCount > 1
                ? F("Added to the export queue ({0} to go).", queue.PendingCount)
                : T("Exporting in the background. Keep editing if you like."));
            return;
        }

        _exportCts = new CancellationTokenSource();
        IsExporting = true;
        ExportProgress = 0;
        try
        {
            var progress = new Progress<double>(p => ExportProgress = p * 100);
            await work(progress, _exportCts.Token);
            ShowToast(doneText());
            Exported?.Invoke(target);
            if (reveal) PlatformShell.Reveal(target);
        }
        catch (OperationCanceledException) { ShowToast(T("Export cancelled.")); }
        catch (Exception ex) { ShowToast(T("Export failed: ") + FirstLine(ex.Message)); }
        finally
        {
            IsExporting = false;
            _exportCts.Dispose();
            _exportCts = null;
        }
    }

    private IReadOnlyList<Chapter>? SourceChapters() =>
        ChapterList.Count < 2 ? null : ChapterList.Select(c => c.Chapter).ToList();



    [RelayCommand] private void CancelExport() => _exportCts?.Cancel();

    // ---------- helpers ----------
    private CancellationTokenSource? _toastCts;

    public void HideToast()
    {
        _toastCts?.Cancel();
        Toast = null;
    }

    public async void ShowToast(string message)
    {
        _toastCts?.Cancel();
        var cts = _toastCts = new CancellationTokenSource();
        Toast = message;
        try { await Task.Delay(3500, cts.Token); Toast = null; } catch (OperationCanceledException) { }
    }

    internal static string FirstLine(string s) => s.Split('\n', 2)[0].Trim();

    public static string FormatPrecise(double ms)
    {
        if (ms < 0) ms = 0;
        var t = TimeSpan.FromMilliseconds(ms);
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss\.f") : t.ToString(@"m\:ss\.f");
    }

    // ---------- remembered edits (Settings, Features, Remember edits) ----------
    private bool KeepEdits => _settings.Current.Features.KeepEdits;

    /// <summary>Stores the editor state for <paramref name="item"/>, or forgets it when there is none.</summary>
    private void RememberEdits(VideoItemViewModel item)
    {
        if (!KeepEdits) return;
        if (!HasEdits) { EditStore.Remove(item.Entry.Path); return; }
        EditStore.Set(item.Entry.Path, new SavedEdit
        {
            In = InPoint,
            Out = OutPoint,
            Parts = Segments.Select(s => new[] { s.Start, s.End }).ToList(),
            Captions = Captions.Select(c => c.ToSpec()).ToList(),
            Speed = SpeedSections.Select(s => s.ToSpec()).ToList(),
            Blurs = Blurs.Select(b => b.ToSpec()).ToList(),
            Zoom = ZoomKeys.Select(k => k.ToSpec()).ToList(),
            Music = MusicPath,
        });
    }

    private bool RestoreEdits(VideoItemViewModel item)
    {
        if (!KeepEdits || EditStore.Get(item.Entry.Path) is not { } e) return false;
        InPoint = e.In;
        OutPoint = e.Out;
        foreach (var p in e.Parts.Where(p => p.Length == 2)) Segments.Add(new SegmentViewModel(p[0], p[1]));
        SegmentsChanged();
        foreach (var spec in e.Captions)
        {
            var c = new CaptionViewModel { Text = spec.Text, Start = spec.Start, End = spec.End };
            c.Position.Value = spec.Position;
            c.Size.Value = spec.Size;
            c.Look.Value = spec.Style;
            c.Changed += EffectsChanged;
            Captions.Add(c);
        }
        foreach (var spec in e.Speed)
        {
            var s = new SpeedSectionViewModel { Start = spec.Start, End = spec.End, Ramp = spec.Ramp };
            s.Rate.Value = spec.Rate;
            s.Changed += EffectsChanged;
            SpeedSections.Add(s);
        }
        foreach (var spec in e.Blurs)
        {
            var b = new BlurViewModel { Start = spec.Start, End = spec.End, Area = new Avalonia.Rect(spec.X, spec.Y, spec.W, spec.H) };
            b.Look.Value = spec.Style;
            b.Changed += EffectsChanged;
            Blurs.Add(b);
        }
        if (e.Music is { } song && File.Exists(song)) MusicPath = song;
        foreach (var spec in e.Zoom)
            AddKey(new ZoomKeyViewModel { At = spec.At, Zoom = spec.Zoom, X = spec.X, Y = spec.Y });
        EffectsChanged();
        return HasEdits;
    }

    /// <summary>Clears trim marks, parts and effects for this video, including what was remembered.</summary>
    [RelayCommand]
    private void StartOver()
    {
        if (Current is null) return;
        InPoint = OutPoint = -1;
        LoopRange = false;
        Segments.Clear();
        SegmentsChanged();
        ClearEffectsCommand.Execute(null);
        EditStore.Remove(Current.Entry.Path);
        OnPropertyChanged(nameof(HasEdits));
        ShowToast(T("Back to the original: no trim marks, parts or effects."));
    }

    public void Dispose()
    {
        if (Current is { } cur && _active) RememberEdits(cur);
        _exportCts?.Cancel();
        // A stop started by Close() may still be running on another thread.
        try { _pendingStop.Wait(3000); } catch { /* it failed; stop again below */ }
        _player?.Stop();
        _player?.Dispose();
        Frames?.Dispose();
    }
}
