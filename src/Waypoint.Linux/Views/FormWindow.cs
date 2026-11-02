using System.Diagnostics;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;

namespace Waypoint;

/// <summary>A dialog with fields (an account change, the report preview, the checkout choice). Notices and questions are
/// <see cref="Dialog"/>'s. Like it, this answers before returning: <see cref="ShowDialog()"/> blocks as WPF's does.</summary>
class FormWindow : Window
{
    Window? _shownOver;
    bool? _result;

    public FormWindow()
    {
        ShowInTaskbar = false;
        // Long sentences wrap in these windows, without a style on each.
        Styles.Add(new Style(x => x.OfType<TextBlock>()) { Setters = { new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap) } });
    }

    /// <summary>The window it opens over; none centres it on the screen and gives it its own place in the taskbar.</summary>
    public void ShowOver(Window? owner)
    {
        _shownOver = owner is { IsVisible: true } ? owner : null;
        WindowStartupLocation = _shownOver is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
        if (_shownOver is null) ShowInTaskbar = true;
    }

    /// <summary>True when the form was accepted; the window over the form is out of reach until it closes.</summary>
    public bool? ShowDialog()
    {
        var frame = new DispatcherFrame();
        Closed += (_, _) => frame.Continue = false;
        if (_shownOver is not null) _ = ShowDialog(_shownOver); else Show();
        Dispatcher.UIThread.PushFrame(frame);
        return _result;
    }

    /// <summary>Closes the form with its answer (WPF's DialogResult = true).</summary>
    protected void Accept()
    {
        _result = true;
        Close();
    }

    /// <summary>The button that leaves without an answer; Escape presses it.</summary>
    protected Button Dismiss(string text)
    {
        var button = new Button { Content = text, IsCancel = true };
        button.Click += (_, _) => Close();
        return button;
    }
}

static class DialogLayout
{
    public static void Configure(FormWindow dialog, Window? owner, string title, int width = 680)
    {
        dialog.ShowOver(owner);
        dialog.Title = title;
        dialog.Width = width;
        dialog.SizeToContent = SizeToContent.Height;
        dialog.MaxHeight = Math.Min(650, WorkHeight(dialog, owner) - 40);
        dialog.MinWidth = 420;
        dialog.MinHeight = 220;
        dialog.CanResize = true;
    }

    /// <summary>The height of the screen the dialog opens on, less what the desktop keeps for itself.</summary>
    public static double WorkHeight(Window dialog, Window? owner)
    {
        var screen = owner is { IsVisible: true } ? owner.Screens.ScreenFromWindow(owner) : dialog.Screens.Primary;
        return screen is null ? double.PositiveInfinity : screen.WorkingArea.Height / screen.Scaling;
    }
}

/// <summary>The account dialog's labels and buttons.</summary>
static class AccountUi
{
    public static TextBlock Text(string text, double size = 13, bool muted = false)
    {
        var label = new TextBlock { Text = L.Text(text), FontSize = size };
        if (muted) label.Foreground = App.Brush("Shell.TextSecondary");
        return label;
    }

    public static Button Button(string text, Action click, bool primary = false)
    {
        var button = new Button { Content = L.Text(text) };
        if (primary) button.Classes.Add("accent");
        button.Click += (_, _) => click();
        return button;
    }
}

/// <summary>The report for a bug: redacted, previewed, and copied only when the player asks.</summary>
sealed class DiagnosticPreviewDialog : FormWindow
{
    public DiagnosticPreviewDialog(Window? owner, Func<string[], string> create)
    {
        DialogLayout.Configure(this, owner, L.Text("Diagnostic report preview"), 820);
        MaxHeight = double.PositiveInfinity; Height = 620; SizeToContent = SizeToContent.Manual;
        var grid = new Grid { Margin = new Thickness(20), RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto") };
        grid.Children.Add(new TextBlock { Text = L.Text("Review the redacted text before copying. Redaction recognizes common patterns; check for any private details it missed.") });
        var namesPanel = new StackPanel { Margin = new Thickness(0, 10, 0, 10) };
        namesPanel.Children.Add(new TextBlock { Text = L.Text("Additional account / character names to remove (comma separated)"), Margin = new Thickness(0, 0, 0, 6) });
        var names = new TextBox();
        ToolTip.SetTip(names, L.Text("Optional account/character names to remove, separated by commas"));
        AutomationProperties.SetName(names, L.Text("Additional names to remove (comma separated)"));
        namesPanel.Children.Add(names);
        Grid.SetRow(namesPanel, 1); grid.Children.Add(namesPanel);
        var preview = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, FontFamily = new FontFamily("monospace"), FontSize = 12 };
        ScrollViewer.SetVerticalScrollBarVisibility(preview, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollBarVisibility(preview, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        preview.Text = create([]);
        names.TextChanged += (_, _) => preview.Text = create((names.Text ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
        Grid.SetRow(preview, 2); grid.Children.Add(preview);
        var buttons = new WrapPanel { Margin = new Thickness(0, 14, 0, 0), ItemSpacing = 8 };
        var copy = new Button { Content = L.Text("Copy reviewed report") };
        copy.Click += async (_, _) => copy.Content = await ViewTools.Copy(this, preview.Text ?? "") ? L.Text("Copied") : L.Text("Clipboard busy — try again");
        buttons.Children.Add(copy); buttons.Children.Add(Dismiss(L.Text("Close")));
        Grid.SetRow(buttons, 3); grid.Children.Add(buttons); Content = grid;
    }
}

/// <summary>Choosing another checkout, and moving to it (the Windows app's App.ChooseServer and App.SwitchServer).</summary>
static class ServerChoice
{
    /// <summary>The folder picker until it gives a server checkout; null when the player cancels.</summary>
    public static async Task<string?> ChooseAsync(Window? owner)
    {
        if (owner?.StorageProvider is not { CanPickFolder: true } storage) return null;
        string start = Repo.Suggested(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory));
        while (true)
        {
            var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = L.Text("LocalExile server checkout (the folder with GameServer.slnx)"),
                SuggestedStartLocation = await storage.TryGetFolderFromPathAsync(start),
            });
            if (folders.Count == 0 || folders[0].TryGetLocalPath() is not { } folder) return null;
            if (Repo.IsRoot(folder) || LocalExileReleases.IsInstall(folder)) return Path.GetFullPath(folder);
            Dialog.Tell(owner, DialogKind.Warning, L.Text("Not a server checkout"),
                L.Text("{0} has no GameServer.slnx and scripts\\dev\\live.ps1.", folder), ok: L.Text("Choose again"));
            start = folder;
        }
    }

    /// <summary>Another checkout: saved in the settings, then Waypoint starts again on it (every section reads from the old
    /// one). The new copy starts only once this one has exited: closing can still be cancelled.</summary>
    public static async Task SwitchAsync(Window owner, Settings settings, string? chosen = null)
    {
        if ((chosen ?? await ChooseAsync(owner)) is not { } root || root == settings.ServerPath) return;
        settings.ServerPath = root;
        settings.Save();
        if (Environment.ProcessPath is { } exe && Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Exit += (_, _) =>
            {
                var start = new ProcessStartInfo(exe) { UseShellExecute = false };
                // Run as `dotnet Waypoint.dll`, the process is dotnet itself.
                if (Path.GetFileNameWithoutExtension(exe) == "dotnet") start.ArgumentList.Add(typeof(ServerChoice).Assembly.Location);
                Process.Start(start)?.Dispose();
            };
        owner.Close();
    }
}
