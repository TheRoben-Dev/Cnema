using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cnema.ViewModels;

/// <summary>One of Mochi's guesses for a voice, best first: a friend chip with how sure it is.</summary>
public sealed class VoiceGuess(string name, double confidence)
{
    public string Name { get; } = name;
    public double Confidence { get; } = confidence;
    public string Hint => Confidence >= 0.75 ? T("pretty sure") : Confidence >= 0.5 ? T("maybe") : T("a hunch");
    /// <summary>The little bar under the name, 6 to 46 pixels.</summary>
    public double Bar => 6 + Math.Clamp(Confidence, 0, 1) * 40;
}

/// <summary>A short bit of one voice to listen to: where it is, what was said, and who Mochi thinks it is.</summary>
public sealed class VoiceRound
{
    public required string Path { get; init; }
    public long StartMs { get; init; }
    public long EndMs { get; init; }
    /// <summary>The group of similar voices it stands for; an answer counts for all of them.</summary>
    public string Cluster { get; init; } = "";
    public string Quote { get; init; } = "";
    public bool HasQuote => Quote.Length > 0;
    public string Context { get; init; } = "";
    /// <summary>Loudness bars for the little waveform, 0 to 1.</summary>
    public IReadOnlyList<double> Levels { get; init; } = Array.Empty<double>();
    public IReadOnlyList<double> Bars => Levels.Select(l => 4 + Math.Clamp(l, 0, 1) * 30).ToList();
    public IReadOnlyList<VoiceGuess> Guesses { get; init; } = Array.Empty<VoiceGuess>();
    public bool HasGuesses => Guesses.Count > 0;
}

/// <summary>How well Mochi knows one friend's voice: a few answers make a voice, more make it sure.</summary>
public sealed partial class VoiceFriend : ObservableObject
{
    public const int Enough = 5;
    public string Name { get; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Fill), nameof(State))] private int _count;
    public VoiceFriend(string name, int count) { Name = name; _count = count; }
    public double Fill => Math.Min(1, Count / (double)Enough);
    public string State => Count >= Enough ? T("knows this voice") : Count == 0 ? T("not heard yet") : T("learning");
}

public enum VoiceAnswerKind { Friend, Me, Skip, Ignore }

public sealed record VoiceAnswer(VoiceRound Round, VoiceAnswerKind Kind, string? Name);

/// <summary>
/// "Who's that?", Mochi's little game for learning voices: a few rounds of a short snippet each, and you pick who
/// it is from Mochi's best guesses (or name someone new). Answers go to <see cref="Answered"/> as you give them
/// and can be taken back with Undo; skipping is always fine.
/// </summary>
public sealed partial class VoiceGameViewModel : ObservableObject
{
    private readonly List<VoiceAnswer> _answers = new();
    private readonly Func<string, IReadOnlyList<string>> _findFriends;

    public ObservableCollection<VoiceRound> Rounds { get; }
    public ObservableCollection<VoiceFriend> Friends { get; }
    public event Action<VoiceAnswer>? Answered;
    public event Action<VoiceAnswer>? Undone;
    public event Action<VoiceRound>? PlayRequested;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Current), nameof(RoundText), nameof(IsDone), nameof(IsPlayingRound), nameof(Progress), nameof(Mood))]
    private int _index;
    [ObservableProperty] private string _mochiLine = "";
    [ObservableProperty] private int _cheer;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Mood))] private bool _isPlaying;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsChoosing))] private bool _isNaming;
    [ObservableProperty] private string _nameText = "";
    [ObservableProperty] private IReadOnlyList<string> _nameMatches = Array.Empty<string>();
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanUndo))] private int _answerCount;

    public VoiceGameViewModel(IEnumerable<VoiceRound> rounds, IEnumerable<VoiceFriend> friends, Func<string, IReadOnlyList<string>> findFriends)
    {
        Rounds = new ObservableCollection<VoiceRound>(rounds);
        Friends = new ObservableCollection<VoiceFriend>(friends);
        _findFriends = findFriends;
        Friends.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasFriends));
        _mochiLine = Rounds.Count == 0 ? T("Mochi hasn't heard any new voices yet. Come back after a few more clips with friends.")
            : T("Listen, then tell Mochi who's talking. Skip any you're not sure about.");
    }

    public VoiceRound? Current => Index < Rounds.Count ? Rounds[Index] : null;
    public bool IsDone => Index >= Rounds.Count;
    public bool IsPlayingRound => !IsDone;
    public bool IsChoosing => !IsNaming;
    public bool HasFriends => Friends.Count > 0;
    public bool CanUndo => AnswerCount > 0;
    public string RoundText => IsDone ? "" : F("{0} of {1}", Index + 1, Rounds.Count);
    public double Progress => Rounds.Count == 0 ? 1 : Index / (double)Rounds.Count;
    public Controls.MochiMood Mood => IsDone ? Controls.MochiMood.Proud : Controls.MochiMood.Listening;

    public string DoneText
    {
        get
        {
            var named = _answers.Where(a => a.Kind is VoiceAnswerKind.Friend or VoiceAnswerKind.Me)
                .Select(a => a.Kind == VoiceAnswerKind.Me ? "\u0001me" : a.Name!.ToLowerInvariant()).Distinct().Count();
            return named == 0 ? T("No answers this time, and that's fine. Mochi will have new voices for you later.")
                : P(named, "Thanks! Mochi got to know {0} voice better. Who was there gets filled in for the clips it's sure about.",
                    "Thanks! Mochi got to know {0} voices better. Who was there gets filled in for the clips it's sure about.");
        }
    }

    [RelayCommand] private void Play() { if (Current is { } r) PlayRequested?.Invoke(r); }

    [RelayCommand]
    private void Pick(string name)
    {
        if (Current is not { } r || string.IsNullOrWhiteSpace(name)) return;
        Give(new VoiceAnswer(r, VoiceAnswerKind.Friend, name.Trim()), F("Got it, that's {0}!", name.Trim()));
        var friend = Friends.FirstOrDefault(f => string.Equals(f.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
        if (friend is null) Friends.Add(friend = new VoiceFriend(name.Trim(), 0));
        friend.Count++;
    }

    [RelayCommand] private void Me() { if (Current is { } r) Give(new VoiceAnswer(r, VoiceAnswerKind.Me, null), T("That's you! Mochi will know your voice.")); }
    [RelayCommand] private void Skip() { if (Current is { } r) Give(new VoiceAnswer(r, VoiceAnswerKind.Skip, null), T("No problem, next one.")); }
    [RelayCommand] private void Ignore() { if (Current is { } r) Give(new VoiceAnswer(r, VoiceAnswerKind.Ignore, null), T("Okay, Mochi won't ask about that one again.")); }

    [RelayCommand]
    private void SomeoneElse()
    {
        NameText = "";
        NameMatches = _findFriends("");
        IsNaming = true;
    }

    [RelayCommand] private void CancelName() => IsNaming = false;

    [RelayCommand]
    private void ConfirmName()
    {
        if (NameText.Trim().Length == 0) return;
        IsNaming = false;
        Pick(NameText);
    }

    partial void OnNameTextChanged(string value) => NameMatches = _findFriends(value);

    [RelayCommand]
    private void Undo()
    {
        if (_answers.Count == 0) return;
        var last = _answers[^1];
        _answers.RemoveAt(_answers.Count - 1);
        AnswerCount = _answers.Count;
        if (last.Kind == VoiceAnswerKind.Friend && Friends.FirstOrDefault(f => string.Equals(f.Name, last.Name, StringComparison.OrdinalIgnoreCase)) is { } f)
            f.Count = Math.Max(0, f.Count - 1);
        Index = Math.Max(0, Index - 1);
        Undone?.Invoke(last);
        MochiLine = T("Taken back. Have another listen.");
        OnPropertyChanged(nameof(DoneText));
    }

    private void Give(VoiceAnswer answer, string line)
    {
        _answers.Add(answer);
        AnswerCount = _answers.Count;
        Answered?.Invoke(answer);
        IsNaming = false;
        IsPlaying = false;
        MochiLine = line;
        if (answer.Kind is VoiceAnswerKind.Friend or VoiceAnswerKind.Me) Cheer++;
        Index++;
        if (IsDone) MochiLine = DoneText;
        OnPropertyChanged(nameof(DoneText));
        if (!IsDone) PlayRequested?.Invoke(Rounds[Index]); // the next voice plays by itself
    }
}
