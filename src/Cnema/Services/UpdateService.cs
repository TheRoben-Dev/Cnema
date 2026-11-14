using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace Cnema.Services;

/// <summary>A newer release: where to read about it and, when this platform has a build, where to get it.</summary>
public sealed record UpdateInfo(Version Version, string Url, string? AssetUrl = null, long AssetSize = 0, string? Sha256 = null);

/// <summary>
/// Finds newer releases on GitHub and installs them: download the build for this platform, check it,
/// swap it for the running app once Cnema has quit, and start the new one.
/// </summary>
public static class UpdateService
{
    // The source repository is private; builds are mirrored to this public, releases-only repository.
    public const string ReleasesPage = "https://github.com/TheRoben-Dev/Cnema-releases/releases/latest";
    // CNEMA_UPDATE_API points the check at another release feed; it exists for testing the updater.
    private static readonly string Api = Environment.GetEnvironmentVariable("CNEMA_UPDATE_API") is { Length: > 0 } feed
        ? feed : "https://api.github.com/repos/TheRoben-Dev/Cnema-releases/releases/latest";

    public static Version Current { get; } =
        Assembly.GetExecutingAssembly().GetName().Version is { } v ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : new Version(0, 0, 0);
    public static string CurrentText => Current.ToString(3);

    /// <summary>The end of this platform's file name in a release, e.g. "macos-apple-silicon.zip".</summary>
    public static string AssetSuffix =>
        OperatingSystem.IsMacOS() ? (RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "macos-apple-silicon.zip" : "macos-intel.zip")
        : OperatingSystem.IsWindows() ? "windows-x64.zip"
        : "linux-x64.tar.gz";

    private static HttpClient NewClient(TimeSpan timeout)
    {
        var http = new HttpClient { Timeout = timeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Cnema-update-check");
        return http;
    }

    /// <summary>The newer release, or null when up to date or when GitHub can't be reached.</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default) => (await CheckDetailedAsync(ct)).Update;

    /// <summary>Like <see cref="CheckAsync"/>, but also says why nothing came back.</summary>
    public static async Task<(UpdateInfo? Update, string? Error)> CheckDetailedAsync(CancellationToken ct = default)
    {
        try
        {
            using var http = NewClient(TimeSpan.FromSeconds(10));
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            using var doc = JsonDocument.Parse(await http.GetStringAsync(Api, ct));
            var root = doc.RootElement;
            var latest = Parse(root.GetProperty("tag_name").GetString());
            var url = root.TryGetProperty("html_url", out var u) ? u.GetString() : null;
            Log.Info($"Update check: running {CurrentText}, latest release {latest?.ToString(3) ?? "unreadable"}");
            if (latest is null || latest <= Current) return (null, null);

            string? assetUrl = null, sha = null;
            long size = 0;
            if (root.TryGetProperty("assets", out var assets))
                foreach (var a in assets.EnumerateArray())
                {
                    var name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    if (name is null || !name.EndsWith(AssetSuffix, StringComparison.OrdinalIgnoreCase)) continue;
                    assetUrl = a.TryGetProperty("browser_download_url", out var d) ? d.GetString() : null;
                    size = a.TryGetProperty("size", out var s) ? s.GetInt64() : 0;
                    // GitHub publishes "sha256:<hex>" for release assets.
                    if (a.TryGetProperty("digest", out var dg) && dg.GetString() is { } digest &&
                        digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                        sha = digest[7..];
                    break;
                }
            return (new UpdateInfo(latest, url ?? ReleasesPage, assetUrl, size, sha), null);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // GitHub answers 404 for a repository that is private or has no published release.
            Log.Warn("Update check failed: the release list is not public (404)");
            return (null, T("the release list is not publicly readable"));
        }
        catch (Exception ex)
        {
            Log.Warn("Update check failed: " + ex.Message);
            return (null, ex.Message);
        }
    }

    public static Version? Parse(string? tag)
    {
        var text = (tag ?? "").Trim().TrimStart('v', 'V');
        var dash = text.IndexOf('-');
        if (dash > 0) text = text[..dash];
        return Version.TryParse(text, out var v) ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : null;
    }

    // ---------- installing ----------

    /// <summary>
    /// What gets replaced: Cnema.app on macOS, the folder holding the executable elsewhere.
    /// Null when this is not an installed, self-contained build (for example a run from source).
    /// </summary>
    public static string? InstallRoot
    {
        get
        {
            var dir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var runtime = OperatingSystem.IsWindows() ? "coreclr.dll" : OperatingSystem.IsMacOS() ? "libcoreclr.dylib" : "libcoreclr.so";
            if (!File.Exists(Path.Combine(dir, runtime))) return null;
            if (!OperatingSystem.IsMacOS()) return dir;
            // .../Cnema.app/Contents/MacOS
            var bundle = Path.GetDirectoryName(Path.GetDirectoryName(dir));
            return bundle is not null && bundle.EndsWith(".app", StringComparison.OrdinalIgnoreCase) ? bundle : null;
        }
    }

    /// <summary>Null when Cnema can replace itself here; otherwise the reason it can't.</summary>
    public static string? WhyNoSelfUpdate()
    {
        if (InstallRoot is not { } root) return T("this copy was not installed from a release build");
        // A quarantined app on macOS runs from a read-only, randomised copy of itself.
        if (root.Contains("/AppTranslocation/", StringComparison.Ordinal))
            return T("macOS is running Cnema from a temporary location; move Cnema.app to Applications and open it from there");
        try
        {
            var parent = Path.GetDirectoryName(root)!;
            var probe = Path.Combine(parent, $".cnema-write-test-{Environment.ProcessId}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return null;
        }
        catch
        {
            return T("Cnema is not allowed to write to the folder it is installed in");
        }
    }

    private static string StageDir => Path.Combine(AppPaths.DataDir, "update");

    /// <summary>
    /// Downloads and unpacks the release next to Cnema's data. Returns the unpacked app
    /// (Cnema.app, or the Cnema folder), ready for <see cref="ApplyAndRestart"/>.
    /// </summary>
    public static async Task<string> DownloadAsync(UpdateInfo info, IProgress<double>? progress, CancellationToken ct)
    {
        if (info.AssetUrl is null) throw new InvalidOperationException(T("This release has no build for this system."));
        try { if (Directory.Exists(StageDir)) Directory.Delete(StageDir, true); } catch { /* reused below */ }
        Directory.CreateDirectory(StageDir);
        var archive = Path.Combine(StageDir, "download" + (AssetSuffix.EndsWith(".zip") ? ".zip" : ".tar.gz"));

        using (var http = NewClient(TimeSpan.FromMinutes(30)))
        using (var response = await http.GetAsync(info.AssetUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? info.AssetSize;
            await using var source = await response.Content.ReadAsStreamAsync(ct);
            await using var target = File.Create(archive);
            var buffer = new byte[1 << 17];
            long done = 0;
            int n;
            while ((n = await source.ReadAsync(buffer, ct)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, n), ct);
                done += n;
                if (total > 0) progress?.Report(Math.Min(1, (double)done / total));
            }
        }

        var length = new FileInfo(archive).Length;
        if (info.AssetSize > 0 && length != info.AssetSize)
            throw new InvalidOperationException(F("The download is incomplete ({0} of {1} bytes).", length, info.AssetSize));
        if (info.Sha256 is { } expected)
        {
            await using var fs = File.OpenRead(archive);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct));
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(T("The download does not match the checksum GitHub published for it."));
        }

        var unpacked = Path.Combine(StageDir, "new");
        Directory.CreateDirectory(unpacked);
        if (OperatingSystem.IsWindows())
            await Task.Run(() => System.IO.Compression.ZipFile.ExtractToDirectory(archive, unpacked, overwriteFiles: true), ct);
        else
        {
            // ditto keeps the bundle's signature, symlinks and permissions intact; tar does the same on Linux.
            var (tool, args) = OperatingSystem.IsMacOS()
                ? ("/usr/bin/ditto", new[] { "-x", "-k", archive, unpacked })
                : ("tar", new[] { "-xzf", archive, "-C", unpacked });
            var r = await ProcessRunner.RunAsync(tool, args, ct: ct);
            if (r.ExitCode != 0) throw new InvalidOperationException(T("Could not unpack the update: ") + r.StdErr.Trim());
        }
        try { File.Delete(archive); } catch { /* removed with the stage folder later */ }

        var app = Path.Combine(unpacked, OperatingSystem.IsMacOS() ? "Cnema.app" : "Cnema");
        var exe = OperatingSystem.IsMacOS() ? Path.Combine(app, "Contents", "MacOS", "Cnema")
            : Path.Combine(app, OperatingSystem.IsWindows() ? "Cnema.exe" : "Cnema");
        if (!File.Exists(exe)) throw new InvalidOperationException(T("The update did not contain the app."));
        return app;
    }

    /// <summary>
    /// Starts a small script that waits for this process to exit, swaps the app and starts it again.
    /// The caller quits right after. The old app is kept until the new one is in place.
    /// </summary>
    public static void ApplyAndRestart(string newApp, bool background)
    {
        var root = InstallRoot ?? throw new InvalidOperationException("Not an installed build.");
        var pid = Environment.ProcessId;
        Log.Info($"Installing update from {newApp} over {root}");
        ProcessStartInfo psi;
        if (OperatingSystem.IsWindows())
        {
            var script = Path.Combine(StageDir, "apply.cmd");
            File.WriteAllText(script, string.Join("\r\n", new[]
            {
                "@echo off",
                ":wait",
                $"tasklist /FI \"PID eq {pid}\" 2>nul | find \" {pid} \" >nul && (timeout /t 1 /nobreak >nul & goto wait)",
                $"robocopy \"{newApp}\" \"{root}\" /E /R:5 /W:1 /NFL /NDL /NJH /NJS /NP >nul",
                $"start \"\" \"{Path.Combine(root, "Cnema.exe")}\"{(background ? " --background" : "")}",
                $"rmdir /S /Q \"{Path.GetDirectoryName(newApp)}\"",
            }));
            psi = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(script);
        }
        else
        {
            static string Q(string s) => "'" + s.Replace("'", "'\\''") + "'";
            var launch = OperatingSystem.IsMacOS()
                ? $"/usr/bin/open {Q(root)}{(background ? " --args --background" : "")}"
                : $"nohup {Q(Path.Combine(root, "Cnema"))}{(background ? " --background" : "")} >/dev/null 2>&1 &";
            var script = Path.Combine(StageDir, "apply.sh");
            File.WriteAllText(script, string.Join("\n", new[]
            {
                "#!/bin/sh",
                // Give up rather than swap the app under a Cnema that never quit.
                $"n=0; while kill -0 {pid} 2>/dev/null; do n=$((n+1)); [ $n -gt 200 ] && exit 1; sleep 0.3; done",
                $"NEW={Q(newApp)}; DEST={Q(root)}; OLD=\"$DEST.old-$$\"",
                // Downloads made by Cnema itself are not quarantined, but an unpacked bundle must never be.
                OperatingSystem.IsMacOS() ? "/usr/bin/xattr -dr com.apple.quarantine \"$NEW\" 2>/dev/null" : ":",
                "if mv \"$DEST\" \"$OLD\"; then",
                "  if mv \"$NEW\" \"$DEST\" 2>/dev/null || cp -Rp \"$NEW\" \"$DEST\"; then rm -rf \"$OLD\"",
                "  else rm -rf \"$DEST\"; mv \"$OLD\" \"$DEST\"; fi",
                "fi",
                launch,
                $"rm -rf {Q(Path.GetDirectoryName(newApp)!)}",
                "",
            }));
            psi = new ProcessStartInfo("/bin/sh") { UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add(script);
        }
        Process.Start(psi);
    }
}
