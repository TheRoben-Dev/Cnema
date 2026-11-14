using System.Security;

namespace Cnema.Services;

/// <summary>
/// "Launch at login", using each OS's own per-user mechanism (no admin rights, nothing installed):
/// a LaunchAgent on macOS, the HKCU Run key on Windows, an XDG autostart entry on Linux.
/// The app is started with --background so it comes up in the tray without a window.
/// </summary>
public static class AutostartService
{
    public const string BackgroundFlag = "--background";
    private const string Label = "dev.theroben.cnema";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static string MacPlist => Path.Combine(Home, "Library", "LaunchAgents", Label + ".plist");
    private static string LinuxDesktop
    {
        get
        {
            var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } x
                ? x : Path.Combine(Home, ".config");
            return Path.Combine(config, "autostart", "cnema.desktop");
        }
    }

    public static bool IsEnabled
    {
        get
        {
            try
            {
                if (OperatingSystem.IsMacOS()) return File.Exists(MacPlist);
                if (OperatingSystem.IsWindows())
                {
                    using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
                    return key?.GetValue("Cnema") is not null;
                }
                return File.Exists(LinuxDesktop);
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>Returns null on success, otherwise a message for the user.</summary>
    public static string? Set(bool enabled)
    {
        var exe = Environment.ProcessPath;
        if (enabled && string.IsNullOrEmpty(exe)) return T("Could not work out where Cnema is installed.");
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                if (!enabled) { File.Delete(MacPlist); return null; }
                Directory.CreateDirectory(Path.GetDirectoryName(MacPlist)!);
                File.WriteAllText(MacPlist, $"""
                    <?xml version="1.0" encoding="UTF-8"?>
                    <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
                    <plist version="1.0">
                    <dict>
                      <key>Label</key><string>{Label}</string>
                      <key>ProgramArguments</key>
                      <array>
                        <string>{SecurityElement.Escape(exe)}</string>
                        <string>{BackgroundFlag}</string>
                      </array>
                      <key>RunAtLoad</key><true/>
                      <key>ProcessType</key><string>Interactive</string>
                    </dict>
                    </plist>

                    """);
                return null;
            }
            if (OperatingSystem.IsWindows())
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey);
                if (enabled) key.SetValue("Cnema", $"\"{exe}\" {BackgroundFlag}");
                else key.DeleteValue("Cnema", throwOnMissingValue: false);
                return null;
            }
            if (!enabled) { File.Delete(LinuxDesktop); return null; }
            Directory.CreateDirectory(Path.GetDirectoryName(LinuxDesktop)!);
            File.WriteAllText(LinuxDesktop,
                "[Desktop Entry]\nType=Application\nName=Cnema\n" +
                $"Exec=\"{exe}\" {BackgroundFlag}\nX-GNOME-Autostart-enabled=true\nTerminal=false\n");
            return null;
        }
        catch (Exception ex)
        {
            return T("Could not change the login item: ") + ex.Message;
        }
    }

    /// <summary>The app may have been moved since the login item was written: point it at this copy.</summary>
    public static void RefreshIfEnabled()
    {
        if (IsEnabled) Set(true);
    }
}
