using System.Text.Json.Nodes;
#if WINDOWS
using System.Windows;
using System.Windows.Controls;
#else
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Threading;
#endif

namespace Waypoint;

/// <summary>Public module manifests and per-league user overrides, with one settings editor across both platforms.</summary>
public sealed class GameSystemsTab : UserControl
{
    public bool LeagueView { get; set; }
    internal event Action? OpenLeaguesRequested;
    Shared _shared = null!;
    GameSystems? _catalog;
    readonly StackPanel _detail = new();
    readonly ListBox _list = new() { Width = 250, Margin = new Thickness(0, 0, 24, 0) };
    readonly TextBlock _status = SettingsEditor.Note("");
    readonly StackPanel _header = new();
    SettingsEditor? _editor;
    object? _selected;
    object? _selectedRow;
    bool _selecting, _choicesDirty;
    bool _creating;
    readonly TextBlock _validation = SettingsEditor.Note("");
    Button? _saveChoices;
    TextBox? _draftName, _draftDescription;
    GameLeague? _draftTemplate;
    readonly Dictionary<string, CheckBox> _checks = new(StringComparer.OrdinalIgnoreCase);
    string _settingsHash = "missing", _settingsId = "", _settingsKind = "", _settingsPath = "";
    bool Dirty => _creating || _editor?.Dirty == true || _choicesDirty;
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
        var heading = new StackPanel { Margin = new Thickness(0, 0, 0, 20) };
        _header.Children.Add(new TextBlock { Text = L.Text(LeagueView ? "Leagues" : "Modules"), FontSize = 22, Margin = new Thickness(0, 0, 0, 6) });
        _header.Children.Add(SettingsEditor.Note(LeagueView ? L.Text("Choose which modules each league uses. Membership changes need a server restart.")
            : L.Text("Modules provide gameplay systems. Their settings are shared by every league using them.")));
        heading.Children.Add(_header);
        heading.Children.Add(SettingsEditor.Button(L.Text("Refresh"), () => _ = RefreshAsync(discard: true)));
        if (LeagueView) heading.Children.Add(SettingsEditor.Button(L.Text("New league"), () => StartCreate()));
        DockPanel.SetDock(heading, Dock.Top); shell.Children.Add(heading);
        DockPanel.SetDock(_status, Dock.Bottom); shell.Children.Add(_status);
        DockPanel.SetDock(_list, Dock.Left); shell.Children.Add(_list);
        shell.Children.Add(new ScrollViewer { Content = _detail, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        Content = shell;
        _list.SelectionChanged += (_, _) => SelectionChanged();
        Loaded += (_, _) => { if (_catalog is null) _ = RefreshAsync(); };
        shared.Workspace.Changed += () => Dispatch(() =>
        {
            if (_catalog is null && shared.Workspace.Latest?.Content is not null) _ = RefreshAsync();
        });
        shared.LiveRunning += running => Dispatch(() => IsEnabled = !running);
        L.Current.PropertyChanged += (_, _) => Dispatch(() =>
        {
            if (Dirty) return;
            _header.Children.Clear();
            _header.Children.Add(new TextBlock { Text = L.Text(LeagueView ? "Leagues" : "Modules"), FontSize = 22 });
            _header.Children.Add(SettingsEditor.Note(LeagueView ? L.Text("Choose which modules each league uses. Membership changes need a server restart.")
                : L.Text("Modules provide gameplay systems. Their settings are shared by every league using them.")));
            RenderSelection();
        });
    }
    static void Dispatch(Action action)
    {
#if WINDOWS
        System.Windows.Application.Current.Dispatcher.BeginInvoke(action);
#else
        Dispatcher.UIThread.Post(action);
#endif
    }
    bool Discard() => !Dirty || Dialog.Ask(Owner, DialogKind.Question, L.Text("Unsaved changes"), L.Text("Discard the changes you have not saved?"), L.Text("Discard"), L.Text("Cancel"));
    async Task RefreshAsync(bool discard = false)
    {
        if (discard && !Discard() || !discard && Dirty) return;
        try
        {
            await _shared.Workspace.RefreshAsync();
            if (_shared.Workspace.Latest?.Content is not { } environment) { _status.Text = L.Text("Refresh the workspace to inspect modules and leagues."); return; }
            string? previousSelection = _selected?.ToString();
            _catalog = new GameSystems(environment);
            _selecting = true;
            var items = LeagueView ? _catalog.Families.Cast<object>().ToArray() : _catalog.Systems.Cast<object>().ToArray();
            _list.ItemsSource = items;
            var previousLeague = _catalog.Leagues.FirstOrDefault(l => l.Name == previousSelection);
            _list.SelectedItem = LeagueView ? items.OfType<GameLeagueFamily>().FirstOrDefault(f => f.Variants.Any(l => l.Name == previousSelection)) ?? items.FirstOrDefault()
                : items.FirstOrDefault(i => i.ToString() == previousSelection) ?? items.FirstOrDefault();
            _selectedRow = _list.SelectedItem;
            _selected = _selectedRow is GameLeagueFamily family ? previousLeague is not null && family.Variants.Contains(previousLeague) ? previousLeague : family.Primary : _selectedRow;
            _selecting = false;
            RenderSelection();
            if (_selected is null) _status.Text = L.Text("No modules or leagues were found in this installation.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException or InvalidOperationException or ArgumentException)
        { _selecting = false; _status.Text = ex.Message; }
    }
    void SelectionChanged()
    {
        if (_selecting || _list.SelectedItem == _selectedRow) return;
        if (!Discard()) { _selecting = true; _list.SelectedItem = _selectedRow; _selecting = false; return; }
        _selectedRow = _list.SelectedItem;
        _selected = _selectedRow is GameLeagueFamily family ? family.Primary : _selectedRow; RenderSelection();
    }
    void RenderSelection()
    {
        _creating = false;
        _status.Text = "";
        _detail.Children.Clear(); _checks.Clear(); _editor = null; _choicesDirty = false; _settingsId = "";
        if (_catalog is null || _selected is null) return;
        if (_selected is GameSystem system)
        {
            _detail.Children.Add(new TextBlock { Text = system.Name, FontSize = 20 });
            if (system.Description.Length > 0) _detail.Children.Add(SettingsEditor.Note(system.Description));
            var used = _catalog.Leagues.Where(l => l.Systems.Contains(system.Path, StringComparer.OrdinalIgnoreCase)).Select(l => l.Name).ToArray();
            _detail.Children.Add(SettingsEditor.Note(L.Text("Used by: {0}", used.Length > 0 ? string.Join(", ", used) : L.Text("No leagues"))));
            _detail.Children.Add(SettingsEditor.Note(L.Text("Shared settings: changes affect every league using this module.")));
            if (system.Depends.Length > 0) _detail.Children.Add(SettingsEditor.Note(L.Text("Dependencies: {0}", string.Join(", ", system.Depends))));
            _detail.Children.Add(SettingsEditor.Button(L.Text("Choose leagues for this module"), () => OpenLeaguesRequested?.Invoke()));
            OpenSettings(system.Id, system.SettingsFile, "modules");
        }
        else if (_selected is GameLeague league)
        {
            var family = _selectedRow as GameLeagueFamily;
            _detail.Children.Add(new TextBlock { Text = family?.Name ?? league.Name, FontSize = 20 });
            _detail.Children.Add(SettingsEditor.Label(L.Text("Game mode")));
            _detail.Children.Add(SettingsEditor.Note(L.Text("Choose a mode to manage its modules and availability. Each mode keeps its own characters and stash.")));
            var modes = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 0, 0, 12) };
            foreach (var variant in family?.Variants ?? [league]) modes.Items.Add(ModeItem(variant, family?.Variants ?? [league]));
            modes.SelectedItem = modes.Items.OfType<ComboBoxItem>().First(i => ReferenceEquals(i.Tag, league));
            bool changingMode = false;
            modes.SelectionChanged += (_, _) =>
            {
                if (changingMode || (modes.SelectedItem as ComboBoxItem)?.Tag is not GameLeague next || ReferenceEquals(next, _selected)) return;
                if (!Discard()) { changingMode = true; modes.SelectedItem = modes.Items.OfType<ComboBoxItem>().First(i => ReferenceEquals(i.Tag, _selected)); changingMode = false; return; }
                _selected = next; RenderSelection();
            };
            _detail.Children.Add(modes);
            if (league.Description.Length > 0) _detail.Children.Add(SettingsEditor.Note(league.Description));
            _detail.Children.Add(new CheckBox { Content = L.Text("Available for new characters"), IsChecked = league.Creatable && !league.Ended, IsEnabled = false, Margin = new Thickness(0, 8, 0, 4) });
            _detail.Children.Add(SettingsEditor.Note(L.Text("Hiding a league only changes character creation. Existing characters and stashes are preserved.")));
            _detail.Children.Add(SettingsEditor.Note(L.Text("Changing availability requires stopped game and services, and applies at the next server start.")));
            var visibility = SettingsEditor.Button(L.Text(league.Creatable ? "Hide from new characters" : "Show for new characters"), () => _ = SetVisibilityAsync(league));
            visibility.IsEnabled = _catalog.Environment.CanCreate && !league.Ended && _catalog.Rooted(league) && !league.Name.Equals("Void", StringComparison.OrdinalIgnoreCase);
            _detail.Children.Add(visibility);
            var preview = new StackPanel { Margin = new Thickness(12, 8, 0, 12) };
            preview.Children.Add(SettingsEditor.Label(league.Name));
            preview.Children.Add(SettingsEditor.Note(league.Description));
            foreach (var (label, active) in new[] { (L.Text("Hardcore"), league.Hardcore), (L.Text("Solo self-found"), league.Ssf), (L.Text("Ruthless"), league.Form.Length > 0) })
                preview.Children.Add(new CheckBox { Content = label, IsChecked = active, IsEnabled = false });
            var variants = _catalog.Leagues.Where(l => l.EntryOf.Equals(league.Name, StringComparison.OrdinalIgnoreCase)).Select(l => l.Name).ToArray();
            if (variants.Length > 0) preview.Children.Add(SettingsEditor.Note(L.Text("Mode options: {0}", string.Join(", ", variants))));
            if (league.EntryOf.Length > 0) preview.Children.Add(SettingsEditor.Note(L.Text("Shown as a mode of: {0}", league.EntryOf)));
            if (league.Parent.Length > 0) preview.Children.Add(SettingsEditor.Note(L.Text("Parent league: {0}", league.Parent)));
            preview.Children.Add(SettingsEditor.Note(L.Text("Read-only preview of the league definition. Names identify saved characters; mode options link gameplay variants.")));
            _detail.Children.Add(new Expander { Header = L.Text("League card preview"), Content = preview, IsExpanded = false, Margin = new Thickness(0, 8, 0, 16) });
            _detail.Children.Add(SettingsEditor.Note(L.Text("Modes are fixed for existing characters. Create a separate league to choose another defined mode.")));
            _detail.Children.Add(SettingsEditor.Button(L.Text("Customize as a new league"), () => StartCreate(league)));
            if (!_catalog.Configurable(league)) _detail.Children.Add(SettingsEditor.Note(L.Text("Ended or non-playable league definitions are read-only.")));
            if (!_catalog.Environment.CanChoose) _detail.Children.Add(SettingsEditor.Note(L.Text("This server does not expose supported league system overrides. Update LocalExile to edit these choices.")));
            AddModuleChoices(_detail, league, _catalog.Environment.CanChoose && _catalog.Configurable(league));
            _detail.Children.Add(SettingsEditor.Note(L.Text("Saved choices apply after the next server restart. Existing characters remain in their league.")));
            _saveChoices = SettingsEditor.Button(L.Text("Save league choices"), () => _ = SaveChoicesAsync(league));
            _detail.Children.Add(_saveChoices);
            ValidateDraft(league);
            if (File.Exists(league.SettingsFile))
            {
                _detail.Children.Add(SettingsEditor.Label(L.Text("League settings")));
                OpenSettings(Path.GetFileName(league.Folder), league.SettingsFile, "leagues");
            }
        }
    }
    void AddModuleChoices(StackPanel panel, GameLeague league, bool editable)
    {
        if (_catalog is null) return;
        if (_validation.Parent is Panel previous) previous.Children.Remove(_validation);
        _checks.Clear();
        panel.Children.Add(SettingsEditor.Label(L.Text("Modules")));
        panel.Children.Add(SettingsEditor.Note(L.Text("Only combinations defined for this league and mode are allowed. Dedicated gameplay bundles stay together; unknown compatibility is blocked.")));
        var required = _catalog.RequiredModules(league);
        var unavailable = new StackPanel { Margin = new Thickness(12, 6, 0, 6) };
        foreach (var module in _catalog.Systems)
        {
            bool selected = (_creating ? league.BaseSystems : league.Systems).Contains(module.Path, StringComparer.OrdinalIgnoreCase);
            bool offered = _catalog.ModuleOffered(league, module.Path), locked = required.Contains(module.Path);
            var box = new CheckBox { Content = module.Name + (locked ? " · " + L.Text("Required") : !offered ? " · " + L.Text("Not defined for this mode") : ""),
                IsChecked = selected, IsEnabled = editable && !locked && (selected || offered), Margin = new Thickness(0, 6, 0, 6) };
            box.Click += (_, _) => { _choicesDirty = true; ValidateDraft(league); };
            _checks[module.Path] = box;
            (selected || offered || locked ? panel : unavailable).Children.Add(box);
        }
        if (unavailable.Children.Count > 0) panel.Children.Add(new Expander { Header = L.Text("Unavailable modules ({0})", unavailable.Children.Count), Content = unavailable, IsExpanded = false, Margin = new Thickness(0, 6, 0, 12) });
        foreach (string missing in league.Systems.Where(p => !_catalog.Systems.Any(s => s.Path.Equals(p, StringComparison.OrdinalIgnoreCase))))
            panel.Children.Add(SettingsEditor.Note(L.Text("Missing module: {0}. Restore it before changing this league.", missing)));
        panel.Children.Add(_validation);
    }
    string[] SelectedModules() => _checks.Where(p => p.Value.IsChecked == true).Select(p => p.Key).ToArray();
    ComboBoxItem ModeItem(GameLeague league, IReadOnlyList<GameLeague> variants) => new()
    {
        Content = _catalog!.Modes(league) + (variants.Count(l => _catalog.Modes(l) == _catalog.Modes(league)) > 1 ? " (" + league.Name + ")" : ""), Tag = league
    };
    void ValidateDraft(GameLeague league)
    {
        if (_catalog is null) return;
        string? error = _catalog.CompositionError(league, SelectedModules());
        if (_creating && error is null)
        {
            try { _catalog.PlanCreate(league, _draftName?.Text ?? "", _draftDescription?.Text ?? "", SelectedModules()); }
            catch (InvalidOperationException e) { error = e.Message; }
        }
        _validation.Text = error ?? L.Text("The selected modules satisfy the declared dependencies and existing profile checks.");
        if (_saveChoices is not null) _saveChoices.IsEnabled = error is null && (_creating ? _catalog.Environment.CanCreate
            : _catalog.Environment.CanChoose && _catalog.Configurable(league) && _choicesDirty);
        _status.Text = _creating ? L.Text("New league draft — nothing is saved yet.") : _choicesDirty ? L.Text("Unsaved module choices. Save them before leaving this league.") : "";
    }
    void StartCreate(GameLeague? origin = null)
    {
        if (_catalog is null || !Discard()) return;
        if (!_catalog.Environment.CanCreate)
        { _status.Text = L.Text("This checkout does not expose supported league creation. Existing leagues remain available for inspection."); return; }
        var candidates = (origin is null ? _catalog.Leagues.Where(l => !l.Custom && _catalog.Configurable(l)) : _catalog.Profiles(origin))
            .Where(l => GameSystems.EmptyTemplate(l.Folder)).ToArray();
        if (candidates.Length == 0)
        { _status.Text = L.Text("This league has custom code or no reusable source template. It cannot be safely copied."); return; }
        _detail.Children.Clear(); _checks.Clear(); _editor = null; _creating = true; _choicesDirty = false;
        _detail.Children.Add(new TextBlock { Text = L.Text("New league"), FontSize = 20 });
        _detail.Children.Add(SettingsEditor.Note(L.Text("Choose an existing league profile and its mode. New leagues have their own characters and stashes. Nothing is migrated or renamed.")));
        var families = candidates.GroupBy(l => _catalog.SourceFamilyName(l), StringComparer.OrdinalIgnoreCase).Select(g => new GameLeagueFamily(g.Key, g.ToArray())).ToArray();
        var chosenFamily = families.FirstOrDefault(f => f.Variants.Contains(origin!)) ?? families[0];
        _detail.Children.Add(SettingsEditor.Label(L.Text("Starting league")));
        var familySelector = new ComboBox { ItemsSource = families, SelectedItem = chosenFamily, HorizontalAlignment = HorizontalAlignment.Stretch };
        _detail.Children.Add(familySelector);
        _detail.Children.Add(SettingsEditor.Label(L.Text("Game mode")));
        var templates = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 0, 0, 12) };
        bool choosing = false;
        void FillModes(GameLeagueFamily family, GameLeague? preferred = null)
        {
            choosing = true; templates.Items.Clear();
            foreach (var profile in family.Variants) templates.Items.Add(ModeItem(profile, family.Variants));
            templates.SelectedItem = templates.Items.OfType<ComboBoxItem>().FirstOrDefault(i => ReferenceEquals(i.Tag, preferred))
                ?? templates.Items.OfType<ComboBoxItem>().First(i => ReferenceEquals(i.Tag, family.Primary));
            choosing = false;
        }
        FillModes(chosenFamily, origin);
        _detail.Children.Add(templates);
        _detail.Children.Add(SettingsEditor.Label(L.Text("New league name")));
        _draftName = new TextBox { Margin = new Thickness(0, 0, 0, 12) }; _detail.Children.Add(_draftName);
        _detail.Children.Add(SettingsEditor.Note(L.Text("Use 3–48 English letters, numbers, spaces, underscores or hyphens, starting with a letter.")));
        _detail.Children.Add(SettingsEditor.Label(L.Text("Description")));
        _draftDescription = new TextBox { Text = ((GameLeague)((ComboBoxItem)templates.SelectedItem!).Tag!).Description, AcceptsReturn = true, MinHeight = 64, Margin = new Thickness(0, 0, 0, 12) };
        _detail.Children.Add(_draftDescription);
        _detail.Children.Add(SettingsEditor.Note(L.Text("Custom leagues are isolated: their parent and Hardcore death destination are Void. Rewards cannot flow into Standard through a league transition. Creation requires the game and services to be stopped.")));
        var composition = new StackPanel(); _detail.Children.Add(composition);
        _saveChoices = SettingsEditor.Button(L.Text("Create league"), () => _ = CreateLeagueAsync()); _detail.Children.Add(_saveChoices);
        _detail.Children.Add(SettingsEditor.Button(L.Text("Cancel"), () => { if (Discard()) RenderSelection(); }));
        void Choose()
        {
            if ((templates.SelectedItem as ComboBoxItem)?.Tag is not GameLeague template) return;
            if (_validation.Parent is Panel previous) previous.Children.Remove(_validation);
            composition.Children.Clear(); _draftTemplate = template; _choicesDirty = false;
            composition.Children.Add(SettingsEditor.Note(L.Text("Selected modes: {0}", _catalog.Modes(template))));
            var profiles = _catalog.Profiles(template).ToArray();
            foreach (var (label, present) in new[] { (L.Text("Hardcore"), profiles.Any(l => l.Hardcore)),
                (L.Text("Solo self-found"), profiles.Any(l => l.Ssf)), (L.Text("Ruthless"), profiles.Any(l => l.Form.Length > 0)) })
                if (!present) composition.Children.Add(SettingsEditor.Note(L.Text("{0}: no variant is defined for this league family; this option is unavailable.", label)));
            AddModuleChoices(composition, template, editable: true); ValidateDraft(template);
        }
        templates.SelectionChanged += (_, _) =>
        {
            if (choosing) return;
            if (_choicesDirty && !Dialog.Ask(Owner, DialogKind.Question, L.Text("Unsaved changes"), L.Text("Discard the module choices in this draft and use the selected profile?"), L.Text("Discard"), L.Text("Cancel")))
            { choosing = true; templates.SelectedItem = templates.Items.OfType<ComboBoxItem>().First(i => ReferenceEquals(i.Tag, _draftTemplate)); choosing = false; return; }
            if (_draftTemplate is { } old && _draftDescription.Text == old.Description && (templates.SelectedItem as ComboBoxItem)?.Tag is GameLeague next) _draftDescription.Text = next.Description;
            Choose();
        };
        familySelector.SelectionChanged += (_, _) =>
        {
            if (choosing || familySelector.SelectedItem is not GameLeagueFamily next) return;
            if (_choicesDirty && !Dialog.Ask(Owner, DialogKind.Question, L.Text("Unsaved changes"), L.Text("Discard the module choices in this draft and use the selected profile?"), L.Text("Discard"), L.Text("Cancel")))
            { choosing = true; familySelector.SelectedItem = chosenFamily; choosing = false; return; }
            chosenFamily = next; FillModes(next);
            if (_draftTemplate is { } old && _draftDescription.Text == old.Description) _draftDescription.Text = next.Primary.Description;
            Choose();
        };
        _draftName.TextChanged += (_, _) => { if (_draftTemplate is { } template) ValidateDraft(template); };
        _draftDescription.TextChanged += (_, _) => { if (_draftTemplate is { } template) ValidateDraft(template); };
        Choose();
    }
    async Task CreateLeagueAsync()
    {
        if (_catalog is null || _draftTemplate is null) return;
        try
        {
            string name = _draftName?.Text ?? "";
            var request = _catalog.PlanCreate(_draftTemplate, name, _draftDescription?.Text ?? "", SelectedModules());
            if (!Dialog.Ask(Owner, DialogKind.Question, L.Text("Create league"),
                L.Text("Create {0} with modes {1}? It will be a separate league, with Void as its destination. Existing leagues and saves are preserved.", name, _catalog.Modes(_draftTemplate)), L.Text("Create"), L.Text("Cancel"))) return;
            var result = await SaveAsync("CreateLeague", request, "", "", "");
            if (result?.Ok != true) return;
            _creating = false; _choicesDirty = false;
            await RefreshAsync();
            _list.SelectedItem = _list.Items.OfType<GameLeagueFamily>().FirstOrDefault(f => f.Variants.Any(l => l.Name == name));
            _status.Text = L.Text("League created. It becomes available after the next server start.");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException)
        { _status.Text = ex.Message; }
    }
    async Task SetVisibilityAsync(GameLeague league)
    {
        if (_catalog is null) return;
        try
        {
            if (Dirty) throw new InvalidOperationException(L.Text("Save or discard the current draft before changing league visibility."));
            bool visible = !league.Creatable;
            var changed = _catalog.VisibilityPlan(league, visible);
            string title = L.Text(visible ? "Show for new characters" : "Hide from new characters");
            if (!Dialog.Ask(Owner, DialogKind.Question, title, L.Text("Change character-creation availability for: {0}? Linked mode options and parent cards are kept consistent. Existing characters are preserved. This applies after a server restart.", string.Join(", ", changed.Select(l => l.Name))), L.Text("Save"), L.Text("Cancel"))) return;
            var request = new JsonObject { ["league"] = league.Name, ["visible"] = visible, ["review"] = _catalog.Review.DeepClone() };
            var result = await SaveAsync("SetLeagueVisibility", request, "", "", "");
            if (result?.Ok == true) { await RefreshAsync(); _status.Text = L.Text("League visibility saved. It applies after the next server start."); }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException)
        { _status.Text = ex.Message; }
    }
    void OpenSettings(string id, string declarations, string kind)
    {
        if (_catalog?.Environment.CanEditSettings != true)
        { _detail.Children.Add(SettingsEditor.Note(L.Text("This installation does not expose the shared settings contract. Settings are read-only."))); return; }
        if (!File.Exists(declarations)) { _detail.Children.Add(SettingsEditor.Note(L.Text("This module declares no editable settings."))); return; }
        try
        {
            _settingsPath = Path.Combine(_shared.Workspace.Latest!.Root, ModSettingsForm.UserFile(id, kind));
            _settingsHash = GameSystems.Hash(_settingsPath);
            var form = ModSettingsForm.Load(id, declarations, _shared.Workspace.Latest.Root, kind);
            if (_settingsHash != GameSystems.Hash(_settingsPath)) throw new IOException(L.Text("Settings changed while reading. Refresh before editing."));
            _settingsId = id; _settingsKind = kind;
            _editor = new SettingsEditor(form); _detail.Children.Add(_editor);
            _editor.Edited += () => _status.Text = L.Text("Unsaved settings");
            _detail.Children.Add(SettingsEditor.Button(L.Text("Save settings"), () => _ = SaveSettingsAsync()));
            _detail.Children.Add(SettingsEditor.Button(L.Text("Reset to defaults"), () => _ = SaveSettingsAsync(defaults: true)));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException)
        { _status.Text = ex.Message; }
    }
    async Task SaveSettingsAsync(bool defaults = false)
    {
        if (_editor is null || _settingsId.Length == 0) return;
        if (defaults && !Dialog.Ask(Owner, DialogKind.Question, L.Text("Reset to defaults"), L.Text("Put every setting back to its default?"), L.Text("Reset"), L.Text("Cancel"))) return;
        try
        {
            var result = await SaveAsync("SaveContentSettings", _editor.Values(defaults), _settingsId, _settingsKind == "modules" ? "Modules" : "Leagues", _settingsHash);
            if (result?.Ok == true)
            {
                var choices = _checks.ToDictionary(p => p.Key, p => p.Value.IsChecked);
                bool dirty = _choicesDirty;
                RenderSelection();
                foreach (var (key, value) in choices) if (_checks.TryGetValue(key, out var box)) box.IsChecked = value;
                _choicesDirty = dirty;
                if (_selected is GameLeague league) ValidateDraft(league);
                _status.Text = L.Text("Settings saved. Each setting shows when it takes effect.");
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException or InvalidOperationException or UnauthorizedAccessException)
        { _status.Text = ex.Message; }
    }
    async Task SaveChoicesAsync(GameLeague league)
    {
        if (_catalog is null) return;
        try
        {
            if (_editor?.Dirty == true) throw new InvalidOperationException(L.Text("Save or discard the league settings before changing module choices."));
            var values = _catalog.Plan(league, _checks.Where(p => p.Value.IsChecked == true).Select(p => p.Key));
            if (!Dialog.Ask(Owner, DialogKind.Question, L.Text("Save league choices"),
                L.Text("Save module choices for {0}? They apply after a server restart. Existing characters and other leagues are preserved.", league.Name), L.Text("Save"), L.Text("Cancel"))) return;
            var request = new JsonObject { ["league"] = league.Name, ["choices"] = values, ["review"] = _catalog.Review.DeepClone() };
            var result = await SaveAsync("SaveLeagueModules", request, "", "", _catalog.ChoicesHash);
            if (result?.Ok == true)
            {
                _choicesDirty = false; await RefreshAsync(); _status.Text = L.Text("League choices saved. Restart servers when you are ready to apply them.");
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException or InvalidOperationException or UnauthorizedAccessException)
        { _status.Text = ex.Message; }
    }
    async Task<BridgeResult?> SaveAsync(string action, JsonObject values, string id, string target, string expected)
    {
        string folder = Path.Combine(ModRepository.DownloadsFolder, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, "values.json");
        try
        {
            await File.WriteAllTextAsync(file, values.ToJsonString());
            var result = await _shared.Workspace.OperateAsync(action, L.Text("Save game configuration"), new(id, Staged: file, Target: target, ExpectedHash: expected));
            _status.Text = result?.Message ?? L.Text("A workspace operation is in progress.");
            return result;
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
}
