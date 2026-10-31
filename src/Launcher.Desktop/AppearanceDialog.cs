using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Launcher.Core;

namespace Launcher.Desktop;

public sealed class AppearanceDialog : DialogWindow
{
    readonly App app;
    readonly AppearanceSettings original;
    readonly Action<AppearanceSettings> save;
    readonly ListBox modes = new(), schemes = new(), vision = new(), fonts = new(), sizes = new();
    readonly StackPanel basicColors = new(), extraColors = new();
    readonly CheckBox protect = new(), stronger = new(), focus = new(), motion = new();
    readonly TextBlock feedback = new(), summary = new();
    bool updating;
    public AppearanceSettings Draft { get; private set; }
    public AppearanceDialog(Window owner, AppearanceSettings? initial, Action<AppearanceSettings> save)
    {
        app = (App)Application.Current; original = ThemePalette.Normalize(initial); Draft = original; this.save = save;
        DialogLayout.Configure(this, owner, L.Text("Appearance and accessibility"), 880);
        MinWidth = 640; MinHeight = 540; Height = Math.Min(820, SystemParameters.WorkArea.Height - 40); MaxHeight = SystemParameters.WorkArea.Height - 20; SizeToContent = SizeToContent.Manual;
        var root = new DockPanel { Margin = new Thickness(20) };
        var footer = new StackPanel { Margin = new Thickness(0, 12, 0, 0) }; DockPanel.SetDock(footer, Dock.Bottom);
        feedback.SetResourceReference(TextBlock.ForegroundProperty, "Error"); footer.Children.Add(feedback);
        footer.Children.Add(Preview());
        var actions = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        var apply = new Button { Content = L.Text("Apply"), IsDefault = true }; apply.SetResourceReference(StyleProperty, "Primary");
        apply.Click += (_, _) => { try { save(Draft); DialogResult = true; } catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { feedback.Text = L.Text("Could not save preferences: ") + ex.Message; } };
        var reset = new Button { Content = L.Text("Reset to defaults") }; reset.Click += (_, _) => Reset();
        actions.Children.Add(apply); actions.Children.Add(new Button { Content = L.Text("Cancel"), IsCancel = true }); actions.Children.Add(reset); footer.Children.Add(actions); root.Children.Add(footer);
        var body = new StackPanel();
        var title = new TextBlock { Text = L.Text("Appearance and accessibility") }; title.SetResourceReference(TextBlock.FontSizeProperty, "Font22"); body.Children.Add(title);
        body.Children.Add(Caption("Preview changes here and in the workspace. Apply saves them; Cancel restores your previous appearance."));
        Heading(body, "Appearance");
        AddOptions(body, modes, "Appearance", [("Light", "Light"), ("Dark", "Dark"), ("System", "System")], id => Change(Draft with { Mode = id }));
        summary.Margin = new Thickness(0, 5, 0, 10); summary.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); body.Children.Add(summary);
        Heading(body, "Color scheme");
        schemes.ItemsPanel = WrapItems(); schemes.HorizontalContentAlignment = HorizontalAlignment.Stretch; schemes.SelectionMode = SelectionMode.Single;
        AutomationProperties.SetName(schemes, L.Text("Color scheme"));
        foreach (var scheme in ThemePalette.Schemes) schemes.Items.Add(SchemeItem(scheme));
        schemes.Items.Add(new ListBoxItem { Tag = "custom", Content = L.Text("Custom"), MinWidth = 124, MinHeight = 54, Padding = new Thickness(10, 7, 10, 7) });
        schemes.SelectionChanged += (_, _) => { if (!updating && schemes.SelectedItem is ListBoxItem item) Change(Draft with { Scheme = (string)item.Tag }); }; body.Children.Add(schemes);
        var colors = new StackPanel();
        colors.Children.Add(Caption("Choose a part of the interface to edit. Light and dark colors are stored separately. Editing a preset creates your Custom scheme."));
        colors.Children.Add(Caption("Customize the currently displayed mode. Switch Light or Dark above to edit the other set."));
        colors.Children.Add(basicColors);
        colors.Children.Add(new Expander { Header = L.Text("More interface colors"), Content = extraColors, Margin = new Thickness(0, 8, 0, 0) });
        var restoreColors = new Button { Content = L.Text("Reset these custom colors"), Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        restoreColors.Click += (_, _) => { bool dark = EditDark; Change(dark ? Draft with { DarkColors = [] } : Draft with { LightColors = [] }); }; colors.Children.Add(restoreColors);
        body.Children.Add(new Expander { Header = L.Text("Customize colors"), Content = colors, Margin = new Thickness(0, 14, 0, 4) });
        Heading(body, "Accessibility");
        body.Children.Add(Caption("Color and font preferences can help readability. Important states also keep their text or symbols."));
        Label(body, "Color vision");
        AddOptions(body, vision, "Color vision", [("Standard", "Standard"), ("RedGreen", "Red–green friendly"), ("BlueYellow", "Blue–yellow friendly"), ("Grayscale", "Grayscale")], id => Change(Draft with { Vision = id }));
        body.Children.Add(Caption("Color-vision profiles override accent and status colors, including custom choices. Windows High Contrast takes priority over all palettes."));
        Label(body, "Reading font");
        AddOptions(body, fonts, "Reading font", [("Default", "Segoe UI"), ("Verdana", "Verdana"), ("OpenDyslexic", "OpenDyslexic")], id => Change(Draft with { Font = id }));
        body.Children.Add(Caption("Choose the font that is easiest for you to read. OpenDyslexic is included; no installation is needed. Unsupported characters use Windows font fallback."));
        Label(body, "Text size");
        AddOptions(body, sizes, "Text size", [("100", "100%"), ("110", "110%"), ("125", "125%"), ("150", "150%")], id => Change(Draft with { TextScale = int.Parse(id, System.Globalization.CultureInfo.InvariantCulture) }));
        AddCheck(body, protect, "Protect text contrast", value => Change(Draft with { ProtectContrast = value, IncreaseContrast = value && Draft.IncreaseContrast }));
        body.Children.Add(Caption("Adjusts foreground colors for readability when needed. Some mixed custom backgrounds may still need manual correction."));
        AddCheck(body, stronger, "Increase text contrast", value => Change(Draft with { IncreaseContrast = value, ProtectContrast = value || Draft.ProtectContrast }));
        body.Children.Add(Caption("Targets stronger text contrast and may adjust button fills."));
        AddCheck(body, focus, "Stronger keyboard focus borders", value => Change(Draft with { StrongFocus = value }));
        AddCheck(body, motion, "Reduce motion", value => Change(Draft with { ReduceMotion = value }));
        body.Children.Add(Caption("Reduced motion replaces the animated progress bar with the existing stage and elapsed-time feedback. Windows animation preferences are respected automatically."));
        body.Children.Add(Caption("Recovery: Escape cancels this preview. Ctrl+R resets this window. In the workspace, Ctrl+Shift+R restores default appearance without changing your checkout or saves."));
        root.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }); Content = root;
        PreviewKeyDown += (_, e) => { if (e.Key == Key.R && Keyboard.Modifiers == ModifierKeys.Control) { Reset(); e.Handled = true; } };
        Closed += (_, _) => { if (DialogResult != true) app.ApplyAppearance(original); };
        Refresh();
    }
    public void Reset() => Change(new AppearanceSettings());
    public void Preview(AppearanceSettings value) => Change(value);
    bool EditDark => ThemePalette.IsDark(Draft, app.SystemLight);
    void Change(AppearanceSettings value) { Draft = ThemePalette.Normalize(value); app.ApplyAppearance(Draft); Refresh(); }
    void Refresh()
    {
        updating = true;
        SetSelected(modes, Draft.Mode); SetSelected(schemes, Draft.Scheme); SetSelected(vision, Draft.Vision); SetSelected(fonts, Draft.Font); SetSelected(sizes, Draft.TextScale.ToString(System.Globalization.CultureInfo.InvariantCulture));
        protect.IsChecked = Draft.ProtectContrast; stronger.IsChecked = Draft.IncreaseContrast; focus.IsChecked = Draft.StrongFocus; motion.IsChecked = Draft.ReduceMotion;
        summary.Text = Draft.Mode == "System" ? L.Text("Following Windows: {0}", L.Text(app.SystemLight ? "Light" : "Dark")) : L.Text("Every color scheme supports Light, Dark and System.");
        updating = false; RefreshColors();
        var palette = ThemePalette.Build(Draft, app.SystemLight);
        bool low = new[] { "Background", "Surface", "Sidebar", "ControlSurface", "InputSurface", "HoverSurface", "StatusSurface" }.Any(k => ThemePalette.Contrast(palette["Text"], palette[k]) < 4.5 || ThemePalette.Contrast(palette["Muted"], palette[k]) < 4.5);
        feedback.Text = low ? L.Text("Some custom text/background combinations have low contrast. Adjust the colors or reset this scheme.") : "";
    }
    void RefreshColors()
    {
        if (updating) return; basicColors.Children.Clear(); extraColors.Children.Clear();
        var colors = ThemePalette.Build(Draft with { Mode = EditDark ? "Dark" : "Light" }, app.SystemLight);
        foreach (var role in ThemePalette.Roles)
        {
            var row = new Grid { Margin = new Thickness(0, 4, 0, 4) }; row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            row.Children.Add(new TextBlock { Text = L.Text(role.Label), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) });
            var content = new StackPanel { Orientation = Orientation.Horizontal }; content.Children.Add(new Border { Background = new SolidColorBrush(colors[role.Key]), Width = 24, Height = 20, BorderThickness = new Thickness(1), BorderBrush = Brushes.Gray, Margin = new Thickness(0, 0, 8, 0) }); content.Children.Add(new TextBlock { Text = ThemePalette.Hex(colors[role.Key]), FontFamily = new FontFamily("Consolas") });
            var button = new Button { Content = content, Margin = new Thickness(0), Padding = new Thickness(10, 5, 10, 5) }; AutomationProperties.SetName(button, L.Text("Change color: {0}", L.Text(role.Label)));
            button.Click += (_, _) => PickColor(role, colors[role.Key]); Grid.SetColumn(button, 1); row.Children.Add(button); (role.Advanced ? extraColors : basicColors).Children.Add(row);
        }
    }
    void PickColor(ColorRole role, Color color)
    {
        var before = Draft; bool dark = EditDark;
        void Update(Color selected)
        {
            bool fromPreset = Draft.Scheme != "custom";
            var changes = new Dictionary<string, string>((dark ? Draft.DarkColors : Draft.LightColors) ?? []);
            if (fromPreset && Draft.CustomBase != Draft.Scheme) changes.Clear();
            changes[role.Key] = ThemePalette.Hex(selected);
            var next = Draft with { Scheme = "custom", CustomBase = fromPreset ? Draft.Scheme : Draft.CustomBase };
            if (fromPreset && Draft.CustomBase != Draft.Scheme) next = next with { LightColors = [], DarkColors = [] };
            Draft = dark ? next with { DarkColors = changes } : next with { LightColors = changes }; app.ApplyAppearance(Draft);
        }
        var picker = new ColorPickerDialog(this, L.Text(role.Label), color, Update);
        if (picker.ShowDialog() != true) { Draft = before; app.ApplyAppearance(Draft); }
        Refresh();
    }
    FrameworkElement Preview()
    {
        var row = new WrapPanel(); row.Children.Add(new TextBlock { Text = L.Text("Preview"), FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 8) });
        row.Children.Add(new TextBlock { Text = L.Text("Readable text"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 8) });
        var sample = new Button { Content = L.Text("Sample button"), IsTabStop = false }; sample.SetResourceReference(StyleProperty, "Primary"); row.Children.Add(sample);
        var ok = new TextBlock { Text = "✓ " + L.Text("Ready"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 8) }; ok.SetResourceReference(TextBlock.ForegroundProperty, "Success"); row.Children.Add(ok);
        var attention = new TextBlock { Text = "! " + L.Text("Needs attention"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 8) }; attention.SetResourceReference(TextBlock.ForegroundProperty, "Warning"); row.Children.Add(attention);
        var border = new Border { Child = row, Padding = new Thickness(12), CornerRadius = new CornerRadius(6) }; border.SetResourceReference(Border.BackgroundProperty, "Surface"); return border;
    }
    void AddOptions(Panel parent, ListBox list, string name, (string Id, string Label)[] options, Action<string> changed)
    {
        list.ItemsPanel = WrapItems(); list.Margin = new Thickness(0, 4, 0, 8); list.HorizontalAlignment = HorizontalAlignment.Left; list.SetValue(KeyboardNavigation.TabNavigationProperty, KeyboardNavigationMode.Once);
        AutomationProperties.SetName(list, L.Text(name));
        foreach (var option in options) list.Items.Add(new ListBoxItem { Content = L.Text(option.Label), Tag = option.Id, Padding = new Thickness(13, 8, 13, 8) });
        list.SelectionChanged += (_, _) => { if (!updating && list.SelectedItem is ListBoxItem item) changed((string)item.Tag); }; parent.Children.Add(list);
    }
    void AddCheck(Panel parent, CheckBox check, string text, Action<bool> changed)
    { check.Content = L.Text(text); check.Checked += (_, _) => { if (!updating) changed(true); }; check.Unchecked += (_, _) => { if (!updating) changed(false); }; parent.Children.Add(check); }
    static void SetSelected(ListBox list, string id) => list.SelectedItem = list.Items.OfType<ListBoxItem>().FirstOrDefault(i => (string)i.Tag == id);
    static ItemsPanelTemplate WrapItems() => new(new FrameworkElementFactory(typeof(WrapPanel)));
    static TextBlock Caption(string text) { var item = new TextBlock { Text = L.Text(text), Margin = new Thickness(0, 7, 0, 10) }; item.SetResourceReference(StyleProperty, "Caption"); return item; }
    static void Heading(Panel parent, string text)
    { var heading = new TextBlock { Text = L.Text(text), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 16, 0, 8) }; heading.SetResourceReference(TextBlock.FontSizeProperty, "Font18"); parent.Children.Add(heading); }
    static void Label(Panel parent, string text) => parent.Children.Add(new TextBlock { Text = L.Text(text), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 2) });
    static ListBoxItem SchemeItem(ColorScheme scheme)
    {
        var panel = new StackPanel(); var colors = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 5) };
        foreach (string color in new[] { scheme.Hex, ThemePalette.Hex(ThemePalette.Mix(ThemePalette.Parse(scheme.Hex), Colors.White, .8)), ThemePalette.Hex(ThemePalette.Mix(ThemePalette.Parse(scheme.Hex), Colors.Black, .75)) }) colors.Children.Add(new Border { Width = 23, Height = 12, Background = new SolidColorBrush(ThemePalette.Parse(color)), Margin = new Thickness(0, 0, 3, 0), CornerRadius = new CornerRadius(2) });
        panel.Children.Add(colors); panel.Children.Add(new TextBlock { Text = L.Text(scheme.Name) });
        var item = new ListBoxItem { Tag = scheme.Id, Content = panel, MinWidth = 124, Padding = new Thickness(10, 7, 10, 7) }; AutomationProperties.SetName(item, L.Text(scheme.Name)); return item;
    }
}

public partial class MainWindow
{
    static readonly RoutedUICommand ResetAppearanceCommand = new("Reset appearance", "ResetAppearance", typeof(MainWindow));
    void InitializeAppearance()
    {
        ((App)Application.Current).ApplyAppearance(settings.Appearance); UpdateAppearanceSummary();
        CommandBindings.Add(new CommandBinding(ResetAppearanceCommand, (_, _) => ResetAppearance()));
        InputBindings.Add(new KeyBinding(ResetAppearanceCommand, new KeyGesture(Key.R, ModifierKeys.Control | ModifierKeys.Shift)));
    }
    void UpdateAppearanceSummary()
    {
        if (AppearanceSummary is null) return;
        var value = ThemePalette.Normalize(settings.Appearance);
        string scheme = value.Scheme == "custom" ? L.Text("Custom") : L.Text(ThemePalette.Schemes.First(s => s.Id == value.Scheme).Name);
        AppearanceSummary.Text = L.Text("{0} · {1} · {2}% text", scheme, L.Text(value.Mode), value.TextScale);
    }
    void Appearance_Click(object sender, RoutedEventArgs e) => new AppearanceDialog(this, settings.Appearance, SaveAppearance).ShowDialog();
    void SaveAppearance(AppearanceSettings value)
    {
        var next = settings with { Appearance = ThemePalette.Normalize(value) }; SettingsStore.Save(next); settings = next;
        ((App)Application.Current).ApplyAppearance(settings.Appearance); UpdateAppearanceSummary(); StatusLabel.Text = L.Text("Appearance saved.");
    }
    void ResetAppearance()
    {
        try { SaveAppearance(new()); SetFeedback(); AnnounceStatus(); }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        { StatusLabel.Text = L.Text("Could not save preferences: ") + ex.Message; SetFeedback(true); }
    }
}
