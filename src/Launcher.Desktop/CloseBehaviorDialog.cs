using System.Windows;
using System.Windows.Controls;
using Launcher.Core;

namespace Launcher.Desktop;

public sealed class CloseBehaviorDialog : DialogWindow
{
    public WindowCloseAction? Choice { get; private set; }
    public CheckBox RememberChoice { get; } = new() { IsChecked = true };
    public RadioButton StopServices { get; } = new() { GroupName = "ExitServices" };

    public CloseBehaviorDialog(Window owner, bool stopServicesOnExit = false)
    {
        DialogLayout.Configure(this, owner, L.Text("Close Waypoint"), 640);
        var body = new StackPanel { Margin = new Thickness(24) };
        body.Children.Add(new TextBlock { Text = L.Text("Close Waypoint or keep it running?"), FontSize = ThemeResources.Font(22), FontWeight = FontWeights.SemiBold });
        body.Children.Add(new TextBlock { Text = L.Text("Minimize to tray keeps Waypoint running beside the clock. Click its icon, or open Waypoint again, to return."), Margin = new Thickness(0, 14, 0, 12) });
        body.Children.Add(new TextBlock { Text = L.Text("On a full exit"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 4) });
        var keepServices = new RadioButton { GroupName = "ExitServices", IsChecked = !stopServicesOnExit,
            Content = new TextBlock { Text = L.Text("Keep services running"), TextWrapping = TextWrapping.Wrap } };
        keepServices.SetResourceReference(StyleProperty, "ChoiceRadio");
        body.Children.Add(keepServices);
        StopServices.Content = new TextBlock { Text = L.Text("Stop services on exit"), TextWrapping = TextWrapping.Wrap };
        StopServices.IsChecked = stopServicesOnExit;
        StopServices.SetResourceReference(StyleProperty, "ChoiceRadio");
        body.Children.Add(StopServices);
        var exitHelp = new TextBlock { Margin = new Thickness(0, 0, 0, 16) };
        void UpdateExitHelp() => exitHelp.Text = L.Text(StopServices.IsChecked == true
            ? "Exit stops this checkout’s services before closing Waypoint. If the game is open, you can cancel or leave everything running."
            : "Exit closes Waypoint completely. Servers and the game keep running either way.");
        StopServices.Checked += (_, _) => UpdateExitHelp();
        StopServices.Unchecked += (_, _) => UpdateExitHelp();
        UpdateExitHelp();
        body.Children.Add(exitHelp);
        var trayHelp = new TextBlock { Text = L.Text("Minimizing to tray always keeps services running. This choice applies to a full exit."), Margin = new Thickness(0, 0, 0, 12) };
        trayHelp.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        body.Children.Add(trayHelp);
        RememberChoice.Content = new TextBlock { Text = L.Text("Remember my choices"), TextWrapping = TextWrapping.Wrap };
        body.Children.Add(RememberChoice);
        var help = new TextBlock { Text = L.Text("You can change this in Settings → Window behavior."), Margin = new Thickness(0, 4, 0, 18) };
        help.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        body.Children.Add(help);
        var buttons = new WrapPanel();
        void Add(string label, WindowCloseAction action)
        {
            var button = new Button { Content = L.Text(label) };
            button.Click += (_, _) => { Choice = action; DialogResult = true; };
            buttons.Children.Add(button);
        }
        Add("Minimize to tray", WindowCloseAction.Hide);
        Add("Exit Waypoint", WindowCloseAction.Exit);
        var cancel = new Button { Content = L.Text("Cancel"), IsCancel = true, IsDefault = true };
        buttons.Children.Add(cancel);
        Content = DialogLayout.WithActions(body, buttons);
        Loaded += (_, _) => cancel.Focus();
    }
}

public sealed class ExitWithGameDialog : DialogWindow
{
    public ExitWithGameDialog(Window owner, string repository)
    {
        DialogLayout.Configure(this, owner, L.Text("The game is still running"), 640);
        var body = new StackPanel { Margin = new Thickness(24) };
        body.Children.Add(new TextBlock { Text = L.Text("Close the game before stopping services"), FontSize = ThemeResources.Font(22), FontWeight = FontWeights.SemiBold });
        body.Children.Add(new TextBlock { Text = L.Text("Waypoint will not stop services underneath an open game. Cancel to close the game first, or exit Waypoint and leave the game and services running this time."), Margin = new Thickness(0, 14, 0, 12) });
        body.Children.Add(new TextBlock { Text = repository, Margin = new Thickness(0, 0, 0, 18) });
        var buttons = new WrapPanel();
        var leave = new Button { Content = L.Text("Exit and leave running") };
        leave.Click += (_, _) => DialogResult = true;
        var cancel = new Button { Content = L.Text("Cancel"), IsCancel = true, IsDefault = true };
        buttons.Children.Add(leave); buttons.Children.Add(cancel);
        Content = DialogLayout.WithActions(body, buttons);
        Loaded += (_, _) => cancel.Focus();
    }
}
