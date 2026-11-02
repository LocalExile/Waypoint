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

/// <summary>Finds LocalExile checkouts in the usual places, or lets the player point at one. Used from Settings; a found
/// checkout is only ever used after the player picks it.</summary>
sealed class CheckoutDialog : FormWindow
{
    readonly CancellationTokenSource _cancel = new();
    readonly TextBlock _status = new() { Margin = new Thickness(0, 0, 0, 10) };
    readonly StackPanel _found = new();
    readonly string? _current;

    /// <summary>The checkout the player chose; null when they cancelled.</summary>
    public string? Chosen { get; private set; }

    public CheckoutDialog(Window? owner, string? current)
    {
        _current = current;
        DialogLayout.Configure(this, owner, L.Text("Choose your LocalExile checkout"), 680);
        var root = new DockPanel { Margin = new Thickness(24) };
        var buttons = new WrapPanel { Margin = new Thickness(0, 16, 0, 0), ItemSpacing = 8 };
        DockPanel.SetDock(buttons, Dock.Bottom);
        var browse = new Button { Content = L.Text("Browse…") };
        browse.Click += async (_, _) => { if (await ServerChoice.ChooseAsync(this) is { } folder) { Chosen = folder; Accept(); } };
        buttons.Children.Add(browse);
        buttons.Children.Add(Dismiss(L.Text("Cancel")));
        root.Children.Add(buttons);
        var body = new StackPanel();
        body.Children.Add(new TextBlock { Text = L.Text("Choose the folder of your LocalExile server checkout: the one with GameServer.slnx and scripts\\dev\\live.ps1. Waypoint remembers it."), Margin = new Thickness(0, 0, 0, 12) });
        body.Children.Add(_status);
        body.Children.Add(_found);
        root.Children.Add(new ScrollViewer { Content = body });
        Content = root;
        Closed += (_, _) => _cancel.Cancel();
        Opened += async (_, _) => await SearchAsync();
    }

    async Task SearchAsync()
    {
        _status.Text = L.Text("Checking upstream HEAD and searching common local folders… You can still Browse to choose a location.");
        try
        {
            var roots = RepositoryDiscovery.DefaultRoots(AppContext.BaseDirectory, Environment.CurrentDirectory, _current ?? "");
            var result = await RepositoryLocator.FindAsync(roots, _cancel.Token);
            int matches = result.Repositories.Count(r => r.MatchesUpstream);
            _status.Text = matches > 1 ? L.Text("{0} checkouts match upstream HEAD. Choose one below, or Browse elsewhere.", matches)
                : matches == 1 ? L.Text("One checkout matches upstream HEAD. You can use it below or Browse elsewhere.")
                : result.Repositories.Length > 0 ? L.Text("No matching upstream HEAD found. Other LocalExile checkouts are listed below; you can use any of them or Browse elsewhere.")
                : L.Text("No LocalExile checkout found in the searched locations. Browse to your checkout instead.");
            if (result.Limited) _status.Text += L.Text("\nSearch limits reached; other checkouts may exist. Browse to any location.");
            foreach (var repository in result.Repositories) _found.Children.Add(Row(repository));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            _status.Text = L.Text("Automatic lookup could not finish. Browse to your checkout instead. ") + ex.Message;
        }
    }

    Control Row(DiscoveredRepository repository)
    {
        var use = new Button { Content = L.Text("Use this checkout"), VerticalAlignment = VerticalAlignment.Center };
        use.Click += (_, _) => { Chosen = repository.Root; Accept(); };
        DockPanel.SetDock(use, Dock.Right);
        var text = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
        text.Children.Add(new TextBlock { Text = repository.Root, FontWeight = FontWeight.SemiBold });
        text.Children.Add(new TextBlock { Text = repository.Summary + " · " + repository.CommitLabel, Margin = new Thickness(0, 2, 0, 0), Foreground = App.Brush("Shell.TextSecondary") });
        var row = new DockPanel { Margin = new Thickness(0, 6, 0, 6) };
        row.Children.Add(use);
        row.Children.Add(text);
        return row;
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
