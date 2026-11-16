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

    [RelayCommand]
    private void OpenBackup()
    {
        var panel = new BackupViewModel(BackupConfig, SecretStore.Get(BackupService.SecretName));
        panel.Summary = BackupSummary();
        panel.IsBusy = _backup.IsRunning;
        BackupPanel = panel;
    }

    [RelayCommand]
    private void CloseBackup()
    {
        if (BackupPanel is { } panel && !panel.IsBusy) SaveBackupSettings(panel);
        BackupPanel = null;
    }

    private void SaveBackupSettings(BackupViewModel panel)
    {
        panel.WriteTo(BackupConfig);
        if (!SecretStore.Set(BackupService.SecretName, panel.SecretKey))
            ShowToast(T("Couldn't store the secret key in the system keychain."));
        _settings.Save();
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
        SaveBackupSettings(panel);
        panel.ShowStatus(T("Connecting…"), false);
        panel.IsBusy = true;
        try
        {
            var problem = await Task.Run(() => _backup.TestAsync(BackupConfig, panel.SecretKey));
            panel.ShowStatus(problem ?? T("Connected. Savefile can back up to this bucket."), problem is not null);
        }
        finally { panel.IsBusy = false; }
    }

    [RelayCommand]
    private async Task BackUpNowAsync()
    {
        if (BackupPanel is { } panel) SaveBackupSettings(panel);
        await RunBackupAsync(quiet: false);
    }

    /// <summary>Runs a backup with progress on the panel (when open) and in the sidebar.</summary>
    private async Task RunBackupAsync(bool quiet)
    {
        if (!Features.Backup || _backup.IsRunning) return;
        var secret = SecretStore.Get(BackupService.SecretName);
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
            result = await Task.Run(() => _backup.BackUpAsync(BackupConfig, secret!, library, folders, progress, _backupCts.Token));
            _settings.Save();
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
        foreach (var v in _items.Values) v.RefreshBackup();
        if (BackupPanel is { } panel)
        {
            panel.IsBusy = false;
            panel.ShowStatus(result.Text, !result.Ok);
            panel.Summary = BackupSummary();
        }
        else if (!quiet || !result.Ok) ShowToast(result.Text);
    }

    [RelayCommand] private void StopBackup() => _backupCts?.Cancel();

    /// <summary>A little while after clips are saved or favourited, back them up on their own.</summary>
    private void QueueAutoBackup()
    {
        if (!Features.Backup || !BackupConfig.Automatic || !BackupConfig.IsSetUp) return;
        _autoBackup ??= new DispatcherTimer(TimeSpan.FromSeconds(30), DispatcherPriority.Background, async (_, _) =>
        {
            _autoBackup!.Stop();
            await RunBackupAsync(quiet: true);
        });
        _autoBackup.Stop();
        _autoBackup.Start();
    }

    // ---------- restore ----------

    [RelayCommand]
    private async Task FindRestoreAsync()
    {
        if (BackupPanel is not { } panel) return;
        SaveBackupSettings(panel);
        if (BackupService.Problem(BackupConfig, panel.SecretKey) is { } problem) { panel.ShowStatus(problem, true); return; }
        panel.IsBusy = true;
        panel.ShowStatus(T("Looking at the server…"), false);
        try
        {
            var library = _items.Values.Select(v => v.Entry).ToList();
            var plan = await Task.Run(() => _backup.PlanRestoreAsync(BackupConfig, panel.SecretKey, library, CancellationToken.None));
            panel.RestorePlan = plan;
            panel.ShowStatus(plan.Missing.Count == 0 ? T("Everything on the server is on this computer too.") : "", false);
        }
        catch (Exception ex)
        {
            Log.Warn($"Restore check failed: {ex.Message}");
            panel.ShowStatus(await Task.Run(() => _backup.TestAsync(BackupConfig, panel.SecretKey)) ?? ex.Message, true);
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
            panel.Report(p);
            BackupActivity = F("Restoring {0} of {1}…", Math.Min(p.Done + 1, p.Total), p.Total);
        });
        string result;
        try
        {
            result = await Task.Run(() => _backup.RestoreAsync(BackupConfig, panel.SecretKey, plan, target,
                (path, details) => _library.Adopt(path, details), progress, _backupCts.Token));
        }
        catch (OperationCanceledException)
        {
            result = T("Restore stopped. Find videos to restore again to carry on.");
        }
        finally
        {
            BackupActivity = "";
            _backupCts = null;
            panel.IsBusy = false;
        }
        _library.SaveCache();
        panel.RestorePlan = null;
        panel.ShowStatus(result, false);
        await RescanAsync();
        panel.Summary = BackupSummary();
    }
}
