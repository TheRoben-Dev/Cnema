using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Transfer;
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
/// Backs videos and the library up to your own server, over its S3-compatible API (Garage, SeaweedFS,
/// MinIO and the like, on your network). Nothing is ever deleted on the server: a video removed here
/// stays backed up. Nothing goes anywhere else: the only address used is the one you type in.
///
/// Layout in the bucket, under the prefix:
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

    private static AmazonS3Client Client(BackupSettings s, string secret)
    {
        var config = new AmazonS3Config
        {
            ServiceURL = s.Endpoint.Trim(),
            // Home servers answer on one address for every bucket: http://server:port/bucket/key.
            ForcePathStyle = true,
            AuthenticationRegion = string.IsNullOrWhiteSpace(s.Region) ? "us-east-1" : s.Region.Trim(),
            // Only send checksums when a request needs one; newer default checksums trip up some home servers.
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
            Timeout = TimeSpan.FromMinutes(5),
            MaxErrorRetry = 3,
        };
        return new AmazonS3Client(new BasicAWSCredentials(s.AccessKey.Trim(), secret), config);
    }

    private static string Root(BackupSettings s)
    {
        var p = s.Prefix.Trim().Trim('/');
        return p.Length == 0 ? "" : p + "/";
    }

    /// <summary>Checks that the server answers, the keys work, and the bucket can be written to. Null when all is well.</summary>
    public async Task<string?> TestAsync(BackupSettings s, string secret, CancellationToken ct = default)
    {
        if (Problem(s, secret) is { } problem) return problem;
        try
        {
            using var client = Client(s, secret);
            await client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = s.Bucket.Trim(), MaxKeys = 1, Prefix = Root(s) }, ct);
            var key = Root(s) + ".savefile-test";
            await client.PutObjectAsync(new PutObjectRequest { BucketName = s.Bucket.Trim(), Key = key, ContentBody = "ok" }, ct);
            // A key that may only add (a sensible setup against ransomware) can't delete: that's fine.
            try { await client.DeleteObjectAsync(s.Bucket.Trim(), key, ct); }
            catch (AmazonS3Exception) { /* left behind; harmless */ }
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Explain(ex, s);
        }
    }

    /// <summary>What's missing before a backup can run, in words; null when nothing is.</summary>
    public static string? Problem(BackupSettings s, string? secret)
    {
        if (!Uri.TryCreate(s.Endpoint.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return T("Type the server's address, such as http://192.168.1.20:3900.");
        if (s.Bucket.Trim().Length == 0) return T("Type the name of the bucket to back up to.");
        if (s.AccessKey.Trim().Length == 0 || string.IsNullOrEmpty(secret)) return T("Type the access key and the secret key.");
        return null;
    }

    private static string Explain(Exception ex, BackupSettings s)
    {
        Log.Warn($"Backup: {ex.GetType().Name}: {ex.Message}");
        return ex switch
        {
            AmazonS3Exception { ErrorCode: "NoSuchBucket" } => F("The server has no bucket called \"{0}\". Make it on the server first.", s.Bucket.Trim()),
            AmazonS3Exception { StatusCode: HttpStatusCode.Forbidden } or AmazonS3Exception { ErrorCode: "InvalidAccessKeyId" or "SignatureDoesNotMatch" }
                => T("The server refused the keys. Check the access key and the secret key, and that they may use this bucket."),
            AmazonS3Exception a => F("The server said: {0}", a.Message),
            HttpRequestException or WebException or TaskCanceledException or AmazonServiceException { InnerException: HttpRequestException }
                => T("Couldn't reach the server. Check the address and that the server is on and on this network."),
            _ => ex.Message,
        };
    }

    // ---------- backing up ----------

    /// <summary>
    /// Uploads every chosen video that isn't on the server yet (or changed since), then the library
    /// details. Returns a line to show; throws OperationCanceledException when stopped.
    /// </summary>
    public async Task<BackupResult> BackUpAsync(BackupSettings s, string secret, IReadOnlyList<VideoEntry> library,
        IReadOnlyList<string> folders, IProgress<BackupProgress>? progress, CancellationToken ct)
    {
        if (Problem(s, secret) is { } problem) return new(problem, false);
        if (!await _run.WaitAsync(0, ct)) return new(T("A backup is already running."), false);
        try
        {
            using var client = Client(s, secret);
            using var transfer = new TransferUtility(client);
            var bucket = s.Bucket.Trim();
            var chosen = library.Where(e => (s.Everything || e.Favorite) && File.Exists(e.Path)).ToList();
            var todo = chosen.Where(e => !IsBackedUp(e)).OrderBy(e => e.ModifiedUtc).ToList();
            var totalBytes = todo.Sum(e => e.Size);
            long doneBytes = 0;
            var done = 0;
            var failed = 0;
            foreach (var e in todo)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(new BackupProgress(done, todo.Count, doneBytes, totalBytes, Path.GetFileName(e.Path)));
                var key = Root(s) + KeyFor(e.Path, folders);
                var request = new TransferUtilityUploadRequest
                {
                    BucketName = bucket,
                    Key = key,
                    FilePath = e.Path,
                    PartSize = 16 * 1024 * 1024,
                };
                var before = doneBytes;
                request.UploadProgressEvent += (_, p) =>
                    progress?.Report(new BackupProgress(done, todo.Count, before + p.TransferredBytes, totalBytes, Path.GetFileName(e.Path)));
                try
                {
                    await transfer.UploadAsync(request, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is AmazonS3Exception or IOException)
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
                await UploadLibraryAsync(client, s, library);
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
            _run.Release();
        }
    }

    /// <summary>Favourites, tags, friends and notes, plus Savefile's own files, next to the videos.</summary>
    private async Task UploadLibraryAsync(AmazonS3Client client, BackupSettings s, IReadOnlyList<VideoEntry> library)
    {
        var bucket = s.Bucket.Trim();
        List<BackupIndexEntry> index;
        lock (_state)
            index = library.Where(e => _state.Videos.ContainsKey(e.Path)).Select(e => new BackupIndexEntry
            {
                Key = _state.Videos[e.Path].Key,
                OriginalPath = e.Path,
                Size = e.Size,
                ModifiedUtc = e.ModifiedUtc,
                Details = UserData(e),
            }).ToList();
        // Keep what an earlier index knew about videos no longer in this library (deleted here, still on the server).
        var known = await ReadIndexAsync(client, s, CancellationToken.None);
        var keys = index.Select(i => i.Key).ToHashSet(StringComparer.Ordinal);
        index.AddRange(known.Where(k => !keys.Contains(k.Key)));
        await client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = bucket,
            Key = Root(s) + "library/index.json",
            ContentBody = JsonSerializer.Serialize(index, new JsonSerializerOptions { WriteIndented = true }),
            ContentType = "application/json",
        });
        foreach (var file in new[] { AppPaths.SettingsFile, AppPaths.CollectionsFile, Path.Combine(AppPaths.DataDir, "edits.json") })
        {
            if (!File.Exists(file)) continue;
            await client.PutObjectAsync(new PutObjectRequest
            {
                BucketName = bucket,
                Key = Root(s) + "library/" + Path.GetFileName(file),
                ContentBody = await File.ReadAllTextAsync(file),
                ContentType = "application/json",
            });
        }
    }

    private static VideoEntry UserData(VideoEntry e)
    {
        var copy = new VideoEntry();
        copy.CopyUserDataFrom(e);
        return copy;
    }

    private static async Task<List<BackupIndexEntry>> ReadIndexAsync(AmazonS3Client client, BackupSettings s, CancellationToken ct)
    {
        try
        {
            using var r = await client.GetObjectAsync(s.Bucket.Trim(), Root(s) + "library/index.json", ct);
            return await JsonSerializer.DeserializeAsync<List<BackupIndexEntry>>(r.ResponseStream, cancellationToken: ct) ?? new();
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return new();
        }
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
    public async Task<RestorePlan> PlanRestoreAsync(BackupSettings s, string secret, IReadOnlyList<VideoEntry> library, CancellationToken ct)
    {
        using var client = Client(s, secret);
        var index = await ReadIndexAsync(client, s, ct);
        var here = library.Select(e => (e.Size, e.ModifiedUtc, Name: Path.GetFileName(e.Path)))
            .ToHashSet();
        var missing = index.Where(i => !here.Contains((i.Size, i.ModifiedUtc, NameOf(i)))).ToList();
        return new RestorePlan(missing, missing.Sum(m => m.Size));
    }

    /// <summary>
    /// Downloads the given videos into <paramref name="target"/>, keeping their folders, dates and (through
    /// <paramref name="adopt"/>) their favourites, tags, friends and notes.
    /// </summary>
    public async Task<BackupResult> RestoreAsync(BackupSettings s, string secret, RestorePlan plan, string target,
        Action<string, VideoEntry> adopt, IProgress<BackupProgress>? progress, CancellationToken ct)
    {
        if (!await _run.WaitAsync(0, ct)) return new(T("A backup is already running."), false);
        try
        {
            using var client = Client(s, secret);
            using var transfer = new TransferUtility(client);
            var prefix = Root(s) + "videos/";
            long doneBytes = 0;
            var done = 0;
            foreach (var item in plan.Missing)
            {
                ct.ThrowIfCancellationRequested();
                var inside = item.Key.StartsWith(prefix, StringComparison.Ordinal) ? item.Key[prefix.Length..] : NameOf(item);
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
                            _state.Videos[local] = new BackedUpVideo { Key = item.Key, Size = item.Size, ModifiedUtc = item.ModifiedUtc, UploadedUtc = DateTime.UtcNow };
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
                var request = new TransferUtilityDownloadRequest { BucketName = s.Bucket.Trim(), Key = item.Key, FilePath = partial };
                var before = doneBytes;
                request.WriteObjectProgressEvent += (_, p) =>
                    progress?.Report(new BackupProgress(done, plan.Missing.Count, before + p.TransferredBytes, plan.Bytes, Path.GetFileName(local)));
                try
                {
                    await transfer.DownloadAsync(request, ct);
                    File.SetLastWriteTimeUtc(partial, item.ModifiedUtc);
                    // Details first, so a folder scan that sees the file right away already finds them.
                    adopt(local, item.Details);
                    File.Move(partial, local);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    try { File.Delete(partial); } catch { /* nothing written */ }
                    if (ex is AmazonS3Exception { StatusCode: HttpStatusCode.NotFound }) continue;
                    return new(Explain(ex, s), false);
                }
                catch (OperationCanceledException)
                {
                    try { File.Delete(partial); } catch { /* nothing written */ }
                    throw;
                }
                // It's on the server already: no need to upload it again.
                lock (_state)
                    _state.Videos[local] = new BackedUpVideo { Key = item.Key, Size = item.Size, ModifiedUtc = item.ModifiedUtc, UploadedUtc = DateTime.UtcNow };
                Save();
                doneBytes += item.Size;
                done++;
            }
            return new(P(done, "{0} video restored.", "{0:N0} videos restored."), true);
        }
        finally
        {
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
