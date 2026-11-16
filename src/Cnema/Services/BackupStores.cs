using System.Net;
using System.Net.Sockets;
using System.Text;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Transfer;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace Cnema.Services;

/// <summary>
/// Where backups go: a few plain file operations on keys like "videos/Clips-3f9a1c/x.mp4" or
/// "library/index.json", relative to the backup's root (bucket prefix or server folder).
/// Nothing here deletes, except the connection test's own little file.
/// </summary>
public interface IBackupStore : IDisposable
{
    Task ConnectAsync(CancellationToken ct);
    /// <summary>Writes and removes a small test file, so a test proves backups can be written.</summary>
    Task CheckWritableAsync(CancellationToken ct);
    Task PutFileAsync(string key, string localPath, Action<long> progress, CancellationToken ct);
    Task PutTextAsync(string key, string text, CancellationToken ct);
    /// <summary>Null when there's no such file.</summary>
    Task<string?> GetTextAsync(string key, CancellationToken ct);
    /// <summary>False when there's no such file.</summary>
    Task<bool> GetFileAsync(string key, string localPath, Action<long> progress, CancellationToken ct);
    /// <summary>A failure that only concerns this one file (the backup carries on with the next).</summary>
    bool IsFileProblem(Exception ex);
}

/// <summary>An S3-compatible server: Garage, SeaweedFS, MinIO.</summary>
public sealed class S3BackupStore : IBackupStore
{
    private readonly AmazonS3Client _client;
    private readonly TransferUtility _transfer;
    private readonly string _bucket;
    private readonly string _root;

    public S3BackupStore(BackupSettings s, string secret)
    {
        _bucket = s.Bucket.Trim();
        var p = s.Prefix.Trim().Trim('/');
        _root = p.Length == 0 ? "" : p + "/";
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
        _client = new AmazonS3Client(new BasicAWSCredentials(s.AccessKey.Trim(), secret), config);
        _transfer = new TransferUtility(_client);
    }

    /// <summary>v0.28 kept the prefix in its keys ("savefile/videos/…"): both forms work.</summary>
    private string Full(string key) => _root.Length > 0 && key.StartsWith(_root, StringComparison.Ordinal) ? key : _root + key;

    public Task ConnectAsync(CancellationToken ct) =>
        _client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = _bucket, MaxKeys = 1, Prefix = _root }, ct);

    public async Task CheckWritableAsync(CancellationToken ct)
    {
        var key = Full(".savefile-test");
        await _client.PutObjectAsync(new PutObjectRequest { BucketName = _bucket, Key = key, ContentBody = "ok" }, ct);
        // A key that may only add (a sensible setup against ransomware) can't delete: that's fine.
        try { await _client.DeleteObjectAsync(_bucket, key, ct); }
        catch (AmazonS3Exception) { /* left behind; harmless */ }
    }

    public async Task PutFileAsync(string key, string localPath, Action<long> progress, CancellationToken ct)
    {
        var request = new TransferUtilityUploadRequest { BucketName = _bucket, Key = Full(key), FilePath = localPath, PartSize = 16 * 1024 * 1024 };
        request.UploadProgressEvent += (_, p) => progress(p.TransferredBytes);
        await _transfer.UploadAsync(request, ct);
    }

    public Task PutTextAsync(string key, string text, CancellationToken ct) =>
        _client.PutObjectAsync(new PutObjectRequest { BucketName = _bucket, Key = Full(key), ContentBody = text, ContentType = "application/json" }, ct);

    public async Task<string?> GetTextAsync(string key, CancellationToken ct)
    {
        try
        {
            using var r = await _client.GetObjectAsync(_bucket, Full(key), ct);
            using var reader = new StreamReader(r.ResponseStream, Encoding.UTF8);
            return await reader.ReadToEndAsync(ct);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound) { return null; }
    }

    public async Task<bool> GetFileAsync(string key, string localPath, Action<long> progress, CancellationToken ct)
    {
        var request = new TransferUtilityDownloadRequest { BucketName = _bucket, Key = Full(key), FilePath = localPath };
        request.WriteObjectProgressEvent += (_, p) => progress(p.TransferredBytes);
        try { await _transfer.DownloadAsync(request, ct); return true; }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound) { return false; }
    }

    public bool IsFileProblem(Exception ex) => ex is IOException or UnauthorizedAccessException
        || ex is AmazonS3Exception { StatusCode: not (HttpStatusCode.Forbidden or HttpStatusCode.NotFound) } a && a.ErrorCode is not ("InvalidAccessKeyId" or "SignatureDoesNotMatch" or "NoSuchBucket");

    /// <summary>The failure in plain words.</summary>
    public static string Describe(Exception ex, BackupSettings s) => ex switch
    {
        AmazonS3Exception { ErrorCode: "NoSuchBucket" } => F("The server has no bucket called \"{0}\". Make it on the server first.", s.Bucket.Trim()),
        AmazonS3Exception { StatusCode: HttpStatusCode.Forbidden } or AmazonS3Exception { ErrorCode: "InvalidAccessKeyId" or "SignatureDoesNotMatch" }
            => T("The server refused the keys. Check the access key and the secret key, and that they may use this bucket."),
        AmazonS3Exception a => F("The server said: {0}", a.Message),
        HttpRequestException or WebException or TaskCanceledException or AmazonServiceException { InnerException: HttpRequestException }
            => T("Couldn't reach the server. Check the address and that the server is on and on this network."),
        _ => ex.Message,
    };

    public void Dispose()
    {
        _transfer.Dispose();
        _client.Dispose();
    }
}

/// <summary>A folder on any computer you can reach over SSH (a Linux server, a NAS), through SFTP.</summary>
public sealed class SftpBackupStore : IBackupStore
{
    private readonly BackupSettings _s;
    private readonly SftpClient _client;
    private readonly string _root;
    private readonly HashSet<string> _made = new(StringComparer.Ordinal);
    private readonly string? _keyFile;
    private string? _seenKey;

    /// <summary>The server's key, trusted the first time (set when the settings had none yet).</summary>
    public string? NewHostKey { get; private set; }

    public sealed class HostKeyChangedException(string seen) : Exception("The server's key changed")
    {
        public string Seen { get; } = seen;
    }

    public sealed class KeyFileException(string message, Exception? inner = null) : Exception(message, inner);

    public SftpBackupStore(BackupSettings s, string? secret)
    {
        _s = s;
        var folder = s.RemoteFolder.Trim().Replace('\\', '/').TrimEnd('/');
        _root = folder.Length == 0 ? "." : folder;
        var user = s.User.Trim();
        var methods = new List<AuthenticationMethod>();
        if (s.UseKey)
        {
            _keyFile = BackupService.KeyFileFor(s);
            if (_keyFile is null) throw new KeyFileException(T("No SSH key found in your .ssh folder. Pick the key file, or sign in with a password."));
            PrivateKeyFile key;
            try { key = string.IsNullOrEmpty(secret) ? new PrivateKeyFile(_keyFile) : new PrivateKeyFile(_keyFile, secret); }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException)
            {
                throw new KeyFileException(F("Couldn't open the key file {0}.", _keyFile), ex);
            }
            catch (Exception ex)
            {
                throw new KeyFileException(string.IsNullOrEmpty(secret)
                    ? T("The SSH key is protected by a passphrase. Type it in the Passphrase box.")
                    : T("Couldn't unlock the SSH key. Check the passphrase."), ex);
            }
            methods.Add(new PrivateKeyAuthenticationMethod(user, key));
        }
        else
        {
            var password = secret ?? "";
            methods.Add(new PasswordAuthenticationMethod(user, password));
            // Many servers ask for the password as "keyboard-interactive" instead.
            var interactive = new KeyboardInteractiveAuthenticationMethod(user);
            interactive.AuthenticationPrompt += (_, e) =>
            {
                foreach (var prompt in e.Prompts) prompt.Response = password;
            };
            methods.Add(interactive);
        }
        var info = new ConnectionInfo(s.Host.Trim(), s.Port is > 0 and < 65536 ? s.Port : 22, user, methods.ToArray())
        {
            Timeout = TimeSpan.FromSeconds(15),
        };
        _client = new SftpClient(info) { OperationTimeout = TimeSpan.FromMinutes(5) };
        _client.HostKeyReceived += (_, e) =>
        {
            var seen = "SHA256:" + e.FingerPrintSHA256;
            _seenKey = seen;
            var trusted = _s.HostKey.Trim();
            if (trusted.Length == 0)
            {
                // Trust on first use, like ssh does; the fingerprint is shown in the panel to compare.
                NewHostKey = seen;
                e.CanTrust = true;
            }
            else e.CanTrust = string.Equals(trusted, seen, StringComparison.Ordinal);
        };
    }

    private string Full(string key) => _root + "/" + key;

    public async Task ConnectAsync(CancellationToken ct)
    {
        try { await _client.ConnectAsync(ct); }
        catch (SshConnectionException) when (_seenKey is { } seen && _s.HostKey.Trim().Length > 0 && _s.HostKey.Trim() != seen)
        {
            throw new HostKeyChangedException(seen);
        }
    }

    public async Task CheckWritableAsync(CancellationToken ct)
    {
        await PutTextAsync(".savefile-test", "ok", ct);
        try { await _client.DeleteFileAsync(Full(".savefile-test"), ct); }
        catch (SftpPermissionDeniedException) { /* left behind; harmless */ }
    }

    /// <summary>Makes the folders a file goes into, one level at a time (SFTP has no "mkdir -p").</summary>
    private async Task MakeFoldersFor(string fullPath, CancellationToken ct)
    {
        var dir = fullPath[..Math.Max(0, fullPath.LastIndexOf('/'))];
        if (dir.Length == 0 || _made.Contains(dir)) return;
        var parts = dir.Split('/');
        var path = dir.StartsWith('/') ? "" : null;
        foreach (var part in parts)
        {
            if (part.Length == 0) continue;
            path = path is null ? part : path + "/" + part;
            if (part == "." || _made.Contains(path)) continue;
            if (!await _client.ExistsAsync(path, ct))
            {
                try { await _client.CreateDirectoryAsync(path, ct); }
                catch (SftpException) when (_client.Exists(path)) { /* made meanwhile */ }
            }
            _made.Add(path);
        }
    }

    /// <summary>Writes to "name.part" and renames it into place, so a stopped upload never looks finished.</summary>
    private async Task UploadAsync(Stream source, string key, CancellationToken ct)
    {
        var target = Full(key);
        await MakeFoldersFor(target, ct);
        var partial = target + ".part";
        await _client.UploadFileAsync(source, partial, ct);
        try { _client.RenameFile(partial, target, true); }
        catch (Exception ex) when (ex is SftpException or SshException or NotSupportedException)
        {
            // A server without the replacing rename: replace the old copy of this very file (same key).
            if (await _client.ExistsAsync(target, ct)) await _client.DeleteFileAsync(target, ct);
            await _client.RenameFileAsync(partial, target, ct);
        }
    }

    public async Task PutFileAsync(string key, string localPath, Action<long> progress, CancellationToken ct)
    {
        await using var file = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, true);
        await using var counted = new CountingStream(file, progress, ct);
        await UploadAsync(counted, key, ct);
    }

    public async Task PutTextAsync(string key, string text, CancellationToken ct)
    {
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(text));
        await UploadAsync(ms, key, ct);
    }

    public async Task<string?> GetTextAsync(string key, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        try { await _client.DownloadFileAsync(Full(key), ms, ct); }
        catch (SftpPathNotFoundException) { return null; }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public async Task<bool> GetFileAsync(string key, string localPath, Action<long> progress, CancellationToken ct)
    {
        if (!await _client.ExistsAsync(Full(key), ct)) return false;
        await using var file = new FileStream(localPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true);
        await using var counted = new CountingStream(file, progress, ct);
        await _client.DownloadFileAsync(Full(key), counted, ct);
        return true;
    }

    public bool IsFileProblem(Exception ex) => ex is IOException or UnauthorizedAccessException && ex is not SshException;

    /// <summary>The failure in plain words.</summary>
    public static string Describe(Exception ex, BackupSettings s) => ex switch
    {
        HostKeyChangedException h => F("The server's key has changed since Savefile first connected (it's now {0}). If you reinstalled the server, choose Forget server key and test again. Otherwise, check what's answering on this address.", h.Seen),
        KeyFileException k => k.Message,
        SshAuthenticationException => s.UseKey
            ? F("The server didn't accept the SSH key {0}. Check the user name, and that the key's public half is in ~/.ssh/authorized_keys on the server.", Path.GetFileName(BackupService.KeyFileFor(s) ?? ""))
            : T("The server refused the user name or password."),
        SftpPermissionDeniedException => F("{0} isn't allowed to write to \"{1}\" on the server. Pick another folder, or change its permissions.", s.User.Trim(), s.RemoteFolder.Trim()),
        SftpPathNotFoundException => F("Couldn't find \"{0}\" on the server.", s.RemoteFolder.Trim()),
        SocketException or SshConnectionException or SshOperationTimeoutException or ProxyException or TimeoutException
            => T("Couldn't reach the server. Check its name or address, the port, and that it's on and on this network."),
        SshException other => F("The server said: {0}", other.Message),
        _ => ex.Message,
    };

    public void Dispose()
    {
        try { if (_client.IsConnected) _client.Disconnect(); } catch { /* gone */ }
        _client.Dispose();
    }

    /// <summary>Counts bytes going through (for progress) and stops when cancelled.</summary>
    private sealed class CountingStream(Stream inner, Action<long> progress, CancellationToken ct) : Stream
    {
        private long _count;
        private long _reported;

        private void Count(int n)
        {
            ct.ThrowIfCancellationRequested();
            _count += n;
            // Every 256 KB is plenty for a progress bar.
            if (n == 0 || _count - _reported >= 256 * 1024) { _reported = _count; progress(_count); }
        }

        public override int Read(byte[] buffer, int offset, int count) { var n = inner.Read(buffer, offset, count); Count(n); return n; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            var n = await inner.ReadAsync(buffer, token);
            Count(n);
            return n;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) =>
            ReadAsync(buffer.AsMemory(offset, count), token).AsTask();
        public override void Write(byte[] buffer, int offset, int count) { inner.Write(buffer, offset, count); Count(count); }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        {
            await inner.WriteAsync(buffer, token);
            Count(buffer.Length);
        }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token) =>
            WriteAsync(buffer.AsMemory(offset, count), token).AsTask();
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken token) => inner.FlushAsync(token);
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => inner.SetLength(value);
    }
}
