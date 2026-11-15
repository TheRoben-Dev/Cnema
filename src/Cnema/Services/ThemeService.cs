using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;

namespace Cnema.Services;

/// <summary>
/// One colour scheme (ids stay the same across renames, names follow the Savefile brand). The
/// backgrounds and text are worked out from a tint (Hue, Tint = saturation), so every scheme has a
/// dark and a light version; the accent and second colour are the scheme's own.
/// </summary>
public sealed record ColorScheme(string Id, string Name, double Hue, double Tint,
    string Accent, string AccentHover, string Second, string OnAccent = "#FFFFFF", bool Gradients = false)
{
    public override string ToString() => Name;
}

/// <summary>The colours actually put on screen: a scheme in dark or light.</summary>
public sealed record Palette(string Bg0, string Bg1, string Bg2, string Bg3, string Line, string Text, string TextDim,
    string Accent, string AccentHover, string Second, string OnAccent, bool Gradients, bool Light);

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

    public static IReadOnlyList<ColorScheme> Schemes { get; } = new[]
    {
        new ColorScheme("violet", "Memory Card", 252, 0.30, "#8E6BFF", "#A78BFF", "#3DDCFF"),
        new ColorScheme("graphite", "Cartridge", 225, 0.06, "#4C8DFF", "#6BA4FF", "#8AB8FF"),
        new ColorScheme("ember", "Campfire", 18, 0.26, "#FF7A45", "#FF9567", "#FFC857", "#1A0D06"),
        new ColorScheme("mint", "Handheld", 162, 0.24, "#2FCF8F", "#55DDA6", "#7CE8D0", "#04140D"),
        new ColorScheme("rose", "Sleepover", 322, 0.26, "#FF5C93", "#FF7DAA", "#C9A0FF"),
        new ColorScheme("aurora", "Classic Cnema (gradients)", 232, 0.28, "#8E6BFF", "#A78BFF", "#3DDCFF", Gradients: true),
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
    /// as layers; light is a soft tinted paper with white cards. On light the accent and second
    /// colour are deepened a little so text and icons in them stay readable on white.
    /// </summary>
    public static Palette Resolve(ColorScheme s, bool light)
    {
        string N(double l, double satScale = 1) => Hsl(s.Hue, s.Tint * satScale, l);
        // Classic Cnema is the original look, kept exactly as it was.
        if (!light && s.Id == "aurora")
            return new Palette("#090A0F", "#0F111A", "#161926", "#20243A", "#2A2F4A", "#EEF0FA", "#8B91B0",
                s.Accent, s.AccentHover, s.Second, s.OnAccent, true, false);
        if (!light)
            return new Palette(N(0.105), N(0.135), N(0.170), N(0.220), N(0.265, 0.9), N(0.965, 0.6), N(0.690, 0.55),
                s.Accent, s.AccentHover, s.Second, s.OnAccent, s.Gradients, false);
        var accent = Color.Parse(s.Accent);
        var deep = Mix(accent, Colors.Black, 0.10);
        return new Palette(N(0.955, 1.4), N(0.985, 1.6), "#FFFFFF", N(0.915, 1.3), N(0.865, 1.0), N(0.135, 1.2), N(0.430, 0.7),
            Hex(deep), Hex(Mix(accent, Colors.Black, 0.22)), Hex(Mix(Color.Parse(s.Second), Colors.Black, 0.32)),
            s.OnAccent, s.Gradients, true);
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
    /// clearly visible lines, and drops every gradient.
    /// </summary>
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
                Text = "#FFFFFF", TextDim = "#E0E0E0", Gradients = false,
            };
        var r = app.Resources;
        static Color C(string hex) => Color.Parse(hex);
        static SolidColorBrush B(string hex, double opacity = 1) => new(Color.Parse(hex), opacity);
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
        r["CardGlow"] = new BoxShadows(new BoxShadow { Blur = 22, Color = Color.FromArgb((byte)(s.Light ? 0x40 : 0x4D), accent.R, accent.G, accent.B) });
        // Keyboard / controller focus: the same accent glow as a hovered card, a little tighter and stronger.
        r["FocusGlow"] = new BoxShadows(new BoxShadow { Blur = 14, Spread = 1, Color = Color.FromArgb(0x73, accent.R, accent.G, accent.B) });
        r["FocusRing"] = new SolidColorBrush(Mix(accent, s.Light ? Colors.Black : Colors.White, 0.12));
        // Badges and small buttons sit on thumbnails and video, so they stay dark in light mode too.
        r["Overlay"] = s.Light ? new SolidColorBrush(C("#17141F"), 0.78) : new SolidColorBrush(C(s.Bg0), 0.88);
        r["OnOverlay"] = B(s.Light ? "#FFFFFF" : s.Text);
        // Toasts and the speed badge float over the video, so they stay solid enough to read.
        r["ToastBg"] = new SolidColorBrush(C(s.Light ? s.Bg1 : "#14151B"), s.Light ? 0.96 : 0.9);

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
        r["SystemAccentColorLight3"] = Mix(C(s.AccentHover), Colors.White, 0.5);
        r["SystemAccentColorDark1"] = Mix(accent, Colors.Black, 0.18);
        r["SystemAccentColorDark2"] = Mix(accent, Colors.Black, 0.3);

        // Menus, submenus and flyouts sit on the scheme's surfaces instead of Fluent's grey.
        r["MenuFlyoutPresenterBackground"] = B(s.Bg2);
        r["MenuFlyoutPresenterBorderBrush"] = B(s.Line);
        r["MenuFlyoutItemBackgroundPointerOver"] = B(s.Bg3);
        r["FlyoutPresenterBackground"] = B(s.Bg2);
        r["FlyoutBorderThemeBrush"] = B(s.Line);
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
