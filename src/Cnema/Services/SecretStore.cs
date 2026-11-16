using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace Cnema.Services;

/// <summary>
/// Keeps small secrets (the backup server's secret key) out of settings.json: the macOS Keychain,
/// Windows DPAPI (encrypted for this Windows user), or the Secret Service (secret-tool) on Linux.
/// Where none of those is available, a file only the current user can read.
/// </summary>
public static class SecretStore
{
    private const string Service = "Savefile";

    public static string? Get(string name)
    {
        try
        {
            if (OperatingSystem.IsMacOS())
                return Run("/usr/bin/security", null, "find-generic-password", "-s", Service, "-a", name, "-w") is { Length: > 0 } s ? s : null;
            if (OperatingSystem.IsWindows()) return GetWindows(name);
            if (SecretTool is { } tool && Run(tool, null, "lookup", "service", Service, "account", name) is { Length: > 0 } found) return found;
            var file = FileFor(name);
            return File.Exists(file) ? Encoding.UTF8.GetString(Convert.FromBase64String(File.ReadAllText(file).Trim())) : null;
        }
        catch (Exception ex)
        {
            Log.Warn($"Reading secret {name} failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>Stores the secret, or removes it when empty. Returns false when it couldn't be stored.</summary>
    public static bool Set(string name, string? value)
    {
        try
        {
            if (string.IsNullOrEmpty(value)) { Remove(name); return true; }
            if (OperatingSystem.IsMacOS())
            {
                // -U updates an existing item; the Keychain keeps it encrypted for this user.
                return Run("/usr/bin/security", null, "add-generic-password", "-U", "-s", Service, "-a", name, "-w", value) is not null;
            }
            if (OperatingSystem.IsWindows()) { SetWindows(name, value); return true; }
            if (SecretTool is { } tool && Run(tool, value, "store", "--label", "Savefile " + name, "service", Service, "account", name) is not null)
            {
                try { File.Delete(FileFor(name)); } catch { /* none */ }
                return true;
            }
            WritePrivate(FileFor(name), Convert.ToBase64String(Encoding.UTF8.GetBytes(value)));
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Storing secret {name} failed: {ex.Message}");
            return false;
        }
    }

    public static void Remove(string name)
    {
        try
        {
            if (OperatingSystem.IsMacOS()) Run("/usr/bin/security", null, "delete-generic-password", "-s", Service, "-a", name);
            else if (!OperatingSystem.IsWindows() && SecretTool is { } tool) Run(tool, null, "clear", "service", Service, "account", name);
            if (File.Exists(FileFor(name))) File.Delete(FileFor(name));
        }
        catch { /* nothing stored */ }
    }

    private static string FileFor(string name) => Path.Combine(AppPaths.DataDir, $".secret-{name}");

    [SupportedOSPlatform("windows")]
    private static string? GetWindows(string name)
    {
        var file = FileFor(name);
        if (!File.Exists(file)) return null;
        var bytes = ProtectedData.Unprotect(File.ReadAllBytes(file), Entropy, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(bytes);
    }

    [SupportedOSPlatform("windows")]
    private static void SetWindows(string name, string value)
    {
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(FileFor(name), bytes);
    }

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Savefile secret v1");

    private static void WritePrivate(string file, string text)
    {
        File.WriteAllText(file, "");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.WriteAllText(file, text);
    }

    private static string? _secretTool;
    private static bool _lookedForSecretTool;
    /// <summary>libsecret's command-line tool, when there is a keyring to talk to.</summary>
    private static string? SecretTool
    {
        get
        {
            if (_lookedForSecretTool) return _secretTool;
            _lookedForSecretTool = true;
            if (Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS") is not { Length: > 0 }) return null;
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                var candidate = Path.Combine(dir, "secret-tool");
                if (File.Exists(candidate)) return _secretTool = candidate;
            }
            return null;
        }
    }

    /// <summary>Runs a helper; stdout (trimmed) on success, null on failure or after 10 seconds.</summary>
    private static string? Run(string file, string? stdin, params string[] args)
    {
        var psi = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin is not null,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi);
        if (p is null) return null;
        if (stdin is not null)
        {
            p.StandardInput.Write(stdin);
            p.StandardInput.Close();
        }
        var output = p.StandardOutput.ReadToEndAsync();
        if (!p.WaitForExit(10_000))
        {
            try { p.Kill(true); } catch { /* gone */ }
            return null;
        }
        return p.ExitCode == 0 ? output.Result.TrimEnd('\r', '\n') : null;
    }
}
