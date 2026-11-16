using System.Text.Json;

namespace Cnema.Services;

/// <summary>Tiny atomic JSON load/save helper (write to temp, then replace).</summary>
public static class JsonStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static T Load<T>(string path) where T : new()
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) ?? new T();
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not read {path}: {ex.Message}");
            // Keep the damaged file instead of overwriting it with defaults at the next save.
            try { File.Move(path, $"{path}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}", overwrite: true); }
            catch { /* best effort */ }
        }
        return new T();
    }

    /// <summary>Reads a file the user picked; throws with a readable message when it is not valid.</summary>
    public static T Read<T>(string path) where T : class =>
        JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) ?? throw new InvalidDataException("The file is empty.");

    public static void Save<T>(string path, T value)
    {
        var tmp = path + ".tmp";
        // Flushed to the disk before the rename, so a power cut can't leave an empty file behind.
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(fs, value, Options);
            fs.Flush(flushToDisk: true);
        }
        File.Move(tmp, path, overwrite: true);
    }
}
