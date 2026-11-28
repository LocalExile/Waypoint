using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Launcher.Core.NativeMods;
using LocalExile.Client.Waypoint;

namespace Waypoint;

/// <summary>The Settings section: the appearance, the language, what closing the window does, updates and the checkout.
/// Every choice is saved as it is made.</summary>
public partial class SettingsTab : UserControl
{
    Shared _shared = null!;
    bool _loading;

    /// <summary>Check for updates: the window has the update card.</summary>
    internal event Action? CheckUpdatesRequested;
    /// <summary>Developer Mode changed: the window shows the sections and the switch again.</summary>
    internal event Action? DeveloperModeChanged;

    public SettingsTab() => InitializeComponent();

    Settings S => _shared.Settings;

    internal void Attach(Shared shared)
    {
        _shared = shared;
        ServerFolder.Text = Environment.CurrentDirectory;
        ServerFolder.ToolTip = Environment.CurrentDirectory;
        foreach (var language in L.Languages)
        {
            // Its own language too, so that its glyphs are that language's whatever the interface shows.
            var choice = new RadioButton
            {
                Content = language.Name, Tag = language.Tag, GroupName = "Language", Style = (Style)FindResource("ChoiceRadio"),
                Margin = new Thickness(0, 0, 20, 0), Language = System.Windows.Markup.XmlLanguage.GetLanguage(language.Tag),
            };
            choice.Checked += LanguageChanged;
            LanguageChoices.Children.Add(choice);
        }
        Theme.Changed += ShowAppearance;
        Theme.Changed += () => _ = RefreshNativeModsAsync();
        L.Current.PropertyChanged += (_, _) => Show();
        L.Current.PropertyChanged += (_, _) => ShowPaths();
        L.Current.PropertyChanged += (_, _) => _ = RefreshNativeModsAsync();
        ShowPaths();
        Show();
        _ = RefreshNativeModsAsync();
    }

    void Show()
    {
        _loading = true;
        ShowAppearance();
        foreach (RadioButton choice in LanguageChoices.Children) choice.IsChecked = (string)choice.Tag == L.Current.Language;
        WindowChoices.Load(S.CloseToTray, S.StopServicesOnExit);
        AlwaysAsk.IsChecked = S.AlwaysAskOnClose;
        TrayWhileGame.IsChecked = S.TrayWhileGameRuns;
        CheckAtStart.IsChecked = S.CheckForUpdates;
        KeepUpToDate.IsChecked = S.KeepUpToDate;
        UseProfile.IsChecked = S.UseLocalProfile;
        DeveloperMode.IsChecked = S.Developer;
        // Versions of the game come from Steam for a release (ClientVersionsWindow), where the tool for it runs.
        ClientSwitchGroup.Visibility = Repo.IsInstall && DepotTool.Supported ? Visibility.Visible : Visibility.Collapsed;
        SwitchKeepBoth.IsChecked = S.ClientSwitch == nameof(SwitchWay.KeepBoth);
        SwitchInPlace.IsChecked = S.ClientSwitch == nameof(SwitchWay.UpdateInPlace);
        SwitchAsk.IsChecked = SwitchKeepBoth.IsChecked != true && SwitchInPlace.IsChecked != true;
        // Developer Mode is a release's: there it unlocks the switch. A source copy is in Advanced mode whatever it says,
        // and nothing shown there writes the mode chosen on a release.
        DeveloperGroup.Visibility = Repo.IsInstall ? Visibility.Visible : Visibility.Collapsed;
        DeveloperNote.Text = L.Text("Adds a Simple | Advanced switch to the sidebar. Advanced replaces Play with Control, and adds Versions, performance and server timings.");
        // A source copy is updated from Versions, by its user: nothing moves it by itself any more.
        KeepUpToDate.Visibility = Repo.IsInstall ? Visibility.Visible : Visibility.Collapsed;
        if (Repo.IsInstall) KeepUpToDate.ToolTip = L.Text("When Waypoint starts, look for a newer LocalExile release and offer to install it.");
        CheckoutGroup.Visibility = Editions.Advanced(S) && !Repo.IsInstall ? Visibility.Visible : Visibility.Collapsed;
        var nm = S.NativeModsFor();
        NativeModsEnabled.IsChecked = nm.Enabled;
        bool useImport = !Repo.IsInstall && NativeModsExtender.ParseMode(nm.Mode) == ExtenderInstallMode.ImportTable;
        NativeModsProxyMode.IsChecked = !useImport;
        NativeModsImportMode.IsChecked = useImport;
        NativeModsImportMode.IsEnabled = !Repo.IsInstall;
        _loading = false;
    }

    void ShowAppearance()
    {
        var value = Theme.Current;
        string scheme = value.Scheme == "custom" ? L.Text("Custom") : L.Text(ThemePalette.Schemes.First(s => s.Id == value.Scheme).Name);
        AppearanceSummary.Text = L.Text("{0} · {1} · {2}% text", scheme, L.Text(value.Mode), value.TextScale);
    }

    void ChangeAppearance(object sender, RoutedEventArgs e) =>
        new AppearanceDialog(Window.GetWindow(this), Theme.Current, value =>
        {
            S.Appearance = ThemePalette.Normalize(value);
            S.Save();
            Theme.Apply(S.Appearance);
        }).ShowDialog();

    void LanguageChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || _shared is null) return;
        S.Language = (string)((RadioButton)sender).Tag;
        S.Save();
        L.Current.SetLanguage(S.Language);
    }

    void WindowChoicesChanged(bool closeChoice)
    {
        if (_loading || _shared is null) return;
        S.CloseToTray = WindowChoices.CloseToTray;
        S.StopServicesOnExit = WindowChoices.StopServicesOnExit;
        S.CloseBehaviorConfirmed = closeChoice || S.CloseBehaviorConfirmed;
        S.Save();
    }

    void AlwaysAskChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || _shared is null) return;
        S.AlwaysAskOnClose = AlwaysAsk.IsChecked == true;
        S.CloseBehaviorConfirmed = true;
        S.Save();
    }

    void TrayChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || _shared is null) return;
        S.TrayWhileGameRuns = TrayWhileGame.IsChecked == true;
        S.Save();
    }

    void ClientSwitchChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || _shared is null) return;
        S.ClientSwitch = SwitchKeepBoth.IsChecked == true ? nameof(SwitchWay.KeepBoth) : SwitchInPlace.IsChecked == true ? nameof(SwitchWay.UpdateInPlace) : "";
        S.Save();
    }

    void CheckAtStartChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || _shared is null) return;
        S.CheckForUpdates = CheckAtStart.IsChecked == true;
        S.Save();
    }

    void KeepUpToDateChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || _shared is null) return;
        S.KeepUpToDate = KeepUpToDate.IsChecked == true;
        S.Save();
    }

    /// <summary>The sidebar's switch moved: Simple and Advanced mode show different settings.</summary>
    internal void ShowMode()
    {
        if (_shared is not null) { Show(); ShowPaths(); }
    }

    /// <summary>On, the sidebar gets its switch and Simple mode stays until it is used. Off, Waypoint goes back to Simple mode.</summary>
    void DeveloperModeClicked(object sender, RoutedEventArgs e)
    {
        if (_loading || _shared is null || !Repo.IsInstall) return;
        S.DeveloperMode = DeveloperMode.IsChecked == true;
        if (!S.Developer) S.AdvancedMode = false;
        S.Save();
        DeveloperModeChanged?.Invoke();
    }

    void CheckUpdates(object sender, RoutedEventArgs e) => CheckUpdatesRequested?.Invoke();

    void ProfileChanged(object sender, RoutedEventArgs e)
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
        Dialog.Tell(Window.GetWindow(this), DialogKind.Info, L.Text("{0} is still running", _shared.BusyWith), L.Text("Wait for the current operation before changing checkout."));
        return true;
    }

    void FindCheckout(object sender, RoutedEventArgs e)
    {
        if (Busy()) return;
        var owner = Window.GetWindow(this);
        var dialog = new CheckoutDialog(owner, S.ServerPath);
        if (dialog.ShowDialog() == true) _ = App.SwitchServerAsync(owner, _shared, dialog.Chosen);
    }

    void ChangeCheckout(object sender, RoutedEventArgs e)
    {
        if (!Busy()) _ = App.SwitchServerAsync(Window.GetWindow(this), _shared);
    }

    void OpenData(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(SettingsStore.DataDirectory);
        OpenFolder(SettingsStore.DataDirectory);
    }

    static void OpenFolder(string folder) =>
        Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { folder }, UseShellExecute = false })?.Dispose();

    // ---- Folders and tools (PathChoices) ----

    static readonly (string Tool, string Label)[] ToolLabels = [("git", "Git"), ("dotnet", ".NET SDK"), ("node", "Node.js"), ("python", "Python"), ("powershell", "PowerShell")];

    /// <summary>A row per folder and tool: the choice, in a field that takes a typed path too, Browse…, Save, and what is in use
    /// now under it.</summary>
    void ShowPaths()
    {
        PathRows.Children.Clear();
        GameSettingsRow.Children.Clear();
        // A power user's page: with Developer Mode, and whenever something there is chosen or cannot be used, so that what
        // startup reports can always be changed here.
        PathsGroup.Visibility = S.Developer || !Repo.IsInstall && Editions.Advanced(S) || S.PathsChosen || Settings.PathProblems.Count > 0
            ? Visibility.Visible : Visibility.Collapsed;
        AddPathRow(PathKind.Data, null, L.Text("Waypoint's data"), S.DataFolder);
        AddPathRow(PathKind.Backups, null, L.Text("Save backups"), S.BackupsFolder);
        AddGameSettingsRow();
        foreach (var (tool, label) in ToolLabels) AddPathRow(PathKind.Tool, tool, label, S.Tools.GetValueOrDefault(tool));
    }

    int Ask(DialogKind kind, string title, string message, string[] buttons) => Dialog.Show(Window.GetWindow(this), kind, title, message, null, buttons);

    /// <summary>LocalExile's game settings profile: the one in use, and Manage… for the Profiles window, where they are switched,
    /// made, renamed and removed (as on Play).</summary>
    void AddGameSettingsRow()
    {
        bool client = Directory.Exists("Client");
        string? current = PathChoices.GameSettingsFolder();
        var name = new TextBlock { Text = current ?? L.Text("Your normal settings"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        var manage = new Button { Content = L.Text("Manage…"), Margin = new Thickness(0, 0, 8, 0), IsEnabled = client };
        var open = new Button { Content = L.Text("Open"), Margin = new Thickness(0) };
        var detail = new TextBlock { Style = (Style)FindResource("Shell.Secondary"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 12),
            Text = PathChoices.GameSettingsDetail() };
        manage.Click += (_, _) =>
        {
            new ProfilesWindow(Window.GetWindow(this), _shared).ShowDialog();
            ShowPaths();
        };
        open.Click += (_, _) =>
        {
            string folder = current is not null ? Path.Combine(ClientSettings.Documents, current) : ClientSettings.Documents;
            if (Directory.Exists(folder)) OpenFolder(folder);
        };
        var line = new DockPanel();
        DockPanel.SetDock(open, Dock.Right);
        DockPanel.SetDock(manage, Dock.Right);
        line.Children.Add(open);
        line.Children.Add(manage);
        line.Children.Add(name);
        GameSettingsRow.Children.Add(line);
        GameSettingsRow.Children.Add(detail);
    }

    void AddPathRow(PathKind kind, string? tool, string label, string? value)
    {
        var field = new TextBox { Text = value ?? "", Margin = new Thickness(0, 0, 8, 0) };
        // A one-line field has nothing to scroll, but its own scroll viewer keeps the wheel: the page gets it instead.
        field.PreviewMouseWheel += (_, e) =>
        {
            e.Handled = true;
            ((UIElement)field.Parent).RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
                { RoutedEvent = MouseWheelEvent, Source = field });
        };
        var browse = new Button { Content = L.Text("Browse…"), Margin = new Thickness(0, 0, 8, 0) };
        var save = new Button { Content = L.Text("Save"), Margin = new Thickness(0) };
        var detail = new TextBlock { Style = (Style)FindResource("Shell.Secondary"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 12) };
        detail.Text = kind == PathKind.Tool ? L.Text("Looking…") : L.Text("In use: {0}", PathChoices.Now(kind));
        if (kind == PathKind.Tool) _ = Describe();
        browse.Click += (_, _) =>
        {
            var owner = Window.GetWindow(this);
            if (kind == PathKind.Tool)
            {
                var pick = new Microsoft.Win32.OpenFileDialog { Title = label, Filter = $"{label} (*.exe)|*.exe" };
                if (pick.ShowDialog(owner) == true) field.Text = pick.FileName;
            }
            else
            {
                var pick = new Microsoft.Win32.OpenFolderDialog { Title = label };
                if (pick.ShowDialog(owner) == true) field.Text = pick.FolderName;
            }
        };
        save.Click += async (_, _) =>
        {
            var owner = Window.GetWindow(this);
            int Ask(DialogKind k, string title, string message, string[] buttons) => Dialog.Show(owner, k, title, message, null, buttons);
            string? chosen = field.Text.Trim() is { Length: > 0 } text ? text : null;
            if (kind == PathKind.Tool)
            {
                if (await PathChoices.ChangeToolAsync(S, tool!, chosen, Ask)) await Describe();
            }
            else if (await PathChoices.ChangeFolderAsync(_shared, kind, chosen, Ask, OpenFolder)) App.StartAgain(owner);
        };
        var line = new DockPanel();
        DockPanel.SetDock(save, Dock.Right);
        DockPanel.SetDock(browse, Dock.Right);
        line.Children.Add(save);
        line.Children.Add(browse);
        line.Children.Add(field);
        PathRows.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        PathRows.Children.Add(line);
        PathRows.Children.Add(detail);

        async Task Describe() => detail.Text = await PathChoices.DescribeToolAsync(S, tool!);
    }

    // ---- NativeMods client extender (opt-in, OFF by default) ----

    bool _nativeModsBusy;

    async Task RefreshNativeModsAsync()
    {
        if (_shared is null) return;
        var pref = S.NativeModsFor();
        var mode = Repo.IsInstall ? ExtenderInstallMode.ProxyVersionDll : NativeModsExtender.ParseMode(pref.Mode);
        var health = await Task.Run(() => NativeModsExtender.GetHealthAsync(Environment.CurrentDirectory, pref.Enabled, Repo.IsInstall, mode));
        ApplyNativeModsHealth(health);
    }

    void ApplyNativeModsHealth(NativeModsHealth health)
    {
        var (summary, brushKey, details) = NativeModsLaunch.DescribeHealth(health);
        NativeModsStatus.Text = summary;
        NativeModsStatus.Foreground = Theme.Brush(brushKey);
        NativeModsDetails.Text = details;
        NativeModsDetails.Visibility = details.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        SetNativeModsButtonsEnabled(!_nativeModsBusy, health.IsInstalled);
    }

    void SetNativeModsButtonsEnabled(bool enabled, bool isInstalled)
    {
        NativeModsEnabled.IsEnabled = enabled;
        NativeModsProxyMode.IsEnabled = enabled;
        NativeModsImportMode.IsEnabled = enabled && !Repo.IsInstall;
        NativeModsGitHubButton.IsEnabled = enabled;
        NativeModsZipButton.IsEnabled = enabled;
        NativeModsFolderButton.IsEnabled = enabled;
        NativeModsVerifyButton.IsEnabled = enabled;
        NativeModsUninstallButton.IsEnabled = enabled && isInstalled;
    }

    async void NativeModsPreferenceChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || _shared is null || _nativeModsBusy) return;
        if (NativeModsLaunch.CannotMutateNow(_shared) is { } blocked)
        {
            Dialog.Tell(Window.GetWindow(this), DialogKind.Info, L.Text("NativeMods (Client Extender)"), blocked);
            Show();
            return;
        }
        bool enabled = NativeModsEnabled.IsChecked == true;
        var mode = !Repo.IsInstall && NativeModsImportMode.IsChecked == true
            ? ExtenderInstallMode.ImportTable
            : ExtenderInstallMode.ProxyVersionDll;
        _nativeModsBusy = true;
        SetNativeModsButtonsEnabled(false, true);
        try
        {
            var result = await NativeModsLaunch.ApplyPreferenceAsync(_shared, enabled, mode);
            if (result is not null)
            {
                if (!result.Succeeded) Dialog.Tell(Window.GetWindow(this), DialogKind.Warning, L.Text("NativeMods (Client Extender)"), result.Message);
                ApplyNativeModsHealth(result.Health);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            Dialog.Tell(Window.GetWindow(this), DialogKind.Warning, L.Text("NativeMods (Client Extender)"), ex.Message);
        }
        finally
        {
            _nativeModsBusy = false;
            Show();
            await RefreshNativeModsAsync();
        }
    }

    async void InstallNativeModsGitHub(object sender, RoutedEventArgs e)
    {
        if (_shared is null || _nativeModsBusy) return;
        if (NativeModsLaunch.CannotMutateNow(_shared) is { } blocked)
        {
            Dialog.Tell(Window.GetWindow(this), DialogKind.Info, L.Text("NativeMods (Client Extender)"), blocked);
            return;
        }
        _nativeModsBusy = true;
        SetNativeModsButtonsEnabled(false, false);
        try
        {
            var result = await NativeModsLaunch.InstallFromGitHubAsync(_shared, text => Dispatcher.BeginInvoke(() => NativeModsStatus.Text = text));
            if (result is not null)
            {
                Dialog.Tell(Window.GetWindow(this), result.Succeeded ? DialogKind.Info : DialogKind.Warning, L.Text("NativeMods (Client Extender)"), result.Message);
                ApplyNativeModsHealth(result.Health);
            }
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or System.Text.Json.JsonException or TaskCanceledException)
        {
            Dialog.Tell(Window.GetWindow(this), DialogKind.Warning, L.Text("NativeMods (Client Extender)"), ex.Message);
        }
        finally
        {
            _nativeModsBusy = false;
            await RefreshNativeModsAsync();
        }
    }

    async void ImportNativeModsZip(object sender, RoutedEventArgs e)
    {
        if (_shared is null || _nativeModsBusy) return;
        if (NativeModsLaunch.CannotMutateNow(_shared) is { } blocked)
        {
            Dialog.Tell(Window.GetWindow(this), DialogKind.Info, L.Text("NativeMods (Client Extender)"), blocked);
            return;
        }
        var pick = new Microsoft.Win32.OpenFileDialog { Title = L.Text("Choose a NativeMods release archive (.zip)"), Filter = "Zip archives (*.zip)|*.zip" };
        if (pick.ShowDialog(Window.GetWindow(this)) != true) return;
        await RunLocalNativeModsInstallAsync(pick.FileName, isZip: true);
    }

    async void ImportNativeModsFolder(object sender, RoutedEventArgs e)
    {
        if (_shared is null || _nativeModsBusy) return;
        if (NativeModsLaunch.CannotMutateNow(_shared) is { } blocked)
        {
            Dialog.Tell(Window.GetWindow(this), DialogKind.Info, L.Text("NativeMods (Client Extender)"), blocked);
            return;
        }
        var pick = new Microsoft.Win32.OpenFolderDialog { Title = L.Text("Choose a NativeMods dist/win-x64 folder") };
        if (pick.ShowDialog(Window.GetWindow(this)) != true) return;
        await RunLocalNativeModsInstallAsync(pick.FolderName, isZip: false);
    }

    async Task RunLocalNativeModsInstallAsync(string path, bool isZip)
    {
        _nativeModsBusy = true;
        SetNativeModsButtonsEnabled(false, false);
        try
        {
            var result = await NativeModsLaunch.InstallFromLocalAsync(_shared, path, isZip, text => Dispatcher.BeginInvoke(() => NativeModsStatus.Text = text));
            if (result is not null)
            {
                Dialog.Tell(Window.GetWindow(this), result.Succeeded ? DialogKind.Info : DialogKind.Warning, L.Text("NativeMods (Client Extender)"), result.Message);
                ApplyNativeModsHealth(result.Health);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            Dialog.Tell(Window.GetWindow(this), DialogKind.Warning, L.Text("NativeMods (Client Extender)"), ex.Message);
        }
        finally
        {
            _nativeModsBusy = false;
            await RefreshNativeModsAsync();
        }
    }

    async void VerifyNativeMods(object sender, RoutedEventArgs e)
    {
        if (_shared is null || _nativeModsBusy) return;
        await RefreshNativeModsAsync();
    }

    async void UninstallNativeMods(object sender, RoutedEventArgs e)
    {
        if (_shared is null || _nativeModsBusy) return;
        if (NativeModsLaunch.CannotMutateNow(_shared) is { } blocked)
        {
            Dialog.Tell(Window.GetWindow(this), DialogKind.Info, L.Text("NativeMods (Client Extender)"), blocked);
            return;
        }
        int answer = Dialog.Show(Window.GetWindow(this), DialogKind.Warning,
            L.Text("Uninstall NativeMods extender?"),
            L.Text("This removes LocalExile.ClientHost.dll, version.dll, and Client/nativemods-host/, and restores Game.patched.exe if import-patched. Stock Game.exe is never touched."),
            null,
            [L.Text("Uninstall extender only"), L.Text("Uninstall and archive client mods"), L.Text("Cancel")],
            defaultButton: 2);
        if (answer is not (0 or 1)) return;
        _nativeModsBusy = true;
        SetNativeModsButtonsEnabled(false, true);
        try
        {
            var result = await NativeModsLaunch.UninstallAsync(_shared, removeClientMods: answer == 1);
            if (result is not null)
            {
                Dialog.Tell(Window.GetWindow(this), result.Succeeded ? DialogKind.Info : DialogKind.Warning, L.Text("NativeMods (Client Extender)"), result.Message);
                ApplyNativeModsHealth(result.Health);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            Dialog.Tell(Window.GetWindow(this), DialogKind.Warning, L.Text("NativeMods (Client Extender)"), ex.Message);
        }
        finally
        {
            _nativeModsBusy = false;
            Show();
            await RefreshNativeModsAsync();
        }
    }
}
