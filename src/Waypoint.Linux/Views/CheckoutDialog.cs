using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Waypoint;

/// <summary>Finds LocalExile checkouts in the usual places, or lets the player point at one. Used on the first start and
/// from Settings; a found checkout is only ever used after the player picks it.</summary>
public sealed class CheckoutDialog : Window
{
    readonly CancellationTokenSource _cancel = new();
    readonly TextBlock _status = new() { Margin = new Thickness(0, 0, 0, 10), TextWrapping = TextWrapping.Wrap };
    readonly StackPanel _found = new();
    readonly string? _current;

    /// <summary>The checkout the player chose; null when they cancelled.</summary>
    public string? Chosen { get; private set; }

    public CheckoutDialog(Window? owner, string? current)
    {
        _current = current;
        Title = L.Text("Choose your LocalExile checkout");
        Width = 680;
        Height = 420;
        MinWidth = 420;
        MinHeight = 220;
        ShowInTaskbar = owner is null;
        WindowStartupLocation = owner is { IsVisible: true } ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen;
        var root = new DockPanel { Margin = new Thickness(24) };
        var buttons = new WrapPanel { Margin = new Thickness(0, 16, 0, 0) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        var browse = new Button { Content = L.Text("Browse…"), Margin = new Thickness(0, 0, 6, 0) };
        browse.Click += (_, _) => { if (App.ChooseServer(this) is { } folder) Choose(folder); };
        buttons.Children.Add(browse);
        var cancel = new Button { Content = L.Text("Cancel"), IsCancel = true };
        cancel.Click += (_, _) => Close();
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);
        var body = new StackPanel();
        body.Children.Add(new TextBlock
        {
            Text = L.Text("Choose the folder of your LocalExile server checkout: the one with GameServer.slnx and scripts\\dev\\live.ps1. Waypoint remembers it."),
            Margin = new Thickness(0, 0, 0, 12), TextWrapping = TextWrapping.Wrap,
        });
        body.Children.Add(_status);
        body.Children.Add(_found);
        root.Children.Add(new ScrollViewer { Content = body });
        Content = root;
        Closed += (_, _) => _cancel.Cancel();
        Opened += async (_, _) => await SearchAsync();
    }

    void Choose(string folder)
    {
        Chosen = folder;
        Close();
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
        use.Click += (_, _) => Choose(repository.Root);
        DockPanel.SetDock(use, Dock.Right);
        var text = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
        text.Children.Add(new TextBlock { Text = repository.Root, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        var detail = new TextBlock { Text = repository.Summary + " · " + repository.CommitLabel, Margin = new Thickness(0, 2, 0, 0) };
        detail.Classes.Add("note");
        text.Children.Add(detail);
        var row = new DockPanel { Margin = new Thickness(0, 6, 0, 6) };
        row.Children.Add(use);
        row.Children.Add(text);
        return row;
    }
}
