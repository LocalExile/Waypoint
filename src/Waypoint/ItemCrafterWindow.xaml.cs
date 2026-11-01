using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using MaterialDesignThemes.Wpf;

namespace Waypoint;

/// <summary>A base on the left.</summary>
sealed record BaseRow(CraftBase Base, PackIconKind Icon, string Name, string Detail);

/// <summary>A modifier that can be added: from which pool, into which list, as the game words it.</summary>
sealed record ModRow(CraftMod Mod, int Pool, string Section, string TierText, string TierHint, string Text, string Detail, double Opacity);

/// <summary>A saved item on the left.</summary>
sealed record SavedRow(CraftedItem Item, PackIconKind Icon, string Name, string Detail, Brush Brush);

/// <summary>
/// The item crafter (Tools section): a base, its rarity, item level, quality, sockets, states and influences, a rare's
/// name and the modifiers with their values, shown as the item would read in game. Save keeps it in Waypoint's own
/// saved-items file; Give item sends /item_give through the client control bridge, so the character in game gets it in
/// the inventory. The data is the Gateway's own (<see cref="CrafterCatalog"/>); no game artwork is used.
/// </summary>
public partial class ItemCrafterWindow : FormWindow
{
    readonly Shared _shared;
    CrafterCatalog? _catalog;
    CraftBase? _base;
    CraftedItem _item = new();
    List<CraftedItem> _saved = CraftedItems.Load();
    string? _class;
    bool _updating;
    readonly Dictionary<string, ToggleButton> _classButtons = [];
    readonly Dictionary<string, ToggleButton> _stateButtons = [];
    readonly Dictionary<string, RadioButton> _rarityButtons = [];

    static readonly string[] Rarities = ["normal", "magic", "rare"];
    static readonly string[] Influences = ["shaper", "elder", "crusader", "redeemer", "hunter", "warlord"];
    static readonly string[] States = ["corrupted", "mirrored", "split", "synthesised", .. Influences];

    internal ItemCrafterWindow(Shared shared)
    {
        _shared = shared;
        Standalone = true;
        // The sliders' ranges raise ValueChanged while the window is still being built.
        _updating = true;
        InitializeComponent();
        _updating = false;
        foreach (string rarity in Rarities)
        {
            var button = new RadioButton { Style = (Style)FindResource("MaterialDesignTabRadioButton"), Padding = new Thickness(16, 6, 16, 6), Margin = new Thickness(0, 0, 4, 0), Tag = rarity };
            button.Checked += RarityChosen;
            _rarityButtons[rarity] = button;
            RarityButtons.Children.Add(button);
        }
        foreach (string state in States)
        {
            var chip = new ToggleButton { Style = (Style)FindResource("Crafter.Chip"), Tag = state };
            chip.Checked += StateClicked;
            chip.Unchecked += StateClicked;
            _stateButtons[state] = chip;
            StateButtons.Children.Add(chip);
            if (state == "synthesised") StateButtons.Children.Add(new Border { Width = 12 });
        }
        ShowLanguage();
        ShowSaved();
        MinWidth = Math.Min(MinWidth, SystemParameters.WorkArea.Width);
        MinHeight = Math.Min(MinHeight, SystemParameters.WorkArea.Height);
        KeyDown += WindowKeyDown;
        Loaded += (_, _) => KeepOnScreen();
        Loaded += async (_, _) => await LoadCatalog();
        L.Current.PropertyChanged += LanguageChanged;
        Theme.Changed += ThemeChanged;
        Closed += (_, _) =>
        {
            L.Current.PropertyChanged -= LanguageChanged;
            Theme.Changed -= ThemeChanged;
        };
    }

    void LanguageChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        ShowLanguage();
        ShowClasses();
        ShowModKinds();
        ShowItem();
        ShowSaved();
    });

    void ThemeChanged() => Dispatcher.BeginInvoke(() => { ShowItem(); ShowSaved(); });

    /// <summary>The texts written from code.</summary>
    void ShowLanguage()
    {
        _rarityButtons["normal"].Content = L.Text("Normal");
        _rarityButtons["magic"].Content = L.Text("Magic");
        _rarityButtons["rare"].Content = L.Text("Rare");
        foreach (var (state, chip) in _stateButtons) chip.Content = StateName(state);
    }

    static string StateName(string state) => state switch
    {
        "corrupted" => L.Text("Corrupted"),
        "mirrored" => L.Text("Mirrored"),
        "split" => L.Text("Split"),
        "synthesised" => L.Text("Synthesised"),
        "shaper" => L.Text("Shaper"),
        "elder" => L.Text("Elder"),
        "crusader" => L.Text("Crusader"),
        "redeemer" => L.Text("Redeemer"),
        "hunter" => L.Text("Hunter"),
        "warlord" => L.Text("Warlord"),
        _ => state,
    };

    // --- Loading ---

    async Task LoadCatalog()
    {
        Loading.Visibility = Visibility.Visible;
        Loading.Text = L.Text("Reading the item data from the Gateway …");
        Status(L.Text("Reading the item data from the Gateway …"));
        try
        {
            _catalog = await CrafterCatalog.LoadAsync();
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or TimeoutException
            or System.ComponentModel.Win32Exception or System.Text.Json.JsonException or KeyNotFoundException)
        {
            Loading.Text = L.Text("The item data could not be read: {0}", e.Message);
            Status(Loading.Text, error: true);
            return;
        }
        Loading.Visibility = Visibility.Collapsed;
        ShowClasses();
        ShowBases();
        ShowModKinds();
        ShowItem();
        Status(L.Text("{0} bases and {1} modifiers. Choose a base, add modifiers, then save it or give it to the character in game.",
            _catalog.Bases.Count, _catalog.Mods.Count));
        if (_saved.Count > 0) SavedPage.IsChecked = true;
    }

    // --- Pages ---

    void PageChanged(object sender, RoutedEventArgs e)
    {
        if (BasesView is null) return;
        BasesView.Visibility = BasesPage.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ModsView.Visibility = ModsPage.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        SavedView.Visibility = SavedPage.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (ModsPage.IsChecked == true) ShowMods();
    }

    void ShowModPicker(object sender, RoutedEventArgs e) => ModsPage.IsChecked = true;

    // --- Bases ---

    /// <summary>An icon for an item class: Material Design's, not the game's artwork.</summary>
    static PackIconKind IconOf(string itemClass) => itemClass switch
    {
        "Ring" => PackIconKind.Ring,
        "Amulet" => PackIconKind.Necklace,
        "Belt" => PackIconKind.Seatbelt,
        "Helmet" => PackIconKind.Helmet,
        "Body Armour" => PackIconKind.TshirtCrew,
        "Gloves" => PackIconKind.Glove,
        "Boots" => PackIconKind.ShoeFormal,
        "Shield" => PackIconKind.Shield,
        "Quiver" => PackIconKind.ArrowProjectileMultiple,
        "Bow" => PackIconKind.BowArrow,
        "Wand" => PackIconKind.MagicWand,
        "Staff" or "Warstaff" => PackIconKind.MagicStaff,
        "Sceptre" => PackIconKind.Crown,
        "One Hand Mace" or "Two Hand Mace" => PackIconKind.Mace,
        "One Hand Axe" => PackIconKind.Axe,
        "Two Hand Axe" => PackIconKind.AxeBattle,
        "One Hand Sword" => PackIconKind.Sword,
        "Two Hand Sword" => PackIconKind.SwordCross,
        "Thrusting One Hand Sword" => PackIconKind.Spear,
        "Dagger" or "Rune Dagger" => PackIconKind.KnifeMilitary,
        "Claw" => PackIconKind.Paw,
        "LifeFlask" => PackIconKind.BottleTonicPlus,
        "ManaFlask" => PackIconKind.BottleTonic,
        "HybridFlask" => PackIconKind.FlaskRoundBottom,
        "UtilityFlask" => PackIconKind.FlaskOutline,
        "Jewel" => PackIconKind.Diamond,
        "AbyssJewel" => PackIconKind.DiamondStone,
        "Map" => PackIconKind.MapOutline,
        "MapKey" => PackIconKind.Key,
        "FishingRod" => PackIconKind.Hook,
        _ => PackIconKind.Anvil,
    };

    /// <summary>The order the class buttons come in: jewellery, armour, weapons, then the rest.</summary>
    static readonly string[] ClassOrder =
    [
        "Ring", "Amulet", "Belt", "Helmet", "Body Armour", "Gloves", "Boots", "Shield", "Quiver",
        "One Hand Sword", "Thrusting One Hand Sword", "Two Hand Sword", "One Hand Axe", "Two Hand Axe", "One Hand Mace", "Two Hand Mace",
        "Sceptre", "Dagger", "Rune Dagger", "Claw", "Bow", "Wand", "Staff", "Warstaff",
        "LifeFlask", "ManaFlask", "HybridFlask", "UtilityFlask", "Jewel", "AbyssJewel",
    ];

    string ClassName(string itemClass) => _catalog?.Classes.GetValueOrDefault(itemClass) ?? itemClass;

    void ShowClasses()
    {
        if (_catalog is null) return;
        ClassButtons.Children.Clear();
        _classButtons.Clear();
        var classes = _catalog.Classes.Keys.OrderBy(c => Array.IndexOf(ClassOrder, c) is int i and >= 0 ? i : 100).ThenBy(c => c, StringComparer.Ordinal);
        foreach (string c in classes.Prepend(""))
        {
            var button = new ToggleButton
            {
                Style = (Style)FindResource("Crafter.Chip"),
                Width = 38,
                Height = 34,
                Padding = new Thickness(0),
                Tag = c,
                IsChecked = (_class ?? "") == c,
                Content = new PackIcon { Kind = c.Length == 0 ? PackIconKind.ViewGridOutline : IconOf(c), Width = 20, Height = 20 },
                ToolTip = c.Length == 0 ? L.Text("All classes") : ClassName(c),
            };
            System.Windows.Automation.AutomationProperties.SetName(button, (string)button.ToolTip);
            button.Click += ClassClicked;
            _classButtons[c] = button;
            ClassButtons.Children.Add(button);
        }
    }

    void ClassClicked(object sender, RoutedEventArgs e)
    {
        string c = (string)((FrameworkElement)sender).Tag;
        _class = c.Length == 0 ? null : c;
        foreach (var (key, button) in _classButtons) button.IsChecked = key == (_class ?? "");
        ShowBases();
    }

    void BaseSearchChanged(object sender, TextChangedEventArgs e) => ShowBases();

    void ShowBases()
    {
        if (_catalog is null) return;
        string search = BaseSearch.Text.Trim();
        var rows = _catalog.Bases
            .Where(b => _class is null || b.Class == _class)
            .Where(b => search.Length == 0 || b.Name.Contains(search, StringComparison.OrdinalIgnoreCase) || b.Id.Contains(search, StringComparison.OrdinalIgnoreCase))
            .OrderBy(b => Array.IndexOf(ClassOrder, b.Class) is int i and >= 0 ? i : 100).ThenBy(b => b.Class, StringComparer.Ordinal)
            .ThenBy(b => b.DropLevel).ThenBy(b => b.Name, StringComparer.OrdinalIgnoreCase)
            .Select(b => new BaseRow(b, IconOf(b.Class), b.Name, BaseDetail(b)))
            .ToList();
        _updating = true;
        BaseList.ItemsSource = rows;
        BaseList.SelectedItem = rows.FirstOrDefault(r => r.Base == _base);
        _updating = false;
    }

    string BaseDetail(CraftBase b)
    {
        var parts = new List<string> { ClassName(b.Class), L.Text("level {0}", b.DropLevel) };
        string[] names = [L.Text("Str"), L.Text("Dex"), L.Text("Int")];
        var attributes = b.Requirements.Select((r, i) => r > 0 ? $"{names[i]} {r}" : null).OfType<string>().ToList();
        if (attributes.Count > 0) parts.Add(string.Join(" ", attributes));
        return string.Join(" · ", parts);
    }

    void BaseChosen(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || BaseList.SelectedItem is not BaseRow row || row.Base == _base) return;
        SetBase(row.Base);
    }

    /// <summary>A new base: its implicits replace the old ones; explicit modifiers it can have stay.</summary>
    void SetBase(CraftBase b)
    {
        if (_catalog is null) return;
        var kept = _item.Mods.Where(m => m.Section != "implicit" && b.Pools.Keys.Any(k => _catalog.Pool(b, k).Any(p => p.Id == m.Id))).ToList();
        _base = b;
        _item.Base = b.Id;
        _item.Mods = [.. b.Implicits.Select(_catalog.Mods.GetValueOrDefault).OfType<CraftMod>().Select(m => NewMod(m, "implicit")), .. kept];
        if (!b.Quality) _item.Quality = 0;
        if (b.Sockets == 0) _item.Sockets = null;
        ShowItem();
        ShowModKinds();
    }

    static CraftedMod NewMod(CraftMod mod, string section) =>
        new() { Id = mod.Id, Section = section, Values = [.. mod.Stats.Select(s => Math.Max(s.Min, s.Max))] };

    // --- The modifiers to add ---

    /// <summary>A kind in the picker: a pool of the base, the list its mods go into, and which of them.</summary>
    sealed record ModKindChoice(string Label, string Pool, string Section, Func<CraftMod, bool> Filter)
    {
        public override string ToString() => Label;
    }

    void ShowModKinds()
    {
        if (_catalog is null) return;
        int selected = ModKind.SelectedIndex;
        var kinds = new List<ModKindChoice>
        {
            new(L.Text("Prefixes"), "pool", "explicit", m => m.Prefix),
            new(L.Text("Suffixes"), "pool", "explicit", m => m.Suffix),
            new(L.Text("Crafted (bench)"), "crafted", "explicit", _ => true),
        };
        foreach (string influence in Influences)
            if (_base?.Pools.ContainsKey(influence) == true)
                kinds.Add(new(L.Text("{0} modifiers", StateName(influence)), influence, "explicit", _ => true));
        kinds.Add(new(L.Text("Corruption implicits"), "corrupted", "implicit", _ => true));
        kinds.Add(new(L.Text("Eldritch implicits"), "eldritch", "implicit", _ => true));
        kinds.Add(new(L.Text("Any modifier (no rules)"), "", "explicit", _ => true));
        ModKind.ItemsSource = kinds;
        ModKind.SelectedIndex = selected >= 0 && selected < kinds.Count ? selected : 0;
        ShowMods();
    }

    void ModFilterChanged(object sender, RoutedEventArgs e) => ShowMods();

    void ShowMods()
    {
        if (_catalog is null || ModList is null || ModsView.Visibility != Visibility.Visible) return;
        if (_base is null)
        {
            ModList.ItemsSource = null;
            ModHint.Text = L.Text("Choose a base first: what can roll depends on it.");
            return;
        }
        if (ModKind.SelectedItem is not ModKindChoice kind) return;
        string search = ModSearch.Text.Trim();
        int level = _item.ItemLevel;
        IEnumerable<(CraftMod Mod, int Pool)> mods = kind.Pool.Length == 0
            ? _catalog.Mods.Values.Select(m => (m, -1))
            : _base.Pools.TryGetValue(kind.Pool, out int pool) ? _catalog.Pools[pool].Where(kind.Filter).Select(m => (m, pool)) : [];
        var rows = new List<ModRow>();
        foreach (var group in mods.GroupBy(p => p.Mod.Group))
        {
            var tiers = group.OrderByDescending(p => p.Mod.Level).ToList();
            var shown = BestTiers.IsChecked == true
                ? tiers.Where(p => AnyLevel.IsChecked == true || p.Mod.Level <= level).Take(1)
                : tiers.Where(p => AnyLevel.IsChecked == true || p.Mod.Level <= level);
            foreach (var (mod, poolId) in shown)
            {
                var lines = _catalog.Lines(mod);
                string text = lines.Count > 0 ? string.Join("\n", lines) : $"{mod.Id} ({string.Join(", ", mod.Stats.Select(s => s.Id))})";
                if (search.Length > 0 && !text.Contains(search, StringComparison.OrdinalIgnoreCase) && !mod.Id.Contains(search, StringComparison.OrdinalIgnoreCase)
                    && !mod.Name.Contains(search, StringComparison.OrdinalIgnoreCase) && !mod.Tags.Any(t => t.Contains(search, StringComparison.OrdinalIgnoreCase)))
                    continue;
                int tier = poolId >= 0 ? _catalog.Tier(poolId, mod) : 0;
                string detail = string.Join(" · ", new[] { mod.Name, L.Text("item level {0}", mod.Level), mod.Id }.Where(s => s.Length > 0));
                rows.Add(new ModRow(mod, poolId, kind.Section, tier > 0 ? $"T{tier}" : "·", tier > 0 ? L.Text("Tier {0} of {1}", tier, tiers.Count) : "",
                    text, detail, mod.Level <= level ? 1 : .5));
            }
            if (rows.Count > 1500) break;
        }
        ModList.ItemsSource = rows;
        ModHint.Text = rows.Count == 0 ? L.Text("Nothing matches.") : L.Text("{0} shown. The best tier for the item level comes first in each group.", rows.Count);
    }

    void AddModClicked(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is ModRow row) AddMod(row);
    }

    void ModListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ModList.SelectedItem is ModRow row && e.OriginalSource is not Button) AddMod(row);
    }

    void ModListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && ModList.SelectedItem is ModRow row) { AddMod(row); e.Handled = true; }
    }

    void AddMod(ModRow row)
    {
        _item.Mods.Add(NewMod(row.Mod, row.Section));
        ShowItem();
        Status(L.Text("Added {0}", row.Text.Replace('\n', ' ')));
    }

    // --- The item ---

    void RarityChosen(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        _item.Rarity = (string)((FrameworkElement)sender).Tag;
        ShowItem();
    }

    void StateClicked(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        var chip = (ToggleButton)sender;
        string state = (string)chip.Tag;
        if (chip.IsChecked == true) { if (!_item.States.Contains(state)) _item.States.Add(state); }
        else _item.States.Remove(state);
        if (Influences.Contains(state)) ShowModKinds();
        ShowItem();
    }

    void LevelChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updating) return;
        _item.ItemLevel = (int)LevelSlider.Value;
        LevelBox.Text = _item.ItemLevel.ToString();
        ShowPreview();
        ShowMods();
    }

    void QualityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updating) return;
        _item.Quality = (int)QualitySlider.Value;
        QualityBox.Text = _item.Quality.ToString();
        ShowPreview();
    }

    void LevelTyped(object sender, RoutedEventArgs e)
    {
        if (int.TryParse(LevelBox.Text, out int level)) LevelSlider.Value = Math.Clamp(level, 1, 100);
        LevelBox.Text = ((int)LevelSlider.Value).ToString();
    }

    void QualityTyped(object sender, RoutedEventArgs e)
    {
        if (int.TryParse(QualityBox.Text, out int quality)) QualitySlider.Value = Math.Clamp(quality, 0, 30);
        QualityBox.Text = ((int)QualitySlider.Value).ToString();
    }

    void NumberKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (sender == LevelBox) LevelTyped(sender, e); else QualityTyped(sender, e);
        e.Handled = true;
    }

    void SocketsChanged(object sender, TextChangedEventArgs e)
    {
        if (_updating) return;
        string text = SocketBox.Text.Trim();
        _item.Sockets = text.Length == 0 ? null : text;
        ShowSockets();
        ShowPreview();
    }

    /// <summary>The sockets typed, as colours and link groups; null with the reason when they cannot be.</summary>
    (List<char> Colours, List<int> Groups)? ParseSockets(string? layout, out string? error)
    {
        error = null;
        var colours = new List<char>();
        var groups = new List<int>();
        foreach (string word in (layout ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            int size = 0;
            foreach (char c in word)
            {
                if (c == '-') continue;
                if (c is not ('R' or 'G' or 'B' or 'W')) { error = L.Text("'{0}' is not a socket colour (R, G, B or W)", c); return null; }
                colours.Add(c);
                size++;
            }
            if (size > 0) groups.Add(size);
        }
        if (_base is not null && colours.Count > _base.Sockets) { error = L.Text("{0} has room for at most {1} sockets", _base.Name, _base.Sockets); return null; }
        return (colours, groups);
    }

    void ShowSockets()
    {
        SocketPalette.Children.Clear();
        if (ParseSockets(_item.Sockets, out string? error) is not var (colours, groups))
        {
            SocketError.Text = error;
            SocketError.Visibility = Visibility.Visible;
            return;
        }
        SocketError.Visibility = Visibility.Collapsed;
        SocketPalette.Children.Add(SocketView(colours, groups, 14));
    }

    /// <summary>Sockets as coloured circles, joined by a bar within a link group.</summary>
    static FrameworkElement SocketView(IReadOnlyList<char> colours, IReadOnlyList<int> groups, double size)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        int at = 0;
        foreach (int group in groups)
        {
            for (int i = 0; i < group; i++, at++)
            {
                if (i > 0) panel.Children.Add(new Rectangle { Width = size / 2, Height = 3, Fill = Theme.Brush("Shell.TextSecondary"), VerticalAlignment = VerticalAlignment.Center });
                panel.Children.Add(new Ellipse { Width = size, Height = size, Fill = new SolidColorBrush(SocketColour(colours[at])), Stroke = Theme.Brush("Shell.Border"), StrokeThickness = 1 });
            }
            panel.Children.Add(new Border { Width = size / 2 });
        }
        return panel;
    }

    /// <summary>The game's socket colours: what they mean stays the same in every colour scheme.</summary>
    static Color SocketColour(char c) => c switch
    {
        'R' => Color.FromRgb(0xD2, 0x3A, 0x3A),
        'G' => Color.FromRgb(0x4C, 0xB0, 0x4C),
        'B' => Color.FromRgb(0x4A, 0x78, 0xE0),
        _ => Color.FromRgb(0xE8, 0xE8, 0xE8),
    };

    void NameChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating) return;
        _item.Name = NameFirst.SelectedItem is RareWord a && NameSecond.SelectedItem is RareWord b ? [a.Key, b.Key] : null;
        ShowPreview();
    }

    void RandomName(object sender, RoutedEventArgs e)
    {
        if (_catalog is null) return;
        var first = _catalog.Words.Where(w => w.List == 1).ToList();
        var second = _catalog.Words.Where(w => w.List == 2).ToList();
        if (first.Count == 0 || second.Count == 0) return;
        _item.Name = [first[Random.Shared.Next(first.Count)].Key, second[Random.Shared.Next(second.Count)].Key];
        ShowItem();
    }

    void ClearMods(object sender, RoutedEventArgs e)
    {
        _item.Mods.Clear();
        ShowItem();
    }

    /// <summary>Everything about the item on screen: the controls, the modifier rows and the preview.</summary>
    void ShowItem()
    {
        _updating = true;
        try
        {
            Editor.IsEnabled = _base is not null;
            SaveButton.IsEnabled = GiveButton.IsEnabled = CopyButton.IsEnabled = _base is not null;
            if (_base is null)
            {
                BaseTitle.Text = L.Text("Choose a base on the left");
                BaseSummary.Text = "";
                BaseIcon.Kind = PackIconKind.Anvil;
            }
            else
            {
                BaseTitle.Text = _base.Name;
                BaseSummary.Text = BaseDetail(_base);
                BaseIcon.Kind = IconOf(_base.Class);
            }
            foreach (var (rarity, button) in _rarityButtons) button.IsChecked = rarity == _item.Rarity;
            foreach (var (state, chip) in _stateButtons) chip.IsChecked = _item.States.Contains(state);
            LevelSlider.Value = _item.ItemLevel;
            LevelBox.Text = _item.ItemLevel.ToString();
            QualityPanel.Visibility = _base?.Quality != false ? Visibility.Visible : Visibility.Collapsed;
            QualitySlider.Value = _item.Quality;
            QualityBox.Text = _item.Quality.ToString();
            SocketPanel.Visibility = _base is null || _base.Sockets > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (SocketBox.Text != (_item.Sockets ?? "")) SocketBox.Text = _item.Sockets ?? "";
            ShowSockets();
            NamePanel.Visibility = _item.Rarity == "rare" ? Visibility.Visible : Visibility.Collapsed;
            if (_catalog is not null)
            {
                if (NameFirst.ItemsSource is null)
                {
                    NameFirst.ItemsSource = _catalog.Words.Where(w => w.List == 1).OrderBy(w => w.Text, StringComparer.OrdinalIgnoreCase).ToList();
                    NameSecond.ItemsSource = _catalog.Words.Where(w => w.List == 2).OrderBy(w => w.Text.Trim(), StringComparer.OrdinalIgnoreCase).ToList();
                    NameFirst.DisplayMemberPath = NameSecond.DisplayMemberPath = nameof(RareWord.Text);
                }
                NameFirst.SelectedItem = _item.Name is [var a, _] ? _catalog.Words.FirstOrDefault(w => w.Key == a && w.List == 1) : null;
                NameSecond.SelectedItem = _item.Name is [_, var b] ? _catalog.Words.FirstOrDefault(w => w.Key == b && w.List == 2) : null;
            }
            LabelBox.Text = _item.Label;
            ShowModRows();
        }
        finally { _updating = false; }
        ShowPreview();
        ShowMods();
    }

    /// <summary>Prefix and suffix limits for the rarity: magic one of each, rare three (jewels two).</summary>
    int AffixLimit => _item.Rarity switch
    {
        "magic" => 1,
        "rare" => _base?.Class is "Jewel" or "AbyssJewel" ? 2 : 3,
        _ => 0,
    };

    string KindOf(CraftMod mod, CraftedMod m) => mod.Generation switch
    {
        1 => mod.Domain == 9 ? L.Text("Crafted prefix") : L.Text("Prefix"),
        2 => mod.Domain == 9 ? L.Text("Crafted suffix") : L.Text("Suffix"),
        5 => L.Text("Corruption"),
        28 => L.Text("Exarch"),
        29 => L.Text("Eater"),
        _ => m.Section == "implicit" ? L.Text("Implicit") : L.Text("Modifier"),
    };

    void ShowModRows()
    {
        ModRows.Children.Clear();
        NoMods.Visibility = _item.Mods.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_catalog is null) return;
        int prefixes = 0, suffixes = 0;
        var families = new Dictionary<int, int>();
        foreach (var m in _item.Mods)
            if (_catalog.Mods.GetValueOrDefault(m.Id) is { } mod && m.Section == "explicit")
            {
                if (mod.Prefix) prefixes++;
                if (mod.Suffix) suffixes++;
                foreach (int f in mod.Families) families[f] = families.GetValueOrDefault(f) + 1;
            }
        int limit = AffixLimit;
        AffixCount.Text = L.Text("prefixes {0}/{1} · suffixes {2}/{3}", prefixes, limit, suffixes, limit);
        AffixCount.Foreground = prefixes > limit || suffixes > limit ? Theme.Brush("Shell.Warning") : Theme.Brush("Shell.TextSecondary");
        for (int index = 0; index < _item.Mods.Count; index++)
        {
            var m = _item.Mods[index];
            if (_catalog.Mods.GetValueOrDefault(m.Id) is not { } mod) continue;
            bool clash = m.Section == "explicit" && mod.Families.Any(f => families.GetValueOrDefault(f) > 1);
            ModRows.Children.Add(ModCard(m, mod, clash));
        }
    }

    /// <summary>A modifier on the item: its kind, tier, the line as the game words it, a slider per value and Remove.</summary>
    FrameworkElement ModCard(CraftedMod m, CraftMod mod, bool clash)
    {
        var catalog = _catalog!;
        var card = new Border
        {
            Background = Theme.Brush("Shell.RowAlt"),
            BorderBrush = clash ? Theme.Brush("Shell.Warning") : Theme.Brush("Shell.Border"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 8, 6, 8),
            Margin = new Thickness(0, 0, 0, 6),
        };
        var body = new DockPanel();
        card.Child = body;
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        DockPanel.SetDock(head, Dock.Top);
        body.Children.Add(head);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(buttons, Dock.Right);
        head.Children.Add(buttons);
        void Move(int by)
        {
            int at = _item.Mods.IndexOf(m), to = Math.Clamp(at + by, 0, _item.Mods.Count - 1);
            if (at < 0 || at == to) return;
            _item.Mods.RemoveAt(at);
            _item.Mods.Insert(to, m);
            ShowItem();
        }
        buttons.Children.Add(IconButton(PackIconKind.ChevronUp, L.Text("Move up"), () => Move(-1)));
        buttons.Children.Add(IconButton(PackIconKind.ChevronDown, L.Text("Move down"), () => Move(1)));
        buttons.Children.Add(IconButton(PackIconKind.Close, L.Text("Remove"), () => { _item.Mods.Remove(m); ShowItem(); }));

        var kind = new Border { Style = (Style)FindResource("Crafter.Tag"), Child = new TextBlock { Text = KindOf(mod, m), FontSize = 11, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.NoWrap } };
        DockPanel.SetDock(kind, Dock.Left);
        head.Children.Add(kind);

        // The tiers of its group in the pool it came from, when there are several.
        int pool = PoolOf(mod, m.Section);
        var tiers = pool >= 0 ? catalog.Tiers(pool, mod) : [];
        if (tiers.Count > 1)
        {
            var tierBox = new ComboBox { Margin = new Thickness(0, 0, 8, 0), MinWidth = 64, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
            for (int i = 0; i < tiers.Count; i++)
                tierBox.Items.Add(new ComboBoxItem { Content = L.Text("T{0} · level {1}", i + 1, tiers[i].Level), Tag = tiers[i] });
            tierBox.SelectedIndex = tiers.ToList().FindIndex(t => t.Id == mod.Id);
            tierBox.SelectionChanged += (_, _) =>
            {
                if (tierBox.SelectedItem is ComboBoxItem { Tag: CraftMod chosen } && chosen.Id != m.Id)
                {
                    var replacement = NewMod(chosen, m.Section);
                    _item.Mods[_item.Mods.IndexOf(m)] = replacement;
                    ShowItem();
                }
            };
            DockPanel.SetDock(tierBox, Dock.Left);
            head.Children.Add(tierBox);
        }
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold };
        void ShowText() => text.Text = ModLine(mod, m.Values);
        ShowText();
        head.Children.Add(text);

        var sliders = new StackPanel();
        body.Children.Add(sliders);
        if (clash) sliders.Children.Add(new TextBlock { Text = L.Text("Another modifier of the same group is on the item: the game would not roll both."), Foreground = Theme.Brush("Shell.Warning"), FontSize = 11.5, TextWrapping = TextWrapping.Wrap });
        for (int i = 0; i < mod.Stats.Count; i++)
        {
            var stat = mod.Stats[i];
            int index = i;
            while (m.Values.Count <= index) m.Values.Add(stat.Max);
            int min = Math.Min(stat.Min, stat.Max), max = Math.Max(stat.Min, stat.Max);
            var row = new DockPanel { Margin = new Thickness(0, 2, 6, 0) };
            var label = new TextBlock { Text = stat.Id.Replace('_', ' '), Width = 190, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap, ToolTip = stat.Id };
            label.SetResourceReference(TextBlock.ForegroundProperty, "Shell.TextSecondary");
            DockPanel.SetDock(label, Dock.Left);
            row.Children.Add(label);
            var box = new TextBox { Width = 64, Margin = new Thickness(10, 0, 0, 0), TextAlignment = TextAlignment.Center, Text = m.Values[index].ToString(), ToolTip = L.Text("{0} to {1}; any whole number is accepted", min, max) };
            DockPanel.SetDock(box, Dock.Right);
            row.Children.Add(box);
            if (min == max)
            {
                row.Children.Add(new TextBlock { Text = L.Text("fixed"), FontSize = 11, Margin = new Thickness(8, 0, 0, 0), Foreground = Theme.Brush("Shell.TextSecondary"), VerticalAlignment = VerticalAlignment.Center });
            }
            else
            {
                var slider = new Slider { Minimum = min, Maximum = max, Value = Math.Clamp(m.Values[index], min, max), IsSnapToTickEnabled = true, TickFrequency = 1, VerticalAlignment = VerticalAlignment.Center };
                slider.ValueChanged += (_, _) =>
                {
                    if (_updating) return;
                    m.Values[index] = (int)slider.Value;
                    box.Text = m.Values[index].ToString();
                    ShowText();
                    ShowPreview();
                };
                box.Tag = slider;
                row.Children.Add(slider);
            }
            void Typed()
            {
                if (!int.TryParse(box.Text, out int value)) { box.Text = m.Values[index].ToString(); return; }
                m.Values[index] = value;
                if (box.Tag is Slider s) { _updating = true; s.Value = Math.Clamp(value, min, max); _updating = false; }
                ShowText();
                ShowPreview();
            }
            box.LostFocus += (_, _) => Typed();
            box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Typed(); e.Handled = true; } };
            sliders.Children.Add(row);
        }
        return card;
    }

    Button IconButton(PackIconKind icon, string tip, Action click)
    {
        var button = new Button { Style = (Style)FindResource("Shell.IconButton"), Width = 26, Height = 26, ToolTip = tip, Content = new PackIcon { Kind = icon, Width = 16, Height = 16 } };
        System.Windows.Automation.AutomationProperties.SetName(button, tip);
        button.Click += (_, _) => click();
        return button;
    }

    /// <summary>The base's pool a mod belongs to (for its tiers), or -1.</summary>
    int PoolOf(CraftMod mod, string section)
    {
        if (_base is null || _catalog is null) return -1;
        foreach (var (key, id) in _base.Pools)
            if ((section == "implicit") == (key is "corrupted" or "eldritch") && _catalog.Pools.TryGetValue(id, out var pool) && pool.Any(p => p.Id == mod.Id))
                return id;
        return -1;
    }

    /// <summary>A mod with its values as one line of text, or its Id and values when the game has no words for it.</summary>
    string ModLine(CraftMod mod, IReadOnlyList<int> values)
    {
        var lines = _catalog!.Lines(mod, values);
        return lines.Count > 0 ? string.Join("\n", lines) : $"{mod.Id}: {string.Join(", ", values)}";
    }

    // --- The preview ---

    /// <summary>The game's rarity and modifier colours, made readable on the scheme's background: they mean the same in every
    /// colour scheme, so they are not theme colours.</summary>
    static Brush GameColour(string what)
    {
        var background = Theme.Brush("Shell.Background") is SolidColorBrush b ? b.Color : Colors.White;
        Color color = what switch
        {
            "magic" or "mod" => Color.FromRgb(0x88, 0x88, 0xFF),
            "rare" => Color.FromRgb(0xFF, 0xFF, 0x77),
            "unique" => Color.FromRgb(0xAF, 0x60, 0x25),
            "crafted" => Color.FromRgb(0xB4, 0xB4, 0xFF),
            "corrupted" => Color.FromRgb(0xD2, 0x00, 0x00),
            "fractured" => Color.FromRgb(0xA2, 0x91, 0x62),
            _ => ((SolidColorBrush)Theme.Brush("Shell.Text")).Color,
        };
        var brush = new SolidColorBrush(ThemePalette.Ensure(color, [background], 4.5));
        brush.Freeze();
        return brush;
    }

    static Brush Tint(Brush brush, byte alpha)
    {
        var tint = new SolidColorBrush(((SolidColorBrush)brush).Color) { Opacity = alpha / 255d };
        tint.Freeze();
        return tint;
    }

    string ItemName()
    {
        if (_base is null || _catalog is null) return "";
        if (_item.Rarity == "magic")
        {
            var mods = _item.Mods.Where(m => m.Section == "explicit").Select(m => _catalog.Mods.GetValueOrDefault(m.Id)).OfType<CraftMod>().ToList();
            string prefix = mods.FirstOrDefault(m => m.Prefix)?.Name ?? "", suffix = mods.FirstOrDefault(m => m.Suffix)?.Name ?? "";
            return string.Join(" ", new[] { prefix, _base.Name, suffix }.Where(s => s.Length > 0));
        }
        return _base.Name;
    }

    void ShowPreview()
    {
        PreviewTitle.Children.Clear();
        PreviewBody.Children.Clear();
        if (_base is null || _catalog is null)
        {
            PreviewHeader.Background = Brushes.Transparent;
            PreviewCard.BorderBrush = Theme.Brush("Shell.Border");
            PreviewBody.Children.Add(new TextBlock { Text = L.Text("The item shows here as you make it."), Foreground = Theme.Brush("Shell.TextSecondary"), TextWrapping = TextWrapping.Wrap });
            return;
        }
        var rarity = GameColour(_item.Rarity == "normal" ? "normal" : _item.Rarity);
        PreviewCard.BorderBrush = Tint(rarity, 160);
        PreviewHeader.Background = Tint(rarity, 40);
        TextBlock Title(string text) => new() { Text = text, Foreground = rarity, FontSize = 16, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        if (_item.Rarity == "rare")
        {
            string name = _item.Name is [var a, var b]
                ? (_catalog.Words.FirstOrDefault(w => w.Key == a)?.Text ?? "") + (_catalog.Words.FirstOrDefault(w => w.Key == b)?.Text ?? "")
                : L.Text("(random name)");
            PreviewTitle.Children.Add(Title(name));
        }
        PreviewTitle.Children.Add(Title(ItemName()));

        void Line(string text, Brush? brush = null, bool center = true, double size = 13) => PreviewBody.Children.Add(new TextBlock
        {
            Text = text, Foreground = brush ?? Theme.Brush("Shell.TextSecondary"), FontSize = size, TextWrapping = TextWrapping.Wrap,
            TextAlignment = center ? TextAlignment.Center : TextAlignment.Left, Margin = new Thickness(0, 1, 0, 1),
        });
        void Separator()
        {
            if (PreviewBody.Children.Count == 0 || PreviewBody.Children[^1] is Rectangle) return;
            PreviewBody.Children.Add(new Rectangle { Height = 1, Margin = new Thickness(24, 6, 24, 6), Fill = Tint(rarity, 120) });
        }

        Line(ClassName(_base.Class));
        if (_item.Quality > 0) Line(L.Text("Quality: +{0}%", _item.Quality), Theme.Brush("Shell.Text"));
        Separator();
        Line(L.Text("Item Level: {0}", _item.ItemLevel), Theme.Brush("Shell.Text"));
        string[] names = [L.Text("Str"), L.Text("Dex"), L.Text("Int")];
        var requirements = _base.Requirements.Select((r, i) => r > 0 ? $"{names[i]} {r}" : null).OfType<string>().ToList();
        if (requirements.Count > 0) Line(L.Text("Requires {0}", string.Join(", ", requirements)));
        if (_base.Sockets > 0)
        {
            if (_item.Sockets is { } layout && ParseSockets(layout, out _) is var (colours, groups) && colours.Count > 0)
                PreviewBody.Children.Add(new Border { Margin = new Thickness(0, 6, 0, 2), HorizontalAlignment = HorizontalAlignment.Center, Child = SocketView(colours, groups, 16) });
            else if (_item.Sockets is null) Line(L.Text("Sockets: as a new item gets them"), size: 11.5);
        }

        var implicits = _item.Mods.Where(m => m.Section != "explicit").ToList();
        var explicits = _item.Mods.Where(m => m.Section == "explicit").ToList();
        void Mods(IEnumerable<CraftedMod> list)
        {
            foreach (var m in list)
                if (_catalog.Mods.GetValueOrDefault(m.Id) is { } mod)
                    foreach (string line in ModLine(mod, m.Values).Split('\n'))
                        Line(line, GameColour(mod.Domain == 9 ? "crafted" : "mod"));
        }
        if (implicits.Count > 0) { Separator(); Mods(implicits); }
        if (explicits.Count > 0) { Separator(); Mods(explicits); }

        var marks = new List<(string, Brush)>();
        foreach (string influence in Influences.Where(_item.States.Contains)) marks.Add((L.Text("{0} Item", StateName(influence)), Theme.Brush("Shell.Text")));
        if (_item.States.Contains("split")) marks.Add((L.Text("Split"), Theme.Brush("Shell.Text")));
        if (_item.States.Contains("synthesised")) marks.Add((L.Text("Synthesised Item"), Theme.Brush("Shell.Text")));
        if (_item.States.Contains("mirrored")) marks.Add((L.Text("Mirrored"), Theme.Brush("Shell.Text")));
        if (_item.States.Contains("corrupted")) marks.Add((L.Text("Corrupted"), GameColour("corrupted")));
        if (marks.Count > 0)
        {
            Separator();
            foreach (var (text, brush) in marks) Line(text, brush);
        }
    }

    // --- Saving and giving ---

    void ShowSaved()
    {
        string search = SavedSearch?.Text.Trim() ?? "";
        var rows = _saved.OrderByDescending(i => i.Saved)
            .Select(i =>
            {
                var b = _catalog?.Bases.FirstOrDefault(x => x.Id == i.Base);
                string name = i.Label.Length > 0 ? i.Label : b?.Name ?? i.Base;
                string detail = string.Join(" · ", new[]
                {
                    b?.Name ?? i.Base, RarityName(i.Rarity), L.Text("{0} modifiers", i.Mods.Count), i.Saved.ToLocalTime().ToString("g", L.Current.Culture),
                });
                return new SavedRow(i, b is null ? PackIconKind.Anvil : IconOf(b.Class), name, detail, GameColour(i.Rarity == "normal" ? "normal" : i.Rarity));
            })
            .Where(r => search.Length == 0 || r.Name.Contains(search, StringComparison.OrdinalIgnoreCase) || r.Detail.Contains(search, StringComparison.OrdinalIgnoreCase))
            .ToList();
        _updating = true;
        SavedList.ItemsSource = rows;
        SavedList.SelectedItem = rows.FirstOrDefault(r => r.Item.Key == _item.Key);
        _updating = false;
        SavedEmpty.Visibility = _saved.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowSavedButtons();
    }

    void ShowSavedButtons()
    {
        DuplicateButton.IsEnabled = SavedList.SelectedItems.Count == 1;
        DeleteButton.IsEnabled = SavedList.SelectedItems.Count > 0;
    }

    static string RarityName(string rarity) => rarity switch
    {
        "magic" => L.Text("Magic"),
        "rare" => L.Text("Rare"),
        _ => L.Text("Normal"),
    };

    void SavedSearchChanged(object sender, TextChangedEventArgs e) => ShowSaved();

    void LabelChanged(object sender, TextChangedEventArgs e)
    {
        if (!_updating) _item.Label = LabelBox.Text;
    }

    void SavedChosen(object sender, SelectionChangedEventArgs e)
    {
        ShowSavedButtons();
        // With several selected (to delete them) the item on screen stays.
        if (_updating || SavedList.SelectedItems.Count != 1 || SavedList.SelectedItem is not SavedRow row || _catalog is null) return;
        Open(row.Item.Copy());
    }

    void Open(CraftedItem item)
    {
        _item = item;
        _base = _catalog?.Bases.FirstOrDefault(b => b.Id == item.Base);
        if (_base is null && item.Base.Length > 0) Status(L.Text("This Gateway has no base {0}", item.Base), error: true);
        ShowBases();
        ShowModKinds();
        ShowItem();
    }

    void NewItem(object sender, RoutedEventArgs e)
    {
        Open(new CraftedItem { Rarity = _item.Rarity, ItemLevel = _item.ItemLevel });
        BasesPage.IsChecked = true;
    }

    /// <summary>An item pasted as text (<see cref="ItemImportWindow"/>), as a new unsaved item; the clipboard's text is
    /// put in when it looks like an item.</summary>
    void ImportItem(object sender, RoutedEventArgs e)
    {
        if (_catalog is null) return;
        string text = "";
        try
        {
            if (Clipboard.ContainsText() && Clipboard.GetText() is { } clipboard && clipboard.Contains("Rarity:", StringComparison.OrdinalIgnoreCase)) text = clipboard;
        }
        catch (System.Runtime.InteropServices.ExternalException) { }
        var dialog = new ItemImportWindow(this, _catalog, text);
        if (dialog.ShowDialog() != true || dialog.Imported is not { } imported) return;
        Open(imported.Item);
        int left = imported.Unmatched.Count;
        Status(left == 0
            ? L.Text("Imported {0} with {1} modifiers. Save keeps it in the list.", imported.Name, imported.Item.Mods.Count)
            : L.Text("Imported {0} with {1} modifiers; {2} lines were left out. Save keeps it in the list.", imported.Name, imported.Item.Mods.Count, left));
    }

    void WindowKeyDown(object sender, KeyEventArgs e)
    {
        // A text box takes Ctrl+V itself; anywhere else it imports.
        if (e.Key != Key.V || Keyboard.Modifiers != ModifierKeys.Control || e.OriginalSource is TextBoxBase or ComboBox) return;
        ImportItem(sender, e);
        e.Handled = true;
    }

    /// <summary>The crafter as it opens: an empty item, every search and filter cleared, the Base page. Asks first when the
    /// item on screen has changes no save holds.</summary>
    void ResetCrafter(object sender, RoutedEventArgs e)
    {
        var saved = _saved.FirstOrDefault(i => i.Key == _item.Key);
        bool unsaved = _base is not null && (saved is null || saved.Spec() != _item.Spec() || saved.Label != _item.Label);
        if (unsaved && !Dialog.Ask(this, DialogKind.Warning, L.Text("Reset the crafter"),
            L.Text("Start over with an empty item? The changes to this one are not saved."), L.Text("Reset"), cancelByDefault: true)) return;
        _class = null;
        BaseSearch.Text = ModSearch.Text = SavedSearch.Text = "";
        BestTiers.IsChecked = true;
        AnyLevel.IsChecked = false;
        ModKind.SelectedIndex = 0;
        SavedList.UnselectAll();
        Open(new CraftedItem());
        ShowClasses();
        BasesPage.IsChecked = true;
        Status(L.Text("The crafter is reset: choose a base."));
    }

    void DuplicateItem(object sender, RoutedEventArgs e)
    {
        if (SavedList.SelectedItem is not SavedRow row) return;
        var copy = row.Item.Copy();
        copy.Key = Guid.NewGuid().ToString("N");
        copy.Label = copy.Label.Length > 0 ? L.Text("{0} (copy)", copy.Label) : "";
        copy.Saved = DateTime.UtcNow;
        _saved.Add(copy);
        Persist();
        Open(copy.Copy());
        ShowSaved();
    }

    void DeleteItem(object sender, RoutedEventArgs e) => Delete([.. SavedList.SelectedItems.OfType<SavedRow>()]);

    void DeleteRowClicked(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is SavedRow row) Delete([row]);
    }

    void SavedListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete || SavedList.SelectedItems.Count == 0) return;
        DeleteItem(sender, e);
        e.Handled = true;
    }

    /// <summary>Saved items out of the list, once asked. The item on screen stays, as a new unsaved one if it was among them.</summary>
    void Delete(IReadOnlyList<SavedRow> rows)
    {
        if (rows.Count == 0) return;
        string question = rows.Count == 1 ? L.Text("Delete {0} from the saved items?", rows[0].Name) : L.Text("Delete {0} saved items?", rows.Count);
        if (!Dialog.Ask(this, DialogKind.Warning, L.Text("Delete saved item"), question, L.Text("Delete"), cancelByDefault: true)) return;
        var keys = rows.Select(r => r.Item.Key).ToHashSet(StringComparer.Ordinal);
        _saved.RemoveAll(i => keys.Contains(i.Key));
        if (!Persist())
        {
            _saved = CraftedItems.Load();
            ShowSaved();
            return;
        }
        if (keys.Contains(_item.Key)) _item.Key = Guid.NewGuid().ToString("N");
        ShowSaved();
        Status(rows.Count == 1 ? L.Text("Deleted {0}", rows[0].Name) : L.Text("Deleted {0} saved items", rows.Count));
    }

    void SaveItem(object sender, RoutedEventArgs e)
    {
        if (_base is null) return;
        _item.Label = LabelBox.Text.Trim();
        _item.Saved = DateTime.UtcNow;
        int at = _saved.FindIndex(i => i.Key == _item.Key);
        if (at >= 0) _saved[at] = _item.Copy(); else _saved.Add(_item.Copy());
        if (!Persist()) return;
        ShowSaved();
        Status(L.Text("Saved {0}", _item.Label.Length > 0 ? _item.Label : _base.Name));
    }

    bool Persist()
    {
        try
        {
            CraftedItems.Save(_saved);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Status(L.Text("The saved items could not be written: {0}", e.Message), error: true);
            return false;
        }
    }

    string Command() => "/item_give " + _item.Spec();

    void CopyCommand(object sender, RoutedEventArgs e)
    {
        if (_base is null) return;
        try
        {
            Clipboard.SetText(Command());
            Status(L.Text("The command is on the clipboard."));
        }
        catch (System.Runtime.InteropServices.ExternalException ex) { Status(ex.Message, error: true); }
    }

    async void GiveItem(object sender, RoutedEventArgs e)
    {
        if (_base is null) return;
        if (ParseSockets(_item.Sockets, out string? socketError) is null) { Status(socketError!, error: true); return; }
        GiveButton.IsEnabled = false;
        Status(L.Text("Giving {0} …", ItemName()));
        try
        {
            var (ok, lines) = ClientBridge.Replies(await _shared.Bridge.SendAsync(Command(), wait: 2));
            string reply = lines.Count > 0 ? string.Join(" ", lines) : L.Text("(no reply)");
            if (!ok && reply.Contains("not running", StringComparison.OrdinalIgnoreCase))
                Status(L.Text("The game is not running: start it, enter an area with a character, then give the item again."), error: true);
            else Status(reply, error: !ok || !reply.StartsWith("Added", StringComparison.Ordinal));
            _shared.Logs.Write($"item crafter: /item_give {_base.Id}: {reply}", ok ? Severity.Info : Severity.Error);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or IOException or System.Text.Json.JsonException)
        {
            Status(ex.Message, error: true);
        }
        finally { GiveButton.IsEnabled = _base is not null; }
    }

    void Status(string text, bool error = false)
    {
        StatusText.Text = text;
        StatusText.ToolTip = text;
        StatusText.Foreground = Theme.Brush(error ? "Shell.Error" : "Shell.TextSecondary");
        StatusIcon.Kind = error ? PackIconKind.AlertCircleOutline : PackIconKind.InformationOutline;
        StatusIcon.Foreground = StatusText.Foreground;
    }
}
