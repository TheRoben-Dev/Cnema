using System.Globalization;
using Avalonia.Threading;
using Cnema.Models;
using Cnema.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cnema.ViewModels;

// The blob: Savefile's mascot sits by Worth a rewatch and says something about your clips. Every line is a
// template filled from what Savefile knows (friends, games, dates, best moments, On this day); nothing is
// generated, so it is never wrong about what happened. The lines take turns every few seconds.
public sealed partial class MainViewModel
{
    [ObservableProperty] private string _mascotLine = "";
    /// <summary>Goes up each time the blob has a new line; it hops and squints happily.</summary>
    [ObservableProperty] private int _mascotCheer;
    /// <summary>False for a moment while one line fades out and the next fades in.</summary>
    [ObservableProperty] private bool _mascotLineShown = true;

    public bool ShowMascot => Features.Mascot;
    public bool ReducedMotion => Accessibility.ReducedMotion;

    private List<string> _mascotLines = new();
    private int _mascotIndex;
    private DispatcherTimer? _mascotTimer;
    private const int MascotSeconds = 9;

    /// <summary>Rebuilds the lines (the library or the day changed) and keeps them turning while the shelf shows.</summary>
    private void RefreshMascot()
    {
        if (!Features.Mascot || _items.Count == 0)
        {
            _mascotTimer?.Stop();
            MascotLine = "";
            return;
        }
        var lines = MascotLines(DateTime.Now);
        if (lines.SequenceEqual(_mascotLines) && MascotLine.Length > 0) return;
        _mascotLines = lines;
        _mascotIndex = 0;
        MascotLine = lines.Count > 0 ? lines[0] : "";
        if (_mascotTimer is null)
        {
            _mascotTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(MascotSeconds) };
            _mascotTimer.Tick += (_, _) => NextMascotLine();
        }
        if (lines.Count > 1) _mascotTimer.Start(); else _mascotTimer.Stop();
    }

    /// <summary>A click on the blob: it hops and says the next thing.</summary>
    public void PokeMascot()
    {
        if (_mascotLines.Count < 2) { MascotCheer++; return; }
        _mascotTimer?.Stop();
        _mascotTimer?.Start(); // a full turn for the line it's about to say
        NextMascotLine(force: true);
    }

    private async void NextMascotLine(bool force = false)
    {
        // Only while it can be seen: the shelf shows on the plain library view.
        if (!force && (!ShowFeatured || _mascotLines.Count < 2 || IsSettingsOpen || Player.Current is not null)) return;
        if (_mascotLines.Count < 2) return;
        MascotLineShown = false;
        await Task.Delay(ReducedMotion ? 0 : 250);
        _mascotIndex = (_mascotIndex + 1) % _mascotLines.Count;
        MascotLine = _mascotLines[_mascotIndex];
        MascotLineShown = true;
        MascotCheer++;
    }

    // ---------- the lines ----------

    private string? GameOf(VideoEntry e)
    {
        var test = GameTest(e);
        return e.Tags.FirstOrDefault(test) is { } g ? SmartTitles.Pretty(g) : null;
    }

    /// <summary>"tonight", "last night", "yesterday", "on Tuesday", "in March", "in March 2025".</summary>
    internal static string WhenWords(DateTime local, DateTime now)
    {
        var days = (now.Date - local.Date).Days;
        var evening = local.Hour >= 18 || local.Hour < 4;
        var culture = CultureInfo.CurrentCulture;
        return days switch
        {
            0 => evening ? T("tonight") : T("today"),
            1 => evening ? T("last night") : T("yesterday"),
            < 7 => F("on {0}", culture.DateTimeFormat.GetDayName(local.DayOfWeek)),
            _ when local.Year == now.Year => F("in {0}", culture.DateTimeFormat.GetMonthName(local.Month)),
            _ => F("in {0} {1}", culture.DateTimeFormat.GetMonthName(local.Month), local.Year),
        };
    }

    /// <summary>Everything the blob could say today, the most personal first, each said once.</summary>
    internal List<string> MascotLines(DateTime now)
    {
        var lines = new List<string>();
        var all = _items.Values.Select(v => v.Entry).Where(e => e.DurationMs > 0).ToList();
        if (all.Count == 0) return lines;
        DateTime Local(VideoEntry e) => e.ModifiedUtc.ToLocalTime();
        var week = all.Where(e => (now - Local(e)).TotalDays < 7).ToList();
        var month = all.Where(e => Local(e).Year == now.Year && Local(e).Month == now.Month).ToList();
        bool Keeper(VideoEntry e) => e.Favorite || (Features.Highlights && e.Smart is { Excitement: >= 0.45 });

        // A keeper from the last few days.
        var recent = all.Where(e => (now.Date - Local(e).Date).TotalDays <= 3 && Keeper(e))
            .OrderByDescending(e => e.Smart?.Excitement ?? 0).ThenByDescending(e => e.ModifiedUtc).FirstOrDefault();
        if (recent is not null)
        {
            var when = WhenWords(Local(recent), now);
            lines.Add(GameOf(recent) is { } game ? F("Your {0} clip from {1} is a keeper.", game, when) : F("That clip from {0} is a keeper.", when));
        }

        // New moments with a friend this week.
        if (Features.People)
        {
            var friend = week.SelectMany(e => e.People.Select(p => p.Trim())).Where(p => p.Length > 0)
                .GroupBy(p => p, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).FirstOrDefault();
            if (friend is not null && friend.Count() >= 2)
                lines.Add(F("{0} new moments with {1} this week.", friend.Count(), friend.First()));
        }

        // On this day, in an earlier year.
        if (Features.OnThisDay)
        {
            var then = all.Where(e => Local(e).Year < now.Year && Local(e).Month == now.Month && Local(e).Day == now.Day)
                .OrderBy(e => e.ModifiedUtc).ToList();
            if (then.Count > 0)
            {
                var years = now.Year - Local(then[0]).Year;
                var ago = years == 1 ? T("A year ago today") : F("{0} years ago today", years);
                var game = then.Select(GameOf).FirstOrDefault(g => g is not null);
                var with = then.SelectMany(e => e.People).FirstOrDefault(p => p.Trim().Length > 0);
                lines.Add(game is not null && with is not null ? F("{0} you were playing {1} with {2}.", ago, game, with.Trim())
                    : game is not null ? F("{0} you were playing {1}.", ago, game)
                    : with is not null ? F("{0} you were with {1}.", ago, with.Trim())
                    : F("{0} you saved {1}.", ago, P(then.Count, "a clip", "{0} clips")));
            }
        }

        // The game of the month.
        var topGame = month.Select(GameOf).Where(g => g is not null).GroupBy(g => g!).OrderByDescending(g => g.Count()).FirstOrDefault();
        if (topGame is not null && topGame.Count() >= 3)
            lines.Add(F("{0} {1} clips this month. Someone's on a run.", topGame.Count(), topGame.Key));

        // A run of days with clips.
        var days = all.Select(e => Local(e).Date).ToHashSet();
        var streak = 0;
        for (var d = days.Contains(now.Date) ? now.Date : now.Date.AddDays(-1); days.Contains(d); d = d.AddDays(-1)) streak++;
        if (streak >= 3) lines.Add(F("{0} days of clips in a row. Nice streak.", streak));

        // Moments that stood out this week.
        if (Features.Highlights)
        {
            var stood = week.Count(e => e.Smart is { Excitement: >= 0.45 });
            if (stood >= 2) lines.Add(F("{0} moments stood out this week.", stood));
        }

        // An old favourite on the shelf.
        var old = Featured.Select(v => v.Entry).FirstOrDefault(e => e.Favorite && (now - Local(e)).TotalDays > 60);
        if (old is not null) lines.Add(F("Still a favourite: {0}, from {1}.", Short(old), WhenWords(Local(old), now).Replace(T("in "), "")));

        // One on the shelf that's never been rewatched.
        var never = Featured.Select(v => v.Entry).FirstOrDefault(e => e.LastPlayedUtc is null && (now - Local(e)).TotalDays > 14 && !ReferenceEquals(e, old));
        if (never is not null) lines.Add(F("You haven't rewatched “{0}” yet.", Short(never)));

        // The friend of the month.
        if (Features.People)
        {
            var mate = month.SelectMany(e => e.People.Select(p => p.Trim())).Where(p => p.Length > 0)
                .GroupBy(p => p, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).FirstOrDefault();
            if (mate is not null && mate.Count() >= 3)
            {
                var line = F("{0} is in {1} of your clips this month.", mate.First(), mate.Count());
                if (!lines.Any(l => l.Contains(mate.First(), StringComparison.Ordinal))) lines.Add(line);
            }
        }

        if (lines.Count < 2) lines.Add(T("Picked a few worth another look."));
        if (lines.Count < 2) lines.Add(T("Old favourites and nights with friends, fresh every day."));
        return lines.Distinct().ToList();
    }

    private static string Short(VideoEntry e)
    {
        var name = e.Title is { Length: > 0 } t ? t : Path.GetFileNameWithoutExtension(e.Path);
        return name.Length <= 32 ? name : name[..31].TrimEnd() + "…";
    }
}
