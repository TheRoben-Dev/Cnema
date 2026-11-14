using System.Collections.ObjectModel;
using Cnema.Controls;
using Cnema.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cnema.ViewModels;

/// <summary>One entry of the speed menu.</summary>
public sealed partial class RateOption : ObservableObject
{
    public double Value { get; }
    public string Label => Value == 1 ? "Normal" : $"{Value:0.##}×";
    [ObservableProperty] private bool _isCurrent;
    public RateOption(double value) => Value = value;
}

/// <summary>One chapter row in the player's Chapters menu.</summary>
public sealed partial class ChapterViewModel : ObservableObject
{
    public Chapter Chapter { get; }
    public string Title => Chapter.Title;
    public string StartText => TrimTimeline.FormatTime(Chapter.Start);
    public string LengthText => TrimTimeline.FormatTime(Chapter.End - Chapter.Start);
    [ObservableProperty] private bool _isCurrent;
    public ChapterViewModel(Chapter chapter) => Chapter = chapter;
}

// Speed, A-B loop, chapters and the frame clock.
public sealed partial class PlayerViewModel
{
    // ---------- speed ----------
    public IReadOnlyList<RateOption> RateOptions { get; } = Rates.Select(r => new RateOption(r) { IsCurrent = r == 1 }).ToList();
    public string RateText => Rate == 1 ? "1×" : $"{Rate:0.##}×";
    public bool IsRateChanged => Rate != 1;

    partial void OnRateChanged(double value)
    {
        _player?.SetRate((float)value);
        foreach (var o in RateOptions) o.IsCurrent = o.Value == value;
        OnPropertyChanged(nameof(RateText));
        OnPropertyChanged(nameof(IsRateChanged));
    }

    [RelayCommand]
    private void SetRate(RateOption? option)
    {
        if (option is null) return;
        Rate = option.Value;
        ShowToast(option.Value == 1 ? "Normal speed" : $"Speed {Rate:0.##}×");
    }

    [RelayCommand]
    private void SpeedUp() => StepRate(+1);

    [RelayCommand]
    private void SpeedDown() => StepRate(-1);

    private void StepRate(int direction)
    {
        var i = Array.IndexOf(Rates, Rate);
        if (i < 0) i = Array.IndexOf(Rates, 1.0);
        var next = Math.Clamp(i + direction, 0, Rates.Length - 1);
        Rate = Rates[next];
        ShowToast(Rate == 1 ? "Normal speed" : $"Speed {Rate:0.##}×");
    }

    // ---------- A-B loop ----------
    // The loop region is the in/out selection, so a loop can be exported as it is.
    private bool _inLoop;

    public string AbText => LoopRange && HasRange ? "A-B on" : HasIn && !HasOut ? "A ··· B" : "A-B";
    public string AbTip => LoopRange && HasRange ? "Stop looping (B)"
        : HasIn && !HasOut ? "Set the end of the loop here (B)"
        : HasRange ? "Loop the selection (B)"
        : "Loop part of the video: press at the start, then at the end (B)";

    partial void OnInPointChanged(double value) => RaiseAb();
    partial void OnOutPointChanged(double value) => RaiseAb();

    partial void OnLoopRangeChanged(bool value)
    {
        RaiseAb();
        if (!value || !HasRange) { _inLoop = false; return; }
        // Turning the loop on outside the region jumps into it.
        if (Position < EffectiveIn || Position >= EffectiveOut - 40) Seek(EffectiveIn);
        else _inLoop = true;
        if (_player is { IsPlaying: false }) _player.SetPause(false);
    }

    private void RaiseAb()
    {
        OnPropertyChanged(nameof(AbText));
        OnPropertyChanged(nameof(AbTip));
    }

    [RelayCommand]
    private void AbLoop()
    {
        if (LoopRange && HasRange)
        {
            LoopRange = false;
            ShowToast("Loop off. The selection stays for trimming; X clears it.");
            return;
        }
        if (HasIn && !HasOut)
        {
            if (Position <= InPoint + 100) { ShowToast("The end of the loop has to come after its start."); return; }
            OutPoint = Position;
            LoopRange = true;
            ShowToast("Looping. Press B again to stop.");
            return;
        }
        if (HasRange)
        {
            LoopRange = true;
            ShowToast("Looping the selection. Press B again to stop.");
            return;
        }
        InPoint = Position;
        ShowToast("Loop start set. Press B again at the end.");
    }

    [RelayCommand]
    private void ToggleLoop()
    {
        if (!HasRange) { ShowToast("Mark a selection first (I / O), or press B to set a loop."); return; }
        LoopRange = !LoopRange;
    }

    /// <summary>Called on every time update while looping; true when it jumped back.</summary>
    private bool LoopTick(long t)
    {
        var end = EffectiveOut;
        if (t >= EffectiveIn && t < end - 40) { _inLoop = true; return false; }
        if (!_inLoop || t < end - 40) return false;
        Seek(EffectiveIn);
        return true;
    }

    private void OnEndReached()
    {
        // A loop with no out point runs to the end of the file, then starts over.
        if (LoopRange && HasRange && _inLoop && _player is not null)
        {
            _player.Stop();
            _player.Play();
            _player.Time = (long)EffectiveIn;
            return;
        }
        IsPlaying = false;
        Position = Duration;
    }

    // ---------- chapters ----------
    public ObservableCollection<ChapterViewModel> ChapterList { get; } = new();
    public bool HasChapters => ChapterList.Count > 1;
    [ObservableProperty] private string _chapterText = "";

    /// <summary>After the Chapters feature was switched on or off.</summary>
    public void RefreshChapters() => BuildChapters();

    private void BuildChapters()
    {
        ChapterList.Clear();
        if (Chapters.Enabled && Current is { } item && Duration > 0)
            foreach (var c in Chapters.FromBookmarks(item.Entry.Bookmarks, (long)Duration))
                ChapterList.Add(new ChapterViewModel(c));
        OnPropertyChanged(nameof(HasChapters));
        UpdateCurrentChapter();
    }

    private void UpdateCurrentChapter()
    {
        if (ChapterList.Count < 2) { ChapterText = ""; return; }
        ChapterViewModel? current = null;
        foreach (var c in ChapterList)
        {
            var on = Position >= c.Chapter.Start && (Position < c.Chapter.End || c == ChapterList[^1]);
            c.IsCurrent = on && current is null;
            if (c.IsCurrent) current = c;
        }
        ChapterText = current is null ? $"{ChapterList.Count} chapters" : $"{current.Title} · {current.Chapter.Number}/{ChapterList.Count}";
    }

    [RelayCommand]
    private void JumpToChapter(ChapterViewModel? chapter)
    {
        if (chapter is null) return;
        Seek(chapter.Chapter.Start);
        if (_player is { IsPlaying: false }) _player.SetPause(false);
    }

    /// <summary>Makes a chapter the in/out selection, ready to export or loop.</summary>
    [RelayCommand]
    private void SelectChapter(ChapterViewModel? chapter)
    {
        if (chapter is null) return;
        InPoint = chapter.Chapter.Start;
        OutPoint = chapter.Chapter.End;
        Seek(InPoint);
        ShowToast($"{chapter.Title} selected. Export saves just this chapter.");
    }

    [RelayCommand]
    private void NextChapter() => StepChapter(+1);

    [RelayCommand]
    private void PreviousChapter() => StepChapter(-1);

    private void StepChapter(int direction)
    {
        if (ChapterList.Count < 2) { ShowToast("This video has no chapters."); return; }
        var target = direction > 0
            ? ChapterList.FirstOrDefault(c => c.Chapter.Start > Position + 500)
            : ChapterList.LastOrDefault(c => c.Chapter.Start < Position - 1500);
        if (target is not null) JumpToChapter(target);
    }

    /// <summary>The chapters inside the export range, for writing into the exported file.</summary>
    private IReadOnlyList<Chapter>? ChaptersFor(long start, long end) =>
        ChapterList.Count < 2 ? null : Chapters.Within(ChapterList.Select(c => c.Chapter).ToList(), start, end);

    // ---------- frame clock ----------
    public bool ShowFrames
    {
        get => _settings.Current.ShowFrames;
        set
        {
            if (_settings.Current.ShowFrames == value) return;
            _settings.Current.ShowFrames = value;
            _settings.Save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(PositionText));
        }
    }

    [RelayCommand]
    private void ToggleClock() => ShowFrames = !ShowFrames;

    private string FormatFrames(double ms)
    {
        if (ms < 0) ms = 0;
        var fps = Current?.Entry.FrameRate is > 1 and var f ? f : 30;
        var t = TimeSpan.FromMilliseconds(ms);
        var frame = (int)Math.Floor(t.Milliseconds / 1000.0 * fps + 0.0001);
        var whole = t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
        return $"{whole} f{frame:00}";
    }
}
