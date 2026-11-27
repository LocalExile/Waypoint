using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Waypoint;

/// <summary>Tools for the game itself, each in its own window: the map viewer, the item crafter, the lab layout (as on Windows).</summary>
partial class ToolsTab : UserControl
{
    /// <summary>Simple mode's way to the Console and Logs sections, which its list leaves out.</summary>
    public event Action? ConsoleRequested, LogsRequested;

    public ToolsTab()
    {
        InitializeComponent();
        ShowLanguage();
        L.Current.PropertyChanged += (_, _) => Dispatcher.UIThread.Post(ShowLanguage);
    }

    /// <summary>The texts Windows gives through {l:Loc}.</summary>
    void ShowLanguage()
    {
        CrafterButton.Content = L.Text("Open item crafter");
        CrafterTitle.Text = L.Text("Item crafter");
        CrafterText.Text = L.Text("Make any item: a base, its rarity, modifiers with their values, sockets and influences. Save it to come back to it, and give it to the character in game.");
        ConsoleButton.Content = L.Text("Open console");
        ConsoleTitle.Text = L.Text("Console");
        ConsoleText.Text = L.Text("Look up the server's commands and use one in the game.");
        LogsButton.Content = L.Text("Open logs");
        LogsTitle.Text = L.Text("Logs");
        LogsText.Text = L.Text("What Waypoint, the servers and the game wrote down: where to look when something went wrong.");
    }

    Shared _shared = null!;
    ItemCrafterWindow? _crafter;

    MapViewerWindow? _mapViewer;

    public void Attach(Shared shared)
    {
        _shared = shared;
        MapButton.IsEnabled = !Repo.IsInstall && MapViewerHelper.IsAvailable(Environment.CurrentDirectory);
        ToolTip.SetTip(MapButton, MapButton.IsEnabled ? null : L.Text("The map viewer needs a source checkout with the terrain generator and extracted game data."));
    }

    void OpenMapViewer(object? sender, RoutedEventArgs e)
    {
        if (_mapViewer is { IsVisible: true }) { _mapViewer.Activate(); return; }
        _mapViewer = new MapViewerWindow(_shared) { WindowStartupLocation = WindowStartupLocation.CenterScreen };
        _mapViewer.Closed += (_, _) => _mapViewer = null;
        _mapViewer.Show();
    }

    /// <summary>The crafter's window, or the one already open brought forward. It is offered on a release too: its window
    /// reads the Gateway's assembly before asking it for the `crafter` mode (CrafterCatalog.HasCrafter), and gives items
    /// through the Waypoint Tools mod there.</summary>
    void OpenItemCrafter(object? sender, RoutedEventArgs e)
    {
        if (_crafter is { IsVisible: true })
        {
            if (_crafter.WindowState == WindowState.Minimized) _crafter.WindowState = WindowState.Normal;
            _crafter.Activate();
            return;
        }
        _crafter = new ItemCrafterWindow(_shared) { WindowStartupLocation = WindowStartupLocation.CenterScreen };
        _crafter.Closed += (_, _) => _crafter = null;
        _crafter.Show();
    }

    /// <summary>The two cards that lead to Console and Logs, for the mode whose list has neither.</summary>
    public void ShowMode(bool simple) => ConsoleCard.IsVisible = LogsCard.IsVisible = simple;

    LabLayoutWindow? _labLayout;

    /// <summary>The lab layout's window, or the one already open brought forward. A window of its own, not owned by
    /// Waypoint's: either can be in front.</summary>
    void OpenLabLayout(object? sender, RoutedEventArgs e)
    {
        if (_labLayout is { IsVisible: true })
        {
            if (_labLayout.WindowState == WindowState.Minimized) _labLayout.WindowState = WindowState.Normal;
            _labLayout.Activate();
            return;
        }
        _labLayout = new LabLayoutWindow { WindowStartupLocation = WindowStartupLocation.CenterScreen };
        _labLayout.Closed += (_, _) => _labLayout = null;
        _labLayout.Show();
    }

    void OpenConsole(object? sender, RoutedEventArgs e) => ConsoleRequested?.Invoke();

    void OpenLogs(object? sender, RoutedEventArgs e) => LogsRequested?.Invoke();
}
