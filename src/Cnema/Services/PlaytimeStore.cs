namespace Cnema.Services;

/// <summary>
/// Hours played per game, counted like Steam does: while a game is the app in front and Savefile is running.
/// Kept on this computer only (playtime.json), and only from the moment it was switched on, which every
/// place that shows it says, so it is never mistaken for a lifetime total.
/// </summary>
public sealed class PlaytimeStore
{
    public sealed class Data
    {
        public DateTime? SinceUtc { get; set; }
        /// <summary>Seconds per game, keyed by the game's name as its app calls itself.</summary>
        public Dictionary<string, double> Seconds { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Seconds per game per year ("2026"), so a year's look back only counts that year.</summary>
        public Dictionary<string, Dictionary<string, double>> Years { get; set; } = new();
        public Dictionary<string, DateTime> LastPlayedUtc { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private static string FilePath => Path.Combine(AppPaths.DataDir, "playtime.json");
    private Data? _data;
    private DateTime _lastSave = DateTime.MinValue;

    public Data Current => _data ??= Load();

    private static Data Load()
    {
        var d = JsonStore.Load<Data>(FilePath);
        // JSON gives back case-sensitive dictionaries; a game is the same game whatever its case.
        d.Seconds = new Dictionary<string, double>(d.Seconds, StringComparer.OrdinalIgnoreCase);
        d.LastPlayedUtc = new Dictionary<string, DateTime>(d.LastPlayedUtc, StringComparer.OrdinalIgnoreCase);
        return d;
    }

    public void Start()
    {
        if (Current.SinceUtc is null) { Current.SinceUtc = DateTime.UtcNow; Save(); }
    }

    public void Add(string game, double seconds)
    {
        var d = Current;
        d.SinceUtc ??= DateTime.UtcNow;
        d.Seconds[game] = d.Seconds.GetValueOrDefault(game) + seconds;
        var year = DateTime.Now.Year.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (!d.Years.TryGetValue(year, out var y)) d.Years[year] = y = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        y[game] = y.GetValueOrDefault(game) + seconds;
        d.LastPlayedUtc[game] = DateTime.UtcNow;
        // A write every few minutes is plenty; a crash loses at most that.
        if (DateTime.UtcNow - _lastSave > TimeSpan.FromMinutes(5)) Save();
    }

    public void Save()
    {
        if (_data is null) return;
        JsonStore.Save(FilePath, _data);
        _lastSave = DateTime.UtcNow;
    }

    /// <summary>Forgets everything counted so far (Settings, Reset playtime).</summary>
    public void Reset()
    {
        _data = new Data { SinceUtc = DateTime.UtcNow };
        Save();
    }
}
