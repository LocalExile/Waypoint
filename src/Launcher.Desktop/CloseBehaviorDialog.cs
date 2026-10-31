using System.Windows;
using System.Windows.Controls;
using Launcher.Core;

namespace Launcher.Desktop;

public sealed class CloseBehaviorDialog : DialogWindow
{
    public WindowCloseAction? Choice { get; private set; }
    public CheckBox RememberChoice { get; } = new() { IsChecked = true };
    public WindowBehaviorChoices Choices { get; } = new() { PromptMode = true };
    public RadioButton StopServices => Choices.StopServices;
    public Button ApplyChoice { get; } = new();

    public CloseBehaviorDialog(Window owner, bool stopServicesOnExit = false, bool closeToTray = false, bool alwaysAsk = false)
    {
        DialogLayout.Configure(this, owner, L.Text("Close Waypoint"), 640);
        var body = new StackPanel { Margin = new Thickness(24) };
        body.Children.Add(new TextBlock { Text = L.Text("What should closing the window do?"), FontSize = ThemeResources.Font(22), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 16) });
        Choices.Load(closeToTray, stopServicesOnExit);
        RememberChoice.IsChecked = !alwaysAsk;
        body.Children.Add(Choices);
        var footer = new StackPanel { Margin = new Thickness(0, 20, 0, 0) };
        RememberChoice.Content = new TextBlock { Text = L.Text("Remember this for next time"), TextWrapping = TextWrapping.Wrap };
        footer.Children.Add(RememberChoice);
        var help = new TextBlock { Text = L.Text("You can change this in Settings → Window behavior."), Margin = new Thickness(0, 4, 0, 18) };
        help.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        footer.Children.Add(help);
        var buttons = new WrapPanel();
        ApplyChoice.Content = Choices.ActionLabel;
        Choices.ChoiceChanged += _ => ApplyChoice.Content = Choices.ActionLabel;
        ApplyChoice.Click += (_, _) => { Choice = Choices.CloseToTray ? WindowCloseAction.Hide : WindowCloseAction.Exit; DialogResult = true; };
        buttons.Children.Add(ApplyChoice);
        var cancel = new Button { Content = L.Text("Cancel"), IsCancel = true, IsDefault = true };
        buttons.Children.Add(cancel);
        footer.Children.Add(buttons);
        var layout = new Grid { Margin = body.Margin };
        body.Margin = new Thickness(0);
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        Grid.SetRow(footer, 1); layout.Children.Add(footer);
        Content = layout;
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
