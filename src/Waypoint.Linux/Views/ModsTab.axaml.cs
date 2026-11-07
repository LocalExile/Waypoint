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
    ModSort _sort = ModSort.Name;
    bool _descending;
    BrowseItem? _pageItem;
    ModEntry? _pageEntry;
    string _pageReadme = "";
    ModSettingsForm? _form;
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
    }

    void ShowTexts()
    {
        InstalledPage.Header = L.Text("My Mods");
        BrowsePage.Header = L.Text("Browse");
        Search.PlaceholderText = BrowseSearch.PlaceholderText = L.Text("Search");
        UpdateAllText.Text = L.Text("Update all");
        ToolTip.SetTip(OpenModsButton, L.Text("Open mods folder"));
        ToolTip.SetTip(RefreshInstalledButton, L.Text("Refresh"));
        ToolTip.SetTip(RefreshButton, L.Text("Refresh"));
        ToolTip.SetTip(MoreButton, L.Text("More"));
        ToolTip.SetTip(BrowseMoreButton, L.Text("More"));
        ToolTip.SetTip(PageMenuButton, L.Text("More"));
        ToolTip.SetTip(BackButton, L.Text("Back"));
        InstallFolderItem.Header = L.Text("Install from folder…");
        BuildAllItem.Header = L.Text("Build all mods");
        CheckModsItem.Header = L.Text("Check live status");
        GatewayLogItem.Header = L.Text("Gateway log");
        ShowLogItem.Header = L.Text("Show operation log");
        AutoBackupItem.Header = L.Text("Automatic backup before mod changes");
        BrowseNameHeader.Text = L.Text("Name");
        BrowseActionHeader.Text = L.Text("Action");
        BrowseLatestHeader.Text = L.Text("Latest");
        BrowseTypeHeader.Text = L.Text("Type");
        BrowseAuthorHeader.Text = L.Text("Author");
        AddRepositoryItem.Header = L.Text("Add repository…");
        OpenRepositoryItem.Header = L.Text("Open on GitHub");
        RemoveRepositoryItem.Header = L.Text("Remove repository");
        AddRepositoryButton.Content = L.Text("Add");
        CancelRepositoryButton.Content = L.Text("Cancel");
        RepositoryUrl.PlaceholderText = L.Text("Another mod repository: github.com/owner/name");
        PageEnabled.Content = L.Text("Enabled");
        ModOverviewPage.Header = L.Text("Overview");
        ModSettingsPage.Header = L.Text("Settings");
        ModVersionsPage.Header = L.Text("Versions");
        SaveSettingsButton.Content = L.Text("Save settings");
        ResetSettingsButton.Content = L.Text("Reset to defaults");
    }

    // ---- Lists ----

    void Render()
    {
        ShowTexts();
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
    bool CanUse => State is { ModEnvironment.Supported: true, Conflicts.Length: 0 } && !_busy;

    void Filter()
    {
        string text = (Search.Text ?? "").Trim();
        var failures = GatewayMods.LoadErrors(State);
        var rows = ModSorting.Sort(_entries.Select(e => new InstalledRow(e, _offers.GetValueOrDefault(e.Id)) { Job = _browser.Job(e.Id), LoadError = failures.GetValueOrDefault(e.Id, "") })
            .Where(r => r.SearchText.Contains(text, StringComparison.OrdinalIgnoreCase)), _sort, _descending).ToList();
        foreach (var (heading, column) in new[] { (SortEnabled, ModSort.Enabled), (NameHeader, ModSort.Name), (ActionHeader, ModSort.Action), (LatestHeader, ModSort.Latest), (AuthorHeader, ModSort.Author) })
            heading.Text = ModSorting.Heading(column, _sort, _descending);
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
        bool browse = Pages.SelectedItem == BrowsePage;
        InstalledView.IsVisible = !browse;
        BrowseView.IsVisible = browse;
        if (browse && Source is { } source && _browser.View(source) is null) _ = ReadBrowseAsync(again: false);
    }

    async void RefreshInstalled(object? sender, RoutedEventArgs e)
    {
        await _shared.Workspace.RefreshAsync();
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

    /// <summary>Turns a mod on or off at once: a line in mod-state.txt, which a running Gateway applies within a second.</summary>
    void Toggle(ModEntry entry, bool enabled)
    {
        try
        {
            if (entry.Fingerprint.Length == 0) throw new InvalidOperationException(L.Text("Build or install this mod first; its binary manifest is missing."));
            _browser.SetEnabled(entry, enabled);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            Dialog.Tell(Owner, DialogKind.Warning, entry.Title, ex.Message);
        }
        Render();
    }

    /// <summary>A heading sorts My Mods by its column; the same heading again reverses it.</summary>
    void SortBy(object? sender, TappedEventArgs e)
    {
        if (sender is not TextBlock { Tag: string tag } || !Enum.TryParse(tag, out ModSort column)) return;
        _descending = column == _sort && !_descending;
        _sort = column;
        Filter();
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
        var shown = _items.Where(i => i.SearchText.Contains(text, StringComparison.OrdinalIgnoreCase)).ToList();
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

    void OpenPage(BrowseItem? item, ModEntry? entry)
    {
        if (item is null && entry is null) return;
        _pageItem = item;
        _pageEntry = entry;
        _form = null;
        _pageReadme = "";
        InstalledView.IsVisible = BrowseView.IsVisible = false;
        ModPage.IsVisible = true;
        PageTabs.SelectedItem = ModOverviewPage;
        RefreshPage();
        _ = LoadReadmeAsync(item, entry);
    }

    void ClosePage(object? sender, RoutedEventArgs e)
    {
        if (!PageOpen) return;
        ModPage.IsVisible = false;
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
        string title = item?.Title ?? entry!.Title;
        PageInitial.Text = ModsText.Initial(title);
        PageTitle.Text = title;
        string author = entry?.Author is { Length: > 0 } a ? a : item?.Author ?? "";
        PageByline.Text = author.Length > 0 ? L.Text("by {0}", author) : "";
        PageDescription.Text = item?.Listing.Description is { Length: > 0 } d ? d : entry?.Description ?? "";
        PageTags.ItemsSource = ModsText.Tags(item, entry);
        PageStats.Text = ModsText.Stats(item, entry);
        PageEnabled.IsVisible = entry is not null;
        PageEnabled.IsChecked = entry?.Enabled == true;
        PageEnabled.IsEnabled = entry?.Fingerprint.Length > 0;
        PageAction.IsVisible = item is not null;
        ShowPageJob(_browser.Job(item?.Listing.Id ?? entry!.Id));
        PageAction.IsEnabled = item?.CanAct == true && _pageJob is null && CanUse;
        var history = item is null ? [] : _browser.View(item.Source)?.History.GetValueOrDefault(item.Listing.Id) ?? [];
        VersionList.ItemsSource = history.Select(r => new VersionRow(item!, r)).ToList();
        ModVersionsPage.IsVisible = history.Count > 0;
        ModSettingsPage.IsVisible = entry?.SettingsFile.Length > 0;
        if (PageTabs.SelectedItem is TabItem { IsVisible: false }) PageTabs.SelectedItem = ModOverviewPage;
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
        string? shown = _form?.Id;
        if (entry is null || entry.SettingsFile.Length == 0 || State is not { } state)
        {
            _form = null;
            SettingsPanel.Children.Clear();
            return;
        }
        SaveSettingsButton.IsEnabled = ResetSettingsButton.IsEnabled = CanUse;
        // A refresh redraws the page; keep what the player is typing in the form of the same mod.
        if (shown == entry.Id && SettingsPanel.Children.Count > 0) return;
        _inputs.Clear();
        SettingsPanel.Children.Clear();
        try { _form = ModSettingsForm.Load(entry.Id, entry.SettingsFile, state.Root); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            _form = null;
            SettingsHint.Text = L.Text("Could not read this mod's settings: ") + e.Message;
            SaveSettingsButton.IsEnabled = ResetSettingsButton.IsEnabled = false;
            return;
        }
        foreach (var setting in _form.Settings) SettingsPanel.Children.Add(Field(_form, setting));
        SettingsHint.Text = L.Text("Saved for this checkout in {0}; a running Gateway reloads the mod.", ModSettingsForm.UserFile(entry.Id));
    }

    static TextBlock Note(string text)
    {
        var note = new TextBlock { Text = text, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
        note.Classes.Add("note");
        return note;
    }

    /// <summary>The control of one setting, and how to read it back.</summary>
    Control Field(ModSettingsForm form, ModSetting setting)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        var current = form.Current(setting);
        if (setting.Type != "bool") panel.Children.Add(new TextBlock { Text = ModsText.Heading(setting), FontWeight = FontWeight.Medium });
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
            case "choice" when setting.Source.Length > 0:
            {
                // Any of the game's values of its kind (an item, a monster), typed; the Gateway checks the name when the mod loads.
                var box = new AutoCompleteBox { Text = ModSettingsForm.Show(current), FilterMode = AutoCompleteFilterMode.ContainsOrdinal, Margin = new Thickness(0, 2, 0, 0) };
                _ = FillValuesAsync(box, setting.Source);
                panel.Children.Add(box);
                _inputs[setting.Name] = () => box.Text;
                break;
            }
            case "choice":
            {
                var box = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 2, 0, 0) };
                foreach (var option in setting.Options) box.Items.Add(new ComboBoxItem { Content = option.Label, Tag = option.Value });
                box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(i => JsonNode.DeepEquals((JsonNode)i.Tag!, current));
                panel.Children.Add(box);
                _inputs[setting.Name] = () => box.SelectedItem is ComboBoxItem { Tag: JsonNode chosen } ? chosen : "";
                break;
            }
            case "choices" when setting.Options.Count > 0:
            {
                var chosen = current as JsonArray ?? [];
                var boxes = setting.Options.Select(o => (o.Value, Box: new CheckBox { Content = o.Label, IsChecked = chosen.Any(c => JsonNode.DeepEquals(c, o.Value)) })).ToList();
                var wrap = new WrapPanel { ItemSpacing = 12 };
                foreach (var (_, box) in boxes) wrap.Children.Add(box);
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

    /// <summary>The game's values of a kind, when the Gateway can list them.</summary>
    static async Task FillValuesAsync(AutoCompleteBox box, string kind)
    {
        try { box.ItemsSource = (await CommandCatalog.ValuesAsync(kind)).Take(5000).Select(v => v.Value).ToList(); }
        catch (Exception e) when (e is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or System.Text.Json.JsonException or TimeoutException) { }
    }

    async void SaveSettings(object? sender, RoutedEventArgs e)
    {
        if (_form is null || _pageEntry is not { } entry) return;
        var (values, errors) = ModsText.Collect(_form, _inputs);
        if (errors.Count > 0)
        {
            Dialog.Tell(Owner, DialogKind.Warning, L.Text("Settings not saved"), string.Join("\n", errors));
            return;
        }
        await SaveValuesAsync(entry.Id, values);
    }

    async void ResetSettings(object? sender, RoutedEventArgs e)
    {
        if (_form is null || _pageEntry is not { } entry) return;
        if (!Dialog.Ask(Owner, DialogKind.Question, L.Text("Reset to defaults"), L.Text("Put every setting Waypoint edits back to its default?"), L.Text("Reset"), L.Text("Cancel"))) return;
        await SaveValuesAsync(entry.Id, ModsText.Defaults(_form, _inputs));
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
            await SaveValuesAsync(entry.Id, ModsText.Defaults(form, editable));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            Dialog.Tell(Owner, DialogKind.Warning, L.Text("Settings not saved"), ex.Message);
        }
    }

    async Task SaveValuesAsync(string id, JsonObject values)
    {
        var result = await _browser.SaveSettingsAsync(id, values);
        if (result is { Ok: false }) ShowLog(true);
        if (result?.Ok == true && _pageEntry?.Id == id)
        {
            // Read the file back, so the form shows what the Gateway will.
            SettingsPanel.Children.Clear();
            ShowSettings(_pageEntry);
        }
    }
}
