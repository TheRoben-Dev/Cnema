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

    /// <summary>Which speech model the person picked: "standard" (Whisper base) or "swedish" (KB-Whisper small).</summary>
    public static string SpeechChoice { get; set; } = "standard";

    /// <summary>The speech model in use (see <see cref="SpeechChoice"/>).</summary>
    public static ModelInfo Speech => SpeechChoice == "swedish" ? SpeechSwedish : SpeechStandard;

    /// <summary>
    /// KB-Whisper small from the National Library of Sweden (Apache-2.0), 5-bit: much better at Swedish,
    /// still fine with English mixed in.
    /// </summary>
    public static readonly ModelInfo SpeechSwedish = new("speech-sv", "kb-whisper-small-q5_0.bin", 175_209_680,
        "6768836a51abc902e420c613153e6d418c90ea2774e913274d02ab23170225b7",
        "https://huggingface.co/KBLab/kb-whisper-small/resolve/main/ggml-model-q5_0.bin");

    /// <summary>Whisper base, multilingual, 5-bit (whisper.cpp format): voice search and captions.</summary>
    public static readonly ModelInfo SpeechStandard = new("speech", "ggml-base-q5_1.bin", 59_707_625,
        "422f1ae452ade6f30a004d7e5c6a43195e4433bc370bf23fac9cc591f01a8898",
        "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base-q5_1.bin");

    /// <summary>ONNX Runtime 1.23.2 (MIT) for this computer, from the official NuGet package; only on the mirror.</summary>
    public static ModelInfo OnnxRuntime { get; } =
        OperatingSystem.IsWindows() ? new("visual", "onnxruntime-1.23.2-win-x64.dll", 14_186_016, "dec964ab1ee36cc9b0ae247d13b376627992fc57dec0454354017ab8fd84f1ea", null)
        : OperatingSystem.IsMacOS() && System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64
            ? new("visual", "onnxruntime-1.23.2-osx-arm64.dylib", 35_138_784, "d306d2bc768540766c7ed8a1e0ff05d2870c77a934ebeee4a7bafa1b732ef299", null)
        : OperatingSystem.IsMacOS()
            ? new("visual", "onnxruntime-1.23.2-osx-x64.dylib", 39_742_608, "8c9c78de65ea3786f987c0d980e9c1b13a3a5fbc6b3e2965ba05b450e6e4c054", null)
        : new("visual", "onnxruntime-1.23.2-linux-x64.so", 22_326_072, "13ab8084954fa4a47c777880180b90810d6020f021441395712b48a75b74c68b", null);

    /// <summary>
    /// CLIP ViT-B/32 (OpenAI, MIT), 8-bit ONNX: visual search, similar videos, look-alike copies. With the
    /// ONNX Runtime library for this computer, which only this feature needs, so it's downloaded with it.
    /// </summary>
    public static IReadOnlyList<ModelInfo> Visual { get; } = new[]
    {
        new ModelInfo("visual", "clip-vit-b32-vision-q8.onnx", 89_117_001, "583fd1110a514667812fee7d684952aaf82a99b959760c8d7dca7e0ab9839299",
            "https://huggingface.co/Xenova/clip-vit-base-patch32/resolve/main/onnx/vision_model_quantized.onnx"),
        new ModelInfo("visual", "clip-vit-b32-text-q8.onnx", 64_504_507, "73baab855d406190da9faa498cfedf65f15cf309f4cc7385b7b032e6d08e5c3a",
            "https://huggingface.co/Xenova/clip-vit-base-patch32/resolve/main/onnx/text_model_quantized.onnx"),
        new ModelInfo("visual", "clip-vocab.json", 862_328, "5047b556ce86ccaf6aa22b3ffccfc52d391ea4accdab9c2f2407da5b742d4363",
            "https://huggingface.co/Xenova/clip-vit-base-patch32/resolve/main/vocab.json"),
        new ModelInfo("visual", "clip-merges.txt", 524_619, "9fd691f7c8039210e0fced15865466c65820d09b63988b0174bfe25de299051a",
            "https://huggingface.co/Xenova/clip-vit-base-patch32/resolve/main/merges.txt"),
        OnnxRuntime,
    };

    /// <summary>
    /// YAMNet (AudioSet sound classes; the PyTorch port by w-hc, MIT, as ONNX by Qualcomm AI Hub): laughter and
    /// hype. The .onnx file reads its weights from yamnet.data beside it, so both keep these names. Shares the
    /// ONNX Runtime library with visual search.
    /// </summary>
    public static IReadOnlyList<ModelInfo> Sound { get; } = new[]
    {
        new ModelInfo("sound", "yamnet.onnx", 166_464, "9261fbe1f196c8bb695933845af28df0a155845c9ec5e37ec96295e82c96f096", null),
        new ModelInfo("sound", "yamnet.data", 14_915_108, "0b4de0b14bf2b528d51e3cd84e2d3e2ae826b942fbec6001c654605f1f2ceb60", null),
        OnnxRuntime,
    };

    public static long BytesOf(IEnumerable<ModelInfo> pack) => pack.Sum(m => m.Bytes);
    public static bool IsInstalled(IEnumerable<ModelInfo> pack) => pack.All(IsInstalled);
    /// <summary>Removes a pack, keeping files another installed pack still uses (ONNX Runtime).</summary>
    public static void Remove(IEnumerable<ModelInfo> pack)
    {
        var list = pack.ToList();
        var shared = new[] { Visual, Sound }.Where(p => !ReferenceEquals(p, pack) && IsInstalled(p)).SelectMany(p => p).ToHashSet();
        foreach (var m in list.Where(m => !shared.Contains(m))) Remove(m);
    }

    /// <summary>Downloads what's missing of a set of files, one after the other (progress 0..1 over all of them).</summary>
    public static async Task DownloadAsync(IReadOnlyList<ModelInfo> pack, IProgress<double>? progress, CancellationToken ct)
    {
        var total = (double)BytesOf(pack);
        long before = 0;
        foreach (var m in pack)
        {
            if (!IsInstalled(m))
            {
                var start = before;
                await DownloadAsync(m, progress is null ? null : new SyncProgress(p => progress.Report((start + p * m.Bytes) / total)), ct);
            }
            before += m.Bytes;
            progress?.Report(before / total);
        }
    }

    /// <summary>Reports on the calling thread, so the outer Progress posts only once per step.</summary>
    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

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
