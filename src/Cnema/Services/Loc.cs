using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace Cnema.Services;

/// <summary>
/// Translations. The English text is the key, so code and XAML stay readable and anything not yet
/// translated simply shows in English. Each language is a flat JSON file of "English": "translation"
/// embedded from Assets/Lang. In XAML use the attached properties in <see cref="Tr"/>; in code use
/// <see cref="T"/> and <see cref="F"/>.
/// </summary>
public static class Loc
{
    /// <summary>Languages Cnema ships, as (code, name in that language). "" means follow the system.</summary>
    public static readonly IReadOnlyList<(string Code, string Name)> Languages = new[]
    {
        ("en", "English"),
        ("sv", "Svenska"),
    };

    private static Dictionary<string, string> _strings = new();

    /// <summary>The language in use, e.g. "en" or "sv".</summary>
    public static string Current { get; private set; } = "en";

    /// <summary>Raised on the UI thread after <see cref="Apply"/> switches language.</summary>
    public static event Action? Changed;

    /// <summary>Translates an English UI string.</summary>
    public static string T(string english) =>
        _strings.TryGetValue(english, out var s) && s.Length > 0 ? s : english;

    /// <summary>Translates an English format string ("{0} clips") and fills it in.</summary>
    public static string F(string english, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, T(english), args);

    /// <summary>Picks singular or plural English, then translates it: P(n, "{0} clip", "{0} clips").</summary>
    public static string P(long n, string one, string many) => F(n == 1 ? one : many, n);

    /// <summary>What "" (follow the system) resolves to on this machine.</summary>
    public static string SystemLanguage
    {
        get
        {
            var sys = CultureInfo.InstalledUICulture.TwoLetterISOLanguageName;
            var ui = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
            foreach (var code in new[] { ui, sys, MacPreferredLanguage() })
                if (code is not null && Languages.Any(l => l.Code == code)) return code;
            return "en";
        }
    }

    /// <summary>Switches language. "" or null follows the system; unknown codes fall back to English.</summary>
    private static string? _loadedFor;

    public static void Apply(string? code)
    {
        var resolved = string.IsNullOrEmpty(code) ? SystemLanguage : code;
        if (!Languages.Any(l => l.Code == resolved)) resolved = "en";
        if (resolved == _loadedFor) return; // already in use (closing Settings without a change)
        _loadedFor = resolved;
        _strings = resolved == "en" ? new() : LoadFile(resolved);
        ApplyDates(resolved);
        var changed = resolved != Current;
        Current = resolved;
        if (changed) Changed?.Invoke();
    }

    private static CultureInfo? _system;

    /// <summary>
    /// Month and day names follow the app's language ("måndag 27 juli" in Swedish), while numbers keep the
    /// system's own format: only the date names and patterns are swapped in.
    /// </summary>
    private static void ApplyDates(string code)
    {
        try
        {
            _system ??= CultureInfo.CurrentCulture;
            CultureInfo culture;
            if (_system.TwoLetterISOLanguageName == code) culture = _system;
            else
            {
                culture = (CultureInfo)_system.Clone();
                culture.DateTimeFormat = (DateTimeFormatInfo)CultureInfo.GetCultureInfo(code == "sv" ? "sv-SE" : "en-GB").DateTimeFormat.Clone();
            }
            CultureInfo.CurrentCulture = culture;
            CultureInfo.DefaultThreadCurrentCulture = culture;
        }
        catch (Exception ex)
        {
            Log.Warn($"Date names for {code}: {ex.Message}");
        }
    }

    private static Dictionary<string, string> LoadFile(string code)
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"Cnema.Lang.{code}.json");
            if (stream is null) return new();
            return JsonSerializer.Deserialize<Dictionary<string, string>>(stream,
                new JsonSerializerOptions { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) ?? new();
        }
        catch (Exception ex)
        {
            Log.Error($"Could not load the {code} translation", ex);
            return new();
        }
    }

    /// <summary>
    /// .NET on macOS reports the region format's language, not the language picked in System Settings,
    /// so ask the system for the first preferred language too.
    /// </summary>
    private static string? MacPreferredLanguage()
    {
        if (!OperatingSystem.IsMacOS()) return null;
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("defaults", "read -g AppleLanguages")
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null) return null;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(2000);
            // ( "sv-SE", "en-SE" )
            var first = output.Split('"', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim()).FirstOrDefault(s => s.Length >= 2 && char.IsLetter(s[0]));
            return first?[..2].ToLowerInvariant();
        }
        catch
        {
            return null;
        }
    }
}
