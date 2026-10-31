using System.Windows;
using System.Windows.Controls;
using Launcher.Core;

namespace Launcher.Desktop;

static class DialogLayout
{
    public static void Configure(DialogWindow dialog, Window owner, string title, int width = 680)
    {
        dialog.Owner = owner;
        dialog.Language = owner.Language;
        dialog.Title = title;
        dialog.Width = width;
        dialog.SizeToContent = SizeToContent.Height;
        dialog.MaxHeight = Math.Min(650, SystemParameters.WorkArea.Height - 40);
        dialog.MinWidth = 420;
        dialog.MinHeight = 220;
        dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        dialog.ResizeMode = ResizeMode.CanResize;
    }

    public static UIElement WithActions(Panel body, UIElement actions)
    {
        body.Children.Remove(actions);
        var layout = new DockPanel { Margin = body.Margin };
        body.Margin = new Thickness(0);
        DockPanel.SetDock(actions, Dock.Bottom);
        layout.Children.Add(actions);
        layout.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        return layout;
    }
}

/// <summary>Localized confirmation with Cancel as the safe keyboard default.</summary>
public sealed class ConfirmationDialog : DialogWindow
{
    public ConfirmationDialog(Window owner, string message, string title)
    {
        DialogLayout.Configure(this, owner, title);
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = title, FontSize = ThemeResources.Font(22) });
        panel.Children.Add(new TextBlock { Text = message, Margin = new Thickness(0, 16, 0, 20) });
        var buttons = new WrapPanel();
        var proceed = new Button { Content = L.Text("Continue") };
        proceed.Click += (_, _) => DialogResult = true;
        var cancel = new Button { Content = L.Text("Cancel"), IsCancel = true, IsDefault = true };
        buttons.Children.Add(proceed);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        Content = DialogLayout.WithActions(panel, buttons);
        Loaded += (_, _) => cancel.Focus();
    }
}

public sealed class SetupInstructionsDialog : DialogWindow
{
    public SetupInstructionsDialog(Window owner, SetupStep step, string root)
    {
        DialogLayout.Configure(this, owner, step.DisplayName);
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = step.DisplayName, FontSize = ThemeResources.Font(22) });
        panel.Children.Add(new TextBlock { Text = step.Instructions, Margin = new Thickness(0, 16, 0, 16) });
        panel.Children.Add(new TextBlock { Text = L.Text("Selected checkout: ") + root, Margin = new Thickness(0, 0, 0, 18) });
        var buttons = new WrapPanel();
        var proceed = new Button { Content = step.Action is "Guide" or "ClientFolder" ? L.Text("Open setup guide") : step.ActionLabel, IsDefault = true };
        proceed.Click += (_, _) => DialogResult = true;
        var close = new Button { Content = L.Text("Close"), IsCancel = true };
        buttons.Children.Add(proceed); buttons.Children.Add(close); panel.Children.Add(buttons);
        Content = DialogLayout.WithActions(panel, buttons);
    }
}

public sealed class CreateAccountDialog : DialogWindow
{
    public AccountRequest? Request { get; private set; }
    public CreateAccountDialog(Window owner, string database)
    {
        DialogLayout.Configure(this, owner, L.Text("Create a LocalExile account"));
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = L.Text("Create a local game account"), FontSize = ThemeResources.Font(22) });
        panel.Children.Add(new TextBlock { Text = L.Text("Close the game and stop services first. This uses a temporary local account service and the database below. Sign in inside the game after creation, then create your character there."), Margin = new Thickness(0, 12, 0, 12) });
        panel.Children.Add(new TextBlock { Text = database, Margin = new Thickness(0, 0, 0, 16) });
        panel.Children.Add(new TextBlock { Text = L.Text("Account name (3–32 letters, digits, _ or -)") });
        var name = new TextBox { Margin = new Thickness(0, 6, 0, 14) };
        panel.Children.Add(name);
        panel.Children.Add(new TextBlock { Text = L.Text("Password (at least 4 characters; not saved by the launcher)") });
        var password = new PasswordBox { Margin = new Thickness(0, 6, 0, 14) };
        panel.Children.Add(password);
        var error = new TextBlock { Margin = new Thickness(0, 0, 0, 12) };
        panel.Children.Add(error);
        var buttons = new WrapPanel();
        var create = new Button { Content = L.Text("Create account"), IsDefault = true };
        create.Click += (_, _) =>
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(name.Text, @"\A[A-Za-z0-9_-]{3,32}\z") || password.Password.Length is < 4 or > 1024)
            { error.Text = L.Text("Check the account name and password requirements above."); return; }
            Request = new(name.Text, password.Password);
            password.Clear();
            DialogResult = true;
        };
        buttons.Children.Add(create); buttons.Children.Add(new Button { Content = L.Text("Cancel"), IsCancel = true });
        panel.Children.Add(buttons); Content = DialogLayout.WithActions(panel, buttons);
        Loaded += (_, _) => name.Focus();
    }
}

public sealed class DiagnosticPreviewDialog : DialogWindow
{
    public DiagnosticPreviewDialog(Window owner, Func<string[], string> create)
    {
        DialogLayout.Configure(this, owner, L.Text("Diagnostic report preview"), 820);
        Height = 620; SizeToContent = SizeToContent.Manual;
        var grid = new Grid { Margin = new Thickness(20) };
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        grid.Children.Add(new TextBlock { Text = L.Text("Review the redacted text before copying. Redaction recognizes common patterns; check for any private details it missed.") });
        var namesPanel = new StackPanel { Margin = new Thickness(0, 10, 0, 10) };
        namesPanel.Children.Add(new TextBlock { Text = L.Text("Additional account / character names to remove (comma separated)"), Margin = new Thickness(0, 0, 0, 6) });
        var names = new TextBox { ToolTip = L.Text("Optional account/character names to remove, separated by commas") };
        System.Windows.Automation.AutomationProperties.SetName(names, L.Text("Additional names to remove (comma separated)"));
        namesPanel.Children.Add(names);
        Grid.SetRow(namesPanel, 1); grid.Children.Add(namesPanel);
        var preview = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"), FontSize = ThemeResources.Font(12),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
        preview.Text = create([]);
        names.TextChanged += (_, _) => preview.Text = create(names.Text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
        Grid.SetRow(preview, 2); grid.Children.Add(preview);
        var buttons = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
        var copy = new Button { Content = L.Text("Copy reviewed report") };
        copy.Click += (_, _) =>
        {
            try { Clipboard.SetText(preview.Text); copy.Content = L.Text("Copied"); }
            catch (System.Runtime.InteropServices.COMException) { copy.Content = L.Text("Clipboard busy — try again"); }
        };
        buttons.Children.Add(copy); buttons.Children.Add(new Button { Content = L.Text("Close"), IsCancel = true });
        Grid.SetRow(buttons, 3); grid.Children.Add(buttons); Content = grid;
    }
}
