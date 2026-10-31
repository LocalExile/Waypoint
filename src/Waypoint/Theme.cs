using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using MaterialDesignThemes.Wpf;
using Microsoft.Win32;

namespace Waypoint;

/// <summary>
/// The chosen appearance: the palette (ThemePalette.cs) as the <c>Shell.*</c> brushes the sections use and the role brushes
/// the settings' dialogs use, Material Design's base theme and primary colour, the reading font, the text size and the
/// accessibility options. System follows the Windows app theme, Windows High Contrast overrides every palette, also when
/// they change while Waypoint runs.
/// </summary>
static class Theme
{
    public static AppearanceSettings Current { get; private set; } = ThemePalette.Normalize(null);
    public static bool IsDark { get; private set; }
    public static bool SystemLight => !WindowsIsDark();
    /// <summary>The text size as a zoom of the whole window: its fixed row and button sizes grow with the text.</summary>
    public static double Zoom => Current.TextScale / 100d;
    /// <summary>The appearance changed (the charts redraw, the status lamps take the new colours).</summary>
    public static event Action? Changed;

    static bool _watching;

    public static void Apply(AppearanceSettings? value)
    {
        Current = ThemePalette.Normalize(value);
        bool light = SystemLight, contrast = SystemParameters.HighContrast;
        IsDark = ThemePalette.IsDark(Current, light);
        var resources = Application.Current.Resources;
        var palette = ThemePalette.Build(Current, light);
        var shell = ThemePalette.Shell(palette, Current, IsDark);
        foreach (var (key, color) in palette.Concat(shell)) resources[key] = Frozen(color);
        if (contrast) UseSystemColors(resources);

        var material = new PaletteHelper();
        var theme = material.GetTheme();
        theme.SetBaseTheme(IsDark ? BaseTheme.Dark : BaseTheme.Light);
        theme.SetPrimaryColor(((SolidColorBrush)resources["Shell.Primary"]).Color);
        theme.SetSecondaryColor(((SolidColorBrush)resources["Shell.Primary"]).Color);
        material.SetTheme(theme);

        var font = Current.Font switch
        {
            "OpenDyslexic" => new FontFamily(new Uri("pack://application:,,,/Waypoint;component/"), "./Fonts/#OpenDyslexic"),
            "Verdana" => new FontFamily("Verdana"),
            _ => new FontFamily("Segoe UI"),
        };
        resources["AppFont"] = font;
        foreach (int size in (int[])[10, 11, 12, 13, 14, 16, 18, 21, 22, 23, 27]) resources["Font" + size] = size * Zoom;
        resources["FocusStroke"] = Current.StrongFocus ? 3d : 1.5d;
        resources["FocusThickness"] = new Thickness(Current.StrongFocus ? 3 : 1.5);
        Brand(resources, palette, contrast);

        foreach (Window window in Application.Current.Windows) Dress(window);
        Watch();
        Changed?.Invoke();
    }

    /// <summary>The emblem: the unchanged artwork on the default scheme, recoloured to the scheme's accent on the others.</summary>
    static void Brand(ResourceDictionary resources, Dictionary<string, Color> palette, bool contrast)
    {
        if (!contrast && Current.Scheme == ThemePalette.DefaultScheme && !Current.IncreaseContrast)
        {
            resources["BrandArtwork"] = WaypointArtwork.Original;
            return;
        }
        Color surface = palette["AccentSurface"], detail = palette["Muted"];
        Color ink = contrast ? SystemColors.WindowTextColor : palette["Accent"];
        if (!contrast && Current.ProtectContrast)
            ink = ThemePalette.Ensure(ink, [surface, ThemePalette.Mix(surface, detail, .48)], Current.IncreaseContrast ? 4.5 : 3);
        resources["BrandArtwork"] = WaypointArtwork.Create(surface, detail, ink, contrast, Current.IncreaseContrast);
    }

    /// <summary>Windows High Contrast: the system's own colours in place of every palette.</summary>
    static void UseSystemColors(ResourceDictionary resources)
    {
        void Set(Brush brush, params string[] keys) { foreach (string key in keys) resources[key] = brush; }
        Set(SystemColors.WindowBrush, "Background", "Sidebar", "Surface", "InputSurface", "StatusSurface", "AccentSurface",
            "Shell.Background", "Shell.Panel", "Shell.RowAlt", "Shell.Banner", "Shell.Sidebar");
        Set(SystemColors.WindowTextBrush, "Text", "Muted", "Outline", "Error", "Warning", "Success", "BadgeText", "CaptionIcon",
            "Shell.Text", "Shell.TextSecondary", "Shell.Border", "Shell.Good", "Shell.Warning", "Shell.Error", "Shell.Idle",
            "Shell.SidebarText", "Shell.SidebarStrong");
        Set(SystemColors.ControlBrush, "ControlSurface", "BadgeSurface");
        Set(SystemColors.HighlightBrush, "Accent", "PrimaryFill", "SelectedSurface", "HoverSurface", "Focus", "ScrollThumb", "CloseHover",
            "Shell.Accent", "Shell.Primary", "Shell.SidebarSelected", "Shell.RowHover");
        Set(SystemColors.HighlightTextBrush, "AccentText", "SelectedText", "CheckMark", "CloseText", "Shell.OnAccent");
    }

    static void Watch()
    {
        if (_watching) return;
        _watching = true;
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Accessibility or UserPreferenceCategory.Color)
                Application.Current.Dispatcher.BeginInvoke(() => Apply(Current));
        };
        SystemParameters.StaticPropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SystemParameters.HighContrast)) Application.Current.Dispatcher.BeginInvoke(() => Apply(Current));
        };
    }

    /// <summary>A brush of the current theme, for code that draws (the charts) or colours a value.</summary>
    public static Brush Brush(string key) => Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;

    /// <summary>What every window takes from the appearance: the title bar, the text size, and motion.</summary>
    public static void Dress(Window window)
    {
        TitleBar(window);
        if (window is not FormWindow && window.Content is FrameworkElement content) content.LayoutTransform = Zoom == 1 ? Transform.Identity : new ScaleTransform(Zoom, Zoom);
        RippleAssist.SetIsDisabled(window, Current.ReduceMotion || !SystemParameters.ClientAreaAnimation);
    }

    /// <summary>What Windows draws around a window: dark with the dark theme (Windows 10 2004 and later), and the rounded
    /// corners of Windows 11, which a window with its own title bar has to ask for. Earlier versions ignore both.</summary>
    public static void TitleBar(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            window.SourceInitialized += (_, _) => TitleBar(window);
            return;
        }
        int dark = IsDark ? 1 : 0;
        DwmSetWindowAttribute(handle, DarkModeAttribute, ref dark, sizeof(int));
        if (window.AllowsTransparency) return;
        int round = RoundCorners;
        DwmSetWindowAttribute(handle, CornerAttribute, ref round, sizeof(int));
    }

    const int DarkModeAttribute = 20;   // DWMWA_USE_IMMERSIVE_DARK_MODE
    const int CornerAttribute = 33;     // DWMWA_WINDOW_CORNER_PREFERENCE
    const int RoundCorners = 2;         // DWMWCP_ROUND

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    static bool WindowsIsDark() =>
        Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is 0;

    static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
