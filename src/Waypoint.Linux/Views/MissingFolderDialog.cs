using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Waypoint;

/// <summary>
/// The folder the settings name is not there at startup: moved, renamed, or on a drive that is not connected. As on Windows
/// that is not a first start, so the setup guide does not open by itself: Waypoint says what it can't find and offers the
/// ways on, which are another folder it knows (<see cref="FolderList"/>), the folder found again, the setup guide, or
/// closing. Nothing is changed until one is chosen.
/// </summary>
public sealed class MissingFolderDialog : Window
{
    /// <summary>The folder to open; null when the guide was asked for, or the dialog closed.</summary>
    public string? Chosen { get; private set; }
    /// <summary>"Set up again…": the setup guide, as on a first start.</summary>
    public bool SetUpAgain { get; private set; }

    internal MissingFolderDialog(string gone, IReadOnlyList<KnownFolder> others)
    {
        Title = L.Text("Waypoint can't find your LocalExile folder");
        Width = 620;
        MinWidth = 420;
        SizeToContent = SizeToContent.Height;
        ShowInTaskbar = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var body = new StackPanel { Margin = new Thickness(24) };
        body.Children.Add(new TextBlock { Text = gone, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        body.Children.Add(new TextBlock
        {
            Text = L.Text("This folder is not there: it may have been moved or renamed, or its drive is not connected. Nothing has been changed."),
            Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap,
        });
        if (others.Count > 0)
        {
            var title = new TextBlock { Text = L.Text("Your other LocalExile folders"), Margin = new Thickness(0, 18, 0, 4) };
            title.Classes.Add("note");
            body.Children.Add(title);
            foreach (var folder in others) body.Children.Add(Row(folder));
        }
        var buttons = new WrapPanel { Margin = new Thickness(0, 18, 0, 0) };
        var find = new Button { Content = L.Text("Find it…"), Margin = new Thickness(0, 0, 6, 0) };
        ToolTip.SetTip(find, L.Text("Choose where the folder is now"));
        find.Click += (_, _) => { if (App.ChooseServer(this) is { } found) Choose(found); };
        var guide = new Button { Content = L.Text("Set up again…"), Margin = new Thickness(0, 0, 6, 0) };
        ToolTip.SetTip(guide, L.Text("Opens the setup guide, which gets LocalExile ready in a folder you choose"));
        guide.Click += (_, _) => { SetUpAgain = true; Close(); };
        var close = new Button { Content = L.Text("Close"), IsCancel = true };
        close.Click += (_, _) => Close();
        buttons.Children.Add(find);
        buttons.Children.Add(guide);
        buttons.Children.Add(close);
        body.Children.Add(buttons);
        Content = body;
    }

    void Choose(string folder)
    {
        Chosen = folder;
        Close();
    }

    Control Row(KnownFolder folder)
    {
        var open = new Button { Content = L.Text("Open"), VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(open, L.Text("Open {0}", folder.Path));
        open.Click += (_, _) => Choose(folder.Path);
        DockPanel.SetDock(open, Dock.Right);
        var text = new StackPanel { Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = folder.Title, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        var path = new TextBlock { Text = folder.Path, Margin = new Thickness(0, 1, 0, 0) };
        path.Classes.Add("note");
        text.Children.Add(path);
        var row = new DockPanel { Margin = new Thickness(0, 6, 0, 6) };
        row.Children.Add(open);
        row.Children.Add(text);
        return row;
    }
}
