using System.IO;
using System.Windows;
using System.Windows.Controls;
using Launcher.Core;

namespace Launcher.Desktop;

public partial class MainWindow
{
    ModEntry[] modEntries = [];
    bool updatingModPreferences;

    void RenderMods(RuntimeSnapshot state)
    {
        updatingModPreferences = true;
        ModsAutoBackupBox.IsChecked = settings.AutomaticBackups;
        ModsAutoBackupBox.IsEnabled = !busy;
        updatingModPreferences = false;
        bool supported = state.ModEnvironment is { Supported: true };
        try { modEntries = ModCatalog.Read(state); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { modEntries = []; ModsStatus.Text = L.Text("Could not inspect mods: ") + ex.Message; UpdateModSelection(); return; }
        var gateway = state.Services.FirstOrDefault(s => s.Name == "Gateway");
        ModsStatus.Text = !supported ? L.Text("This revision has no supported mod host. Older LocalExile revisions can still be launched normally.")
            : L.Text("{0} mods · {1}\n{2}", modEntries.Length, (state.ModEnvironment!.Enabled ? L.Text("Mods enabled") : L.Text("Mods off in launch profile (Game_MODS=0)")), state.ModEnvironment.Folder);
        bool canUse = supported && !busy && state.Conflicts.Length == 0;
        BuildAllModsButton.IsEnabled = canUse && modEntries.Any(e => e.Project.Length > 0);
        OpenModsButton.IsEnabled = supported && Directory.Exists(state.ModEnvironment!.Folder) && ModCatalog.Physical(state.ModEnvironment.Folder);
        OpenModsButton.ToolTip = OpenModsButton.IsEnabled ? state.ModEnvironment?.Folder : L.Text("Build source mods or install a local mod to create this folder.");
        CheckModsButton.IsEnabled = canUse && gateway?.Status == "Ready" && state.GameRunning;
        CheckModsButton.ToolTip = CheckModsButton.IsEnabled ? L.Text("Query the running Gateway’s confirmed mod status.")
            : L.Text("Start Gateway and enter a playable area to check live status. Python + Frida are required.");
        ApplyModFilter();
    }
    void ApplyModFilter()
    {
        if (ModsList is null || ModSearch is null) return;
        string? selected = (ModsList.SelectedItem as ModEntry)?.Id;
        var matches = modEntries.Where(e => (e.SearchText + " " + e.Summary + " " + e.DisplayDetail).Contains(ModSearch.Text.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
        UiCollection.Reconcile(visibleMods, matches, e => e.Id,
            (a, b) => a with { Depends = b.Depends } == b && a.Depends.SequenceEqual(b.Depends));
        if (selected is not null) ModsList.SelectedItem = visibleMods.FirstOrDefault(e => e.Id == selected);
        ModsEmpty.Visibility = matches.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ModsEmpty.Text = ModSearch.Text.Trim().Length > 0 ? L.Text("No mods match your search. Clear it to see all mods.")
            : snapshot is null ? L.Text("Choose and check a workspace to see its mods.")
            : snapshot.ModEnvironment is not { Supported: true } ? L.Text("This revision does not have a supported mod host.")
            : L.Text("No mods found. Build source mods or open the mods folder to add one.");
        UpdateModSelection();
    }
    void ModSearch_Changed(object sender, TextChangedEventArgs e) => ApplyModFilter();
    void ModSelection_Changed(object sender, SelectionChangedEventArgs e) => UpdateModSelection();
    void UpdateModSelection()
    {
        if (ToggleModButton is null) return;
        var selected = ModsList.SelectedItem as ModEntry;
        var gateway = snapshot?.Services.FirstOrDefault(s => s.Name == "Gateway");
        bool supported = snapshot?.ModEnvironment is { Supported: true };
        bool canUse = !busy && supported && snapshot!.Conflicts.Length == 0 && selected is not null;
        bool live = supported && gateway?.Status == "Ready" && snapshot?.GameRunning == true && snapshot.ModEnvironment?.Enabled == true;
        ToggleModButton.Content = selected?.Enabled == true ? L.Text("Disable mod") : L.Text("Enable mod");
        ToggleModButton.IsEnabled = canUse && selected!.Fingerprint.Length > 0 && (gateway?.IsRunning != true || live);
        BuildModButton.IsEnabled = canUse && selected!.Project.Length > 0;
        ReloadModButton.IsEnabled = canUse && live && selected!.Enabled && selected.Fingerprint.Length > 0;
        OpenSelectedModButton.IsEnabled = selected is not null && Directory.Exists(selected.Folder) && ModCatalog.Physical(selected.Folder);
        SelectedModTitle.Text = selected?.Id ?? L.Text("Mod details");
        ModActionHint.Text = selected is null ? L.Text("Choose a mod from the list.")
            : busy ? L.Text("A workspace operation is in progress.")
            : gateway?.IsRunning == true && !live ? L.Text("Enter a playable area for live controls, or stop Gateway to save enable choices.")
            : live ? L.Text("Live changes affect every character on this Gateway. Reload resets mod state.")
            : L.Text("Enable choices apply at the next Gateway start. Build selected compiles this mod’s source.");
        ToggleModButton.ToolTip = ModActionHint.Text;
        ReloadModButton.ToolTip = live ? L.Text("Reload this enabled mod and its dependents.") : L.Text("Needs Gateway running, an enabled built mod, and a client in a playable area (Python + Frida).");
        BuildModButton.ToolTip = selected?.Project.Length > 0 ? L.Text("Build this source project.") : L.Text("This mod has no source project in the selected checkout.");
        ModDetails.Text = selected is null ? L.Text("Select a mod to view its dependencies and controls.")
            : L.Text("{0} · {1}\nDependencies: {2}\n{3}\n{4}", selected.Id, selected.Summary, (selected.Depends.Length == 0 ? L.Text("none") : string.Join(", ", selected.Depends)), selected.DisplayDetail, (selected.Project.Length > 0 ? L.Text("Source: ") + selected.Project : L.Text("Binary-only mod")));
        ModCommandHint.Text = selected is null ? L.Text("In game: /mod lists runtime status.")
            : L.Text("In game: /mod_{0} {1} · /mod_reload {2}", (selected.Enabled ? "disable" : "enable"), selected.Id, selected.Id);
    }
    async void ModsBackupPreference_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || updatingPreferences || updatingModPreferences) return;
        SaveDevelopmentPreferences(ModsAutoBackupBox.IsChecked == true, settings.HotReload);
        await RefreshAsync();
    }
    async void CheckMods_Click(object sender, RoutedEventArgs e) => await OperateAsync("QueryMods");
    async void BuildAllMods_Click(object sender, RoutedEventArgs e) => await OperateAsync("BuildMods");
    async void BuildMod_Click(object sender, RoutedEventArgs e)
    { if (ModsList.SelectedItem is ModEntry selected) await OperateAsync("BuildMod", mod: new(selected.Id, selected.Project)); }
    async void ToggleMod_Click(object sender, RoutedEventArgs e)
    { if (ModsList.SelectedItem is ModEntry selected) await OperateAsync(selected.Enabled ? "DisableMod" : "EnableMod", mod: new(selected.Id)); }
    async void ReloadMod_Click(object sender, RoutedEventArgs e)
    { if (ModsList.SelectedItem is ModEntry selected) await OperateAsync("ReloadMod", mod: new(selected.Id)); }
    void OpenMods_Click(object sender, RoutedEventArgs e)
    { if (snapshot?.ModEnvironment is { Supported: true } environment && Directory.Exists(environment.Folder) && ModCatalog.Physical(environment.Folder)) OpenFolder(environment.Folder); }
    void OpenSelectedMod_Click(object sender, RoutedEventArgs e)
    { if (ModsList.SelectedItem is ModEntry selected && Directory.Exists(selected.Folder) && ModCatalog.Physical(selected.Folder)) OpenFolder(selected.Folder); }
}
