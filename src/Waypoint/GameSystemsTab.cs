using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
#if WINDOWS
using System.Windows;
using System.Windows.Controls;
using AccessibleElement = System.Windows.FrameworkElement;
using A11y = System.Windows.Automation.AutomationProperties;
using LiveSetting = System.Windows.Automation.AutomationLiveSetting;
#else
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Threading;
using AccessibleElement = Avalonia.Controls.Control;
using A11y = Avalonia.Automation.AutomationProperties;
using LiveSetting = Avalonia.Automation.AutomationLiveSetting;
#endif

namespace Waypoint;

/// <summary>League families, their modes and guarded public-file customization. The same view serves both front ends.</summary>
public sealed partial class GameSystemsTab : UserControl
{
    public bool LeagueView { get; set; }
    internal event Action? OpenLeaguesRequested;
    Shared _shared = null!;
    GameSystems? _catalog;
    readonly StackPanel _detail = new(), _header = new();
    readonly StackPanel _toolbar = new() { Orientation = Orientation.Horizontal };
    readonly StackPanel _actions = new() { Orientation = Orientation.Horizontal };
    readonly ListBox _list = new() { Width = 220, Margin = new Thickness(0, 0, 22, 0) };
    readonly TextBlock _status = SettingsEditor.Note(""), _validation = SettingsEditor.Note("");
    readonly Dictionary<string, CheckBox> _checks = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _draftModules = new(StringComparer.OrdinalIgnoreCase);
    SettingsEditor? _editor;
    object? _selected, _selectedRow;
    bool _selecting, _choicesDirty, _creating, _refreshing, _applying, _liveRunning;
    Button? _primary, _discardAction, _settingsAction, _availabilityAction, _newAction;
    TextBlock? _availabilityHint;
    ComboBox? _modeSelector;
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
        heading.Children.Add(_header); UpdateHeading();
        _toolbar.Children.Add(SettingsEditor.Button(L.Text("Refresh"), () => _ = RefreshAsync(discard: true)));
        if (LeagueView) { _newAction = SettingsEditor.Button(L.Text("New league"), () => StartCreate()); _toolbar.Children.Add(_newAction); }
        heading.Children.Add(_toolbar);
        DockPanel.SetDock(heading, Dock.Top); shell.Children.Add(heading);
        var footer = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        footer.Children.Add(_validation); footer.Children.Add(_status); footer.Children.Add(_actions);
        A11y.SetLiveSetting(_status, LiveSetting.Polite); A11y.SetLiveSetting(_validation, LiveSetting.Polite);
        DockPanel.SetDock(footer, Dock.Bottom); shell.Children.Add(footer);
        Named(_list, L.Text(LeagueView ? "Leagues" : "Modules"));
        DockPanel.SetDock(_list, Dock.Left); shell.Children.Add(_list);
        shell.Children.Add(new ScrollViewer { Content = _detail, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        Content = shell;
        _list.SelectionChanged += (_, _) => SelectionChanged();
        Loaded += (_, _) => { if (_catalog is null) _ = RefreshAsync(); };
        shared.Workspace.Changed += () => Dispatch(() =>
        {
            if (_catalog is null && shared.Workspace.Latest?.Content is not null) _ = RefreshAsync();
            else UpdateFeedback();
        });
        shared.LiveRunning += running => Dispatch(() => { _liveRunning = running; UpdateFeedback(); });
        L.Current.PropertyChanged += (_, _) => Dispatch(() => { if (!Dirty && !Busy) { UpdateHeading(); RenderSelection(); } });
    }
    void UpdateHeading()
    {
        _header.Children.Clear();
        _header.Children.Add(new TextBlock { Text = L.Text(LeagueView ? "Leagues" : "Modules"), FontSize = 22 });
        _header.Children.Add(SettingsEditor.Note(LeagueView ? L.Text("Choose a league, then manage its game mode and modules.")
            : L.Text("Modules provide gameplay systems. Their settings are shared by every league using them.")));
    }
    static T Named<T>(T control, string name) where T : AccessibleElement { A11y.SetName(control, name); return control; }
    static TextBlock LabelFor(AccessibleElement control, string text)
    { var label = SettingsEditor.Label(text); A11y.SetName(control, text); A11y.SetLabeledBy(control, label); return label; }
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
#if WINDOWS
        System.Windows.Automation.Peers.UIElementAutomationPeer.FromElement(_status)?.RaiseAutomationEvent(System.Windows.Automation.Peers.AutomationEvents.LiveRegionChanged);
#endif
    }
    void UpdateLock()
    {
        _detail.IsEnabled = _list.IsEnabled = _toolbar.IsEnabled = _actions.IsEnabled = !Busy;
        if (_newAction is not null) _newAction.IsEnabled = !_creating && _catalog?.Environment.CanCreate == true;
    }
    Button FooterButton(string text, Action action, bool primary = false)
    {
        var button = Named(SettingsEditor.Button(text, action), text);
        if (primary)
        {
#if WINDOWS
            if (TryFindResource("Shell.PrimaryButton") is Style style) button.Style = style;
#else
            button.Classes.Add("accent");
#endif
        }
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
            _selecting = true;
            var items = LeagueView ? _catalog.Families.Where(f => f.Variants.Any(_catalog.Configurable)).Cast<object>().ToArray() : _catalog.Systems.Cast<object>().ToArray();
            _list.ItemsSource = items;
            var previousLeague = _catalog.Leagues.FirstOrDefault(l => l.Name == previous);
            _list.SelectedItem = LeagueView ? items.OfType<GameLeagueFamily>().FirstOrDefault(f => f.Variants.Any(l => l.Name == previous)) ?? items.FirstOrDefault()
                : items.FirstOrDefault(i => i.ToString() == previous) ?? items.FirstOrDefault();
            _selectedRow = _list.SelectedItem;
            _selected = _selectedRow is GameLeagueFamily family ? previousLeague is not null && family.Variants.Contains(previousLeague) ? previousLeague : family.Primary : _selectedRow;
            _selecting = false; RenderSelection();
            if (_selected is null) SetStatus(L.Text("No modules or leagues were found in this installation."));
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
        _selected = _selectedRow is GameLeagueFamily family ? family.Primary : _selectedRow;
        RenderSelection();
    }
    void ClearView()
    {
        _detail.Children.Clear(); _actions.Children.Clear(); _checks.Clear(); _editor = null;
        _primary = _discardAction = _settingsAction = _availabilityAction = null; _availabilityHint = null; _modeSelector = null;
        _settingsId = ""; _validation.Text = ""; SetStatus("");
    }
    void RenderSelection(bool focusMode = false)
    {
        _creating = false; _choicesDirty = false; ClearView();
        if (_catalog is null || _selected is null) return;
        if (_selected is GameSystem system)
        {
            _detail.Children.Add(new TextBlock { Text = ModuleTitle(system), FontSize = 20 });
            if (system.Description.Length > 0) _detail.Children.Add(SettingsEditor.Note(system.Description));
            var used = _catalog.Leagues.Where(l => l.Systems.Contains(system.Path, StringComparer.OrdinalIgnoreCase)).Select(l => l.Name).ToArray();
            _detail.Children.Add(SettingsEditor.Note(L.Text("Used by: {0}", used.Length > 0 ? string.Join(", ", used) : L.Text("No leagues"))));
            _detail.Children.Add(SettingsEditor.Note(L.Text("Shared settings: changes affect every league using this module.")));
            _detail.Children.Add(SettingsEditor.Button(L.Text("Choose leagues for this module"), () => OpenLeaguesRequested?.Invoke()));
            OpenSettings(_detail, system.Id, system.SettingsFile, "modules");
        }
        else if (_selected is GameLeague league) RenderLeague(league);
        _discardAction = FooterButton(L.Text("Discard changes"), DiscardCurrent);
        UpdateFeedback(); UpdateLock();
        if (focusMode && _modeSelector is not null) Dispatch(() => _modeSelector.Focus());
    }
    void RenderLeague(GameLeague league)
    {
        var family = _selectedRow as GameLeagueFamily;
        _detail.Children.Add(new TextBlock { Text = family?.Name ?? league.Name, FontSize = 20, Margin = new Thickness(0, 0, 0, 6) });
        var variants = family?.Variants ?? [league];
        _modeSelector = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 0, 0, 10) };
        _detail.Children.Add(LabelFor(_modeSelector, L.Text("Game mode")));
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
        _detail.Children.Add(_modeSelector);
        var availability = new StackPanel { Orientation = Orientation.Horizontal };
        availability.Children.Add(new TextBlock { Text = L.Text(league.Creatable && !league.Ended ? "Visible to new characters" : "Hidden from new characters"), Margin = new Thickness(0, 10, 18, 0) });
        _availabilityAction = SettingsEditor.Button(L.Text(league.Creatable ? "Hide…" : "Show…"), () => _ = SetVisibilityAsync(league));
        Named(_availabilityAction, L.Text(league.Creatable ? "Hide from new characters" : "Show for new characters"));
        availability.Children.Add(_availabilityAction); _detail.Children.Add(availability);
        _availabilityHint = SettingsEditor.Note(""); _detail.Children.Add(_availabilityHint);
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
        details.Children.Add(SettingsEditor.Label(L.Text("League card preview")));
        details.Children.Add(SettingsEditor.Note(league.Name + "\n" + league.Description));
        if (league.EntryOf.Length > 0) details.Children.Add(SettingsEditor.Note(L.Text("Shown as a mode of: {0}", league.EntryOf)));
        if (league.Parent.Length > 0) details.Children.Add(SettingsEditor.Note(L.Text("Parent league: {0}", league.Parent)));
        _detail.Children.Add(new Expander { Header = L.Text("League details and compatibility"), Content = details, IsExpanded = false, Margin = new Thickness(0, 12, 0, 8) });
        if (File.Exists(league.SettingsFile))
        {
            var settings = new StackPanel { Margin = new Thickness(12, 8, 0, 8) };
            OpenSettings(settings, Path.GetFileName(league.Folder), league.SettingsFile, "leagues");
            _detail.Children.Add(new Expander { Header = L.Text("League settings"), Content = settings, IsExpanded = false });
        }
    }
    static string ModuleTitle(GameSystem module) => module.Name == module.Id
        ? module.Id == "Game" ? L.Text("Core game rules") : Regex.Replace(module.Name, @"(?<=[a-z0-9])(?=[A-Z])", " ").Replace("Npc ", "NPC ") : module.Name;
    TextBox AddModuleChoices(StackPanel panel, GameLeague league, bool editable)
    {
        _checks.Clear();
        panel.Children.Add(SettingsEditor.Label(L.Text("Gameplay modules")));
        var search = new TextBox { Margin = new Thickness(0, 0, 0, 10) };
        panel.Children.Add(LabelFor(search, L.Text("Find a module"))); panel.Children.Add(search);
        var count = SettingsEditor.Note(""); panel.Children.Add(count);
        var rows = new List<(AccessibleElement Row, string Text)>();
        var unavailable = new StackPanel { Margin = new Thickness(12, 6, 0, 6) };
        var required = _catalog!.RequiredModules(league);
        void Count() => count.Text = L.Text("{0} modules enabled · {1} required", _checks.Count(p => p.Value.IsChecked == true), required.Count);
        foreach (var module in _catalog.Systems)
        {
            bool selected = (_creating ? (IEnumerable<string>)_draftModules : league.Systems).Contains(module.Path, StringComparer.OrdinalIgnoreCase);
            bool offered = _catalog.ModuleOffered(league, module.Path), locked = required.Contains(module.Path);
            string name = ModuleTitle(module);
            var holder = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };
            var box = Named(new CheckBox { Content = name, IsChecked = selected, IsEnabled = editable && !locked && (selected || offered) }, name);
            _checks[module.Path] = box;
            if (locked || !editable || !offered && !selected)
                holder.Children.Add(Named(SettingsEditor.Label((selected ? "✓ " : "– ") + name + (locked ? " · " + L.Text("Required") : !offered ? " · " + L.Text("Not defined for this mode") : "")), name + (locked ? " · " + L.Text("Required") : "")));
            else holder.Children.Add(box);
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
            (selected || offered || locked ? panel : unavailable).Children.Add(holder);
            rows.Add((holder, name + " " + module.Id + " " + module.Description));
        }
        foreach (string path in (_creating ? _draftModules : league.Systems.AsEnumerable()).Where(p => !_checks.ContainsKey(p)))
        {
            // Retain unresolved choices in the draft. A missing installation must never silently remove a saved module.
            _checks[path] = new CheckBox { IsChecked = true, IsEnabled = false };
            var missing = SettingsEditor.Note(L.Text("Missing module: {0}. Restore it before changing this league.", path));
            panel.Children.Add(missing); rows.Add((missing, path));
        }
        Expander? unavailableGroup = null;
        if (unavailable.Children.Count > 0) { unavailableGroup = new Expander { Header = L.Text("Unavailable modules ({0})", unavailable.Children.Count), Content = unavailable, IsExpanded = false }; panel.Children.Add(unavailableGroup); }
        var empty = SettingsEditor.Note(L.Text("No modules match your search.")); SettingsEditor.Show(empty, false); panel.Children.Add(empty);
        search.TextChanged += (_, _) =>
        {
            var words = (search.Text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries); int shown = 0;
            foreach (var row in rows) { bool match = words.All(w => row.Text.Contains(w, StringComparison.CurrentCultureIgnoreCase)); SettingsEditor.Show(row.Row, match); if (match) shown++; }
            if (unavailableGroup is not null) unavailableGroup.IsExpanded = words.Length > 0;
            SettingsEditor.Show(empty, shown == 0);
        };
        Count(); return search;
    }
    string[] SelectedModules() => _creating ? _draftModules.ToArray() : _checks.Where(p => p.Value.IsChecked == true).Select(p => p.Key).ToArray();
    ComboBoxItem ModeItem(GameLeague league, IReadOnlyList<GameLeague> variants) => new()
    { Content = _catalog!.Modes(league) + (variants.Count(l => _catalog.Modes(l) == _catalog.Modes(league)) > 1 ? " (" + league.Name + ")" : ""), Tag = league };
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
        if (_catalog?.Environment.CanEditSettings != true) { panel.Children.Add(SettingsEditor.Note(L.Text("This installation does not expose the shared settings contract. Settings are read-only."))); return; }
        if (!File.Exists(declarations)) { panel.Children.Add(SettingsEditor.Note(L.Text("This module declares no editable settings."))); return; }
        try
        {
            string path = Path.Combine(_shared.Workspace.Latest!.Root, ModSettingsForm.UserFile(id, kind));
            _settingsHash = GameSystems.Hash(path);
            var form = ModSettingsForm.Load(id, declarations, _shared.Workspace.Latest.Root, kind);
            if (_settingsHash != GameSystems.Hash(path)) throw new IOException(L.Text("Settings changed while reading. Refresh before editing."));
            _settingsId = id; _settingsKind = kind; _editor = new SettingsEditor(form); panel.Children.Add(_editor);
            _editor.Edited += UpdateFeedback;
            _settingsAction = FooterButton(L.Text("Save settings"), () => _ = SaveSettingsAsync(), primary: !LeagueView);
            panel.Children.Add(SettingsEditor.Button(L.Text("Reset to defaults"), () => _ = SaveSettingsAsync(defaults: true)));
        }
        catch (Exception e) when (e is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException) { SetStatus(e.Message); }
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
        _applying = true; UpdateLock(); SetStatus(L.Text("Saving league configuration…"));
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
