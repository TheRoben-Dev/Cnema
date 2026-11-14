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
    // The source repository is private; builds are mirrored to this public, releases-only repository.
    public const string ReleasesPage = "https://github.com/TheRoben-Dev/Cnema-releases/releases/latest";
    private const string Api = "https://api.github.com/repos/TheRoben-Dev/Cnema-releases/releases/latest";

    public static Version Current { get; } =
        Assembly.GetExecutingAssembly().GetName().Version is { } v ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : new Version(0, 0, 0);
    public static string CurrentText => Current.ToString(3);

    /// <summary>The newer release, or null when up to date or when GitHub can't be reached.</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default) => (await CheckDetailedAsync(ct)).Update;

    /// <summary>Like <see cref="CheckAsync"/>, but also says why nothing came back.</summary>
    public static async Task<(UpdateInfo? Update, string? Error)> CheckDetailedAsync(CancellationToken ct = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Cnema-update-check");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            using var doc = JsonDocument.Parse(await http.GetStringAsync(Api, ct));
            var latest = Parse(doc.RootElement.GetProperty("tag_name").GetString());
            var url = doc.RootElement.TryGetProperty("html_url", out var u) ? u.GetString() : null;
            Log.Info($"Update check: running {CurrentText}, latest release {latest?.ToString(3) ?? "unreadable"}");
            return (latest is not null && latest > Current ? new UpdateInfo(latest, url ?? ReleasesPage) : null, null);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // GitHub answers 404 for a repository that is private or has no published release.
            Log.Warn("Update check failed: the release list is not public (404)");
            return (null, "the release list is not publicly readable");
        }
        catch (Exception ex)
        {
            Log.Warn("Update check failed: " + ex.Message);
            return (null, ex.Message);
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
