using Avalonia.Threading;
using Cnema.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cnema.ViewModels;

// Backup to your own server (S3-compatible), and restoring from it on a new computer.
public sealed partial class MainViewModel
{
    private readonly BackupService _backup = new();
    private CancellationTokenSource? _backupCts;
    private DispatcherTimer? _autoBackup;

    public bool ShowBackup => Features.Backup;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsShowingBackup))] private BackupViewModel? _backupPanel;
    public bool IsShowingBackup => BackupPanel is not null;

    /// <summary>The sidebar's line under Backup: "Backing up 3 of 12…" while it runs.</summary>
    [ObservableProperty] private string _backupActivity = "";

    private BackupSettings BackupConfig => _settings.Current.Backup;

    /// <summary>Any file (an SSH key): the window's file picker.</summary>
    public Func<string, Task<string?>>? PickAnyFile { get; set; }

    [RelayCommand]
    private async Task OpenBackupAsync()
    {
        // The keychain may take a moment (or ask to be unlocked): read it off the UI thread.
        var (secret, sftpSecret) = await Task.Run(() =>
            (SecretStore.Get(BackupService.SecretName), SecretStore.Get(BackupService.SftpSecretName)));
        var panel = new BackupViewModel(BackupConfig, secret, sftpSecret);
        panel.Summary = BackupSummary();
        panel.IsBusy = _backup.IsRunning;
        BackupPanel = panel;
    }

    [RelayCommand]
    private async Task CloseBackupAsync()
    {
        var panel = BackupPanel;
        BackupPanel = null;
        if (panel is { IsBusy: false }) await SaveBackupSettingsAsync(panel);
    }

    private async Task SaveBackupSettingsAsync(BackupViewModel panel)
    {
        panel.WriteTo(BackupConfig);
        _settings.Save();
        var (secret, sftpSecret) = (panel.SecretKey, panel.SftpSecret);
        if (!await Task.Run(() => SecretStore.Set(BackupService.SecretName, secret) & SecretStore.Set(BackupService.SftpSecretName, sftpSecret)))
            ShowToast(T("Couldn't store the password in the system keychain."));
        // Pointed somewhere else: the backed-up marks were for the old place.
        var before = _backup.BackedUpCount;
        _backup.UseTarget(BackupConfig);
        if (_backup.BackedUpCount != before)
        {
            foreach (var v in _items.Values) v.RefreshBackup();
            panel.Summary = BackupSummary();
        }
    }

    /// <summary>A first connection to an SSH server trusts its key: keep it, and show it.</summary>
    private void KeepHostKey()
    {
        if (BackupPanel is { } open && open.HostKey != BackupConfig.HostKey) open.HostKey = BackupConfig.HostKey;
        _settings.Save();
    }

    [RelayCommand]
    private void ForgetHostKey()
    {
        BackupConfig.HostKey = "";
        if (BackupPanel is { } open) { open.HostKey = ""; open.ShowStatus(T("Forgotten. Test the connection to trust the server's key again."), false); }
        _settings.Save();
    }

    [RelayCommand]
    private async Task PickKeyFileAsync()
    {
        if (BackupPanel is not { } panel || PickAnyFile is null) return;
        if (await PickAnyFile(T("Pick your SSH private key")) is { } picked) panel.KeyFile = picked;
    }

    private string BackupSummary()
    {
        var chosen = _items.Values.Count(v => BackupConfig.Everything || v.Entry.Favorite);
        var done = _items.Values.Count(v => (BackupConfig.Everything || v.Entry.Favorite) && _backup.IsBackedUp(v.Entry));
        var last = BackupConfig.LastRunUtc is { } t ? F("Last backup {0}.", t.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture)) : T("Not backed up yet.");
        return chosen == 0 ? last : F("{0} of {1} on the server. {2}", done.ToString("N0"), P(chosen, "{0} video", "{0:N0} videos"), last);
    }

    [RelayCommand]
    private async Task TestBackupAsync()
    {
        if (BackupPanel is not { } panel) return;
        await SaveBackupSettingsAsync(panel);
        panel.ShowStatus(T("Connecting…"), false);
        panel.IsBusy = true;
        try
        {
            var known = BackupConfig.HostKey;
            var problem = await Task.Run(() => _backup.TestAsync(BackupConfig, panel.CurrentSecret));
            var trusted = BackupConfig.HostKey != known && BackupConfig.HostKey.Length > 0;
            KeepHostKey();
            panel.ShowStatus(problem ?? (trusted
                ? T("Connected. Savefile can back up here, and from now on it only talks to this server (see its key below).")
                : T("Connected. Savefile can back up here.")), problem is not null);
        }
        finally { panel.IsBusy = false; }
    }

    [RelayCommand]
    private async Task BackUpNowAsync()
    {
        if (BackupPanel is { } panel) await SaveBackupSettingsAsync(panel);
        await RunBackupAsync(quiet: false);
    }

    /// <summary>Runs a backup with progress on the panel (when open) and in the sidebar.</summary>
    private async Task RunBackupAsync(bool quiet)
    {
        if (!Features.Backup) return;
        if (_backup.IsRunning) { _backupAgain = true; return; }
        var secretName = BackupService.SecretNameFor(BackupConfig);
        var secret = await Task.Run(() => SecretStore.Get(secretName));
        if (BackupService.Problem(BackupConfig, secret) is { } problem)
        {
            if (!quiet) BackupPanel?.ShowStatus(problem, true);
            return;
        }
        _backupCts = new CancellationTokenSource();
        var library = _items.Values.Select(v => v.Entry).ToList();
        var folders = _settings.Current.Folders.ToList();
        var progress = new Progress<BackupProgress>(p =>
        {
            BackupPanel?.Report(p);
            BackupActivity = p.Total == 0 ? "" : F("Backing up {0} of {1}…", Math.Min(p.Done + 1, p.Total), p.Total);
        });
        if (BackupPanel is { } open) { open.IsBusy = true; open.ShowStatus("", false); }
        BackupResult result;
        try
        {
            result = await Task.Run(() => _backup.BackUpAsync(BackupConfig, secret, library, folders, progress, _backupCts.Token));
            if (result.Ok)
            {
                _settings.Current.Backup.LastRunUtc = DateTime.UtcNow;
                _settings.Save();
            }
        }
        catch (OperationCanceledException)
        {
            result = new(T("Backup stopped. It carries on from here next time."), true);
        }
        finally
        {
            BackupActivity = "";
            _backupCts = null;
        }
        KeepHostKey();
        foreach (var v in _items.Values) v.RefreshBackup();
        if (BackupPanel is { } panel)
        {
            panel.IsBusy = false;
            panel.ShowStatus(result.Text, !result.Ok);
            panel.Summary = BackupSummary();
        }
        else if (!quiet || !result.Ok) ShowToast(result.Text);
        // Clips saved while this ran weren't in its list: go again for them.
        if (_backupAgain)
        {
            _backupAgain = false;
            QueueAutoBackup();
        }
    }

    private bool _backupAgain;

    [RelayCommand] private void StopBackup() => _backupCts?.Cancel();

    /// <summary>A little while after clips are saved or favourited, back them up on their own.</summary>
    private void QueueAutoBackup()
    {
        if (!Features.Backup || !BackupConfig.Automatic || !BackupConfig.IsSetUp) return;
        _autoBackup ??= new DispatcherTimer(TimeSpan.FromSeconds(30), DispatcherPriority.Background, async (_, _) =>
        {
            _autoBackup!.Stop();
            try { await RunBackupAsync(quiet: true); }
            catch (Exception ex)
            {
                Log.Warn($"Automatic backup failed: {ex.Message}");
                if (BackupPanel is { } open) open.IsBusy = false;
            }
        });
        _autoBackup.Stop();
        _autoBackup.Start();
    }

    // ---------- restore ----------

    [RelayCommand]
    private async Task FindRestoreAsync()
    {
        if (BackupPanel is not { } panel) return;
        await SaveBackupSettingsAsync(panel);
        if (BackupService.Problem(BackupConfig, panel.CurrentSecret) is { } problem) { panel.ShowStatus(problem, true); return; }
        panel.IsBusy = true;
        panel.ShowStatus(T("Looking at the server…"), false);
        try
        {
            var library = _items.Values.Select(v => v.Entry).ToList();
            var plan = await Task.Run(() => _backup.PlanRestoreAsync(BackupConfig, panel.CurrentSecret, library, CancellationToken.None));
            KeepHostKey();
            panel.RestorePlan = plan;
            panel.ShowStatus(plan.Missing.Count == 0 ? T("Everything on the server is on this computer too.") : "", false);
        }
        catch (Exception ex)
        {
            Log.Warn($"Restore check failed: {ex.Message}");
            panel.ShowStatus(await Task.Run(() => _backup.TestAsync(BackupConfig, panel.CurrentSecret)) ?? ex.Message, true);
        }
        finally { panel.IsBusy = false; }
    }

    /// <summary>Restored videos go to a "Restored" folder in the clips folder, which joins the library.</summary>
    [RelayCommand]
    private async Task RestoreAsync()
    {
        if (BackupPanel is not { RestorePlan: { } plan } panel) return;
        var target = Path.Combine(_settings.Current.Clip.ResolvedFolder, "Restored");
        try { Directory.CreateDirectory(target); }
        catch (Exception ex) { panel.ShowStatus(T("Could not create the folder: ") + ex.Message, true); return; }
        if (!_settings.Current.Folders.Any(f => Under(Path.Combine(target, "x"), f)))
        {
            _settings.Current.Folders.Add(target);
            _settings.Save();
            RebuildFolders(SelectedFolder?.Path);
        }
        _backupCts = new CancellationTokenSource();
        panel.IsBusy = true;
        panel.ShowStatus("", false);
        var progress = new Progress<BackupProgress>(p =>
        {
            BackupPanel?.Report(p);
            BackupActivity = F("Restoring {0} of {1}…", Math.Min(p.Done + 1, p.Total), p.Total);
        });
        BackupResult result;
        try
        {
            var secret = panel.CurrentSecret;
            result = await Task.Run(() => _backup.RestoreAsync(BackupConfig, secret, plan, target,
                (path, details) => _library.Adopt(path, details), progress, _backupCts.Token));
        }
        catch (OperationCanceledException)
        {
            result = new(T("Restore stopped. Find videos to restore again to carry on."), true);
        }
        finally
        {
            BackupActivity = "";
            _backupCts = null;
        }
        KeepHostKey();
        _library.SaveCache();
        await RescanAsync();
        // The panel may have been closed and opened again meanwhile: update the one on screen.
        if (BackupPanel is { } now)
        {
            now.IsBusy = false;
            now.RestorePlan = null;
            now.ShowStatus(result.Text, !result.Ok);
            now.Summary = BackupSummary();
        }
        else ShowToast(result.Text);
    }
}
