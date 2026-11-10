using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace Waypoint;

/// <summary>The Settings section: the language, what exiting does, updates and the checkout. Every choice is saved as it is
/// made. Linux has no tray for Waypoint to close to, and its appearance is the one in App.axaml, so the Windows section's
/// tray choices and appearance editor are not here.</summary>
partial class SettingsTab : UserControl
{
    Shared _shared = null!;
    bool _loading;

    /// <summary>Check for updates: the window has the update card.</summary>
    public event Action? CheckUpdatesRequested;
    /// <summary>Developer Mode changed: the window shows the sections and the switch again.</summary>
    public event Action? DeveloperModeChanged;

    public SettingsTab() => InitializeComponent();

    Settings S => _shared.Settings;

    public void Attach(Shared shared)
    {
        _shared = shared;
        ServerFolder.Text = Environment.CurrentDirectory;
        ToolTip.SetTip(ServerFolder, Environment.CurrentDirectory);
        foreach (var language in L.Languages)
        {
            var choice = new RadioButton { Content = language.Name, Tag = language.Tag, GroupName = "Language" };
            choice.IsCheckedChanged += LanguageChanged;
            LanguageChoices.Children.Add(choice);
        }
        L.Current.PropertyChanged += (_, _) => Show();
        L.Current.PropertyChanged += (_, _) => ShowPaths();
        Show();
        ShowPaths();
    }

    void Show()
    {
        _loading = true;
        ShowTexts();
        foreach (RadioButton choice in LanguageChoices.Children) choice.IsChecked = (string)choice.Tag! == L.Current.Language;
        StopServices.IsChecked = S.StopServicesOnExit;
        LeaveServices.IsChecked = !S.StopServicesOnExit;
        ShowServicesHelp();
        AlwaysAsk.IsChecked = S.AlwaysAskOnClose;
        CheckAtStart.IsChecked = S.CheckForUpdates;
        KeepUpToDate.IsChecked = S.KeepUpToDate;
        UseProfile.IsChecked = S.UseLocalProfile;
        DeveloperMode.IsChecked = S.Developer;
        // Developer Mode is a release's: there it unlocks the switch. A source copy is in Advanced mode whatever it says,
        // and nothing shown there writes the mode chosen on a release.
        DeveloperGroup.IsVisible = Repo.IsInstall;
        DeveloperNote.Text = L.Text("Adds a Simple | Advanced switch to the sidebar. Advanced replaces Play with Control, and adds Versions, performance and server timings.");
        // A source copy is updated from Versions, by its user: nothing moves it by itself any more.
        KeepUpToDate.IsVisible = Repo.IsInstall;
        ToolTip.SetTip(KeepUpToDate, Repo.IsInstall ? L.Text("When Waypoint starts, look for a newer LocalExile release and offer to install it.")
            : L.Text("Play gets the newest LocalExile at most once a day, after backing up your saves. A copy with your own changes is left alone."));
        CheckoutGroup.IsVisible = Editions.Advanced(S) && !Repo.IsInstall;
        _loading = false;
    }

    void ShowTexts()
    {
        LanguageHeading.Text = L.Text("Language");
        LanguageNote.Text = L.Text("This changes Waypoint's interface. Repository names, paths, commit hashes, in-game commands and raw logs keep their original text.");
        WindowHeading.Text = L.Text("Window behavior");
        ExitHeading.Text = L.Text("When Waypoint exits");
        LeaveServices.Content = L.Text("Leave the servers and the game running");
        StopServices.Content = L.Text("Stop the servers and the game before exiting");
        AlwaysAsk.Content = L.Text("Always ask when I close the window");
        UpdatesHeading.Text = L.Text("Updates");
        CheckUpdatesButton.Content = L.Text("Check for updates");
        ToolTip.SetTip(CheckUpdatesButton, L.Text("Look for a newer Waypoint release on GitHub"));
        CheckAtStart.Content = L.Text("Look for updates at start");
        ToolTip.SetTip(CheckAtStart, L.Text("Look for a newer Waypoint release whenever it starts (release builds)"));
        KeepUpToDate.Content = L.Text("Keep LocalExile up to date");
        DeveloperHeading.Text = L.Text("Developer");
        DeveloperMode.Content = L.Text("Developer Mode");
        CheckoutHeading.Text = L.Text("Server checkout");
        PathsHeading.Text = L.Text("Folders and tools");
        GameSettingsHeading.Text = L.Text("Game settings");
        PathsNote.Text = L.Text("Leave a field empty to use the default. A folder or program that is not there is skipped, and the default is used instead.");
        UseProfile.Content = L.Text("Use the checkout's local launch profile when it has one");
        FindButton.Content = L.Text("Find automatically…");
        ChangeButton.Content = L.Text("Change…");
        ToolTip.SetTip(ChangeButton, L.Text("Pick another LocalExile server checkout; Waypoint starts again on it"));
        OpenDataButton.Content = L.Text("Open Waypoint's data folder");
        ToolTip.SetTip(OpenDataButton, L.Text("Operation logs and save backups"));
    }

    void ShowServicesHelp() => ServicesHelp.Text = L.Text(S.StopServicesOnExit
        ? "Same as Stop all: the game, the walk-map recorder, the servers and the account service."
        : "The game and services stay open after Waypoint exits.");

    // A radio button says it was checked before its group unchecks the other, so these go by the sender.
    void LanguageChanged(object? sender, RoutedEventArgs e)
    {
        if (_loading || _shared is null || sender is not RadioButton { IsChecked: true, Tag: string tag }) return;
        S.Language = tag;
        S.Save();
        L.Current.SetLanguage(S.Language);
    }

    void ServicesChanged(object? sender, RoutedEventArgs e)
    {
        if (_loading || _shared is null || sender is not RadioButton { IsChecked: true }) return;
        S.StopServicesOnExit = sender == StopServices;
        S.Save();
        ShowServicesHelp();
    }

    void AlwaysAskChanged(object? sender, RoutedEventArgs e)
    {
        if (_loading || _shared is null) return;
        S.AlwaysAskOnClose = AlwaysAsk.IsChecked == true;
        S.CloseBehaviorConfirmed = true;
        S.Save();
    }

    void CheckAtStartChanged(object? sender, RoutedEventArgs e)
    {
        if (_loading || _shared is null) return;
        S.CheckForUpdates = CheckAtStart.IsChecked == true;
        S.Save();
    }

    void KeepUpToDateChanged(object? sender, RoutedEventArgs e)
    {
        if (_loading || _shared is null) return;
        S.KeepUpToDate = KeepUpToDate.IsChecked == true;
        S.Save();
    }

    /// <summary>The sidebar's switch moved: Simple and Advanced mode show different settings.</summary>
    public void ShowMode()
    {
        if (_shared is null) return;
        Show();
        ShowPaths();
    }

    /// <summary>On, the sidebar gets its switch and Simple mode stays until it is used. Off, Waypoint goes back to Simple mode.</summary>
    void DeveloperModeClicked(object? sender, RoutedEventArgs e)
    {
        if (_loading || _shared is null || !Repo.IsInstall) return;
        S.DeveloperMode = DeveloperMode.IsChecked == true;
        if (!S.Developer) S.AdvancedMode = false;
        S.Save();
        DeveloperModeChanged?.Invoke();
    }

    void CheckUpdates(object? sender, RoutedEventArgs e) => CheckUpdatesRequested?.Invoke();

    void ProfileChanged(object? sender, RoutedEventArgs e)
    {
        if (_loading || _shared is null) return;
        S.UseLocalProfile = UseProfile.IsChecked == true;
        Repo.UseLocalProfile = S.UseLocalProfile;
        S.Save();
        _ = _shared.Workspace.RefreshAsync();
    }

    bool Busy()
    {
        if (!_shared.IsLiveRunning) return false;
        Dialog.Tell(ViewTools.Owner(this), DialogKind.Info, L.Text("{0} is still running", _shared.BusyWith), L.Text("Wait for the current operation before changing checkout."));
        return true;
    }

    async void FindCheckout(object? sender, RoutedEventArgs e)
    {
        if (Busy() || ViewTools.Owner(this) is not { } owner) return;
        var dialog = new CheckoutDialog(owner, S.ServerPath);
        await Ui.ShowAsync(dialog, owner);
        if (dialog.Chosen is not null) await App.SwitchServerAsync(owner, _shared, dialog.Chosen);
    }

    async void ChangeCheckout(object? sender, RoutedEventArgs e)
    {
        if (!Busy() && ViewTools.Owner(this) is { } owner) await App.SwitchServerAsync(owner, _shared);
    }

    void OpenData(object? sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(SettingsStore.DataDirectory);
        ViewTools.Open(SettingsStore.DataDirectory);
    }

    // ---- Folders and tools (PathChoices), as on Windows, with the Wine prefix ----

    static readonly (string Tool, string Label)[] ToolLabels = [("git", "Git"), ("dotnet", ".NET SDK"), ("node", "Node.js"), ("python", "Python"), ("powershell", "PowerShell")];

    /// <summary>A row per folder and tool: the choice, in a field that takes a typed path too (a picker may be missing here),
    /// Browse…, Save, and what is in use now under it.</summary>
    void ShowPaths()
    {
        PathRows.Children.Clear();
        GameSettingsRow.Children.Clear();
        // A power user's page: with Developer Mode, on a source copy in Advanced mode, and whenever something there is
        // chosen or cannot be used, so that what startup reports can always be changed here.
        PathsCard.IsVisible = S.Developer || !Repo.IsInstall && Editions.Advanced(S) || S.PathsChosen || Settings.PathProblems.Count > 0;
        AddPathRow(PathKind.Data, null, L.Text("Waypoint's data"), S.DataFolder);
        AddPathRow(PathKind.Backups, null, L.Text("Save backups"), S.BackupsFolder);
        AddPathRow(PathKind.WinePrefix, null, L.Text("Wine prefix"), S.WinePrefix);
        AddGameSettingsRow();
        foreach (var (tool, label) in ToolLabels) AddPathRow(PathKind.Tool, tool, label, S.Tools.GetValueOrDefault(tool));
    }

    /// <summary>LocalExile's game settings here are the Wine prefix's (GameSettings): another prefix, chosen above, is another set.</summary>
    void AddGameSettingsRow()
    {
        string folder = Path.Combine(Platform.GamePrefix, GameSettings.Documents);
        var detail = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Text = L.Text("In the Wine prefix: {0}. Another Wine prefix has settings of its own.", folder) };
        detail.Classes.Add("note");
        var open = new Button { Content = L.Text("Open"), Margin = new Thickness(8, 0, 0, 0) };
        open.Click += (_, _) => { if (Directory.Exists(folder)) ViewTools.Open(folder); };
        var line = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(open, Dock.Right);
        line.Children.Add(open);
        line.Children.Add(detail);
        GameSettingsRow.Children.Add(line);
    }

    void AddPathRow(PathKind kind, string? tool, string label, string? value)
    {
        var field = new TextBox { Text = value ?? "", Margin = new Thickness(0, 0, 8, 0) };
        var browse = new Button { Content = L.Text("Browse…"), Margin = new Thickness(0, 0, 8, 0) };
        var save = new Button { Content = L.Text("Save") };
        var detail = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 12) };
        detail.Classes.Add("note");
        detail.Text = kind == PathKind.Tool ? L.Text("Looking…") : L.Text("In use: {0}", PathChoices.Now(kind));
        if (kind == PathKind.Tool) _ = Describe();
        browse.Click += async (_, _) =>
        {
            if (Ui.Owner(this) is not { } owner) return;
            if (kind != PathKind.Tool)
            {
                if (Ui.PickFolder(owner, label) is { } folder) field.Text = folder;
                return;
            }
            try
            {
                var files = await owner.StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions { Title = label });
                if (files.FirstOrDefault()?.TryGetLocalPath() is { } program) field.Text = program;
            }
            // No portal and no GTK: the field takes a typed path.
            catch (Exception error) when (error is InvalidOperationException or NotSupportedException or IOException or UnauthorizedAccessException)
            {
                Dialog.Tell(owner, DialogKind.Warning, label, error.Message);
            }
        };
        save.Click += async (_, _) =>
        {
            var owner = Ui.Owner(this);
            int Ask(DialogKind k, string title, string message, string[] buttons) => Dialog.Show(owner, k, title, message, null, buttons);
            string? chosen = field.Text?.Trim() is { Length: > 0 } text ? text : null;
            if (kind == PathKind.Tool)
            {
                if (await PathChoices.ChangeToolAsync(S, tool!, chosen, Ask)) await Describe();
            }
            else if (kind == PathKind.WinePrefix)
            {
                PathChoices.ChangeWinePrefix(S, chosen, Ask);
                detail.Text = L.Text("In use: {0}", PathChoices.Now(kind));
            }
            else if (await PathChoices.ChangeFolderAsync(_shared, kind, chosen, Ask, ViewTools.Open) && owner is not null) App.Restart(owner);
        };
        var line = new DockPanel();
        DockPanel.SetDock(save, Dock.Right);
        DockPanel.SetDock(browse, Dock.Right);
        line.Children.Add(save);
        line.Children.Add(browse);
        line.Children.Add(field);
        PathRows.Children.Add(new TextBlock { Text = label, FontWeight = Avalonia.Media.FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        PathRows.Children.Add(line);
        PathRows.Children.Add(detail);

        async Task Describe() => detail.Text = await PathChoices.DescribeToolAsync(S, tool!);
    }
}
