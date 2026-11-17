using Cnema.Models;
using Cnema.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cnema.ViewModels;

// Text on screen: a small text reader (an optional 10 MB download) reads kill feeds, scoreboards and banners
// in a few frames per video. Search finds the words, titles name the banner (Victory Royale, Ace), and a
// clip with no game yet gets one when the game's own words give it away.
public sealed partial class MainViewModel
{
    private CancellationTokenSource? _screenCts;
    private Task? _screenRun;
    private CancellationTokenSource? _screenModelCts;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ScreenModelText), nameof(CanDownloadScreen))] private bool _screenModelReady = ModelStore.IsInstalled(ModelStore.Screen);
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ScreenModelText), nameof(CanDownloadScreen))] private bool _isDownloadingScreen;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ScreenModelText))] private double _screenDownloadProgress;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ScreenModelText))] private string? _screenModelError;
    [ObservableProperty] private string _screenActivity = "";

    public bool CanDownloadScreen => !ScreenModelReady && !IsDownloadingScreen;

    /// <summary>What still has to come down: the reader, plus ONNX Runtime unless another feature brought it.</summary>
    private long ScreenDownloadBytes => ModelStore.Screen.Where(m => !ModelStore.IsInstalled(m)).Sum(m => m.Bytes);

    public string ScreenModelText =>
        IsDownloadingScreen ? F("Downloading the text reader: {0:0}%", ScreenDownloadProgress * 100)
        : ScreenModelError is { } err ? err
        : ScreenModelReady ? F("Text reader installed. Read {0} of {1}; text found in {2}.",
            _items.Values.Count(v => v.Entry.Screen is { Version: ScreenService.Version }).ToString("N0"), _items.Count.ToString("N0"),
            P(_items.Values.Count(v => v.Entry.Screen is { Version: ScreenService.Version, Lines.Count: > 0 }), "{0} video", "{0:N0} videos")) + FailedText("Screen")
        : F("Needs a one-time download of a small text reader ({0}). After that, nothing leaves this computer.", BackupViewModel.SizeText(ScreenDownloadBytes));

    [RelayCommand]
    private async Task DownloadScreenModelAsync()
    {
        if (IsDownloadingScreen) return;
        _screenModelCts = new CancellationTokenSource();
        IsDownloadingScreen = true;
        ScreenModelError = null;
        ScreenDownloadProgress = 0;
        try
        {
            var progress = new Progress<double>(p => ScreenDownloadProgress = p);
            await Task.Run(() => ModelStore.DownloadAsync(ModelStore.Screen, progress, _screenModelCts.Token));
            ScreenModelReady = true;
            StartScreenPass();
        }
        catch (OperationCanceledException) { /* cancelled */ }
        catch (Exception ex) { ScreenModelError = ex.Message; }
        finally
        {
            IsDownloadingScreen = false;
            _screenModelCts.Dispose();
            _screenModelCts = null;
        }
    }

    [RelayCommand] private void CancelScreenDownload() => _screenModelCts?.Cancel();

    [RelayCommand]
    private void RemoveScreenModel()
    {
        StopScreenPass();
        ModelStore.Remove(ModelStore.Screen);
        ScreenModelReady = ModelStore.IsInstalled(ModelStore.Screen);
        ScreenModelError = null;
        // ONNX Runtime goes too when nothing else uses it.
        VisualModelReady = ModelStore.IsInstalled(ModelStore.Visual);
        SoundModelReady = ModelStore.IsInstalled(ModelStore.Sound);
        VoiceModelReady = ModelStore.IsInstalled(ModelStore.Voice);
    }

    private void StartScreenPass()
    {
        if (!Features.ScreenText || !ScreenModelReady || ToolLocator.Ffmpeg is null) return;
        if (_screenRun is { IsCompleted: false }) return;
        _screenCts = new CancellationTokenSource();
        _screenRun = RunScreenPassAsync(_screenCts.Token);
    }

    private void StopScreenPass()
    {
        _screenCts?.Cancel();
        ScreenActivity = "";
    }

    private async Task RunScreenPassAsync(CancellationToken ct)
    {
        var done = 0;
        ScreenService? model = null;
        try
        {
            var tried = new HashSet<VideoItemViewModel>();
            var pace = new PassLog("Screen");
            using var awake = PlatformShell.StayAwake("Screen");
            Log.Info($"Screen pass: {_items.Values.Count(v => v.Entry.Screen is not { Version: ScreenService.Version })} to do");
            while (!ct.IsCancellationRequested && Features.ScreenText)
            {
                var todo = _items.Values.Where(v => v.Entry.Screen is not { Version: ScreenService.Version } && !tried.Contains(v))
                    .OrderByDescending(v => v.Entry.ModifiedUtc).ToList();
                if (todo.Count == 0) break;
                for (var i = 0; i < todo.Count && !ct.IsCancellationRequested && Features.ScreenText; i++)
                {
                    var item = todo[i];
                    tried.Add(item);
                    if (item.Entry.Screen is { Version: ScreenService.Version } || !File.Exists(item.Entry.Path)) continue;
                    var working = F("Reading the screen: {0} to go, {1}.", (todo.Count - i).ToString("N0"), CostMeter.Span(LeftSeconds(CostMeter.Screen, todo.Skip(i))));
                    if (model is null)
                    {
                        ScreenActivity = T("Starting the text reader…");
                        model = await LoadModelAsync("Screen", ScreenService.Load, ct);
                    }
                    var waited = await TakeTurnAsync(s => ScreenActivity = s, working, "Screen", ct);
                    using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    BackgroundHold.CancelAfterActive(limit, ItemLimit(item.Entry.DurationMs));
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    ScreenInfo? info;
                    try
                    {
                        var path = item.Entry.Path;
                        var length = item.Entry.DurationMs;
                        var best = item.Entry.Smart?.BestMs ?? -1;
                        info = await Task.Run(() => model.ReadAsync(path, length, best, limit.Token), limit.Token);
                        if (watch.Elapsed.TotalSeconds >= 0.5) CostMeter.Record(CostMeter.Screen, watch.Elapsed.TotalSeconds, length);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        Log.Warn($"Screen text for {item.Entry.FileName}: {SkipReason(ex, item.Entry.DurationMs)}");
                        CountFailure("Screen");
                        info = null;
                    }
                    finally { _workGate.Release(); pace.Add(item.Entry.FileName, waited, watch.Elapsed); }
                    if (_items.TryGetValue(item.Entry.Path, out var still) && ReferenceEquals(still, item))
                    {
                        item.Entry.Screen = info ?? new ScreenInfo { Version = ScreenService.Version };
                        TagGameFromScreen(item);
                        Retitle(item);
                    }
                    if (++done % 10 == 0) { _library.SaveCache(); OnPropertyChanged(nameof(ScreenModelText)); OnCostsChanged(); }
                    await Task.Delay(100, ct);
                }
            }
        }
        catch (OperationCanceledException) { /* stopped */ }
        catch (Exception ex)
        {
            Log.Warn($"Screen text pass stopped: {ex.Message}");
            ScreenModelError = ex.Message;
        }
        finally
        {
            Log.Info($"Screen pass ended: {done} done");
            model?.Dispose();
            ScreenActivity = "";
            OnCostsChanged();
            if (done > 0)
            {
                _library.SaveCache();
                OnPropertyChanged(nameof(ScreenModelText));
                RebuildTags();
            }
        }
    }

    /// <summary>A clip with no game yet gets the one whose own words are on screen (SPIKE PLANTED: Valorant).</summary>
    private void TagGameFromScreen(VideoItemViewModel item)
    {
        var e = item.Entry;
        if (ScreenWords.Game(e.Screen) is not { } game) return;
        var isGame = GameTest(e);
        if (e.Tags.Any(isGame)) return;
        e.Tags.Add(game);
        item.RefreshAll();
    }

    private bool ScreenMatches(VideoEntry e, string term) => ScreenWords.Matches(e.Screen, term);
}
