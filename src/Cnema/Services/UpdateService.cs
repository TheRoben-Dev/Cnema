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

    /// <summary>
    /// The end of this platform's file name in a release, e.g. "macos-apple-silicon.zip". A copy put there by the
    /// Windows installer updates with the next installer, so its shortcuts, uninstaller and Apps entry stay right.
    /// </summary>
    public static string AssetSuffix =>
        OperatingSystem.IsMacOS() ? (RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "macos-apple-silicon.zip" : "macos-intel.zip")
        : OperatingSystem.IsWindows() ? (IsInstalled ? "windows-x64-setup.exe" : "windows-x64.zip")
        : "linux-x64.tar.gz";

    /// <summary>The installer's AppId: the key of Savefile's entry under Apps (the "_is1" suffix is Inno Setup's).</summary>
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{7E6B1C3A-5D2F-4E8B-9A41-2C5D7F1E0B93}_is1";

    /// <summary>Installed by the Windows installer (its uninstaller sits next to the app), not unzipped.</summary>
    public static bool IsInstalled =>
        OperatingSystem.IsWindows() && InstallRoot is { } root && File.Exists(Path.Combine(root, "unins000.exe"));

    /// <summary>Installed for everyone on this PC (in Program Files), so updating asks for an administrator once.</summary>
    public static bool IsAllUsersInstall
    {
        get
        {
            if (!OperatingSystem.IsWindows() || !IsInstalled || InstallRoot is not { } root) return false;
            try
            {
                using var key = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64)
                    .OpenSubKey(UninstallKey);
                return key?.GetValue("InstallLocation") is string where &&
                       string.Equals(where.TrimEnd('\\', '/'), root.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }
    }

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
        catch (HttpRequestException ex) when (ex.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.TooManyRequests)
        {
            // GitHub's limit for checks without an account (60 an hour per network) was reached.
            Log.Warn("Update check failed: " + ex.Message);
            return (null, T("GitHub is busy right now, so try again in a while"));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Net.Sockets.SocketException)
        {
            Log.Warn("Update check failed: " + ex.Message);
            return (null, T("no connection to GitHub. Check your internet connection"));
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
        // The installer does the work (and asks for an administrator itself when Savefile is installed for everyone).
        if (IsInstalled) return null;
        // A quarantined app on macOS runs from a read-only, randomised copy of itself.
        if (root.Contains("/AppTranslocation/", StringComparison.Ordinal))
            return T("macOS is running Savefile from a temporary location; move Cnema.app to Applications and open it from there");
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
            return T("Savefile is not allowed to write to the folder it is installed in");
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
        var installer = info.AssetUrl.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
        var archive = Path.Combine(StageDir, installer ? "Savefile-setup.exe" : "download" + (AssetSuffix.EndsWith(".zip") ? ".zip" : ".tar.gz"));

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

        if (installer) return archive;

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

        // The app is still packaged as Cnema(.app) for now; a later release may ship it as Savefile.
        foreach (var name in new[] { "Cnema", "Savefile" })
        {
            var app = Path.Combine(unpacked, OperatingSystem.IsMacOS() ? name + ".app" : name);
            var exe = OperatingSystem.IsMacOS() ? Path.Combine(app, "Contents", "MacOS", name)
                : Path.Combine(app, OperatingSystem.IsWindows() ? name + ".exe" : name);
            if (File.Exists(exe)) return app;
        }
        throw new InvalidOperationException(T("The update did not contain the app."));
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
        if (OperatingSystem.IsWindows() && newApp.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            RunInstaller(newApp, root, pid, background);
            return;
        }
        // The new build may be packaged as Savefile instead of Cnema; start whichever it ships.
        var exeName = new[] { "Savefile", "Cnema" }.FirstOrDefault(n =>
            File.Exists(Path.Combine(newApp, OperatingSystem.IsWindows() ? n + ".exe" : n))) ?? "Cnema";
        ProcessStartInfo psi;
        if (OperatingSystem.IsWindows())
        {
            // The paths go in as environment variables: cmd reads a .bat in the OEM code page, so a
            // user name like "José" written into the script itself would be garbled. The script text
            // stays plain ASCII. The new app is copied next to the old one first and the folders are
            // swapped only when the copy worked, so a locked file can't leave a half-updated app.
            var script = Path.Combine(StageDir, "apply.cmd");
            File.WriteAllText(script, string.Join("\r\n", new[]
            {
                "@echo off",
                "set N=0",
                ":wait",
                $"tasklist /FI \"PID eq {pid}\" 2>nul | find \" {pid} \" >nul || goto ready",
                "set /a N+=1",
                "if %N% GEQ 180 goto launch",
                "timeout /t 1 /nobreak >nul",
                "goto wait",
                ":ready",
                "if exist \"%SF_DEST%.new\" rmdir /S /Q \"%SF_DEST%.new\"",
                "robocopy \"%SF_NEW%\" \"%SF_DEST%.new\" /E /R:5 /W:1 /NFL /NDL /NJH /NJS /NP >nul",
                "if errorlevel 8 goto cleanup",
                "if exist \"%SF_DEST%.old\" rmdir /S /Q \"%SF_DEST%.old\"",
                "move \"%SF_DEST%\" \"%SF_DEST%.old\" >nul || goto cleanup",
                "move \"%SF_DEST%.new\" \"%SF_DEST%\" >nul || (move \"%SF_DEST%.old\" \"%SF_DEST%\" >nul & goto cleanup)",
                "rmdir /S /Q \"%SF_DEST%.old\"",
                "goto launch",
                ":cleanup",
                "if exist \"%SF_DEST%.new\" rmdir /S /Q \"%SF_DEST%.new\"",
                ":launch",
                // Whichever app is in place now: the new one, or the old one if the swap didn't happen.
                "if exist \"%SF_DEST%\\%SF_EXE%\" (start \"\" \"%SF_DEST%\\%SF_EXE%\" %SF_ARGS%) else (start \"\" \"%SF_DEST%\\Cnema.exe\" %SF_ARGS%)",
                "rmdir /S /Q \"%SF_STAGE%\"",
                "",
            }));
            psi = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(script);
            psi.Environment["SF_NEW"] = newApp;
            psi.Environment["SF_DEST"] = root;
            psi.Environment["SF_EXE"] = exeName + ".exe";
            psi.Environment["SF_ARGS"] = background ? "--background" : "";
            psi.Environment["SF_STAGE"] = Path.GetDirectoryName(newApp)!;
            // Not inside the app folder, or Windows won't let the script move it.
            psi.WorkingDirectory = Path.GetTempPath();
        }
        else
        {
            static string Q(string s) => "'" + s.Replace("'", "'\\''") + "'";
            var launch = OperatingSystem.IsMacOS()
                ? $"/usr/bin/open {Q(root)}{(background ? " --args --background" : "")}"
                : $"nohup {Q(Path.Combine(root, exeName))}{(background ? " --background" : "")} >/dev/null 2>&1 &";
            var script = Path.Combine(StageDir, "apply.sh");
            File.WriteAllText(script, string.Join("\n", new[]
            {
                "#!/bin/sh",
                // Give up on the swap (not on the relaunch) if Cnema takes more than about two minutes to quit.
                $"n=0; swap=1; while kill -0 {pid} 2>/dev/null; do n=$((n+1)); [ $n -gt 400 ] && {{ swap=0; break; }}; sleep 0.3; done",
                $"NEW={Q(newApp)}; DEST={Q(root)}; OLD=\"$DEST.old-$$\"",
                // Downloads made by Cnema itself are not quarantined, but an unpacked bundle must never be.
                OperatingSystem.IsMacOS() ? "/usr/bin/xattr -dr com.apple.quarantine \"$NEW\" 2>/dev/null" : ":",
                "if [ $swap = 1 ] && mv \"$DEST\" \"$OLD\"; then",
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
        Process.Start(psi)?.Dispose(); // it runs on by itself; only our handle goes
    }

    /// <summary>Where the installer writes what it did during an update (kept for diagnostics).</summary>
    public static string InstallerLog => Path.Combine(AppPaths.DataDir, "update-setup.log");

    /// <summary>
    /// Waits for this process to exit, runs the new installer silently in the same mode (just me, or everyone,
    /// which shows Windows' administrator prompt once) and starts Savefile again from where it is installed.
    /// </summary>
    private static void RunInstaller(string setup, string root, int pid, bool background)
    {
        var script = Path.Combine(StageDir, "apply.cmd");
        File.WriteAllText(script, string.Join("\r\n", new[]
        {
            "@echo off",
            "set N=0",
            ":wait",
            $"tasklist /FI \"PID eq {pid}\" 2>nul | find \" {pid} \" >nul || goto ready",
            "set /a N+=1",
            "if %N% GEQ 180 goto launch",
            "timeout /t 1 /nobreak >nul",
            "goto wait",
            ":ready",
            // /wait also covers the administrator case: the first installer waits for its elevated copy.
            "start \"\" /wait \"%SF_SETUP%\" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /NOCANCEL %SF_MODE% \"/LOG=%SF_LOG%\"",
            ":launch",
            "start \"\" \"%SF_DEST%\\Cnema.exe\" %SF_ARGS%",
            "rmdir /S /Q \"%SF_STAGE%\"",
            "",
        }));
        var psi = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add(script);
        psi.Environment["SF_SETUP"] = setup;
        psi.Environment["SF_MODE"] = IsAllUsersInstall ? "/ALLUSERS" : "/CURRENTUSER";
        psi.Environment["SF_LOG"] = InstallerLog;
        psi.Environment["SF_DEST"] = root;
        psi.Environment["SF_ARGS"] = background ? "--background" : "";
        psi.Environment["SF_STAGE"] = Path.GetDirectoryName(setup)!;
        psi.WorkingDirectory = Path.GetTempPath();
        Process.Start(psi)?.Dispose();
    }

    /// <summary>
    /// CI check of the installer update path: copies <paramref name="setup"/> to where a downloaded update goes and
    /// hands over to it exactly like "Install and restart", then exits.
    /// </summary>
    public static int SelfTestInstallerUpdate(string setup)
    {
        Console.WriteLine($"installed: {IsInstalled}, for everyone: {IsAllUsersInstall}, update file: *{AssetSuffix}, self-update: {WhyNoSelfUpdate() ?? "yes"}");
        if (!IsInstalled || WhyNoSelfUpdate() is not null) return 1;
        try { if (Directory.Exists(StageDir)) Directory.Delete(StageDir, true); } catch { /* reused */ }
        Directory.CreateDirectory(StageDir);
        var staged = Path.Combine(StageDir, "Savefile-setup.exe");
        File.Copy(setup, staged, true);
        try { File.Delete(InstallerLog); } catch { /* fresh log below */ }
        ApplyAndRestart(staged, background: true);
        return 0;
    }
}
