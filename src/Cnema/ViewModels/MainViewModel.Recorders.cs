using Cnema.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cnema.ViewModels;

// Notes, drag-out and other recorders (OBS, NVIDIA, Xbox Game Bar, Steam).
public sealed partial class MainViewModel
{
    public bool ShowNotes => Features.Notes;
    public bool ShowRecorders => Features.Recorders;
    public bool AllowDragOut => Features.DragOut;

    // ---------- other recorders ----------
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsShowingRecorders))] private RecordersViewModel? _recorders;
    public bool IsShowingRecorders => Recorders is not null;
    private CancellationTokenSource? _steamCts;

    /// <summary>Where converted Steam clips go: a "Steam" folder next to Cnema's own clips.</summary>
    private string SteamOutput => Path.Combine(_settings.Current.Clip.ResolvedFolder, "Steam");

    /// <summary>The recorder whose folder a file is in, as used for game tags. Steam's is the converted-clips folder.</summary>
    private RecorderImport? ImportFor(string file) => !Features.Recorders ? null : _settings.Current.Recorders
        .Select(i => i.Kind == RecorderKind.Steam ? new RecorderImport { Kind = i.Kind, Path = SteamOutput } : i)
        .Where(i => Under(file, i.Path))
        .OrderByDescending(i => i.Path.Length)
        .FirstOrDefault();

    private string? GameTagFor(string file)
    {
        if (!_settings.Current.RecorderTags || ImportFor(file) is not { } import) return null;
        // Savefile's own clips folder often sits inside OBS's or NVIDIA's (Videos / Movies); those
        // clips are tagged by Savefile itself, not with "Savefile Clips" as a game.
        if (import.Kind != RecorderKind.Steam && Under(file, _settings.Current.Clip.ResolvedFolder)) return null;
        return RecorderService.GameFor(import, file);
    }

    [RelayCommand]
    private async Task OpenRecordersAsync()
    {
        var vm = new RecordersViewModel(_settings.Current.Recorders, _settings.Current.RecorderTags);
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(RecordersViewModel.TagByGame)) return;
            _settings.Current.RecorderTags = vm.TagByGame;
            _settings.Save();
        };
        Recorders = vm;
        await CountSteamAsync();
    }

    [RelayCommand] private void CloseRecorders() => Recorders = null;

    [RelayCommand]
    private async Task ChangeRecorderFolderAsync(RecorderRow? row)
    {
        if (row is null || PickFolder is null || await PickFolder() is not { Length: > 0 } path) return;
        var wasAdded = row.IsAdded;
        if (wasAdded) await DropRecorderAsync(row);
        row.Path = NormalizeFolder(path);
        if (wasAdded) await AddRecorderAsync(row);
    }

    [RelayCommand]
    private async Task AddRecorderAsync(RecorderRow? row)
    {
        if (row?.Path is not { } path) return;
        var import = new RecorderImport { Kind = row.Kind, Path = path };
        // Steam's own folder holds streaming fragments; the library watches the folder the MP4 copies go to.
        var libraryFolder = row.IsSteam ? SteamOutput : path;
        try { Directory.CreateDirectory(libraryFolder); }
        catch (Exception ex) { ShowToast(T("Could not create the folder: ") + ex.Message); return; }

        _settings.Current.Recorders.RemoveAll(i => i.Kind == row.Kind);
        if (!_settings.Current.Folders.Any(f => Under(Path.Combine(libraryFolder, "x"), f)))
        {
            _settings.Current.Folders.Add(libraryFolder);
            import.AddedFolder = true;
        }
        _settings.Current.Recorders.Add(import);
        _settings.Save();
        row.IsAdded = true;

        // Videos already in the library from that folder get their game tag now.
        if (_settings.Current.RecorderTags)
        {
            foreach (var v in _items.Values)
                if (GameTagFor(v.Entry.Path) is { } game && !v.Entry.Tags.Contains(game, StringComparer.OrdinalIgnoreCase))
                {
                    v.Entry.Tags.Add(game);
                    v.RefreshAll();
                }
            _library.SaveCache();
        }
        RebuildFolders(SelectedFolder?.Path);
        ShowToast(F("{0} added to your library", row.Name));
        await RescanAsync();
        if (row.IsSteam) await ConvertSteamClipsAsync();
    }

    [RelayCommand]
    private async Task RemoveRecorderAsync(RecorderRow? row)
    {
        if (row is null) return;
        if (!await Ask.SureAsync(F("Stop importing from {0}?", row.Name),
                T("Its clips leave your library. Nothing is deleted: the files stay where they are, and you can add it again any time."),
                T("Remove"))) return;
        await DropRecorderAsync(row);
    }

    private async Task DropRecorderAsync(RecorderRow row)
    {
        var import = _settings.Current.Recorders.FirstOrDefault(i => i.Kind == row.Kind);
        _settings.Current.Recorders.RemoveAll(i => i.Kind == row.Kind);
        _settings.Save();
        row.IsAdded = false;
        if (import is { AddedFolder: true })
        {
            var folder = row.IsSteam ? SteamOutput : import.Path;
            if (Folders.FirstOrDefault(f => f.Path is { } p && string.Equals(p, folder, StringComparison.OrdinalIgnoreCase)) is { } f)
                await DropFolderAsync(f);
        }
    }

    [RelayCommand]
    private async Task ConvertSteamClipsAsync()
    {
        if (!Features.Recorders || _settings.Current.Recorders.FirstOrDefault(i => i.Kind == RecorderKind.Steam) is not { } steam) return;
        if (_steamCts is not null) return; // already running
        var cts = _steamCts = new CancellationTokenSource();
        var row = Recorders?.Rows.FirstOrDefault(r => r.IsSteam);
        if (row is not null) row.IsConverting = true;
        int done = 0, failed = 0;
        try
        {
            var output = SteamOutput;
            var converted = new HashSet<string>(steam.Converted, StringComparer.OrdinalIgnoreCase);
            var pending = await Task.Run(() => RecorderService.PendingSteamClips(steam.Path, output, converted), cts.Token);
            void Remember()
            {
                // Looked up again: saving Settings meanwhile replaces the recorder objects.
                if (_settings.Current.Recorders.FirstOrDefault(i => i.Kind == RecorderKind.Steam) is not { } live) return;
                if (converted.Count == live.Converted.Count) return;
                live.Converted = converted.ToList();
                _settings.Save();
            }
            Remember();
            foreach (var clip in pending)
            {
                if (await RecorderService.ConvertSteamClipAsync(clip, cts.Token))
                {
                    done++;
                    converted.Add(clip.Id);
                    Remember();
                }
                else failed++;
                if (row is not null) row.Pending = pending.Count - done - failed;
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            _steamCts = null;
            if (row is not null) row.IsConverting = false;
        }
        if (done > 0) ShowToast(P(done, "Copied {0} Steam clip into your library", "Copied {0} Steam clips into your library"));
        if (failed > 0) ShowToast(P(failed, "{0} Steam clip could not be copied (see the log)", "{0} Steam clips could not be copied (see the log)"));
        await CountSteamAsync();
    }

    private async Task CountSteamAsync()
    {
        if (Recorders?.Rows.FirstOrDefault(r => r.IsSteam) is not { IsAdded: true } row) return;
        var steam = _settings.Current.Recorders.FirstOrDefault(i => i.Kind == RecorderKind.Steam);
        if (steam is null) return;
        var output = SteamOutput;
        var converted = new HashSet<string>(steam.Converted, StringComparer.OrdinalIgnoreCase);
        row.Pending = (await Task.Run(() => RecorderService.PendingSteamClips(steam.Path, output, converted))).Count;
    }

    /// <summary>At launch: new Steam clips since last time become MP4s.</summary>
    private Task ImportRecordersAtLaunchAsync() =>
        Features.Recorders && _settings.Current.Recorders.Any(i => i.Kind == RecorderKind.Steam) ? ConvertSteamClipsAsync() : Task.CompletedTask;
}
