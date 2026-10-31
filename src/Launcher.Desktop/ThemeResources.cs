using System.Windows;
using System.Windows.Media;
using Launcher.Core;

namespace Launcher.Desktop;

public static class ThemeResources
{
    public static double Scale => Application.Current is App app ? app.Appearance.TextScale / 100d : 1;
    public static double Font(double size) => size * Scale;
    public static void Apply(ResourceDictionary resources, AppearanceSettings settings, bool systemLight, bool highContrast)
    {
        void Set(string key, object value) { if (!Equals(resources[key], value)) resources[key] = value; }
        if (!highContrast)
            foreach (var (key, color) in ThemePalette.Build(settings, systemLight))
            {
                if (resources[key] is SolidColorBrush existing && existing.Color == color) continue;
                var brush = new SolidColorBrush(color); brush.Freeze(); resources[key] = brush;
            }
        var family = settings.Font switch
        {
            "OpenDyslexic" => new FontFamily(new Uri($"pack://application:,,,/{typeof(App).Assembly.GetName().Name};component/"), "./Fonts/#OpenDyslexic"),
            "Verdana" => new FontFamily("Verdana"),
            _ => new FontFamily("Segoe UI")
        };
        if (resources["AppFont"] is not FontFamily oldFamily || oldFamily.Source != family.Source) resources["AppFont"] = family;
        foreach (int size in new[] { 10, 11, 12, 13, 14, 16, 18, 21, 22, 23, 27 }) Set("Font" + size, size * settings.TextScale / 100d);
        Set("FocusThickness", new Thickness(settings.StrongFocus ? 4 : 2));
        Set("AnimateProgress", !settings.ReduceMotion && SystemParameters.ClientAreaAnimation);
        Set("WorkspaceMinHeight", 380 * Math.Pow(settings.TextScale / 100d, 2) * (settings.Font == "OpenDyslexic" ? 1.3 : 1));
        bool compact = settings.TextScale >= 125 || settings.Font == "OpenDyslexic";
        Set("SidebarDetailsExpanded", !compact);
        Set("BrandSubtitleVisibility", compact ? Visibility.Collapsed : Visibility.Visible);
        if (!highContrast) return;
        foreach (string key in new[] { "Background", "Sidebar", "Surface", "InputSurface", "StatusSurface", "AccentSurface" }) Set(key, SystemColors.WindowBrush);
        foreach (string key in new[] { "Text", "Muted", "Outline", "Error", "Warning", "Success", "BadgeText", "CaptionIcon" }) Set(key, SystemColors.WindowTextBrush);
        foreach (string key in new[] { "ControlSurface", "BadgeSurface" }) Set(key, SystemColors.ControlBrush);
        foreach (string key in new[] { "Accent", "PrimaryFill", "SelectedSurface", "HoverSurface", "Focus", "ScrollThumb", "CloseHover" }) Set(key, SystemColors.HighlightBrush);
        foreach (string key in new[] { "AccentText", "SelectedText", "CheckMark", "CloseText" }) Set(key, SystemColors.HighlightTextBrush);
    }
}
