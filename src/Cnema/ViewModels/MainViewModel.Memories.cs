using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cnema.ViewModels;

// The sentimental side of Savefile: "Worth a rewatch", "On this day", who was there, and milestones.
public sealed partial class MainViewModel
{
    // ---------- worth a rewatch ----------
    /// <summary>A handful of clips to rediscover: older favourites, clips with friends, ones not seen in a while.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowFeatured))]
    private ObservableCollection<VideoItemViewModel> _featured = new();

    // The row and the memory card sit outside the virtualized grid, so they hold their own thumbnails.
    partial void OnFeaturedChanged(ObservableCollection<VideoItemViewModel>? oldValue, ObservableCollection<VideoItemViewModel> newValue)
    {
        foreach (var v in newValue) v.AcquireThumbnail();
        if (oldValue is not null) foreach (var v in oldValue) v.ReleaseThumbnail();
    }

    partial void OnMemoryItemChanged(VideoItemViewModel? oldValue, VideoItemViewModel? newValue)
    {
        newValue?.AcquireThumbnail();
        oldValue?.ReleaseThumbnail();
    }

    /// <summary>The row only shows on All videos with no search or filters, in a library big enough to need it.</summary>
    public bool ShowFeatured => Features.Rewatch && Featured.Count >= 3 && IsPlainLibraryView;

    private bool IsPlainLibraryView =>
        ActiveNav is null or { IsAll: true } && SearchText.Trim().Length == 0 && !HasFilters && !IsSelecting;

    private const int FeaturedCount = 10;

    /// <summary>
    /// Picks today's rewatch row. The pick is seeded by the date, so it stays put for the day
    /// and changes tomorrow, instead of reshuffling every time the library refreshes.
    /// </summary>
    private List<VideoItemViewModel> PickFeatured()
    {
        if (!Features.Rewatch || _items.Count < 8) return new();
        var now = DateTime.Now;
        var rng = new Random(now.Year * 1000 + now.DayOfYear);
        return _items.Values
            .Where(v => v.Entry.DurationMs > 0)
            .Select(v =>
            {
                var age = (now - v.Entry.ModifiedUtc.ToLocalTime()).TotalDays;
                var sinceSeen = v.Entry.LastPlayedUtc is { } seen ? (now - seen.ToLocalTime()).TotalDays : age;
                double score = rng.NextDouble() * 2.5;            // a little luck, so it isn't always the same ten
                if (v.Entry.Favorite) score += 3;
                if (v.Entry.People.Count > 0) score += 1.5;       // clips with friends in them
                if (!string.IsNullOrWhiteSpace(v.Entry.Note)) score += 1;
                if (age > 14) score += 1;                         // rediscovery, not what you saved an hour ago
                if (age > 180) score += 0.5;
                if (sinceSeen > 30) score += 1;
                if (age < 2) score -= 2;
                return (v, score);
            })
            .OrderByDescending(x => x.score)
            .Take(FeaturedCount)
            .Select(x => x.v)
            .ToList();
    }

    // ---------- on this day ----------
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowMemory))] private VideoItemViewModel? _memoryItem;
    [ObservableProperty] private string _memoryTitle = "";
    private DateTime? _memoryDismissedOn;

    public bool ShowMemory => Features.OnThisDay && MemoryItem is not null && IsPlainLibraryView;

    /// <summary>
    /// A clip from this day in an earlier year ("One year ago today"), or failing that from this
    /// day last month. Favourites and clips with friends win when there are several.
    /// </summary>
    private (VideoItemViewModel? Item, string Title) PickMemory()
    {
        var today = DateTime.Now.Date;
        if (!Features.OnThisDay || _memoryDismissedOn == today) return (null, "");
        static double Weight(VideoItemViewModel v) => (v.Entry.Favorite ? 2 : 0) + (v.Entry.People.Count > 0 ? 1 : 0);

        var earlierYears = _items.Values
            .Select(v => (v, d: v.Entry.ModifiedUtc.ToLocalTime().Date))
            .Where(x => x.d.Year < today.Year && x.d.Month == today.Month && x.d.Day == today.Day)
            .OrderByDescending(x => Weight(x.v)).ThenBy(x => x.d)
            .FirstOrDefault();
        if (earlierYears.v is not null)
        {
            var years = today.Year - earlierYears.d.Year;
            return (earlierYears.v, years == 1 ? T("One year ago today") : F("{0} years ago today", years));
        }

        var lastMonth = today.AddMonths(-1);
        var month = _items.Values
            .Where(v => v.Entry.ModifiedUtc.ToLocalTime().Date == lastMonth)
            .OrderByDescending(Weight)
            .FirstOrDefault();
        return month is null ? (null, "") : (month, T("One month ago today"));
    }

    [RelayCommand]
    private void DismissMemory()
    {
        _memoryDismissedOn = DateTime.Now.Date;
        MemoryItem = null;
    }

    /// <summary>Re-picks the rewatch row and the memory card; called whenever the library view refreshes.</summary>
    private void RefreshMemories()
    {
        var (item, title) = PickMemory();
        // The clip on the memory card isn't repeated in the row under it.
        var picked = PickFeatured().Where(v => !ReferenceEquals(v, item)).ToList();
        if (!picked.SequenceEqual(Featured)) Featured = new ObservableCollection<VideoItemViewModel>(picked);
        MemoryTitle = title.ToUpper(System.Globalization.CultureInfo.CurrentCulture);
        if (!ReferenceEquals(item, MemoryItem)) MemoryItem = item;
        OnPropertyChanged(nameof(ShowFeatured));
        OnPropertyChanged(nameof(ShowMemory));
    }

    // ---------- who was there ----------
    public bool ShowPeople => Features.People;

    /// <summary>Everyone tagged on any video, most often tagged first.</summary>
    public List<string> AllPeople => _items.Values
        .SelectMany(v => v.Entry.People)
        .GroupBy(n => n, StringComparer.OrdinalIgnoreCase)
        .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase)
        .Select(g => g.First())
        .ToList();

    // ---------- friendlier words ----------
    /// <summary>What the toast says when the hotkey saves a clip; one of these, picked at random.</summary>
    private static readonly string[] SavedLines =
    {
        "Saved. That one's a keeper.",
        "Saved. You'll want to see that again.",
        "Saved. Nobody can say it didn't happen.",
        "Saved for the group chat.",
        "Saved. Future you says thanks.",
    };

    private static readonly int[] Milestones = { 1, 10, 25, 50, 100, 250, 500, 1000, 2500, 5000, 10000 };

    /// <summary>The toast for a saved clip: a milestone when one is reached, otherwise a friendly line.</summary>
    private string SavedToastText(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var n = ++_settings.Current.ClipsSaved;
        _settings.Save();
        if (Features.Milestones && Milestones.Contains(n))
            return n == 1 ? T("Your first save. Plenty more to come.") : F("{0:N0} moments saved. Here's to the next {0:N0}.", n);
        return F("{0} · {1}", T(SavedLines[Random.Shared.Next(SavedLines.Length)]), name);
    }

    /// <summary>"214 moments kept since March 2025" on All videos; a plain count everywhere else.</summary>
    private string FriendlyCount()
    {
        if (!IsPlainLibraryView || Videos.Count == 0)
            return Videos.Count == 1 ? T("1 video") : F("{0:N0} videos", Videos.Count);
        var first = _items.Values.Min(v => v.Entry.ModifiedUtc).ToLocalTime();
        var count = P(Videos.Count, "{0:N0} moment kept", "{0:N0} moments kept");
        return (DateTime.Now - first).TotalDays > 31
            ? F("{0} since {1}", count, first.ToString("MMMM yyyy", System.Globalization.CultureInfo.CurrentCulture))
            : count;
    }
}
