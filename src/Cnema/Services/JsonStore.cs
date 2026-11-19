using System.Text.Json;

namespace Cnema.Services;

/// <summary>Tiny atomic JSON load/save helper (write to temp, then replace).</summary>
public static class JsonStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    // For the big files only the app reads (the library): no indenting, a good deal smaller and quicker to write.
    private static readonly JsonSerializerOptions Compact = new();

    public static T Load<T>(string path) where T : new()
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (!File.Exists(path)) return new T();
                // Straight from the file's bytes: no copy of a many-megabyte file as one big string first.
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
                return JsonSerializer.Deserialize<T>(fs, Options) ?? new T();
            }
            catch (IOException ex) when (attempt < 6 && ex is not FileNotFoundException)
            {
                // Held for a moment by something else (a virus scanner, a backup tool, our own save): that's not a
                // damaged file, and treating it as one started the library over from nothing.
                Log.Warn($"Could not read {path} yet ({ex.Message}); trying again");
                Thread.Sleep(150 * attempt);
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not read {path}: {ex.Message}");
                // Keep the damaged file instead of overwriting it with defaults at the next save.
                var aside = $"{path}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
                try { File.Move(path, aside, overwrite: true); }
                catch
                {
                    try { File.Copy(path, aside, overwrite: true); } catch { /* best effort */ }
                }
                return new T();
            }
        }
    }

    /// <summary>Reads a file the user picked; throws with a readable message when it is not valid.</summary>
    public static T Read<T>(string path) where T : class =>
        JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) ?? throw new InvalidDataException("The file is empty.");

    public static void Save<T>(string path, T value, bool compact = false)
    {
        // One temporary file per thread: two saves of the same file at once must not trip over each other's.
        var tmp = $"{path}.{Environment.CurrentManagedThreadId}.tmp";
        // Flushed to the disk before the rename, so a power cut can't leave an empty file behind.
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
        {
            JsonSerializer.Serialize(fs, value, compact ? Compact : Options);
            fs.Flush(flushToDisk: true);
        }
        File.Move(tmp, path, overwrite: true);
    }
}
