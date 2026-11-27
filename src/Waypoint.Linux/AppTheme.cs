using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;

namespace Waypoint;

/// <summary>
/// The appearance on Linux, from the same palette as on Windows (<see cref="ThemePalette"/>, linked from src/Waypoint): the
/// colour scheme in light, dark or the system's mode, custom colours, the colour-vision profiles and stronger contrast, put
/// into the application's <c>Shell.*</c> brushes, which every view reads by name. Light and dark also pick Avalonia's own
/// controls' variant, and the reading font (OpenDyslexic is bundled, as on Windows) becomes every control's. The text size
/// and the stronger focus borders of the Windows appearance are Windows-only: there they scale WPF's own resources.
/// </summary>
static class AppTheme
{
    /// <summary>The appearance in use, normalized.</summary>
    public static AppearanceSettings Current { get; private set; } = new();

    /// <summary>The colours changed: what draws with fixed brushes draws again.</summary>
    public static event Action? Changed;

    static bool _following;

    /// <summary>Puts an appearance into the application's brushes; System follows the desktop's light or dark setting.</summary>
    public static void Apply(AppearanceSettings? settings)
    {
        var app = App.Instance;
        var chosen = ThemePalette.Normalize(settings);
        bool systemLight = SystemLight;
        bool dark = ThemePalette.IsDark(chosen, systemLight);
        var palette = ThemePalette.Build(chosen, systemLight);
        var shell = ThemePalette.Shell(palette, chosen, dark);
        // The table rows' cells (Linux only): the banner's surface.
        shell["Shell.Cell"] = shell["Shell.Banner"];
        foreach (var (key, color) in shell) app.Resources[key] = new SolidColorBrush(color);
        app.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        // The Fluent theme's controls read their font from this resource; without it they keep the theme's own (Inter).
        if (Font(chosen.Font) is { } font) app.Resources[FontKey] = font;
        else app.Resources.Remove(FontKey);
        Current = chosen;
        if (!_following && app.PlatformSettings is { } platform)
        {
            _following = true;
            platform.ColorValuesChanged += (_, _) => { if (Current.Mode == "System") Apply(Current); };
        }
        Changed?.Invoke();
    }

    const string FontKey = "ContentControlThemeFontFamily";

    /// <summary>The reading font chosen; null for the default.</summary>
    static FontFamily? Font(string font) => font switch
    {
        "OpenDyslexic" => new FontFamily("avares://Waypoint/Fonts#OpenDyslexic"),
        // Verdana where it is installed; its metric twin from DejaVu where it is not.
        "Verdana" => new FontFamily("Verdana, DejaVu Sans"),
        _ => null,
    };

    /// <summary>Whether the desktop is in light mode, which System follows.</summary>
    public static bool SystemLight => App.Instance.PlatformSettings?.GetColorValues().ThemeVariant == PlatformThemeVariant.Light;

    /// <summary>A colour of the theme by its name, as on Windows.</summary>
    public static IBrush Brush(string key) => App.Brush(key);
}
