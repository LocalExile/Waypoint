using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;

namespace Waypoint;

enum DialogKind { Info, Question, Warning, Error }

/// <summary>The same message boxes as on Windows, called the same way: they answer before returning, so the shared engine
/// can ask a question in the middle of its work. Buttons are named after what they do; the first is the main one.</summary>
static class Dialog
{
    /// <summary>The chosen button's index, or -1 when the box was closed without one.</summary>
    public static int Show(Window? owner, DialogKind kind, string title, string message, string? details, string[] buttons, int defaultButton = 0)
    {
        var box = new Window
        {
            Title = title, Width = 520, SizeToContent = SizeToContent.Height, CanResize = false,
            WindowStartupLocation = owner is { IsVisible: true } ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
        };
        var panel = new StackPanel { Margin = new Thickness(20), Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 17, FontWeight = Avalonia.Media.FontWeight.SemiBold, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        if (!string.IsNullOrEmpty(details))
            panel.Children.Add(new ScrollViewer { MaxHeight = 220, Content = new SelectableTextBlock { Text = details, TextWrapping = Avalonia.Media.TextWrapping.Wrap, FontSize = 12 } });
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        int chosen = -1;
        for (int i = 0; i < buttons.Length; i++)
        {
            int index = i;
            var button = new Button { Content = buttons[i], MinWidth = 84, IsDefault = i == defaultButton, IsCancel = buttons.Length > 1 && i == buttons.Length - 1 };
            if (i == 0) button.Classes.Add("accent");
            button.Click += (_, _) => { chosen = index; box.Close(); };
            row.Children.Add(button);
        }
        panel.Children.Add(row);
        box.Content = panel;
        var frame = new DispatcherFrame();
        box.Closed += (_, _) => frame.Continue = false;
        if (owner is { IsVisible: true }) _ = box.ShowDialog(owner); else box.Show();
        Dispatcher.UIThread.PushFrame(frame);
        return chosen;
    }

    /// <summary>A question: true for the main button.</summary>
    public static bool Ask(Window? owner, DialogKind kind, string title, string message, string yes, string? no = null, string? details = null, bool cancelByDefault = false) =>
        Show(owner, kind, title, message, details, [yes, no ?? L.Text("Cancel")], cancelByDefault ? 1 : 0) == 0;

    /// <summary>A notice with one button.</summary>
    public static void Tell(Window? owner, DialogKind kind, string title, string message, string? details = null, string? ok = null) =>
        Show(owner, kind, title, message, details, [ok ?? L.Text("OK")]);
}

/// <summary>One line of the setup check (the same record as on Windows, whose lives in ControlTab).</summary>
sealed record SetupCheck(bool? Ok, string Text, string Hint = "");
