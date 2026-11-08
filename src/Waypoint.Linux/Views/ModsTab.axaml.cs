using System.Net.Http;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Material.Icons;
using Material.Icons.Avalonia;

namespace Waypoint;

/// <summary>The Mods section, as on Windows: My Mods (the checkout's mods, each row's check box turning it on or off at once,
/// Update when a repository has a newer version) and Browse (a mod repository's index, with Install and Update). A row opens
/// the mod's page (Overview, Settings, Versions); right-clicking it shows its actions, Uninstall among them.</summary>
partial class ModsTab : UserControl
{
    Shared _shared = null!;
    ModBrowser _browser = null!;
    ModEntry[] _entries = [];
    IReadOnlyDictionary<string, BrowseItem> _offers = new Dictionary<string, BrowseItem>();
    IReadOnlyList<BrowseItem> _items = [];
    bool _busy, _filling;
    ModSort _sort = ModSort.Name, _browseSort = ModSort.Name;
    bool _descending, _browseDescending;
    BrowseItem? _pageItem;
    ModEntry? _pageEntry;
    string _pageReadme = "";
    ModSettingsForm? _form;
    SettingsEditor? _settingsEditor;
    ModJob? _pageJob;
    readonly Dictionary<string, Func<object?>> _inputs = new(StringComparer.Ordinal);

    /// <summary>Gateway log: the window opens the Logs section on that source.</summary>
    public event Action? ShowGatewayLog;

    public ModsTab() => InitializeComponent();

    RuntimeSnapshot? State => _shared.Workspace.Latest;
    ModSource? Source => SourcePicker.SelectedItem is ComboBoxItem { Tag: ModSource source } ? source : null;
    Window? Owner => ViewTools.Owner(this);
    bool PageOpen => ModPage.IsVisible;

    public void Attach(Shared shared)
    {
        _shared = shared;
        _browser = new ModBrowser(shared);
        OutputView.Attach(shared);
        // A release cannot build, and live status needs the client bridge it does not have.
        if (Repo.IsInstall) BuildAllItem.IsVisible = CheckModsItem.IsVisible = false;
        shared.Workspace.Changed += () => Dispatcher.UIThread.Post(Render);
        _browser.JobsChanged += () => Dispatcher.UIThread.Post(Render);
        shared.LiveRunning += running => Dispatcher.UIThread.Post(() =>
        {
            _busy = running;
            Render();
        });
        L.Current.PropertyChanged += (_, _) =>
        {
            FillSources();
            Render();
        };
        ViewTools.WhenShown(this, async () =>
        {
            await shared.Workspace.RefreshAsync();
            await ModBrowser.RefreshClientVersionAsync();
            await _browser.ReadAllAsync(again: false);
            Render();
        });
        // The Gateway logs a mod it loads, reloads or fails to load: My Mods follows its log while it is shown.
        DispatcherTimer.Run(() =>
        {
            if (IsEffectivelyVisible && GatewayMods.Changed(State)) Render();
            return true;
        }, TimeSpan.FromSeconds(2));
        FillSources();
        Render();
        _ = RefreshClientAsync();
    }

    async Task RefreshClientAsync()
    {
        await ModBrowser.RefreshClientVersionAsync();
        Render();
    }

    // ---- Lists ----

    void Render()
    {
        var state = State;
        bool supported = state?.ModEnvironment is { Supported: true };
        string notice = "";
        try { _entries = state is null ? [] : ModCatalog.Read(state); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _entries = [];
            notice = L.Text("Could not inspect mods: ") + ex.Message;
        }
        if (state is not null && supported && !state.ModEnvironment!.Enabled) notice = L.Text("Mods off in launch profile (Game_MODS=0)");
        notice = string.Join("\n", new[] { notice, ModsText.ChoicesNotice(state) }.Where(s => s.Length > 0));
        if (Repo.LegacyReleaseMods) notice = L.Text("This release does not declare compatibility with downloadable mods. Use a current source checkout to install or update mods.");
        Notice.Text = notice;
        Notice.IsVisible = notice.Length > 0;
        _offers = _browser.Offers(_entries);
        int updates = _entries.Count(e => _offers.GetValueOrDefault(e.Id)?.Offer == ModOffer.Update);
        UpdateCount.Text = updates.ToString();
        UpdateCountBadge.IsVisible = updates > 0;
        UpdateAllButton.IsEnabled = CanUse && updates > 0;
        InstallFolderItem.IsEnabled = CanUse;
        BuildAllItem.IsEnabled = CanUse && _entries.Any(e => e.Project.Length > 0);
        var gateway = state?.Services.FirstOrDefault(s => s.Name == "Gateway");
        CheckModsItem.IsEnabled = CanUse && gateway?.Status == "Ready" && state!.GameRunning;
        OpenModsButton.IsEnabled = supported && Directory.Exists(state!.ModEnvironment!.Folder) && ModCatalog.Physical(state.ModEnvironment.Folder);
        AutoBackupItem.IsChecked = _shared.Settings.AutomaticBackups;
        Filter();
        ShowBrowse();
        if (PageOpen) RefreshPage();
    }

    /// <summary>Whether a mod operation can start: a checkout with a mod host, nothing else running, no conflicts.</summary>
    bool CanUse => State is { ModEnvironment.Supported: true, Conflicts.Length: 0 } && !_busy && !Repo.LegacyReleaseMods;

    void Filter()
    {
        string text = (Search.Text ?? "").Trim();
        var failures = GatewayMods.LoadErrors(State);
        var rows = ModSorting.Sort(_entries.Select(e => new InstalledRow(e, _offers.GetValueOrDefault(e.Id)) { Job = _browser.Job(e.Id), LoadError = failures.GetValueOrDefault(e.Id, "") })
            .Where(r => r.SearchText.Contains(text, StringComparison.OrdinalIgnoreCase)), _sort, _descending).ToList();
        ShowHeadings();
        int updates = _entries.Count(e => _offers.GetValueOrDefault(e.Id)?.Offer == ModOffer.Update);
        InstalledCount.Text = text.Length > 0 ? L.Text("{0} of {1} installed mods", rows.Count, _entries.Length)
            : L.Text("{0} installed · {1} updates", _entries.Length, updates);
        ModList.ItemsSource = rows;
        Empty.IsVisible = rows.Count == 0;
        Empty.Text = text.Length > 0 ? L.Text("No mods match your search.")
            : State is null ? L.Text("Choose and check a workspace to see its mods.")
            : State.ModEnvironment is not { Supported: true } ? L.Text("This revision does not have a supported mod host.")
            : L.Text("No mods installed yet. Browse lists the official mods to install.");
    }

    void SearchChanged(object? sender, TextChangedEventArgs e)
    {
        if (_shared is not null) Filter();
    }

    void PageChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (e.Source != Pages || _shared is null) return;
        ClosePage(this, e);
        if (PageOpen) return;
        bool browse = Pages.SelectedItem == BrowsePage;
        InstalledView.IsVisible = !browse;
        BrowseView.IsVisible = browse;
        if (browse && Source is { } source && _browser.View(source) is null) _ = ReadBrowseAsync(again: false);
    }

    async void RefreshInstalled(object? sender, RoutedEventArgs e)
    {
        await _shared.Workspace.RefreshAsync();
        await ModBrowser.RefreshClientVersionAsync();
        await _browser.ReadAllAsync(again: true);
        Render();
    }

    static (BrowseItem? Item, ModEntry? Entry) RowOf(object? context) => context switch
    {
        InstalledRow r => (r.Offer, r.Entry),
        BrowseItem b => (b, b.Installed),
        _ => (null, null),
    };

    /// <summary>A tap on a row (not on its check box or button) opens the mod's page.</summary>
    void RowTapped(object? sender, TappedEventArgs e)
    {
        if ((e.Source as Visual)?.FindAncestorOfType<Button>(includeSelf: true) is not null
            || (e.Source as Visual)?.FindAncestorOfType<CheckBox>(includeSelf: true) is not null) return;
        var (item, entry) = RowOf((sender as Control)?.DataContext);
        OpenPage(item, entry);
    }

    void RowContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not Control row) return;
        var (item, entry) = RowOf(row.DataContext);
        if (item is null && entry is null) return;
        e.Handled = true;
        Menu(item, entry, withView: true).ShowAt(row, showAtPointer: true);
    }

    /// <summary>A mod's actions, for its row's right-click menu and its page's ⋮.</summary>
    MenuFlyout Menu(BrowseItem? item, ModEntry? entry, bool withView)
    {
        var menu = new MenuFlyout();
        void Add(string text, MaterialIconKind icon, Action run, bool enabled = true)
        {
            var menuItem = new MenuItem { Header = text, Icon = new MaterialIcon { Kind = icon }, IsEnabled = enabled };
            menuItem.Click += (_, _) => run();
            menu.Items.Add(menuItem);
        }
        if (item is { CanAct: true }) Add(item.ActionText, MaterialIconKind.Download, () => _ = InstallItemAsync(item), CanUse);
        else if (item is { Installed: not null }) Add(L.Text("Reinstall"), MaterialIconKind.DownloadOutline, () => _ = InstallItemAsync(item), CanUse && _browser.Job(item.Listing.Id) is null);
        if (withView) Add(L.Text("View mod"), MaterialIconKind.EyeOutline, () => OpenPage(item, entry));
        if (entry is not null && Directory.Exists(entry.Folder) && ModCatalog.Physical(entry.Folder)) Add(L.Text("Open folder"), MaterialIconKind.FolderOpenOutline, () => ViewTools.Open(entry.Folder));
        if (item is not null)
        {
            Add(L.Text("Open on GitHub"), MaterialIconKind.Github, () => ViewTools.Open(item.Source.ModPage(item.Listing)));
            Add(L.Text("Copy mod link"), MaterialIconKind.LinkVariant, () => _ = ViewTools.Copy(this, item.Source.ModPage(item.Listing)));
        }
        if (entry is not null)
        {
            if (entry.SettingsFile.Length > 0) Add(L.Text("Reset settings"), MaterialIconKind.CogRefreshOutline, () => _ = ResetSettingsOfAsync(entry), CanUse);
            if (!Repo.IsInstall && entry.Fingerprint.Length > 0 && CanUse && State?.Services.FirstOrDefault(s => s.Name == "Gateway") is { Status: "Ready" } && State.GameRunning)
                Add(L.Text("Reload mod"), MaterialIconKind.Reload, () => _ = _shared.Workspace.OperateAsync("ReloadMod", L.Text("Reload mod"), mod: new(entry.Id)));
            if (entry.Project.Length > 0 && !Repo.IsInstall) Add(L.Text("Build mod"), MaterialIconKind.Hammer, () => _ = BuildAsync(entry), CanUse && _browser.Job(entry.Id) is null);
            menu.Items.Add(new Separator());
            Add(L.Text("Uninstall"), MaterialIconKind.TrashCanOutline, () => _ = UninstallAsync(entry), CanUse && _browser.Job(entry.Id) is null);
        }
        return menu;
    }

    // ---- On, off, install, uninstall ----

    void ToggleFromList(object? sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { DataContext: InstalledRow row }) Toggle(row.Entry, !row.Entry.Enabled);
    }

    void TogglePage(object? sender, RoutedEventArgs e)
    {
        if (_pageEntry is { } entry) Toggle(entry, !entry.Enabled);
    }

    /// <summary>Saves a choice under the workspace lock. The notice explains servers that need a restart.</summary>
    async void Toggle(ModEntry entry, bool enabled)
    {
        try
        {
            if (entry.Fingerprint.Length == 0) throw new InvalidOperationException(L.Text("Build or install this mod first; its binary manifest is missing."));
            if (!await _browser.SetEnabledAsync(entry, enabled))
                Dialog.Tell(Owner, DialogKind.Warning, entry.Title, L.Text("A workspace operation is in progress."));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            Dialog.Tell(Owner, DialogKind.Warning, entry.Title, ex.Message);
        }
        Render();
    }

    /// <summary>A column's heading: sorts its list by it, and a second click reverses the order.</summary>
    void SortBy(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ModSort column } button) return;
        if (button.Parent == BrowseHeadings)
        {
            _browseDescending = _browseSort == column && !_browseDescending;
            _browseSort = column;
            ShowBrowse();
        }
        else
        {
            _descending = _sort == column && !_descending;
            _sort = column;
            Filter();
        }
    }

    void ShowHeadings()
    {
        foreach (var button in InstalledHeadings.Children.OfType<Button>()) button.Content = ModSorting.Heading((ModSort)button.Tag!, _sort, _descending);
        foreach (var button in BrowseHeadings.Children.OfType<Button>()) button.Content = ModSorting.Heading((ModSort)button.Tag!, _browseSort, _browseDescending);
    }

    void OpenRowDetails(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        e.Handled = true;
        if (button.DataContext is InstalledRow row) OpenPage(row.Offer, row.Entry);
        else if (button.DataContext is BrowseItem item) OpenPage(item, item.Installed);
    }

    async void RowAction(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: InstalledRow row }) await ActAsync(row);
    }

    /// <summary>A row's action: Update installs the newer version; Build compiles the source project, or installs the
    /// repository's copy of a mod with no project here.</summary>
    async Task ActAsync(InstalledRow row)
    {
        if (row.Action is Waypoint.RowAction.Update or Waypoint.RowAction.Reinstall && row.Offer is { } update) await InstallItemAsync(update);
        else if (row.Action == Waypoint.RowAction.Build && row.Entry.Project.Length > 0 && !Repo.IsInstall) await BuildAsync(row.Entry);
        else if (row.Action == Waypoint.RowAction.Build && row.Offer is { } listed) await InstallItemAsync(listed);
    }

    /// <summary>Builds a mod's source project; its row shows the build, and the log opens when it fails.</summary>
    async Task BuildAsync(ModEntry entry)
    {
        if (!CanUse) return;
        if (new InstalledRow(entry, _offers.GetValueOrDefault(entry.Id)).Incompatible
            && !Dialog.Ask(Owner, DialogKind.Warning, entry.Title, ModBrowser.IncompatibleQuestion(), L.Text("Build"), L.Text("Cancel"))) return;
        try
        {
            if (await _browser.BuildAsync(entry) is { Ok: false }) ShowLog(true);
        }
        catch (InvalidOperationException ex)
        {
            Dialog.Tell(Owner, DialogKind.Warning, entry.Title, ex.Message);
        }
    }

    /// <summary>Starts every update at once: they download side by side, then install one after another.</summary>
    async void UpdateAll(object? sender, RoutedEventArgs e) =>
        await Task.WhenAll(_entries.Select(m => _offers.GetValueOrDefault(m.Id)).Where(i => i?.Offer == ModOffer.Update).Select(i => InstallItemAsync(i!)).ToList());

    async void InstallFromBrowse(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: BrowseItem item }) await InstallItemAsync(item);
    }

    async void InstallVersion(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: VersionRow row }) await InstallItemAsync(row.Item with { Release = row.Release });
    }

    async void PageActionClicked(object? sender, RoutedEventArgs e)
    {
        if (_pageItem is { CanAct: true } item) await InstallItemAsync(item);
    }

    /// <summary>Installs or updates one mod, after the warnings it needs (source to build later, a repository other than the
    /// official one). Its row shows the download and install; others can start meanwhile.</summary>
    async Task InstallItemAsync(BrowseItem item)
    {
        if (State?.ModEnvironment is not { Supported: true })
        {
            Dialog.Tell(Owner, DialogKind.Info, item.Title, L.Text("This revision does not have a supported mod host."));
            return;
        }
        if (!CanUse || _browser.Job(item.Listing.Id) is not null) return;
        if (item.NeedsBuild)
        {
            if (ModBrowser.CannotInstallSource() is { } reason) { Dialog.Tell(Owner, DialogKind.Warning, item.Title, reason); return; }
            if (!Dialog.Ask(Owner, DialogKind.Info, L.Text("Install from source"), ModBrowser.SourceWarning(item), L.Text("Install"), L.Text("Cancel"))) return;
        }
        if (!item.Source.IsOfficial && !Dialog.Ask(Owner, DialogKind.Warning, L.Text("Install from {0}", item.Source.Key), ModsText.Untrusted(item), L.Text("Install"), L.Text("Cancel"))) return;
        if (item.Incompatible && !Dialog.Ask(Owner, DialogKind.Warning, item.Title, ModBrowser.IncompatibleQuestion(), L.Text("Install"), L.Text("Cancel"))) return;
        try
        {
            if (await _browser.InstallAsync(item) is { Ok: false }) ShowLog(true);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException
            or System.Text.Json.JsonException or TaskCanceledException)
        {
            Dialog.Tell(Owner, DialogKind.Warning, L.Text("{0} not installed", item.Title), ex.Message);
        }
    }

    async Task UninstallAsync(ModEntry entry)
    {
        if (!CanUse) return;
        if (!Dialog.Ask(Owner, DialogKind.Warning, L.Text("Uninstall {0}", entry.Title), ModsText.UninstallQuestion(entry), L.Text("Uninstall"), L.Text("Cancel"))) return;
        var result = await _browser.UninstallAsync(entry);
        if (result is { Ok: false }) ShowLog(true);
        if (result?.Ok == true && _pageEntry?.Id == entry.Id && _pageItem is null) ClosePage(this, new RoutedEventArgs());
    }

    async void InstallFolder(object? sender, RoutedEventArgs e)
    {
        if (Owner is not { } owner || Ui.PickFolder(owner, L.Text("Choose a mod folder (with its mod.json)")) is not { } folder) return;
        try
        {
            if (await _browser.InstallFolderAsync(folder) is { Ok: false }) ShowLog(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or System.Text.Json.JsonException)
        {
            Dialog.Tell(owner, DialogKind.Warning, L.Text("Mod not installed"), ex.Message);
        }
    }

    // ---- The ⋮ menu's tools ----

    async void CheckMods(object? sender, RoutedEventArgs e) => await _shared.Workspace.OperateAsync("QueryMods", L.Text("Check live status"));

    async void BuildAll(object? sender, RoutedEventArgs e) => await _shared.Workspace.OperateAsync("BuildMods", L.Text("Build all mods"));

    void GatewayLog(object? sender, RoutedEventArgs e) => ShowGatewayLog?.Invoke();

    void ShowLogChanged(object? sender, RoutedEventArgs e) => ShowLog(ShowLogItem.IsChecked);

    void ShowLog(bool shown)
    {
        ShowLogItem.IsChecked = shown;
        OutputView.IsVisible = shown;
    }

    void BackupChanged(object? sender, RoutedEventArgs e)
    {
        _shared.Settings.AutomaticBackups = AutoBackupItem.IsChecked;
        _shared.Settings.Save();
    }

    void OpenMods(object? sender, RoutedEventArgs e)
    {
        if (State?.ModEnvironment is { Supported: true } environment && Directory.Exists(environment.Folder) && ModCatalog.Physical(environment.Folder))
            ViewTools.Open(environment.Folder);
    }

    // ---- Browse ----

    void FillSources()
    {
        string? key = Source?.Key;
        _filling = true;
        SourcePicker.Items.Clear();
        foreach (var source in _browser.Sources) SourcePicker.Items.Add(new ComboBoxItem { Content = ModsText.SourceName(source), Tag = source });
        _filling = false;
        SourcePicker.SelectedItem = SourcePicker.Items.OfType<ComboBoxItem>().FirstOrDefault(i => ((ModSource)i.Tag!).Key == key) ?? SourcePicker.Items[0];
    }

    async void SourceChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_browser is null || _filling || Source is not { } source) return;
        RemoveRepositoryItem.IsEnabled = !source.IsOfficial;
        if (_browser.View(source) is null && BrowseView.IsVisible) await ReadBrowseAsync(again: false);
        else ShowBrowse();
    }

    async Task ReadBrowseAsync(bool again)
    {
        if (Source is not { } source) return;
        RefreshButton.IsEnabled = false;
        BrowseStatus.Text = L.Text("Reading {0} …", source.Key);
        BrowseStatus.IsVisible = true;
        try { await _browser.ReadAsync(source, again); }
        finally { RefreshButton.IsEnabled = true; }
        Render();
    }

    void ShowBrowse()
    {
        if (_browser is null || Source is not { } source) return;
        _items = _browser.Items(source, _entries);
        string text = (BrowseSearch.Text ?? "").Trim();
        var shown = ModSorting.Sort(_items.Where(i => i.SearchText.Contains(text, StringComparison.OrdinalIgnoreCase)), _browseSort, _browseDescending).ToList();
        ShowHeadings();
        BrowseList.ItemsSource = shown;
        BrowseCount.Text = _browser.View(source) is null ? "" : L.Text("{0} mods", _items.Count);
        string? problem = ModsText.BrowseProblem(_browser, source);
        BrowseStatus.Text = problem ?? "";
        BrowseStatus.IsVisible = problem is not null;
        BrowseEmpty.IsVisible = shown.Count == 0;
        BrowseEmpty.Text = text.Length > 0 ? L.Text("No mods match your search.") : _browser.View(source) is null ? L.Text("Refresh reads the repository's mods.") : L.Text("This repository lists no mods.");
    }

    void BrowseSearchChanged(object? sender, TextChangedEventArgs e)
    {
        if (_browser is not null) ShowBrowse();
    }

    async void RefreshBrowse(object? sender, RoutedEventArgs e) => await ReadBrowseAsync(again: true);

    void OpenRepository(object? sender, RoutedEventArgs e)
    {
        if (Source is { } source) ViewTools.Open(source.Page);
    }

    void ShowAddRepository(object? sender, RoutedEventArgs e)
    {
        AddRepositoryRow.IsVisible = true;
        RepositoryUrl.Focus();
    }

    void HideAddRepository(object? sender, RoutedEventArgs e)
    {
        RepositoryUrl.Text = "";
        AddRepositoryRow.IsVisible = false;
    }

    async void AddRepository(object? sender, RoutedEventArgs e)
    {
        if (!_browser.Add(RepositoryUrl.Text ?? "", out var source))
        {
            Dialog.Tell(Owner, DialogKind.Info, L.Text("Add repository"), ModsText.AddRefused(source));
            return;
        }
        HideAddRepository(sender, e);
        FillSources();
        SourcePicker.SelectedItem = SourcePicker.Items.OfType<ComboBoxItem>().First(i => ((ModSource)i.Tag!).Key == source!.Key);
        await ReadBrowseAsync(again: true);
    }

    void RepositoryUrlKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) AddRepository(sender, e);
        else if (e.Key == Key.Escape) HideAddRepository(sender, e);
    }

    void RemoveRepository(object? sender, RoutedEventArgs e)
    {
        if (Source is not { IsOfficial: false } source) return;
        if (!Dialog.Ask(Owner, DialogKind.Question, L.Text("Remove repository"), L.Text("Remove {0} from Browse? Mods installed from it stay installed.", source.Key), L.Text("Remove"), L.Text("Cancel"))) return;
        _browser.Remove(source);
        FillSources();
        Render();
    }

    // ---- A mod's page ----

    bool DiscardSettings() => _settingsEditor?.Dirty != true || Dialog.Ask(Owner, DialogKind.Question,
        L.Text("Unsaved changes"), L.Text("Discard the changes you have not saved?"), L.Text("Discard"), L.Text("Cancel"));

    void OpenPage(BrowseItem? item, ModEntry? entry)
    {
        if (item is null && entry is null) return;
        if (!DiscardSettings()) return;
        _settingsEditor = null;
        _pageItem = item;
        _pageEntry = entry;
        _form = null;
        _pageReadme = "";
        InstalledView.IsVisible = BrowseView.IsVisible = false;
        ModPage.IsVisible = true;
        SettingsEditor.Show(ChoiceScopeHint, false);
        PageTabs.SelectedItem = ModOverviewPage;
        RefreshPage();
        _ = LoadReadmeAsync(item, entry);
    }

    void ClosePage(object? sender, RoutedEventArgs e)
    {
        if (!PageOpen) return;
        if (!DiscardSettings()) return;
        _settingsEditor = null;
        ModPage.IsVisible = false;
        SettingsEditor.Show(ChoiceScopeHint, true);
        _pageItem = null;
        _pageEntry = null;
        _form = null;
        ShowPageJob(null);
        bool browse = Pages.SelectedItem == BrowsePage;
        InstalledView.IsVisible = !browse;
        BrowseView.IsVisible = browse;
    }

    /// <summary>Redraws the open page from the latest lists: the installed mod and its offer may have changed under it.</summary>
    void RefreshPage()
    {
        string id = _pageItem?.Listing.Id ?? _pageEntry!.Id;
        var entry = _entries.FirstOrDefault(e => e.Id == id);
        var item = _pageItem is null ? _offers.GetValueOrDefault(id)
            : _browser.Items(_pageItem.Source, _entries).FirstOrDefault(i => i.Listing.Id == id) ?? _pageItem with { Installed = entry };
        _pageItem = item;
        _pageEntry = entry;
        if (item is null && entry is null) { ClosePage(this, new RoutedEventArgs()); return; }
        string title = entry is null ? item!.Title : new InstalledRow(entry, item).Title;
        PageIcon.Kind = ModsText.Icon(id);
        BackText.Text = L.Text(Pages.SelectedItem == BrowsePage ? "Back to Browse" : "Back to My Mods");
        PageTitle.Text = title;
        string author = entry?.Author is { Length: > 0 } a ? a : item?.Author ?? "";
        PageByline.Text = author.Length > 0 ? L.Text("by {0}", author) : "";
        PageDescription.Text = entry?.Description is { Length: > 0 } d ? d : item?.Listing.Description ?? "";
        PageTags.ItemsSource = ModsText.Tags(item, entry);
        PageStats.Text = ModsText.Stats(item, entry);
        PageEnabled.IsVisible = entry is not null;
        PageEnabled.IsChecked = entry?.Enabled == true;
        PageEnabled.IsEnabled = entry?.Fingerprint.Length > 0;

        ShowPageJob(_browser.Job(item?.Listing.Id ?? entry!.Id));
        PageAction.IsEnabled = item?.CanAct == true && _pageJob is null && CanUse;
        var history = item is null ? [] : _browser.View(item.Source)?.History.GetValueOrDefault(item.Listing.Id) ?? [];
        VersionList.ItemsSource = history.Select(r => new VersionRow(item!, r)).ToList();
        ModVersionsPage.IsVisible = history.Count > 0;
        SettingsEditor.Show(ModSettingsPage, true);
        if (PageTabs.SelectedItem is TabItem { IsVisible: false }) PageTabs.SelectedItem = ModOverviewPage;
        PagePackageStatus.Text = entry is null ? "" : new InstalledRow(entry, item).PackageStatus;
        SettingsEditor.Show(PagePackageStatus, entry is not null && _pageJob is null);
        string? incompatible = entry is not null ? new InstalledRow(entry, item).IncompatibleTip : item?.IncompatibleTip;
        PageIncompatibleText.Text = incompatible ?? "";
        SettingsEditor.Show(PageIncompatible, incompatible is not null);
        ShowSettings(entry);
        if (_pageReadme.Length == 0) ShowOverview(PageDescription.Text ?? "");
    }

    /// <summary>The page's button follows the mod's job while one runs: "40%", "Installing…".</summary>
    void ShowPageJob(ModJob? job)
    {
        if (_pageJob != job)
        {
            if (_pageJob is not null) _pageJob.PropertyChanged -= PageJobChanged;
            _pageJob = job;
            if (job is not null) job.PropertyChanged += PageJobChanged;
        }
        PageAction.Content = job?.Text ?? _pageItem?.ActionText ?? "";
        // An Update is green, as in the lists.
        PageAction.Classes.Set("accent", _pageItem?.IsUpdate != true);
        PageAction.Classes.Set("update", _pageItem?.IsUpdate == true);
        SettingsEditor.Show(PageAction, job is not null || _pageItem?.Offer is ModOffer.Install or ModOffer.Update);
    }

    void PageJobChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender == _pageJob) PageAction.Content = _pageJob!.Text;
    }

    void OpenPageMenu(object? sender, RoutedEventArgs e)
    {
        if (sender is Control button) Menu(_pageItem, _pageEntry, withView: false).ShowAt(button);
    }

    async Task LoadReadmeAsync(BrowseItem? item, ModEntry? entry)
    {
        string text = await _browser.ReadmeAsync(item, entry);
        // The player may have opened another mod while it was read.
        if ((_pageItem?.Listing.Id ?? _pageEntry?.Id) != (item?.Listing.Id ?? entry?.Id) || text.Length == 0) return;
        _pageReadme = text;
        ShowOverview(text);
    }

    /// <summary>The Overview: the README, drawn as Markdown (no HTML or images), or the description when there is none.</summary>
    void ShowOverview(string markdown)
    {
        if (markdown.Trim().Length == 0) markdown = L.Text("This mod has no description yet.");
        OverviewPanel.Children.Clear();
        OverviewPanel.Children.AddRange(MarkdownView.Render(markdown, ModsText.ReadmeBase(_pageItem), this));
    }

    // ---- Settings ----

    void ShowSettings(ModEntry? entry)
    {
        // Refreshes must not replace a form that still belongs to this installed mod.
        if (!Repo.LegacyReleaseMods && entry is not null && _form?.Id == entry.Id && SettingsPanel.Children.Count > 0)
        { UpdateSettingsButtons(); return; }
        _form = null; _settingsEditor = null; _inputs.Clear(); SettingsPanel.Children.Clear();
        SettingsEditor.Show(SettingsActions, false); SettingsEditor.Show(SettingsFooter, false); SettingsHint.Text = "";
        if (Repo.LegacyReleaseMods)
        {
            SettingsNotice(L.Text("Settings support unavailable"), L.Text("This release does not declare compatibility with downloadable mod settings. Use a compatible source checkout to edit them.")); return;
        }
        if (entry is null)
        { SettingsNotice(L.Text("Settings unavailable before installation"), L.Text("Install this mod to view its settings.")); return; }
        if (entry.SettingsFile.Length == 0)
        {
            SettingsNotice(L.Text("Settings not exposed to Waypoint"), L.Text("The installed mod does not provide a settings declaration for Waypoint. Separate configuration files may still exist.")); return;
        }
        if (State is not { } state)
        { SettingsNotice(L.Text("Settings could not be loaded"), L.Text("Choose and check a workspace to see its mods.")); return; }
        try { _form = ModSettingsForm.Load(entry.Id, entry.SettingsFile, state.Root); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            _form = null;
            SettingsNotice(L.Text("Settings could not be loaded"), L.Text("Waypoint found a settings declaration but could not load it. Refresh to try again. Nothing was changed."));
            SettingsPanel.Children.Add(new Expander { Header = L.Text("Error details"), Content = Note(e.Message), Margin = new Thickness(0, 8, 0, 0) });
            return;
        }
        if (_form.Settings.Count == 0)
        { _form = null; SettingsNotice(L.Text("No editable settings"), L.Text("The settings declaration was loaded successfully and contains no editable settings.")); return; }
        _settingsEditor = new SettingsEditor(_form);
        SettingsPanel.Children.Add(_settingsEditor); _settingsEditor.Edited += UpdateSettingsButtons;
        SettingsEditor.Show(SettingsActions, true); UpdateSettingsButtons();
    }
    void SettingsNotice(string heading, string explanation)
    {
        var title = Note(heading); title.FontSize = 14; title.FontWeight = FontWeight.SemiBold;
        title.Bind(TextBlock.ForegroundProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("Shell.Text")); SettingsPanel.Children.Add(title);
        var note = Note(explanation); note.Margin = new Thickness(0, 6, 0, 0); SettingsPanel.Children.Add(note);
    }
    void UpdateSettingsButtons()
    {
        SettingsEditor.Show(SettingsFooter, _settingsEditor is not null);
        bool dirty = _settingsEditor?.Dirty == true;
        SaveSettingsButton.IsEnabled = CanUse && dirty; ResetSettingsButton.IsEnabled = CanUse && _form is not null;
        DiscardSettingsButton.IsEnabled = dirty;
        if (_settingsEditor is not null) _settingsEditor.IsEnabled = CanUse;
        SettingsHint.Text = !CanUse ? L.Text("Settings cannot be saved while this workspace is unavailable or busy.") : dirty ? L.Text("Unsaved settings") : L.Text("No unsaved changes.");
    }
    void DiscardModSettings(object? sender, RoutedEventArgs e)
    {
        if (_settingsEditor?.Dirty != true || !DiscardSettings()) return;
        _form = null; ShowSettings(_pageEntry);
    }

    static TextBlock Note(string text)
    {
        var note = new TextBlock { Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
        note.Classes.Add("note");
        return note;
    }

    async void SaveSettings(object? sender, RoutedEventArgs e)
    {
        if (_form is null || _pageEntry is not { } entry) return;
        try { if (_settingsEditor is not null) await SaveValuesAsync(entry.Id, _settingsEditor.Values()); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException)
        { Dialog.Tell(Owner, DialogKind.Warning, L.Text("Settings not saved"), ex.Message); }
    }

    async void ResetSettings(object? sender, RoutedEventArgs e)
    {
        if (_form is null || _pageEntry is not { } entry) return;
        if (!Dialog.Ask(Owner, DialogKind.Question, L.Text("Reset to defaults"), L.Text("Put every setting Waypoint edits back to its default?"), L.Text("Reset"), L.Text("Cancel"))) return;
        if (_settingsEditor is not null) await SaveValuesAsync(entry.Id, _settingsEditor.Values(defaults: true));
    }

    /// <summary>Reset settings from a row's menu: the defaults of what Waypoint edits, read from the mod's declarations.</summary>
    async Task ResetSettingsOfAsync(ModEntry entry)
    {
        if (State is not { } state) return;
        if (!Dialog.Ask(Owner, DialogKind.Question, L.Text("Reset to defaults"), L.Text("Put every setting Waypoint edits back to its default?"), L.Text("Reset"), L.Text("Cancel"))) return;
        try
        {
            var form = ModSettingsForm.Load(entry.Id, entry.SettingsFile, state.Root);
            var editable = form.Settings.Where(s => s.Editable).ToDictionary(s => s.Name, s => (Func<object?>)(() => null), StringComparer.Ordinal);
            await SaveValuesAsync(entry.Id, ModsText.Defaults(form, editable), form.UserHash);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            Dialog.Tell(Owner, DialogKind.Warning, L.Text("Settings not saved"), ex.Message);
        }
    }

    async Task SaveValuesAsync(string id, JsonObject values, string? expectedHash = null)
    {
        var result = await _browser.SaveSettingsAsync(id, values, expectedHash ?? _form?.UserHash ?? "missing");
        if (result is { Ok: false }) ShowLog(true);
        if (result?.Ok == true && _pageEntry?.Id == id)
        {
            // Read the file back, so the form shows what the Gateway will.
            SettingsPanel.Children.Clear();
            ShowSettings(_pageEntry);
        }
    }
}
