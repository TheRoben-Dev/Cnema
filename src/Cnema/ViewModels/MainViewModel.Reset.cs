using Cnema.Models;
using Cnema.Services;
using CommunityToolkit.Mvvm.Input;

namespace Cnema.ViewModels;

/// <summary>
/// A Reset for each smart feature: forgets what it worked out for every video and lets it start over in the
/// background. What you did yourself stays: names you gave, friends you tagged, your answers in Who's that?.
/// </summary>
public partial class MainViewModel
{
    /// <summary>Asks first, stops the pass, clears its results from every video, saves, and starts it again.</summary>
    private async Task ResetSmartAsync(string feature, string what, Action stop, Func<Task?> running, Action start, Func<VideoItemViewModel, bool> clear)
    {
        if (!await Ask.SureAsync(F("Start {0} over?", feature), what, T("Reset"))) return;
        stop();
        if (running() is { IsCompleted: false } run)
            try { await run; } catch { /* it was stopped; its own finally tidied up */ }
        var n = 0;
        foreach (var item in _items.Values.ToList())
            if (clear(item)) { item.RefreshAll(); n++; }
        Log.Info($"Reset {feature}: {n} videos");
        _library.SaveCache();
        RebuildTags();
        RefreshView();
        OnCostsChanged();
        start();
    }

    /// <summary>Takes the friends added by voice off a video (never ones tagged by hand) and forgets its voices.</summary>
    private static bool ClearVoices(VideoEntry e)
    {
        if (e.Voices is not { } v) return false;
        foreach (var name in v.Added)
            e.People.RemoveAll(p => string.Equals(p, name, StringComparison.OrdinalIgnoreCase));
        e.Voices = null;
        return true;
    }

    [RelayCommand]
    private Task ResetHighlightsAsync() => ResetSmartAsync(T("Best moments"),
        T("Savefile forgets the best moments it found in every video (and the thumbnails and trim suggestions that came from them), then looks again in the background. Thumbnails you picked yourself stay."),
        StopSmartPass, () => _smartRun, StartSmartPass, item =>
        {
            if (item.Entry.Smart is null) return false;
            item.Entry.Smart = null;
            return true;
        });

    [RelayCommand]
    private Task ResetTitlesAsync() => ResetSmartAsync(T("Smart titles"),
        T("Every title Savefile made up by itself goes back to the file's name, and new ones are suggested again with what Savefile knows now. Titles you typed or picked yourself stay."),
        () => { }, () => null, () => { }, item =>
        {
            if (!item.Entry.TitleAuto) return false;
            item.Entry.Title = null;
            Retitle(item);
            return true;
        });

    [RelayCommand]
    private Task ResetSpeechAsync() => ResetSmartAsync(T("Voice search"),
        T("Savefile forgets everything it wrote down from your videos, and who's talking in them (it comes from those words), then listens again in the background. Friends you tagged yourself and your answers in Who's that? stay."),
        () => { StopSpeechPass(); StopVoicePass(); }, () => _speechRun, () => { StartSpeechPass(); StartVoicePass(); }, item =>
        {
            var e = item.Entry;
            var had = e.Speech is not null;
            e.Speech = null;
            return ClearVoices(e) || had;
        });

    [RelayCommand]
    private Task ResetVisualAsync() => ResetSmartAsync(T("Visual search"),
        T("Savefile forgets what it saw in your videos, then looks again in the background."),
        StopVisualPass, () => _visualRun, StartVisualPass, item =>
        {
            if (item.Entry.Visual is null) return false;
            item.Entry.Visual = null;
            return true;
        });

    [RelayCommand]
    private Task ResetSoundAsync() => ResetSmartAsync(T("Laughter and hype"),
        T("Savefile forgets the laughs and cheers it heard in your videos, then listens again in the background."),
        StopSoundPass, () => _soundRun, StartSoundPass, item =>
        {
            if (item.Entry.Sound is null) return false;
            item.Entry.Sound = null;
            return true;
        });

    [RelayCommand]
    private Task ResetScreenAsync() => ResetSmartAsync(T("Text on screen"),
        T("Savefile forgets the text it read on screen in your videos, then reads again in the background."),
        StopScreenPass, () => _screenRun, StartScreenPass, item =>
        {
            if (item.Entry.Screen is null) return false;
            item.Entry.Screen = null;
            return true;
        });

    [RelayCommand]
    private Task ResetVoicesAsync() => ResetSmartAsync(T("Who's talking"),
        T("Savefile takes off every friend it tagged by voice and listens to your videos again in the background. From now on it only tags a friend when it's certain. Friends you tagged yourself stay, and so do your answers in Who's that?."),
        StopVoicePass, () => _voiceRun, StartVoicePass, item => ClearVoices(item.Entry));
}
