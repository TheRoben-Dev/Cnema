using Avalonia;
using Avalonia.Media;

namespace Cnema.Services;

/// <summary>One colour scheme: backgrounds, text, an accent and a second colour for selections and highlights.</summary>
public sealed record ColorScheme(string Id, string Name, string Bg0, string Bg1, string Bg2, string Bg3, string Line,
    string Text, string TextDim, string Accent, string AccentHover, string Second, string OnAccent = "#FFFFFF", bool Gradients = false)
{
    public override string ToString() => Name;
}

/// <summary>
/// Swaps the app's brushes for a colour scheme. The views use DynamicResource for every themed
/// brush, so a change shows at once.
/// </summary>
public static class ThemeService
{
    public const string DefaultId = "violet";

    public static IReadOnlyList<ColorScheme> Schemes { get; } = new[]
    {
        new ColorScheme("violet", "Violet", "#090A0F", "#0F111A", "#161926", "#20243A", "#2A2F4A", "#EEF0FA", "#8B91B0", "#8E6BFF", "#A78BFF", "#3DDCFF"),
        new ColorScheme("graphite", "Graphite", "#0B0B0D", "#121214", "#1A1A1D", "#26262B", "#323238", "#F2F2F4", "#93939C", "#4C8DFF", "#6BA4FF", "#8AB8FF"),
        new ColorScheme("ember", "Ember", "#0E0A09", "#16100E", "#1F1714", "#2E221D", "#3D2E27", "#FAF1EC", "#B09A8E", "#FF7A45", "#FF9567", "#FFC857", "#1A0D06"),
        new ColorScheme("mint", "Mint", "#080D0C", "#0E1514", "#141E1C", "#1D2C29", "#294039", "#ECF8F4", "#88A8A0", "#2FCF8F", "#55DDA6", "#7CE8D0", "#04140D"),
        new ColorScheme("rose", "Rose", "#0E090C", "#161016", "#1F1620", "#2E2030", "#3F2C40", "#FAEEF5", "#B093A6", "#FF5C93", "#FF7DAA", "#C9A0FF"),
        new ColorScheme("aurora", "Aurora (gradients)", "#090A0F", "#0F111A", "#161926", "#20243A", "#2A2F4A", "#EEF0FA", "#8B91B0", "#8E6BFF", "#A78BFF", "#3DDCFF", Gradients: true),
    };

    public static ColorScheme Find(string? id) =>
        Schemes.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase)) ?? Schemes[0];

    public static ColorScheme Current { get; private set; } = Schemes[0];

    public static bool HighContrast { get; private set; }

    /// <summary>
    /// High contrast keeps the scheme's accent but puts it on pure black with white text and
    /// clearly visible lines, and drops every gradient.
    /// </summary>
    public static void Apply(string? id, bool highContrast = false)
    {
        if (Application.Current is not { } app) return;
        var s = Find(id);
        Current = s;
        HighContrast = highContrast;
        if (highContrast)
            s = s with
            {
                Bg0 = "#000000", Bg1 = "#000000", Bg2 = "#121212", Bg3 = "#242424", Line = "#9A9A9A",
                Text = "#FFFFFF", TextDim = "#E0E0E0", Gradients = false,
            };
        var r = app.Resources;
        static Color C(string hex) => Color.Parse(hex);
        static SolidColorBrush B(string hex, double opacity = 1) => new(Color.Parse(hex), opacity);
        static Color Mix(Color a, Color b, double t) => Color.FromRgb(
            (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
        static IBrush Diagonal(Color from, Color to) => new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(from, 0), new GradientStop(to, 1) },
        };

        r["Bg0"] = B(s.Bg0); r["Bg1"] = B(s.Bg1); r["Bg2"] = B(s.Bg2); r["Bg3"] = B(s.Bg3);
        r["Line"] = B(s.Line); r["Text"] = B(s.Text); r["TextDim"] = B(s.TextDim);
        r["Accent"] = B(s.Accent); r["AccentSoft"] = B(s.Accent, 0.18); r["Cyan"] = B(s.Second);
        r["OnAccent"] = B(s.OnAccent);
        var accent = C(s.Accent);
        var second = C(s.Second);
        r["TagText"] = new SolidColorBrush(Mix(accent, C(s.Text), 0.55));
        r["PartFill"] = new SolidColorBrush(second, 0.13);
        r["PartLine"] = new SolidColorBrush(second, 0.33);
        r["CardGlow"] = new BoxShadows(new BoxShadow { Blur = 22, Color = Color.FromArgb(0x4D, accent.R, accent.G, accent.B) });
        // Keyboard / controller focus: the same accent glow as a hovered card, a little tighter and stronger.
        r["FocusGlow"] = new BoxShadows(new BoxShadow { Blur = 14, Spread = 1, Color = Color.FromArgb(0x73, accent.R, accent.G, accent.B) });
        r["FocusRing"] = new SolidColorBrush(Mix(accent, Colors.White, 0.12));
        r["Overlay"] = new SolidColorBrush(C(s.Bg0), 0.88);

        if (s.Gradients)
        {
            r["Brand"] = Diagonal(accent, second);
            r["BrandButton"] = Diagonal(accent, C("#4C8DFF"));
            r["BrandButtonHover"] = Diagonal(C(s.AccentHover), C("#6BA4FF"));
            r["SidebarGlow"] = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.FromArgb(0x1F, accent.R, accent.G, accent.B), 0), new GradientStop(Color.FromArgb(0, accent.R, accent.G, accent.B), 0.35) },
            };
        }
        else
        {
            r["Brand"] = B(s.Accent);
            r["BrandButton"] = B(s.Accent);
            r["BrandButtonHover"] = B(s.AccentHover);
            r["SidebarGlow"] = Brushes.Transparent;
        }

        // Fluent's own controls (toggles, sliders, progress bars) follow the accent.
        r["SystemAccentColor"] = accent;
        r["SystemAccentColorLight1"] = C(s.AccentHover);
        r["SystemAccentColorLight2"] = Mix(C(s.AccentHover), Colors.White, 0.3);
        r["SystemAccentColorDark1"] = Mix(accent, Colors.Black, 0.18);

        // Menus, submenus and flyouts sit on the scheme's surfaces instead of Fluent's grey.
        r["MenuFlyoutPresenterBackground"] = B(s.Bg2);
        r["MenuFlyoutPresenterBorderBrush"] = B(s.Line);
        r["MenuFlyoutItemBackgroundPointerOver"] = B(s.Bg3);
        r["FlyoutPresenterBackground"] = B(s.Bg2);
        r["FlyoutBorderThemeBrush"] = B(s.Line);
    }
}
