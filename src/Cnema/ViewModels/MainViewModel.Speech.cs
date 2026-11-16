using Avalonia.Threading;
using Cnema.Models;
using Cnema.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cnema.ViewModels;

// Voice search and captions: a speech model (a one-time download) writes down what's said, on this computer.
public sealed partial class MainViewModel
{
    private CancellationTokenSource? _speechCts;
    private Task? _speechRun;
    private CancellationTokenSource? _modelCts;

    public bool ShowSpeech => Features.Speech;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(SpeechModelText), nameof(CanDownloadSpeech))] private bool _speechModelReady = ModelStore.IsInstalled(ModelStore.Speech);
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(SpeechModelText), nameof(CanDownloadSpeech))] private bool _isDownloadingSpeech;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(SpeechModelText))] private double _speechDownloadProgress;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(SpeechModelText))] private string? _speechModelError;
    /// <summary>"Listening: 12 to go" while the background pass runs.</summary>
    [ObservableProperty] private string _speechActivity = "";

    private static string SpeechCostKey => ModelStore.SpeechChoice == "swedish" ? CostMeter.SpeechSwedish : CostMeter.Speech;

    public bool CanDownloadSpeech => !SpeechModelReady && !IsDownloadingSpeech;

    /// <summary>The speech models to pick from, in the order of <see cref="SpeechModelIndex"/>.</summary>
    public IReadOnlyList<string> SpeechModelOptions { get; } = new[]
    {
        F("Standard ({0}): quick, many languages", BackupViewModel.SizeText(ModelStore.SpeechStandard.Bytes)),
        F("Better Swedish ({0}): KB-Whisper, about 3 times slower", BackupViewModel.SizeText(ModelStore.SpeechSwedish.Bytes)),
    };

    public int SpeechModelIndex
    {
        get => ModelStore.SpeechChoice == "swedish" ? 1 : 0;
        set
        {
            var choice = value == 1 ? "swedish" : "standard";
            if (choice == ModelStore.SpeechChoice || IsDownloadingSpeech) return;
            StopSpeechPass();
            ModelStore.SpeechChoice = choice;
            _settings.Current.SpeechModel = choice;
            _settings.Save();
            SpeechModelReady = ModelStore.IsInstalled(ModelStore.Speech);
            SpeechModelError = null;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SpeechModelText));
            OnPropertyChanged(nameof(SpeechCostText));
            if (Features.Speech && SpeechModelReady) StartSpeechPass();
        }
    }

    public string SpeechModelText =>
        IsDownloadingSpeech ? F("Downloading the speech model: {0:0}%", SpeechDownloadProgress * 100)
        : SpeechModelError is { } err ? err
        : SpeechModelReady ? F("Speech model installed ({0}). Listened to {1} of {2}; speech found in {3}.", BackupViewModel.SizeText(ModelStore.Speech.Bytes),
            _items.Values.Count(v => v.Entry.Speech is { Version: SpeechService.Version }).ToString("N0"), _items.Count.ToString("N0"), SpeechCountText) + FailedText("Speech")
        : F("Needs a one-time download of a speech model ({0}). After that, nothing leaves this computer.", BackupViewModel.SizeText(ModelStore.Speech.Bytes));

    private string SpeechCountText
    {
        get
        {
            var heard = _items.Values.Count(v => v.Entry.Speech is { Version: SpeechService.Version, Lines.Count: > 0 });
            return P(heard, "{0} video", "{0:N0} videos");
        }
    }

    [RelayCommand]
    private async Task DownloadSpeechModelAsync()
    {
        if (IsDownloadingSpeech) return;
        _modelCts = new CancellationTokenSource();
        IsDownloadingSpeech = true;
        SpeechModelError = null;
        SpeechDownloadProgress = 0;
        try
        {
            var progress = new Progress<double>(p => SpeechDownloadProgress = p);
            await Task.Run(() => ModelStore.DownloadAsync(ModelStore.Speech, progress, _modelCts.Token));
            SpeechModelReady = true;
            StartSpeechPass();
        }
        catch (OperationCanceledException) { /* cancelled */ }
        catch (Exception ex) { SpeechModelError = ex.Message; }
        finally
        {
            IsDownloadingSpeech = false;
            _modelCts.Dispose();
            _modelCts = null;
        }
    }

    [RelayCommand]
    private void CancelSpeechDownload() => _modelCts?.Cancel();

    /// <summary>Removes the model (the words already found stay, and stay searchable).</summary>
    [RelayCommand]
    private void RemoveSpeechModel()
    {
        StopSpeechPass();
        ModelStore.Remove(ModelStore.Speech);
        SpeechModelReady = ModelStore.IsInstalled(ModelStore.Speech);
        SpeechModelError = null;
    }

    // ---------- listening, in the background ----------

    private void StartSpeechPass()
    {
        if (!Features.Speech || !SpeechModelReady || ToolLocator.Ffmpeg is null) return;
        if (_speechRun is { IsCompleted: false }) return;
        _speechCts = new CancellationTokenSource();
        _speechRun = RunSpeechPassAsync(_speechCts.Token);
    }

    private void StopSpeechPass()
    {
        _speechCts?.Cancel();
        SpeechActivity = "";
    }


    private async Task RunSpeechPassAsync(CancellationToken ct)
    {
        var done = 0;
        SpeechService? speech = null;
        try
        {
            var tried = new HashSet<VideoItemViewModel>();
            Log.Info($"Speech pass: {_items.Values.Count(v => v.Entry.Speech is not { Version: SpeechService.Version })} to do");
            while (!ct.IsCancellationRequested && Features.Speech)
            {
                var todo = _items.Values.Where(v => v.Entry.Speech is not { Version: SpeechService.Version } && !tried.Contains(v))
                    .OrderByDescending(v => v.Entry.ModifiedUtc).ToList();
                if (todo.Count == 0) break;
                for (var i = 0; i < todo.Count && !ct.IsCancellationRequested && Features.Speech; i++)
                {
                    var item = todo[i];
                    tried.Add(item);
                    if (item.Entry.Speech is { Version: SpeechService.Version } || !File.Exists(item.Entry.Path)) continue;
                    var working = F("Listening for what's said: {0} to go, {1}.", (todo.Count - i).ToString("N0"), CostMeter.Span(LeftSeconds(SpeechCostKey, todo.Skip(i))));
                    if (speech is null)
                    {
                        // Loaded only once there's something to do, and let go of when the pass ends (it holds memory).
                        SpeechActivity = T("Starting the speech model…");
                        speech = await LoadModelAsync("Speech", () => SpeechService.Load(ModelStore.PathOf(ModelStore.Speech)), ct);
                    }
                    await TakeTurnAsync(s => SpeechActivity = s, working, "Speech", ct);
                    SpeechInfo? info;
                    try
                    {
                        var path = item.Entry.Path;
                        var length = item.Entry.DurationMs;
                        var watch = System.Diagnostics.Stopwatch.StartNew();
                        info = await Task.Run(() => speech.TranscribeAsync(path, length, ct), ct);
                        // A video with no sound is skipped in a blink; that says nothing about this computer's pace.
                        if (info.Language is not null || watch.Elapsed.TotalSeconds >= 1) CostMeter.Record(SpeechCostKey, watch.Elapsed.TotalSeconds, length);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        Log.Warn($"Speech for {item.Entry.FileName}: {ex.Message}");
                        CountFailure("Speech");
                        info = null;
                    }
                    finally { _workGate.Release(); }
                    if (_items.TryGetValue(item.Entry.Path, out var still) && ReferenceEquals(still, item))
                    {
                        item.Entry.Speech = info ?? new SpeechInfo { Version = SpeechService.Version };
                        if (ReferenceEquals(Player.Current, item)) Player.RefreshSpeech();
                    }
                    if (++done % 10 == 0) { _library.SaveCache(); OnPropertyChanged(nameof(SpeechModelText)); OnCostsChanged(); }
                    await Task.Delay(150, ct);
                }
            }
        }
        catch (OperationCanceledException) { /* stopped */ }
        catch (Exception ex)
        {
            Log.Warn($"Speech pass stopped: {ex.Message}");
            SpeechModelError = ex.Message;
        }
        finally
        {
            Log.Info($"Speech pass ended: {done} done");
            speech?.Dispose();
            SpeechActivity = "";
            OnCostsChanged();
            if (done > 0)
            {
                _library.SaveCache();
                OnPropertyChanged(nameof(SpeechModelText));
                if (SearchText.Trim().Length > 0) RefreshView();
            }
        }
    }

    // ---------- search ----------

    private static bool SaidMatches(VideoEntry e, string term) =>
        e.Speech is { Lines.Count: > 0 } s && s.Lines.Any(l => TextMatch.Has(l.Text, term));

    /// <summary>On each card in the results: the line where the searched words are said, if any.</summary>
    private void MarkSaid(IEnumerable<VideoItemViewModel> shown, string search)
    {
        var words = search.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(t => !t.StartsWith('#') && !t.StartsWith('@')).ToList();
        var phrase = string.Join(' ', words);
        var visible = new HashSet<VideoItemViewModel>();
        if (Features.Speech && phrase.Length > 0)
            foreach (var v in shown)
            {
                visible.Add(v);
                if (v.Entry.Speech is not { Lines.Count: > 0 } s) { v.Said = null; continue; }
                var line = s.Lines.FirstOrDefault(l => TextMatch.Has(l.Text, phrase))
                           ?? s.Lines.FirstOrDefault(l => words.Any(w => TextMatch.Has(l.Text, w)));
                v.Said = line is null ? null : Snippet(line.Text, phrase.Length > 0 && TextMatch.Has(line.Text, phrase) ? phrase : words.First(w => TextMatch.Has(line.Text, w)));
                v.SaidAtMs = line?.Start ?? -1;
            }
        foreach (var v in _items.Values)
            if (v.Said is not null && !visible.Contains(v)) { v.Said = null; v.SaidAtMs = -1; }
    }

    /// <summary>About fifty characters around the match.</summary>
    internal static string Snippet(string text, string match)
    {
        const int Room = 50;
        if (text.Length <= Room) return text;
        var at = Math.Max(0, TextMatch.IndexOf(text, match));
        var start = Math.Clamp(at - (Room - match.Length) / 2, 0, text.Length - Room);
        var s = text.Substring(start, Room).Trim();
        return (start > 0 ? "…" : "") + s + (start + Room < text.Length ? "…" : "");
    }
}
