using System.Security.Cryptography;

namespace Cnema.Services;

/// <summary>An optional model the smart features can use, downloaded once and checked against its hash.</summary>
public sealed record ModelInfo(string Id, string File, long Bytes, string Sha256, string? Fallback);

/// <summary>
/// The optional models: nothing is downloaded until the user asks. They come from Savefile's own release
/// mirror (the "models" release of Cnema-releases), with the original source as a fallback, and a file
/// only counts once its SHA-256 matches. Everything runs on this computer afterwards.
/// </summary>
public static class ModelStore
{
    public const string Mirror = "https://github.com/TheRoben-Dev/Cnema-releases/releases/download/models/";

    /// <summary>Whisper base, multilingual, 5-bit (whisper.cpp format): voice search and captions.</summary>
    public static readonly ModelInfo Speech = new("speech", "ggml-base-q5_1.bin", 59_707_625,
        "422f1ae452ade6f30a004d7e5c6a43195e4433bc370bf23fac9cc591f01a8898",
        "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base-q5_1.bin");

    public static string Dir => Path.Combine(AppPaths.DataDir, "models");
    public static string PathOf(ModelInfo m) => Path.Combine(Dir, m.File);

    public static bool IsInstalled(ModelInfo m)
    {
        try { return new FileInfo(PathOf(m)) is { Exists: true } f && f.Length == m.Bytes; }
        catch { return false; }
    }

    public static void Remove(ModelInfo m)
    {
        foreach (var p in new[] { PathOf(m), PathOf(m) + ".part" })
            try { File.Delete(p); } catch (Exception ex) { Log.Warn($"Removing {p}: {ex.Message}"); }
    }

    /// <summary>Downloads the model (progress 0..1). Throws with a message fit to show when it can't.</summary>
    public static async Task DownloadAsync(ModelInfo m, IProgress<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Dir);
        var part = PathOf(m) + ".part";
        Exception? last = null;
        foreach (var url in new[] { Mirror + m.File, m.Fallback }.OfType<string>())
        {
            try
            {
                await FetchAsync(url, part, m, progress, ct);
                File.Move(part, PathOf(m), overwrite: true);
                Log.Info($"Model {m.Id} downloaded from {new Uri(url).Host}");
                return;
            }
            catch (OperationCanceledException) { TryDelete(part); throw; }
            catch (Exception ex)
            {
                Log.Warn($"Model {m.Id} from {url}: {ex.Message}");
                last = ex;
                TryDelete(part);
            }
        }
        throw new InvalidOperationException(last is HttpRequestException or TaskCanceledException
            ? T("Couldn't reach the download. Check the internet connection and try again.")
            : last?.Message ?? T("The download failed."));
    }

    private static async Task FetchAsync(string url, string part, ModelInfo m, IProgress<double>? progress, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Cnema-models");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30)); // until the response starts
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        resp.EnsureSuccessStatusCode();
        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using (var dst = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
        using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            var buf = new byte[1 << 16];
            long got = 0;
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                sha.AppendData(buf, 0, n);
                got += n;
                if (got > m.Bytes) throw new InvalidOperationException(T("The download was not the expected file."));
                progress?.Report((double)got / m.Bytes);
            }
            var hash = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
            if (got != m.Bytes || hash != m.Sha256)
                throw new InvalidOperationException(T("The download was not the expected file."));
        }
    }

    private static void TryDelete(string p)
    {
        try { File.Delete(p); } catch { /* best effort */ }
    }
}
