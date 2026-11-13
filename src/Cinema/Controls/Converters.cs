using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Cinema.Controls;

public static class Converters
{
    public static readonly IValueConverter LibraryOrFolderIcon = new FuncValueConverter<bool, Geometry?>(isAll =>
        Lookup(isAll ? "IcLibrary" : "IcFolder"));

    public static readonly IValueConverter IsZero = new FuncValueConverter<double, bool>(v => v <= 0);

    /// <summary>Width (minus card padding) → 16:9 height.</summary>
    public static readonly IValueConverter SixteenByNine = new FuncValueConverter<double, double>(w =>
        Math.Max(0, (w - 16) * 9 / 16));

    private static Geometry? Lookup(string key) =>
        Avalonia.Application.Current?.TryFindResource(key, out var r) == true ? r as Geometry : null;
}
