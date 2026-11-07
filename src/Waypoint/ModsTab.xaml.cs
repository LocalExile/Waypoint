using System.Diagnostics;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;

namespace Waypoint;

/// <summary>The Mods section, in two pages. Mods: the checkout's mods (ModCatalog reads their manifests and the server's saved
/// choices), on and off, their details and their settings (ModSettingsForm, written through the adapter). Browse: a mod
/// repository's index (the official LocalExile-Mods, and those the player adds), with Install and Update (ModBrowser). With a
/// client in a playable area, on, off and Reload act on the running Gateway; otherwise a choice applies at its next start.</summary>
public partial class ModsTab : UserControl
{
    Shared _shared = null!;
    ModBrowser _browser = null!;
    ModEntry[] _entries = [];
    IReadOnlyList<BrowseItem> _items = [];
    bool _busy, _loading, _browseRead;
    ModSettingsForm? _form;
    readonly Dictionary<string, Func<object?>> _inputs = new(StringComparer.Ordinal);

    /// <summary>Gateway log: the window opens the Logs section on that source.</summary>
    internal event Action? ShowGatewayLog;

    public ModsTab() => InitializeComponent();

    RuntimeSnapshot? State => _shared.Workspace.Latest;
    ModEntry? Chosen => ModList.SelectedItem as ModEntry;
    ModSource? Source => SourcePicker.SelectedItem is ComboBoxItem { Tag: ModSource source } ? source : null;

    internal void Attach(Shared shared)
    {
        _shared = shared;
        _browser = new ModBrowser(shared);
        OutputView.Attach(shared);
        // A release cannot build, and live control needs the client bridge it does not have.
        if (Repo.IsInstall) BuildAllButton.Visibility = BuildButton.Visibility = ReloadButton.Visibility = CheckModsButton.Visibility = Visibility.Collapsed;
        shared.Workspace.Changed += () => Dispatcher.BeginInvoke(Render);
        shared.LiveRunning += running => Dispatcher.BeginInvoke(() =>
        {
            _busy = running;
            Render();
        });
        L.Current.PropertyChanged += (_, _) =>
        {
            Render();
            FillSources();
        };
        IsVisibleChanged += async (_, e) => { if ((bool)e.NewValue) await shared.Workspace.RefreshAsync(); };
        Pages.SelectionChanged += async (_, e) =>
        {
            if (e.Source == Pages && Pages.SelectedItem == BrowsePage && !_browseRead) await ReadBrowseAsync(again: false);
        };
        FillSources();
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
        InstallFolderButton.IsEnabled = canUse;
        InstallFolderButton.ToolTip = canUse ? L.Text("Install a mod folder from your disk: a project is built, a built mod or scripts are copied.")
            : L.Text("Choose a workspace with a mod host, and wait for the current operation.");
        OpenModsButton.IsEnabled = supported && Directory.Exists(state!.ModEnvironment!.Folder) && ModCatalog.Physical(state.ModEnvironment.Folder);
        OpenModsButton.ToolTip = OpenModsButton.IsEnabled ? state!.ModEnvironment?.Folder : L.Text("Build source mods or install a local mod to create this folder.");
        CheckModsButton.IsEnabled = canUse && gateway?.Status == "Ready" && state!.GameRunning;
        CheckModsButton.ToolTip = CheckModsButton.IsEnabled ? L.Text("Query the running Gateway’s confirmed mod status.")
            : L.Text("Start Gateway and enter a playable area to check live status. Python + Frida are required.");
        Filter();
        ShowBrowse();
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
            : L.Text("No mods installed yet. Browse lists the official mods to install.");
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

    /// <summary>Whether a mod can be turned on or off now, and whether that acts on the running Gateway.</summary>
    (bool Can, bool Live) ToggleState(ModEntry? selected)
    {
        var state = State;
        var gateway = state?.Services.FirstOrDefault(s => s.Name == "Gateway");
        bool supported = state?.ModEnvironment is { Supported: true };
        bool canUse = !_busy && supported && state!.Conflicts.Length == 0 && selected is not null;
        bool live = !Repo.IsInstall && supported && gateway?.Status == "Ready" && state?.GameRunning == true && state.ModEnvironment?.Enabled == true;
        return (canUse && selected!.Fingerprint.Length > 0 && (gateway?.IsRunning != true || live), live);
    }

    void ShowSelection()
    {
        var selected = Chosen;
        var state = State;
        var gateway = state?.Services.FirstOrDefault(s => s.Name == "Gateway");
        bool supported = state?.ModEnvironment is { Supported: true };
        bool canUse = !_busy && supported && state!.Conflicts.Length == 0 && selected is not null;
        var (canToggle, live) = ToggleState(selected);
        ToggleButton.Content = selected?.Enabled == true ? L.Text("Disable mod") : L.Text("Enable mod");
        ToggleButton.IsEnabled = canToggle;
        BuildButton.IsEnabled = canUse && selected!.Project.Length > 0;
        ReloadButton.IsEnabled = canUse && live && selected!.Enabled && selected.Fingerprint.Length > 0;
        OpenButton.IsEnabled = selected is not null && Directory.Exists(selected.Folder) && ModCatalog.Physical(selected.Folder);
        Selected.Text = selected?.Title ?? L.Text("Mod details");
        Hint.Text = selected is null ? L.Text("Choose a mod from the list.")
            : _busy ? L.Text("A workspace operation is in progress.")
            : Repo.IsInstall ? L.Text(gateway?.IsRunning == true ? "Close the game to change mods." : "Enable choices apply the next time you press Play.")
            : gateway?.IsRunning == true && !live ? L.Text("Enter a playable area for live controls, or stop Gateway to save enable choices.")
            : live ? L.Text("Live changes affect every character on this Gateway. Reload resets mod state.")
            : L.Text("Enable choices apply at the next Gateway start. Build selected compiles this mod’s source.");
        ToggleButton.ToolTip = Hint.Text;
        ReloadButton.ToolTip = live ? L.Text("Reload this enabled mod and its dependents.") : L.Text("Needs Gateway running, an enabled built mod, and a client in a playable area (Python + Frida).");
        BuildButton.ToolTip = selected?.Project.Length > 0 ? L.Text("Build this source project.") : L.Text("This mod has no source project in the selected checkout.");
        Details.Text = selected is null ? L.Text("Select a mod to view its dependencies and controls.") : ModsText.Details(selected);
        CommandHint.Text = selected is null ? L.Text("In game: /mod lists runtime status.")
            : L.Text("In game: /mod_{0} {1} · /mod_reload {2}", selected.Enabled ? "disable" : "enable", selected.Id, selected.Id);
        ShowSettings(selected);
    }

    // ---- Settings ----

    void ShowSettings(ModEntry? selected)
    {
        string? shown = _form?.Id;
        if (selected is null || selected.SettingsFile.Length == 0 || State is not { } state)
        {
            _form = null;
            SettingsArea.Visibility = Visibility.Collapsed;
            return;
        }
        SettingsArea.Visibility = Visibility.Visible;
        SaveSettingsButton.IsEnabled = ResetSettingsButton.IsEnabled = !_busy;
        // A refresh redraws the list; keep what the player is typing in the form of the same mod.
        if (shown == selected.Id && SettingsPanel.Children.Count > 0) return;
        _inputs.Clear();
        SettingsPanel.Children.Clear();
        try { _form = ModSettingsForm.Load(selected.Id, selected.SettingsFile, state.Root); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            _form = null;
            SettingsHint.Text = L.Text("Could not read this mod's settings: ") + e.Message;
            SaveSettingsButton.IsEnabled = ResetSettingsButton.IsEnabled = false;
            return;
        }
        foreach (var setting in _form.Settings) SettingsPanel.Children.Add(Field(_form, setting));
        SettingsHint.Text = L.Text("Saved for this checkout in {0}; a running Gateway reloads the mod.", ModSettingsForm.UserFile(selected.Id));
    }

    TextBlock Note(string text) => new() { Text = text, Style = (Style)FindResource("Shell.Secondary"), FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };

    /// <summary>The control of one setting, and how to read it back.</summary>
    UIElement Field(ModSettingsForm form, ModSetting setting)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        var current = form.Current(setting);
        if (setting.Type != "bool") panel.Children.Add(new TextBlock { Text = ModsText.Heading(setting), FontWeight = FontWeights.Medium });
        switch (setting.Type)
        {
            case "bool":
            {
                var box = new CheckBox { Content = setting.Title, IsChecked = current is JsonValue v && v.TryGetValue(out bool b) && b };
                panel.Children.Add(box);
                _inputs[setting.Name] = () => box.IsChecked == true;
                break;
            }
            case "integer" or "number" or "text":
            {
                var box = new TextBox { Text = ModSettingsForm.Show(current, setting), Margin = new Thickness(0, 2, 0, 0) };
                panel.Children.Add(box);
                _inputs[setting.Name] = () => box.Text;
                break;
            }
            case "choice":
            {
                var box = new ComboBox { IsEditable = setting.Source.Length > 0, Margin = new Thickness(0, 2, 0, 0) };
                foreach (var option in setting.Options) box.Items.Add(new ComboBoxItem { Content = option.Label, Tag = option.Value });
                box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(i => JsonNode.DeepEquals((JsonNode)i.Tag, current));
                if (box.IsEditable) { box.Text = ModSettingsForm.Show(current); _ = FillValuesAsync(box, setting.Source); }
                panel.Children.Add(box);
                _inputs[setting.Name] = () => !box.IsEditable && box.SelectedItem is ComboBoxItem { Tag: JsonNode chosen } ? chosen : box.Text;
                break;
            }
            case "choices" when setting.Options.Count > 0:
            {
                var chosen = current as JsonArray ?? [];
                var boxes = setting.Options.Select(o => (o.Value, Box: new CheckBox { Content = o.Label, IsChecked = chosen.Any(c => JsonNode.DeepEquals(c, o.Value)) })).ToList();
                var wrap = new WrapPanel();
                foreach (var (_, box) in boxes) { box.Margin = new Thickness(0, 2, 12, 0); wrap.Children.Add(box); }
                panel.Children.Add(wrap);
                _inputs[setting.Name] = () => boxes.Where(b => b.Box.IsChecked == true).Select(b => b.Value).ToList();
                break;
            }
            case "choices":
            {
                var box = new TextBox { Text = string.Join("\n", (current as JsonArray ?? []).Select(n => ModSettingsForm.Show(n))), AcceptsReturn = true,
                    MinHeight = 60, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
                panel.Children.Add(box);
                panel.Children.Add(Note(L.Text("One per line.")));
                _inputs[setting.Name] = () => box.Text;
                break;
            }
            default:
                panel.Children.Add(Note(ModsText.NotEditable(setting)));
                break;
        }
        if (ModsText.Note(setting) is { Length: > 0 } note) panel.Children.Add(Note(note));
        return panel;
    }

    /// <summary>The game's values of a kind for an editable choice (an item, a monster), when the Gateway can list them.</summary>
    static async Task FillValuesAsync(ComboBox box, string kind)
    {
        try
        {
            var values = await CommandCatalog.ValuesAsync(kind);
            string text = box.Text;
            foreach (var value in values.Take(5000)) box.Items.Add(new ComboBoxItem { Content = value.Value, ToolTip = value.Detail.Length > 0 ? value.Detail : null });
            box.Text = text;
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or System.Text.Json.JsonException or TimeoutException) { }
    }

    async void SaveSettings(object sender, RoutedEventArgs e)
    {
        if (_form is null || Chosen is not { } selected) return;
        var (values, errors) = ModsText.Collect(_form, _inputs);
        if (errors.Count > 0)
        {
            Dialog.Tell(Window.GetWindow(this), DialogKind.Warning, L.Text("Settings not saved"), string.Join("\n", errors));
            return;
        }
        await SaveValuesAsync(selected.Id, values);
    }

    async void ResetSettings(object sender, RoutedEventArgs e)
    {
        if (_form is null || Chosen is not { } selected) return;
        if (!Dialog.Ask(Window.GetWindow(this), DialogKind.Question, L.Text("Reset to defaults"), L.Text("Put every setting Waypoint edits back to its default?"), L.Text("Reset"), L.Text("Cancel"))) return;
        await SaveValuesAsync(selected.Id, ModsText.Defaults(_form, _inputs));
    }

    async Task SaveValuesAsync(string id, JsonObject values)
    {
        if (await _browser.SaveSettingsAsync(id, values) is { Ok: true })
        {
            // Read the file back, so the form shows what the Gateway will.
            SettingsPanel.Children.Clear();
            ShowSettings(Chosen);
        }
    }

    // ---- On, off, build, reload ----

    async void ToggleFromList(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: ModEntry entry }) return;
        ModList.SelectedItem = entry;
        if (!ToggleState(entry).Can)
        {
            Render();
            Dialog.Tell(Window.GetWindow(this), DialogKind.Info, entry.Title, Hint.Text);
            return;
        }
        await ToggleAsync(entry);
    }

    async void Toggle(object sender, RoutedEventArgs e)
    {
        if (Chosen is { } selected) await ToggleAsync(selected);
    }

    async Task ToggleAsync(ModEntry selected) =>
        await _shared.Workspace.OperateAsync(selected.Enabled ? "DisableMod" : "EnableMod", selected.Enabled ? L.Text("Disable mod") : L.Text("Enable mod"), mod: new(selected.Id));

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

    async void Reload(object sender, RoutedEventArgs e)
    {
        if (Chosen is { } selected) await _shared.Workspace.OperateAsync("ReloadMod", L.Text("Reload mod"), mod: new(selected.Id));
    }

    async void InstallFolder(object sender, RoutedEventArgs e)
    {
        var pick = new OpenFolderDialog { Title = L.Text("Choose a mod folder (with its mod.json)") };
        if (pick.ShowDialog(Window.GetWindow(this)) != true) return;
        try { await _browser.InstallFolderAsync(pick.FolderName); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or System.Text.Json.JsonException)
        {
            Dialog.Tell(Window.GetWindow(this), DialogKind.Warning, L.Text("Mod not installed"), ex.Message);
        }
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

    // ---- Browse ----

    void FillSources()
    {
        string? key = Source?.Key;
        SourcePicker.Items.Clear();
        foreach (var source in _browser.Sources) SourcePicker.Items.Add(new ComboBoxItem { Content = ModsText.SourceName(source), Tag = source });
        SourcePicker.SelectedItem = SourcePicker.Items.OfType<ComboBoxItem>().FirstOrDefault(i => ((ModSource)i.Tag).Key == key) ?? SourcePicker.Items[0];
    }

    async void SourceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_browser is null || Source is not { } source) return;
        RemoveRepositoryButton.IsEnabled = !source.IsOfficial;
        if (_browser.View(source) is null && Pages.SelectedItem == BrowsePage) await ReadBrowseAsync(again: false);
        else ShowBrowse();
    }

    async Task ReadBrowseAsync(bool again)
    {
        if (Source is not { } source) return;
        _browseRead = true;
        RefreshButton.IsEnabled = false;
        BrowseStatus.Text = L.Text("Reading {0} …", source.Key);
        try { await _browser.ReadAsync(source, again); }
        finally { RefreshButton.IsEnabled = true; }
        ShowBrowse();
    }

    void ShowBrowse()
    {
        if (_browser is null || Source is not { } source) return;
        _items = _browser.Items(source, _entries);
        string text = BrowseSearch.Text.Trim();
        var shown = _items.Where(i => i.SearchText.Contains(text, StringComparison.OrdinalIgnoreCase)).ToList();
        BrowseList.ItemsSource = shown;
        BrowseStatus.Text = ModsText.BrowseStatus(_browser, source, _items);
        BrowseEmpty.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        BrowseEmpty.Text = text.Length > 0 ? L.Text("No mods match your search.") : _browser.View(source) is null ? L.Text("Refresh reads the repository's mods.") : L.Text("This repository lists no mods.");
    }

    void BrowseSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (_browser is not null) ShowBrowse();
    }

    async void RefreshBrowse(object sender, RoutedEventArgs e) => await ReadBrowseAsync(again: true);

    void OpenRepository(object sender, RoutedEventArgs e)
    {
        if (Source is { } source) Process.Start(new ProcessStartInfo(source.Page) { UseShellExecute = true })?.Dispose();
    }

    async void AddRepository(object sender, RoutedEventArgs e)
    {
        if (!_browser.Add(RepositoryUrl.Text, out var source))
        {
            BrowseStatus.Text = ModsText.AddRefused(source);
            return;
        }
        RepositoryUrl.Clear();
        FillSources();
        SourcePicker.SelectedItem = SourcePicker.Items.OfType<ComboBoxItem>().First(i => ((ModSource)i.Tag).Key == source!.Key);
        await ReadBrowseAsync(again: true);
    }

    void RepositoryUrlKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) AddRepository(sender, e);
    }

    void RemoveRepository(object sender, RoutedEventArgs e)
    {
        if (Source is not { IsOfficial: false } source) return;
        if (!Dialog.Ask(Window.GetWindow(this), DialogKind.Question, L.Text("Remove repository"), L.Text("Remove {0} from Browse? Mods installed from it stay installed.", source.Key), L.Text("Remove"), L.Text("Cancel"))) return;
        _browser.Remove(source);
        FillSources();
    }

    async void InstallFromBrowse(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: BrowseItem item } button) return;
        var owner = Window.GetWindow(this);
        if (State?.ModEnvironment is not { Supported: true })
        {
            Dialog.Tell(owner, DialogKind.Info, item.Title, L.Text("This revision does not have a supported mod host."));
            return;
        }
        if (item.NeedsBuild)
        {
            if (_browser.CannotBuild() is { } reason) { Dialog.Tell(owner, DialogKind.Warning, item.Title, reason); return; }
            if (!Dialog.Ask(owner, DialogKind.Warning, L.Text("Build from source"), ModBrowser.BuildWarning(item), L.Text("Build and install"), L.Text("Cancel"))) return;
        }
        if (!item.Source.IsOfficial && !Dialog.Ask(owner, DialogKind.Warning, L.Text("Install from {0}", item.Source.Key), ModsText.Untrusted(item), L.Text("Install"), L.Text("Cancel"))) return;
        button.IsEnabled = false;
        BrowseStatus.Text = L.Text("Downloading {0} …", item.Title);
        try
        {
            var result = await _browser.InstallAsync(item, (done, total) => Dispatcher.BeginInvoke(() => BrowseStatus.Text = ModsText.Downloading(item, done, total)));
            if (result?.Ok == true) await _shared.Workspace.RefreshAsync();
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException
            or System.Text.Json.JsonException or TaskCanceledException)
        {
            Dialog.Tell(owner, DialogKind.Warning, L.Text("Mod not installed"), ex.Message);
        }
        ShowBrowse();
    }
}
