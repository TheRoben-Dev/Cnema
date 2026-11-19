namespace Cnema.Services;

/// <summary>
/// The update check runs first at every start, before the video player, the library or the main window, so a
/// release that can't open its window can still be replaced by the next one. It is quick and never holds up a
/// start for long: a few seconds at most, and nothing at all when the computer is offline.
/// <para>
/// A start that never got as far as a working window leaves a marker behind. The next start of the same version
/// then checks (and installs) before any window code runs at all, from <see cref="Program"/>.
/// </para>
/// </summary>
public static class EarlyUpdate
{
    private static string Marker => Path.Combine(AppPaths.DataDir, "start-pending");

    /// <summary>The last start of this same version never showed its window (or was ended while it tried).</summary>
    public static bool LastStartFailed { get; private set; }

    /// <summary>Called first thing at start: notes how the last one went, and that this one has begun.</summary>
    public static void BeginStart()
    {
        try
        {
            LastStartFailed = File.Exists(Marker) && File.ReadAllText(Marker).Trim() == UpdateService.CurrentText;
            File.WriteAllText(Marker, UpdateService.CurrentText);
            if (LastStartFailed) Log.Warn($"The last start of {UpdateService.CurrentText} never got to the main window");
        }
        catch (Exception ex)
        {
            Log.Warn($"Start marker: {ex.Message}");
        }
    }

    /// <summary>The window is up (or, starting hidden, built): this start worked.</summary>
    public static void StartSucceeded()
    {
        LastStartFailed = false;
        try { File.Delete(Marker); } catch { /* the next start only checks for updates once more */ }
    }

    private static (UpdateInfo? Update, DateTime At)? _result;

    /// <summary>
    /// What the check at start found, for the app's own first check, so GitHub isn't asked twice in a row.
    /// Only a check that went through counts; it is handed out once.
    /// </summary>
    public static bool TakeResult(out UpdateInfo? update)
    {
        update = null;
        if (_result is not { } r || DateTime.UtcNow - r.At > TimeSpan.FromMinutes(10)) return false;
        _result = null;
        update = r.Update;
        return true;
    }

    /// <summary>The newer release, if GitHub answers within <paramref name="limit"/>; offline it returns at once.</summary>
    public static async Task<UpdateInfo?> CheckAsync(TimeSpan limit, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(limit);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var (update, error) = await UpdateService.CheckDetailedAsync(cts.Token);
        Log.Info($"Start-up: update check {(error is null ? "done" : "skipped (" + error + ")")} after {sw.ElapsedMilliseconds} ms");
        if (error is null) _result = (update, DateTime.UtcNow);
        return error is null ? update : null;
    }

    /// <summary>
    /// Should this release be installed now, before anything opens? It follows Install updates automatically,
    /// tries each version once, and leaves a copy installed for everyone (which needs an administrator) to a
    /// click, unless the last start failed: then any update is worth more than a start that may fail again.
    /// </summary>
    public static bool ShouldInstall(UpdateInfo info, AppSettings settings, bool hidden)
    {
        if (info.AssetUrl is null || UpdateService.WhyNoSelfUpdate() is not null) return false;
        if (LastStartFailed) return !(hidden && UpdateService.IsAllUsersInstall);
        return settings.AutoUpdate && !UpdateService.IsAllUsersInstall &&
               settings.LastUpdateAttempt != info.Version.ToString(3);
    }

    /// <summary>
    /// Downloads the update and hands over to the script that swaps it in and starts it. True when the caller
    /// should now quit. Cancelling (a click on the loading screen) carries on with the start instead.
    /// </summary>
    public static async Task<bool> InstallAsync(UpdateInfo info, SettingsStore settings, bool hidden,
        IProgress<double>? progress, CancellationToken ct)
    {
        // Tried once per version: a download that fails or is skipped leaves the update card, not a loop.
        settings.Current.LastUpdateAttempt = info.Version.ToString(3);
        settings.Save();
        try
        {
            Log.Info($"Start-up: installing {info.Version.ToString(3)} before anything opens");
            var staged = await UpdateService.DownloadAsync(info, progress, ct);
            // If the swap doesn't take and this version starts again, that start is a normal one, not a failed one.
            StartSucceeded();
            UpdateService.ApplyAndRestart(staged, hidden);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Log.Info("Start-up: update skipped; it waits on its card");
            return false;
        }
        catch (Exception ex)
        {
            Log.Warn($"Start-up: the update could not be installed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// After a start that never reached its window: check and install before any window code runs, since that
    /// code may be what broke. Runs on the main thread before the app starts; true means quit now.
    /// </summary>
    public static bool Recover(bool hidden)
    {
        try
        {
            var settings = new SettingsStore();
            if (!settings.Current.CheckForUpdates) return false;
            Loc.Apply(settings.Current.Language);
            // A second launch while this one downloads should find it, not start a download of its own.
            using var guard = new SingleInstance();
            guard.Listen();
            var found = CheckAsync(TimeSpan.FromSeconds(6)).GetAwaiter().GetResult();
            if (found is null || !ShouldInstall(found, settings.Current, hidden))
            {
                Log.Info($"Start-up: no update to recover with ({(found is null ? "none found" : "can't install it here")})");
                return false;
            }
            PlatformShell.Notify("Savefile", F("Updating to {0}…", found.Version.ToString(3)));
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            return InstallAsync(found, settings, hidden, null, cts.Token).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Log.Warn($"Start-up: recovery update failed: {ex.Message}");
            return false;
        }
    }
}
