using System.Windows;
using System.Windows.Controls;

namespace Waypoint;

/// <summary>Tools for the game itself, each in its own window: the map viewer, the item crafter, the lab layout.</summary>
public partial class ToolsTab : UserControl
{
    Shared _shared = null!;
    ItemCrafterWindow? _crafter;
    MapViewerWindow? _mapViewer;
    LabLayoutWindow? _labLayout;

    /// <summary>Simple mode's way to the Console and Logs sections, which its list leaves out.</summary>
    internal event Action? ConsoleRequested, LogsRequested;

    public ToolsTab() => InitializeComponent();

    internal void Attach(Shared shared)
    {
        _shared = shared;
        MapButton.IsEnabled = !Repo.IsInstall && MapViewerHelper.IsAvailable(Environment.CurrentDirectory);
        MapButton.ToolTip = MapButton.IsEnabled ? null : L.Text("The map viewer needs a source checkout with the terrain generator and extracted game data.");
        ToolTipService.SetShowOnDisabled(MapButton, true);
        // The crafter is offered on a release too: its window reads the Gateway's assembly before asking it for the
        // `crafter` mode (CrafterCatalog.HasCrafter), and gives items through the Waypoint Tools mod there.
    }

    /// <summary>The two cards that lead to Console and Logs, for the mode whose list has neither.</summary>
    internal void ShowMode(bool simple) =>
        ConsoleCard.Visibility = LogsCard.Visibility = simple ? Visibility.Visible : Visibility.Collapsed;

    void OpenConsole(object sender, RoutedEventArgs e) => ConsoleRequested?.Invoke();

    void OpenLogs(object sender, RoutedEventArgs e) => LogsRequested?.Invoke();

    void OpenMapViewer(object sender, RoutedEventArgs e)
    {
        if (_mapViewer is { IsLoaded: true }) { _mapViewer.Activate(); return; }
        _mapViewer = new MapViewerWindow(_shared);
        _mapViewer.Closed += (_, _) => _mapViewer = null;
        _mapViewer.Show();
    }

    /// <summary>The crafter's window, or the one already open brought forward.</summary>
    void OpenItemCrafter(object sender, RoutedEventArgs e)
    {
        if (_crafter is { IsLoaded: true })
        {
            if (_crafter.WindowState == WindowState.Minimized) _crafter.WindowState = WindowState.Normal;
            _crafter.Activate();
            return;
        }
        // A window of its own, not owned by Waypoint's: either can be in front. It opens centred on Waypoint's.
        _crafter = new ItemCrafterWindow(_shared);
        if (Window.GetWindow(this) is { WindowState: WindowState.Normal, IsVisible: true } main)
        {
            _crafter.WindowStartupLocation = WindowStartupLocation.Manual;
            _crafter.Left = main.Left + (main.ActualWidth - _crafter.Width) / 2;
            _crafter.Top = main.Top + (main.ActualHeight - _crafter.Height) / 2;
        }
        else _crafter.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        _crafter.Closed += (_, _) => _crafter = null;
        _crafter.Show();
    }

    /// <summary>The lab layout's window, or the one already open brought forward.</summary>
    void OpenLabLayout(object sender, RoutedEventArgs e)
    {
        if (_labLayout is { IsLoaded: true })
        {
            if (_labLayout.WindowState == WindowState.Minimized) _labLayout.WindowState = WindowState.Normal;
            _labLayout.Activate();
            return;
        }
        _labLayout = new LabLayoutWindow();
        if (Window.GetWindow(this) is { WindowState: WindowState.Normal, IsVisible: true } main)
        {
            _labLayout.WindowStartupLocation = WindowStartupLocation.Manual;
            _labLayout.Left = main.Left + (main.ActualWidth - _labLayout.Width) / 2;
            _labLayout.Top = main.Top + (main.ActualHeight - _labLayout.Height) / 2;
        }
        else _labLayout.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        _labLayout.Closed += (_, _) => _labLayout = null;
        _labLayout.Show();
    }
}
