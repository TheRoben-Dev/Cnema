using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;

namespace Cnema.Services;

/// <summary>
/// One colour scheme (ids stay the same across renames, names follow the Savefile brand). The
/// backgrounds and text are worked out from a tint (Hue, Tint = saturation), so every scheme has a
/// dark and a light version. Highlight is the lighter partner of the accent: selected icons, small
/// accent text (In / Out times, statuses) and the selection on the timeline.
/// </summary>
public sealed record ColorScheme(string Id, string Name, double Hue, double Tint,
    string Accent, string AccentHover, string Highlight, string OnAccent = "#FFFFFF")
{
    public override string ToString() => Name;
}

/// <summary>The colours actually put on screen: a scheme in dark or light.</summary>
public sealed record Palette(string Bg0, string Bg1, string Bg2, string Bg3, string Line, string Text, string TextDim,
    string Accent, string AccentHover, string Highlight, string OnAccent, bool Light);

/// <summary>One entry of the Appearance picker; Id "" follows the system.</summary>
public sealed record ThemeModeOption(string Id, string English)
{
    public string Name => Loc.T(English);
    public override string ToString() => Name;
}

/// <summary>
/// Swaps the app's brushes for a colour scheme. The views use DynamicResource for every themed
/// brush, so a change shows at once.
/// </summary>
public static class ThemeService
{
    public const string DefaultId = "violet";

    /// <summary>
    /// Neutral greys with Savefile purple as the accent only, for pop (Roben, 2026-11-16); a saved id from an
    /// older scheme falls back to it.
    /// </summary>
    public static IReadOnlyList<ColorScheme> Schemes { get; } = new[]
    {
        new ColorScheme("violet", "Memory Card", 252, 0.0, "#8E6BFF", "#A78BFF", "#C2AEFF"),
    };

    /// <summary>"" follows the system, otherwise "dark" or "light".</summary>
    public static IReadOnlyList<ThemeModeOption> Modes { get; } = new[]
    {
        new ThemeModeOption("", "Same as system"),
        new ThemeModeOption("dark", "Dark"),
        new ThemeModeOption("light", "Light"),
    };

    public static ThemeModeOption FindMode(string? id) =>
        Modes.FirstOrDefault(m => string.Equals(m.Id, id ?? "", StringComparison.OrdinalIgnoreCase)) ?? Modes[0];

    /// <summary>The Appearance setting: "", "dark" or "light". Apply reads it.</summary>
    public static string Mode { get; set; } = "";

    /// <summary>True when the light version is on screen.</summary>
    public static bool IsLight { get; private set; }

    /// <summary>
    /// The scheme in dark or light. Dark sits a step above near-black so cards and panels read
    /// as layers; light is a soft tinted paper with white cards. On light the accent is deepened a
    /// little and the highlight becomes a deep violet, so text and icons in them stay readable on white.
    /// </summary>
    public static Palette Resolve(ColorScheme s, bool light)
    {
        string N(double l, double satScale = 1) => Hsl(s.Hue, s.Tint * satScale, l);
        if (!light)
            return new Palette(N(0.125), N(0.155), N(0.190), N(0.240), N(0.285, 0.9), N(0.955, 0.6), N(0.680, 0.55),
                s.Accent, s.AccentHover, s.Highlight, s.OnAccent, false);
        var accent = Color.Parse(s.Accent);
        var deep = Mix(accent, Colors.Black, 0.10);
        return new Palette(N(0.955, 1.4), "#FFFFFF", "#FFFFFF", N(0.915, 1.3), N(0.865, 1.0), N(0.135, 1.2), N(0.430, 0.7),
            Hex(deep), Hex(Mix(accent, Colors.Black, 0.22)), Hex(Mix(accent, Colors.Black, 0.30)),
            s.OnAccent, true);
    }

    private static string Hsl(double h, double s, double l)
    {
        s = Math.Clamp(s, 0, 1);
        double c = (1 - Math.Abs(2 * l - 1)) * s, hp = h / 60 % 6, x = c * (1 - Math.Abs(hp % 2 - 1)), m = l - c / 2;
        var (r, g, b) = hp switch
        {
            < 1 => (c, x, 0.0), < 2 => (x, c, 0.0), < 3 => (0.0, c, x),
            < 4 => (0.0, x, c), < 5 => (x, 0.0, c), _ => (c, 0.0, x),
        };
        static byte B(double v) => (byte)Math.Round(Math.Clamp(v, 0, 1) * 255);
        return Hex(Color.FromRgb(B(r + m), B(g + m), B(b + m)));
    }

    private static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    public static ColorScheme Find(string? id) =>
        Schemes.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase)) ?? Schemes[0];

    public static ColorScheme Current { get; private set; } = Schemes[0];

    public static bool HighContrast { get; private set; }

    /// <summary>
    /// High contrast keeps the scheme's accent but puts it on pure black with white text and
    /// clearly visible lines.
    /// </summary>
    /// <summary>The colours last put into the app's resources.</summary>
    private static object? _applied;

    public static void Apply(string? id, bool highContrast = false)
    {
        if (Application.Current is not { } app) return;
        var scheme = Find(id);
        Current = scheme;
        HighContrast = highContrast;
        HookSystemTheme(app);
        var light = !highContrast && Mode switch
        {
            "light" => true,
            "dark" => false,
            _ => app.PlatformSettings?.GetColorValues().ThemeVariant == Avalonia.Platform.PlatformThemeVariant.Light,
        };
        IsLight = light;
        // Fluent's own controls (text boxes, toggles, scroll bars) follow the same variant.
        var variant = light ? ThemeVariant.Light : ThemeVariant.Dark;
        if (app.RequestedThemeVariant != variant) app.RequestedThemeVariant = variant;
        var s = Resolve(scheme, light);
        if (highContrast)
            s = s with
            {
                Bg0 = "#000000", Bg1 = "#000000", Bg2 = "#121212", Bg3 = "#242424", Line = "#9A9A9A",
                Text = "#FFFFFF", TextDim = "#E0E0E0",
            };
        // Nothing changed (closing Settings without a preview, saving unrelated settings): leave the brushes be.
        // Swapping them all re-styles every control in the window, a visible hitch with a big library.
        if (Equals(s, _applied)) return;
        _applied = s;
        var r = app.Resources;
        static Color C(string hex) => Color.Parse(hex);
        static SolidColorBrush B(string hex, double opacity = 1) => new(Color.Parse(hex), opacity);

        r["Bg0"] = B(s.Bg0); r["Bg1"] = B(s.Bg1); r["Bg2"] = B(s.Bg2); r["Bg3"] = B(s.Bg3);
        r["Line"] = B(s.Line); r["Text"] = B(s.Text); r["TextDim"] = B(s.TextDim);
        r["Accent"] = B(s.Accent); r["AccentSoft"] = B(s.Accent, 0.18); r["Highlight"] = B(s.Highlight);
        r["OnAccent"] = B(s.OnAccent);
        var accent = C(s.Accent);
        var highlight = C(s.Highlight);
        r["TagText"] = new SolidColorBrush(Mix(accent, C(s.Text), 0.55));
        r["PartFill"] = new SolidColorBrush(highlight, 0.13);
        r["PartLine"] = new SolidColorBrush(highlight, 0.33);
        // Delete buttons: the same red in both looks, a step lighter on hover.
        r["DangerHover"] = B("#FF5C87");
        // Behind dialogs: a deep violet shade over the app. Light keeps it lighter, so the page
        // still reads as "behind" without turning the whole window grey.
        r["Scrim"] = new SolidColorBrush(C(s.Light ? "#202020" : "#000000"), s.Light ? 0.45 : 0.70);
        r["ScrimStrong"] = new SolidColorBrush(C(s.Light ? "#202020" : "#000000"), s.Light ? 0.60 : 0.90);
        r["ScrimLight"] = new SolidColorBrush(C(s.Light ? "#202020" : "#000000"), s.Light ? 0.35 : 0.60);
        r["CardGlow"] = new BoxShadows(new BoxShadow { Blur = 22, Color = Color.FromArgb((byte)(s.Light ? 0x40 : 0x4D), accent.R, accent.G, accent.B) });
        r["DialogShadow"] = new BoxShadows(new BoxShadow { OffsetY = 18, Blur = 60, Color = s.Light ? Color.FromArgb(0x30, 0x20, 0x20, 0x20) : Color.FromArgb(0x80, 0, 0, 0) });
        // Keyboard focus: the same accent glow as a hovered card, a little tighter and stronger.
        r["FocusGlow"] = new BoxShadows(new BoxShadow { Blur = 14, Spread = 1, Color = Color.FromArgb(0x73, accent.R, accent.G, accent.B) });
        r["FocusRing"] = new SolidColorBrush(Mix(accent, s.Light ? Colors.Black : Colors.White, 0.12));
        // Badges and small buttons sit on thumbnails and video, so they stay dark in light mode too.
        r["Overlay"] = s.Light ? new SolidColorBrush(C("#161616"), 0.78) : new SolidColorBrush(C(s.Bg0), 0.88);
        r["OnOverlay"] = B(s.Light ? "#FFFFFF" : s.Text);
        // Toasts and the speed badge float over the video, so they stay solid enough to read.
        r["ToastBg"] = new SolidColorBrush(C(s.Light ? s.Bg1 : "#181818"), s.Light ? 0.96 : 0.9);

        // One flat purple: no gradients anywhere (Roben, 2026-11-15).
        r["Brand"] = B(s.Accent);
        r["BrandButton"] = B(s.Accent);
        r["BrandButtonHover"] = B(s.AccentHover);

        // Fluent's own controls (toggles, sliders, progress bars) follow the accent.
        r["SystemAccentColor"] = accent;
        r["SystemAccentColorLight1"] = C(s.AccentHover);
        r["SystemAccentColorLight2"] = Mix(C(s.AccentHover), Colors.White, 0.3);
        r["SystemAccentColorLight3"] = Mix(C(s.AccentHover), Colors.White, 0.5);
        r["SystemAccentColorDark1"] = Mix(accent, Colors.Black, 0.18);
        r["SystemAccentColorDark2"] = Mix(accent, Colors.Black, 0.3);

        // Menus, submenus and flyouts sit on the scheme's surfaces instead of Fluent's grey.
        r["MenuFlyoutPresenterBackground"] = B(s.Bg2);
        r["MenuFlyoutPresenterBorderBrush"] = B(s.Line);
        r["MenuFlyoutItemBackgroundPointerOver"] = B(s.Bg3);
        r["FlyoutPresenterBackground"] = B(s.Bg2);
        r["FlyoutBorderThemeBrush"] = B(s.Line);

        // Text boxes sit on the card surface in every state, instead of Fluent's black (dark) or
        // white (light) when focused, which looked like a hole in the dialog.
        r["TextControlBackground"] = B(s.Bg1);
        r["TextControlBackgroundPointerOver"] = B(s.Bg1);
        r["TextControlBackgroundFocused"] = B(s.Bg1);
        r["TextControlBorderBrush"] = B(s.Line);
        r["TextControlBorderBrushPointerOver"] = B(s.TextDim);
        r["TextControlForegroundFocused"] = B(s.Text);
        r["TextControlForeground"] = B(s.Text);
        r["TextControlPlaceholderForeground"] = B(s.TextDim);
        r["TextControlPlaceholderForegroundPointerOver"] = B(s.TextDim);
        r["TextControlPlaceholderForegroundFocused"] = B(s.TextDim);
        // Switches that are off: a soft outline in the text colour instead of Fluent's hard black / white.
        r["ToggleSwitchStrokeOff"] = B(s.TextDim);
        r["ToggleSwitchStrokeOffPointerOver"] = B(s.Text);
        r["ToggleSwitchKnobFillOff"] = B(s.TextDim);
        r["ToggleSwitchKnobFillOffPointerOver"] = B(s.Text);
        r["ToggleSwitchFillOff"] = Brushes.Transparent;
        r["ComboBoxBackground"] = B(s.Bg1);
        r["ComboBoxBackgroundPointerOver"] = B(s.Bg2);
        r["ComboBoxBorderBrush"] = B(s.Line);
        r["ComboBoxBorderBrushPointerOver"] = B(s.TextDim);
        r["ComboBoxDropDownBackground"] = B(s.Bg2);
        r["ComboBoxDropDownBorderBrush"] = B(s.Line);
    }

    private static bool _hooked;

    /// <summary>With "Same as system", follow the OS when it switches between dark and light.</summary>
    private static void HookSystemTheme(Application app)
    {
        if (_hooked || app.PlatformSettings is not { } ps) return;
        _hooked = true;
        ps.ColorValuesChanged += (_, _) =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (Mode == "") Apply(Current.Id, HighContrast); });
    }
}
