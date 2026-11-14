using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Cnema.Services;

/// <summary>OS integration: reveal files, desktop notifications, the macOS screen recording permission.</summary>
public static class PlatformShell
{
    public static void Reveal(string path)
    {
        try
        {
            if (OperatingSystem.IsMacOS())
                Start("open", "-R", path);
            else if (OperatingSystem.IsWindows())
                Process.Start("explorer.exe", $"/select,\"{path}\"");
            else
                Start("xdg-open", Path.GetDirectoryName(path) ?? path);
        }
        catch (Exception ex)
        {
            Log.Warn($"Reveal failed: {ex.Message}");
        }
    }

    /// <summary>Best-effort desktop notification (shown over a game where the OS allows it).</summary>
    public static void Notify(string title, string body)
    {
        try
        {
            if (OperatingSystem.IsMacOS())
                Start("osascript", "-e", $"display notification {Quote(body)} with title {Quote(title)}");
            else if (OperatingSystem.IsLinux())
                Start("notify-send", "-a", "Cnema", "-t", "3000", title, body);
            // Windows: the tray icon tooltip and the in-app banner report the save.
        }
        catch { /* notifications are a nicety */ }

        static string Quote(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    [DllImport(CoreGraphics)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool CGPreflightScreenCaptureAccess();
    [DllImport(CoreGraphics)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool CGRequestScreenCaptureAccess();

    /// <summary>
    /// macOS only records other apps' windows once the user allows it under Privacy &amp; Security.
    /// Returns true if access is already granted; otherwise triggers the system prompt (first time
    /// only) and returns false. Other platforms need no permission.
    /// </summary>
    public static bool EnsureScreenCaptureAccess()
    {
        if (!OperatingSystem.IsMacOS()) return true;
        try
        {
            return CGPreflightScreenCaptureAccess() || CGRequestScreenCaptureAccess();
        }
        catch
        {
            return true; // very old macOS without the API: let ffmpeg try
        }
    }

    public static void OpenUrl(string url)
    {
        try
        {
            if (OperatingSystem.IsMacOS()) Start("open", url);
            else if (OperatingSystem.IsWindows()) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            else Start("xdg-open", url);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not open {url}: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------- trash
    /// <summary>
    /// Moves a file to the Trash / Recycle Bin so a delete can be undone. Throws if the OS has no
    /// working trash here; the caller then tells the user rather than deleting for good.
    /// </summary>
    public static void MoveToTrash(string path)
    {
        if (!File.Exists(path)) return;
        if (OperatingSystem.IsMacOS())
        {
            // Finder's own trash, so "Put Back" works.
            var script = $"tell application \"Finder\" to delete (POSIX file {AppleQuote(path)} as alias)";
            if (Run("osascript", "-e", script) != 0) MoveToHomeTrash(path, Path.Combine(Home, ".Trash"), info: false);
        }
        else if (OperatingSystem.IsWindows())
        {
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        }
        else
        {
            if (Run("gio", "trash", path) == 0 && !File.Exists(path)) return;
            var data = Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } x
                ? x : Path.Combine(Home, ".local", "share");
            MoveToHomeTrash(path, Path.Combine(data, "Trash"), info: true);
        }
        if (File.Exists(path)) throw new IOException(T("The file could not be moved to the trash."));
    }

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>Plain move into the trash folder; on Linux with the .trashinfo the XDG spec asks for.</summary>
    private static void MoveToHomeTrash(string path, string trash, bool info)
    {
        var files = info ? Path.Combine(trash, "files") : trash;
        Directory.CreateDirectory(files);
        var name = Path.GetFileName(path);
        var target = Path.Combine(files, name);
        for (var i = 2; File.Exists(target); i++)
            target = Path.Combine(files, $"{Path.GetFileNameWithoutExtension(name)} {i}{Path.GetExtension(name)}");
        if (info)
        {
            var infoDir = Path.Combine(trash, "info");
            Directory.CreateDirectory(infoDir);
            File.WriteAllText(Path.Combine(infoDir, Path.GetFileName(target) + ".trashinfo"),
                $"[Trash Info]\nPath={Uri.EscapeDataString(path).Replace("%2F", "/")}\nDeletionDate={DateTime.Now:yyyy-MM-ddTHH:mm:ss}\n");
        }
        File.Move(path, target);
    }

    // ---------------------------------------------------------------- sound
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern bool PlaySound(string? sound, IntPtr module, uint flags);

    /// <summary>A short system sound, audible over a game, to confirm a clip was saved.</summary>
    public static void PlayConfirmSound()
    {
        try
        {
            if (OperatingSystem.IsMacOS()) Start("afplay", "/System/Library/Sounds/Glass.aiff");
            else if (OperatingSystem.IsWindows()) PlaySound("SystemAsterisk", IntPtr.Zero, 0x00010000 | 0x0001 | 0x0002); // alias, async, no default
            else
            {
                const string sound = "/usr/share/sounds/freedesktop/stereo/complete.oga";
                if (File.Exists(sound)) Start("paplay", sound);
            }
        }
        catch { /* a nicety */ }
    }

    // ---------------------------------------------------------------- frontmost app
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    [DllImport(ObjC)] private static extern IntPtr objc_getClass(string name);
    [DllImport(ObjC)] private static extern IntPtr sel_registerName(string name);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr receiver, IntPtr selector);

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    private static readonly string[] NotGames =
    {
        "cnema", "finder", "explorer", "dock", "loginwindow", "systemuiserver", "windowserver", "desktop",
        "searchhost", "shellexperiencehost", "startmenuexperiencehost", "gnome-shell", "plasmashell", "xfdesktop",
    };

    /// <summary>
    /// Name of the app in front right now ("Warframe"), used to label clips with the game they came
    /// from. Null for the desktop, the shell or Cnema itself. Needs no permission on any OS.
    /// </summary>
    public static string? FrontmostAppName()
    {
        try
        {
            string? name = null;
            if (OperatingSystem.IsMacOS())
            {
                var workspace = Send(objc_getClass("NSWorkspace"), sel_registerName("sharedWorkspace"));
                var app = Send(workspace, sel_registerName("frontmostApplication"));
                if (app != IntPtr.Zero)
                {
                    var ns = Send(app, sel_registerName("localizedName"));
                    if (ns != IntPtr.Zero) name = Marshal.PtrToStringUTF8(Send(ns, sel_registerName("UTF8String")));
                }
            }
            else if (OperatingSystem.IsWindows())
            {
                GetWindowThreadProcessId(GetForegroundWindow(), out var pid);
                if (pid != 0)
                {
                    using var p = Process.GetProcessById((int)pid);
                    try { name = p.MainModule?.FileVersionInfo.ProductName; } catch { /* protected process */ }
                    if (string.IsNullOrWhiteSpace(name)) name = p.ProcessName;
                }
            }
            else
            {
                var active = Capture("xprop", "-root", "_NET_ACTIVE_WINDOW");
                var id = active?.Split(' ', '\n').LastOrDefault(t => t.Length > 0)?.Trim().TrimEnd(',');
                if (id is not null && !id.StartsWith("0x")) id = long.TryParse(id, out var n) ? "0x" + n.ToString("x") : null;
                if (id is not null && id != "0x0")
                {
                    // WM_CLASS(STRING) = "instance", "Class"
                    var cls = Capture("xprop", "-id", id, "WM_CLASS");
                    name = cls?.Split('"').Where((_, i) => i % 2 == 1).LastOrDefault();
                }
            }
            name = name?.Trim();
            if (string.IsNullOrEmpty(name) || NotGames.Contains(name.ToLowerInvariant())) return null;
            return name.Length > 40 ? name[..40].Trim() : name;
        }
        catch
        {
            return null;
        }
    }

    private static string AppleQuote(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static int Run(string file, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p is null) return -1;
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            return p.WaitForExit(10000) ? p.ExitCode : -1;
        }
        catch
        {
            return -1;
        }
    }

    private static string? Capture(string file, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p is null) return null;
            var output = p.StandardOutput.ReadToEnd();
            return p.WaitForExit(3000) && p.ExitCode == 0 ? output : null;
        }
        catch
        {
            return null;
        }
    }

    private static void Start(string file, params string[] args)
    {
        var psi = new ProcessStartInfo(file) { UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        Process.Start(psi);
    }
}
