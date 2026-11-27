using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Waypoint;

/// <summary>
/// Settings → Appearance on Linux: the Linux copy of src/Waypoint/AppearanceDialog.cs, in Avalonia. Light, Dark or System, the
/// colour scheme, custom colours for the mode shown, the colour-vision profiles, the reading font, the contrast protections
/// and reduced motion, previewed in every window as they change: Apply saves them, Cancel puts back what was. The text size
/// and the stronger focus borders are Windows-only (<see cref="AppTheme"/>); what is saved for them is kept as it is.
/// </summary>
sealed class AppearanceDialog : FormWindow
{
    readonly AppearanceSettings _original;
    readonly Action<AppearanceSettings> _save;
    readonly ListBox _modes = new(), _schemes = new(), _vision = new(), _fonts = new();
    readonly StackPanel _basicColors = new(), _extraColors = new();
    readonly CheckBox _protect = new(), _stronger = new(), _motion = new();
    readonly TextBlock _feedback = new(), _summary = new();
    bool _updating, _applied;

    public AppearanceSettings Draft { get; private set; }

    public AppearanceDialog(Window? owner, AppearanceSettings? initial, Action<AppearanceSettings> save)
    {
        _original = ThemePalette.Normalize(initial);
        Draft = _original;
        _save = save;
        DialogLayout.Configure(this, owner, L.Text("Appearance and accessibility"), 880);
        MinWidth = 640; MinHeight = 540; SizeToContent = SizeToContent.Manual;
        Height = Math.Min(820, DialogLayout.WorkHeight(this, owner) - 40);
        var root = new DockPanel { Margin = new Thickness(20) };
        var footer = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(footer, Dock.Bottom);
        BindBrush(_feedback, TextBlock.ForegroundProperty, "Shell.Error");
        footer.Children.Add(_feedback);
        footer.Children.Add(Preview());
        var actions = new WrapPanel { Margin = new Thickness(0, 12, 0, 0), ItemSpacing = 8 };
        var apply = new Button { Content = L.Text("Apply"), IsDefault = true, Classes = { "accent" } };
        apply.Click += (_, _) =>
        {
            try
            {
                _save(Draft);
                _applied = true;
                Accept();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _feedback.Text = L.Text("Could not save preferences: ") + ex.Message; }
        };
        var reset = new Button { Content = L.Text("Reset to defaults") };
        reset.Click += (_, _) => Reset();
        actions.Children.Add(apply);
        actions.Children.Add(Dismiss(L.Text("Cancel")));
        actions.Children.Add(reset);
        footer.Children.Add(actions);
        root.Children.Add(footer);

        var body = new StackPanel();
        body.Children.Add(new TextBlock { Text = L.Text("Appearance and accessibility"), FontSize = 22 });
        body.Children.Add(Caption("Preview changes here and in the workspace. Apply saves them; Cancel restores your previous appearance."));
        Heading(body, "Appearance");
        AddOptions(body, _modes, "Appearance", [("Light", "Light"), ("Dark", "Dark"), ("System", "System")], id => Change(Draft with { Mode = id }));
        _summary.Margin = new Thickness(0, 5, 0, 10);
        BindBrush(_summary, TextBlock.ForegroundProperty, "Shell.TextSecondary");
        body.Children.Add(_summary);
        Heading(body, "Color scheme");
        _schemes.ItemsPanel = WrapItems();
        AutomationProperties.SetName(_schemes, L.Text("Color scheme"));
        foreach (var scheme in ThemePalette.Schemes) _schemes.Items.Add(SchemeItem(scheme));
        _schemes.Items.Add(new ListBoxItem { Tag = "custom", Content = L.Text("Custom"), MinWidth = 124, MinHeight = 54, Padding = new Thickness(10, 7, 10, 7) });
        _schemes.SelectionChanged += (_, _) => { if (!_updating && _schemes.SelectedItem is ListBoxItem item) Change(Draft with { Scheme = (string)item.Tag! }); };
        body.Children.Add(_schemes);
        var colors = new StackPanel();
        colors.Children.Add(Caption("Choose a part of the interface to edit. Light and dark colors are stored separately. Editing a preset creates your Custom scheme."));
        colors.Children.Add(Caption("Customize the currently displayed mode. Switch Light or Dark above to edit the other set."));
        colors.Children.Add(_basicColors);
        colors.Children.Add(new Expander { Header = L.Text("More interface colors"), Content = _extraColors, Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Stretch });
        var restoreColors = new Button { Content = L.Text("Reset these custom colors"), Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        restoreColors.Click += (_, _) => Change(EditDark ? Draft with { DarkColors = [] } : Draft with { LightColors = [] });
        colors.Children.Add(restoreColors);
        body.Children.Add(new Expander { Header = L.Text("Customize colors"), Content = colors, Margin = new Thickness(0, 14, 0, 4), HorizontalAlignment = HorizontalAlignment.Stretch });
        Heading(body, "Accessibility");
        body.Children.Add(Caption("Color and font preferences can help readability. Important states also keep their text or symbols."));
        Label(body, "Color vision");
        AddOptions(body, _vision, "Color vision", [("Standard", "Standard"), ("RedGreen", "Red–green friendly"), ("BlueYellow", "Blue–yellow friendly"), ("Grayscale", "Grayscale")], id => Change(Draft with { Vision = id }));
        body.Children.Add(Caption("Color-vision profiles override accent and status colors, including custom choices."));
        Label(body, "Reading font");
        AddOptions(body, _fonts, "Reading font", [("Default", "Inter"), ("Verdana", "Verdana"), ("OpenDyslexic", "OpenDyslexic")], id => Change(Draft with { Font = id }));
        body.Children.Add(Caption("Choose the font that is easiest for you to read. OpenDyslexic is included; no installation is needed."));
        AddCheck(body, _protect, "Protect text contrast", value => Change(Draft with { ProtectContrast = value, IncreaseContrast = value && Draft.IncreaseContrast }));
        body.Children.Add(Caption("Adjusts foreground colors for readability when needed. Some mixed custom backgrounds may still need manual correction."));
        AddCheck(body, _stronger, "Increase text contrast", value => Change(Draft with { IncreaseContrast = value, ProtectContrast = value || Draft.ProtectContrast }));
        body.Children.Add(Caption("Targets stronger text contrast and may adjust button fills."));
        AddCheck(body, _motion, "Reduce motion", value => Change(Draft with { ReduceMotion = value }));
        body.Children.Add(Caption("Reduced motion replaces the animated progress bar with the existing stage and elapsed-time feedback."));
        body.Children.Add(Caption("Recovery: Escape cancels this preview. Ctrl+R resets this window."));
        root.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        Content = root;
        KeyDown += (_, e) => { if (e.Key == Key.R && e.KeyModifiers == KeyModifiers.Control) { Reset(); e.Handled = true; } };
        Closed += (_, _) => { if (!_applied) AppTheme.Apply(_original); };
        Refresh();
    }

    void Reset() => Change(new AppearanceSettings());

    bool EditDark => ThemePalette.IsDark(Draft, AppTheme.SystemLight);

    void Change(AppearanceSettings value)
    {
        Draft = ThemePalette.Normalize(value);
        AppTheme.Apply(Draft);
        Refresh();
    }

    void Refresh()
    {
        _updating = true;
        SetSelected(_modes, Draft.Mode); SetSelected(_schemes, Draft.Scheme); SetSelected(_vision, Draft.Vision); SetSelected(_fonts, Draft.Font);
        _protect.IsChecked = Draft.ProtectContrast; _stronger.IsChecked = Draft.IncreaseContrast; _motion.IsChecked = Draft.ReduceMotion;
        _summary.Text = Draft.Mode == "System" ? L.Text("Following the desktop: {0}", L.Text(AppTheme.SystemLight ? "Light" : "Dark")) : L.Text("Every color scheme supports Light, Dark and System.");
        _updating = false;
        RefreshColors();
        var palette = ThemePalette.Build(Draft, AppTheme.SystemLight);
        bool low = new[] { "Background", "Surface", "Sidebar", "ControlSurface", "InputSurface", "HoverSurface", "StatusSurface" }
            .Any(k => ThemePalette.Contrast(palette["Text"], palette[k]) < 4.5 || ThemePalette.Contrast(palette["Muted"], palette[k]) < 4.5);
        _feedback.Text = low ? L.Text("Some custom text/background combinations have low contrast. Adjust the colors or reset this scheme.") : "";
    }

    void RefreshColors()
    {
        _basicColors.Children.Clear();
        _extraColors.Children.Clear();
        var colors = ThemePalette.Build(Draft with { Mode = EditDark ? "Dark" : "Light" }, AppTheme.SystemLight);
        foreach (var role in ThemePalette.Roles)
        {
            var row = new Grid { Margin = new Thickness(0, 4, 0, 4), ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            row.Children.Add(new TextBlock { Text = L.Text(role.Label), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) });
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            content.Children.Add(BindBrush(new Border { Background = new SolidColorBrush(colors[role.Key]), Width = 24, Height = 20, BorderThickness = new Thickness(1) }, Border.BorderBrushProperty, "Shell.Border"));
            content.Children.Add(new TextBlock { Text = ThemePalette.Hex(colors[role.Key]), FontFamily = new FontFamily("monospace"), VerticalAlignment = VerticalAlignment.Center });
            var button = new Button { Content = content, Padding = new Thickness(10, 5, 10, 5) };
            AutomationProperties.SetName(button, L.Text("Change color: {0}", L.Text(role.Label)));
            var color = colors[role.Key];
            button.Click += (_, _) => PickColor(role, color);
            Grid.SetColumn(button, 1);
            row.Children.Add(button);
            (role.Advanced ? _extraColors : _basicColors).Children.Add(row);
        }
    }

    void PickColor(ColorRole role, Color color)
    {
        var before = Draft;
        bool dark = EditDark;
        void Update(Color selected)
        {
            bool fromPreset = Draft.Scheme != "custom";
            var changes = new Dictionary<string, string>((dark ? Draft.DarkColors : Draft.LightColors) ?? []);
            if (fromPreset && Draft.CustomBase != Draft.Scheme) changes.Clear();
            changes[role.Key] = ThemePalette.Hex(selected);
            var next = Draft with { Scheme = "custom", CustomBase = fromPreset ? Draft.Scheme : Draft.CustomBase };
            if (fromPreset && Draft.CustomBase != Draft.Scheme) next = next with { LightColors = [], DarkColors = [] };
            Draft = dark ? next with { DarkColors = changes } : next with { LightColors = changes };
            AppTheme.Apply(Draft);
        }
        if (new ColorPickerDialog(this, L.Text(role.Label), color, Update).ShowDialog() != true)
        {
            Draft = before;
            AppTheme.Apply(Draft);
        }
        Refresh();
    }

    static Control Preview()
    {
        var row = new WrapPanel();
        row.Children.Add(new TextBlock { Text = L.Text("Preview"), FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 8) });
        row.Children.Add(new TextBlock { Text = L.Text("Readable text"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 8) });
        row.Children.Add(new Button { Content = L.Text("Sample button"), IsTabStop = false, Classes = { "accent" }, Margin = new Thickness(0, 0, 16, 8) });
        var ok = new TextBlock { Text = "✓ " + L.Text("Ready"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 8) };
        ok.Bind(TextBlock.ForegroundProperty, ok.GetResourceObservable("Shell.Good"));
        row.Children.Add(ok);
        var attention = new TextBlock { Text = "! " + L.Text("Needs attention"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 8) };
        attention.Bind(TextBlock.ForegroundProperty, attention.GetResourceObservable("Shell.Warning"));
        row.Children.Add(attention);
        var border = new Border { Child = row, Padding = new Thickness(12), CornerRadius = new CornerRadius(6) };
        border.Bind(Border.BackgroundProperty, border.GetResourceObservable("Shell.Panel"));
        return border;
    }

    void AddOptions(Panel parent, ListBox list, string name, (string Id, string Label)[] options, Action<string> changed)
    {
        list.ItemsPanel = WrapItems();
        list.Margin = new Thickness(0, 4, 0, 8);
        list.HorizontalAlignment = HorizontalAlignment.Left;
        AutomationProperties.SetName(list, L.Text(name));
        foreach (var option in options) list.Items.Add(new ListBoxItem { Content = L.Text(option.Label), Tag = option.Id, Padding = new Thickness(13, 8, 13, 8) });
        list.SelectionChanged += (_, _) => { if (!_updating && list.SelectedItem is ListBoxItem item) changed((string)item.Tag!); };
        parent.Children.Add(list);
    }

    void AddCheck(Panel parent, CheckBox check, string text, Action<bool> changed)
    {
        check.Content = L.Text(text);
        check.IsCheckedChanged += (_, _) => { if (!_updating) changed(check.IsChecked == true); };
        parent.Children.Add(check);
    }

    static void SetSelected(ListBox list, string id) => list.SelectedItem = list.Items.OfType<ListBoxItem>().FirstOrDefault(i => (string)i.Tag! == id);
    static FuncTemplate<Panel?> WrapItems() => new(() => new WrapPanel());
    internal static T BindBrush<T>(T control, AvaloniaProperty property, string key) where T : Control { control.Bind(property, control.GetResourceObservable(key)); return control; }
    static TextBlock Caption(string text) => BindBrush(new TextBlock { Text = L.Text(text), Margin = new Thickness(0, 7, 0, 10), FontSize = 12, TextWrapping = TextWrapping.Wrap }, TextBlock.ForegroundProperty, "Shell.TextSecondary");
    static void Heading(Panel parent, string text) => parent.Children.Add(new TextBlock { Text = L.Text(text), FontWeight = FontWeight.SemiBold, FontSize = 18, Margin = new Thickness(0, 16, 0, 8) });
    static void Label(Panel parent, string text) => parent.Children.Add(new TextBlock { Text = L.Text(text), FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 12, 0, 2) });

    static ListBoxItem SchemeItem(ColorScheme scheme)
    {
        var panel = new StackPanel();
        var colors = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 5) };
        var hue = ThemePalette.Parse(scheme.Hex);
        foreach (var color in new[] { hue, ThemePalette.Mix(hue, Colors.White, .8), ThemePalette.Mix(hue, Colors.Black, .75) })
            colors.Children.Add(new Border { Width = 23, Height = 12, Background = new SolidColorBrush(color), Margin = new Thickness(0, 0, 3, 0), CornerRadius = new CornerRadius(2) });
        panel.Children.Add(colors);
        panel.Children.Add(new TextBlock { Text = L.Text(scheme.Name) });
        var item = new ListBoxItem { Tag = scheme.Id, Content = panel, MinWidth = 124, Padding = new Thickness(10, 7, 10, 7) };
        AutomationProperties.SetName(item, L.Text(scheme.Name));
        return item;
    }
}

/// <summary>One colour of the appearance: typed as #RRGGBB or set with its red, green and blue sliders, previewed in the
/// workspace as it changes. OK keeps it; Cancel or Escape gives back the one before.</summary>
sealed class ColorPickerDialog : FormWindow
{
    readonly Slider _red = Channel(), _green = Channel(), _blue = Channel();
    readonly TextBox _hex = new() { Width = 110, FontFamily = new FontFamily("monospace") };
    readonly Border _swatch = new() { Width = 64, Height = 40, CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1) };
    readonly Action<Color> _changed;
    bool _updating;

    public ColorPickerDialog(Window owner, string title, Color color, Action<Color> changed)
    {
        _changed = changed;
        DialogLayout.Configure(this, owner, title, 460);
        AppearanceDialog.BindBrush(_swatch, Border.BorderBrushProperty, "Shell.Border");
        var root = new StackPanel { Margin = new Thickness(20), Spacing = 10 };
        var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        top.Children.Add(_swatch);
        var hexRow = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        hexRow.Children.Add(new TextBlock { Text = L.Text("Color (#RRGGBB)") });
        hexRow.Children.Add(_hex);
        top.Children.Add(hexRow);
        root.Children.Add(top);
        foreach (var (slider, name) in new[] { (_red, "Red"), (_green, "Green"), (_blue, "Blue") })
        {
            var row = new DockPanel();
            var label = new TextBlock { Text = L.Text(name), Width = 70, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(label, Dock.Left);
            row.Children.Add(label);
            AutomationProperties.SetName(slider, L.Text(name));
            slider.ValueChanged += (_, _) => FromSliders();
            row.Children.Add(slider);
            root.Children.Add(row);
        }
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        var ok = new Button { Content = L.Text("OK"), IsDefault = true, Classes = { "accent" } };
        ok.Click += (_, _) => Accept();
        buttons.Children.Add(ok);
        buttons.Children.Add(Dismiss(L.Text("Cancel")));
        root.Children.Add(buttons);
        Content = root;
        _hex.TextChanged += (_, _) =>
        {
            if (_updating || !ThemePalette.TryColor(_hex.Text?.Trim(), out var typed)) return;
            Show(typed, fromHex: true);
        };
        Show(color, fromHex: false);
    }

    static Slider Channel() => new() { Minimum = 0, Maximum = 255, IsSnapToTickEnabled = true, TickFrequency = 1, SmallChange = 1, LargeChange = 16 };

    void FromSliders()
    {
        if (_updating) return;
        Show(Color.FromRgb((byte)_red.Value, (byte)_green.Value, (byte)_blue.Value), fromHex: false);
    }

    void Show(Color color, bool fromHex)
    {
        _updating = true;
        _red.Value = color.R; _green.Value = color.G; _blue.Value = color.B;
        if (!fromHex) _hex.Text = ThemePalette.Hex(color);
        _swatch.Background = new SolidColorBrush(color);
        _updating = false;
        _changed(color);
    }
}
