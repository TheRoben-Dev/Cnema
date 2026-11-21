using Avalonia.Threading;
using Cnema.Models;
using Cnema.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LibVLCSharp.Shared;

namespace Cnema.ViewModels;

// Who's talking: a voiceprint for each voice in a video (an optional 26 MB model, after Voice search wrote
// down what was said), Mochi's "Who's that?" game to put names to voices, and Who was there filled in for the
// clips where a friend's voice is recognised for sure. Everything stays on this computer.
public sealed partial class MainViewModel
{
    // ---------- the model and the pass ----------

    private readonly VoiceStore _voices = new();
    private CancellationTokenSource? _voiceCts;
    private Task? _voiceRun;
    private CancellationTokenSource? _voiceModelCts;

    // A friend is only ever tagged by voice when Mochi is as good as certain (Roben: "don't tag people unless the
    // voice is a perfect match, guesses in tags are not allowed"). The game still shows guesses below this, as
    // chips you confirm yourself.
    /// <summary>How close the voice must be to that friend's answers (mean of the three closest).</summary>
    private const float TagAt = 0.68f;
    /// <summary>And how far ahead of the next-closest person, so two similar voices are never mixed up.</summary>
    private const float TagMargin = 0.15f;
    /// <summary>Voiceprints from answers a friend needs before their voice tags anyone.</summary>
    private const int TagPrints = 3;
    /// <summary>Talking time in a clip needed to tag from it: a line or two isn't enough to be sure.</summary>
    private const long TagMs = 4000;

    /// <summary>Who this voice is when Mochi is certain enough to tag it (you included, as <see cref="VoiceStore.MeKey"/>), else null.</summary>
    private string? SureOf(float[] print)
    {
        if (print.Length == 0) return null;
        var match = _voices.Match(print);
        if (match.Count == 0) return null;
        var (name, score) = match[0];
        var next = match.Count > 1 ? match[1].Score : 0f;
        return score >= TagAt && score - next >= TagMargin && _voices.CountFor(name) >= TagPrints ? name : null;
    }

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(VoiceModelText), nameof(CanDownloadVoice), nameof(CanPlayVoiceGame))] private bool _voiceModelReady = ModelStore.IsInstalled(ModelStore.Voice);
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(VoiceModelText), nameof(CanDownloadVoice))] private bool _isDownloadingVoice;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(VoiceModelText))] private double _voiceDownloadProgress;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(VoiceModelText))] private string? _voiceModelError;
    [ObservableProperty] private string _voiceActivity = "";

    public bool CanDownloadVoice => !VoiceModelReady && !IsDownloadingVoice;
    // Always there once the model is: the game itself says when Mochi hasn't heard anyone new yet. It used to
    // hide until a voice had been found, and the button didn't notice when one was, so it looked gone.
    public bool CanPlayVoiceGame => Features.Voices && VoiceModelReady;

    /// <summary>On a friend's page: teach Mochi voices from there (it's not on the home page).</summary>
    public bool ShowFriendVoiceGame => CanPlayVoiceGame && ActiveNav is { Kind: NavKind.Person } && !IsSelecting;
    private long VoiceDownloadBytes => ModelStore.Voice.Where(m => !ModelStore.IsInstalled(m)).Sum(m => m.Bytes);

    public string VoiceModelText =>
        IsDownloadingVoice ? F("Downloading the voice model: {0:0}%", VoiceDownloadProgress * 100)
        : VoiceModelError is { } err ? err
        : !Features.Speech ? T("Turn on Voice search first: who's talking is worked out from the lines it writes down.")
        : VoiceModelReady ? F("Voice model installed. Listened to {0} of {1} videos with talking; {2} recognised so far.",
            _items.Values.Count(v => v.Entry.Voices is { Version: VoiceService.Version }).ToString("N0"),
            _items.Values.Count(v => v.Entry.Speech is { Lines.Count: > 0 }).ToString("N0"),
            P(_voices.Current.Known.Keys.Count(k => k != VoiceStore.MeKey), "{0} friend's voice", "{0} friends' voices")) + FailedText("Voices")
        : F("Needs a one-time download of a small voice model ({0}). After that, nothing leaves this computer.", BackupViewModel.SizeText(VoiceDownloadBytes));

    [RelayCommand]
    private async Task DownloadVoiceModelAsync()
    {
        if (IsDownloadingVoice) return;
        _voiceModelCts = new CancellationTokenSource();
        IsDownloadingVoice = true;
        VoiceModelError = null;
        VoiceDownloadProgress = 0;
        try
        {
            var progress = new Progress<double>(p => VoiceDownloadProgress = p);
            await Task.Run(() => ModelStore.DownloadAsync(ModelStore.Voice, progress, _voiceModelCts.Token));
            VoiceModelReady = true;
            StartVoicePass();
        }
        catch (OperationCanceledException) { /* cancelled */ }
        catch (Exception ex) { VoiceModelError = ex.Message; }
        finally
        {
            IsDownloadingVoice = false;
            _voiceModelCts.Dispose();
            _voiceModelCts = null;
        }
    }

    [RelayCommand] private void CancelVoiceDownload() => _voiceModelCts?.Cancel();

    [RelayCommand]
    private async Task RemoveVoiceModelAsync()
    {
        if (!await Ask.SureAsync(T("Remove the model?"), T("It frees up the space. To use this again, it needs downloading again. What it already found stays."), T("Remove the model"))) return;
        StopVoicePass();
        ModelStore.Remove(ModelStore.Voice);
        VoiceModelReady = ModelStore.IsInstalled(ModelStore.Voice);
        VoiceModelError = null;
        VisualModelReady = ModelStore.IsInstalled(ModelStore.Visual);
        SoundModelReady = ModelStore.IsInstalled(ModelStore.Sound);
    }

    private void StartVoicePass()
    {
        if (!Features.Voices || !Features.Speech || !VoiceModelReady || ToolLocator.Ffmpeg is null) return;
        if (_voiceRun is { IsCompleted: false } running)
        {
            // Switched off and on again before the last run had stopped: start again once it has.
            if (_voiceCts is { IsCancellationRequested: true }) RestartAfter(running, StartVoicePass);
            return;
        }
        _voiceCts = new CancellationTokenSource();
        _voiceRun = RunVoicePassAsync(_voiceCts.Token);
    }

    private void StopVoicePass()
    {
        _voiceCts?.Cancel();
        VoiceActivity = "";
    }

    /// <summary>Videos whose talking has been written down but not yet listened to for voices.</summary>
    private bool NeedsVoices(VideoEntry e) => e.Speech is { Lines.Count: > 0 } && e.Voices is not { Version: VoiceService.Version };

    private async Task RunVoicePassAsync(CancellationToken ct)
    {
        var done = 0;
        VoiceService? model = null;
        try
        {
            var tried = new HashSet<VideoItemViewModel>();
            var pace = new PassLog("Voices");
            using var awake = PlatformShell.StayAwake("Voices");
            Log.Info($"Voice pass: {_items.Values.Count(v => NeedsVoices(v.Entry))} to do");
            FillInAllVoices(); // tags from voices only when certain: older, looser ones are checked again
            // Runs as long as Voice search keeps writing things down: new lines are picked up each round.
            while (!ct.IsCancellationRequested && Features.Voices)
            {
                var todo = _items.Values.Where(v => NeedsVoices(v.Entry) && !tried.Contains(v)).OrderByDescending(v => v.Entry.ModifiedUtc).ToList();
                if (todo.Count == 0)
                {
                    if (SpeechActivity.Length == 0) break;
                    VoiceActivity = ""; // waiting for Voice search to write more down
                    await Task.Delay(TimeSpan.FromSeconds(30), ct);
                    continue;
                }
                for (var i = 0; i < todo.Count && !ct.IsCancellationRequested && Features.Voices; i++)
                {
                    var item = todo[i];
                    tried.Add(item);
                    if (!NeedsVoices(item.Entry) || !File.Exists(item.Entry.Path)) continue;
                    if (model is null)
                    {
                        VoiceActivity = T("Starting the voice model…");
                        model = await LoadModelAsync("Voices", VoiceService.Load, ct);
                    }
                    var waited = await TakeTurnAsync(s => VoiceActivity = s, F("Telling voices apart: {0} to go.", (todo.Count - i).ToString("N0")), "Voices", ct);
                    // A video starting to play stops this one: the model runs inside Savefile, where holding the
                    // background ffmpeg doesn't reach it, and it would compete with playback. It's done again after.
                    var held = BackgroundHold.WhenHeld;
                    var interrupted = false;
                    using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct, held);
                    BackgroundHold.CancelAfterActive(limit, ItemLimit(item.Entry.DurationMs));
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    VoiceInfo? info;
                    try
                    {
                        var entry = item.Entry;
                        info = await Task.Run(() => model.ListenAsync(entry, limit.Token), limit.Token);
                        if (watch.Elapsed.TotalSeconds >= 0.5) CostMeter.Record(CostMeter.Voices, watch.Elapsed.TotalSeconds, entry.DurationMs);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (OperationCanceledException) when (held.IsCancellationRequested) { interrupted = true; info = null; }
                    catch (Exception ex)
                    {
                        Log.Warn($"Voices for {item.Entry.FileName}: {SkipReason(ex, item.Entry.DurationMs)}");
                        CountFailure("Voices");
                        info = null;
                    }
                    finally { _workGate.Release(); pace.Add(item.Entry.FileName, waited, watch.Elapsed); }
                    if (interrupted) { tried.Remove(item); continue; }
                    if (_items.TryGetValue(item.Entry.Path, out var still) && ReferenceEquals(still, item))
                    {
                        // Keep who was added by voice before, so a friend taken off by hand stays off.
                        info ??= new VoiceInfo { Version = VoiceService.Version };
                        if (item.Entry.Voices is { } old) info.Added = old.Added;
                        item.Entry.Voices = info;
                        if (FillInVoices(item)) item.RefreshAll();
                        Retitle(item);
                    }
                    if (++done % 10 == 0) { _library.SaveCache(); OnPropertyChanged(nameof(VoiceModelText)); OnPropertyChanged(nameof(CanPlayVoiceGame)); }
                    await Task.Delay(100, ct);
                }
            }
        }
        catch (OperationCanceledException) { /* stopped */ }
        catch (Exception ex)
        {
            Log.Warn($"Voice pass stopped: {ex.Message}");
            VoiceModelError = ex.Message;
        }
        finally
        {
            Log.Info($"Voice pass ended: {done} done");
            model?.Dispose();
            VoiceActivity = "";
            OnCostsChanged();
            if (done > 0)
            {
                _library.SaveCache();
                RebuildTags();
                OnPropertyChanged(nameof(VoiceModelText));
                OnPropertyChanged(nameof(CanPlayVoiceGame));
            }
        }
    }

    // ---------- Who was there, from voices ----------

    /// <summary>
    /// Adds the friends whose voices are recognised for sure to a video's Who was there. Each name is added once:
    /// if you take it off again, it stays off. Returns whether anything changed.
    /// </summary>
    private bool FillInVoices(VideoItemViewModel item)
    {
        if (!Features.People || item.Entry.Voices is not { } v) return false;
        var changed = false;
        var sure = v.Speakers.Where(s => s.Ms >= TagMs).Select(s => SureOf(s.Vec)).Where(n => n is not null && n != VoiceStore.MeKey)
            .Select(n => n!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        // Tags added by voice that aren't certain any more (made under the looser rule before 0.51, or a friend's
        // voice that's now closer to someone else) come off. Tags you added yourself are never touched.
        foreach (var name in v.Added.ToList())
        {
            if (sure.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            v.Added.RemoveAll(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
            item.Entry.People.RemoveAll(p => string.Equals(p, name, StringComparison.OrdinalIgnoreCase));
            changed = true;
        }
        foreach (var name in sure)
        {
            if (item.Entry.People.Contains(name, StringComparer.OrdinalIgnoreCase) || v.Added.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            item.Entry.People.Add(name);
            v.Added.Add(name);
            changed = true;
        }
        return changed;
    }

    /// <summary>After answers (and once at start): every video gets another look with what Mochi now knows.</summary>
    private void FillInAllVoices()
    {
        var n = 0;
        foreach (var item in _items.Values)
            if (FillInVoices(item)) { item.RefreshAll(); n++; }
        if (n == 0) return;
        Log.Info($"Voices: friends added to {n} videos");
        _library.SaveCache();
        RebuildTags();
        RefreshView();
    }

    // ---------- "Who's that?" ----------

    /// <summary>One voice heard in one video, for grouping the voices nobody has named yet.</summary>
    private sealed record HeardVoice(VideoItemViewModel Item, VoiceSpeaker Speaker);

    /// <summary>Friends whose names start with (or contain) what's typed, for "Someone else".</summary>
    internal IReadOnlyList<string> FindFriends(string typed)
    {
        var t = typed.Trim();
        return _items.Values.SelectMany(v => v.Entry.People).Select(p => p.Trim()).Where(p => p.Length > 0)
            .Concat(_voices.Current.Known.Keys.Where(k => k != VoiceStore.MeKey))
            .GroupBy(p => p, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Name: g.First(), Count: g.Count()))
            .Where(f => t.Length == 0 || f.Name.Contains(t, StringComparison.CurrentCultureIgnoreCase))
            .OrderByDescending(f => t.Length > 0 && f.Name.StartsWith(t, StringComparison.CurrentCultureIgnoreCase))
            .ThenByDescending(f => f.Count)
            .Select(f => f.Name).Take(6).ToList();
    }

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsShowingVoiceGame))] private VoiceGameViewModel? _voiceGame;
    public bool IsShowingVoiceGame => VoiceGame is not null;
    private readonly Dictionary<VoiceRound, List<float[]>> _roundPrints = new();
    private MediaPlayer? _voicePlayer;

    // What this game has asked about already, so the next voices fetched are new ones (a skipped voice isn't
    // asked again this time; answered ones are known or ignored by then anyway).
    private readonly HashSet<float[]> _askedPrints = new(ReferenceEqualityComparer.Instance);
    private readonly List<float[]> _askedMeans = new();
    /// <summary>Voices fetched at a time; more are fetched while the last two play.</summary>
    private const int VoiceBatch = 6;

    [RelayCommand]
    private async Task OpenVoiceGameAsync()
    {
        if (!VoiceModelReady) return;
        _askedPrints.Clear();
        _askedMeans.Clear();
        _roundPrints.Clear();
        var rounds = await NextVoiceRoundsAsync();
        var friends = _voices.Current.Known.Where(kv => kv.Value.Count > 0)
            .Select(kv => new VoiceFriend(kv.Key == VoiceStore.MeKey ? T("You") : kv.Key, kv.Value.Count))
            .OrderByDescending(f => f.Count).ToList();
        var game = ShowVoiceGame(rounds, friends);
        if (rounds.Count > 0) PlayVoiceRound(rounds[0]);
        game.Start();
    }

    /// <summary>The next voices to ask about, put together off the UI thread; their voiceprints are noted here.</summary>
    private async Task<List<VoiceRound>> NextVoiceRoundsAsync()
    {
        // A copy of the library: the passes change it on this thread while the rounds are put together on another.
        var items = _items.Values.ToList();
        var askedPrints = new HashSet<float[]>(_askedPrints, ReferenceEqualityComparer.Instance);
        var askedMeans = _askedMeans.ToList();
        List<(VoiceRound Round, List<float[]> Prints, float[] Mean)> built;
        try { built = await Task.Run(() => BuildVoiceRounds(items, askedPrints, askedMeans)); }
        catch (Exception ex)
        {
            Log.Warn($"Who's that?: {ex.Message}");
            built = new();
        }
        foreach (var (round, prints, mean) in built)
        {
            _roundPrints[round] = prints;
            foreach (var p in prints) _askedPrints.Add(p);
            _askedMeans.Add(mean);
        }
        return built.Select(b => b.Round).ToList();
    }

    private async void OnMoreVoicesWanted(VoiceGameViewModel game)
    {
        var more = await NextVoiceRoundsAsync();
        if (!ReferenceEquals(VoiceGame, game)) return; // closed meanwhile
        game.AddRounds(more);
    }

    /// <summary>Opens the game with these rounds (the real ones, or made-up ones for a preview).</summary>
    internal VoiceGameViewModel ShowVoiceGame(IEnumerable<VoiceRound> rounds, IEnumerable<VoiceFriend> friends)
    {
        var points = Features.VoicePoints;
        var game = new VoiceGameViewModel(rounds, friends, FindFriends,
            points ? _voices.Current.Game : null, points ? _voices.SetGame : null);
        game.Answered += OnVoiceAnswered;
        game.Undone += OnVoiceUndone;
        game.PlayRequested += PlayVoiceRound;
        game.MoreWanted += () => OnMoreVoicesWanted(game);
        VoiceGame = game;
        return game;
    }

    [RelayCommand]
    private void CloseVoiceGame()
    {
        StopVoicePlayback();
        VoiceGame = null;
        _roundPrints.Clear();
        _askedPrints.Clear();
        _askedMeans.Clear();
        OnPropertyChanged(nameof(VoiceModelText));
    }

    /// <summary>
    /// The next few voices to ask about: the voices of all videos Mochi isn't certain of yet, minus the ones this
    /// game already asked about, grouped by who they sound like, the ones heard most first, each with Mochi's best
    /// guesses (friends with a similar voice, or friends tagged in those clips). Runs off the UI thread.
    /// </summary>
    private List<(VoiceRound Round, List<float[]> Prints, float[] Mean)> BuildVoiceRounds(List<VideoItemViewModel> items, HashSet<float[]> askedPrints, List<float[]> askedMeans)
    {
        var heard = items
            .Where(v => v.Entry.Voices is { Version: VoiceService.Version, Speakers.Count: > 0 }) // older ones can mix voices; they're being redone
            .SelectMany(v => v.Entry.Voices!.Speakers.Where(s => s.Ms >= 2500 && s.Lines.Count > 0 && s.Vec.Length > 0).Select(s => new HeardVoice(v, s)))
            .Where(h => !askedPrints.Contains(h.Speaker.Vec) && !_voices.WasAnswered(h.Speaker.Vec) && !askedMeans.Any(m => VoiceService.Cosine(m, h.Speaker.Vec) >= VoiceService.SameVoice + 0.05f))
            .Where(h => !_voices.IsIgnored(h.Speaker.Vec) && SureOf(h.Speaker.Vec) is null) // not yet certain: still worth asking about
            .OrderByDescending(h => h.Speaker.Ms).Take(300).ToList();
        var groups = VoiceService.Group(heard.Select(h => h.Speaker.Vec).ToList(), VoiceService.SameVoice - 0.05f)
            .Select(g => g.Select(i => heard[i]).ToList())
            .OrderByDescending(g => g.Sum(h => h.Speaker.Ms) * Math.Sqrt(g.Select(h => h.Item).Distinct().Count()))
            .Take(VoiceBatch).ToList();
        var rounds = new List<(VoiceRound, List<float[]>, float[])>();
        var c = System.Globalization.CultureInfo.CurrentCulture;
        foreach (var g in groups)
        {
            var rep = g.OrderByDescending(h => h.Speaker.Ms).First();
            var line = rep.Speaker.Lines[0]; // the best bit to hear this voice alone, and what its quote is from
            var mean = VoiceService.Normalize(VoiceService.Mean(g.Select(h => h.Speaker.Vec)));
            // Guesses: friends who sound alike, then friends tagged in these clips (most often first).
            var guesses = _voices.Match(mean).Where(m => m.Name != VoiceStore.MeKey && m.Score >= 0.2f).Take(3)
                .Select(m => new VoiceGuess(m.Name, Math.Clamp((m.Score - 0.2) / 0.45, 0.05, 1))).ToList();
            foreach (var name in g.SelectMany(h => h.Item.Entry.People).GroupBy(p => p.Trim(), StringComparer.OrdinalIgnoreCase)
                         .OrderByDescending(x => x.Count()).Select(x => x.Key).Where(n => n.Length > 0))
            {
                if (guesses.Count >= 3) break;
                if (guesses.Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))) continue;
                guesses.Add(new VoiceGuess(name, 0.15));
            }
            var e = rep.Item.Entry;
            var local = e.ModifiedUtc.ToLocalTime();
            var bits = new List<string>();
            if (GameOf(e) is { } game) bits.Add(game);
            bits.Add(local.ToString("d MMMM", c));
            var with = e.People.Where(p => p.Trim().Length > 0).Take(3).ToList();
            if (with.Count > 0) bits.Add(F("in a clip with {0}", string.Join(", ", with)));
            var clips = g.Select(h => h.Item).Distinct().Count();
            if (clips > 1) bits.Add(P(clips, "heard in {0} clip", "heard in {0} clips"));
            var round = new VoiceRound
            {
                Path = e.Path, StartMs = line[0], EndMs = Math.Min(line[1], line[0] + 6000),
                Quote = rep.Speaker.Quote.Length > 90 ? rep.Speaker.Quote[..89].TrimEnd() + "…" : rep.Speaker.Quote,
                Context = string.Join(" · ", bits),
                Levels = SnippetLevels(e.Path, line[0], Math.Min(line[1], line[0] + 6000)),
                Guesses = guesses,
            };
            rounds.Add((round, g.Select(h => h.Speaker.Vec).ToList(), mean));
        }
        return rounds;
    }

    /// <summary>48 loudness bars for the little waveform.</summary>
    private static IReadOnlyList<double> SnippetLevels(string path, long from, long to)
    {
        try
        {
            if (ToolLocator.Ffmpeg is not { } ffmpeg) return Array.Empty<double>();
            var samples = SpeechService.ReadAudioAsync(ffmpeg, path, TimeSpan.FromMilliseconds(from), 1, CancellationToken.None, TimeSpan.FromMilliseconds(to - from + 100), background: false).GetAwaiter().GetResult();
            var n = (int)Math.Min(samples.Length, (to - from) * 16);
            if (n < 48) return Array.Empty<double>();
            var step = n / 48;
            var rms = Enumerable.Range(0, 48).Select(k =>
            {
                double s = 0;
                for (var i = k * step; i < (k + 1) * step; i++) s += samples[i] * samples[i];
                return Math.Sqrt(s / step);
            }).ToList();
            var max = Math.Max(1e-6, rms.Max());
            return rms.Select(r => Math.Pow(r / max, 0.6)).ToList();
        }
        catch { return Array.Empty<double>(); }
    }

    private void OnVoiceAnswered(VoiceAnswer a)
    {
        StopVoicePlayback();
        if (!_roundPrints.TryGetValue(a.Round, out var prints)) return; // the preview
        _voices.MarkAnswered(prints); // whatever the answer, skips too: these bits are never asked about again
        switch (a.Kind)
        {
            case VoiceAnswerKind.Friend: _voices.Add(a.Name!, prints); break;
            case VoiceAnswerKind.Me: _voices.Add(VoiceStore.MeKey, prints); break;
            case VoiceAnswerKind.Ignore: _voices.Ignore(prints); break;
        }
        if (a.Kind != VoiceAnswerKind.Skip) FillInAllVoices(); // any answer can make a voice certain, or no longer certain
    }

    private void OnVoiceUndone(VoiceAnswer a)
    {
        if (!_roundPrints.TryGetValue(a.Round, out var prints)) return;
        _voices.MarkAnswered(prints, undo: true);
        switch (a.Kind)
        {
            case VoiceAnswerKind.Friend: _voices.Remove(a.Name!, prints); break;
            case VoiceAnswerKind.Me: _voices.Remove(VoiceStore.MeKey, prints); break;
            case VoiceAnswerKind.Ignore: _voices.Ignore(prints, undo: true); break;
        }
        // Friends added by voice because of this answer come off again.
        if (a.Kind == VoiceAnswerKind.Friend)
            foreach (var item in _items.Values)
                if (item.Entry.Voices is { } v && v.Added.RemoveAll(n => string.Equals(n, a.Name, StringComparison.OrdinalIgnoreCase)) > 0 && item.Entry.People.RemoveAll(p => string.Equals(p, a.Name, StringComparison.OrdinalIgnoreCase)) > 0)
                {
                    FillInVoices(item);
                    item.RefreshAll();
                }
        _library.SaveCache();
        RebuildTags();
    }

    /// <summary>Plays the bit of the video where that voice talks (sound only).</summary>
    private void PlayVoiceRound(VoiceRound r)
    {
        if (VlcBootstrap.LibVLC is not { } lib || !File.Exists(r.Path)) return;
        _voicePlaying = true;
        UpdateBackgroundHold(); // like any playback: indexing waits, so the snippet doesn't crackle
        if (VoiceGame is { } g) g.IsPlaying = true;
        // Every libVLC call for the snippets, making the player too, happens off the UI thread (see VoiceOp).
        VoiceOp(() =>
        {
            if (_voicePlayer is null)
            {
                var made = new MediaPlayer(lib);
                // libVLC's own threads: never call back into it from here, just tell the UI.
                made.EndReached += (_, _) => Dispatcher.UIThread.Post(VoicePlaybackEnded);
                made.EncounteredError += (_, _) => Dispatcher.UIThread.Post(VoicePlaybackEnded);
                _voicePlayer = made;
            }
            using var media = new Media(lib, r.Path, FromType.FromPath);
            media.AddOption(":no-video");
            media.AddOption($":start-time={r.StartMs / 1000.0:0.###}".Replace(',', '.'));
            media.AddOption($":stop-time={r.EndMs / 1000.0 + 0.3:0.###}".Replace(',', '.'));
            _voicePlayer.Play(media);
        });
    }

    private bool _voicePlaying;
    private Task _voiceOps = Task.CompletedTask;

    /// <summary>
    /// The snippet player's Play and Stop, one after another off the UI thread: Stop can wait on libVLC's threads,
    /// and a Stop must never land after the Play of the next voice.
    /// </summary>
    private void VoiceOp(Action op) =>
        _voiceOps = _voiceOps.ContinueWith(_ =>
        {
            try { op(); }
            catch (Exception ex) { Log.Warn($"Voice snippet: {ex.Message}"); }
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);

    private void VoicePlaybackEnded()
    {
        _voicePlaying = false;
        UpdateBackgroundHold();
        if (VoiceGame is { } g) g.IsPlaying = false;
    }

    private void StopVoicePlayback()
    {
        if (_voicePlaying) VoiceOp(() => _voicePlayer?.Stop());
        VoicePlaybackEnded();
    }

    /// <summary>Background work freezes while anything plays in Savefile (a video, or a voice snippet).</summary>
    private void UpdateBackgroundHold() =>
        BackgroundHold.Set((Player.IsPlaying && Player.Current is not null) || Player.IsStopping || _voicePlaying);

    /// <summary>A friend renamed or merged: their voice goes with them.</summary>
    internal void RenameVoice(string from, string to) => _voices.Rename(from, to);


    /// <summary>The friend a voice belongs to, for titles: only when Mochi is certain, and never you.</summary>
    private string? VoiceName(float[] print) => SureOf(print) is { } n && n != VoiceStore.MeKey ? n : null;
}
