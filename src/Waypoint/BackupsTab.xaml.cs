using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;

namespace Waypoint;

/// <summary>The Backups section: consistent copies of the save database (the adapter's SaveBackup.py) with what they were
/// made from. Restoring keeps a copy of the current saves first and moves them aside; nothing is overwritten in place.</summary>
public partial class BackupsTab : UserControl
{
    Shared _shared = null!;
    bool _busy, _loading;

    public BackupsTab() => InitializeComponent();

    RuntimeSnapshot? State => _shared.Workspace.Latest;
    SaveBackup? Chosen => BackupList.SelectedItem as SaveBackup;

    internal void Attach(Shared shared)
    {
        _shared = shared;
        OutputView.Attach(shared);
        shared.Workspace.Changed += () => Dispatcher.BeginInvoke(Reload);
        shared.LiveRunning += running => Dispatcher.BeginInvoke(() =>
        {
            _busy = running;
            Toolbar.IsEnabled = !running;
            ShowSelection();
        });
        L.Current.PropertyChanged += (_, _) => Reload();
        IsVisibleChanged += async (_, e) => { if ((bool)e.NewValue) await shared.Workspace.RefreshAsync(); };
        Reload();
    }

    void Reload()
    {
        _loading = true;
        AutoBackup.IsChecked = _shared.Settings.AutomaticBackups;
        _loading = false;
        if (State is not { } state)
        {
            Status.Text = L.Text("Select and check an installation to view its backups.");
            BackupList.ItemsSource = null;
            Empty.Text = L.Text("Choose and check a workspace to see its backups.");
            Empty.Visibility = Visibility.Visible;
            ShowSelection();
            return;
        }
        try
        {
            string? selected = Chosen?.Id;
            var backups = BackupCatalog.Read(state.SaveDatabase);
            BackupList.ItemsSource = backups;
            BackupList.SelectedItem = backups.FirstOrDefault(b => b.Id == selected);
            Status.Text = L.Text("{0} backups for {1}", backups.Length, state.SaveDatabase);
            Status.ToolTip = state.SaveDatabase;
            Empty.Visibility = backups.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            Empty.Text = L.Text("No backups yet. Use Back up now to save a copy of your current progress.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            BackupList.ItemsSource = null;
            Status.Text = L.Text("Could not read backups: ") + ex.Message;
            Empty.Visibility = Visibility.Visible;
            Empty.Text = L.Text("Backup history could not be read. Check the folder and try Refresh backups.");
        }
        ShowSelection();
    }

    void SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_shared is not null) ShowSelection();
    }

    void ShowSelection()
    {
        var selected = Chosen;
        var state = State;
        Details.Text = selected?.Details ?? L.Text("Select a backup to restore or delete it.");
        RestoreButton.IsEnabled = !_busy && selected is not null && state is { GameRunning: false };
        DeleteButton.IsEnabled = !_busy && selected is not null;
        RestoreModsButton.IsEnabled = !_busy && selected?.HasModChoices == true && state?.ModEnvironment is { Supported: true }
            && state.Services.All(s => s.Name != "Gateway" || !s.IsRunning);
        Hint.Text = selected is null ? L.Text("Choose a backup to inspect its source and mod history.")
            : state?.GameRunning == true ? L.Text("Close the game before restoring saves. Stop Gateway before restoring mod choices.")
            : L.Text("Restore saves replaces progress after confirmation. Mod choices restore separately and require Gateway stopped.");
    }

    void BackupChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || _shared is null) return;
        _shared.Settings.AutomaticBackups = AutoBackup.IsChecked == true;
        _shared.Settings.Save();
    }

    async void RefreshClicked(object sender, RoutedEventArgs e) => await _shared.Workspace.RefreshAsync();

    async void BackUpNow(object sender, RoutedEventArgs e) => await _shared.Workspace.OperateAsync("Backup", L.Text("Back up now"));

    void OpenFolder(object sender, RoutedEventArgs e)
    {
        if (State is { } state && Directory.Exists(BackupCatalog.DirectoryFor(state.SaveDatabase)))
            Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { BackupCatalog.DirectoryFor(state.SaveDatabase) }, UseShellExecute = false })?.Dispose();
    }

    bool Confirm(string title, string text, string action) =>
        Dialog.Ask(Window.GetWindow(this), DialogKind.Warning, title, text, action, L.Text("Cancel"), cancelByDefault: true);

    string BackupState => _shared.Settings.AutomaticBackups ? L.Text("on") : L.Text("OFF");

    async void Restore(object sender, RoutedEventArgs e)
    {
        if (Chosen is not { } selected) return;
        if (Confirm(L.Text("Restore selected save backup"),
                L.Text("Restore saves from {0:g}?\n\n{1}\n\nOwned servers will stop. Automatic backup is {2}. Source, client and login keys stay as selected; an older save may need its original revision.",
                    selected.CreatedUtc.ToLocalTime(), selected.Identity, BackupState), L.Text("Restore saves")))
            await _shared.Workspace.OperateAsync("RestoreBackup", L.Text("Restore saves"), backupId: selected.Id);
    }

    async void Delete(object sender, RoutedEventArgs e)
    {
        if (Chosen is not { } selected) return;
        if (Confirm(L.Text("Delete selected backup"),
                L.Text("Delete only the backup from {0:g}?\n\nCurrent saves are unchanged. This backup will no longer be available to restore.", selected.CreatedUtc.ToLocalTime()),
                L.Text("Delete backup")))
            await _shared.Workspace.OperateAsync("DeleteBackup", L.Text("Delete backup"), backupId: selected.Id);
    }

    async void RestoreMods(object sender, RoutedEventArgs e)
    {
        if (Chosen is not { } selected) return;
        if (Confirm(L.Text("Restore mod choices"),
                L.Text("Restore saved mod enable/disable choices from {0:g}?\n\n{1}\n\nThis replaces the saved choices only. Mod DLLs and saves are not restored. The choices take effect at the next Gateway start. Automatic backup is {2}.",
                    selected.CreatedUtc.ToLocalTime(), selected.ModSummary, BackupState), L.Text("Restore mod choices")))
            await _shared.Workspace.OperateAsync("RestoreModChoices", L.Text("Restore mod choices"), backupId: selected.Id);
    }
}
