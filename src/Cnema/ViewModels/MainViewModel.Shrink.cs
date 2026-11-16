using System.Collections.ObjectModel;
using Cnema.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cnema.ViewModels;

/// <summary>One video in Make room: how big it is now, about how big it would be, and how it went.</summary>
public sealed partial class ShrinkRow : ObservableObject
{
    public VideoItemViewModel Item { get; }
    public string Name => Item.Title;
    public string Detail { get; }
    public long Bytes { get; }
    public long GuessBytes => (long)(Bytes * ShrinkService.Guess);
    [ObservableProperty] private bool _isChecked;
    [ObservableProperty] private string _state = "";
    [ObservableProperty] private double _progress = -1;
    public bool IsWorking => Progress >= 0;
    partial void OnProgressChanged(double value) => OnPropertyChanged(nameof(IsWorking));

    public ShrinkRow(VideoItemViewModel item)
    {
        Item = item;
        Bytes = item.Entry.Size;
        var seen = item.Entry.LastPlayedUtc is { } p ? F("last watched {0}", p.ToLocalTime().ToString("MMM yyyy", System.Globalization.CultureInfo.CurrentCulture)) : T("never watched");
        Detail = F("{0} · {1} · about {2} after", VideoItemViewModel.FormatSize(Bytes), seen, VideoItemViewModel.FormatSize(GuessBytes));
    }
}

/// <summary>Make room: big, rarely watched videos encoded again at the same look, after a look at the list.</summary>
public sealed partial class ShrinkViewModel : ObservableObject
{
    public ObservableCollection<ShrinkRow> Rows { get; }
    public bool HasRows => Rows.Count > 0;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsIdle))] private bool _isBusy;
    public bool IsIdle => !IsBusy;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _isConfirming;
    public string Intro { get; }
    public string Total => F("Ticked: {0} now, about {1} after. You'd get back about {2}.",
        VideoItemViewModel.FormatSize(Rows.Where(r => r.IsChecked).Sum(r => r.Bytes)),
        VideoItemViewModel.FormatSize(Rows.Where(r => r.IsChecked).Sum(r => r.GuessBytes)),
        VideoItemViewModel.FormatSize(Rows.Where(r => r.IsChecked).Sum(r => r.Bytes - r.GuessBytes)));
    public CancellationTokenSource? Cts { get; set; }

    public ShrinkViewModel(List<ShrinkRow> rows)
    {
        Rows = new ObservableCollection<ShrinkRow>(rows);
        foreach (var r in rows) r.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ShrinkRow.IsChecked)) OnPropertyChanged(nameof(Total)); };
        Intro = rows.Count == 0
            ? T("Nothing to do. Make room looks for videos over 150 MB that aren't favourites, are over a month old and haven't been watched in three months.")
            : P(rows.Count, "{0} video is big, not a favourite and hasn't been watched in three months or more.",
                  "{0:N0} videos are big, not favourites and haven't been watched in three months or more.")
              + " " + T("Savefile can encode them again as HEVC: they look the same and take about half the space. Each original waits in the bin for 24 hours, in case.");
    }

    public void CheckAll(bool on)
    {
        foreach (var r in Rows) r.IsChecked = on;
    }
}

// Make room (Settings, Features, Make room): opened from Tools.
public sealed partial class MainViewModel
{
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsShowingShrink))] private ShrinkViewModel? _shrink;
    public bool IsShowingShrink => Shrink is not null;
    public bool ShowShrink => Features.MakeRoom;

    private List<VideoItemViewModel> ShrinkCandidates()
    {
        var now = DateTime.UtcNow;
        return _items.Values.Where(v => ShrinkService.IsCandidate(v.Entry, now)).OrderByDescending(v => v.Entry.Size).ToList();
    }

    [RelayCommand]
    private void OpenShrink()
    {
        var rows = ShrinkCandidates().Select(v => new ShrinkRow(v) { IsChecked = true }).ToList();
        Shrink = new ShrinkViewModel(rows);
    }

    [RelayCommand]
    private void CloseShrink()
    {
        if (Shrink is { IsBusy: true } s) { s.Cts?.Cancel(); return; }
        Shrink = null;
    }

    [RelayCommand] private void ShrinkCheckAll() => Shrink?.CheckAll(true);
    [RelayCommand] private void ShrinkCheckNone() => Shrink?.CheckAll(false);

    /// <summary>Shows the confirmation first; "Try one first" and the confirmed run come here too.</summary>
    [RelayCommand]
    private void AskShrink()
    {
        if (Shrink is { IsBusy: false } s && s.Rows.Any(r => r.IsChecked)) s.IsConfirming = true;
    }

    [RelayCommand] private void CancelShrinkAsk() { if (Shrink is { } s) s.IsConfirming = false; }

    [RelayCommand] private Task ShrinkOneAsync() => RunShrinkAsync(onlyOne: true);
    [RelayCommand] private Task ShrinkAllAsync() => RunShrinkAsync(onlyOne: false);

    private async Task RunShrinkAsync(bool onlyOne)
    {
        if (Shrink is not { IsBusy: false } s || ToolLocator.Ffmpeg is not { } ffmpeg) return;
        s.IsConfirming = false;
        var todo = s.Rows.Where(r => r.IsChecked && r.State.Length == 0).ToList();
        if (onlyOne) todo = todo.Take(1).ToList();
        if (todo.Count == 0) return;
        s.IsBusy = true;
        s.Cts = new CancellationTokenSource();
        var ct = s.Cts.Token;
        long saved = 0;
        int done = 0, failed = 0;
        try
        {
            s.Status = T("Finding the best way to encode on this computer…");
            var enc = await ShrinkService.PickAsync(ffmpeg);
            if (enc is null)
            {
                s.Status = T("This computer has no HEVC encoder Savefile can use, so nothing was changed.");
                return;
            }
            Log.Info($"Make room: {todo.Count} video(s) with {enc.Name}");
            using var awake = PlatformShell.StayAwake("Make room");
            foreach (var row in todo)
            {
                ct.ThrowIfCancellationRequested();
                s.Status = F("Making “{0}” smaller ({1} of {2})…", row.Name, done + failed + 1, todo.Count);
                row.Progress = 0;
                var entry = row.Item.Entry;
                try
                {
                    var newSize = await Task.Run(() => ShrinkService.ShrinkAsync(entry, enc,
                        e => _bin.Add(e),
                        item => { try { _bin.Restore(item); } catch (Exception ex) { Log.Warn($"Make room: couldn't put back {item.OriginalPath}: {ex.Message}"); } },
                        p => Avalonia.Threading.Dispatcher.UIThread.Post(() => row.Progress = p), ct), ct);
                    saved += row.Bytes - newSize;
                    entry.Size = newSize;
                    entry.VideoCodec = "hevc";
                    row.State = F("{0} → {1}", VideoItemViewModel.FormatSize(row.Bytes), VideoItemViewModel.FormatSize(newSize));
                    done++;
                }
                catch (ShrinkService.SkipException ex) { row.State = ex.Message; }
                catch (OperationCanceledException) { row.State = T("Stopped; kept as it was."); throw; }
                catch (Exception ex)
                {
                    Log.Warn($"Make room: {entry.FileName}: {ex.Message}");
                    row.State = F("Kept as it was: {0}", ex.Message);
                    failed++;
                }
                finally { row.Progress = -1; row.IsChecked = false; }
            }
        }
        catch (OperationCanceledException) { /* stopped */ }
        finally
        {
            s.IsBusy = false;
            s.Cts?.Dispose();
            s.Cts = null;
            RaiseBin();
            if (done > 0)
            {
                _library.SaveCache();
                _ = RescanAsync();
            }
            s.Status = done == 0 && failed == 0 ? s.Status
                : F("{0} smaller, about {1} saved. The originals wait in the bin for 24 hours; the space is yours once they're gone.",
                    P(done, "{0} video", "{0:N0} videos"), VideoItemViewModel.FormatSize(saved))
                  + (failed > 0 ? " " + P(failed, "{0} was kept as it was.", "{0} were kept as they were.") : "");
            Log.Info($"Make room done: {done} smaller, {failed} kept, {saved / 1048576} MB");
        }
    }

    /// <summary>Mochi mentions it when there's real room to win (2 GB or more).</summary>
    private string? ShrinkLine()
    {
        if (!Features.MakeRoom) return null;
        var win = ShrinkCandidates().Sum(v => (long)(v.Entry.Size * (1 - ShrinkService.Guess)));
        return win >= 2L * 1024 * 1024 * 1024
            ? F("Savefile could free up about {0} without losing a thing. Tools, Make room.", VideoItemViewModel.FormatSize(win))
            : null;
    }
}
