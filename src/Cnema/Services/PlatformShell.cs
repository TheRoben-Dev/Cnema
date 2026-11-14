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
            Console.Error.WriteLine($"[Cnema] Reveal failed: {ex.Message}");
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

    private static void Start(string file, params string[] args)
    {
        var psi = new ProcessStartInfo(file) { UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        Process.Start(psi);
    }
}
