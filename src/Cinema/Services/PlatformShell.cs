using System.Diagnostics;

namespace Cinema.Services;

/// <summary>OS integration: reveal a file in Finder / Explorer / the Linux file manager.</summary>
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
            Console.Error.WriteLine($"[Cinema] Reveal failed: {ex.Message}");
        }
    }

    private static void Start(string file, params string[] args)
    {
        var psi = new ProcessStartInfo(file) { UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        Process.Start(psi);
    }
}
