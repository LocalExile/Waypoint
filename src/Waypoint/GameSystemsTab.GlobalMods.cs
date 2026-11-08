#if WINDOWS
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
#else
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
#endif

namespace Waypoint;

public sealed partial class GameSystemsTab
{
    TextBlock? _globalModsTitle, _globalModsChoices, _globalModsRuntime;
    Button? _manageModsAction;
    Border? _globalModsBar;

    Border CreateGlobalModsBar()
    {
        var row = new DockPanel();
        _manageModsAction = SettingsEditor.Button("", () => OpenModsRequested?.Invoke());
        _manageModsAction.VerticalAlignment = VerticalAlignment.Center;
        _manageModsAction.Margin = new Thickness(16, 0, 0, 0);
        DockPanel.SetDock(_manageModsAction, Dock.Right); row.Children.Add(_manageModsAction);
        var text = new StackPanel();
        var caption = new StackPanel { Orientation = Orientation.Horizontal };
        _globalModsTitle = Type("", 13, true); caption.Children.Add(_globalModsTitle);
        _globalModsRuntime = Type("", 11, ink: "Shell.TextSecondary");
        _globalModsRuntime.Margin = new Thickness(12, 0, 0, 0); caption.Children.Add(_globalModsRuntime);
        text.Children.Add(caption);
        _globalModsChoices = Type("", 12, ink: "Shell.TextSecondary");
        _globalModsChoices.TextWrapping = TextWrapping.NoWrap; _globalModsChoices.TextTrimming = TextTrimming.CharacterEllipsis;
        _globalModsChoices.Margin = new Thickness(0, 4, 0, 0); text.Children.Add(_globalModsChoices);
        row.Children.Add(text);
        _globalModsBar = Card(row, 12); _globalModsBar.Margin = new Thickness(0, 0, 0, 12);
        return _globalModsBar;
    }
    void UpdateGlobalMods()
    {
        if (_globalModsBar is null) return;
        _globalModsTitle!.Text = L.Text("Global mods");
        _manageModsAction!.Content = L.Text("Manage in Mods"); Named(_manageModsAction, L.Text("Manage in Mods"));
        var state = _shared.Workspace.Latest;
        string detail;
        try
        {
            var entries = state is null ? [] : ModCatalog.Read(state);
            _globalModsChoices!.Text = ModsText.GlobalChoices(state?.ModEnvironment, entries);
            detail = ModsText.GlobalChoices(state?.ModEnvironment, entries, compact: false);
            if (state?.ModEnvironment is { Supported: true, Enabled: true })
                foreach (var entry in entries.Where(e => e.Enabled)) detail += "\n" + entry.Title + " · " + ModsText.RuntimeStatus(state, entry.Id);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException or InvalidOperationException or ArgumentException)
        {
            _globalModsChoices!.Text = L.Text("Could not inspect global mods"); detail = e.Message;
        }
        _globalModsRuntime!.Text = ModsText.GlobalRuntime(state);
        string help = L.Text("Mod choices apply across all leagues. Saved choices do not confirm live load status.") + "\n" + detail;
        Named(_globalModsBar, L.Text("Global mods") + ". " + detail + ". " + _globalModsRuntime.Text);
#if WINDOWS
        _globalModsBar.ToolTip = help;
#else
        ToolTip.SetTip(_globalModsBar, help);
#endif
    }
}
