using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;

namespace Waypoint;

/// <summary>The Mods section: the checkout's mods (ModCatalog reads their manifests and the server's saved choices), built
/// from source, enabled and disabled through the adapter. With a client in a playable area the same buttons act on the
/// running Gateway; otherwise the choice applies at its next start.</summary>
public partial class ModsTab : UserControl
{
    Shared _shared = null!;
    ModEntry[] _entries = [];
    bool _busy, _loading;

    /// <summary>Gateway log: the window opens the Logs section on that source.</summary>
    internal event Action? ShowGatewayLog;

    public ModsTab() => InitializeComponent();

    RuntimeSnapshot? State => _shared.Workspace.Latest;
    ModEntry? Chosen => ModList.SelectedItem as ModEntry;

    internal void Attach(Shared shared)
    {
        _shared = shared;
        OutputView.Attach(shared);
        // A release cannot build, and live control needs the client bridge it does not have.
        if (Repo.IsInstall) BuildAllButton.Visibility = BuildButton.Visibility = ReloadButton.Visibility = CheckModsButton.Visibility = Visibility.Collapsed;
        shared.Workspace.Changed += () => Dispatcher.BeginInvoke(Render);
        shared.LiveRunning += running => Dispatcher.BeginInvoke(() =>
        {
            _busy = running;
            Render();
        });
        L.Current.PropertyChanged += (_, _) => Render();
        IsVisibleChanged += async (_, e) => { if ((bool)e.NewValue) await shared.Workspace.RefreshAsync(); };
        Render();
    }

    void Render()
    {
        _loading = true;
        AutoBackup.IsChecked = _shared.Settings.AutomaticBackups;
        _loading = false;
        var state = State;
        bool supported = state?.ModEnvironment is { Supported: true };
        try { _entries = state is null ? [] : ModCatalog.Read(state); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _entries = [];
            Status.Text = L.Text("Could not inspect mods: ") + ex.Message;
            Filter();
            return;
        }
        var gateway = state?.Services.FirstOrDefault(s => s.Name == "Gateway");
        Status.Text = state is null ? L.Text("Choose and check a workspace to see its mods.")
            : !supported ? L.Text("This revision has no supported mod host. Older LocalExile revisions can still be launched normally.")
            : L.Text("{0} mods · {1}\n{2}", _entries.Length, state.ModEnvironment!.Enabled ? L.Text("Mods enabled") : L.Text("Mods off in launch profile (Game_MODS=0)"), state.ModEnvironment.Folder);
        bool canUse = supported && !_busy && state!.Conflicts.Length == 0;
        BuildAllButton.IsEnabled = canUse && _entries.Any(e => e.Project.Length > 0);
        OpenModsButton.IsEnabled = supported && Directory.Exists(state!.ModEnvironment!.Folder) && ModCatalog.Physical(state.ModEnvironment.Folder);
        OpenModsButton.ToolTip = OpenModsButton.IsEnabled ? state!.ModEnvironment?.Folder : L.Text("Build source mods or install a local mod to create this folder.");
        CheckModsButton.IsEnabled = canUse && gateway?.Status == "Ready" && state!.GameRunning;
        CheckModsButton.ToolTip = CheckModsButton.IsEnabled ? L.Text("Query the running Gateway’s confirmed mod status.")
            : L.Text("Start Gateway and enter a playable area to check live status. Python + Frida are required.");
        Filter();
    }

    void Filter()
    {
        string? selected = Chosen?.Id;
        string text = Search.Text.Trim();
        var matches = _entries.Where(e => (e.SearchText + " " + e.Summary + " " + e.DisplayDetail).Contains(text, StringComparison.OrdinalIgnoreCase)).ToArray();
        ModList.ItemsSource = matches;
        if (selected is not null) ModList.SelectedItem = matches.FirstOrDefault(e => e.Id == selected);
        Empty.Visibility = matches.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        Empty.Text = text.Length > 0 ? L.Text("No mods match your search. Clear it to see all mods.")
            : State is null ? L.Text("Choose and check a workspace to see its mods.")
            : State.ModEnvironment is not { Supported: true } ? L.Text("This revision does not have a supported mod host.")
            : L.Text("No mods found. Build source mods or open the mods folder to add one.");
        ShowSelection();
    }

    void SearchChanged(object sender, TextChangedEventArgs e)
    {
        if (_shared is not null) Filter();
    }

    void SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_shared is not null) ShowSelection();
    }

    void ShowSelection()
    {
        var selected = Chosen;
        var state = State;
        var gateway = state?.Services.FirstOrDefault(s => s.Name == "Gateway");
        bool supported = state?.ModEnvironment is { Supported: true };
        bool canUse = !_busy && supported && state!.Conflicts.Length == 0 && selected is not null;
        bool live = !Repo.IsInstall && supported && gateway?.Status == "Ready" && state?.GameRunning == true && state.ModEnvironment?.Enabled == true;
        ToggleButton.Content = selected?.Enabled == true ? L.Text("Disable mod") : L.Text("Enable mod");
        ToggleButton.IsEnabled = canUse && selected!.Fingerprint.Length > 0 && (gateway?.IsRunning != true || live);
        BuildButton.IsEnabled = canUse && selected!.Project.Length > 0;
        ReloadButton.IsEnabled = canUse && live && selected!.Enabled && selected.Fingerprint.Length > 0;
        OpenButton.IsEnabled = selected is not null && Directory.Exists(selected.Folder) && ModCatalog.Physical(selected.Folder);
        Selected.Text = selected?.Id ?? L.Text("Mod details");
        Hint.Text = selected is null ? L.Text("Choose a mod from the list.")
            : _busy ? L.Text("A workspace operation is in progress.")
            : Repo.IsInstall ? L.Text(gateway?.IsRunning == true ? "Close the game to change mods." : "Enable choices apply the next time you press Play.")
            : gateway?.IsRunning == true && !live ? L.Text("Enter a playable area for live controls, or stop Gateway to save enable choices.")
            : live ? L.Text("Live changes affect every character on this Gateway. Reload resets mod state.")
            : L.Text("Enable choices apply at the next Gateway start. Build selected compiles this mod’s source.");
        ToggleButton.ToolTip = Hint.Text;
        ReloadButton.ToolTip = live ? L.Text("Reload this enabled mod and its dependents.") : L.Text("Needs Gateway running, an enabled built mod, and a client in a playable area (Python + Frida).");
        BuildButton.ToolTip = selected?.Project.Length > 0 ? L.Text("Build this source project.") : L.Text("This mod has no source project in the selected checkout.");
        Details.Text = selected is null ? L.Text("Select a mod to view its dependencies and controls.")
            : L.Text("{0} · {1}\nDependencies: {2}\n{3}\n{4}", selected.Id, selected.Summary, selected.Depends.Length == 0 ? L.Text("none") : string.Join(", ", selected.Depends), selected.DisplayDetail,
                selected.Project.Length > 0 ? L.Text("Source: ") + selected.Project : L.Text("Binary-only mod"));
        CommandHint.Text = selected is null ? L.Text("In game: /mod lists runtime status.")
            : L.Text("In game: /mod_{0} {1} · /mod_reload {2}", selected.Enabled ? "disable" : "enable", selected.Id, selected.Id);
    }

    void BackupChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || _shared is null) return;
        _shared.Settings.AutomaticBackups = AutoBackup.IsChecked == true;
        _shared.Settings.Save();
    }

    async void CheckMods(object sender, RoutedEventArgs e) => await _shared.Workspace.OperateAsync("QueryMods", L.Text("Check live status"));

    async void BuildAll(object sender, RoutedEventArgs e) => await _shared.Workspace.OperateAsync("BuildMods", L.Text("Build all mods"));

    async void BuildSelected(object sender, RoutedEventArgs e)
    {
        if (Chosen is { } selected) await _shared.Workspace.OperateAsync("BuildMod", L.Text("Build selected"), mod: new(selected.Id, selected.Project));
    }

    async void Toggle(object sender, RoutedEventArgs e)
    {
        if (Chosen is { } selected)
            await _shared.Workspace.OperateAsync(selected.Enabled ? "DisableMod" : "EnableMod", selected.Enabled ? L.Text("Disable mod") : L.Text("Enable mod"), mod: new(selected.Id));
    }

    async void Reload(object sender, RoutedEventArgs e)
    {
        if (Chosen is { } selected) await _shared.Workspace.OperateAsync("ReloadMod", L.Text("Reload mod"), mod: new(selected.Id));
    }

    void OpenMods(object sender, RoutedEventArgs e)
    {
        if (State?.ModEnvironment is { Supported: true } environment && Directory.Exists(environment.Folder) && ModCatalog.Physical(environment.Folder))
            OpenFolder(environment.Folder);
    }

    void OpenSelected(object sender, RoutedEventArgs e)
    {
        if (Chosen is { } selected && Directory.Exists(selected.Folder) && ModCatalog.Physical(selected.Folder)) OpenFolder(selected.Folder);
    }

    void GatewayLog(object sender, RoutedEventArgs e) => ShowGatewayLog?.Invoke();

    static void OpenFolder(string path) => Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { path }, UseShellExecute = false })?.Dispose();
}
