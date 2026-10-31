using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace Waypoint;

/// <summary>Shared wording and choice order for the close dialog and saved window preferences.</summary>
public sealed class WindowBehaviorChoices : StackPanel
{
    readonly TextBlock windowHeading = Heading(), exitHeading = Heading();
    readonly TextBlock trayHelp = Help(), exitHelp = Help(), servicesHelp = Help(), trayExitHelp = Help();
    readonly StackPanel serviceChoices = new() { Margin = new Thickness(27, 12, 0, 0) };
    bool updating, promptMode;
    public RadioButton KeepInTray { get; } = Option();
    public RadioButton ExitApplication { get; } = Option();
    public RadioButton LeaveServices { get; } = Option();
    public RadioButton StopServices { get; } = Option();
    public event Action<bool>? ChoiceChanged;
    public bool CloseToTray => KeepInTray.IsChecked == true;
    public bool StopServicesOnExit => StopServices.IsChecked == true;
    public bool ShowsServiceChoices => serviceChoices.Visibility == Visibility.Visible;
    public bool PromptMode { get => promptMode; set { promptMode = value; RefreshState(); } }
    public string ActionLabel => L.Text(CloseToTray ? "Hide to tray" : StopServicesOnExit ? "Stop services and exit" : "Exit Waypoint");

    public WindowBehaviorChoices()
    {
        string identity = Guid.NewGuid().ToString("N");
        KeepInTray.GroupName = ExitApplication.GroupName = "WindowClose" + identity;
        LeaveServices.GroupName = StopServices.GroupName = "ServiceExit" + identity;
        Children.Add(windowHeading);
        Children.Add(KeepInTray); Children.Add(trayHelp);
        Children.Add(ExitApplication); Children.Add(exitHelp);
        serviceChoices.Children.Add(exitHeading);
        serviceChoices.Children.Add(LeaveServices); serviceChoices.Children.Add(StopServices);
        serviceChoices.Children.Add(servicesHelp); serviceChoices.Children.Add(trayExitHelp);
        Children.Add(serviceChoices);
        KeepInTray.Checked += (_, _) => Changed(true);
        ExitApplication.Checked += (_, _) => Changed(true);
        LeaveServices.Checked += (_, _) => Changed(false);
        StopServices.Checked += (_, _) => Changed(false);
        Load(false, false);
    }

    public void Load(bool closeToTray, bool stopServices)
    {
        updating = true;
        windowHeading.Text = L.Text("When I close the window");
        Label(KeepInTray, "Keep Waypoint running in the tray");
        trayHelp.Text = L.Text("Hide the window beside the clock. Waypoint, services and the game keep running.");
        Label(ExitApplication, "Exit Waypoint");
        exitHelp.Text = L.Text("Close Waypoint completely.");
        exitHeading.Text = L.Text("When Waypoint exits");
        Label(LeaveServices, "Leave the servers and the game running");
        Label(StopServices, "Stop the servers and the game before exiting");
        trayExitHelp.Text = L.Text("Also applies to Exit Waypoint from the tray menu.");
        KeepInTray.IsChecked = closeToTray;
        ExitApplication.IsChecked = !closeToTray;
        StopServices.IsChecked = stopServices;
        LeaveServices.IsChecked = !stopServices;
        updating = false;
        RefreshState();
    }

    void Changed(bool windowChoice)
    {
        if (updating) return;
        RefreshState();
        ChoiceChanged?.Invoke(windowChoice);
    }
    void RefreshState()
    {
        windowHeading.Visibility = PromptMode ? Visibility.Collapsed : Visibility.Visible;
        exitHelp.Visibility = PromptMode ? Visibility.Collapsed : Visibility.Visible;
        serviceChoices.Visibility = PromptMode && CloseToTray ? Visibility.Collapsed : Visibility.Visible;
        serviceChoices.Margin = new Thickness(PromptMode ? 27 : 0, 16, 0, 0);
        trayExitHelp.Visibility = PromptMode ? Visibility.Collapsed : Visibility.Visible;
        servicesHelp.Text = L.Text(StopServicesOnExit
            ? "Same as Stop all: the game, the walk-map recorder, the servers and the account service."
            : "The game and services stay open after Waypoint exits.");
    }
    static RadioButton Option()
    {
        var option = new RadioButton(); option.SetResourceReference(StyleProperty, "ChoiceRadio"); return option;
    }
    static void Label(RadioButton option, string text)
    {
        string label = L.Text(text);
        option.Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetName(option, label);
    }
    static TextBlock Heading() => new() { FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) };
    static TextBlock Help()
    {
        var text = new TextBlock { Margin = new Thickness(27, 0, 0, 8) };
        text.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); return text;
    }
}
