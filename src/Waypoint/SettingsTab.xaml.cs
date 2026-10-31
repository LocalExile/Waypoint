using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;

namespace Waypoint;

/// <summary>The Settings section: the appearance, the language, what closing the window does, updates and the checkout.
/// Every choice is saved as it is made.</summary>
public partial class SettingsTab : UserControl
{
    Shared _shared = null!;
    bool _loading;

    /// <summary>Check for updates: the window has the update card.</summary>
    internal event Action? CheckUpdatesRequested;

    public SettingsTab() => InitializeComponent();

    Settings S => _shared.Settings;

    internal void Attach(Shared shared)
    {
        _shared = shared;
        ServerFolder.Text = Environment.CurrentDirectory;
        ServerFolder.ToolTip = Environment.CurrentDirectory;
        Theme.Changed += ShowAppearance;
        L.Current.PropertyChanged += (_, _) => Show();
        Show();
    }

    void Show()
    {
        _loading = true;
        ShowAppearance();
        English.IsChecked = L.Current.Language != "ru";
        Russian.IsChecked = L.Current.Language == "ru";
        WindowChoices.Load(S.CloseToTray, S.StopServicesOnExit);
        AlwaysAsk.IsChecked = S.AlwaysAskOnClose;
        TrayWhileGame.IsChecked = S.TrayWhileGameRuns;
        CheckAtStart.IsChecked = S.CheckForUpdates;
        KeepUpToDate.IsChecked = S.KeepUpToDate;
        UseProfile.IsChecked = S.UseLocalProfile;
        KeepUpToDate.Visibility = S.Advanced ? Visibility.Collapsed : Visibility.Visible;
        CheckoutGroup.Visibility = S.Advanced ? Visibility.Visible : Visibility.Collapsed;
        _loading = false;
    }

    void ShowAppearance()
    {
        var value = Theme.Current;
        string scheme = value.Scheme == "custom" ? L.Text("Custom") : L.Text(ThemePalette.Schemes.First(s => s.Id == value.Scheme).Name);
        AppearanceSummary.Text = L.Text("{0} · {1} · {2}% text", scheme, L.Text(value.Mode), value.TextScale);
    }

    void ChangeAppearance(object sender, RoutedEventArgs e) =>
        new AppearanceDialog(Window.GetWindow(this), Theme.Current, value =>
        {
            S.Appearance = ThemePalette.Normalize(value);
            S.Save();
            Theme.Apply(S.Appearance);
        }).ShowDialog();

    void LanguageChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || _shared is null) return;
        S.Language = Russian.IsChecked == true ? "ru" : "en";
        S.Save();
        L.Current.SetLanguage(S.Language);
    }

    void WindowChoicesChanged(bool closeChoice)
    {
        if (_loading || _shared is null) return;
        S.CloseToTray = WindowChoices.CloseToTray;
        S.StopServicesOnExit = WindowChoices.StopServicesOnExit;
        S.CloseBehaviorConfirmed = closeChoice || S.CloseBehaviorConfirmed;
        S.Save();
    }

    void AlwaysAskChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || _shared is null) return;
        S.AlwaysAskOnClose = AlwaysAsk.IsChecked == true;
        S.CloseBehaviorConfirmed = true;
        S.Save();
    }

    void TrayChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || _shared is null) return;
        S.TrayWhileGameRuns = TrayWhileGame.IsChecked == true;
        S.Save();
    }

    void CheckAtStartChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || _shared is null) return;
        S.CheckForUpdates = CheckAtStart.IsChecked == true;
        S.Save();
    }

    void KeepUpToDateChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || _shared is null) return;
        S.KeepUpToDate = KeepUpToDate.IsChecked == true;
        S.Save();
    }

    /// <summary>The sidebar's switch moved: Simple and Advanced mode show different settings.</summary>
    internal void ShowMode()
    {
        if (_shared is not null) Show();
    }

    void CheckUpdates(object sender, RoutedEventArgs e) => CheckUpdatesRequested?.Invoke();

    void ProfileChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || _shared is null) return;
        S.UseLocalProfile = UseProfile.IsChecked == true;
        Repo.UseLocalProfile = S.UseLocalProfile;
        S.Save();
        _ = _shared.Workspace.RefreshAsync();
    }

    bool Busy()
    {
        if (!_shared.IsLiveRunning) return false;
        Dialog.Tell(Window.GetWindow(this), DialogKind.Info, L.Text("{0} is still running", _shared.BusyWith), L.Text("Wait for the current operation before changing checkout."));
        return true;
    }

    void FindCheckout(object sender, RoutedEventArgs e)
    {
        if (Busy()) return;
        var owner = Window.GetWindow(this);
        var dialog = new CheckoutDialog(owner, S.ServerPath);
        if (dialog.ShowDialog() == true) App.SwitchServer(owner, S, dialog.Chosen);
    }

    void ChangeCheckout(object sender, RoutedEventArgs e)
    {
        if (!Busy()) App.SwitchServer(Window.GetWindow(this), S);
    }

    void OpenData(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(SettingsStore.DataDirectory);
        Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { SettingsStore.DataDirectory }, UseShellExecute = false })?.Dispose();
    }
}
