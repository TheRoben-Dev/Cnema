using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cnema.ViewModels;

// Who's talking: Mochi's "Who's that?" game (Settings, Features, Who was there, Help recognise voices).
public sealed partial class MainViewModel
{
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsShowingVoiceGame))] private VoiceGameViewModel? _voiceGame;
    public bool IsShowingVoiceGame => VoiceGame is not null;

    /// <summary>Friends whose names start with (or contain) what's typed, for "Someone else".</summary>
    internal IReadOnlyList<string> FindFriends(string typed)
    {
        var t = typed.Trim();
        return _items.Values.SelectMany(v => v.Entry.People).Select(p => p.Trim()).Where(p => p.Length > 0)
            .GroupBy(p => p, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Name: g.First(), Count: g.Count()))
            .Where(f => t.Length == 0 || f.Name.Contains(t, StringComparison.CurrentCultureIgnoreCase))
            .OrderByDescending(f => t.Length > 0 && f.Name.StartsWith(t, StringComparison.CurrentCultureIgnoreCase))
            .ThenByDescending(f => f.Count)
            .Select(f => f.Name).Take(6).ToList();
    }

    /// <summary>Opens the game with these rounds (the voice pass picks them; the preview passes made-up ones).</summary>
    internal void ShowVoiceGame(IEnumerable<VoiceRound> rounds, IEnumerable<VoiceFriend> friends)
    {
        var game = new VoiceGameViewModel(rounds, friends, FindFriends);
        VoiceGame = game;
    }

    [RelayCommand]
    private void CloseVoiceGame() => VoiceGame = null;
}
