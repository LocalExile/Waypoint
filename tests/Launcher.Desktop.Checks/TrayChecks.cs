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
                var toggle = (CheckBox)owner.FindName("CloseToTrayBox");
                check(toggle.IsChecked == false && !string.IsNullOrWhiteSpace(new CheckBoxAutomationPeer(toggle).GetName()), "Tray opt-in starts off with a readable accessible name.");
                check(!((Button)owner.FindName("AskCloseAgainButton")).IsEnabled, "First-use Settings state does not suggest a choice has already been saved.");
                var dialog = new CloseBehaviorDialog(owner);
                var layout = (DockPanel)dialog.Content;
                var buttons = ((WrapPanel)layout.Children[0]).Children.OfType<Button>().ToArray();
                check(dialog.Owner == owner && dialog.WindowStyle == WindowStyle.None && !dialog.ShowInTaskbar, "First-close prompt uses the existing owned custom window frame.");
                check(dialog.Choice is null && dialog.RememberChoice.IsChecked == true, "Opening the prompt does not select an action; remembering the eventual choice defaults on.");
                check(dialog.StopServices.IsChecked == false && new RadioButtonAutomationPeer(dialog.StopServices).GetName() == L.Text("Stop services on exit"), "First close offers the separate service-exit choice with leave-running as the accessible default.");
                var options = ((Panel)dialog.StopServices.Parent).Children.OfType<RadioButton>().ToArray();
                check(options.Length == 2 && options[0].IsChecked == true && new RadioButtonAutomationPeer(options[0]).GetName() == L.Text("Keep services running"), "The service prompt presents an explicit either/or choice, initially keeping services running.");
                check(buttons.Length == 3 && buttons.Single(b => b.IsDefault).IsCancel, "Cancel is the safe Enter/Escape action; tray and exit remain explicit choices.");
                check((string)buttons[0].Content == L.Text("Minimize to tray") && (string)buttons[1].Content == L.Text("Exit Waypoint"), "Close actions translate without swapping their meanings.");
                foreach (string mode in new[] { "Light", "Dark" })
                {
                    app.ApplyAppearance(new(Mode: mode, Font: "OpenDyslexic", TextScale: 150, StrongFocus: true));
                    layout.Measure(new Size(580, 520)); layout.Arrange(new Rect(0, 0, 580, 520)); layout.UpdateLayout();
                    check(layout.Children.OfType<ScrollViewer>().Single().ActualHeight > 80 && buttons.All(b => b.ActualHeight >= 36), "Large reading text leaves a scrollable explanation and reachable close actions.");
                }
                dialog.StopServices.IsChecked = true;
                check(options[0].IsChecked == false && dialog.StopServices.IsChecked == true, "Choosing service shutdown deselects keep-running instead of allowing conflicting choices.");
                options[0].IsChecked = true;
                check(dialog.StopServices.IsChecked == false, "Either service choice can be selected again before committing a close action.");
                app.ApplyContrast(true);
                check(ReferenceEquals(dialog.Background, SystemColors.WindowBrush), "First-close prompt honors Windows High Contrast.");
                app.ApplyContrast(false);
                dialog.Close();
                var stopDialog = new CloseBehaviorDialog(owner, true);
                check(stopDialog.StopServices.IsChecked == true, "Re-asking preserves an already selected service-exit preference.");
                stopDialog.Close();
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
