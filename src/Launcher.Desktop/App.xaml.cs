using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using Launcher.Core;
using Microsoft.Win32;

namespace Launcher.Desktop;

public partial class App : Application
{
    public AppearanceSettings Appearance { get; private set; } = new();
    bool contrast;
    public bool SystemLight { get; private set; }
    public bool IsSessionEnding { get; private set; }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        IsSessionEnding = true;
        base.OnSessionEnding(e);
        if (e.Cancel) IsSessionEnding = false;
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        contrast = SystemParameters.HighContrast;
        SystemLight = ReadSystemLight();
        ApplyAppearance(Appearance);
        SystemParameters.StaticPropertyChanged += SystemSettingChanged;
        SystemEvents.UserPreferenceChanged += UserPreferenceChanged;
        base.OnStartup(e);
        // Explicit startup also lets in-process checks supply an unshown fixture window
        // before pumping bindings, without starting a second window and its runtime timers.
        if (MainWindow is null) await StartDesktopAsync(e.Args);
    }

    public static bool ReadSystemLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is not int value || value != 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException) { return false; }
    }
    void UserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e) => Dispatcher.BeginInvoke(new Action(RefreshSystemAppearance));
    void SystemSettingChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SystemParameters.HighContrast) or nameof(SystemParameters.ClientAreaAnimation))
            Dispatcher.BeginInvoke(new Action(RefreshSystemAppearance));
    }
    void RefreshSystemAppearance()
    {
        SystemLight = ReadSystemLight();
        contrast = SystemParameters.HighContrast;
        ApplyAppearance(Appearance);
    }
    public void ApplyAppearance(AppearanceSettings? settings)
    {
        Appearance = ThemePalette.Normalize(settings);
        ThemeResources.Apply(Resources, Appearance, SystemLight, contrast);
        foreach (var window in Windows.OfType<MainWindow>()) window.RefreshServiceIndicatorAppearance();
    }
    public void ApplyContrast(bool enabled) { contrast = enabled; ApplyAppearance(Appearance); }

    protected override void OnExit(ExitEventArgs e)
    {
        if (MainWindow is MainWindow workspace) workspace.DisposeTrayIcon();
        desktopInstance?.Dispose();
        SystemParameters.StaticPropertyChanged -= SystemSettingChanged;
        SystemEvents.UserPreferenceChanged -= UserPreferenceChanged;
        base.OnExit(e);
    }
}
