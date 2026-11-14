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
    [ObservableProperty] private bool _isChecked;
}

/// <summary>The "Clean up" dialog: duplicates and junk files, with removal suggested but up to the user.</summary>
public sealed partial class CleanupViewModel : ObservableObject
{
    private readonly CancellationTokenSource _cts = new();
    public ObservableCollection<CleanupRow> Rows { get; } = new();
    [ObservableProperty] private bool _isScanning = true;
    [ObservableProperty] private string _summary = "Looking for duplicates and broken or empty files…";
    [ObservableProperty] private string _removeText = "Move to trash";
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
        var trash = OperatingSystem.IsWindows() ? "Recycle Bin" : "Trash";
        foreach (var group in found.Where(f => f.Reason == CleanupReason.Duplicate).GroupBy(f => f.Group))
        {
            var original = group.First(f => !f.Suggested).Entry;
            foreach (var f in group)
                Rows.Add(Row(f, lookup, f.Suggested ? $"Copy of “{Path.GetFileNameWithoutExtension(original.Path)}”" : "Original, kept"));
        }
        foreach (var f in found.Where(f => f.Reason != CleanupReason.Duplicate))
            Rows.Add(Row(f, lookup, f.Reason switch
            {
                CleanupReason.Empty => "Empty file",
                CleanupReason.Unreadable => "Can't be played",
                _ => "Shorter than 2 seconds",
            }));
        foreach (var r in Rows) r.PropertyChanged += (_, _) => Recount(trash);
        var dupes = found.Count(f => f.Reason == CleanupReason.Duplicate && f.Suggested);
        var junk = found.Count(f => f.Reason != CleanupReason.Duplicate);
        Summary = dupes + junk == 0
            ? "Nothing to clean up. No duplicates, and every file plays."
            : $"Found {Plural(dupes, "duplicate")} and {Plural(junk, "broken, empty or very short file")}. The ticked ones are suggested; you choose.";
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
    };

    private void Recount(string trash)
    {
        var n = Checked.Count();
        CanRemove = n > 0;
        RemoveText = n == 0 ? "Move to " + trash : $"Move {n} to {trash}";
    }

    private static string Plural(int n, string what) => n == 1 ? "1 " + what : $"{n} {what}s";
}
