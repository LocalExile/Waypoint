using System.Globalization;
using System.Windows.Media;
using Launcher.Core;

namespace Launcher.Desktop;

public sealed record ColorScheme(string Id, string Name, string Hex);
public sealed record ColorRole(string Key, string Label, bool Advanced = false);

/// <summary>Pure palette generation; no settings, registry or resource dictionary writes.</summary>
public static class ThemePalette
{
    public static readonly ColorScheme[] Schemes =
    [
        new("waypoint", "Waypoint mint", "#51B68A"), new("slate", "Slate", "#718096"),
        new("ocean", "Ocean", "#2584D7"), new("ice", "Ice", "#31A5C7"),
        new("teal", "Teal", "#129C91"), new("forest", "Forest", "#34845B"),
        new("lime", "Lime", "#789D27"), new("gold", "Gold", "#BC8A19"),
        new("amber", "Amber", "#DA8A18"), new("copper", "Copper", "#B66B3A"),
        new("sunset", "Sunset", "#D26B49"), new("ruby", "Ruby", "#BA455D"),
        new("rose", "Rose", "#CC6D91"), new("orchid", "Orchid", "#AD58AD"),
        new("violet", "Violet", "#8961CF"), new("indigo", "Indigo", "#6377C4"),
        new("sand", "Sand", "#A68A67"), new("mono", "Monochrome", "#808080")
    ];
    public static readonly ColorRole[] Roles =
    [
        new("Accent", "Accent text and icons"), new("PrimaryFill", "Primary buttons"),
        new("Background", "Main background"), new("Surface", "Panels"), new("Sidebar", "Sidebar"),
        new("Text", "Main text"), new("Muted", "Secondary text"), new("ControlSurface", "Buttons"),
        new("InputSurface", "Input fields", true), new("HoverSurface", "Hovered controls", true),
        new("SelectedSurface", "Selected items", true), new("SelectedText", "Selected text", true),
        new("Outline", "Borders", true), new("Focus", "Keyboard focus", true),
        new("AccentText", "Primary button text", true), new("AccentSurface", "Logo background", true),
        new("StatusSurface", "Status banner", true), new("BadgeSurface", "Commit badge background", true),
        new("BadgeText", "Commit badge text", true), new("Success", "Success indicators", true),
        new("Warning", "Attention indicators", true), new("Error", "Error indicators", true),
        new("CaptionIcon", "Window controls", true), new("ScrollThumb", "Scrollbars", true),
        new("CheckMark", "Checkbox marks", true), new("CloseHover", "Close button hover", true), new("CloseText", "Close button symbol", true)
    ];

    public static AppearanceSettings Normalize(AppearanceSettings? value)
    {
        value ??= new();
        string scheme = value.Scheme == "custom" || Schemes.Any(s => s.Id == value.Scheme) ? value.Scheme : "waypoint";
        Dictionary<string, string> Clean(Dictionary<string, string>? entries) => entries?
            .Where(e => Roles.Any(r => r.Key == e.Key) && TryColor(e.Value, out _))
            .ToDictionary(e => e.Key, e => Hex(Parse(e.Value))) ?? [];
        return value with
        {
            Mode = value.Mode is "Light" or "System" ? value.Mode : "Dark",
            Scheme = scheme,
            CustomBase = Schemes.Any(s => s.Id == value.CustomBase) ? value.CustomBase : "waypoint",
            LightColors = Clean(value.LightColors),
            DarkColors = Clean(value.DarkColors),
            Vision = value.Vision is "RedGreen" or "BlueYellow" or "Grayscale" ? value.Vision : "Standard",
            Font = value.Font is "OpenDyslexic" or "Verdana" ? value.Font : "Default",
            TextScale = Math.Clamp(value.TextScale, 100, 150),
            ProtectContrast = value.ProtectContrast || value.IncreaseContrast
        };
    }
    public static bool IsDark(AppearanceSettings settings, bool systemLight) => settings.Mode == "Dark" || settings.Mode == "System" && !systemLight;
    public static Dictionary<string, Color> Build(AppearanceSettings value, bool systemLight)
    {
        var settings = Normalize(value);
        bool dark = IsDark(settings, systemLight);
        var scheme = Schemes.First(s => s.Id == (settings.Scheme == "custom" ? settings.CustomBase : settings.Scheme));
        Color hue = Parse(scheme.Hex), white = Colors.White, black = Parse("#101820");
        Color Tint(string baseHex, double amount) => Mix(Parse(baseHex), hue, amount);
        var p = new Dictionary<string, Color>
        {
            ["Background"] = dark ? Tint("#10151E", .035) : Tint("#F3F5F8", .035),
            ["Surface"] = dark ? Tint("#181F2B", .045) : Tint("#FFFFFF", .015),
            ["Sidebar"] = dark ? Tint("#0C1119", .04) : Tint("#E9EDF2", .055),
            ["Text"] = dark ? Parse("#ECF1F8") : Parse("#18212C"),
            ["Muted"] = dark ? Parse("#A5B2C5") : Parse("#4F5F70"),
            ["ControlSurface"] = dark ? Tint("#273347", .07) : Tint("#E2E8EF", .04),
            ["HoverSurface"] = dark ? Tint("#33445A", .1) : Tint("#D8E1EB", .08),
            ["SelectedSurface"] = dark ? Tint("#293F45", .19) : Mix(white, hue, .19),
            ["SelectedText"] = dark ? Parse("#ECF1F8") : Parse("#18212C"),
            ["Outline"] = dark ? Parse("#65768D") : Parse("#7B8CA0"),
            ["Accent"] = dark ? Mix(hue, white, .58) : Mix(hue, black, .34),
            ["PrimaryFill"] = dark ? Mix(hue, white, .58) : Mix(hue, black, .28),
            ["AccentText"] = dark ? black : white,
            ["AccentSurface"] = dark ? Mix(Parse("#162821"), hue, .18) : Mix(white, hue, .19),
            ["StatusSurface"] = dark ? Tint("#202D39", .055) : Tint("#E7EDF4", .055),
            ["BadgeSurface"] = dark ? Tint("#26364A", .05) : Tint("#E3EAF3", .05),
            ["BadgeText"] = dark ? Parse("#C7DBF4") : Parse("#263C57"),
            ["Success"] = dark ? Parse("#A4E7C4") : Parse("#176B44"),
            ["Warning"] = dark ? Parse("#FFCB87") : Parse("#825000"),
            ["Error"] = dark ? Parse("#FFC0B8") : Parse("#B12727")
        };
        if (scheme.Id == "waypoint" && dark)
        {
            string[] keys = ["Background", "Surface", "Sidebar", "ControlSurface", "HoverSurface", "SelectedSurface", "Accent", "PrimaryFill", "AccentText", "AccentSurface", "StatusSurface", "BadgeSurface"];
            string[] colors = ["#10151E", "#181F2B", "#0C1119", "#273347", "#33445A", "#293F45", "#A4E7C4", "#A4E7C4", "#10251C", "#244338", "#202D39", "#26364A"];
            for (int i = 0; i < keys.Length; i++) p[keys[i]] = Parse(colors[i]);
        }
        p["InputSurface"] = p["Background"]; p["Focus"] = p["Accent"];
        p["CaptionIcon"] = p["Muted"]; p["ScrollThumb"] = p["Outline"];
        p["CheckMark"] = dark ? black : white; p["CloseHover"] = Parse("#C42B1C"); p["CloseText"] = white;
        if (settings.Scheme == "custom")
            foreach (var (key, hex) in (dark ? settings.DarkColors : settings.LightColors)!) p[key] = Parse(hex);
        // Functional colors stay distinguishable by labels/symbols as well as these optional palettes.
        if (settings.Vision is "RedGreen" or "BlueYellow")
        {
            bool redGreen = settings.Vision == "RedGreen";
            p["Accent"] = p["PrimaryFill"] = Parse(dark ? redGreen ? "#81C7FF" : "#D1B3FF" : redGreen ? "#075BA5" : "#674098");
            p["Success"] = Parse(dark ? "#81C7FF" : "#075BA5");
            p["Warning"] = Parse(dark ? "#F8D57E" : "#805600");
            p["Error"] = Parse(dark ? "#FFB887" : "#9B4700");
            p["Focus"] = p["Accent"];
            p["AccentText"] = dark ? black : white;
        }
        if (settings.Vision == "Grayscale")
            foreach (string key in p.Keys.ToArray()) { byte gray = (byte)Math.Round(.2126 * p[key].R + .7152 * p[key].G + .0722 * p[key].B); p[key] = Color.FromRgb(gray, gray, gray); }
        if (settings.ProtectContrast || settings.IncreaseContrast)
        {
            double ratio = settings.IncreaseContrast ? 7 : 4.5;
            // Mid-tone button fills cannot support 7:1 text in either black or white.
            // In enhanced contrast mode, move those fills toward the appropriate endpoint.
            foreach (string key in new[] { "PrimaryFill", "CloseHover" })
                if (Math.Max(Contrast(Colors.Black, p[key]), Contrast(Colors.White, p[key])) < ratio)
                    p[key] = Ensure(p[key], [key == "PrimaryFill" && dark ? Colors.Black : Colors.White], ratio);
            Color[] surfaces = [p["Background"], p["Surface"], p["Sidebar"], p["ControlSurface"], p["InputSurface"], p["HoverSurface"], p["StatusSurface"]];
            p["Text"] = Ensure(p["Text"], surfaces, ratio);
            p["Muted"] = Ensure(p["Muted"], surfaces, ratio);
            foreach (string key in new[] { "Accent", "Success", "Warning", "Error" }) p[key] = Ensure(p[key], [p["Background"], p["Surface"], p["StatusSurface"], p["SelectedSurface"]], ratio);
            p["SelectedText"] = Ensure(p["SelectedText"], [p["SelectedSurface"], p["HoverSurface"]], ratio);
            p["AccentText"] = Ensure(p["AccentText"], [p["PrimaryFill"]], ratio);
            p["BadgeText"] = Ensure(p["BadgeText"], [p["BadgeSurface"]], ratio);
            p["CaptionIcon"] = Ensure(p["CaptionIcon"], [p["Background"], p["HoverSurface"]], ratio);
            p["Focus"] = Ensure(p["Focus"], [p["Background"], p["Surface"], p["ControlSurface"]], 3);
            p["ScrollThumb"] = Ensure(p["ScrollThumb"], [p["Background"]], 3);
            p["CheckMark"] = Ensure(p["CheckMark"], [p["Accent"]], ratio);
            p["CloseText"] = Ensure(p["CloseText"], [p["CloseHover"]], ratio);
        }
        return p;
    }
    public static double Contrast(Color a, Color b)
    {
        static double L(Color c) { static double S(byte b) { double x = b / 255d; return x <= .04045 ? x / 12.92 : Math.Pow((x + .055) / 1.055, 2.4); } return .2126 * S(c.R) + .7152 * S(c.G) + .0722 * S(c.B); }
        double x = L(a), y = L(b); return (Math.Max(x, y) + .05) / (Math.Min(x, y) + .05);
    }
    public static Color Ensure(Color color, Color[] backgrounds, double ratio)
    {
        if (backgrounds.All(b => Contrast(color, b) >= ratio)) return color;
        Color best = color; double bestScore = backgrounds.Min(b => Contrast(color, b));
        foreach (Color end in new[] { Colors.Black, Colors.White })
            for (int step = 1; step <= 100; step++)
            {
                Color next = Mix(color, end, step / 100d); double score = backgrounds.Min(b => Contrast(next, b));
                if (score >= ratio) return next;
                if (score > bestScore) { best = next; bestScore = score; }
            }
        return best;
    }
    public static Color Mix(Color a, Color b, double amount) => Color.FromRgb((byte)Math.Round(a.R + (b.R - a.R) * amount), (byte)Math.Round(a.G + (b.G - a.G) * amount), (byte)Math.Round(a.B + (b.B - a.B) * amount));
    public static bool TryColor(string? text, out Color color)
    {
        color = Colors.Black;
        if (text is not { Length: 7 } || text[0] != '#' || !uint.TryParse(text.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint value)) return false;
        color = Color.FromRgb((byte)(value >> 16), (byte)(value >> 8), (byte)value); return true;
    }
    public static Color Parse(string text) => TryColor(text, out var color) ? color : throw new ArgumentException("Expected #RRGGBB", nameof(text));
    public static string Hex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";
}
