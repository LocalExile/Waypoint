using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Launcher.Core;

namespace Launcher.Desktop;

public partial class MainWindow
{
    TrayIcon? trayIcon;
    bool explicitExit, decidingClose, preparingExit, exitServicesPrepared, updatingClosePreferences;
    bool? exitStopServicesOverride;

    void UpdateClosePreferences()
    {
        updatingClosePreferences = true;
        WindowBehaviorTitle.Text = L.Text("Window behavior");
        CloseToTrayBox.Content = new TextBlock { Text = L.Text("Close window to the notification area (system tray)"), TextWrapping = TextWrapping.Wrap };
        CloseToTrayBox.IsChecked = settings.CloseToTray;
        StopServicesOnExitBox.Content = new TextBlock { Text = L.Text("Stop this checkout’s services when exiting Waypoint"), TextWrapping = TextWrapping.Wrap };
        StopServicesOnExitBox.IsChecked = settings.StopServicesOnExit;
        StopServicesOnExitHelp.Text = L.Text("Only applies to a full exit, including Exit Waypoint in the tray menu. Hiding to tray keeps services running. If the game is open, you can cancel or leave everything running.");
        SidebarCloseSummary.Text = L.Text(settings.StopServicesOnExit ? "Exiting Waypoint stops this checkout’s services." : "Exiting Waypoint leaves services running.");
        CloseBehaviorHelp.Text = L.Text("The minimize button still uses the taskbar. When hidden, click the Waypoint icon beside the clock to return. You may find it under the hidden-icons arrow.");
        CloseBehaviorState.Text = L.Text(!settings.CloseBehaviorConfirmed ? "Waypoint will ask what to do the next time you close the window."
            : settings.CloseToTray ? "Closing the window keeps Waypoint running. Use Exit Waypoint in the tray menu to quit."
            : "Closing the window exits Waypoint completely.");
        AskCloseAgainButton.Content = L.Text("Ask again next time");
        AskCloseAgainButton.IsEnabled = settings.CloseBehaviorConfirmed;
        trayIcon?.RefreshLanguage();
        updatingClosePreferences = false;
    }

    bool SaveClosePreferences(bool closeToTray, bool confirmed, bool? stopServices = null)
    {
        try
        {
            var next = settings with { CloseToTray = closeToTray, CloseBehaviorConfirmed = confirmed, StopServicesOnExit = stopServices ?? settings.StopServicesOnExit };
            SettingsStore.Save(next); settings = next;
            UpdateClosePreferences();
            StatusLabel.Text = L.Text("Window behavior saved.");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            UpdateClosePreferences();
            StatusLabel.Text = L.Text("Could not save preferences: ") + ex.Message;
            SetFeedback(true); return false;
        }
    }

    void CloseToTray_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || updatingClosePreferences) return;
        SaveClosePreferences(CloseToTrayBox.IsChecked == true, true);
    }
    void AskCloseAgain_Click(object sender, RoutedEventArgs e) => SaveClosePreferences(settings.CloseToTray, false);
    void StopServicesOnExit_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || updatingClosePreferences) return;
        SaveClosePreferences(settings.CloseToTray, settings.CloseBehaviorConfirmed, StopServicesOnExitBox.IsChecked == true);
    }

    void HandleWindowClosing(CancelEventArgs e)
    {
        // Unshown test/failed-startup windows should never open a first-use dialog.
        bool sessionEnding = Application.Current is App { IsSessionEnding: true };
        var action = WindowClosePolicy.Decide(settings, busy, explicitExit || !IsLoaded, sessionEnding);
        explicitExit = false;
        if ((decidingClose || preparingExit) && !sessionEnding) { e.Cancel = true; return; }
        if (action == WindowCloseAction.Ask)
        {
            e.Cancel = true; decidingClose = true;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    if (closing) return;
                    var dialog = new CloseBehaviorDialog(this, settings.StopServicesOnExit);
                    if (dialog.ShowDialog() != true || dialog.Choice is not { } choice) return;
                    if (dialog.RememberChoice.IsChecked == true && !SaveClosePreferences(choice == WindowCloseAction.Hide, true, dialog.StopServices.IsChecked == true)) return;
                    if (choice == WindowCloseAction.Hide) HideToTray();
                    else { exitStopServicesOverride = dialog.StopServices.IsChecked == true; explicitExit = true; Dispatcher.BeginInvoke(new Action(Close)); }
                }
                finally { decidingClose = false; }
            }));
            return;
        }
        if (action == WindowCloseAction.Hide)
        {
            e.Cancel = true;
            Dispatcher.BeginInvoke(new Action(HideToTray));
            return;
        }
        if (action == WindowCloseAction.Wait)
        {
            exitServicesPrepared = false;
            exitStopServicesOverride = null;
            e.Cancel = true; Dispatcher.BeginInvoke(new Action(RestoreFromTray));
            StatusLabel.Text = L.Text("Wait for the current build or runtime operation to finish before closing.");
            return;
        }
        bool stopServices = exitStopServicesOverride ?? settings.StopServicesOnExit;
        exitStopServicesOverride = null;
        if (!sessionEnding && IsLoaded && stopServices && !exitServicesPrepared && !string.IsNullOrWhiteSpace(settings.RepositoryPath))
        {
            e.Cancel = true;
            preparingExit = true;
            Dispatcher.BeginInvoke(new Action(async () => await PrepareExitAsync()));
            return;
        }
        closing = true;
        trayIcon?.Dispose(); trayIcon = null;
        discoveryCancellation.Cancel();
        timer.Stop(); operationTimer.Stop();
    }

    void HideToTray()
    {
        if (closing) return;
        try
        {
            trayIcon ??= new TrayIcon(this, RestoreFromTray, ExitFromTray, TrayUnavailable);
            // Only hide after Windows confirms there is a usable return path.
            trayIcon.Show();
            Hide();
            timer.Stop();
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException)
        {
            RestoreFromTray();
            StatusLabel.Text = L.Text("Could not minimize to tray: ") + ex.Message; SetFeedback(true);
        }
    }

    public void RestoreFromTray()
    {
        if (closing) return;
        bool wasHidden = !IsVisible;
        if (wasHidden) Show();
        trayIcon?.Hide();
        WindowActivation.Focus(this);
        if (wasHidden) { timer.Start(); if (!busy) _ = RefreshAsync(); }
    }

    void ExitFromTray() { explicitExit = true; Close(); }

    async Task PrepareExitAsync()
    {
        bool shouldExit = false;
        try
        {
            RestoreFromTray();
            bool success = await OperateAsync("Stop", workflow: async execute =>
            {
                var prepared = await ServiceExitWorkflow.PrepareAsync(execute, () =>
                    Task.FromResult(new ExitWithGameDialog(this, settings.RepositoryPath).ShowDialog() == true));
                shouldExit = prepared.ShouldExit;
                return prepared.Operation;
            });
            if (!success || !shouldExit) return;
            exitServicesPrepared = true;
            explicitExit = true;
            _ = Dispatcher.BeginInvoke(new Action(Close));
        }
        finally { preparingExit = false; }
    }
    void TrayUnavailable()
    {
        RestoreFromTray();
        StatusLabel.Text = L.Text("The notification area is unavailable. Waypoint has reopened so you can still use it.");
        SetFeedback(true);
    }

    internal void DisposeTrayIcon() { trayIcon?.Dispose(); trayIcon = null; }
}
