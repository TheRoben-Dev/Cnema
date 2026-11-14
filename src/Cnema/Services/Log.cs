using System.Text;

namespace Cnema.Services;

/// <summary>
/// A small rolling log file (logs/cnema.log in the data folder). Cnema is a windowed app that
/// often runs with no terminal, so this is the only place a failure can leave a trace.
/// </summary>
public static class Log
{
    private const long MaxBytes = 512 * 1024;
    private static readonly object Gate = new();
    public static string Dir { get; } = Path.Combine(AppPaths.DataDir, "logs");
    public static string File { get; } = Path.Combine(Dir, "cnema.log");

    public static void Info(string message) => Write("info", message);
    public static void Warn(string message) => Write("warn", message);
    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}: {ex}");

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}{Environment.NewLine}";
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var info = new FileInfo(File);
                if (info.Exists && info.Length > MaxBytes)
                    System.IO.File.Move(File, Path.Combine(Dir, "cnema.old.log"), overwrite: true);
                System.IO.File.AppendAllText(File, line, Encoding.UTF8);
            }
            catch { /* logging must never take the app down */ }
        }
        try { Console.Error.Write("[Cnema] " + line); } catch { /* no console */ }
    }

    /// <summary>The newest lines, for the diagnostics report.</summary>
    public static string Tail(int lines)
    {
        lock (Gate)
        {
            try
            {
                return System.IO.File.Exists(File)
                    ? string.Join(Environment.NewLine, System.IO.File.ReadLines(File).TakeLast(lines))
                    : "(no log yet)";
            }
            catch (Exception ex)
            {
                return "(log unreadable: " + ex.Message + ")";
            }
        }
    }

    /// <summary>Records crashes that would otherwise vanish with the process.</summary>
    public static void HookUnhandled()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Error("Unhandled exception (crash)", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Error("Unobserved task exception", e.Exception);
            e.SetObserved();
        };
    }
}
