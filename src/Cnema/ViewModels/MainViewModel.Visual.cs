using Avalonia.Threading;
using Cnema.Models;
using Cnema.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cnema.ViewModels;

// Visual search, similar videos and look-alike copies: CLIP (an optional download) looks at a few frames of each video.
public sealed partial class MainViewModel
{
    private CancellationTokenSource? _visualCts;
    private Task? _visualRun;
    private CancellationTokenSource? _visualModelCts;
    private VisualService? _visual;
    private readonly Dictionary<string, float[]> _queryVectors = new(StringComparer.OrdinalIgnoreCase);
    private string? _embedding;

    public bool ShowVisual => Features.Visual;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(VisualModelText), nameof(CanDownloadVisual))] private bool _visualModelReady = ModelStore.IsInstalled(ModelStore.Visual);
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(VisualModelText), nameof(CanDownloadVisual))] private bool _isDownloadingVisual;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(VisualModelText))] private double _visualDownloadProgress;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(VisualModelText))] private string? _visualModelError;
    [ObservableProperty] private string _visualActivity = "";

    public bool CanDownloadVisual => !VisualModelReady && !IsDownloadingVisual;

    public string VisualModelText =>
        IsDownloadingVisual ? F("Downloading the visual model: {0:0}%", VisualDownloadProgress * 100)
        : VisualModelError is { } err ? err
        : VisualModelReady ? F("Visual model installed ({0}). Looked at {1} of {2} videos.", BackupViewModel.SizeText(ModelStore.BytesOf(ModelStore.Visual)),
            _items.Values.Count(v => v.Entry.Visual is { Version: VisualService.Version }).ToString("N0"), _items.Count.ToString("N0")) + FailedText("Visual")
        : F("Needs a one-time download of a visual model ({0}). After that, nothing leaves this computer.", BackupViewModel.SizeText(ModelStore.BytesOf(ModelStore.Visual)));

    [RelayCommand]
    private async Task DownloadVisualModelAsync()
    {
        if (IsDownloadingVisual) return;
        _visualModelCts = new CancellationTokenSource();
        IsDownloadingVisual = true;
        VisualModelError = null;
        VisualDownloadProgress = 0;
        try
        {
            var progress = new Progress<double>(p => VisualDownloadProgress = p);
            await Task.Run(() => ModelStore.DownloadAsync(ModelStore.Visual, progress, _visualModelCts.Token));
            VisualModelReady = true;
            StartVisualPass();
        }
        catch (OperationCanceledException) { /* cancelled */ }
        catch (Exception ex) { VisualModelError = ex.Message; }
        finally
        {
            IsDownloadingVisual = false;
            _visualModelCts.Dispose();
            _visualModelCts = null;
        }
    }

    [RelayCommand]
    private void CancelVisualDownload() => _visualModelCts?.Cancel();

    [RelayCommand]
    private void RemoveVisualModel()
    {
        StopVisualPass();
        _visual?.Dispose();
        _visual = null;
        _queryVectors.Clear();
        ModelStore.Remove(ModelStore.Visual);
        VisualModelReady = ModelStore.IsInstalled(ModelStore.Visual);
        VisualModelError = null;
    }

    /// <summary>The model, loaded on first use and kept (the text side answers searches as you type).</summary>
    private VisualService? VisualModel()
    {
        lock (_visualLoad)
        {
            if (_visual is not null || !Features.Visual || !VisualModelReady) return _visual;
            try { _visual = VisualService.Load(); }
            catch (Exception ex)
            {
                var message = ex.Message;
                Dispatcher.UIThread.Post(() => VisualModelError = message);
            }
            return _visual;
        }
    }

    private readonly object _visualLoad = new();

    // ---------- looking, in the background ----------

    private void StartVisualPass()
    {
        if (!Features.Visual || !VisualModelReady || ToolLocator.Ffmpeg is null) return;
        if (_visualRun is { IsCompleted: false }) return;
        _visualCts = new CancellationTokenSource();
        _visualRun = RunVisualPassAsync(_visualCts.Token);
    }

    private void StopVisualPass()
    {
        _visualCts?.Cancel();
        VisualActivity = "";
    }


    private async Task RunVisualPassAsync(CancellationToken ct)
    {
        var done = 0;
        try
        {
            var tried = new HashSet<VideoItemViewModel>();
            var pace = new PassLog("Visual");
            using var awake = PlatformShell.StayAwake("Visual");
            Log.Info($"Visual pass: {_items.Values.Count(v => v.Entry.Visual is not { Version: VisualService.Version })} to do");
            while (!ct.IsCancellationRequested && Features.Visual)
            {
                var todo = _items.Values.Where(v => v.Entry.Visual is not { Version: VisualService.Version } && !tried.Contains(v))
                    .OrderByDescending(v => v.Entry.ModifiedUtc).ToList();
                if (todo.Count == 0) break;
                for (var i = 0; i < todo.Count && !ct.IsCancellationRequested && Features.Visual; i++)
                {
                    var item = todo[i];
                    tried.Add(item);
                    if (item.Entry.Visual is { Version: VisualService.Version } || !File.Exists(item.Entry.Path)) continue;
                    var working = F("Looking at your videos: {0} to go, {1}.", (todo.Count - i).ToString("N0"), CostMeter.Span(LeftSeconds(CostMeter.Visual, todo.Skip(i))));
                    if (_visual is null) VisualActivity = T("Starting the visual model…");
                    var model = _visual ?? await LoadModelAsync("Visual", VisualModel, ct);
                    if (model is null) return;
                    var waited = await TakeTurnAsync(s => VisualActivity = s, working, "Visual", ct);
                    using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    BackgroundHold.CancelAfterActive(limit, ItemLimit(item.Entry.DurationMs));
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    VisualInfo? info;
                    try
                    {
                        var path = item.Entry.Path;
                        var length = item.Entry.DurationMs;
                        info = await Task.Run(() => model.LookAsync(path, length, limit.Token), limit.Token);
                        CostMeter.Record(CostMeter.Visual, watch.Elapsed.TotalSeconds, length);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        Log.Warn($"Visual for {item.Entry.FileName}: {SkipReason(ex, item.Entry.DurationMs)}");
                        CountFailure("Visual");
                        info = null;
                    }
                    finally { _workGate.Release(); pace.Add(item.Entry.FileName, waited, watch.Elapsed); }
                    if (_items.TryGetValue(item.Entry.Path, out var still) && ReferenceEquals(still, item))
                        item.Entry.Visual = info ?? new VisualInfo { Version = VisualService.Version };
                    if (++done % 10 == 0) { _library.SaveCache(); OnPropertyChanged(nameof(VisualModelText)); OnCostsChanged(); }
                    await Task.Delay(100, ct);
                }
            }
        }
        catch (OperationCanceledException) { /* stopped */ }
        catch (Exception ex)
        {
            Log.Warn($"Visual pass stopped: {ex.Message}");
            VisualModelError = ex.Message;
        }
        finally
        {
            Log.Info($"Visual pass ended: {done} done");
            VisualActivity = "";
            OnCostsChanged();
            if (done > 0)
            {
                _library.SaveCache();
                OnPropertyChanged(nameof(VisualModelText));
                if (SearchText.Trim().Length > 0) RefreshView();
            }
        }
    }

    // ---------- search by what's on screen ----------

    /// <summary>How much better than plain "a screenshot" a frame has to match the words to count.</summary>
    private const float SeenMargin = 0.032f;

    /// <summary>
    /// Videos whose frames clearly look like the search words, best first, with where. Empty until the
    /// words have been turned into a vector (that happens in the background, then the view refreshes).
    /// </summary>
    private List<(VideoItemViewModel Item, float Score, long AtMs)> SeenMatches(IEnumerable<VideoItemViewModel> pool, string search)
    {
        var result = new List<(VideoItemViewModel, float, long)>();
        if (!Features.Visual || !VisualModelReady) return result;
        var words = string.Join(' ', search.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(t => !t.StartsWith('#') && !t.StartsWith('@')));
        if (words.Length < 3) return result;
        if (!_queryVectors.TryGetValue(words, out var q))
        {
            EmbedQueryLater(words);
            return result;
        }
        var neutral = _visual?.Neutral;
        if (neutral is null) return result;
        foreach (var v in pool)
        {
            if (v.Entry.Visual is not { Version: VisualService.Version } vis || vis.Frames.Length == 0) continue;
            float best = float.MinValue;
            long at = 0;
            for (var i = 0; i < vis.Frames.Length; i++)
            {
                var f = vis.Frames[i];
                var margin = VisualInfo.Dot(f, q) - neutral.Average(n => VisualInfo.Dot(f, n));
                if (margin > best) { best = margin; at = i < vis.At.Length ? vis.At[i] : 0; }
            }
            if (best >= SeenMargin) result.Add((v, best, at));
        }
        result.Sort((a, b) => b.Item2.CompareTo(a.Item2));
        return result;
    }

    private void EmbedQueryLater(string words)
    {
        if (_embedding == words) return;
        _embedding = words;
        _ = Task.Run(() =>
        {
            try
            {
                var model = VisualModel();
                if (model is null) return;
                _ = model.Neutral;
                var vector = model.EmbedText(words);
                Dispatcher.UIThread.Post(() =>
                {
                    if (_queryVectors.Count > 200) _queryVectors.Clear();
                    _queryVectors[words] = vector;
                    if (_embedding == words) _embedding = null;
                    RefreshView();
                });
            }
            catch (Exception ex)
            {
                Log.Warn($"Visual search for \"{words}\": {ex.Message}");
                Dispatcher.UIThread.Post(() => { if (_embedding == words) _embedding = null; });
            }
        });
    }

    private void MarkSeen(List<(VideoItemViewModel Item, float Score, long AtMs)> seen)
    {
        var hits = seen.ToDictionary(s => s.Item, s => s.AtMs);
        foreach (var v in _items.Values)
        {
            var hit = hits.TryGetValue(v, out var at);
            if (v.HasSeen != hit) v.HasSeen = hit;
            v.SeenAtMs = hit ? at : -1;
        }
    }

    // ---------- similar videos ----------

    /// <summary>"Videos like this one": the view shows the videos that look most alike.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsShowingSimilar), nameof(HeaderTitle))] private VideoItemViewModel? _similarTo;
    public bool IsShowingSimilar => SimilarTo is not null;

    /// <summary>How alike two whole videos have to look to count as similar.</summary>
    private const float SimilarMin = 0.82f;

    public bool CanFindSimilar(VideoItemViewModel? item) =>
        Features.Visual && item?.Entry.Visual is { Version: VisualService.Version } v && v.Frames.Length > 0;

    [RelayCommand]
    private void FindSimilar(VideoItemViewModel? item)
    {
        if (item is null) return;
        if (!CanFindSimilar(item))
        {
            Player.ShowToast(T("Savefile hasn't looked at this video yet. Try again in a little while."));
            return;
        }
        SearchText = "";
        SimilarTo = item;
        RefreshView();
    }

    partial void OnSimilarToChanged(VideoItemViewModel? value) => OnPropertyChanged(nameof(HeaderCount));

    [RelayCommand]
    private void ClearSimilar()
    {
        SimilarTo = null;
        RefreshView();
    }

    private List<VideoItemViewModel> SimilarVideos(VideoItemViewModel to)
    {
        if (to.Entry.Visual is not { } mine) return new();
        return _items.Values
            .Where(v => !ReferenceEquals(v, to) && v.Entry.Visual is { Version: VisualService.Version, Frames.Length: > 0 })
            .Select(v => (v, s: VisualInfo.Dot(mine.Mean, v.Entry.Visual!.Mean)))
            .Where(x => x.s >= SimilarMin)
            .OrderByDescending(x => x.s)
            .Take(60)
            .Select(x => x.v)
            .Prepend(to)
            .ToList();
    }

    // ---------- look-alike copies (Clean up) ----------

    /// <summary>
    /// The same video saved twice in different files (re-encoded, exported, trimmed by a frame): about the
    /// same length and every frame alike.
    /// </summary>
    internal bool LooksSame(VideoEntry a, VideoEntry b)
    {
        if (!Features.Visual) return false;
        if (a.Visual is not { Version: VisualService.Version } va || b.Visual is not { Version: VisualService.Version } vb) return false;
        if (va.Frames.Length == 0 || va.Frames.Length != vb.Frames.Length) return false;
        if (Math.Abs(a.DurationMs - b.DurationMs) > Math.Max(1500, a.DurationMs / 50)) return false;
        for (var i = 0; i < va.Frames.Length; i++)
            if (VisualInfo.Dot(va.Frames[i], vb.Frames[i]) < 0.95f) return false;
        return true;
    }
}
