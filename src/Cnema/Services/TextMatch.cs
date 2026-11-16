using System.Globalization;

namespace Cnema.Services;

/// <summary>
/// Search matching that is forgiving about case and accents, so "rakna" finds "räkna", "o" finds "ö" and a
/// word written with a combining ring (as some transcripts are) still matches the same word typed normally.
/// </summary>
public static class TextMatch
{
    private static readonly CompareInfo Compare = CultureInfo.InvariantCulture.CompareInfo;
    private const CompareOptions Loose = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace | CompareOptions.IgnoreWidth;

    public static bool Has(string? text, string term) => text is not null && IndexOf(text, term) >= 0;

    public static int IndexOf(string text, string term)
    {
        if (term.Length == 0) return 0;
        try { return Compare.IndexOf(text, term, Loose); }
        catch (Exception) { return text.IndexOf(term, StringComparison.OrdinalIgnoreCase); } // no ICU: plain matching
    }
}
