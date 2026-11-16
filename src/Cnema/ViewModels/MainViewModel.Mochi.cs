using Avalonia.Threading;
using Cnema.Controls;
using Cnema.Models;
using Cnema.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cnema.ViewModels;

// Mochi's moods and looks around the app. Each behaviour has its own switch under Settings, Features, Mochi,
// and all of them go quiet when Mochi is off. Nothing here ticks on its own except a minute timer for the
// hat and the late-night mood; the drawing lives in Controls/BlobMascot.
public sealed partial class MainViewModel
{
    /// <summary>Goes up on each save: the toast's Mochi squishes and sparkles.</summary>
    [ObservableProperty] private int _mochiSparkle;
    /// <summary>Goes up when a clip saved a moment ago turns out to be a big one: an anticipating, high hop.</summary>
    [ObservableProperty] private int _mochiBigCheer;
    /// <summary>The toast's Mochi: null for a plain toast (a tick instead).</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasToastMochi), nameof(ToastMochiMood))] private MochiMood? _toastMochi;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShelfMood))] private bool _mochiProud;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(MochiHat), nameof(ShelfMood), nameof(MochiTinsel))] private DateTime _mochiClock = DateTime.Now;

    private DispatcherTimer? _mochiTimer;
    private CancellationTokenSource? _proudCts;

    private bool Mochi(bool behaviour) => Features.Mascot && behaviour;

    public bool HasToastMochi => ToastMochi is not null;
    public MochiMood ToastMochiMood => ToastMochi ?? MochiMood.Normal;

    public bool MochiFollows => Mochi(Features.MochiFollow);
    public bool MochiPokeable => Mochi(Features.MochiPoke);
    public bool ShowMochiHelper => Mochi(Features.MochiHelper);
    public bool ShowMochiEmpty => Mochi(Features.MochiEmpty);

    /// <summary>The shelf's Mochi: proud for a moment after a milestone, sleepy late at night.</summary>
    public MochiMood ShelfMood =>
        MochiProud ? MochiMood.Proud
        : IsLateNight(MochiClock) && Mochi(Features.MochiSleepy) ? MochiMood.Sleepy
        : MochiMood.Normal;

    internal static bool IsLateNight(DateTime now) => now.Hour < 5;

    /// <summary>A Santa hat in December, a party hat on Savefile's birthday (the day it first ran here).</summary>
    public MochiHat MochiHat
    {
        get
        {
            if (!Mochi(Features.MochiSeasonal)) return MochiHat.None;
            var now = MochiClock;
            var first = FirstSeen().ToLocalTime();
            if (now.Year > first.Year && now.Month == first.Month && now.Day == first.Day) return MochiHat.Party;
            return now.Month == 12 ? MochiHat.Santa : MochiHat.None;
        }
    }

    public bool MochiTinsel => Mochi(Features.MochiSeasonal) && MochiClock.Month == 12;

    private DateTime FirstSeen()
    {
        if (_settings.Current.FirstSeenUtc is { } at) return at;
        // From before this was kept: the settings file's age is the best guess.
        var guess = File.Exists(AppPaths.SettingsFile) ? File.GetCreationTimeUtc(AppPaths.SettingsFile) : DateTime.UtcNow;
        if (guess.Year < 2000 || guess > DateTime.UtcNow) guess = DateTime.UtcNow;
        _settings.Current.FirstSeenUtc = guess;
        _settings.Save();
        return guess;
    }

    /// <summary>Started once: the hat and the late-night mood follow the clock, checked each minute.</summary>
    private void StartMochiClock()
    {
        FirstSeen();
        UpdatePlaytime();
        if (_mochiTimer is not null) return;
        _mochiTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _mochiTimer.Tick += (_, _) =>
        {
            var now = DateTime.Now;
            var night = IsLateNight(now) != IsLateNight(MochiClock);
            if (now.Date != MochiClock.Date || night)
            {
                MochiClock = now;
                if (night) RefreshMascot();
            }
        };
        _mochiTimer.Start();
    }

    private void RaiseMochi()
    {
        OnPropertyChanged(nameof(MochiFollows));
        OnPropertyChanged(nameof(MochiPokeable));
        OnPropertyChanged(nameof(ShowMochiHelper));
        OnPropertyChanged(nameof(ShowMochiEmpty));
        OnPropertyChanged(nameof(MochiHat));
        OnPropertyChanged(nameof(MochiTinsel));
        OnPropertyChanged(nameof(ShelfMood));
        OnPropertyChanged(nameof(ShowBackupBuddy));
        RaiseEmpty();
        RaiseMochiHelp();
        UpdatePlaytime();
    }

    // ---------- saves and milestones ----------

    /// <summary>The toast for a save, with Mochi reacting (or holding up the badge for a milestone).</summary>
    private void ShowSavedToast(string text, bool milestone)
    {
        if (milestone && Mochi(Features.MochiBadges))
        {
            ShowToast(text, MochiMood.Proud, 6);
            BeProud();
        }
        else if (Mochi(Features.MochiSaves))
        {
            ShowToast(text, MochiMood.Normal);
            MochiSparkle++;
        }
        else ShowToast(text);
    }

    /// <summary>The shelf's Mochi bounces with the badge for a few seconds too.</summary>
    private async void BeProud()
    {
        _proudCts?.Cancel();
        var cts = _proudCts = new CancellationTokenSource();
        MochiProud = true;
        try { await Task.Delay(6500, cts.Token); MochiProud = false; }
        catch (OperationCanceledException) { /* a newer milestone took over */ }
    }

    /// <summary>The best moments pass found a big one in a clip saved a few minutes ago: Mochi is thrilled.</summary>
    private void MaybeCheerFreshClip(VideoEntry entry, SmartInfo? info)
    {
        if (info is not { Excitement: >= 0.8 } || !Mochi(Features.MochiSaves)) return;
        if ((DateTime.UtcNow - entry.ModifiedUtc).TotalMinutes > 15) return;
        var name = entry.Title is { Length: > 0 } t ? t : Path.GetFileNameWithoutExtension(entry.Path);
        ShowToast(F("Mochi loved that one: {0}", name), MochiMood.Normal);
        MochiBigCheer++;
    }

    // ---------- the shelf's easter egg ----------

    public void MochiDizzy()
    {
        MascotLine = T("Wheee… okay, the room is spinning.");
        MascotLineShown = true;
    }

    /// <summary>Said first after midnight, when Mochi is sleepy.</summary>
    private string? LateLine(DateTime now) =>
        IsLateNight(now) && Mochi(Features.MochiSleepy) ? T("Late session? Mochi's staying up too.") : null;

    // ---------- backups ----------

    public bool ShowBackupBuddy => Mochi(Features.MochiBackup) && BackupActivity.Length > 0;

    partial void OnBackupActivityChanged(string value) => OnPropertyChanged(nameof(ShowBackupBuddy));

    // ---------- empty pages ----------

    public string NoResultsTitle =>
        !ShowMochiEmpty ? T("Nothing here yet")
        : ActiveNav is { Kind: NavKind.Person } friend && SearchText.Trim().Length == 0 && !HasFilters ? F("No clips with {0} yet", friend.Name)
        : SearchText.Trim().Length > 0 ? T("Mochi looked everywhere")
        : T("Nothing here yet");

    public string NoResultsText =>
        !ShowMochiEmpty ? T("Try another search, or pick a different folder.")
        : ActiveNav is { Kind: NavKind.Person } && SearchText.Trim().Length == 0 && !HasFilters ? T("Next time you play together, Mochi will keep the good bits here.")
        : SearchText.Trim().Length > 0 ? F("Nothing matches “{0}”. Try other words, or clear the filters.", SearchText.Trim())
        : T("Try another search, or pick a different folder.");

    public string BinEmptyText => ShowMochiEmpty
        ? T("Nothing in the bin. Videos you delete wait here for 24 hours, in case you change your mind.")
        : T("The bin is empty. Videos you delete wait here for 24 hours.");

    private void RaiseEmpty()
    {
        OnPropertyChanged(nameof(NoResultsTitle));
        OnPropertyChanged(nameof(NoResultsText));
        OnPropertyChanged(nameof(BinEmptyText));
    }

    // ---------- problems, in plain words ----------

    /// <summary>What to do about a model download or load that failed, in plain words.</summary>
    internal static string? HelpFor(string? error)
    {
        if (string.IsNullOrWhiteSpace(error)) return null;
        var e = error.ToLowerInvariant();
        if (e.Contains("space") || e.Contains("disk full")) return T("Mochi thinks the disk is full. Free up some space, then try the download again.");
        if (e.Contains("sha") || e.Contains("checksum") || e.Contains("damaged") || e.Contains("corrupt"))
            return T("The download came through damaged. Remove the model and download it again.");
        if (e.Contains("host") || e.Contains("network") || e.Contains("connect") || e.Contains("timed out") || e.Contains("timeout") || e.Contains("ssl") || e.Contains("name"))
            return T("Mochi couldn't reach the download. Check the internet connection, then try again.");
        if (e.Contains("memory")) return T("There wasn't enough memory for the model. Close a few apps, then restart Savefile.");
        return T("Something went wrong with the model. Remove it and download it again; if it keeps happening, send the diagnostics from Settings, About.");
    }

    public string? SpeechHelp => ShowMochiHelper ? HelpFor(SpeechModelError) : null;
    public string? VisualHelp => ShowMochiHelper ? HelpFor(VisualModelError) : null;
    public string? SoundHelp => ShowMochiHelper ? HelpFor(SoundModelError) : null;

    partial void OnSpeechModelErrorChanged(string? value) => OnPropertyChanged(nameof(SpeechHelp));
    partial void OnVisualModelErrorChanged(string? value) => OnPropertyChanged(nameof(VisualHelp));
    partial void OnSoundModelErrorChanged(string? value) => OnPropertyChanged(nameof(SoundHelp));

    private void RaiseMochiHelp()
    {
        OnPropertyChanged(nameof(SpeechHelp));
        OnPropertyChanged(nameof(VisualHelp));
        OnPropertyChanged(nameof(SoundHelp));
    }
}
