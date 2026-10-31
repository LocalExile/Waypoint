using System.IO;
using System.Windows;
using System.Windows.Controls;
using Launcher.Core;

namespace Launcher.Desktop;

public partial class MainWindow
{
    SourceChoice[] sourceChoices = [];
    HotReloadTracker hotReloadTracker = new();
    string? hotReloadRoot;
    bool loadingSources, updatingPreferences;

    void LoadDevelopmentPreferences()
    {
        updatingPreferences = true;
        AutoBackupBox.IsChecked = ManagerAutoBackupBox.IsChecked = ModsAutoBackupBox.IsChecked = settings.AutomaticBackups;
        HotReloadBox.IsChecked = settings.HotReload;
        AccountsView.SetRuntime(snapshot, busy, settings.AutomaticBackups);
        updatingPreferences = false;
        BackupModeDetail.Text = settings.AutomaticBackups ? L.Text("Backups are on for both current-character and fresh starts. Manage them on Backups.") : L.Text("Automatic backups are off. Back up manually on Backups before changes you may want to undo.");
    }

    void SaveDevelopmentPreferences(bool backups, bool hotReload)
    {
        if (updatingPreferences) return;
        if (busy) { LoadDevelopmentPreferences(); StatusLabel.Text = L.Text("Wait for the current operation before changing these options."); return; }
        try
        {
            var selected = settings with { AutomaticBackups = backups, HotReload = hotReload };
            SettingsStore.Save(selected);
            settings = selected;
            LoadDevelopmentPreferences();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { LoadDevelopmentPreferences(); StatusLabel.Text = L.Text("Could not save preferences: ") + ex.Message; }
    }
    void BackupPreference_Changed(object sender, RoutedEventArgs e) { if (IsLoaded) SaveDevelopmentPreferences(AutoBackupBox.IsChecked == true, settings.HotReload); }
    void ManagerBackupPreference_Changed(object sender, RoutedEventArgs e) { if (IsLoaded) SaveDevelopmentPreferences(ManagerAutoBackupBox.IsChecked == true, settings.HotReload); }
    void HotReloadPreference_Changed(object sender, RoutedEventArgs e) { if (IsLoaded) SaveDevelopmentPreferences(settings.AutomaticBackups, HotReloadBox.IsChecked == true); }

    void RenderDevelopment(RuntimeSnapshot state)
    {
        var gateway = state.Services.FirstOrDefault(s => s.Name == "Gateway");
        if (hotReloadRoot != state.Root) { hotReloadRoot = state.Root; hotReloadTracker = new(); }
        var status = hotReloadTracker.Update(gateway, LogReader.Tail(Path.Combine(state.Root, "logs", "live-gateway.log")));
        if (gateway is { WatchProcessId: > 0, ProcessId: 0 })
        {
            string errors = LogReader.Tail(Path.Combine(state.Root, "logs", "live-gateway.err"));
            status = errors.Contains(": error ", StringComparison.Ordinal) ? new("Build failed / waiting", "Watcher is alive but Gateway has no running child. Open Gateway output to inspect the build errors.")
                : new("Building / restarting", "Watcher is alive; waiting for its Gateway child. Open Gateway output if this persists.");
        }
        HotReloadStateLabel.Text = L.Text(status.State) + (gateway?.WatchProcessId > 0 ? L.Text(" · watcher active") : "");
        HotReloadDetail.Text = L.Text(status.Detail) + L.Text("\nLast reported Gateway event. Open Gateway output for details.");
        AutoBackupBox.IsEnabled = ManagerAutoBackupBox.IsEnabled = HotReloadBox.IsEnabled = !busy;
        UpdateSourceSelection();
        UpdateBackupSelection();
    }

    async Task LoadSourcesAsync(bool refresh)
    {
        if (loadingSources || string.IsNullOrWhiteSpace(settings.RepositoryPath) || closing) return;
        loadingSources = true;
        string root = settings.RepositoryPath;
        SourcesStatus.Text = L.Text("Reading local branches and GitHub PR metadata…");
        try
        {
            var result = await SourceCatalog.LoadAsync(root, refresh, discoveryCancellation.Token, local =>
            {
                if (closing || root != settings.RepositoryPath) return;
                sourceChoices = local; ApplySourceFilter();
                SourcesStatus.Text = L.Text("{0} local/fetched branches ready. Loading GitHub PRs…", local.Length);
            });
            if (closing || root != settings.RepositoryPath) return;
            sourceChoices = result.Sources;
            ApplySourceFilter();
            SourcesStatus.Text = L.Text("{0} choices. Remote branches reflect the last fetch. ", sourceChoices.Length) + result.Warning;
        }
        catch (OperationCanceledException) when (discoveryCancellation.IsCancellationRequested) { }
        catch (Exception ex) { if (!closing && root == settings.RepositoryPath) SourcesStatus.Text = L.Text("Could not load source choices: ") + ex.Message; }
        finally { loadingSources = false; }
    }
    void ApplySourceFilter()
    {
        if (SourcesList is null || SourceSearch is null) return;
        string filter = SourceSearch.Text.Trim();
        var selected = SourcesList.SelectedItem as SourceChoice;
        var matches = sourceChoices.Where(s => (s.SearchText + " " + s.DisplayDetail + " " + s.DisplayLabel).Contains(filter, StringComparison.OrdinalIgnoreCase)).ToArray();
        SourcesList.ItemsSource = matches;
        if (selected is not null) SourcesList.SelectedItem = matches.FirstOrDefault(s => s.Kind == selected.Kind && s.Value == selected.Value);
        SourcesEmpty.Visibility = matches.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    void SourceSearch_Changed(object sender, TextChangedEventArgs e) => ApplySourceFilter();
    void SourceSelection_Changed(object sender, SelectionChangedEventArgs e) => UpdateSourceSelection();
    void UpdateSourceSelection()
    {
        if (SwitchSourceButton is null) return;
        var selected = SourcesList.SelectedItem as SourceChoice;
        SelectedSourceIdentity.Value = selected is null ? L.Text("Select a revision above.") : $"{L.Text(selected.Kind)} · {selected.DisplayLabel} · {selected.Commit}";
        SwitchSourceButton.IsEnabled = !busy && selected is not null && snapshot is { GameRunning: false };
    }
    async void RefreshSources_Click(object sender, RoutedEventArgs e) => await LoadSourcesAsync(true);
    async void FetchSources_Click(object sender, RoutedEventArgs e) => await OperateAsync("Fetch");
    async void SwitchSource_Click(object sender, RoutedEventArgs e)
    {
        if (SourcesList.SelectedItem is SourceChoice source) await OperateAsync("Switch", source: new(source.Kind, source.Value, FreshSavesBox.IsChecked == true));
    }
    async void SwitchPrNumber_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(PrNumberBox.Text.Trim().TrimStart('#'), out int number) || number <= 0 || number > 999999999)
        { PrValidation.Text = L.Text("Enter a PR number from 1 to 999999999, for example 401."); PrValidation.Visibility = Visibility.Visible; PrNumberBox.Focus(); return; }
        PrValidation.Visibility = Visibility.Collapsed;
        // Selecting a number previews it first; switching uses the same reviewed options as any other entry.
        var item = sourceChoices.FirstOrDefault(s => s.Kind == "PR" && s.Value == number.ToString())
            ?? new SourceChoice("PR", number.ToString(), $"PR #{number} (fetched when activated)", "", "Upstream LocalExile PR head; may be closed or unmerged");
        sourceChoices = sourceChoices.Where(s => s.Kind != "PR" || s.Value != item.Value).Append(item).ToArray();
        SourceSearch.Text = "";
        ApplySourceFilter(); SourcesList.SelectedItem = item; SourcesList.ScrollIntoView(item);
        await Task.CompletedTask;
    }
    void ShowBackups_Click(object sender, RoutedEventArgs e) { Pages.SelectedItem = BackupsPage; RefreshBackups(); }
    void RefreshBackups_Click(object sender, RoutedEventArgs e) => RefreshBackups();
    void RefreshBackups()
    {
        if (snapshot is null) { BackupsStatus.Text = L.Text("Select and check an installation to view its backups."); BackupsList.ItemsSource = null; BackupsEmpty.Visibility = Visibility.Visible; BackupsEmpty.Text = L.Text("Choose and check a workspace to see its backups."); UpdateBackupSelection(); return; }
        try
        {
            var selected = BackupsList.SelectedItem as SaveBackup;
            var backups = BackupCatalog.Read(snapshot.SaveDatabase);
            BackupsList.ItemsSource = backups;
            if (selected is not null) BackupsList.SelectedItem = backups.FirstOrDefault(b => b.Id == selected.Id);
            BackupsStatus.Text = L.Text("{0} backups for {1}", backups.Length, snapshot.SaveDatabase);
            BackupsEmpty.Visibility = backups.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            BackupsEmpty.Text = L.Text("No backups yet. Use Back up now to save a copy of your current progress.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { BackupsList.ItemsSource = null; BackupsStatus.Text = L.Text("Could not read backups: ") + ex.Message; BackupsEmpty.Visibility = Visibility.Visible; BackupsEmpty.Text = L.Text("Backup history could not be read. Check the folder and try Refresh backups."); }
        UpdateBackupSelection();
    }
    void BackupSelection_Changed(object sender, SelectionChangedEventArgs e) => UpdateBackupSelection();
    void UpdateBackupSelection()
    {
        if (RestoreBackupButton is null) return;
        var selected = BackupsList.SelectedItem as SaveBackup;
        BackupDetails.Value = selected?.Details ?? L.Text("Select a backup to restore or delete it.");
        RestoreBackupButton.IsEnabled = !busy && selected is not null && snapshot is { GameRunning: false };
        DeleteBackupButton.IsEnabled = !busy && selected is not null;
        RestoreModChoicesButton.IsEnabled = !busy && selected?.HasModChoices == true && snapshot?.ModEnvironment is { Supported: true }
            && snapshot.Services.All(s => s.Name != "Gateway" || !s.IsRunning);
        BackupActionHint.Text = selected is null ? L.Text("Choose a backup to inspect its source and mod history.")
            : snapshot?.GameRunning == true ? L.Text("Close the game before restoring saves. Stop Gateway before restoring mod choices.")
            : L.Text("Restore saves replaces progress after confirmation. Mod choices restore separately and require Gateway stopped.");
    }
    async void BackupNow_Click(object sender, RoutedEventArgs e) => await OperateAsync("Backup");
    void OpenBackupFolder_Click(object sender, RoutedEventArgs e)
    {
        if (snapshot is not null) OpenFolder(BackupCatalog.DirectoryFor(snapshot.SaveDatabase));
    }
    async void RestoreBackup_Click(object sender, RoutedEventArgs e)
    {
        if (busy || BackupsList.SelectedItem is not SaveBackup selected) return;
        if (new ConfirmationDialog(this, L.Text("Restore saves from {0:g}?\n\n{1}\n\nOwned servers will stop. Automatic backup is {2}. Source, client and login keys stay as selected; an older save may need its original revision.", selected.CreatedUtc.ToLocalTime(), selected.Identity, (settings.AutomaticBackups ? L.Text("on") : L.Text("OFF"))), L.Text("Restore selected save backup")).ShowDialog() == true)
            await OperateAsync("RestoreBackup", backupId: selected.Id);
    }
    async void DeleteBackup_Click(object sender, RoutedEventArgs e)
    {
        if (busy || BackupsList.SelectedItem is not SaveBackup selected) return;
        if (new ConfirmationDialog(this, L.Text("Delete only the backup from {0:g}?\n\nCurrent saves are unchanged. This backup will no longer be available to restore.", selected.CreatedUtc.ToLocalTime()), L.Text("Delete selected backup")).ShowDialog() == true)
            await OperateAsync("DeleteBackup", backupId: selected.Id);
    }
    void GatewayLog_Click(object sender, RoutedEventArgs e) { logSource = "Gateway"; RefreshLog(); Pages.SelectedItem = LogsPage; }
    async void RestartGateway_Click(object sender, RoutedEventArgs e) => await OperateAsync("Restart", "Gateway");
    async void ReloadData_Click(object sender, RoutedEventArgs e) => await OperateAsync("ReloadData");
    async void RestoreModChoices_Click(object sender, RoutedEventArgs e)
    {
        if (busy || BackupsList.SelectedItem is not SaveBackup selected) return;
        if (new ConfirmationDialog(this, L.Text("Restore saved mod enable/disable choices from {0:g}?\n\n{1}\n\nThis replaces the saved choices only. Mod DLLs and saves are not restored. The choices take effect at the next Gateway start. Automatic backup is {2}.", selected.CreatedUtc.ToLocalTime(), selected.ModSummary, (settings.AutomaticBackups ? L.Text("on") : L.Text("OFF"))), L.Text("Restore mod choices")).ShowDialog() == true)
            await OperateAsync("RestoreModChoices", backupId: selected.Id);
    }
}
