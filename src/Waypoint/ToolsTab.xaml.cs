using System.Windows;
using System.Windows.Controls;

namespace Waypoint;

/// <summary>Tools for the game itself, each in its own window: the item crafter.</summary>
public partial class ToolsTab : UserControl
{
    Shared _shared = null!;
    ItemCrafterWindow? _crafter;

    /// <summary>The player wants a copy of LocalExile that has the crafter.</summary>
    internal event Action? NewCopyRequested;

    public ToolsTab() => InitializeComponent();

    internal void Attach(Shared shared)
    {
        _shared = shared;
        // Asking a Gateway for a mode it lacks starts it serving, so a release's is not asked.
        if (!Repo.IsInstall) return;
        CrafterButton.IsEnabled = false;
        ReleaseNote.Visibility = Visibility.Visible;
    }

    void GetCrafterClicked(object sender, RoutedEventArgs e) => NewCopyRequested?.Invoke();

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
}
