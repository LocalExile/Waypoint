using System.Windows;
using System.Windows.Controls;

namespace Waypoint;

/// <summary>Tools for the game itself, each in its own window: the item crafter.</summary>
public partial class ToolsTab : UserControl
{
    Shared _shared = null!;
    ItemCrafterWindow? _crafter;

    public ToolsTab() => InitializeComponent();

    internal void Attach(Shared shared) => _shared = shared;

    /// <summary>The crafter's window, or the one already open brought forward.</summary>
    void OpenItemCrafter(object sender, RoutedEventArgs e)
    {
        if (_crafter is { IsLoaded: true })
        {
            if (_crafter.WindowState == WindowState.Minimized) _crafter.WindowState = WindowState.Normal;
            _crafter.Activate();
            return;
        }
        _crafter = new ItemCrafterWindow(_shared) { Owner = Window.GetWindow(this) };
        _crafter.Closed += (_, _) => _crafter = null;
        _crafter.Show();
    }
}
