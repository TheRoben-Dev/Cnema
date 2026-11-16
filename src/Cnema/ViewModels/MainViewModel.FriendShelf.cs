using System.Globalization;
using Cnema.Models;
using Cnema.Services;

namespace Cnema.ViewModels;

// Friend picks: above Worth a rewatch, a row of your closest friends' names. Pick one and the shelf fills with
// the best of your clips together, and Mochi says something about the two of you. "For you" goes back to the mix.
public sealed partial class MainViewModel
{
    /// <summary>A friend needs this many clips to get a chip: fewer can't fill a shelf.</summary>
    private const int ShelfFriendMin = 3;
    private const int ShelfFriendChips = 5;

    private ChipGroup? _shelfChips;
    private string _shelfFriend = "";

    /// <summary>"For you", then the friends you have the most clips with.</summary>
    public ChipGroup? ShelfChips
    {
        get => _shelfChips;
        private set
        {
            if (_shelfChips is not null) _shelfChips.PropertyChanged -= OnShelfChip;
            _shelfChips = value;
            if (value is not null) value.PropertyChanged += OnShelfChip;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowShelfFriends));
        }
    }

    public bool ShowShelfFriends => Features.FriendShelf && Features.People && ShelfChips is { Chips.Count: > 2 };

    /// <summary>The friend the shelf is showing, or "" for everyone.</summary>
    private string ShelfFriend => Features.FriendShelf && Features.People ? _shelfFriend : "";

    private void OnShelfChip(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ChipGroup.Value) || ShelfChips?.Value is not string name || name == _shelfFriend) return;
        _shelfFriend = name;
        RefreshMemories();
        if (!Features.Mascot) return;
        // Mochi turns to the friend at once, then carries on as usual.
        _mascotTimer?.Stop();
        _mascotTimer?.Start();
        var line = name.Length == 0 ? DrawChatter() : FriendShelfLine(name, DateTime.Now);
        if (line is not null) _ = SayNowAsync(line);
    }

    private async Task SayNowAsync(string line)
    {
        MascotLineShown = false;
        await Task.Delay(ReducedMotion ? 0 : 200);
        MascotLine = line;
        MascotLineShown = true;
        MascotCheer++;
    }

    /// <summary>The friends you have the most clips with (at least three each).</summary>
    private List<string> TopShelfFriends() =>
        _items.Values.SelectMany(v => v.Entry.People.Select(p => p.Trim()).Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
            .GroupBy(p => p, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Name: g.First(), Count: g.Count()))
            .Where(f => f.Count >= ShelfFriendMin)
            .OrderByDescending(f => f.Count).ThenBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(ShelfFriendChips).Select(f => f.Name).ToList();

    /// <summary>Keeps the chips in step with the library; a friend that's gone takes the shelf back to everyone.</summary>
    private void RefreshShelfFriends()
    {
        var names = Features.FriendShelf && Features.People ? TopShelfFriends() : new();
        if (_shelfFriend.Length > 0 && !names.Contains(_shelfFriend, StringComparer.OrdinalIgnoreCase)) _shelfFriend = "";
        var current = ShelfChips?.Chips.Skip(1).Select(c => (string)c.Value).ToList();
        OnPropertyChanged(nameof(ShowShelfFriends));
        if (current is not null && current.SequenceEqual(names) && Equals(ShelfChips!.Value, _shelfFriend)) return;
        var options = new List<(string, object)> { ("For you", "") };
        options.AddRange(names.Select(n => (n, (object)n)));
        ShelfChips = new ChipGroup(_shelfFriend, options.ToArray());
    }

    private static bool With(VideoEntry e, string friend) => e.People.Any(p => string.Equals(p.Trim(), friend, StringComparison.OrdinalIgnoreCase));

    /// <summary>What Mochi says when you pick a friend: about the two of you, from your clips together.</summary>
    internal string FriendShelfLine(string friend, DateTime now)
    {
        var together = _items.Values.Select(v => v.Entry).Where(e => e.DurationMs > 0 && With(e, friend)).ToList();
        var lines = new List<string>
        {
            F(together.Count == 1 ? "You and {1}: {0} clip together. Here's the best of it." : "You and {1}: {0:N0} clips together. Here's the best of them.", together.Count, friend),
        };
        if (Features.Laughs && together.Where(e => LaughSeconds(e) >= 2).OrderByDescending(LaughSeconds).FirstOrDefault() is { } funny)
            lines.Add(F("The biggest laugh with {0} was {1}. It's on the shelf.", friend, WhenWords(funny.ModifiedUtc.ToLocalTime(), now)));
        var game = together.SelectMany(e => e.Tags.Where(GameTest(e))).GroupBy(t => t, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count()).FirstOrDefault();
        if (game is { } g && g.Count() >= 2)
            lines.Add(F("{0} is your game with {1}: {2} clips. These are the ones that stand out.", SmartTitles.Pretty(g.Key), friend, g.Count()));
        var first = together.MinBy(e => e.ModifiedUtc);
        if (first is not null && (now - first.ModifiedUtc.ToLocalTime()).TotalDays > 60)
            lines.Add(F("Your first clip with {0} is from {1}. Look how far you've come.", friend,
                first.ModifiedUtc.ToLocalTime().ToString(first.ModifiedUtc.ToLocalTime().Year == now.Year ? "d MMMM" : "MMMM yyyy", CultureInfo.CurrentCulture)));
        var heard = together.Count(e => e.Voices?.Added.Contains(friend, StringComparer.OrdinalIgnoreCase) == true);
        if (heard > 0)
            lines.Add(F(heard == 1 ? "Mochi heard {1} in {0} clip you hadn't tagged. It's in here too." : "Mochi heard {1} in {0} clips you hadn't tagged. They're in here too.", heard, friend));
        // A different one each time the chip is picked.
        return lines[_friendLineTurn++ % lines.Count];
    }

    private int _friendLineTurn = DateTime.Now.DayOfYear;

    /// <summary>A line for the mix, so the friend chips get found.</summary>
    private string? FriendShelfHint()
    {
        if (!ShowShelfFriends || _shelfFriend.Length > 0) return null;
        var name = ShelfChips!.Chips[1 + DateTime.Now.DayOfYear % (ShelfChips.Chips.Count - 1)].Value as string;
        return F("Pick {0} above the shelf for the best of your clips together.", name!);
    }
}
