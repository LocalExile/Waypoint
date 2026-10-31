using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation.Peers;
using System.Windows.Media;
using Launcher.Core;
using Launcher.Desktop;

static class TrayChecks
{
    public static void Run(App app, MainWindow owner, Action<bool, string> check)
    {
        var settingsField = typeof(MainWindow).GetField("settings", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var original = (LauncherSettings)settingsField.GetValue(owner)!;
        var apply = typeof(MainWindow).GetMethod("ApplyLanguage", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        try
        {
            foreach (string language in new[] { "en", "ru" })
            {
                settingsField.SetValue(owner, original with { Language = language, CloseToTray = false, CloseBehaviorConfirmed = false });
                apply.Invoke(owner, null);
                var preferences = (WindowBehaviorChoices)owner.FindName("WindowChoices");
                check(!preferences.CloseToTray && new RadioButtonAutomationPeer(preferences.KeepInTray).GetName() == L.Text("Keep Waypoint running in the tray"), "Tray opt-in starts off with an explicit, accessible alternative to Exit.");
                check(((CheckBox)owner.FindName("ShowCloseDialogBox")).IsChecked == false, "Always ask is a separate opt-in from the first-use prompt.");
                var dialog = new CloseBehaviorDialog(owner);
                var layout = (Grid)dialog.Content;
                var buttons = ((StackPanel)layout.Children[1]).Children.OfType<WrapPanel>().Single().Children.OfType<Button>().ToArray();
                check(dialog.Owner == owner && dialog.WindowStyle == WindowStyle.None && !dialog.ShowInTaskbar, "First-close prompt uses the existing owned custom window frame.");
                check(dialog.Choice is null && dialog.RememberChoice.IsChecked == true, "Opening the prompt does not select an action; remembering the eventual choice defaults on.");
                check(dialog.StopServices.IsChecked == false && new RadioButtonAutomationPeer(dialog.StopServices).GetName() == L.Text("Stop services before exiting"), "First close offers the separate service-exit choice with leave-running as the accessible default.");
                var options = ((Panel)dialog.StopServices.Parent).Children.OfType<RadioButton>().ToArray();
                check(options.Length == 2 && options[0].IsChecked == true && new RadioButtonAutomationPeer(options[0]).GetName() == L.Text("Leave LocalExile services running"), "The service prompt presents an explicit either/or choice, initially keeping services running.");
                check(buttons.Length == 2 && buttons.Single(b => b.IsDefault).IsCancel, "One explicit action and safe Cancel replace competing close buttons.");
                check((string)dialog.ApplyChoice.Content == L.Text("Exit Waypoint") && !dialog.Choices.CloseToTray, "The action describes the default full exit.");
                foreach (string mode in new[] { "Light", "Dark" })
                {
                    app.ApplyAppearance(new(Mode: mode, Font: "OpenDyslexic", TextScale: 150, StrongFocus: true));
                    layout.Measure(new Size(580, 520)); layout.Arrange(new Rect(0, 0, 580, 520)); layout.UpdateLayout();
                    check(layout.Children.OfType<ScrollViewer>().Single().ActualHeight > 80 && buttons.All(b => b.ActualHeight >= 36), "Large reading text leaves a scrollable explanation and reachable close actions.");
                }
                dialog.StopServices.IsChecked = true;
                check(options[0].IsChecked == false && dialog.StopServices.IsChecked == true, "Choosing service shutdown deselects keep-running instead of allowing conflicting choices.");
                check((string)dialog.ApplyChoice.Content == L.Text("Stop services and exit"), "The action names service shutdown before exit.");
                dialog.Choices.KeepInTray.IsChecked = true;
                check(!dialog.Choices.ShowsServiceChoices && (string)dialog.ApplyChoice.Content == L.Text("Hide to tray"), "Tray mode hides irrelevant exit choices and labels the single action as Hide.");
                dialog.Choices.ExitApplication.IsChecked = true;
                check(dialog.Choices.ShowsServiceChoices && dialog.Choices.StopServicesOnExit, "Returning to Exit preserves the pending service choice without applying it.");
                options[0].IsChecked = true;
                check(dialog.StopServices.IsChecked == false, "Either service choice can be selected again before committing a close action.");
                app.ApplyContrast(true);
                check(ReferenceEquals(dialog.Background, SystemColors.WindowBrush), "First-close prompt honors Windows High Contrast.");
                app.ApplyContrast(false);
                dialog.Close();
                var stopDialog = new CloseBehaviorDialog(owner, true, true);
                check(stopDialog.StopServices.IsChecked == true && stopDialog.Choices.CloseToTray && !stopDialog.Choices.ShowsServiceChoices, "Re-asking preserves both preferences and shows only the relevant choices.");
                stopDialog.Close();
                var alwaysDialog = new CloseBehaviorDialog(owner, alwaysAsk: true);
                check(alwaysDialog.RememberChoice.IsChecked == false, "Always ask opens with Remember off so a one-time action preserves repeated prompting.");
                alwaysDialog.Close();
                int changes = 0; bool changedWindow = false;
                var independent = new WindowBehaviorChoices();
                independent.ChoiceChanged += windowChoice => { changes++; changedWindow = windowChoice; };
                independent.Load(true, true);
                check(independent.ShowsServiceChoices && changes == 0, "Rendering Settings or changing language leaves full-exit preferences available without saving.");
                independent.ExitApplication.IsChecked = true;
                check(changes == 1 && changedWindow && independent.StopServicesOnExit, "Changing close mode raises one event without changing service behavior.");
                independent.LeaveServices.IsChecked = true;
                check(changes == 2 && !changedWindow, "A service-only change remains separate from confirming the first-close choice.");
                var gameDialog = new ExitWithGameDialog(owner, @"C:\Disposable checkout");
                var gameButtons = ((WrapPanel)((DockPanel)gameDialog.Content).Children[0]).Children.OfType<Button>().ToArray();
                check(gameButtons.Length == 2 && gameButtons.Single(b => b.IsDefault).IsCancel && (string)gameButtons[0].Content == L.Text("Exit and leave running"), "Open-game exit prompt defaults to Cancel and offers only the explicit leave-running alternative.");
                gameDialog.Close();
            }
        }
        finally
        {
            settingsField.SetValue(owner, original); apply.Invoke(owner, null); app.ApplyAppearance(new());
        }
    }
}
