using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cnema.Services;

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
/// "Who's that?", Mochi's little game for learning voices: one short snippet at a time, and you pick who it is from
/// Mochi's best guesses (or name someone new). It goes on for as long as you like: more voices are fetched while
/// you play (<see cref="MoreWanted"/>), until Mochi has asked about every voice it has. Answers go to
/// <see cref="Answered"/> as you give them and can be taken back with Undo; skipping is always fine. With points
/// on, answers earn points, levels and badges (<see cref="VoiceGameProgress"/>).
/// </summary>
public sealed partial class VoiceGameViewModel : ObservableObject
{
    private sealed record Given(VoiceAnswer Answer, VoiceStore.VoiceGameStats Before, int Streak, int Session, int Points);

    private readonly List<Given> _answers = new();
    private readonly Func<string, IReadOnlyList<string>> _findFriends;
    private readonly Action<VoiceStore.VoiceGameStats>? _saveStats;
    private VoiceStore.VoiceGameStats _stats;
    private int _streak, _session;

    public ObservableCollection<VoiceRound> Rounds { get; }
    public ObservableCollection<VoiceFriend> Friends { get; }
    public event Action<VoiceAnswer>? Answered;
    public event Action<VoiceAnswer>? Undone;
    public event Action<VoiceRound>? PlayRequested;
    /// <summary>The rounds are about to run out: find more and hand them to <see cref="AddRounds"/>.</summary>
    public event Action? MoreWanted;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Current), nameof(RoundText), nameof(IsDone), nameof(IsPlayingRound), nameof(IsWaiting), nameof(Mood))]
    private int _index;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsDone), nameof(IsWaiting), nameof(Mood))] private bool _outOfVoices;
    [ObservableProperty] private bool _isLoadingMore;
    [ObservableProperty] private string _mochiLine = "";
    [ObservableProperty] private int _cheer;
    [ObservableProperty] private int _bigCheer;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Mood))] private bool _isPlaying;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsChoosing))] private bool _isNaming;
    [ObservableProperty] private string _nameText = "";
    [ObservableProperty] private IReadOnlyList<string> _nameMatches = Array.Empty<string>();
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanUndo))] private int _answerCount;
    /// <summary>"+14" next to what Mochi says, for the answer just given.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasPointsPop))] private string _pointsPop = "";
    /// <summary>A badge just earned, or a level reached.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasToast))] private string _toast = "";

    public VoiceGameViewModel(IEnumerable<VoiceRound> rounds, IEnumerable<VoiceFriend> friends, Func<string, IReadOnlyList<string>> findFriends,
        VoiceStore.VoiceGameStats? stats = null, Action<VoiceStore.VoiceGameStats>? saveStats = null)
    {
        Rounds = new ObservableCollection<VoiceRound>(rounds);
        Friends = new ObservableCollection<VoiceFriend>(friends);
        _findFriends = findFriends;
        _stats = stats?.Copy() ?? new VoiceStore.VoiceGameStats();
        PointsOn = stats is not null;
        _saveStats = saveStats;
        _outOfVoices = Rounds.Count == 0;
        Friends.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasFriends));
        _mochiLine = Rounds.Count == 0 ? T("Mochi hasn't heard any new voices yet. Come back after a few more clips with friends.")
            : T("Listen, then tell Mochi who's talking. Skip any you're not sure about, and stop whenever you like.");
    }

    public VoiceRound? Current => Index < Rounds.Count ? Rounds[Index] : null;
    /// <summary>Every voice Mochi had has been asked about.</summary>
    public bool IsDone => Index >= Rounds.Count && OutOfVoices;
    /// <summary>Answered faster than Mochi found the next voices.</summary>
    public bool IsWaiting => Index >= Rounds.Count && !OutOfVoices;
    public bool IsPlayingRound => Index < Rounds.Count;
    public bool IsChoosing => !IsNaming;
    public bool HasFriends => Friends.Count > 0;
    public bool CanUndo => AnswerCount > 0;
    public string RoundText => IsPlayingRound ? F("Voice {0}", Index + 1) : "";
    public Controls.MochiMood Mood => IsDone ? Controls.MochiMood.Proud : Controls.MochiMood.Listening;

    // ---------- points, levels and badges ----------
    public bool PointsOn { get; }
    public int Points => _stats.Points;
    public int Level => VoiceGameProgress.LevelOf(Points);
    public string LevelText => F("Level {0} · {1}", Level, VoiceGameProgress.TitleOf(Level));
    public string PointsText => F("{0} of {1} points", Points.ToString("N0"), VoiceGameProgress.PointsFor(Level + 1).ToString("N0"));
    public double LevelFill => VoiceGameProgress.LevelFill(Points);
    public bool HasStreak => PointsOn && _streak >= 2;
    public string StreakText => F("{0} in a row", _streak);
    public bool HasPointsPop => PointsOn && PointsPop.Length > 0;
    public bool HasToast => PointsOn && Toast.Length > 0;
    public IReadOnlyList<VoiceBadgeView> Badges =>
        VoiceGameProgress.Badges.Select(b => new VoiceBadgeView(T(b.Name), T(b.Description), _stats.Badges.Contains(b.Id))).ToList();
    public string BadgeCountText => F("{0} of {1}", _stats.Badges.Count(id => VoiceGameProgress.Badges.Any(b => b.Id == id)), VoiceGameProgress.Badges.Count);

    private void ProgressChanged()
    {
        foreach (var n in new[] { nameof(Points), nameof(Level), nameof(LevelText), nameof(PointsText), nameof(LevelFill), nameof(HasStreak), nameof(StreakText), nameof(Badges), nameof(BadgeCountText) })
            OnPropertyChanged(n);
    }

    public string DoneText
    {
        get
        {
            var named = _answers.Select(g => g.Answer).Where(a => a.Kind is VoiceAnswerKind.Friend or VoiceAnswerKind.Me)
                .Select(a => a.Kind == VoiceAnswerKind.Me ? "\u0001me" : a.Name!.ToLowerInvariant()).Distinct().Count();
            return named == 0 ? T("That's every voice Mochi has for now, and that's fine. There'll be new ones after more clips with friends.")
                : P(named, "That's every voice Mochi has for now. Thanks! Mochi got to know {0} voice better, and Who was there gets filled in for the clips it's sure about.",
                    "That's every voice Mochi has for now. Thanks! Mochi got to know {0} voices better, and Who was there gets filled in for the clips it's sure about.");
        }
    }

    /// <summary>More voices to ask about; none means Mochi has asked about them all.</summary>
    public void AddRounds(IReadOnlyCollection<VoiceRound> more)
    {
        IsLoadingMore = false;
        var waiting = IsWaiting;
        foreach (var r in more) Rounds.Add(r);
        if (more.Count == 0) OutOfVoices = true;
        OnPropertyChanged(nameof(Current));
        OnPropertyChanged(nameof(IsPlayingRound));
        OnPropertyChanged(nameof(IsWaiting));
        OnPropertyChanged(nameof(IsDone));
        OnPropertyChanged(nameof(RoundText));
        if (IsDone) MochiLine = DoneText;
        else if (waiting && Current is { } r) PlayRequested?.Invoke(r);
    }

    private void WantMore()
    {
        if (OutOfVoices || IsLoadingMore || Index < Rounds.Count - 2) return;
        IsLoadingMore = true;
        MoreWanted?.Invoke();
    }

    /// <summary>Asks for the next voices straight away when the first few are all there is.</summary>
    public void Start() => WantMore();

    [RelayCommand] private void Play() { if (Current is { } r) PlayRequested?.Invoke(r); }

    [RelayCommand]
    private void Pick(string name)
    {
        if (Current is not { } r || string.IsNullOrWhiteSpace(name)) return;
        name = name.Trim();
        Give(new VoiceAnswer(r, VoiceAnswerKind.Friend, name), F("Got it, that's {0}!", name));
        var friend = Friends.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
        if (friend is null) Friends.Add(friend = new VoiceFriend(name, 0));
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
        var a = last.Answer;
        if (a.Kind == VoiceAnswerKind.Friend && Friends.FirstOrDefault(f => string.Equals(f.Name, a.Name, StringComparison.OrdinalIgnoreCase)) is { } f)
            f.Count = Math.Max(0, f.Count - 1);
        if (PointsOn)
        {
            _stats = last.Before;
            _streak = last.Streak;
            _session = last.Session;
            _saveStats?.Invoke(_stats.Copy());
            ProgressChanged();
        }
        PointsPop = "";
        Toast = "";
        Index = Math.Max(0, Index - 1);
        Undone?.Invoke(a);
        MochiLine = T("Taken back. Have another listen.");
        OnPropertyChanged(nameof(DoneText));
    }

    private void Give(VoiceAnswer answer, string line)
    {
        var named = answer.Kind is VoiceAnswerKind.Friend or VoiceAnswerKind.Me;
        var given = new Given(answer, _stats.Copy(), _streak, _session, 0);
        PointsPop = "";
        Toast = "";
        if (PointsOn) given = given with { Points = Score(answer, named) };
        _answers.Add(given);
        AnswerCount = _answers.Count;
        Answered?.Invoke(answer);
        IsNaming = false;
        IsPlaying = false;
        MochiLine = line;
        if (named) Cheer++;
        Index++;
        if (IsDone) MochiLine = DoneText;
        OnPropertyChanged(nameof(DoneText));
        WantMore();
        if (IsPlayingRound) PlayRequested?.Invoke(Rounds[Index]); // the next voice plays by itself
    }

    /// <summary>Counts the answer in: points, streak, the stats behind the badges, and what was just earned.</summary>
    private int Score(VoiceAnswer answer, bool named)
    {
        var s = _stats;
        var newFriend = answer.Kind == VoiceAnswerKind.Friend && !s.Friends.Contains(answer.Name!, StringComparer.OrdinalIgnoreCase);
        var guessedRight = answer.Kind == VoiceAnswerKind.Friend && answer.Round.Guesses.FirstOrDefault() is { } top
                           && string.Equals(top.Name, answer.Name, StringComparison.OrdinalIgnoreCase);
        if (named) _streak++;
        else if (answer.Kind == VoiceAnswerKind.Skip) _streak = 0; // skipping is fine: it only starts a new streak
        if (answer.Kind != VoiceAnswerKind.Skip) _session++;
        var points = VoiceGameProgress.PointsForAnswer(answer.Kind, _streak, guessedRight, newFriend);
        var levelBefore = VoiceGameProgress.LevelOf(s.Points);
        s.Points += points;
        if (named) s.Named++;
        if (answer.Kind == VoiceAnswerKind.Me) s.NamedMe = true;
        if (answer.Kind == VoiceAnswerKind.Ignore) s.NotPeople++;
        if (guessedRight) s.GuessedRight++;
        if (newFriend) s.Friends.Add(answer.Name!);
        s.BestStreak = Math.Max(s.BestStreak, _streak);
        s.BestSession = Math.Max(s.BestSession, _session);
        var today = DateTime.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        if (answer.Kind != VoiceAnswerKind.Skip && !s.Days.Contains(today))
        {
            s.Days.Add(today);
            if (s.Days.Count > 30) s.Days.RemoveAt(0);
        }
        var earned = VoiceGameProgress.NewlyEarned(s, _session);
        foreach (var b in earned) s.Badges.Add(b.Id);
        var level = VoiceGameProgress.LevelOf(s.Points);

        if (points > 0) PointsPop = "+" + points;
        if (earned.Count > 0) Toast = earned.Count == 1 ? F("New badge: {0}", T(earned[0].Name)) : F("New badges: {0}", string.Join(", ", earned.Select(b => T(b.Name))));
        else if (level > levelBefore) Toast = F("Level {0}! {1}", level, VoiceGameProgress.TitleOf(level));
        if (earned.Count > 0 || level > levelBefore) BigCheer++;
        _saveStats?.Invoke(s.Copy());
        ProgressChanged();
        return points;
    }
}
