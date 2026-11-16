using Avalonia.Threading;
using Cnema.Controls;
using Cnema.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cnema.ViewModels;

// Three small things that live off the library: badges for what your clips show, Mochi bringing up a
// favourite you haven't watched in a long time, and hours played per game (off unless switched on).
public sealed partial class MainViewModel
{
    // ---------- badges ----------

    private void CheckBadges()
    {
        if (!Features.Badges || _items.Count == 0) return;
        var facts = new Achievements.Facts
        {
            All = _items.Values.Select(v => v.Entry).ToList(),
            GameOf = GameOf,
            Highlights = Features.Highlights,
            Laughs = Features.Laughs,
            People = Features.People,
        };
        var earned = _settings.Current.BadgesEarned;
        var fresh = new List<Achievements.Badge>();
        foreach (var b in Achievements.All)
        {
            if (earned.ContainsKey(b.Id)) continue;
            try
            {
                if (!b.Earned(facts)) continue;
            }
            catch (Exception ex) { Log.Warn($"Badge {b.Id}: {ex.Message}"); continue; }
            earned[b.Id] = DateTime.UtcNow;
            fresh.Add(b);
        }
        var quiet = !_settings.Current.BadgesSeeded;
        if (fresh.Count == 0 && !quiet) return;
        _settings.Current.BadgesSeeded = true;
        _settings.Save();
        // What was already earned before badges existed is taken in quietly, not as a burst of toasts.
        if (quiet || fresh.Count == 0) return;
        Log.Info($"Badges earned: {string.Join(", ", fresh.Select(b => b.Id))}");
        var first = fresh[0];
        var text = fresh.Count == 1
            ? F("New badge: {0}. {1}", T(first.Name), T(first.Description))
            : F("{0} new badges, starting with {1}. Stats has them all.", fresh.Count, T(first.Name));
        if (Mochi(Features.MochiBadges)) { ShowToast(text, MochiMood.Proud, 6); BeProud(); }
        else ShowToast(text);
    }

    private IReadOnlyList<BadgeRow> BadgeRows()
    {
        if (!Features.Badges) return Array.Empty<BadgeRow>();
        var earned = _settings.Current.BadgesEarned;
        return Achievements.All
            .Select(b => earned.TryGetValue(b.Id, out var at)
                ? new BadgeRow(T(b.Name), T(b.Description), true, at.ToLocalTime().ToString("d MMM yyyy", System.Globalization.CultureInfo.CurrentCulture))
                : new BadgeRow(T(b.Name), T(b.Description), false, ""))
            .OrderByDescending(r => r.Earned).ToList();
    }

    // ---------- rewatch reminders ----------

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowNudge))] private VideoItemViewModel? _nudge;
    private string? _nudgeLine;

    /// <summary>The bubble offers to play it while it's talking about it.</summary>
    public bool ShowNudge => Nudge is not null && _nudgeLine is not null && MascotLine == _nudgeLine;

    partial void OnMascotLineChanged(string value) => OnPropertyChanged(nameof(ShowNudge));

    [RelayCommand]
    private void WatchNudge()
    {
        if (Nudge is { } item) OpenCommand.Execute(item);
    }

    /// <summary>
    /// Every few days at most, a favourite that hasn't been watched in two months or more. It stays Mochi's first
    /// line for the rest of the day (until it's watched), and the same one doesn't come back for months.
    /// </summary>
    private string? NudgeLine(DateTime now)
    {
        Nudge = null;
        _nudgeLine = null;
        if (!Features.RewatchReminders || !Features.Mascot) return null;
        var s = _settings.Current;
        static DateTime Seen(Models.VideoEntry e) => e.LastPlayedUtc ?? e.ModifiedUtc;

        VideoItemViewModel? pick = null;
        if (s.LastReminderUtc is { } last && last.ToLocalTime().Date == now.Date)
        {
            // Today's reminder, unless it has been watched since.
            var path = s.Reminded.FirstOrDefault(kv => kv.Value == last).Key;
            if (path is not null && _items.TryGetValue(path, out var today) && !(today.Entry.LastPlayedUtc > last)) pick = today;
        }
        else if (s.LastReminderUtc is not { } before || (DateTime.UtcNow - before).TotalDays >= 3)
        {
            pick = _items.Values
                .Where(v => v.Entry.Favorite && (DateTime.UtcNow - Seen(v.Entry)).TotalDays >= 60
                            && !(s.Reminded.TryGetValue(v.Entry.Path, out var r) && (DateTime.UtcNow - r).TotalDays < 120)
                            && File.Exists(v.Entry.Path))
                .OrderBy(v => Seen(v.Entry)).FirstOrDefault();
            if (pick is not null)
            {
                var at = DateTime.UtcNow;
                s.Reminded[pick.Entry.Path] = at;
                s.LastReminderUtc = at;
                _settings.Save();
            }
        }
        if (pick is null) return null;
        _nudgeLine = F("Remember “{0}”? It's been a while since you watched it.", Short(pick.Entry));
        Nudge = pick;
        OnPropertyChanged(nameof(ShowNudge));
        return _nudgeLine;
    }

    // ---------- playtime ----------

    private readonly PlaytimeStore _playtime = new();
    private DispatcherTimer? _playTimer;
    private DateTime _playTick;

    private void UpdatePlaytime()
    {
        if (!Features.Playtime)
        {
            if (_playTimer is { IsEnabled: true }) { _playTimer.Stop(); _playtime.Save(); }
            return;
        }
        _playtime.Start();
        if (_playTimer is null)
        {
            _playTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
            _playTimer.Tick += async (_, _) => await CountPlaytimeAsync();
        }
        _playTick = DateTime.UtcNow;
        _playTimer.Start();
    }

    /// <summary>Once a minute: which app is in front, and is it a game Savefile knows?</summary>
    private async Task CountPlaytimeAsync()
    {
        var now = DateTime.UtcNow;
        // A sleeping computer doesn't tick; never count more than the minute that just went by.
        var seconds = Math.Min(75, (now - _playTick).TotalSeconds);
        _playTick = now;
        try
        {
            var front = await Task.Run(PlatformShell.FrontmostAppName);
            if (front is null || !IsGameName(front)) return;
            _playtime.Add(front, seconds);
        }
        catch (Exception ex) { Log.Warn($"Playtime: {ex.Message}"); }
    }

    private bool IsGameName(string name) =>
        WellKnownGames.Contains(name)
        || _settings.Current.GameTags.Contains(name, StringComparer.OrdinalIgnoreCase)
        || _settings.Current.Clip.Profiles.Any(p => string.Equals(p.Game.Trim(), name, StringComparison.OrdinalIgnoreCase));

    private (IReadOnlyList<PlayBar> Bars, string Since) PlaytimeRows()
    {
        if (!Features.Playtime || _playtime.Current.SinceUtc is not { } since) return (Array.Empty<PlayBar>(), "");
        var top = _playtime.Current.Seconds.Where(kv => kv.Value >= 60).OrderByDescending(kv => kv.Value).Take(8).ToList();
        var max = top.Count == 0 ? 1 : top[0].Value;
        var bars = top.Select(kv => new PlayBar(kv.Key, PlayText(kv.Value), kv.Value / max)).ToList();
        return (bars, F("Counted on this computer since you turned on playtime, {0}.", since.ToLocalTime().ToString("d MMMM yyyy", System.Globalization.CultureInfo.CurrentCulture)));
    }

    internal static string PlayText(double seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1
            ? t.Minutes == 0 ? F("{0} h", (int)t.TotalHours) : F("{0} h {1} min", (int)t.TotalHours, t.Minutes)
            : F("{0} min", (int)t.TotalMinutes);
    }

    [RelayCommand]
    private void ResetPlaytime()
    {
        _playtime.Reset();
        ShowToast(T("Playtime starts counting again from now."));
    }

    /// <summary>On quit: the last few minutes are written down too.</summary>
    internal void SavePlaytime() => _playtime.Save();
}

public sealed record BadgeRow(string Name, string Description, bool Earned, string When)
{
    public double Faded => Earned ? 1 : 0.6;
}

/// <summary>Hours for one game in Stats; <see cref="Fraction"/> is 0..1 of the most played.</summary>
public sealed record PlayBar(string Label, string Text, double Fraction)
{
    public double Width => 6 + Fraction * 260;
}
