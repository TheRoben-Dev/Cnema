using Avalonia.Threading;
using Cnema.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cnema.ViewModels;

// The indexing indicator: the blob in the sidebar looking through your clips while the smart features work.
public sealed partial class MainViewModel
{
    private DispatcherTimer? _indexTimer;
    [ObservableProperty] private int _indexDone;
    [ObservableProperty] private int _indexTotal;
    [ObservableProperty] private bool _isIndexPaused;
    [ObservableProperty] private bool _isIndexOpen;
    /// <summary>Goes up when the best moments pass finds a really good one: the blob hops.</summary>
    [ObservableProperty] private int _indexCheer;

    public bool IsIndexing => SmartActivity.Length > 0 || SpeechActivity.Length > 0 || VisualActivity.Length > 0 || SoundActivity.Length > 0 || VoiceActivity.Length > 0;
    public bool ShowIndexing => IsIndexing && IndexTotal > 0 && IndexDone < IndexTotal;
    public double IndexProgress => IndexTotal == 0 ? 0 : (double)IndexDone / IndexTotal;
    public string IndexTitle =>
        IsIndexPaused ? T("Taking a break")
        : IsMakingWrapped ? T("Waiting for your Wrapped")
        : SmartShouldWait ? T("Waiting for a moment")
        : T("Looking through your clips…");
    public string IndexCount => F("{0} of {1}", IndexDone.ToString("N0"), IndexTotal.ToString("N0"));
    public string IndexPauseText => IsIndexPaused ? T("Carry on") : T("Pause for now");

    public bool HasMomentsActivity => SmartActivity.Length > 0;
    public bool HasSpeechActivity => SpeechActivity.Length > 0;
    public bool HasVisualActivity => VisualActivity.Length > 0;
    public bool HasSoundActivity => SoundActivity.Length > 0;
    public bool HasVoiceActivity => VoiceActivity.Length > 0;

    partial void OnSmartActivityChanged(string value) => RaiseIndexing();
    partial void OnSpeechActivityChanged(string value) => RaiseIndexing();
    partial void OnVisualActivityChanged(string value) => RaiseIndexing();
    partial void OnSoundActivityChanged(string value) => RaiseIndexing();
    partial void OnVoiceActivityChanged(string value) => RaiseIndexing();
    partial void OnIndexDoneChanged(int value) => RaiseIndexCounts();
    partial void OnIndexTotalChanged(int value) => RaiseIndexCounts();

    partial void OnIsIndexPausedChanged(bool value)
    {
        OnPropertyChanged(nameof(IndexTitle));
        OnPropertyChanged(nameof(IndexPauseText));
        Log.Info(value ? "Smart features paused from the sidebar" : "Smart features carry on");
    }

    private void RaiseIndexing()
    {
        OnPropertyChanged(nameof(IsIndexing));
        OnPropertyChanged(nameof(HasMomentsActivity));
        OnPropertyChanged(nameof(HasSpeechActivity));
        OnPropertyChanged(nameof(HasVisualActivity));
        OnPropertyChanged(nameof(HasSoundActivity));
        OnPropertyChanged(nameof(HasVoiceActivity));
        if (IsIndexing)
        {
            // Counting is a quick look over the library, a few times a minute at most.
            _indexTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(3), DispatcherPriority.Background, (_, _) => CountIndexed());
            if (!_indexTimer.IsEnabled) { CountIndexed(); _indexTimer.Start(); }
        }
        else if (_indexTimer is { IsEnabled: true })
        {
            _indexTimer.Stop();
            CountIndexed();
            if (!IsIndexing) IsIndexOpen = false;
        }
        OnPropertyChanged(nameof(ShowIndexing));
    }

    private void RaiseIndexCounts()
    {
        OnPropertyChanged(nameof(IndexTitle));
        OnPropertyChanged(nameof(IndexCount));
        OnPropertyChanged(nameof(IndexProgress));
        OnPropertyChanged(nameof(ShowIndexing));
    }

    /// <summary>A video counts as looked through once every smart feature that's on has had it.</summary>
    private void CountIndexed()
    {
        var done = 0;
        var total = 0;
        foreach (var v in _items.Values)
        {
            total++;
            var e = v.Entry;
            if ((!Features.Highlights || e.Smart is { Version: SmartAnalyzer.Version })
                && (!(Features.Speech && SpeechModelReady) || e.Speech is { Version: SpeechService.Version })
                && (!(Features.Visual && VisualModelReady) || e.Visual is { Version: VisualService.Version })
                && (!(Features.Laughs && SoundModelReady) || e.Sound is { Version: SoundService.Version })
                && (!(Features.Voices && Features.Speech && VoiceModelReady) || !NeedsVoices(e)))
                done++;
        }
        IndexTotal = total;
        IndexDone = done;
        OnPropertyChanged(nameof(IndexTitle)); // waiting for a game, an export or Wrapped comes and goes
    }

    [RelayCommand] private void ToggleIndexOpen() => IsIndexOpen = !IsIndexOpen;
    [RelayCommand] private void ToggleIndexPause() => IsIndexPaused = !IsIndexPaused;
}
