using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cnema.Models;

namespace Cnema.Services;

/// <summary>What one video looked like when it was backed up.</summary>
public sealed class BackedUpVideo
{
    public string Key { get; set; } = "";
    public long Size { get; set; }
    public DateTime ModifiedUtc { get; set; }
    public DateTime UploadedUtc { get; set; }
}

public sealed class BackupStateFile
{
    public Dictionary<string, BackedUpVideo> Videos { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Where these were backed up to (BackupService.TargetOf).</summary>
    public string? Target { get; set; }
}

/// <summary>A video's library details as stored next to it on the server, so a restore brings them back.</summary>
public sealed class BackupIndexEntry
{
    public string Key { get; set; } = "";
    public string OriginalPath { get; set; } = "";
    public long Size { get; set; }
    public DateTime ModifiedUtc { get; set; }
    public VideoEntry Details { get; set; } = new();
}

public sealed record BackupProgress(int Done, int Total, long Bytes, long TotalBytes, string Current);

public sealed record BackupResult(string Text, bool Ok);

/// <summary>
/// Backs videos and the library up to your own server: a folder on it over SFTP (SSH), or its
/// S3-compatible API (Garage, SeaweedFS, MinIO), on your network (see BackupStores.cs).
/// Nothing is ever deleted on the server: a video removed here
/// stays backed up. Nothing goes anywhere else: the only address used is the one you type in.
///
/// Layout under the server folder (SFTP) or the bucket prefix (S3):
///   videos/&lt;folder&gt;-&lt;id&gt;/&lt;path inside that folder&gt;   the videos themselves
///   library/index.json                       favourites, tags, friends, notes per video
///   library/settings.json, collections.json, edits.json
/// </summary>
public sealed class BackupService
{
    public const string SecretName = "backup-secret";

    private static string StateFile => Path.Combine(AppPaths.DataDir, "backup.json");
    private readonly SemaphoreSlim _run = new(1, 1);
    private BackupStateFile _state;

    public BackupService()
    {
        _state = JsonStore.Load<BackupStateFile>(StateFile) ?? new BackupStateFile();
        _state.Videos = new Dictionary<string, BackedUpVideo>(_state.Videos, StringComparer.OrdinalIgnoreCase);
    }

    public bool IsRunning => _run.CurrentCount == 0;

    /// <summary>True when this exact file (same size and date) is on the server.</summary>
    public bool IsBackedUp(VideoEntry e)
    {
        lock (_state) return _state.Videos.TryGetValue(e.Path, out var b) && b.Size == e.Size && b.ModifiedUtc == e.ModifiedUtc;
    }

    public int BackedUpCount { get { lock (_state) return _state.Videos.Count; } }

    /// <summary>A video renamed or moved in Savefile keeps its backed-up mark.</summary>
    public void Moved(string from, string to)
    {
        lock (_state)
        {
            if (!_state.Videos.Remove(from, out var b)) return;
            _state.Videos[to] = b;
        }
        Save();
    }

    private void Save()
    {
        lock (_state) JsonStore.Save(StateFile, _state);
    }

    // ---------- connection ----------

    /// <summary>The keychain entry holding the S3 secret key, or the SFTP password / key passphrase.</summary>
    public static string SecretNameFor(BackupSettings s) => s.IsSftp ? SftpSecretName : SecretName;
    public const string SftpSecretName = "backup-sftp-secret";

    /// <summary>Where the backups go, as one line: marks from another place don't count here.</summary>
    public static string TargetOf(BackupSettings s) => s.IsSftp
        ? $"sftp://{s.User.Trim()}@{s.Host.Trim()}:{s.Port}/{s.RemoteFolder.Trim().Trim('/')}"
        : $"s3:{s.Endpoint.Trim().TrimEnd('/')}/{s.Bucket.Trim()}/{s.Prefix.Trim().Trim('/')}";

    /// <summary>
    /// Settings now point somewhere else: forget which videos were backed up (they aren't there).
    /// The first time (settings from v0.28, which didn't record this) the marks are kept.
    /// </summary>
    public void UseTarget(BackupSettings s)
    {
        if (!s.IsSetUp) return;
        var target = TargetOf(s);
        lock (_state)
        {
            if (_state.Target == target) return;
            if (_state.Target is not null) _state.Videos.Clear();
            _state.Target = target;
        }
        Save();
    }

    /// <summary>The SSH key to sign in with: the one picked, or the usual ones in ~/.ssh.</summary>
    public static string? KeyFileFor(BackupSettings s)
    {
        if (s.KeyFile.Trim() is { Length: > 0 } picked)
        {
            if (picked.StartsWith('~')) picked = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), picked.TrimStart('~', '/', '\\'));
            return picked;
        }
        var ssh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
        return new[] { "id_ed25519", "id_ecdsa", "id_rsa" }.Select(n => Path.Combine(ssh, n)).FirstOrDefault(File.Exists);
    }

    private static async Task<IBackupStore> OpenAsync(BackupSettings s, string? secret, CancellationToken ct)
    {
        IBackupStore store = s.IsSftp ? new SftpBackupStore(s, secret) : new S3BackupStore(s, secret ?? "");
        try
        {
            await store.ConnectAsync(ct);
            // First connection to this SSH server: remember its key, so a server pretending to be it is refused later.
            if (store is SftpBackupStore { NewHostKey: { } key }) s.HostKey = key;
            return store;
        }
        catch
        {
            store.Dispose();
            throw;
        }
    }

    /// <summary>Checks that the server answers, signing in works, and backups can be written. Null when all is well.</summary>
    public async Task<string?> TestAsync(BackupSettings s, string? secret, CancellationToken ct = default)
    {
        if (Problem(s, secret) is { } problem) return problem;
        IBackupStore? store = null;
        try
        {
            store = await OpenAsync(s, secret, ct);
            await store.CheckWritableAsync(ct);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Explain(ex, s);
        }
        finally
        {
            store?.Dispose();
        }
    }

    /// <summary>What's missing before a backup can run, in words; null when nothing is.</summary>
    public static string? Problem(BackupSettings s, string? secret)
    {
        if (s.IsSftp)
        {
            if (s.Host.Trim().Length == 0 || s.Host.Contains("://")) return T("Type the server's name or address, such as 192.168.1.20 or myserver.local.");
            if (s.User.Trim().Length == 0) return T("Type your user name on the server.");
            if (s.UseKey && KeyFileFor(s) is null) return T("No SSH key found in your .ssh folder. Pick the key file, or sign in with a password.");
            if (s.UseKey && !File.Exists(KeyFileFor(s))) return F("Couldn't find the key file {0}.", KeyFileFor(s)!);
            if (!s.UseKey && string.IsNullOrEmpty(secret)) return T("Type the password.");
            return null;
        }
        if (!Uri.TryCreate(s.Endpoint.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return T("Type the server's address, such as http://192.168.1.20:3900.");
        if (s.Bucket.Trim().Length == 0) return T("Type the name of the bucket to back up to.");
        if (s.AccessKey.Trim().Length == 0 || string.IsNullOrEmpty(secret)) return T("Type the access key and the secret key.");
        return null;
    }

    private static string Explain(Exception ex, BackupSettings s)
    {
        Log.Warn($"Backup: {ex.GetType().Name}: {ex.Message}");
        if (ex is SftpBackupStore.KeyFileException k) return k.Message;
        return s.IsSftp ? SftpBackupStore.Describe(ex, s) : S3BackupStore.Describe(ex, s);
    }

    // ---------- backing up ----------

    /// <summary>
    /// Uploads every chosen video that isn't on the server yet (or changed since), then the library
    /// details. Returns a line to show; throws OperationCanceledException when stopped.
    /// </summary>
    public async Task<BackupResult> BackUpAsync(BackupSettings s, string? secret, IReadOnlyList<VideoEntry> library,
        IReadOnlyList<string> folders, IProgress<BackupProgress>? progress, CancellationToken ct)
    {
        if (Problem(s, secret) is { } problem) return new(problem, false);
        if (!await _run.WaitAsync(0, ct)) return new(T("A backup is already running."), false);
        IBackupStore? store = null;
        try
        {
            UseTarget(s);
            var chosen = library.Where(e => (s.Everything || e.Favorite) && File.Exists(e.Path)).ToList();
            var todo = chosen.Where(e => !IsBackedUp(e)).OrderBy(e => e.ModifiedUtc).ToList();
            var totalBytes = todo.Sum(e => e.Size);
            progress?.Report(new BackupProgress(0, todo.Count, 0, totalBytes, ""));
            try { store = await OpenAsync(s, secret, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { return new(Explain(ex, s), false); }
            long doneBytes = 0;
            var done = 0;
            var failed = 0;
            foreach (var e in todo)
            {
                ct.ThrowIfCancellationRequested();
                var name = Path.GetFileName(e.Path);
                progress?.Report(new BackupProgress(done, todo.Count, doneBytes, totalBytes, name));
                var key = KeyFor(e.Path, folders);
                var before = doneBytes;
                var count = done;
                try
                {
                    await store.PutFileAsync(key, e.Path, sent => progress?.Report(new BackupProgress(count, todo.Count, before + sent, totalBytes, name)), ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException && store.IsFileProblem(ex))
                {
                    // One file that can't be read (in use, gone) doesn't stop the rest.
                    Log.Warn($"Backup of {e.Path} failed: {ex.Message}");
                    failed++;
                    continue;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    return new(Explain(ex, s), false);
                }
                lock (_state)
                    _state.Videos[e.Path] = new BackedUpVideo { Key = key, Size = e.Size, ModifiedUtc = e.ModifiedUtc, UploadedUtc = DateTime.UtcNow };
                Save();
                doneBytes += e.Size;
                done++;
            }
            progress?.Report(new BackupProgress(done, todo.Count, doneBytes, totalBytes, ""));
            try
            {
                await UploadLibraryAsync(store, library, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return new(Explain(ex, s), false);
            }
            var already = chosen.Count - todo.Count;
            if (failed > 0) return new(F("{0} backed up, {1} couldn't be read. They'll be tried again next time.", P(done, "{0} video", "{0} videos"), failed), false);
            return new(done == 0
                ? P(already, "Everything is backed up ({0} video).", "Everything is backed up ({0:N0} videos).")
                : F("{0} backed up. {1} on the server in all.", P(done, "{0} video", "{0:N0} videos"), BackedUpCount.ToString("N0")), true);
        }
        finally
        {
            store?.Dispose();
            _run.Release();
        }
    }

    /// <summary>Favourites, tags, friends and notes, plus Savefile's own files, next to the videos.</summary>
    private async Task UploadLibraryAsync(IBackupStore store, IReadOnlyList<VideoEntry> library, CancellationToken ct)
    {
        List<BackupIndexEntry> index;
        lock (_state)
            index = library.Where(e => _state.Videos.ContainsKey(e.Path)).Select(e => new BackupIndexEntry
            {
                Key = Rel(_state.Videos[e.Path].Key),
                OriginalPath = e.Path,
                Size = e.Size,
                ModifiedUtc = e.ModifiedUtc,
                Details = UserData(e),
            }).ToList();
        // Keep what an earlier index knew about videos no longer in this library (deleted here, still on the server).
        var known = await ReadIndexAsync(store, ct);
        var keys = index.Select(i => i.Key).ToHashSet(StringComparer.Ordinal);
        index.AddRange(known.Where(k => !keys.Contains(k.Key)));
        await store.PutTextAsync("library/index.json", JsonSerializer.Serialize(index, new JsonSerializerOptions { WriteIndented = true }), ct);
        foreach (var file in new[] { AppPaths.SettingsFile, AppPaths.CollectionsFile, Path.Combine(AppPaths.DataDir, "edits.json") })
        {
            if (!File.Exists(file)) continue;
            await store.PutTextAsync("library/" + Path.GetFileName(file), await File.ReadAllTextAsync(file, ct), ct);
        }
    }

    private static VideoEntry UserData(VideoEntry e)
    {
        var copy = new VideoEntry();
        copy.CopyUserDataFrom(e);
        return copy;
    }

    private static async Task<List<BackupIndexEntry>> ReadIndexAsync(IBackupStore store, CancellationToken ct)
    {
        if (await store.GetTextAsync("library/index.json", ct) is not { } json) return new();
        var list = JsonSerializer.Deserialize<List<BackupIndexEntry>>(json) ?? new();
        foreach (var i in list) i.Key = Rel(i.Key);
        return list;
    }

    /// <summary>Keys from v0.28 carry the bucket prefix ("savefile/videos/…"): drop it.</summary>
    private static string Rel(string key)
    {
        if (key.StartsWith("videos/", StringComparison.Ordinal)) return key;
        var i = key.IndexOf("/videos/", StringComparison.Ordinal);
        return i >= 0 ? key[(i + 1)..] : key;
    }

    /// <summary>
    /// "videos/Clips-3f9a1c/Valorant/Clip 2026-11-16.mp4": the library folder's name plus a short id
    /// (two folders can share a name), then the path inside it.
    /// </summary>
    public static string KeyFor(string path, IReadOnlyList<string> folders)
    {
        var root = folders.Where(f => IsUnder(path, f)).OrderByDescending(f => f.Length).FirstOrDefault();
        string label, rest;
        if (root is null)
        {
            var dir = Path.GetDirectoryName(path) ?? "";
            label = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) is { Length: > 0 } n ? n : "Videos";
            root = dir;
            rest = Path.GetFileName(path);
        }
        else
        {
            label = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) is { Length: > 0 } n ? n : "Drive";
            rest = Path.GetRelativePath(root, path);
        }
        var id = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(root.ToLowerInvariant())))[..6].ToLowerInvariant();
        return $"videos/{label}-{id}/{rest.Replace('\\', '/')}";
    }

    private static bool IsUnder(string path, string folder)
    {
        var f = folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return path.StartsWith(f, StringComparison.OrdinalIgnoreCase);
    }

    // ---------- restoring ----------

    public sealed record RestorePlan(List<BackupIndexEntry> Missing, long Bytes);

    /// <summary>The backed-up videos that aren't in this library (a new computer, or deleted here).</summary>
    public async Task<RestorePlan> PlanRestoreAsync(BackupSettings s, string? secret, IReadOnlyList<VideoEntry> library, CancellationToken ct)
    {
        using var store = await OpenAsync(s, secret, ct);
        var index = await ReadIndexAsync(store, ct);
        var here = library.Select(e => (e.Size, e.ModifiedUtc, Name: Path.GetFileName(e.Path)))
            .ToHashSet();
        var missing = index.Where(i => !here.Contains((i.Size, i.ModifiedUtc, NameOf(i)))).ToList();
        return new RestorePlan(missing, missing.Sum(m => m.Size));
    }

    /// <summary>
    /// Downloads the given videos into <paramref name="target"/>, keeping their folders, dates and (through
    /// <paramref name="adopt"/>) their favourites, tags, friends and notes.
    /// </summary>
    public async Task<BackupResult> RestoreAsync(BackupSettings s, string? secret, RestorePlan plan, string target,
        Action<string, VideoEntry> adopt, IProgress<BackupProgress>? progress, CancellationToken ct)
    {
        if (!await _run.WaitAsync(0, ct)) return new(T("A backup is already running."), false);
        IBackupStore? store = null;
        try
        {
            try { store = await OpenAsync(s, secret, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { return new(Explain(ex, s), false); }
            UseTarget(s);
            long doneBytes = 0;
            var done = 0;
            foreach (var item in plan.Missing)
            {
                ct.ThrowIfCancellationRequested();
                var key = Rel(item.Key);
                var inside = key.StartsWith("videos/", StringComparison.Ordinal) ? key["videos/".Length..] : NameOf(item);
                // "Clips-3f9a1c/Valorant/x.mp4" → "Clips/Valorant/x.mp4"
                var parts = inside.Split('/');
                if (parts.Length > 1 && parts[0].LastIndexOf('-') is var dash and > 0) parts[0] = parts[0][..dash];
                var local = Path.Combine(new[] { target }.Concat(parts.Select(SafeName)).ToArray());
                if (File.Exists(local))
                {
                    var there = new FileInfo(local);
                    if (there.Length == item.Size && there.LastWriteTimeUtc == item.ModifiedUtc)
                    {
                        // Restored before (a restore that was stopped): just give it its details again.
                        adopt(local, item.Details);
                        lock (_state)
                            _state.Videos[local] = new BackedUpVideo { Key = key, Size = item.Size, ModifiedUtc = item.ModifiedUtc, UploadedUtc = DateTime.UtcNow };
                        Save();
                        done++;
                        continue;
                    }
                    // Another video with the same name (two folders called Clips): keep both.
                    var stem = Path.Combine(Path.GetDirectoryName(local)!, Path.GetFileNameWithoutExtension(local));
                    var ext = Path.GetExtension(local);
                    for (var n = 2; File.Exists(local); n++) local = $"{stem} ({n}){ext}";
                }
                Directory.CreateDirectory(Path.GetDirectoryName(local)!);
                var partial = local + ".part";
                var before = doneBytes;
                var count = done;
                var name = Path.GetFileName(local);
                try
                {
                    if (!await store.GetFileAsync(key, partial, got => progress?.Report(new BackupProgress(count, plan.Missing.Count, before + got, plan.Bytes, name)), ct))
                    {
                        try { File.Delete(partial); } catch { /* nothing written */ }
                        continue;
                    }
                    File.SetLastWriteTimeUtc(partial, item.ModifiedUtc);
                    // Details first, so a folder scan that sees the file right away already finds them.
                    adopt(local, item.Details);
                    File.Move(partial, local);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    try { File.Delete(partial); } catch { /* nothing written */ }
                    return new(Explain(ex, s), false);
                }
                catch (OperationCanceledException)
                {
                    try { File.Delete(partial); } catch { /* nothing written */ }
                    throw;
                }
                // It's on the server already: no need to upload it again.
                lock (_state)
                    _state.Videos[local] = new BackedUpVideo { Key = key, Size = item.Size, ModifiedUtc = item.ModifiedUtc, UploadedUtc = DateTime.UtcNow };
                Save();
                doneBytes += item.Size;
                done++;
            }
            return new(P(done, "{0} video restored.", "{0:N0} videos restored."), true);
        }
        finally
        {
            store?.Dispose();
            _run.Release();
        }
    }

    /// <summary>The file name, whichever system backed it up ("C:\\Clips\\x.mp4" on Windows).</summary>
    private static string NameOf(BackupIndexEntry i) => i.OriginalPath.Replace('\\', '/').Split('/')[^1];

    private static string SafeName(string part)
    {
        var bad = Path.GetInvalidFileNameChars();
        var clean = new string(part.Select(c => bad.Contains(c) ? '_' : c).ToArray()).Trim();
        return clean is "" or "." or ".." ? "_" : clean;
    }
}
