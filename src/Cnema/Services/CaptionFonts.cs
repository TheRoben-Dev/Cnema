using Avalonia.Platform;

namespace Cnema.Services;

/// <summary>
/// The caption font for ffmpeg. Cnema's own Inter font is copied out of the app once, so burned-in
/// captions look the same on every computer whatever fonts it has installed.
/// </summary>
public static class CaptionFonts
{
    private static readonly string[] Files = { "Inter-Bold.ttf", "Inter-SemiBold.ttf" };
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>A folder holding the caption fonts, for libass's fontsdir.</summary>
    public static async Task<string> EnsureAsync()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cnema-fonts");
        await Gate.WaitAsync();
        try
        {
            Directory.CreateDirectory(dir);
            foreach (var name in Files)
            {
                var target = Path.Combine(dir, name);
                if (File.Exists(target) && new FileInfo(target).Length > 0) continue;
                await using var src = AssetLoader.Open(new Uri($"avares://Avalonia.Fonts.Inter/Assets/{name}"));
                var tmp = target + ".tmp";
                await using (var dst = File.Create(tmp)) await src.CopyToAsync(dst);
                File.Move(tmp, target, overwrite: true);
            }
        }
        finally
        {
            Gate.Release();
        }
        return dir;
    }
}
