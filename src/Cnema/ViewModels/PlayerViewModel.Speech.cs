using Cnema.Models;
using Cnema.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cnema.ViewModels;

// What's said in the video: captions while playing, and turning speech into editable captions.
public sealed partial class PlayerViewModel
{
    /// <summary>The line being said right now, when spoken captions are on.</summary>
    [ObservableProperty] private string? _spokenLine;

    private IReadOnlyList<SpeechLine> SpeechLines =>
        Features.Speech && Current?.Entry.Speech is { } s ? s.Lines : Array.Empty<SpeechLine>();

    /// <summary>This video has words written down for it.</summary>
    public bool HasSpeech => SpeechLines.Count > 0;

    public bool ShowSpoken
    {
        get => _settings.Current.SpokenCaptions;
        set
        {
            if (_settings.Current.SpokenCaptions == value) return;
            _settings.Current.SpokenCaptions = value;
            _settings.Save();
            OnPropertyChanged();
            UpdateSpoken();
        }
    }

    [RelayCommand]
    private void ToggleSpoken()
    {
        ShowSpoken = !ShowSpoken;
        if (ShowSpoken && !HasSpeech) ShowToast(T("Nothing said in this video yet, or it hasn't been listened to."));
    }

    /// <summary>Called when the video, the speech or the features change.</summary>
    public void RefreshSpeech()
    {
        OnPropertyChanged(nameof(HasSpeech));
        UpdateSpoken();
    }

    private void UpdateSpoken()
    {
        string? line = null;
        if (ShowSpoken)
        {
            var t = (long)Position;
            var lines = SpeechLines;
            // Lines are in order; a line stays up for a moment after it ends so short ones can be read.
            for (var i = 0; i < lines.Count; i++)
            {
                var l = lines[i];
                if (l.Start > t) break;
                var until = Math.Max(l.End, l.Start + 1500);
                if (i + 1 < lines.Count) until = Math.Min(until, lines[i + 1].Start);
                if (t < until) line = l.Text;
            }
        }
        if (line != SpokenLine) SpokenLine = line;
    }

    /// <summary>Every line said in the part being exported becomes a caption, which can then be edited.</summary>
    [RelayCommand]
    private void CaptionsFromSpeech()
    {
        if (!ShowCaptionTools || !HasSpeech) return;
        var from = EffectiveIn;
        var to = EffectiveOut;
        var added = 0;
        foreach (var l in SpeechLines)
        {
            if (l.End <= from || l.Start >= to) continue;
            // Already there (pressed twice): leave it.
            if (Captions.Any(c => Math.Abs(c.Start - l.Start) < 200 && c.Text == l.Text)) continue;
            var c = new CaptionViewModel { Start = Math.Max(from, l.Start), End = Math.Min(to, Math.Max(l.End, l.Start + 1000)), Text = l.Text };
            c.Size.Value = CaptionSize.Small;
            c.Look.Value = CaptionStyle.Box;
            c.Changed += EffectsChanged;
            Captions.Add(c);
            added++;
        }
        ShowToast(added == 0 ? T("No new words to add here.") : P(added, "Added {0} caption from what's said.", "Added {0:N0} captions from what's said."));
    }
}
