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

    /// <summary>A friend's voice counts as recognised from here (mean of the three closest answers).</summary>
    private const float KnowAt = 0.55f;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(VoiceModelText), nameof(CanDownloadVoice), nameof(CanPlayVoiceGame))] private bool _voiceModelReady = ModelStore.IsInstalled(ModelStore.Voice);
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(VoiceModelText), nameof(CanDownloadVoice))] private bool _isDownloadingVoice;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(VoiceModelText))] private double _voiceDownloadProgress;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(VoiceModelText))] private string? _voiceModelError;
    [ObservableProperty] private string _voiceActivity = "";

    public bool CanDownloadVoice => !VoiceModelReady && !IsDownloadingVoice;
    public bool CanPlayVoiceGame => VoiceModelReady && _items.Values.Any(v => v.Entry.Voices is { Speakers.Count: > 0 });
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
    private void RemoveVoiceModel()
    {
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
                    using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
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
                    catch (Exception ex)
                    {
                        Log.Warn($"Voices for {item.Entry.FileName}: {SkipReason(ex, item.Entry.DurationMs)}");
                        CountFailure("Voices");
                        info = null;
                    }
                    finally { _workGate.Release(); pace.Add(item.Entry.FileName, waited, watch.Elapsed); }
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
        foreach (var s in v.Speakers)
        {
            if (s.Ms < 2500 || s.Vec.Length == 0) continue; // a word or two isn't enough to be sure
            var best = _voices.Match(s.Vec).FirstOrDefault();
            if (best.Name is null || best.Score < KnowAt || best.Name == VoiceStore.MeKey) continue;
            if (item.Entry.People.Contains(best.Name, StringComparer.OrdinalIgnoreCase) || v.Added.Contains(best.Name, StringComparer.OrdinalIgnoreCase)) continue;
            item.Entry.People.Add(best.Name);
            v.Added.Add(best.Name);
            changed = true;
        }
        return changed;
    }

    /// <summary>After answers: every video gets another look with what Mochi now knows.</summary>
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

    [RelayCommand]
    private async Task OpenVoiceGameAsync()
    {
        if (!VoiceModelReady) return;
        // A copy of the library: the passes change it on this thread while the rounds are put together on another.
        var items = _items.Values.ToList();
        var rounds = await Task.Run(() => BuildVoiceRounds(items));
        var friends = _voices.Current.Known.Where(kv => kv.Value.Count > 0)
            .Select(kv => new VoiceFriend(kv.Key == VoiceStore.MeKey ? T("You") : kv.Key, kv.Value.Count))
            .OrderByDescending(f => f.Count).ToList();
        ShowVoiceGame(rounds, friends);
        if (rounds.Count > 0) PlayVoiceRound(rounds[0]);
    }

    /// <summary>Opens the game with these rounds (the real ones, or made-up ones for a preview).</summary>
    internal void ShowVoiceGame(IEnumerable<VoiceRound> rounds, IEnumerable<VoiceFriend> friends)
    {
        var game = new VoiceGameViewModel(rounds, friends, FindFriends);
        game.Answered += OnVoiceAnswered;
        game.Undone += OnVoiceUndone;
        game.PlayRequested += PlayVoiceRound;
        VoiceGame = game;
    }

    [RelayCommand]
    private void CloseVoiceGame()
    {
        StopVoicePlayback();
        VoiceGame = null;
        _roundPrints.Clear();
        OnPropertyChanged(nameof(VoiceModelText));
    }

    /// <summary>
    /// Up to eight voices to ask about: the unnamed voices of all videos grouped by who they sound like, the ones
    /// heard most first, each with Mochi's best guesses (friends with a similar voice, or friends tagged in those clips).
    /// </summary>
    private List<VoiceRound> BuildVoiceRounds(List<VideoItemViewModel> items)
    {
        _roundPrints.Clear();
        var heard = items
            .Where(v => v.Entry.Voices is { Speakers.Count: > 0 })
            .SelectMany(v => v.Entry.Voices!.Speakers.Where(s => s.Ms >= 2500 && s.Lines.Count > 0 && s.Vec.Length > 0).Select(s => new HeardVoice(v, s)))
            .Where(h => !_voices.IsIgnored(h.Speaker.Vec) && !(_voices.Match(h.Speaker.Vec).FirstOrDefault() is { Name: not null } m && m.Score >= KnowAt + 0.1f))
            .OrderByDescending(h => h.Speaker.Ms).Take(300).ToList();
        var groups = VoiceService.Group(heard.Select(h => h.Speaker.Vec).ToList(), VoiceService.SameVoice - 0.05f)
            .Select(g => g.Select(i => heard[i]).ToList())
            .OrderByDescending(g => g.Sum(h => h.Speaker.Ms) * Math.Sqrt(g.Select(h => h.Item).Distinct().Count()))
            .Take(8).ToList();
        var rounds = new List<VoiceRound>();
        var c = System.Globalization.CultureInfo.CurrentCulture;
        foreach (var g in groups)
        {
            var rep = g.OrderByDescending(h => h.Speaker.Ms).First();
            var line = rep.Speaker.Lines.OrderByDescending(l => Math.Min(l[1] - l[0], 6000)).First();
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
            _roundPrints[round] = g.Select(h => h.Speaker.Vec).ToList();
            rounds.Add(round);
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
        switch (a.Kind)
        {
            case VoiceAnswerKind.Friend: _voices.Add(a.Name!, prints); break;
            case VoiceAnswerKind.Me: _voices.Add(VoiceStore.MeKey, prints); break;
            case VoiceAnswerKind.Ignore: _voices.Ignore(prints); break;
        }
        if (a.Kind == VoiceAnswerKind.Friend) FillInAllVoices();
    }

    private void OnVoiceUndone(VoiceAnswer a)
    {
        if (!_roundPrints.TryGetValue(a.Round, out var prints)) return;
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
        if (_voicePlayer is null)
        {
            _voicePlayer = new MediaPlayer(lib);
            // libVLC's own threads: never call back into it from here, just tell the UI.
            _voicePlayer.EndReached += (_, _) => Dispatcher.UIThread.Post(VoicePlaybackEnded);
            _voicePlayer.EncounteredError += (_, _) => Dispatcher.UIThread.Post(VoicePlaybackEnded);
        }
        var player = _voicePlayer;
        _voicePlaying = true;
        UpdateBackgroundHold(); // like any playback: indexing waits, so the snippet doesn't crackle
        if (VoiceGame is { } g) g.IsPlaying = true;
        VoiceOp(() =>
        {
            using var media = new Media(lib, r.Path, FromType.FromPath);
            media.AddOption(":no-video");
            media.AddOption($":start-time={r.StartMs / 1000.0:0.###}".Replace(',', '.'));
            media.AddOption($":stop-time={r.EndMs / 1000.0 + 0.3:0.###}".Replace(',', '.'));
            player.Play(media);
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
        if (_voicePlayer is { } p && _voicePlaying) VoiceOp(() => p.Stop());
        VoicePlaybackEnded();
    }

    /// <summary>Background work freezes while anything plays in Savefile (a video, or a voice snippet).</summary>
    private void UpdateBackgroundHold() =>
        BackgroundHold.Set((Player.IsPlaying && Player.Current is not null) || _voicePlaying);

    /// <summary>A friend renamed or merged: their voice goes with them.</summary>
    internal void RenameVoice(string from, string to) => _voices.Rename(from, to);

    /// <summary>The friend a voice belongs to, for titles: only when Mochi is sure, and never you.</summary>
    private string? VoiceName(float[] print)
    {
        var best = _voices.Match(print).FirstOrDefault();
        return best.Name is null || best.Score < KnowAt || best.Name == VoiceStore.MeKey ? null : best.Name;
    }
}
