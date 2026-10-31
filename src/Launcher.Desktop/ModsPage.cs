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
        { modEntries = []; ModsStatus.Text = "Could not inspect mods: " + ex.Message; UpdateModSelection(); return; }
        var gateway = state.Services.FirstOrDefault(s => s.Name == "Gateway");
        ModsStatus.Text = !supported ? "This revision has no supported mod host. Older LocalExile revisions can still be launched normally."
            : $"{modEntries.Length} mods · {(state.ModEnvironment!.Enabled ? "Mods enabled" : "Mods off in launch profile (Game_MODS=0)")}\n{state.ModEnvironment.Folder}";
        bool canUse = supported && !busy && state.Conflicts.Length == 0;
        BuildAllModsButton.IsEnabled = canUse && modEntries.Any(e => e.Project.Length > 0);
        OpenModsButton.IsEnabled = supported && Directory.Exists(state.ModEnvironment!.Folder) && ModCatalog.Physical(state.ModEnvironment.Folder);
        OpenModsButton.ToolTip = OpenModsButton.IsEnabled ? state.ModEnvironment?.Folder : "Build source mods or install a local mod to create this folder.";
        CheckModsButton.IsEnabled = canUse && gateway?.Status == "Ready" && state.GameRunning;
        ApplyModFilter();
    }
    void ApplyModFilter()
    {
        if (ModsList is null || ModSearch is null) return;
        string? selected = (ModsList.SelectedItem as ModEntry)?.Id;
        ModsList.ItemsSource = modEntries.Where(e => e.SearchText.Contains(ModSearch.Text.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
        if (selected is not null) ModsList.SelectedItem = modEntries.FirstOrDefault(e => e.Id == selected);
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
        bool live = gateway?.Status == "Ready" && snapshot?.GameRunning == true && snapshot.ModEnvironment!.Enabled;
        ToggleModButton.Content = selected?.Enabled == true ? "Disable mod" : "Enable mod";
        ToggleModButton.IsEnabled = canUse && selected!.Fingerprint.Length > 0 && (gateway?.IsRunning != true || live);
        BuildModButton.IsEnabled = canUse && selected!.Project.Length > 0;
        ReloadModButton.IsEnabled = canUse && live && selected!.Enabled && selected.Fingerprint.Length > 0;
        OpenSelectedModButton.IsEnabled = selected is not null && Directory.Exists(selected.Folder) && ModCatalog.Physical(selected.Folder);
        ModDetails.Text = selected is null ? "Select a mod to view its dependencies and controls."
            : $"{selected.Id} · {selected.Summary}\nDependencies: {(selected.Depends.Length == 0 ? "none" : string.Join(", ", selected.Depends))}\n{selected.Detail}\n{(selected.Project.Length > 0 ? "Source: " + selected.Project : "Binary-only mod")}";
        ModCommandHint.Text = selected is null ? "In game: /mod lists runtime status."
            : $"In game: /mod_{(selected.Enabled ? "disable" : "enable")} {selected.Id} · /mod_reload {selected.Id}";
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
