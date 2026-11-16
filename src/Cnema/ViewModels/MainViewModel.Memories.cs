using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cnema.Services;

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
                if (Features.Highlights && v.Entry.Smart is { } sm) score += sm.Excitement * 3; // a moment that stands out
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
        OnPropertyChanged(nameof(ShowRecapBanner));
        OnPropertyChanged(nameof(ShowMergeSuggestion));
        OnPropertyChanged(nameof(RecapBannerTitle));
        OnPropertyChanged(nameof(RecapBannerText));
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
        // A friend's page: how long you've been saving moments together, and what you mostly play.
        if (ActiveNav is { Kind: NavKind.Person } && Videos.Count > 0 && SearchText.Trim().Length == 0 && !HasFilters)
        {
            var together = P(Videos.Count, "{0:N0} moment together", "{0:N0} moments together");
            var since = Videos.Min(v => v.Entry.ModifiedUtc).ToLocalTime();
            var text = (DateTime.Now - since).TotalDays > 31
                ? F("{0} since {1}", together, since.ToString("MMMM yyyy", System.Globalization.CultureInfo.CurrentCulture))
                : together;
            var game = Videos.SelectMany(v => v.Entry.Tags).GroupBy(t => t, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count()).FirstOrDefault();
            return game is not null && game.Count() * 2 >= Videos.Count ? F("{0} · mostly {1}", text, game.First()) : text;
        }
        if (!IsPlainLibraryView || Videos.Count == 0)
            return Videos.Count == 1 ? T("1 video") : F("{0:N0} videos", Videos.Count);
        var first = _items.Values.Min(v => v.Entry.ModifiedUtc).ToLocalTime();
        var count = P(Videos.Count, "{0:N0} moment kept", "{0:N0} moments kept");
        return (DateTime.Now - first).TotalDays > 31
            ? F("{0} since {1}", count, first.ToString("MMMM yyyy", System.Globalization.CultureInfo.CurrentCulture))
            : count;
    }

    // ---------- year in clips ----------
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsShowingRecap))] private RecapViewModel? _recap;
    partial void OnRecapChanged(RecapViewModel? oldValue, RecapViewModel? newValue) => oldValue?.Release();
    public bool IsShowingRecap => Recap is not null;
    public bool ShowRecap => Features.Recap;
    public bool ShowRecapMontage => Features.Montage;

    [RelayCommand]
    private void OpenRecap() => Recap = new RecapViewModel(_items.Values.ToList(), Features.People);

    /// <summary>The year the season's banner is about: this year in December, last year in early January.</summary>
    private static int RecapSeasonYear => DateTime.Now.Month == 12 ? DateTime.Now.Year : DateTime.Now.Year - 1;
    private static bool IsRecapSeason => DateTime.Now is { Month: 12 } or { Month: 1, Day: <= 15 };

    /// <summary>From December to mid-January, a card on All videos says the year's look back is ready.</summary>
    public bool ShowRecapBanner => Features.Recap && IsRecapSeason && IsPlainLibraryView &&
                                   _settings.Current.RecapBannerClosed != RecapSeasonYear &&
                                   _items.Values.Count(v => v.Entry.ModifiedUtc.ToLocalTime().Year == RecapSeasonYear) >= 5;
    public string RecapBannerTitle => F("Your {0} in clips is ready", RecapSeasonYear);
    public string RecapBannerText => F("{0} with your friends. Take a look back.",
        P(_items.Values.Count(v => v.Entry.ModifiedUtc.ToLocalTime().Year == RecapSeasonYear), "{0:N0} moment", "{0:N0} moments"));

    [RelayCommand]
    private void OpenRecapFromBanner()
    {
        OpenRecap();
        if (Recap is { } r && r.Year.Chips.Any(c => Equals(c.Value, RecapSeasonYear))) r.Year.Value = RecapSeasonYear;
    }

    [RelayCommand]
    private void CloseRecapBanner()
    {
        _settings.Current.RecapBannerClosed = RecapSeasonYear;
        _settings.Save();
        OnPropertyChanged(nameof(ShowRecapBanner));
    }

    [RelayCommand] private void CloseRecap() => Recap = null;

    /// <summary>Set by the window: draws the open Year in clips card into a PNG at the given path.</summary>
    public Func<string, bool>? CaptureRecap { get; set; }

    /// <summary>Year in clips as a picture, to post in the group chat.</summary>
    [RelayCommand]
    private void SaveRecapImage()
    {
        if (Recap is not { } r || CaptureRecap is null) return;
        var name = F("Savefile {0} in clips", r.SelectedYear);
        foreach (var dir in new[] { Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), _settings.Current.Clip.ResolvedFolder })
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
            var target = Path.Combine(dir, name + ".png");
            for (var n = 2; File.Exists(target); n++) target = Path.Combine(dir, $"{name} {n}.png");
            bool ok;
            try { ok = CaptureRecap(target); }
            catch (Exception ex) { Log.Warn($"Recap image failed: {ex.Message}"); ok = false; }
            if (!ok || !File.Exists(target)) continue;
            ShowToast(F("Saved {0} in {1}", Path.GetFileName(target), Path.GetFileName(dir)));
            PlatformShell.Reveal(target);
            return;
        }
        ShowToast(T("Couldn't save the picture."));
    }

    [RelayCommand]
    private async Task WatchRecapClipAsync(VideoItemViewModel? item)
    {
        if (item is null) return;
        Recap = null;
        await OpenAsync(item);
    }

    [RelayCommand]
    private void MakeRecapMontage()
    {
        if (Recap is not { } r) return;
        var videos = r.HighlightVideos();
        var label = F("{0} highlights", r.SelectedYear);
        Recap = null;
        OpenMontageWith(videos, label);
    }

    /// <summary>On a friend's page: a montage of the best moments together.</summary>
    public bool ShowFriendMontage => Features.Montage && ActiveNav is { Kind: NavKind.Person } && !IsSelecting &&
                                     Videos.Count(v => v.Entry.DurationMs >= 1000) >= 2;

    [RelayCommand]
    private void MakeFriendMontage()
    {
        if (ActiveNav is not { Kind: NavKind.Person } friend) return;
        var videos = RecapViewModel.Highlights(Videos);
        if (videos.Count == 0) return;
        OpenMontageWith(videos, F("Moments with {0}", friend.Name));
    }

    // ---------- playing with ----------
    /// <summary>Friends to tag on the next saves, per path, until the library picks the file up.</summary>
    private readonly Dictionary<string, List<string>> _pendingPeople = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The "Playing with" line in the clipping box shows while clipping is on.</summary>
    public bool ShowPlayingWith => Features.People && IsClipEnabled;
    public bool HasSquad => Features.People && _settings.Current.PlayingWith.Count > 0;
    public string PlayingWithLabel => HasSquad
        ? F("Playing with {0}", JoinNames(_settings.Current.PlayingWith))
        : T("Playing with someone? Tag them on every save");
    [ObservableProperty] private string _squadText = "";
    public List<string> SquadSuggestions => AllPeople.Take(8).ToList();

    private static string JoinNames(IReadOnlyList<string> names) => names.Count switch
    {
        0 => "",
        1 => names[0],
        2 => F("{0} and {1}", names[0], names[1]),
        3 => F("{0}, {1} and {2}", names[0], names[1], names[2]),
        _ => F("{0}, {1} and {2} more", names[0], names[1], names.Count - 2),
    };

    private void QueueSquad(string path)
    {
        if (!HasSquad) return;
        _pendingPeople[path] = _settings.Current.PlayingWith.ToList();
        // The folder watcher may already have added the file; tag it now in that case.
        if (_items.TryGetValue(path, out var existing)) Upsert(existing.Entry);
    }

    /// <summary>Fills the box when the flyout opens.</summary>
    [RelayCommand]
    private void EditSquad()
    {
        SquadText = string.Join(", ", _settings.Current.PlayingWith);
        OnPropertyChanged(nameof(SquadSuggestions));
    }

    [RelayCommand]
    private void AddToSquad(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        var names = SquadText.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(n => n.Trim()).Where(n => n.Length > 0).ToList();
        if (!names.Contains(name, StringComparer.OrdinalIgnoreCase)) names.Add(name);
        SquadText = string.Join(", ", names);
    }

    [RelayCommand]
    private void SaveSquad()
    {
        var known = AllPeople;
        _settings.Current.PlayingWith = SquadText
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(n => n.Trim().TrimStart('@').Trim())
            .Where(n => n.Length is > 0 and <= 40)
            .Select(n => known.FirstOrDefault(p => string.Equals(p, n, StringComparison.OrdinalIgnoreCase)) ?? n)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToList();
        _settings.Save();
        RaiseSquad();
    }

    [RelayCommand]
    private void ClearSquad()
    {
        SquadText = "";
        _settings.Current.PlayingWith.Clear();
        _settings.Save();
        RaiseSquad();
    }

    private void RaiseSquad()
    {
        OnPropertyChanged(nameof(HasSquad));
        OnPropertyChanged(nameof(PlayingWithLabel));
        OnPropertyChanged(nameof(ShowPlayingWith));
    }
}
