using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using MaterialDesignThemes.Wpf;
using Microsoft.Win32;

namespace GameServer.Launcher;

enum ThemeChoice { System, Light, Dark }

/// <summary>
/// Light or dark: Material Design's base theme and primary colour, the launcher's own <c>Shell.*</c> brushes (Shell.xaml
/// holds the light ones), and the windows' title bars. System follows the Windows app theme, also when it changes while
/// the launcher runs.
/// </summary>
static class Theme
{
    static readonly Dictionary<string, string> Light = new()
    {
        ["Shell.Sidebar"] = "#1D2F9F", ["Shell.SidebarSelected"] = "#3550D4", ["Shell.SidebarText"] = "#DDE3FF",
        ["Shell.Background"] = "#FFFFFF", ["Shell.RowAlt"] = "#F6F7F9", ["Shell.RowHover"] = "#EEF1FB",
        ["Shell.Border"] = "#E4E7EC", ["Shell.Text"] = "#1F2328", ["Shell.TextSecondary"] = "#6B7280",
        ["Shell.Banner"] = "#E9EDFB", ["Shell.Accent"] = "#2442C8", ["Shell.Panel"] = "#F6F7F9",
        ["Shell.Good"] = "#2E9E4F", ["Shell.Warning"] = "#C77700", ["Shell.Error"] = "#C62828", ["Shell.Idle"] = "#9AA1AD",
        ["Shell.UpdateBack"] = "#E3F5E8", ["Shell.UpdateBorder"] = "#7CC995", ["Shell.UpdateText"] = "#14532D", ["Shell.UpdateAccent"] = "#1E8E3E",
    };
    static readonly Dictionary<string, string> Dark = new()
    {
        ["Shell.Sidebar"] = "#152274", ["Shell.SidebarSelected"] = "#2C45C4", ["Shell.SidebarText"] = "#C9D2FF",
        ["Shell.Background"] = "#16181D", ["Shell.RowAlt"] = "#1C1F26", ["Shell.RowHover"] = "#232A3D",
        ["Shell.Border"] = "#2B303A", ["Shell.Text"] = "#E6E8EC", ["Shell.TextSecondary"] = "#9AA1AD",
        ["Shell.Banner"] = "#1A2550", ["Shell.Accent"] = "#4D6BFF", ["Shell.Panel"] = "#1C1F26",
        ["Shell.Good"] = "#43B865", ["Shell.Warning"] = "#E0A030", ["Shell.Error"] = "#EF5350", ["Shell.Idle"] = "#6B7280",
        ["Shell.UpdateBack"] = "#173A25", ["Shell.UpdateBorder"] = "#2F7D4C", ["Shell.UpdateText"] = "#CDEFD8", ["Shell.UpdateAccent"] = "#2EA352",
    };

    public static ThemeChoice Mode { get; private set; } = ThemeChoice.System;
    public static bool IsDark { get; private set; }
    /// <summary>The theme changed (the charts redraw).</summary>
    public static event Action? Changed;

    static bool _watching;

    public static void Apply(ThemeChoice mode)
    {
        Mode = mode;
        IsDark = mode == ThemeChoice.Dark || mode == ThemeChoice.System && WindowsIsDark();
        var resources = Application.Current.Resources;
        foreach (var (key, hex) in IsDark ? Dark : Light) resources[key] = new SolidColorBrush(Hex(hex));

        var palette = new PaletteHelper();
        var theme = palette.GetTheme();
        theme.SetBaseTheme(IsDark ? BaseTheme.Dark : BaseTheme.Light);
        theme.SetPrimaryColor(Hex(IsDark ? Dark["Shell.Accent"] : Light["Shell.Accent"]));
        palette.SetTheme(theme);

        foreach (Window window in Application.Current.Windows) TitleBar(window);
        if (!_watching)
        {
            _watching = true;
            SystemEvents.UserPreferenceChanged += (_, e) =>
            {
                if (e.Category == UserPreferenceCategory.General && Mode == ThemeChoice.System)
                    Application.Current.Dispatcher.BeginInvoke(() => Apply(ThemeChoice.System));
            };
        }
        Changed?.Invoke();
    }

    /// <summary>A brush of the current theme, for code that draws (the charts) or colours a value.</summary>
    public static Brush Brush(string key) => Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;

    /// <summary>The window's title bar dark with the dark theme (Windows 10 2004 and later; earlier ones ignore it).</summary>
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
    }

    const int DarkModeAttribute = 20;   // DWMWA_USE_IMMERSIVE_DARK_MODE

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    static bool WindowsIsDark() =>
        Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is 0;

    static Color Hex(string hex) => (Color)ColorConverter.ConvertFromString(hex);
}
