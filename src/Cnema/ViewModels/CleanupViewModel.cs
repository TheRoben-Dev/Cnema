using System.Collections.ObjectModel;
using Cnema.Models;
using Cnema.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cnema.ViewModels;

public sealed partial class CleanupRow : ObservableObject
{
    public VideoItemViewModel? Item { get; init; }
    public string Title { get; init; } = "";
    public string Details { get; init; } = "";
    public string Reason { get; init; } = "";
    public bool IsKept { get; init; }
    /// <summary>Copies of one video share a group; -1 for broken or empty files.</summary>
    public int Group { get; init; } = -1;
    [ObservableProperty] private bool _isChecked;
}

/// <summary>The "Clean up" dialog: duplicates and junk files, with removal suggested but up to the user.</summary>
public sealed partial class CleanupViewModel : ObservableObject
{
    private readonly CancellationTokenSource _cts = new();
    public ObservableCollection<CleanupRow> Rows { get; } = new();
    [ObservableProperty] private bool _isScanning = true;
    [ObservableProperty] private string _summary = T("Looking for duplicates and broken or empty files…");
    [ObservableProperty] private string _removeText = T("Move to trash");
    [ObservableProperty] private bool _canRemove;

    public IEnumerable<CleanupRow> Checked => Rows.Where(r => r.IsChecked && r.Item is not null);

    public void Cancel() => _cts.Cancel();

    public async Task ScanAsync(IReadOnlyCollection<VideoEntry> entries, Func<string, VideoItemViewModel?> lookup)
    {
        List<CleanupFinding> found;
        try
        {
            found = await CleanupService.FindAsync(entries, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        var trash = T(OperatingSystem.IsWindows() ? "Recycle Bin" : "Trash");
        foreach (var group in found.Where(f => f.Reason == CleanupReason.Duplicate).GroupBy(f => f.Group))
        {
            var original = group.First(f => !f.Suggested).Entry;
            foreach (var f in group)
                Rows.Add(Row(f, lookup, f.Suggested ? F("Copy of “{0}”", Path.GetFileNameWithoutExtension(original.Path)) : T("Original, kept")));
        }
        foreach (var f in found.Where(f => f.Reason != CleanupReason.Duplicate))
            Rows.Add(Row(f, lookup, f.Reason switch
            {
                CleanupReason.Empty => T("Empty file"),
                CleanupReason.Unreadable => T("Can't be played"),
                _ => T("Shorter than 2 seconds"),
            }));
        foreach (var r in Rows) r.PropertyChanged += (_, _) => Recount(trash);
        var dupes = found.Count(f => f.Reason == CleanupReason.Duplicate && f.Suggested);
        var junk = found.Count(f => f.Reason != CleanupReason.Duplicate);
        Summary = dupes + junk == 0
            ? T("Nothing to clean up. No duplicates, and every file plays.")
            : F("Found {0} and {1}. The ticked ones are suggested; you choose.",
                P(dupes, "{0} duplicate", "{0} duplicates"), P(junk, "{0} broken, empty or very short file", "{0} broken, empty or very short files"));
        IsScanning = false;
        Recount(trash);
    }

    private static CleanupRow Row(CleanupFinding f, Func<string, VideoItemViewModel?> lookup, string reason) => new()
    {
        Item = lookup(f.Entry.Path),
        Title = Path.GetFileName(f.Entry.Path),
        Details = Path.GetDirectoryName(f.Entry.Path) + "  ·  " + VideoItemViewModel.FormatSize(f.Entry.Size),
        Reason = reason,
        IsKept = !f.Suggested,
        IsChecked = f.Suggested,
        Group = f.Reason == CleanupReason.Duplicate ? f.Group : -1,
    };

    private void Recount(string trash)
    {
        var n = Checked.Count();
        CanRemove = n > 0;
        RemoveText = n == 0 ? F("Move to {0}", trash) : F("Move {0} to {1}", n, trash);
    }
}
