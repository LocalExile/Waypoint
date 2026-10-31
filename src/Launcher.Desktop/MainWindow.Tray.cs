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
        WindowChoices.Load(settings.CloseToTray, settings.StopServicesOnExit);
        SidebarCloseSummary.Text = L.Text(settings.StopServicesOnExit ? "Exiting Waypoint stops this checkout’s services." : "Exiting Waypoint leaves services running.");
        CloseBehaviorState.Text = L.Text("Changes here are saved immediately. The minimize button still uses the taskbar.");
        ShowCloseDialogBox.Content = new TextBlock { Text = L.Text("Always ask when I close the window"), TextWrapping = TextWrapping.Wrap };
        ShowCloseDialogBox.IsChecked = settings.AlwaysAskOnClose;
        ClosePromptHint.Text = L.Text(settings.AlwaysAskOnClose
            ? "Choose each time. Remembering a choice in the dialog turns this off."
            : !settings.CloseBehaviorConfirmed ? "You’ll be asked once before a closing preference is saved."
            : "The selected action will run without a close dialog.");
        trayIcon?.RefreshLanguage();
        updatingClosePreferences = false;
    }

    bool SaveClosePreferences(bool closeToTray, bool confirmed, bool? stopServices = null, bool? alwaysAsk = null)
    {
        try
        {
            var next = settings with { CloseToTray = closeToTray, CloseBehaviorConfirmed = confirmed,
                StopServicesOnExit = stopServices ?? settings.StopServicesOnExit, AlwaysAskOnClose = alwaysAsk ?? settings.AlwaysAskOnClose };
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

    void WindowChoices_Changed(bool closeChoice)
    {
        if (!IsLoaded || updatingClosePreferences) return;
        SaveClosePreferences(WindowChoices.CloseToTray, closeChoice || settings.CloseBehaviorConfirmed, WindowChoices.StopServicesOnExit);
    }
    void ShowCloseDialog_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || updatingClosePreferences) return;
        SaveClosePreferences(settings.CloseToTray, true, alwaysAsk: ShowCloseDialogBox.IsChecked == true);
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
                    var dialog = new CloseBehaviorDialog(this, settings.StopServicesOnExit, settings.CloseToTray, settings.AlwaysAskOnClose);
                    if (dialog.ShowDialog() != true || dialog.Choice is not { } choice) return;
                    if (dialog.RememberChoice.IsChecked == true && !SaveClosePreferences(choice == WindowCloseAction.Hide, true, dialog.StopServices.IsChecked == true, false)) return;
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
