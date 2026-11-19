using Cnema.Services;

namespace Cnema.ViewModels;

// What each smart feature costs: shown under its switch in Settings, on or off, so the choice is an informed one.
public sealed partial class MainViewModel
{
    public string MomentsCostText => CostText(CostMeter.Moments, v => v.Entry.Smart is { Version: SmartAnalyzer.Version }, Features.Highlights, 0);
    public string SpeechCostText => CostText(SpeechCostKey, v => v.Entry.Speech is { Version: SpeechService.Version }, Features.Speech, ModelStore.BytesOf(ModelStore.SpeechPack));
    public string VisualCostText => CostText(CostMeter.Visual, v => v.Entry.Visual is { Version: VisualService.Version }, Features.Visual, ModelStore.BytesOf(ModelStore.Visual));
    public string SoundCostText => CostText(CostMeter.Sound, v => v.Entry.Sound is { Version: SoundService.Version }, Features.Laughs, SoundDownloadBytes);
    public string VoicesCostText => CostText(CostMeter.Voices, v => v.Entry.Speech is not { Lines.Count: > 0 } || v.Entry.Voices is { Version: VoiceService.Version }, Features.Voices, VoiceDownloadBytes);
    public string ScreenCostText => CostText(CostMeter.Screen, v => v.Entry.Screen is { Version: ScreenService.Version }, Features.ScreenText, ScreenDownloadBytes);
    public string TitlesCostText => T("Costs next to nothing: titles come from what Savefile already knows, with no download and no background work.");

    private DateTime _costsSaved = DateTime.MinValue;

    /// <summary>Re-reads the cost lines (a pass moved on) and keeps this computer's pace in settings now and then.</summary>
    private void OnCostsChanged()
    {
        OnPropertyChanged(nameof(MomentsCostText));
        OnPropertyChanged(nameof(SpeechCostText));
        OnPropertyChanged(nameof(SpeechRedoCount));
        OnPropertyChanged(nameof(CanRedoSpeech));
        OnPropertyChanged(nameof(SpeechRedoText));
        OnPropertyChanged(nameof(VisualCostText));
        OnPropertyChanged(nameof(SoundCostText));
        OnPropertyChanged(nameof(VoicesCostText));
        OnPropertyChanged(nameof(ScreenCostText));
        if (DateTime.UtcNow - _costsSaved > TimeSpan.FromSeconds(30))
        {
            _costsSaved = DateTime.UtcNow;
            try { _settings.Save(); } catch { /* next time */ }
        }
    }

    /// <summary>Seconds of work left for these videos on this computer.</summary>
    private static double LeftSeconds(string key, IEnumerable<VideoItemViewModel> todo) =>
        todo.Sum(v => CostMeter.SecondsFor(key, v.Entry.DurationMs));

    private string CostText(string key, Func<VideoItemViewModel, bool> isDone, bool on, long downloadBytes)
    {
        var p = CostMeter.Of(key);
        var memory = p.ExtraMemoryMb >= 100 ? F("about {0} MB more memory", p.ExtraMemoryMb) : T("very little memory");
        var disk = downloadBytes > 0 ? F("a {0} download", BackupViewModel.SizeText(downloadBytes)) : T("nothing to download");
        var uses = F("While it works: {0} · {1} · {2}.", T(p.Uses), memory, disk);

        var all = _items.Values.ToList();
        if (all.Count == 0) return uses;
        var left = all.Where(v => !isDone(v)).ToList();
        var typical = CostMeter.SecondsFor(key, Median(all.Select(v => v.Entry.DurationMs)));
        var source = CostMeter.IsMeasured(key) ? T("timed on this computer") : T("an estimate until it has done a few");
        string time;
        if (left.Count == 0)
            time = F("All {0} done. A new clip takes {1} ({2}).", P(all.Count, "{0} video", "{0:N0} videos"), CostMeter.Span(typical), source);
        else
        {
            var hours = left.Sum(v => Math.Max(0, v.Entry.DurationMs)) / 3_600_000.0;
            var length = hours >= 1 ? F("{0:0.#} h of video", hours) : F("{0:0} min of video", Math.Max(1, hours * 60));
            time = F(on ? "{0} to go ({1}): {2} in the background, then {3} per new clip ({4})." : "Your library, {0} ({1}): {2} in the background the first time, then {3} per new clip ({4}).",
                P(left.Count, "{0} video", "{0:N0} videos"), length, CostMeter.Span(LeftSeconds(key, left)), CostMeter.Span(typical), source);
        }
        return uses + "\n" + time;
    }

    private static long Median(IEnumerable<long> values)
    {
        var sorted = values.Where(v => v > 0).OrderBy(v => v).ToList();
        return sorted.Count == 0 ? 60_000 : sorted[sorted.Count / 2];
    }
}
