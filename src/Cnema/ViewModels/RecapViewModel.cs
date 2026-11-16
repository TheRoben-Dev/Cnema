using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cnema.ViewModels;

/// <summary>One name with a count in the recap ("Sam · 14 clips").</summary>
public sealed record RecapRow(string Name, string Detail, double Fraction)
{
    public double Width => 6 + Fraction * 200;
    public string Initial => Name.Length > 0 ? char.ToUpper(Name[0], CultureInfo.CurrentCulture).ToString() : "?";
}

/// <summary>
/// "Your year in clips": the moments, friends and games of one year, worked out from the library.
/// Opens on the latest year with videos; earlier years are a chip away.
/// </summary>
public sealed partial class RecapViewModel : ObservableObject
{
    private readonly IReadOnlyList<VideoItemViewModel> _all;

    public ChipGroup Year { get; }
    public bool HasSeveralYears => Year.Chips.Count > 1;

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _subtitle = "";
    [ObservableProperty] private IReadOnlyList<StatTile> _tiles = Array.Empty<StatTile>();
    [ObservableProperty] private IReadOnlyList<StatBar> _months = Array.Empty<StatBar>();
    [ObservableProperty] private IReadOnlyList<RecapRow> _friends = Array.Empty<RecapRow>();
    [ObservableProperty] private IReadOnlyList<RecapRow> _games = Array.Empty<RecapRow>();
    [ObservableProperty] private string _busiest = "";
    [ObservableProperty] private VideoItemViewModel? _firstSave;
    [ObservableProperty] private string _firstSaveText = "";
    [ObservableProperty] private VideoItemViewModel? _bestDay;
    [ObservableProperty] private string _bestDayText = "";

    public bool HasFriends => Friends.Count > 0;
    public bool HasGames => Games.Count > 0;
    public bool PeopleEnabled { get; }

    public RecapViewModel(IReadOnlyList<VideoItemViewModel> all, bool peopleEnabled)
    {
        _all = all;
        PeopleEnabled = peopleEnabled;
        var years = all.Select(v => v.Entry.ModifiedUtc.ToLocalTime().Year).Distinct().OrderByDescending(y => y).Take(6).ToList();
        if (years.Count == 0) years.Add(DateTime.Now.Year);
        Year = new ChipGroup(years[0], years.Select(y => (y.ToString(CultureInfo.InvariantCulture), (object)y)).ToArray());
        Year.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ChipGroup.Value)) Build(); };
        Build();
    }

    public int SelectedYear => (int)Year.Value;

    // The two moment cards sit outside the library grid, so they hold their own thumbnails.
    partial void OnFirstSaveChanged(VideoItemViewModel? oldValue, VideoItemViewModel? newValue)
    {
        newValue?.AcquireThumbnail();
        oldValue?.ReleaseThumbnail();
    }

    partial void OnBestDayChanged(VideoItemViewModel? oldValue, VideoItemViewModel? newValue)
    {
        newValue?.AcquireThumbnail();
        oldValue?.ReleaseThumbnail();
    }

    /// <summary>Lets go of the thumbnails when the dialog closes.</summary>
    public void Release()
    {
        FirstSave = null;
        BestDay = null;
    }

    /// <summary>The year's clips, oldest first.</summary>
    public List<VideoItemViewModel> YearVideos() => _all
        .Where(v => v.Entry.ModifiedUtc.ToLocalTime().Year == SelectedYear)
        .OrderBy(v => v.Entry.ModifiedUtc).ToList();

    /// <summary>What a montage of the year uses: favourites first, topped up with clips with friends or notes.</summary>
    public List<VideoItemViewModel> HighlightVideos() => Highlights(YearVideos());

    /// <summary>The best of a set of clips for a montage, oldest first.</summary>
    public static List<VideoItemViewModel> Highlights(IEnumerable<VideoItemViewModel> clips)
    {
        var year = clips.Where(v => v.Entry.DurationMs >= 1000).ToList();
        var picks = year.Where(v => v.Entry.Favorite).ToList();
        if (picks.Count < 12)
            picks.AddRange(year.Where(v => !v.Entry.Favorite)
                .OrderByDescending(v => (v.Entry.People.Count > 0 ? 2 : 0) + (v.Entry.Smart?.Excitement ?? 0) * 3)
                .ThenByDescending(v => string.IsNullOrWhiteSpace(v.Entry.Note) ? 0 : 1)
                .ThenBy(v => v.Entry.ModifiedUtc)
                .Take(12 - picks.Count));
        return picks.OrderBy(v => v.Entry.ModifiedUtc).Take(40).ToList();
    }

    private void Build()
    {
        var culture = CultureInfo.CurrentCulture;
        var year = SelectedYear;
        var list = YearVideos();
        var isThisYear = year == DateTime.Now.Year;
        Title = F("Your {0} in clips", year);
        Subtitle = list.Count == 0 ? T("Nothing saved this year yet. Your next great moment starts the list.")
            : isThisYear ? T("So far this year. Here's what you kept.") : T("Here's what you kept that year.");

        var days = list.Select(v => v.Entry.ModifiedUtc.ToLocalTime().Date).Distinct().Count();
        var friends = list.SelectMany(v => v.Entry.People).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var tiles = new List<StatTile>
        {
            new(list.Count.ToString("N0", culture), list.Count == 1 ? T("moment kept") : T("moments kept")),
            new(Duration(list.Sum(v => v.Entry.DurationMs)), T("of memories")),
            new(days.ToString("N0", culture), days == 1 ? T("day with a save") : T("days with a save")),
        };
        if (PeopleEnabled) tiles.Add(new(friends.ToString("N0", culture), friends == 1 ? T("friend tagged") : T("friends tagged")));
        else tiles.Add(new(list.Count(v => v.Entry.Favorite).ToString("N0", culture), T("favourites")));
        Tiles = tiles;

        // Clips per month, January to December.
        var perMonth = Enumerable.Range(1, 12).Select(m => list.Count(v => v.Entry.ModifiedUtc.ToLocalTime().Month == m)).ToList();
        var max = Math.Max(1, perMonth.Max());
        Months = perMonth.Select((n, i) => new StatBar(culture.DateTimeFormat.GetAbbreviatedMonthName(i + 1), n, (double)n / max)).ToList();
        var top = perMonth.IndexOf(perMonth.Max());
        Busiest = perMonth[top] == 0 ? ""
            : F("Your busiest month was {0}, with {1}.", culture.DateTimeFormat.GetMonthName(top + 1), P(perMonth[top], "{0} save", "{0} saves"));

        Friends = PeopleEnabled ? Top(list.SelectMany(v => v.Entry.People), "{0} clip together", "{0} clips together") : Array.Empty<RecapRow>();
        Games = Top(list.SelectMany(v => v.Entry.Tags), "{0} clip", "{0} clips");

        FirstSave = list.FirstOrDefault();
        FirstSaveText = FirstSave is { } f ? F("{0}, {1}", f.Title, f.Entry.ModifiedUtc.ToLocalTime().ToString("d MMMM", culture)) : "";

        // The day with the most saves: usually a great night with friends.
        var best = list.GroupBy(v => v.Entry.ModifiedUtc.ToLocalTime().Date).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).FirstOrDefault();
        if (best is not null && best.Count() > 1)
        {
            BestDay = best.OrderByDescending(v => v.Entry.Favorite).ThenBy(v => v.Entry.ModifiedUtc).First();
            var who = best.SelectMany(v => v.Entry.People).Distinct(StringComparer.OrdinalIgnoreCase).Take(3).ToList();
            var date = best.Key.ToString("dddd d MMMM", culture);
            BestDayText = PeopleEnabled && who.Count > 0
                ? F("{0}: {1}, with {2}.", date, P(best.Count(), "{0} save", "{0} saves"), string.Join(", ", who))
                : F("{0}: {1}.", date, P(best.Count(), "{0} save", "{0} saves"));
        }
        else
        {
            BestDay = null;
            BestDayText = "";
        }
        OnPropertyChanged(nameof(HasFriends));
        OnPropertyChanged(nameof(HasGames));
    }

    private static IReadOnlyList<RecapRow> Top(IEnumerable<string> names, string one, string many)
    {
        var groups = names.GroupBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Name: g.First(), Count: g.Count()))
            .OrderByDescending(g => g.Count).ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(5).ToList();
        var max = Math.Max(1, groups.Select(g => g.Count).DefaultIfEmpty(0).Max());
        return groups.Select(g => new RecapRow(g.Name, P(g.Count, one, many), (double)g.Count / max)).ToList();
    }

    private static string Duration(long ms)
    {
        var t = TimeSpan.FromMilliseconds(ms);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes} min"
            : t.TotalMinutes >= 1 ? $"{(int)Math.Round(t.TotalMinutes)} min"
            : $"{(int)Math.Round(t.TotalSeconds)} s";
    }
}
