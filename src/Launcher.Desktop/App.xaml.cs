using System.ComponentModel;
using System.Windows;
using System.Windows.Media;

namespace Launcher.Desktop;

public partial class App : Application
{
    readonly Dictionary<string, Brush> darkBrushes = [];

    protected override void OnStartup(StartupEventArgs e)
    {
        ApplyContrast(SystemParameters.HighContrast);
        SystemParameters.StaticPropertyChanged += SystemSettingChanged;
        base.OnStartup(e);
    }

    void SystemSettingChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.HighContrast))
            Dispatcher.Invoke(() => ApplyContrast(SystemParameters.HighContrast));
    }

    public void ApplyContrast(bool enabled)
    {
        if (darkBrushes.Count == 0)
            foreach (var key in Resources.Keys.OfType<string>())
                if (Resources[key] is Brush brush) darkBrushes[key] = brush;
        if (!enabled)
        {
            foreach (var pair in darkBrushes) Resources[pair.Key] = pair.Value;
            return;
        }
        foreach (string key in new[] { "Background", "Sidebar", "Surface", "StatusSurface", "AccentSurface" }) Resources[key] = SystemColors.WindowBrush;
        foreach (string key in new[] { "Text", "Muted", "Outline", "Error", "BadgeText" }) Resources[key] = SystemColors.WindowTextBrush;
        foreach (string key in new[] { "ControlSurface", "BadgeSurface" }) Resources[key] = SystemColors.ControlBrush;
        foreach (string key in new[] { "Accent", "SelectedSurface", "HoverSurface" }) Resources[key] = SystemColors.HighlightBrush;
        foreach (string key in new[] { "AccentText", "SelectedText" }) Resources[key] = SystemColors.HighlightTextBrush;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SystemParameters.StaticPropertyChanged -= SystemSettingChanged;
        base.OnExit(e);
    }
}
