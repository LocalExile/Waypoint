using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
#if WINDOWS
using System.Windows;
using System.Windows.Controls;
using TextWrapping = System.Windows.TextWrapping;
using AccessibleElement = System.Windows.FrameworkElement;
using A11y = System.Windows.Automation.AutomationProperties;
using LiveSetting = System.Windows.Automation.AutomationLiveSetting;
#else
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Threading;
using TextWrapping = Avalonia.Media.TextWrapping;
using AccessibleElement = Avalonia.Controls.Control;
using A11y = Avalonia.Automation.AutomationProperties;
using LiveSetting = Avalonia.Automation.AutomationLiveSetting;
#endif

namespace Waypoint;

/// <summary>Declared player flags, linked modes and guarded public-file customization, shared by both front ends.</summary>
public sealed partial class GameSystemsTab : UserControl
{
    public bool LeagueView { get; set; }
    internal event Action? OpenLeaguesRequested;
    internal event Action? OpenModsRequested;
    Shared _shared = null!;
    GameSystems? _catalog;
    readonly StackPanel _detail = new(), _header = new();
    readonly StackPanel _toolbar = new() { Orientation = Orientation.Horizontal };
    readonly StackPanel _actions = new() { Orientation = Orientation.Horizontal };
    readonly ScrollViewer _contentScroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    readonly ListBox _list = new() { Width = 218 };
    readonly TextBlock _status = SettingsEditor.Note(""), _validation = SettingsEditor.Note("");
    readonly TextBlock _flagSummary = SettingsEditor.Note("");
    readonly CheckBox _hiddenDefinitions = new();
    bool _changingFilter;
    bool _settingsExpanded;
    readonly Dictionary<string, CheckBox> _checks = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _draftModules = new(StringComparer.OrdinalIgnoreCase);
    SettingsEditor? _editor;
    object? _selected, _selectedRow;
    bool _selecting, _choicesDirty, _creating, _refreshing, _applying, _liveRunning;
    Button? _primary, _discardAction, _settingsAction, _availabilityAction, _newAction;
    Button? _refreshAction;
    Border? _footerCard;
    TextBlock? _availabilityHint;
    ComboBox? _modeSelector;
    Expander? _leagueSettings;
    string _settingsHash = "missing", _settingsId = "", _settingsKind = "";
    bool Dirty => _creating || _editor?.Dirty == true || _choicesDirty;
    bool Busy => _refreshing || _applying || _liveRunning;
    bool WritersStopped => string.IsNullOrEmpty(_shared.Workspace.Latest?.AccountWriteBlockedReason);
    Window? Owner =>
#if WINDOWS
        Window.GetWindow(this);
#else
        TopLevel.GetTopLevel(this) as Window;
#endif

    internal void Attach(Shared shared)
    {
        _shared = shared;
        var shell = new DockPanel { Margin = new Thickness(18, 8, 18, 12) };
        var heading = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        var headingRow = new DockPanel { Margin = new Thickness(0, 0, 0, 16) };
        _refreshAction = Named(SettingsEditor.Button(L.Text("Refresh"), () => _ = RefreshAsync(discard: true)), L.Text("Refresh"));
        _refreshAction.VerticalAlignment = VerticalAlignment.Top; _refreshAction.Margin = new Thickness(16, 0, 0, 0);
        DockPanel.SetDock(_refreshAction, Dock.Right); headingRow.Children.Add(_refreshAction);
        headingRow.Children.Add(_header); heading.Children.Add(headingRow); UpdateHeading();
        if (LeagueView) { _newAction = SettingsEditor.Button(L.Text("New league"), () => StartCreate()); Primary(_newAction); _toolbar.Children.Add(_newAction); }
        if (LeagueView)
        {
            _hiddenDefinitions.Content = L.Text("Show hidden definitions");
            Named(_hiddenDefinitions, L.Text("Show hidden definitions"));
            _hiddenDefinitions.Margin = new Thickness(18, 0, 0, 0);
            _hiddenDefinitions.VerticalAlignment = VerticalAlignment.Center;
            _hiddenDefinitions.Click += (_, _) => ChangePresentationFilter();
            _toolbar.Children.Add(_hiddenDefinitions);
        }
        heading.Children.Add(_toolbar);
        if (LeagueView) { Ink(_flagSummary, "Shell.TextSecondary"); _flagSummary.Margin = new Thickness(0, 10, 0, 0); heading.Children.Add(_flagSummary); }
        DockPanel.SetDock(heading, Dock.Top); shell.Children.Add(heading);
        if (LeagueView)
        {
            var globalMods = CreateGlobalModsBar();
            DockPanel.SetDock(globalMods, Dock.Top); shell.Children.Add(globalMods);
            UpdateGlobalMods();
        }
        var footer = new StackPanel();
        _validation.FontSize = 12; _validation.Margin = new Thickness(0, 0, 0, 6); Ink(_validation, "Shell.TextSecondary");
        _status.FontSize = 12; Ink(_status, "Shell.TextSecondary"); SettingsEditor.Show(_status, false);
        _actions.HorizontalAlignment = HorizontalAlignment.Right;
        footer.Children.Add(_validation); footer.Children.Add(_status); footer.Children.Add(_actions);
        A11y.SetLiveSetting(_status, LiveSetting.Polite); A11y.SetLiveSetting(_validation, LiveSetting.Polite);
        var footerCard = Card(footer, 10); footerCard.Margin = new Thickness(238, 12, 0, 0);
        _footerCard = footerCard;
        DockPanel.SetDock(footerCard, Dock.Bottom); shell.Children.Add(footerCard);
        Named(_list, L.Text(LeagueView ? "Leagues" : "Modules"));
        DressNavigation();
        if (LeagueView)
        {
            var navigation = new DockPanel { Width = 218, Margin = new Thickness(0, 0, 20, 0) };
            var caption = Type(L.Text("League flags"), 12, true, "Shell.TextSecondary"); caption.Margin = new Thickness(0, 0, 0, 10);
            DockPanel.SetDock(caption, Dock.Top); navigation.Children.Add(caption); navigation.Children.Add(_list);
            DockPanel.SetDock(navigation, Dock.Left); shell.Children.Add(navigation);
        }
        else
        {
            var navigation = new DockPanel { Width = 218, Margin = new Thickness(0, 0, 20, 0) };
            var filters = new StackPanel();
            filters.Children.Add(LabelFor(_moduleSearch, L.Text("Find a module"))); filters.Children.Add(Card(_moduleSearch, 6, "Shell.Background"));
            _moduleCount.FontSize = 12; Ink(_moduleCount, "Shell.TextSecondary"); _moduleCount.Margin = new Thickness(0, 10, 0, 10); filters.Children.Add(_moduleCount);
            DockPanel.SetDock(filters, Dock.Top); navigation.Children.Add(filters);
            _moduleEmpty.Text = L.Text("No modules match your search."); _moduleEmpty.TextWrapping = TextWrapping.Wrap; Ink(_moduleEmpty, "Shell.TextSecondary");
            SettingsEditor.Show(_moduleEmpty, false); DockPanel.SetDock(_moduleEmpty, Dock.Top); navigation.Children.Add(_moduleEmpty);
            navigation.Children.Add(_list); DockPanel.SetDock(navigation, Dock.Left); shell.Children.Add(navigation);
            _moduleSearch.TextChanged += (_, _) => FilterModules();
        }
        _contentScroll.Content = _detail; shell.Children.Add(_contentScroll);
        Content = shell;
        _list.SelectionChanged += (_, _) => SelectionChanged();
        Loaded += (_, _) => { if (_catalog is null) _ = RefreshAsync(); };
        shared.Workspace.Changed += () => Dispatch(() =>
        {
            UpdateGlobalMods();
            if (_catalog is null && shared.Workspace.Latest?.Content is not null) _ = RefreshAsync();
            else UpdateFeedback();
        });
        shared.LiveRunning += running => Dispatch(() => { _liveRunning = running; UpdateFeedback(); });
        L.Current.PropertyChanged += (_, _) => Dispatch(() => { UpdateGlobalMods(); if (!Dirty && !Busy) { UpdateHeading(); RenderSelection(); } });
#if WINDOWS
        Theme.Changed += RefreshFlagColors;
#endif
    }
    void UpdateHeading()
    {
        _header.Children.Clear();
        _header.Children.Add(new ContentSectionHeading { Section = LeagueView ? ManagedContentSection.Leagues : ManagedContentSection.Modules });
    }
    static T Named<T>(T control, string name) where T : AccessibleElement { A11y.SetName(control, name); return control; }
    static TextBlock LabelFor(AccessibleElement control, string text)
    { var label = Type(text, 12, true, "Shell.TextSecondary"); label.Margin = new Thickness(0, 0, 0, 6); A11y.SetName(control, text); A11y.SetLabeledBy(control, label); return label; }
    static void Dispatch(Action action)
    {
#if WINDOWS
        System.Windows.Application.Current.Dispatcher.BeginInvoke(action);
#else
        Dispatcher.UIThread.Post(action);
#endif
    }
    void SetStatus(string text)
    {
        _status.Text = text;
        SettingsEditor.Show(_status, text.Length > 0);
        UpdateFooterVisibility();
#if WINDOWS
        System.Windows.Automation.Peers.UIElementAutomationPeer.FromElement(_status)?.RaiseAutomationEvent(System.Windows.Automation.Peers.AutomationEvents.LiveRegionChanged);
#endif
    }
    void UpdateLock()
    {
        _detail.IsEnabled = _list.IsEnabled = _toolbar.IsEnabled = _actions.IsEnabled = _moduleSearch.IsEnabled = !Busy;
        if (_refreshAction is not null) _refreshAction.IsEnabled = !Busy;
        if (_newAction is not null) _newAction.IsEnabled = !_creating && _catalog?.Environment.CanCreate == true;
    }
    void UpdateFooterVisibility()
    {
        if (_footerCard is not null) SettingsEditor.Show(_footerCard, LeagueView || _editor is not null || _status.Text is { Length: > 0 });
    }
    Button FooterButton(string text, Action action, bool primary = false)
    {
        var button = Named(SettingsEditor.Button(text, action), text);
        button.Margin = new Thickness(0, 0, 8, 0);
        if (primary) Primary(button);
        _actions.Children.Add(button); return button;
    }
    bool Discard() => !Dirty || Dialog.Ask(Owner, DialogKind.Question, L.Text("Unsaved changes"), L.Text("Discard the changes you have not saved?"), L.Text("Discard"), L.Text("Cancel"));
    void DiscardCurrent() { if (!Busy && Discard()) RenderSelection(focusMode: true); }
    async Task RefreshAsync(bool discard = false)
    {
        if (Busy || discard && !Discard() || !discard && Dirty) return;
        _refreshing = true; UpdateLock(); SetStatus(L.Text("Refreshing leagues and modules…"));
        try
        {
            // Inputs are disabled throughout the await: a late refresh cannot replace an intervening edit.
            await _shared.Workspace.RefreshAsync();
            if (_shared.Workspace.Latest?.Content is not { } environment) { SetStatus(L.Text("Refresh the workspace to inspect modules and leagues.")); return; }
            string? previous = _selected?.ToString();
            _catalog = new GameSystems(environment);
            RefreshModuleIcons();
            RefreshFlagColors();
            if (LeagueView) _nativeArtwork = await LeagueArtwork.LoadAsync(_shared.Workspace.Latest.Root);
            _selecting = true;
            var items = LeagueView ? PresentationCards().Cast<object>().ToArray() : MatchingModules().Cast<object>().ToArray();
            _list.ItemsSource = items;
            var previousLeague = _catalog.Leagues.FirstOrDefault(l => l.Name == previous);
            _list.SelectedItem = LeagueView ? items.OfType<GameLeagueCard>().FirstOrDefault(f => f.Variants.Any(l => l.Name == previous)) ?? items.FirstOrDefault()
                : items.FirstOrDefault(i => i.ToString() == previous) ?? items.FirstOrDefault();
            _selectedRow = _list.SelectedItem;
            _selected = _selectedRow is GameLeagueCard card ? previousLeague is not null && CardModes(card).Contains(previousLeague) ? previousLeague : card.Primary : _selectedRow;
            UpdateFlagSummary();
            if (!LeagueView) UpdateModuleCount(items.Length);
            _selecting = false; RenderSelection();
            if (_selected is null && (LeagueView || _catalog.Systems.Count == 0)) SetStatus(L.Text("No modules or leagues were found in this installation."));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException or InvalidOperationException or ArgumentException)
        { _selecting = false; SetStatus(e.Message); }
        finally { _refreshing = false; UpdateLock(); UpdateFeedback(); }
    }
    void SelectionChanged()
    {
        if (_selecting || Busy || ReferenceEquals(_list.SelectedItem, _selectedRow)) return;
        if (!Discard()) { _selecting = true; _list.SelectedItem = _selectedRow; _selecting = false; return; }
        _selectedRow = _list.SelectedItem;
        _selected = _selectedRow is GameLeagueCard card ? card.Primary : _selectedRow;
        RenderSelection();
    }
    void ClearView()
    {
#if WINDOWS
        _contentScroll.ScrollToTop();
#else
        _contentScroll.Offset = default;
#endif
        _detail.Children.Clear(); _actions.Children.Clear(); _checks.Clear(); _editor = null;
        _primary = _discardAction = _settingsAction = _availabilityAction = null; _availabilityHint = null; _modeSelector = null;
        _leagueSettings = null;
        _settingsId = ""; _validation.Text = ""; SetStatus("");
    }
    void RenderSelection(bool focusMode = false)
    {
        _creating = false; _choicesDirty = false; ClearView();
        if (_catalog is null || _selected is null) return;
        if (_selected is GameSystem system) RenderModule(system);
        else if (_selected is GameLeague league) RenderLeague(league);
        if (LeagueView || _editor is not null) _discardAction = FooterButton(L.Text("Discard changes"), DiscardCurrent);
        UpdateFeedback(); UpdateLock();
        if (focusMode && _modeSelector is not null) Dispatch(() => _modeSelector.Focus());
    }
    IEnumerable<GameLeagueCard> PresentationCards() => _catalog!.Cards.Where(c => c.Visible || _hiddenDefinitions.IsChecked == true);
    GameLeague[] CardModes(GameLeagueCard card) => card.Variants.Where(l => _hiddenDefinitions.IsChecked == true || _catalog!.ReachableMode(l)).ToArray();
    void UpdateFlagSummary()
    {
        if (!LeagueView || _catalog is null) return;
        _flagSummary.Text = L.Text("{0} flags at the next server start. The running game may still use earlier definitions.", _catalog.Cards.Count(c => c.Visible));
        if (_catalog.Leagues.Any(l => _catalog.OfferedDefinition(l) && !_catalog.ReachableMode(l)))
            _flagSummary.Text += "\n" + L.Text("Some mode links cannot be previewed reliably. Show hidden definitions to inspect them.");
    }
    void ChangePresentationFilter()
    {
        if (_catalog is null || _changingFilter) return;
        if (!Discard())
        {
            _changingFilter = true; _hiddenDefinitions.IsChecked = _hiddenDefinitions.IsChecked != true; _changingFilter = false; return;
        }
        string? previous = (_selected as GameLeague)?.Name;
        _selecting = true;
        var cards = PresentationCards().ToArray(); _list.ItemsSource = cards;
        var selectedCard = cards.FirstOrDefault(c => CardModes(c).Any(l => l.Name == previous)) ?? cards.FirstOrDefault();
        _list.SelectedItem = selectedCard; _selectedRow = selectedCard;
        _selected = selectedCard is null ? null : CardModes(selectedCard).FirstOrDefault(l => l.Name == previous) ?? selectedCard.Primary;
        _selecting = false; RenderSelection();
    }
    void AddFlagPreview(GameLeagueCard? card, GameLeague league)
    {
        if (card is null || _catalog is null) return;
        var root = card.Primary;
        var hero = new DockPanel();
        var pennant = Pennant(league.Hardcore, large: true, flagName: card.Name); pennant.Margin = new Thickness(0, 0, 18, 0);
        pennant.VerticalAlignment = VerticalAlignment.Top; DockPanel.SetDock(pennant, Dock.Left); hero.Children.Add(pennant);
        var body = new StackPanel();
        var badges = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        badges.Children.Add(Badge(card.IsFlag ? L.Text("League flag") : L.Text("Not shown"), "Shell.Accent"));
        if (root.Hardcore) { var hc = Badge(L.Text("Hardcore")); hc.Margin = new Thickness(8, 0, 0, 0); badges.Children.Add(hc); }
        var title = new DockPanel(); DockPanel.SetDock(badges, Dock.Right); title.Children.Add(badges); title.Children.Add(Type(card.Name, 25, true)); body.Children.Add(title);
        var settingsButton = SettingsEditor.Button("⚙", () =>
        {
            if (_leagueSettings is null) return;
            _leagueSettings.IsExpanded = true;
#if WINDOWS
            _contentScroll.ScrollToTop();
#else
            _contentScroll.Offset = default;
#endif
        });
        Named(settingsButton, L.Text("League settings")); settingsButton.Padding = new Thickness(7, 3, 7, 3); settingsButton.Margin = new Thickness(8, 0, 0, 0);
        badges.Children.Add(settingsButton);
        var description = Type(root.Description.StartsWith('#') ? L.Text("Description uses the game's text key: {0}", root.Description) : root.Description,
            13, false, "Shell.TextSecondary"); description.Margin = new Thickness(0, 8, 0, 0); body.Children.Add(description);
        if (_nativeArtwork is null) { var placeholder = Type(L.Text("Game artwork unavailable — placeholder flag shown."), 12, false, "Shell.TextSecondary"); placeholder.Margin = new Thickness(0, 8, 0, 0); body.Children.Add(placeholder); }
        if (!_catalog.ReachableMode(league))
        {
            var warning = Type(L.Text("Not offered: hidden, ended, unloaded or unreachable through valid mode links. Inspect this definition before showing it."), 12, false, "Shell.Warning");
            warning.Margin = new Thickness(0, 10, 0, 0); body.Children.Add(warning);
        }
        if (!card.IsFlag) body.Children.Add(Type(L.Text("Unlinked mode definition — this is not a player flag."), 12, false, "Shell.Warning"));
        hero.Children.Add(body); var surface = Card(hero, 18); surface.Margin = new Thickness(0, 0, 0, 12); _detail.Children.Add(surface);
    }
    void AddModeLinks(StackPanel panel, GameLeagueCard card)
    {
        if (_catalog is null) return;
        var links = card.Variants.Where(l => !l.IsMain && (_hiddenDefinitions.IsChecked == true || _catalog.ReachableMode(l))).ToArray();
        bool uncertainLinks = card.Variants.Any(l => !l.IsMain && _catalog.OfferedDefinition(l) && !_catalog.ReachableMode(l));
        if (links.Length == 0 && !uncertainLinks)
        { var note = Type(L.Text("No linked mode options on this flag."), 12, false, "Shell.TextSecondary"); note.Margin = new Thickness(0, 8, 0, 0); panel.Children.Add(note); }
        else
        {
            var options = new StackPanel { Margin = new Thickness(12, 4, 0, 8) };
            foreach (var variant in links)
            {
                var path = Type(L.Text("{0}: {1}", _catalog.ModePath(variant), variant.Name)
                    + (_catalog.ReachableMode(variant) ? "" : " · " + L.Text("Not shown")), 12, false, "Shell.TextSecondary");
                path.Margin = new Thickness(0, 6, 0, 6); options.Children.Add(path);
            }
            panel.Children.Add(new Expander { Header = Type(L.Text("In-game mode links and combinations ({0})", links.Count(_catalog.ReachableMode)), 12, true),
                Content = options, Margin = new Thickness(0, 10, 0, 0) });
        }
    }
    void RenderLeague(GameLeague league)
    {
        var card = _selectedRow as GameLeagueCard;
        AddFlagPreview(card, league);
        var modePanel = new StackPanel();
        var fields = new Grid(); fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.7, GridUnitType.Star) });
        fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var choiceArea = new StackPanel { Margin = new Thickness(0, 0, 20, 0) };
        var availabilityArea = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(availabilityArea, 1); fields.Children.Add(choiceArea); fields.Children.Add(availabilityArea); modePanel.Children.Add(fields);
        var variants = card is null ? [league] : CardModes(card);
        _modeSelector = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 0, 0, 8) };
        var modeTitle = LabelFor(_modeSelector, L.Text("Game mode")); modeTitle.FontSize = 16; Ink(modeTitle, "Shell.Text"); choiceArea.Children.Add(modeTitle);
        A11y.SetHelpText(_modeSelector, L.Text("Choose a mode to manage its modules and availability. Each mode keeps its own characters and stash."));
        foreach (var variant in variants) _modeSelector.Items.Add(ModeItem(variant, variants));
        _modeSelector.SelectedItem = _modeSelector.Items.OfType<ComboBoxItem>().First(i => ReferenceEquals(i.Tag, league));
        bool reverting = false;
        _modeSelector.SelectionChanged += (_, _) =>
        {
            if (reverting || Busy || (_modeSelector.SelectedItem as ComboBoxItem)?.Tag is not GameLeague next || ReferenceEquals(next, _selected)) return;
            if (!Discard()) { reverting = true; _modeSelector.SelectedItem = _modeSelector.Items.OfType<ComboBoxItem>().First(i => ReferenceEquals(i.Tag, _selected)); reverting = false; return; }
            _selected = next; RenderSelection(focusMode: true);
        };
        choiceArea.Children.Add(_modeSelector);
        var selectedProfile = Type(L.Text("Selected league: {0} · {1}", league.Name, _catalog!.Modes(league)), 12, false, "Shell.TextSecondary");
        selectedProfile.Margin = new Thickness(0); choiceArea.Children.Add(selectedProfile);
        var availability = new StackPanel { Orientation = Orientation.Horizontal };
        var visible = Badge(L.Text(_catalog!.ReachableMode(league) ? "Visible to new characters" : "Not shown at character creation"));
        visible.Margin = new Thickness(0, 0, 8, 0); availability.Children.Add(visible);
        _availabilityAction = SettingsEditor.Button(L.Text(league.Creatable ? "Hide…" : "Show…"), () => _ = SetVisibilityAsync(league));
        Named(_availabilityAction, L.Text(league.Creatable ? "Hide from new characters" : "Show for new characters"));
        availability.Children.Add(_availabilityAction); availabilityArea.Children.Add(availability);
        _availabilityHint = SettingsEditor.Note(""); Ink(_availabilityHint, "Shell.TextSecondary"); availabilityArea.Children.Add(_availabilityHint);
        if (card is not null) AddModeLinks(modePanel, card);
        var settings = new StackPanel { Margin = new Thickness(16) };
        if (card?.IsFlag == true) AddFlagAppearance(settings, card);
        modePanel.Margin = new Thickness(0, 20, 0, 0); settings.Children.Add(modePanel);
        if (File.Exists(league.SettingsFile)) OpenSettings(settings, Path.GetFileName(league.Folder), league.SettingsFile, "leagues");
        var modeDisclosure = new Expander { Header = Type(L.Text("League settings"), 14, true), Content = settings, IsExpanded = _settingsExpanded };
        _leagueSettings = modeDisclosure;
#if WINDOWS
        modeDisclosure.Expanded += (_, _) => _settingsExpanded = true; modeDisclosure.Collapsed += (_, _) => _settingsExpanded = false;
#else
        modeDisclosure.PropertyChanged += (_, e) => { if (e.Property == Expander.IsExpandedProperty) _settingsExpanded = modeDisclosure.IsExpanded; };
#endif
        var modeCard = Card(modeDisclosure, 0); modeCard.Margin = new Thickness(0, 0, 0, 12); _detail.Children.Add(modeCard);
        AddModuleChoices(_detail, league, _catalog!.Environment.CanChoose && _catalog.Configurable(league));
        _primary = FooterButton(L.Text("Save module choices"), () => _ = SaveChoicesAsync(league), primary: true);
        var details = new StackPanel { Margin = new Thickness(12, 8, 0, 8) };
        details.Children.Add(SettingsEditor.Note(league.Description));
        details.Children.Add(SettingsEditor.Note(L.Text("Modes are fixed for existing characters. Create a separate league to choose another defined mode.")));
        var customize = SettingsEditor.Button(L.Text("Customize as a new league"), () => StartCreate(league));
        bool reusable = !league.Custom && _catalog.Configurable(league) && GameSystems.EmptyTemplate(league.Folder);
        customize.IsEnabled = reusable && _catalog.Environment.CanCreate; details.Children.Add(customize);
        if (!reusable) details.Children.Add(SettingsEditor.Note(L.Text("This league has custom code or no reusable source template. It cannot be safely copied.")));
        details.Children.Add(SettingsEditor.Note(L.Text("Only combinations defined for this league and mode are allowed. Dedicated gameplay bundles stay together; unknown compatibility is blocked.")));
        details.Children.Add(SettingsEditor.Label(L.Text("Selected league definition: {0}", league.Name)));
        if (league.EntryOf.Length > 0) details.Children.Add(SettingsEditor.Note(L.Text("Shown as a mode of: {0}", league.EntryOf)));
        if (league.Parent.Length > 0) details.Children.Add(SettingsEditor.Note(L.Text("Parent league: {0}", league.Parent)));
        _detail.Children.Add(new Expander { Header = L.Text("League details and compatibility"), Content = details, IsExpanded = false, Margin = new Thickness(0, 12, 0, 8) });
    }
    static string ModuleTitle(GameSystem module) => module.Name == module.Id
        ? module.Id == "Game" ? L.Text("Core game rules") : Regex.Replace(module.Name, @"(?<=[a-z0-9])(?=[A-Z])", " ").Replace("Npc ", "NPC ") : module.Name;
    TextBox AddModuleChoices(StackPanel panel, GameLeague league, bool editable)
    {
        _checks.Clear();
        var body = new StackPanel(); var section = Card(body); section.Margin = new Thickness(0, 0, 0, 12); panel.Children.Add(section); panel = body;
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        var searchArea = new StackPanel { Width = 220, Margin = new Thickness(16, 0, 0, 0) };
        DockPanel.SetDock(searchArea, Dock.Right); header.Children.Add(searchArea);
        var heading = new StackPanel { VerticalAlignment = VerticalAlignment.Center }; header.Children.Add(heading); panel.Children.Add(header);
        heading.Children.Add(Type(L.Text("Gameplay modules"), 18, true));
        var search = new TextBox { MinHeight = 28 };
        searchArea.Children.Add(LabelFor(search, L.Text("Find a module")));
        searchArea.Children.Add(Card(search, 6, "Shell.Background"));
        var count = Type("", 12, false, "Shell.TextSecondary"); count.Margin = new Thickness(0, 6, 0, 0); heading.Children.Add(count);
        var rows = new List<(AccessibleElement Row, string Text, bool Unavailable)>();
        var unavailable = new StackPanel { Margin = new Thickness(12, 6, 0, 6) };
        var required = _catalog!.RequiredModules(league);
        void Count() => count.Text = L.Text("{0} modules enabled · {1} required", _checks.Count(p => p.Value.IsChecked == true), required.Count);
        foreach (var module in _catalog.Systems)
        {
            bool selected = (_creating ? (IEnumerable<string>)_draftModules : league.Systems).Contains(module.Path, StringComparer.OrdinalIgnoreCase);
            bool offered = _catalog.ModuleOffered(league, module.Path), locked = required.Contains(module.Path);
            string name = ModuleTitle(module);
            var box = Named(new CheckBox { Content = ModuleLabel(module), IsChecked = selected, IsEnabled = editable && !locked && (selected || offered), HorizontalAlignment = HorizontalAlignment.Stretch }, name);
            _checks[module.Path] = box;
            AccessibleElement row;
            if (locked || !editable || !offered && !selected)
            {
                var readOnly = new DockPanel();
                var state = Badge(locked ? L.Text("Required") : !offered ? L.Text("Not defined for this mode") : selected ? L.Text("Enabled") : L.Text("Disabled"));
                state.Margin = new Thickness(12, 0, 0, 0); DockPanel.SetDock(state, Dock.Right); readOnly.Children.Add(state);
                var mark = Type(selected ? "✓" : "–", 13, false, "Shell.TextSecondary"); mark.Margin = new Thickness(0, 0, 12, 0);
                DockPanel.SetDock(mark, Dock.Left); readOnly.Children.Add(mark); readOnly.Children.Add(ModuleLabel(module)); row = readOnly;
            }
            else row = box;
            var holder = Card(row, 10, "Shell.Background"); holder.Margin = new Thickness(0, 0, 0, 6);
            Named(holder, name + (locked ? " · " + L.Text("Required") : ""));
            string help = module.Description.Length > 0 ? module.Description : locked ? L.Text("Required by this league's gameplay bundle.") : !offered ? L.Text("Not defined for this mode") : L.Text("Optional module for this mode.");
            if (module.Depends.Length > 0) help += "\n" + L.Text("Dependencies: {0}", string.Join(", ", module.Depends));
            A11y.SetHelpText(box, help);
            A11y.SetHelpText(holder, help);
#if WINDOWS
            holder.ToolTip = help;
#else
            ToolTip.SetTip(holder, help);
#endif
            box.Click += (_, _) =>
            {
                _choicesDirty = true;
                if (_creating) { _draftModules.Clear(); foreach (var path in _checks.Where(p => p.Value.IsChecked == true).Select(p => p.Key)) _draftModules.Add(path); }
                Count(); UpdateFeedback();
            };
            bool isUnavailable = !selected && !offered && !locked;
            (isUnavailable ? unavailable : panel).Children.Add(holder);
            rows.Add((holder, name + " " + module.Id + " " + module.Description, isUnavailable));
        }
        foreach (string path in (_creating ? _draftModules : league.Systems.AsEnumerable()).Where(p => !_checks.ContainsKey(p)))
        {
            // Retain unresolved choices in the draft. A missing installation must never silently remove a saved module.
            _checks[path] = new CheckBox { IsChecked = true, IsEnabled = false };
            var missing = SettingsEditor.Note(L.Text("Missing module: {0}. Restore it before changing this league.", path));
            panel.Children.Add(missing); rows.Add((missing, path, false));
        }
        Expander? unavailableGroup = null;
        if (unavailable.Children.Count > 0) { unavailableGroup = new Expander { Header = L.Text("Unavailable modules ({0})", unavailable.Children.Count), Content = unavailable, IsExpanded = false }; panel.Children.Add(unavailableGroup); }
        var empty = SettingsEditor.Note(L.Text("No modules match your search.")); SettingsEditor.Show(empty, false); panel.Children.Add(empty);
        search.TextChanged += (_, _) =>
        {
            var words = (search.Text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries); int shown = 0, unavailableShown = 0;
            foreach (var row in rows)
            {
                bool match = words.All(w => row.Text.Contains(w, StringComparison.CurrentCultureIgnoreCase));
                SettingsEditor.Show(row.Row, match);
                if (match) { shown++; if (row.Unavailable) unavailableShown++; }
            }
            if (unavailableGroup is not null)
            {
                SettingsEditor.Show(unavailableGroup, unavailableShown > 0);
                unavailableGroup.Header = L.Text("Unavailable modules ({0})", unavailableShown);
                unavailableGroup.IsExpanded = words.Length > 0;
            }
            SettingsEditor.Show(empty, shown == 0);
        };
        Count(); return search;
    }
    string[] SelectedModules() => _creating ? _draftModules.ToArray() : _checks.Where(p => p.Value.IsChecked == true).Select(p => p.Key).ToArray();
    ComboBoxItem ModeItem(GameLeague league, IReadOnlyList<GameLeague> variants) => new()
    { Content = _catalog!.ModePath(league) + (variants.Count(l => _catalog.ModePath(l) == _catalog.ModePath(league)) > 1 ? " (" + league.Name + ")" : "")
        + (_catalog.ReachableMode(league) ? "" : " · " + L.Text("Not shown")), Tag = league };
    void UpdateFeedback()
    {
        if (_catalog is null) { UpdateLock(); return; }
        if (_creating) UpdateDraftFeedback();
        else if (_selected is GameLeague league)
        {
            string? error = _catalog.CompositionError(league, SelectedModules());
            _validation.Text = error is not null ? L.Text("Cannot save: {0}", error) : !_catalog.Environment.CanChoose ? L.Text("Read-only module list. Update LocalExile to edit these choices.")
                : _editor?.Dirty == true ? L.Text("Save league settings before applying module choices.") : _choicesDirty ? L.Text("Unsaved module choices — applies after a server restart.") : L.Text("No unsaved changes.");
            if (_primary is not null) _primary.IsEnabled = !Busy && error is null && _catalog.Environment.CanChoose && _catalog.Configurable(league) && _choicesDirty && _editor?.Dirty != true;
            if (_availabilityAction is not null) _availabilityAction.IsEnabled = !Busy && WritersStopped && _catalog.Environment.CanCreate && !league.Ended && _catalog.Rooted(league);
            if (_availabilityHint is not null) _availabilityHint.Text = !_catalog.Environment.CanCreate ? L.Text("League availability needs a newer source checkout.")
                : !WritersStopped ? L.Text("Close the game and stop services to change availability.") : L.Text("Availability changes apply at the next server start.");
        }
        else _validation.Text = _editor?.Dirty == true ? L.Text("Unsaved settings") : L.Text("No unsaved changes.");
        if (_settingsAction is not null) _settingsAction.IsEnabled = !Busy && _editor?.Dirty == true;
        if (_discardAction is not null) _discardAction.IsEnabled = !Busy && Dirty;
        UpdateFooterVisibility();
        UpdateLock();
    }
    async Task SetVisibilityAsync(GameLeague league)
    {
        if (_catalog is null || Busy) return;
        try
        {
            if (Dirty) throw new InvalidOperationException(L.Text("Save or discard the current draft before changing league visibility."));
            bool visible = !league.Creatable; var changed = _catalog.VisibilityPlan(league, visible);
            if (!Dialog.Ask(Owner, DialogKind.Question, L.Text(visible ? "Show for new characters" : "Hide from new characters"),
                L.Text("Change character-creation availability for: {0}? Linked mode options and parent cards are kept consistent. Existing characters are preserved. This applies after a server restart.", string.Join(", ", changed.Select(l => l.Name))), L.Text("Save"), L.Text("Cancel"))) return;
            var request = new JsonObject { ["league"] = league.Name, ["visible"] = visible, ["review"] = _catalog.Review.DeepClone() };
            var result = await SaveAsync("SetLeagueVisibility", request);
            if (result?.Ok == true) { await RefreshAsync(); SetStatus(L.Text("League visibility saved. It applies after the next server start.")); }
        }
        catch (Exception e) when (e is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException) { SetStatus(e.Message); }
    }
    void OpenSettings(StackPanel panel, string id, string declarations, string kind)
    {
        void Notice(string heading, string explanation)
        {
            panel.Children.Add(Type(heading, 14, true));
            var note = Type(explanation, 12, false, "Shell.TextSecondary"); note.Margin = new Thickness(0, 6, 0, 0); panel.Children.Add(note);
        }
        try
        {
            // File.Exists also returns false for access failures; do not mislabel those as an absent declaration.
            try { _ = File.GetAttributes(declarations); }
            catch (IOException e) when (e is FileNotFoundException or DirectoryNotFoundException)
            {
                Notice(L.Text("Settings not exposed to Waypoint"),
                    L.Text("No settings declaration is available for Waypoint here. Separate configuration files may still exist in this LocalExile version."));
                return;
            }
            if (_catalog?.Environment.CanEditSettings != true)
            {
                Notice(L.Text("Settings support unavailable"),
                    L.Text("A settings declaration is present, but this LocalExile version does not support editing shared settings through Waypoint."));
                return;
            }
            string path = Path.Combine(_shared.Workspace.Latest!.Root, ModSettingsForm.UserFile(id, kind));
            _settingsHash = GameSystems.Hash(path);
            var form = ModSettingsForm.Load(id, declarations, _shared.Workspace.Latest.Root, kind);
            if (_settingsHash != GameSystems.Hash(path)) throw new IOException(L.Text("Settings changed while reading. Refresh before editing."));
            if (form.Settings.Count == 0)
            {
                Notice(L.Text("No editable settings"), L.Text("The settings declaration was loaded successfully and contains no editable settings."));
                return;
            }
            _settingsId = id; _settingsKind = kind; _editor = new SettingsEditor(form); panel.Children.Add(_editor);
            _editor.Edited += UpdateFeedback;
            _settingsAction = FooterButton(L.Text("Save settings"), () => _ = SaveSettingsAsync(), primary: !LeagueView);
            panel.Children.Add(SettingsEditor.Button(L.Text("Reset to defaults"), () => _ = SaveSettingsAsync(defaults: true)));
        }
        catch (Exception e) when (e is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            Notice(L.Text("Settings could not be loaded"),
                L.Text("Waypoint found a settings declaration but could not load it. Refresh to try again. Nothing was changed."));
            panel.Children.Add(new Expander { Header = L.Text("Error details"), Content = Type(e.Message, 12, false, "Shell.TextSecondary"), Margin = new Thickness(0, 8, 0, 0) });
            SetStatus(L.Text("Settings could not be loaded"));
        }
    }
    async Task SaveSettingsAsync(bool defaults = false)
    {
        if (_editor is null || _settingsId.Length == 0 || Busy) return;
        if (defaults && !Dialog.Ask(Owner, DialogKind.Question, L.Text("Reset to defaults"), L.Text("Put every setting back to its default?"), L.Text("Reset"), L.Text("Cancel"))) return;
        try
        {
            var choices = _checks.ToDictionary(p => p.Key, p => p.Value.IsChecked); bool dirty = _choicesDirty;
            var result = await SaveAsync("SaveContentSettings", _editor.Values(defaults), _settingsId, _settingsKind == "modules" ? "Modules" : "Leagues", _settingsHash);
            if (result?.Ok == true)
            {
                RenderSelection(); foreach (var (key, value) in choices) if (_checks.TryGetValue(key, out var box)) box.IsChecked = value;
                _choicesDirty = dirty; UpdateFeedback(); SetStatus(L.Text("Settings saved. Each setting shows when it takes effect."));
            }
        }
        catch (Exception e) when (e is IOException or InvalidDataException or System.Text.Json.JsonException or InvalidOperationException or UnauthorizedAccessException) { SetStatus(e.Message); }
    }
    async Task SaveChoicesAsync(GameLeague league)
    {
        if (_catalog is null || Busy) return;
        try
        {
            var values = _catalog.Plan(league, SelectedModules());
            if (!Dialog.Ask(Owner, DialogKind.Question, L.Text("Save league choices"), L.Text("Save module choices for {0}? They apply after a server restart. Existing characters and other leagues are preserved.", league.Name), L.Text("Save"), L.Text("Cancel"))) return;
            var request = new JsonObject { ["league"] = league.Name, ["choices"] = values, ["review"] = _catalog.Review.DeepClone() };
            var result = await SaveAsync("SaveLeagueModules", request, expected: _catalog.ChoicesHash);
            if (result?.Ok == true) { _choicesDirty = false; await RefreshAsync(); SetStatus(L.Text("League choices saved. Restart servers when you are ready to apply them.")); }
        }
        catch (Exception e) when (e is IOException or InvalidDataException or System.Text.Json.JsonException or InvalidOperationException or UnauthorizedAccessException) { SetStatus(e.Message); }
    }
    async Task<BridgeResult?> SaveAsync(string action, JsonObject values, string id = "", string target = "", string expected = "")
    {
        if (Busy) return null;
        _applying = true; UpdateLock(); SetStatus(L.Text(LeagueView ? "Saving league configuration…" : "Saving module settings…"));
        string folder = Path.Combine(ModRepository.DownloadsFolder, Guid.NewGuid().ToString("N")); string file = Path.Combine(folder, "values.json");
        try
        {
            Directory.CreateDirectory(folder); await File.WriteAllTextAsync(file, values.ToJsonString());
            var result = await _shared.Workspace.OperateAsync(action, L.Text("Save game configuration"), new(id, Staged: file, Target: target, ExpectedHash: expected), quiet: true);
            SetStatus(result?.Ok == true ? "" : result?.Message ?? L.Text("A workspace operation is in progress.")); return result;
        }
        finally
        {
            try { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { _shared.Logs.Write($"Could not remove staged league configuration {folder}: {e.Message}", Severity.Error); }
            finally { _applying = false; UpdateLock(); UpdateFeedback(); }
        }
    }
}
