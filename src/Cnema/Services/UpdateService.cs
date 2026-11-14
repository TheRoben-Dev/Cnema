using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace Cnema.Services;

public sealed record UpdateInfo(Version Version, string Url);

/// <summary>
/// Asks GitHub whether a newer release exists. It only tells the user and opens the release page:
/// builds are not code-signed, so silently replacing the app would be both fragile and rude.
/// </summary>
public static class UpdateService
{
    public const string ReleasesPage = "https://github.com/TheRoben-Dev/Cnema/releases/latest";
    private const string Api = "https://api.github.com/repos/TheRoben-Dev/Cnema/releases/latest";

    public static Version Current { get; } =
        Assembly.GetExecutingAssembly().GetName().Version is { } v ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : new Version(0, 0, 0);
    public static string CurrentText => Current.ToString(3);

    /// <summary>The newer release, or null when up to date or when GitHub can't be reached.</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Cnema-update-check");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            using var doc = JsonDocument.Parse(await http.GetStringAsync(Api, ct));
            var latest = Parse(doc.RootElement.GetProperty("tag_name").GetString());
            var url = doc.RootElement.TryGetProperty("html_url", out var u) ? u.GetString() : null;
            return latest is not null && latest > Current ? new UpdateInfo(latest, url ?? ReleasesPage) : null;
        }
        catch (Exception ex)
        {
            Log.Info("Update check skipped: " + ex.Message);
            return null;
        }
    }

    public static Version? Parse(string? tag)
    {
        var text = (tag ?? "").Trim().TrimStart('v', 'V');
        var dash = text.IndexOf('-');
        if (dash > 0) text = text[..dash];
        return Version.TryParse(text, out var v) ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : null;
    }
}
