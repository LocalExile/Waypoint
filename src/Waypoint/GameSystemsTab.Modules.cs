#if WINDOWS
using System.Windows;
using System.Windows.Controls;
using Element = System.Windows.FrameworkElement;
using A11y = System.Windows.Automation.AutomationProperties;
#else
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Element = Avalonia.Controls.Control;
using A11y = Avalonia.Automation.AutomationProperties;
#endif

namespace Waypoint;

public sealed partial class GameSystemsTab
{
    readonly TextBox _moduleSearch = new() { MinHeight = 28 };
    readonly TextBlock _moduleCount = new();
    readonly TextBlock _moduleEmpty = new();
    bool _filteringModules;
    void RefreshModuleIcons()
    {
        if (_catalog is null) return;
        foreach (var module in _catalog.Systems) PublishIcon(module.Id, ModuleIcons.Resolve(_shared.Settings, module.Id));
    }
    static void PublishIcon(string id, ModuleIcon icon)
    {
#if WINDOWS
        Application.Current.Resources[ModuleIcons.Resource(id)] = icon;
#else
        App.Instance.Resources[ModuleIcons.Resource(id)] = icon;
#endif
    }
    void FilterModules()
    {
        if (_catalog is null || _filteringModules || Busy) return;
        var matches = MatchingModules();
        var next = matches.FirstOrDefault(m => ReferenceEquals(m, _selected)) ?? matches.FirstOrDefault();
        bool changed = !ReferenceEquals(next, _selected);
        if (changed && !Discard())
        {
            _filteringModules = true; _moduleSearch.Text = _lastModuleSearch; _filteringModules = false; return;
        }
        _lastModuleSearch = _moduleSearch.Text ?? "";
        _selecting = true; _list.ItemsSource = matches;
        _list.SelectedItem = next;
        _selectedRow = _selected = _list.SelectedItem; _selecting = false;
        UpdateModuleCount(matches.Length);
        if (changed) RenderSelection();
    }
    GameSystem[] MatchingModules()
    {
        var words = (_moduleSearch.Text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return _catalog!.Systems.Where(m => words.All(w => (ModuleTitle(m) + " " + m.Id + " " + m.Path + " " + m.Description)
            .Contains(w, StringComparison.CurrentCultureIgnoreCase))).ToArray();
    }
    void UpdateModuleCount(int shown)
    {
        _moduleCount.Text = L.Text("{0} of {1} modules", shown, _catalog!.Systems.Count);
        SettingsEditor.Show(_moduleEmpty, shown == 0);
    }
    string _lastModuleSearch = "";
    void RenderModule(GameSystem module)
    {
        if (_catalog is null) return;
        var hero = new DockPanel();
        var emblem = Card(new ModuleGlyph(module.Id, 34), 14, "Shell.Background"); emblem.Width = 64; emblem.Height = 66; emblem.Margin = new Thickness(0, 0, 18, 0);
        emblem.VerticalAlignment = VerticalAlignment.Top; DockPanel.SetDock(emblem, Dock.Left); hero.Children.Add(emblem);
        var body = new StackPanel();
        var title = new DockPanel();
        var iconAction = SettingsEditor.Button(L.Text("Choose icon…"), () => PickModuleIcon(module));
        Named(iconAction, L.Text("Choose icon for {0}", ModuleTitle(module)));
        iconAction.Margin = new Thickness(12, 0, 0, 0); DockPanel.SetDock(iconAction, Dock.Right); title.Children.Add(iconAction);
        title.Children.Add(Type(ModuleTitle(module), 25, true)); body.Children.Add(title);
        var identity = Type(L.Text("Gameplay module · {0}", module.Id), 12, false, "Shell.TextSecondary"); identity.Margin = new Thickness(0, 6, 0, 0); body.Children.Add(identity);
        if (module.Description.Length > 0)
        {
            var description = Type(module.Description, 13, false, "Shell.TextSecondary");
            description.Margin = new Thickness(0, 10, 0, 0); body.Children.Add(description);
        }
        hero.Children.Add(body); var heroCard = Card(hero, 18); heroCard.Margin = new Thickness(0, 0, 0, 12); _detail.Children.Add(heroCard);

        var usage = new StackPanel();
        var usageHeader = new DockPanel();
        var choose = SettingsEditor.Button(L.Text("Manage in Leagues"), () => OpenLeaguesRequested?.Invoke());
        Named(choose, L.Text("Manage in Leagues"));
        choose.Margin = new Thickness(12, 0, 0, 0); DockPanel.SetDock(choose, Dock.Right); usageHeader.Children.Add(choose);
        usageHeader.Children.Add(Type(L.Text("League usage"), 18, true)); usage.Children.Add(usageHeader);
        var used = _catalog.Leagues.Where(l => l.Systems.Contains(module.Path, StringComparer.OrdinalIgnoreCase)).ToArray();
        var usageText = Type(used.Length == 0 ? L.Text("No leagues currently use this module.") : L.Text("Used by {0} league definitions, including linked game modes.", used.Length), 12, false, "Shell.TextSecondary");
        usageText.Margin = new Thickness(0, 8, 0, 0); usage.Children.Add(usageText);
        if (used.Length > 0)
        {
            var names = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
            foreach (var league in used.Take(5)) { var badge = Badge(league.Name); badge.Margin = new Thickness(0, 0, 6, 6); names.Children.Add(badge); }
            usage.Children.Add(names);
            if (used.Length > 5)
            {
                var all = new StackPanel { Margin = new Thickness(12, 6, 0, 0) };
                foreach (var league in used.Skip(5)) { var name = Type(league.Name, 12, false, "Shell.TextSecondary"); name.Margin = new Thickness(0, 3, 0, 3); all.Children.Add(name); }
                usage.Children.Add(new Expander { Header = L.Text("More league definitions ({0})", used.Length - 5), Content = all });
            }
        }
        var usageCard = Card(usage); usageCard.Margin = new Thickness(0, 0, 0, 12); _detail.Children.Add(usageCard);

        var settings = new StackPanel(); settings.Children.Add(Type(L.Text("Shared settings"), 18, true));
        var shared = Type(L.Text("Changes affect every league using this module."), 12, false, "Shell.TextSecondary"); shared.Margin = new Thickness(0, 6, 0, 16); settings.Children.Add(shared);
        OpenSettings(settings, module.Id, module.SettingsFile, "modules");
        var settingsCard = Card(settings); settingsCard.Margin = new Thickness(0, 0, 0, 12); _detail.Children.Add(settingsCard);
        if (File.Exists(module.SettingsFile)) { _detail.Children.Remove(settingsCard); _detail.Children.Insert(1, settingsCard); }

        var dependencies = new StackPanel { Margin = new Thickness(12, 8, 0, 8) };
        if (module.Depends.Length == 0) dependencies.Children.Add(Type(L.Text("No module dependencies declared."), 12, false, "Shell.TextSecondary"));
        foreach (var id in module.Depends)
        {
            var dependency = _catalog.Systems.FirstOrDefault(m => m.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            var label = dependency is null ? Type(L.Text("Missing dependency: {0}", id), 12, false, "Shell.Warning") : ModuleLabel(dependency);
            label.Margin = new Thickness(0, 4, 0, 4); dependencies.Children.Add(label);
        }
        var location = Type(module.Path, 12, false, "Shell.TextSecondary"); location.Margin = new Thickness(0, 10, 0, 0); dependencies.Children.Add(location);
        _detail.Children.Add(new Expander { Header = L.Text("Module details and dependencies ({0})", module.Depends.Length), Content = dependencies });
    }
    void PickModuleIcon(GameSystem module)
    {
        var picker = new ModuleIconDialog(Owner, module, ModuleIcons.Resolve(_shared.Settings, module.Id));
        if (picker.ShowDialog() != true) return;
        var preferences = _shared.Settings.ModuleIcons;
        if (!preferences.TryGetValue(ModuleIcons.Scope, out var icons) || icons is null) preferences[ModuleIcons.Scope] = icons = [];
        string id = module.Id.ToUpperInvariant();
        if (picker.Selected.Key == ModuleIcons.Default(module.Id).Key) icons.Remove(id); else icons[id] = picker.Selected.Key;
        _shared.Settings.Save(); PublishIcon(module.Id, picker.Selected);
        SetStatus(L.Text("Module icon saved in Waypoint. It is shared by Modules and Leagues; LocalExile was not changed."));
    }
    sealed class ModuleIconDialog : FormWindow
    {
        public ModuleIcon Selected { get; private set; }
        public ModuleIconDialog(Window? owner, GameSystem module, ModuleIcon current)
        {
            Selected = current; DialogLayout.Configure(this, owner, L.Text("Choose icon for {0}", ModuleTitle(module)), 780);
            Height = 620; SizeToContent = SizeToContent.Manual;
            var layout = new DockPanel { Margin = new Thickness(20) };
            var header = new StackPanel();
            header.Children.Add(Type(L.Text("One icon, wherever this module appears in Waypoint."), 14, true));
            var note = Type(L.Text("This is a personal display choice. Gameplay and game artwork are unchanged."), 12, false, "Shell.TextSecondary"); note.Margin = new Thickness(0, 6, 0, 14); header.Children.Add(note);
            var filters = new Grid(); filters.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            filters.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
            var searchArea = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
            var search = new TextBox { MinHeight = 28 }; searchArea.Children.Add(LabelFor(search, L.Text("Find an icon"))); searchArea.Children.Add(search);
            var categoryArea = new StackPanel(); Grid.SetColumn(categoryArea, 1);
            var categories = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 28 };
            categoryArea.Children.Add(LabelFor(categories, L.Text("Category"))); categoryArea.Children.Add(categories); filters.Children.Add(searchArea); filters.Children.Add(categoryArea); header.Children.Add(filters);
            var count = Type("", 12, false, "Shell.TextSecondary"); count.Margin = new Thickness(0, 10, 0, 10); header.Children.Add(count);
            DockPanel.SetDock(header, Dock.Top); layout.Children.Add(header);
            var footer = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
            var selected = Type("", 13, true); footer.Children.Add(selected);
            var choices = new List<(ModuleIcon Icon, Button Button)>();
            var buttons = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
            buttons.Children.Add(SettingsEditor.Button(L.Text("Use module default"), () => Select(ModuleIcons.Default(module.Id))));
            var apply = SettingsEditor.Button(L.Text("Apply"), () =>
            {
#if WINDOWS
                DialogResult = true;
#else
                Accept();
#endif
            });
#if WINDOWS
            apply.SetResourceReference(StyleProperty, "Shell.PrimaryButton");
#else
            apply.Classes.Add("accent");
#endif
            buttons.Children.Add(apply);
#if WINDOWS
            buttons.Children.Add(new Button { Content = L.Text("Cancel"), IsCancel = true });
#else
            buttons.Children.Add(Dismiss(L.Text("Cancel")));
#endif
            footer.Children.Add(buttons); DockPanel.SetDock(footer, Dock.Bottom); layout.Children.Add(footer);
            var tiles = new WrapPanel();
            var scroll = new ScrollViewer { Content = tiles, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            layout.Children.Add(scroll); Content = layout;
            foreach (var icon in ModuleIcons.All)
            {
                var label = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                var graphic = ModuleGlyph.Draw(icon, 26); graphic.HorizontalAlignment = HorizontalAlignment.Center; label.Children.Add(graphic);
                var name = Type(icon.Title(), 11); name.HorizontalAlignment = HorizontalAlignment.Center; name.Margin = new Thickness(0, 7, 0, 0); label.Children.Add(name);
                var button = new Button { Content = label, Width = 104, MinHeight = 82, Padding = new Thickness(6), Margin = new Thickness(0, 0, 6, 6), BorderThickness = new Thickness(1) };
                A11y.SetName(button, icon.Title()); A11y.SetHelpText(button, icon.Category());
                button.Click += (_, _) => Select(icon); choices.Add((icon, button)); tiles.Children.Add(button);
            }
            categories.Items.Add(L.Text("All icons"));
            foreach (var category in ModuleIcons.All.Select(i => i.Category()).Distinct()) categories.Items.Add(category);
            categories.SelectedIndex = 0;
            void Select(ModuleIcon icon)
            {
                Selected = icon; selected.Text = L.Text("Selected icon: {0}", icon.Title());
                foreach (var entry in choices)
                {
                    string brush = entry.Icon.Key == icon.Key ? "Shell.Accent" : "Shell.Border";
#if WINDOWS
                    entry.Button.SetResourceReference(BorderBrushProperty, brush);
#else
                    entry.Button.Bind(BorderBrushProperty, new DynamicResourceExtension(brush));
#endif
                    entry.Button.BorderThickness = new Thickness(entry.Icon.Key == icon.Key ? 2 : 1);
                    A11y.SetHelpText(entry.Button, entry.Icon.Category() + (entry.Icon.Key == icon.Key ? " · " + L.Text("Selected") : ""));
                }
            }
            void Filter()
            {
                var words = (search.Text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries); int shown = 0;
                foreach (var entry in choices)
                {
                    bool match = (categories.SelectedIndex == 0 || Equals(categories.SelectedItem, entry.Icon.Category()))
                        && words.All(w => (entry.Icon.Title() + " " + entry.Icon.Key + " " + entry.Icon.Category()).Contains(w, StringComparison.CurrentCultureIgnoreCase));
                    SettingsEditor.Show(entry.Button, match); if (match) shown++;
                }
                count.Text = shown == 0 ? L.Text("No icons match your search.") : L.Text("{0} of {1} icons", shown, choices.Count);
#if WINDOWS
                scroll.ScrollToTop();
#else
                scroll.Offset = default;
#endif
            }
            search.TextChanged += (_, _) => Filter(); categories.SelectionChanged += (_, _) => Filter();
            Select(current); Filter();
        }
    }
}
