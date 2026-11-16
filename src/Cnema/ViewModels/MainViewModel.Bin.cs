using System.Collections.ObjectModel;
using Avalonia.Threading;
using Cnema.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cnema.ViewModels;

/// <summary>A row in the bin: the video's name and how long it has left.</summary>
public sealed partial class BinRowViewModel : ObservableObject
{
    public BinItem Item { get; }
    public string Name => Item.Details?.Title is { Length: > 0 } t ? t : System.IO.Path.GetFileNameWithoutExtension(Item.OriginalPath);
    public string Folder => System.IO.Path.GetDirectoryName(Item.OriginalPath) ?? "";
    [ObservableProperty] private string _left = "";

    public BinRowViewModel(BinItem item)
    {
        Item = item;
        Tick();
    }

    public void Tick()
    {
        var left = BinService.Keep - (DateTime.UtcNow - Item.DeletedUtc);
        Left = left <= TimeSpan.Zero ? Loc.T("Deleted for good in a moment")
            : left.TotalHours >= 1 ? Loc.F("Deleted for good in {0} h {1} min", (int)left.TotalHours, left.Minutes)
            : Loc.F("Deleted for good in {0} min", Math.Max(1, (int)Math.Ceiling(left.TotalMinutes)));
    }
}

// Savefile's bin: deleted videos wait 24 hours each before they're gone for good, and can be put back.
public sealed partial class MainViewModel
{
    private readonly BinService _bin = new();
    private DispatcherTimer? _binTimer;
    public ObservableCollection<BinRowViewModel> BinRows { get; } = new();
    [ObservableProperty] private bool _isShowingBin;

    private bool UseBin => Features.Bin;
    public int BinCount => _bin.Items.Count;
    public bool ShowBinNav => BinCount > 0;
    public string BinCountText => BinCount.ToString(System.Globalization.CultureInfo.CurrentCulture);

    private void StartBin()
    {
        _bin.Changed += () => Dispatcher.UIThread.Post(RaiseBin);
        // Each video goes on its own clock, so look often; deleting what's due is a few file deletes at most.
        _binTimer ??= new DispatcherTimer(TimeSpan.FromMinutes(5), DispatcherPriority.Background, (_, _) => ExpireBin());
        _binTimer.Start();
        ExpireBin();
        RaiseBin();
    }

    private void ExpireBin() => _ = Task.Run(() =>
    {
        try { _bin.Expire(); }
        catch (Exception ex) { Log.Warn($"Bin: {ex.Message}"); }
    });

    private void RaiseBin()
    {
        OnPropertyChanged(nameof(BinCount));
        OnPropertyChanged(nameof(ShowBinNav));
        OnPropertyChanged(nameof(BinCountText));
        if (IsShowingBin) FillBinRows();
    }

    private void FillBinRows()
    {
        BinRows.Clear();
        foreach (var item in _bin.Items) BinRows.Add(new BinRowViewModel(item));
    }

    private DispatcherTimer? _binTick;

    [RelayCommand]
    private void OpenBin()
    {
        FillBinRows();
        IsShowingBin = true;
        _binTick ??= new DispatcherTimer(TimeSpan.FromSeconds(30), DispatcherPriority.Background, (_, _) =>
        {
            if (IsShowingBin) TickBin(); else _binTick!.Stop();
        });
        _binTick.Start();
    }

    [RelayCommand] private void CloseBin() => IsShowingBin = false;

    /// <summary>Moves a video out of the library: into the bin, or to the system Trash with the bin off.</summary>
    private void RemoveVideo(VideoItemViewModel item)
    {
        if (UseBin)
        {
            _bin.Add(item.Entry);
            _library.Forget(item.Entry);
        }
        else _library.Delete(item.Entry);
        _items.Remove(item.Entry.Path);
    }

    [RelayCommand]
    private async Task RestoreFromBinAsync(BinRowViewModel? row)
    {
        if (row is null) return;
        try
        {
            var path = await Task.Run(() => _bin.Restore(row.Item));
            if (row.Item.Details is { } details) _library.Adopt(path, details);
            ShowToast(F("Put back: {0}", row.Name));
            FillBinRows();
            if (BinRows.Count == 0) IsShowingBin = false;
            await RescanAsync();
        }
        catch (Exception ex)
        {
            Log.Warn($"Bin: restore failed: {ex.Message}");
            ShowToast(F("Couldn't put it back: {0}", ex.Message));
        }
    }

    [RelayCommand]
    private async Task DeleteFromBinNowAsync(BinRowViewModel? row)
    {
        if (row is null) return;
        await Task.Run(() => _bin.DeleteNow(row.Item));
        FillBinRows();
        if (BinRows.Count == 0) IsShowingBin = false;
    }

    /// <summary>The bin's countdowns, refreshed while it's open.</summary>
    public void TickBin()
    {
        foreach (var row in BinRows) row.Tick();
    }

    // ---------- from the Quick player ----------

    /// <summary>Rename, tag or move the video that's playing, without leaving the player.</summary>
    [RelayCommand]
    private void EditPlaying()
    {
        if (Player.Current is { } item) BeginEdit(item);
    }

    /// <summary>
    /// Delete in the Quick player: the video goes to the bin (it's back with one click for 24 hours) and the
    /// next one plays. With the bin off it asks first, since the system Trash is the only way back.
    /// </summary>
    [RelayCommand]
    private async Task DeletePlayingAsync()
    {
        if (Player.Current is not { } current) return;
        if (!UseBin) { RequestDelete(current); return; }
        var i = Videos.IndexOf(current);
        var next = i >= 0 && i + 1 < Videos.Count ? Videos[i + 1] : i > 0 ? Videos[i - 1] : null;
        // Let go of the file first: a video that's open can't be moved on Windows.
        if (next is not null) await Player.OpenAsync(next);
        else
        {
            Player.Close();
            IsMiniPlayerOpen = false;
        }
        try
        {
            RemoveVideo(current);
            RefreshView();
            var msg = F("Moved to the bin: {0}. It's deleted for good in 24 hours.", current.Title);
            if (next is not null) Player.ShowToast(msg); else ShowToast(msg);
        }
        catch (Exception ex)
        {
            Log.Warn($"Delete failed: {ex.Message}");
            var msg = F("Couldn't move it to the bin: {0}", ex.Message);
            if (next is not null) Player.ShowToast(msg); else ShowToast(msg);
        }
    }
}
