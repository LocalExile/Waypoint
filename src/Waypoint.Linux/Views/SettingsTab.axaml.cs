using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Waypoint;

/// <summary>The Settings section: the language, what exiting does, updates and the checkout. Every choice is saved as it is
/// made. Linux has no tray for Waypoint to close to, and its appearance is the one in App.axaml, so the Windows section's
/// tray choices and appearance editor are not here.</summary>
partial class SettingsTab : UserControl
{
    Shared _shared = null!;
    bool _loading;

    /// <summary>Check for updates: the window has the update card.</summary>
    public event Action? CheckUpdatesRequested;
    /// <summary>Developer Mode changed: the window shows the sections and the switch again.</summary>
    public event Action? DeveloperModeChanged;

    public SettingsTab() => InitializeComponent();

    Settings S => _shared.Settings;

    public void Attach(Shared shared)
    {
        _shared = shared;
        ServerFolder.Text = Environment.CurrentDirectory;
        ToolTip.SetTip(ServerFolder, Environment.CurrentDirectory);
        L.Current.PropertyChanged += (_, _) => Show();
        Show();
    }

    void Show()
    {
        _loading = true;
        ShowTexts();
        English.IsChecked = L.Current.Language != "ru";
        Russian.IsChecked = L.Current.Language == "ru";
        StopServices.IsChecked = S.StopServicesOnExit;
        LeaveServices.IsChecked = !S.StopServicesOnExit;
        ShowServicesHelp();
        AlwaysAsk.IsChecked = S.AlwaysAskOnClose;
        CheckAtStart.IsChecked = S.CheckForUpdates;
        KeepUpToDate.IsChecked = S.KeepUpToDate;
        UseProfile.IsChecked = S.UseLocalProfile;
        DeveloperMode.IsChecked = S.Developer;
        DeveloperNote.Text = L.Text("Adds a Simple | Advanced switch to the sidebar. Advanced replaces Play with Control, and adds Versions, performance and server timings.");
        KeepUpToDate.IsVisible = !(S.Advanced && !Repo.IsInstall);
        ToolTip.SetTip(KeepUpToDate, Repo.IsInstall ? L.Text("When Waypoint starts, look for a newer LocalExile release and offer to install it.")
            : L.Text("Play gets the newest LocalExile at most once a day, after backing up your saves. A copy with your own changes is left alone."));
        CheckoutGroup.IsVisible = S.Advanced && !Repo.IsInstall;
        _loading = false;
    }

    void ShowTexts()
    {
        LanguageHeading.Text = L.Text("Language");
        LanguageNote.Text = L.Text("This changes Waypoint's interface. Repository names, paths, commit hashes, in-game commands and raw logs keep their original text.");
        WindowHeading.Text = L.Text("Window behavior");
        ExitHeading.Text = L.Text("When Waypoint exits");
        LeaveServices.Content = L.Text("Leave the servers and the game running");
        StopServices.Content = L.Text("Stop the servers and the game before exiting");
        AlwaysAsk.Content = L.Text("Always ask when I close the window");
        UpdatesHeading.Text = L.Text("Updates");
        CheckUpdatesButton.Content = L.Text("Check for updates");
        ToolTip.SetTip(CheckUpdatesButton, L.Text("Look for a newer Waypoint release on GitHub"));
        CheckAtStart.Content = L.Text("Look for updates at start");
        ToolTip.SetTip(CheckAtStart, L.Text("Look for a newer Waypoint release whenever it starts (release builds)"));
        KeepUpToDate.Content = L.Text("Keep LocalExile up to date");
        DeveloperHeading.Text = L.Text("Developer");
        DeveloperMode.Content = L.Text("Developer Mode");
        CheckoutHeading.Text = L.Text("Server checkout");
        UseProfile.Content = L.Text("Use the checkout's local launch profile when it has one");
        FindButton.Content = L.Text("Find automatically…");
        ChangeButton.Content = L.Text("Change…");
        ToolTip.SetTip(ChangeButton, L.Text("Pick another LocalExile server checkout; Waypoint starts again on it"));
        OpenDataButton.Content = L.Text("Open Waypoint's data folder");
        ToolTip.SetTip(OpenDataButton, L.Text("Operation logs and save backups"));
    }

    void ShowServicesHelp() => ServicesHelp.Text = L.Text(S.StopServicesOnExit
        ? "Same as Stop all: the game, the walk-map recorder, the servers and the account service."
        : "The game and services stay open after Waypoint exits.");

    // A radio button says it was checked before its group unchecks the other, so these go by the sender.
    void LanguageChanged(object? sender, RoutedEventArgs e)
    {
        if (_loading || _shared is null || sender is not RadioButton { IsChecked: true }) return;
        S.Language = sender == Russian ? "ru" : "en";
        S.Save();
        L.Current.SetLanguage(S.Language);
    }

    void ServicesChanged(object? sender, RoutedEventArgs e)
    {
        if (_loading || _shared is null || sender is not RadioButton { IsChecked: true }) return;
        S.StopServicesOnExit = sender == StopServices;
        S.Save();
        ShowServicesHelp();
    }

    void AlwaysAskChanged(object? sender, RoutedEventArgs e)
    {
        if (_loading || _shared is null) return;
        S.AlwaysAskOnClose = AlwaysAsk.IsChecked == true;
        S.CloseBehaviorConfirmed = true;
        S.Save();
    }

    void CheckAtStartChanged(object? sender, RoutedEventArgs e)
    {
        if (_loading || _shared is null) return;
        S.CheckForUpdates = CheckAtStart.IsChecked == true;
        S.Save();
    }

    void KeepUpToDateChanged(object? sender, RoutedEventArgs e)
    {
        if (_loading || _shared is null) return;
        S.KeepUpToDate = KeepUpToDate.IsChecked == true;
        S.Save();
    }

    /// <summary>The sidebar's switch moved: Simple and Advanced mode show different settings.</summary>
    public void ShowMode()
    {
        if (_shared is not null) Show();
    }

    /// <summary>On, the sidebar gets its switch and Simple mode stays until it is used. Off, Waypoint goes back to Simple mode.</summary>
    void DeveloperModeClicked(object? sender, RoutedEventArgs e)
    {
        if (_loading || _shared is null) return;
        S.DeveloperMode = DeveloperMode.IsChecked == true;
        if (!S.Developer) S.AdvancedMode = false;
        S.Save();
        DeveloperModeChanged?.Invoke();
    }

    void CheckUpdates(object? sender, RoutedEventArgs e) => CheckUpdatesRequested?.Invoke();

    void ProfileChanged(object? sender, RoutedEventArgs e)
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
        Dialog.Tell(ViewTools.Owner(this), DialogKind.Info, L.Text("{0} is still running", _shared.BusyWith), L.Text("Wait for the current operation before changing checkout."));
        return true;
    }

    async void FindCheckout(object? sender, RoutedEventArgs e)
    {
        if (Busy() || ViewTools.Owner(this) is not { } owner) return;
        var dialog = new CheckoutDialog(owner, S.ServerPath);
        if (dialog.ShowDialog() == true) await ServerChoice.SwitchAsync(owner, S, dialog.Chosen);
    }

    async void ChangeCheckout(object? sender, RoutedEventArgs e)
    {
        if (!Busy() && ViewTools.Owner(this) is { } owner) await ServerChoice.SwitchAsync(owner, S);
    }

    void OpenData(object? sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(SettingsStore.DataDirectory);
        ViewTools.Open(SettingsStore.DataDirectory);
    }
}
