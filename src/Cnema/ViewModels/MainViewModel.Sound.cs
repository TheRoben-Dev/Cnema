using Cnema.Models;
using Cnema.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cnema.ViewModels;

// Laughter and hype: YAMNet (an optional 15 MB download) listens for laughing, cheering and shouting,
// and those moments lead Worth a rewatch and give the blob something to say.
public sealed partial class MainViewModel
{
    private CancellationTokenSource? _soundCts;
    private Task? _soundRun;
    private CancellationTokenSource? _soundModelCts;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(SoundModelText), nameof(CanDownloadSound))] private bool _soundModelReady = ModelStore.IsInstalled(ModelStore.Sound);
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(SoundModelText), nameof(CanDownloadSound))] private bool _isDownloadingSound;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(SoundModelText))] private double _soundDownloadProgress;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(SoundModelText))] private string? _soundModelError;
    [ObservableProperty] private string _soundActivity = "";

    public bool CanDownloadSound => !SoundModelReady && !IsDownloadingSound;

    /// <summary>What still has to come down: the model, plus ONNX Runtime unless visual search brought it.</summary>
    private long SoundDownloadBytes => ModelStore.Sound.Where(m => !ModelStore.IsInstalled(m)).Sum(m => m.Bytes);

    public string SoundModelText =>
        IsDownloadingSound ? F("Downloading the sound model: {0:0}%", SoundDownloadProgress * 100)
        : SoundModelError is { } err ? err
        : SoundModelReady ? F("Sound model installed. Listened to {0} of {1}; laughter heard in {2}.",
            _items.Values.Count(v => v.Entry.Sound is { Version: SoundService.Version }).ToString("N0"), _items.Count.ToString("N0"),
            P(_items.Values.Count(v => v.Entry.Sound is { Version: SoundService.Version, Laugh.Count: > 1 }), "{0} video", "{0:N0} videos")) + FailedText("Sound")
        : F("Needs a one-time download of a small sound model ({0}). After that, nothing leaves this computer.", BackupViewModel.SizeText(SoundDownloadBytes));

    [RelayCommand]
    private async Task DownloadSoundModelAsync()
    {
        if (IsDownloadingSound) return;
        _soundModelCts = new CancellationTokenSource();
        IsDownloadingSound = true;
        SoundModelError = null;
        SoundDownloadProgress = 0;
        try
        {
            var progress = new Progress<double>(p => SoundDownloadProgress = p);
            await Task.Run(() => ModelStore.DownloadAsync(ModelStore.Sound, progress, _soundModelCts.Token));
            SoundModelReady = true;
            StartSoundPass();
        }
        catch (OperationCanceledException) { /* cancelled */ }
        catch (Exception ex) { SoundModelError = ex.Message; }
        finally
        {
            IsDownloadingSound = false;
            _soundModelCts.Dispose();
            _soundModelCts = null;
        }
    }

    [RelayCommand] private void CancelSoundDownload() => _soundModelCts?.Cancel();

    [RelayCommand]
    private void RemoveSoundModel()
    {
        StopSoundPass();
        ModelStore.Remove(ModelStore.Sound);
        SoundModelReady = ModelStore.IsInstalled(ModelStore.Sound);
        SoundModelError = null;
        VisualModelReady = ModelStore.IsInstalled(ModelStore.Visual);
    }

    private void StartSoundPass()
    {
        if (!Features.Laughs || !SoundModelReady || ToolLocator.Ffmpeg is null) return;
        if (_soundRun is { IsCompleted: false } running)
        {
            // Switched off and on again before the last run had stopped: start again once it has.
            if (_soundCts is { IsCancellationRequested: true }) RestartAfter(running, StartSoundPass);
            return;
        }
        _soundCts = new CancellationTokenSource();
        _soundRun = RunSoundPassAsync(_soundCts.Token);
    }

    private void StopSoundPass()
    {
        _soundCts?.Cancel();
        SoundActivity = "";
    }

    /// <summary>One background job at a time: after best moments, listening and looking.</summary>

    private async Task RunSoundPassAsync(CancellationToken ct)
    {
        var done = 0;
        SoundService? model = null;
        try
        {
            var tried = new HashSet<VideoItemViewModel>();
            var pace = new PassLog("Sound");
            using var awake = PlatformShell.StayAwake("Sound");
            Log.Info($"Sound pass: {_items.Values.Count(v => v.Entry.Sound is not { Version: SoundService.Version })} to do");
            while (!ct.IsCancellationRequested && Features.Laughs)
            {
                var todo = _items.Values.Where(v => v.Entry.Sound is not { Version: SoundService.Version } && !tried.Contains(v))
                    .OrderByDescending(v => v.Entry.ModifiedUtc).ToList();
                if (todo.Count == 0) break;
                for (var i = 0; i < todo.Count && !ct.IsCancellationRequested && Features.Laughs; i++)
                {
                    var item = todo[i];
                    tried.Add(item);
                    if (item.Entry.Sound is { Version: SoundService.Version } || !File.Exists(item.Entry.Path)) continue;
                    var working = F("Listening for laughs: {0} to go, {1}.", (todo.Count - i).ToString("N0"), CostMeter.Span(LeftSeconds(CostMeter.Sound, todo.Skip(i))));
                    if (model is null)
                    {
                        SoundActivity = T("Starting the sound model…");
                        model = await LoadModelAsync("Sound", SoundService.Load, ct);
                    }
                    var waited = await TakeTurnAsync(s => SoundActivity = s, working, "Sound", ct);
                    using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    BackgroundHold.CancelAfterActive(limit, ItemLimit(item.Entry.DurationMs));
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    SoundInfo? info;
                    try
                    {
                        var path = item.Entry.Path;
                        var length = item.Entry.DurationMs;
                        info = await Task.Run(() => model.ListenAsync(path, length, limit.Token), limit.Token);
                        if (watch.Elapsed.TotalSeconds >= 0.5) CostMeter.Record(CostMeter.Sound, watch.Elapsed.TotalSeconds, length);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        Log.Warn($"Laughs for {item.Entry.FileName}: {SkipReason(ex, item.Entry.DurationMs)}");
                        CountFailure("Sound");
                        info = null;
                    }
                    finally { _workGate.Release(); pace.Add(item.Entry.FileName, waited, watch.Elapsed); }
                    if (_items.TryGetValue(item.Entry.Path, out var still) && ReferenceEquals(still, item))
                    {
                        item.Entry.Sound = info ?? new SoundInfo { Version = SoundService.Version };
                        Retitle(item);
                    }
                    if (++done % 10 == 0) { _library.SaveCache(); OnPropertyChanged(nameof(SoundModelText)); OnCostsChanged(); }
                    await Task.Delay(100, ct);
                }
            }
        }
        catch (OperationCanceledException) { /* stopped */ }
        catch (Exception ex)
        {
            Log.Warn($"Laughs pass stopped: {ex.Message}");
            SoundModelError = ex.Message;
        }
        finally
        {
            Log.Info($"Sound pass ended: {done} done");
            model?.Dispose();
            SoundActivity = "";
            OnCostsChanged();
            if (done > 0)
            {
                _library.SaveCache();
                OnPropertyChanged(nameof(SoundModelText));
                RefreshMemories();
            }
        }
    }

    /// <summary>Seconds of laughter in a video (clusters of at least two moments, so one odd blip doesn't count).</summary>
    internal static double LaughSeconds(VideoEntry e) =>
        e.Sound is { Laugh.Count: > 1 } s ? s.LaughSeconds : 0;

    internal static double HypeSeconds(VideoEntry e) =>
        e.Sound is { Hype.Count: > 1 } s ? s.HypeSeconds : 0;
}
