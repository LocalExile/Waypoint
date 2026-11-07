using System.Text.Json.Nodes;
#if WINDOWS
using System.Windows;
using System.Windows.Controls;
#else
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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
    bool _selecting, _choicesDirty;
    readonly Dictionary<string, CheckBox> _checks = new(StringComparer.OrdinalIgnoreCase);
    string _settingsHash = "missing", _settingsId = "", _settingsKind = "", _settingsPath = "";
    bool Dirty => _editor?.Dirty == true || _choicesDirty;
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
            _list.ItemsSource = LeagueView ? _catalog.Leagues.Cast<object>().ToArray() : _catalog.Systems.Cast<object>().ToArray();
            _list.SelectedItem = _list.Items.Cast<object>().FirstOrDefault(i => i.ToString() == previousSelection) ?? _list.Items.Cast<object>().FirstOrDefault();
            _selected = _list.SelectedItem;
            _selecting = false;
            RenderSelection();
            if (_selected is null) _status.Text = L.Text("No modules or leagues were found in this installation.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException or InvalidOperationException or ArgumentException)
        { _selecting = false; _status.Text = ex.Message; }
    }
    void SelectionChanged()
    {
        if (_selecting || _list.SelectedItem == _selected) return;
        if (!Discard()) { _selecting = true; _list.SelectedItem = _selected; _selecting = false; return; }
        _selected = _list.SelectedItem; RenderSelection();
    }
    void RenderSelection()
    {
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
            _detail.Children.Add(new TextBlock { Text = league.Name, FontSize = 20 });
            if (league.Description.Length > 0) _detail.Children.Add(SettingsEditor.Note(league.Description));
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
            if (league.Ended || !league.Creatable) _detail.Children.Add(SettingsEditor.Note(L.Text("This league is ended or does not allow new characters. Module choices are read-only.")));
            if (!_catalog.Environment.CanChoose) _detail.Children.Add(SettingsEditor.Note(L.Text("This server does not expose supported league system overrides. Update LocalExile to edit these choices.")));
            foreach (var module in _catalog.Systems)
            {
                bool required = module.Id.Equals("Game", StringComparison.OrdinalIgnoreCase);
                var box = new CheckBox { Content = module.Name + (required ? " · " + L.Text("Required") : ""),
                    IsChecked = league.Systems.Contains(module.Path, StringComparer.OrdinalIgnoreCase),
                    IsEnabled = !required && _catalog.Environment.CanChoose && league.Creatable && !league.Ended, Margin = new Thickness(0, 8, 0, 8) };
                box.Click += (_, _) => { _choicesDirty = true; _status.Text = L.Text("Unsaved module choices. Save them before leaving this league."); };
                _checks[module.Path] = box; _detail.Children.Add(box);
            }
            foreach (string missing in league.Systems.Where(p => !_catalog.Systems.Any(s => s.Path.Equals(p, StringComparison.OrdinalIgnoreCase))))
                _detail.Children.Add(SettingsEditor.Note(L.Text("Missing module: {0}. Restore it before changing this league.", missing)));
            _detail.Children.Add(SettingsEditor.Note(L.Text("Saved choices apply after the next server restart. Existing characters remain in their league.")));
            var save = SettingsEditor.Button(L.Text("Save league choices"), () => _ = SaveChoicesAsync(league));
            save.IsEnabled = _catalog.Environment.CanChoose && league.Creatable && !league.Ended;
            _detail.Children.Add(save);
            if (File.Exists(league.SettingsFile))
            {
                _detail.Children.Add(SettingsEditor.Label(L.Text("League settings")));
                OpenSettings(Path.GetFileName(league.Folder), league.SettingsFile, "leagues");
            }
        }
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
            var result = await SaveAsync("SaveLeagueModules", values, "", "", _catalog.ChoicesHash);
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
