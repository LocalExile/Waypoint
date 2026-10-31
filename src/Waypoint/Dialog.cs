using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MaterialDesignThemes.Wpf;

namespace Waypoint;

enum DialogKind { Info, Question, Warning, Error }

/// <summary>
/// The launcher's message boxes, in its own look and theme instead of Windows' MessageBox. Buttons are named after what
/// they do ("Stop and switch", not OK); the first is the main one, the last one cancels (Esc, or closing the box).
/// </summary>
static class Dialog
{
    /// <summary>The chosen button's index, or -1 when the box was closed without one.</summary>
    /// <param name="defaultButton">The button Enter presses: the main one, unless a risky question makes it Cancel.</param>
    public static int Show(Window? owner, DialogKind kind, string title, string message, string? details, string[] buttons,
        int defaultButton = 0)
    {
        var box = new DialogWindow { Title = title };
        Theme.Dress(box);
        if (owner is { IsVisible: true }) box.Owner = owner;
        else box.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        (PackIconKind icon, string brush) = kind switch
        {
            DialogKind.Info => (PackIconKind.InformationOutline, "Shell.Accent"),
            DialogKind.Question => (PackIconKind.HelpCircleOutline, "Shell.Accent"),
            DialogKind.Warning => (PackIconKind.AlertOutline, "Shell.Warning"),
            _ => (PackIconKind.AlertCircleOutline, "Shell.Error"),
        };
        box.KindIcon.Kind = icon;
        box.KindIcon.Foreground = box.IconBack.Fill = Theme.Brush(brush);
        box.Heading.Text = title;
        box.Message.Text = message;
        if (!string.IsNullOrEmpty(details))
        {
            box.Details.Text = details;
            box.DetailsBox.Visibility = Visibility.Visible;
        }
        int chosen = -1;
        for (int i = 0; i < buttons.Length; i++)
        {
            int index = i;
            var button = new Button
            {
                Content = buttons[i],
                Style = (Style)Application.Current.FindResource(i == 0 ? "Shell.PrimaryButton" : "Shell.Button"),
                Margin = new Thickness(8, 0, 0, 0),
                MinWidth = 84,
                IsDefault = i == defaultButton,
                IsCancel = buttons.Length > 1 && i == buttons.Length - 1,
            };
            button.Click += (_, _) => { chosen = index; box.Close(); };
            box.Buttons.Children.Add(button);
        }
        box.Loaded += (_, _) => (box.Buttons.Children[Math.Clamp(defaultButton, 0, buttons.Length - 1)] as Button)?.Focus();
        box.ShowDialog();
        return chosen;
    }

    /// <summary>A question: true for the main button.</summary>
    public static bool Ask(Window? owner, DialogKind kind, string title, string message, string yes, string? no = null,
        string? details = null, bool cancelByDefault = false) =>
        Show(owner, kind, title, message, details, [yes, no ?? L.Text("Cancel")], cancelByDefault ? 1 : 0) == 0;

    /// <summary>A notice with one button.</summary>
    public static void Tell(Window? owner, DialogKind kind, string title, string message, string? details = null, string? ok = null) =>
        Show(owner, kind, title, message, details, [ok ?? L.Text("OK")]);
}

public partial class DialogWindow : Window
{
    public DialogWindow() => InitializeComponent();

    void Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
