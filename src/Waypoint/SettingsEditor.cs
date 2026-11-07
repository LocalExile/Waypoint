using System.Globalization;
using System.Text.Json.Nodes;
#if WINDOWS
using System.Windows;
using System.Windows.Controls;
using Element = System.Windows.UIElement;
#else
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Element = Avalonia.Controls.Control;
#endif

namespace Waypoint;

/// <summary>The same declaration-driven editor for mods, modules and leagues on both front ends.</summary>
sealed class SettingsEditor : StackPanel
{
    readonly ModSettingsForm _form;
    readonly Dictionary<string, Func<JsonNode?>> _read = new(StringComparer.Ordinal);
    readonly List<Action> _conditions = [];
    readonly CheckBox _advanced = new() { Content = L.Text("Show advanced settings"), Margin = new Thickness(0, 0, 0, 16) };
    bool _building = true;
    public bool Dirty { get; private set; }
    public event Action? Edited;

    public SettingsEditor(ModSettingsForm form)
    {
        _form = form;
        bool HasAdvanced(IEnumerable<ModSetting> settings) => settings.Any(s => s.Advanced || HasAdvanced(s.Children ?? [])
            || s.Item is { } item && HasAdvanced([item]) || s.Value is { } value && HasAdvanced([value]));
        if (HasAdvanced(form.Settings)) Children.Add(_advanced);
        CheckChanged(_advanced, UpdateConditions);
        BuildGroup(this, form.Settings, s => form.Current(s), _read);
        _building = false;
        UpdateConditions();
    }
    void Change()
    {
        if (_building) return;
        Dirty = true;
        UpdateConditions();
        Edited?.Invoke();
    }
    void UpdateConditions() { foreach (var update in _conditions) update(); }
    public JsonObject Values(bool defaults = false)
    {
        var edited = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        foreach (var setting in _form.Settings)
        {
            var value = defaults ? setting.Default?.DeepClone() : _read[setting.Name]();
            if (ModSettingsForm.Validate(setting, value) is { } error) throw new InvalidDataException(error);
            edited[setting.Name] = value;
        }
        return _form.Values(edited);
    }
    void BuildGroup(StackPanel panel, IReadOnlyList<ModSetting> settings, Func<ModSetting, JsonNode?> current, Dictionary<string, Func<JsonNode?>> readers)
    {
        foreach (var setting in settings)
        {
            var holder = new StackPanel { Margin = new Thickness(0, 0, 0, 18) };
            readers[setting.Name] = Field(holder, setting, current(setting));
            panel.Children.Add(holder);
            _conditions.Add(() =>
            {
                bool visible = !setting.Advanced || _advanced.IsChecked == true;
                if (setting.When is { } when)
                    foreach (var (name, expected) in when)
                    {
                        try { visible &= readers.TryGetValue(name, out var read) && ModSettingsForm.Equal(read(), expected); }
                        catch (InvalidDataException) { visible = false; }
                    }
                Show(holder, visible);
            });
        }
    }
    Func<JsonNode?> Field(StackPanel panel, ModSetting setting, JsonNode? value)
    {
        if (setting.Type is "group" or "list" or "map" or "choices" && (setting.Type != "choices" || setting.Options.Count == 0))
        {
            var inner = new StackPanel { Margin = new Thickness(12, 12, 0, 0) };
            var expander = new Expander { Header = setting.Title, Content = inner, IsExpanded = setting.Type == "group" && !setting.Advanced };
            panel.Children.Add(expander);
            Description(inner, setting);
            if (setting.Type == "group")
            {
                var readers = new Dictionary<string, Func<JsonNode?>>(StringComparer.Ordinal);
                var values = value as JsonObject;
                BuildGroup(inner, setting.Children ?? [], s => ModSettingsForm.Merge(s, values?.FirstOrDefault(p => p.Key.Equals(s.Name, StringComparison.OrdinalIgnoreCase)).Value), readers);
                return () =>
                {
                    var result = values?.DeepClone() as JsonObject ?? new JsonObject();
                    foreach (var (name, read) in readers) result[name] = read()?.DeepClone();
                    return result;
                };
            }
            return Collection(inner, setting, value);
        }
        if (setting.Type != "bool") panel.Children.Add(Label(Heading(setting)));
        Func<JsonNode?> read;
        switch (setting.Type)
        {
            case "bool":
                var toggle = new CheckBox { Content = setting.Title, IsChecked = value is JsonValue flag && flag.TryGetValue(out bool b) && b };
                panel.Children.Add(toggle); CheckChanged(toggle, Change);
                read = () => JsonValue.Create(toggle.IsChecked == true); break;
            case "integer" or "number": read = Number(panel, setting, value); break;
            case "range":
                var range = value as JsonArray;
                var component = setting with { Type = setting.Integers ? "integer" : "number", Default = null };
                panel.Children.Add(Label(L.Text("Minimum")));
                var low = Number(panel, component, range?.ElementAtOrDefault(0));
                panel.Children.Add(Label(L.Text("Maximum")));
                var high = Number(panel, component, range?.ElementAtOrDefault(1));
                read = () => new JsonArray(low(), high()); break;
            case "choice" when setting.Options.Count > 0:
                var choice = new ComboBox { MinWidth = 120, HorizontalAlignment = HorizontalAlignment.Stretch };
                foreach (var option in setting.Options) choice.Items.Add(new ComboBoxItem { Content = option.Label, Tag = option.Value });
                choice.SelectedItem = choice.Items.OfType<ComboBoxItem>().FirstOrDefault(i => ModSettingsForm.Equal((JsonNode?)i.Tag, value));
                choice.SelectionChanged += (_, _) => Change(); panel.Children.Add(choice);
                read = () => (choice.SelectedItem as ComboBoxItem)?.Tag is JsonNode chosen ? chosen.DeepClone() : null; break;
            case "choices":
                var selected = value as JsonArray ?? [];
                var boxes = setting.Options.Select(o => (o.Value, Box: new CheckBox { Content = o.Label, IsChecked = selected.Any(v => ModSettingsForm.Equal(v, o.Value)), Margin = new Thickness(0, 3, 0, 3) })).ToArray();
                foreach (var (_, box) in boxes) { panel.Children.Add(box); CheckChanged(box, Change); }
                read = () => new JsonArray(boxes.Where(b => b.Box.IsChecked == true).Select(b => b.Value.DeepClone()).ToArray()); break;
            default:
                var text = new TextBox { Text = ModSettingsForm.Show(value), MinWidth = 100 };
                text.TextChanged += (_, _) => Change(); panel.Children.Add(text);
                read = () => JsonValue.Create(text.Text ?? ""); break;
        }
        Description(panel, setting);
        return read;
    }
    Func<JsonNode?> Number(StackPanel panel, ModSetting setting, JsonNode? value)
    {
        var text = new TextBox { Text = ModSettingsForm.Show(value, setting), MinWidth = 100, Margin = new Thickness(0, 3, 0, 6) };
        string initialText = text.Text ?? "";
        panel.Children.Add(text);
        Slider? slider = null;
        bool updating = false;
        double scale = setting.DisplayScale;
        if (setting.Min is { } min && setting.Max is { } max && min < max && double.IsFinite(min) && double.IsFinite(max))
        {
            slider = new Slider { Minimum = min * scale, Maximum = max * scale, Margin = new Thickness(0, 0, 0, 6) };
#if WINDOWS
            slider.IsSnapToTickEnabled = setting.Step is not null || setting.Type == "integer";
            slider.TickFrequency = (setting.Step ?? 1) * scale;
#endif
            slider.Value = double.TryParse(text.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var initial) ? Math.Clamp(initial, min * scale, max * scale) : min * scale;
            var local = slider;
            slider.ValueChanged += (_, _) =>
            {
                if (updating) return;
                double step = setting.Step ?? (setting.Type == "integer" ? 1 : 0);
                double n = local.Value / scale;
                if (step > 0) n = min + Math.Round((n - min) / step) * step;
                updating = true; text.Text = (Math.Clamp(n, min, max) * scale).ToString("G", CultureInfo.InvariantCulture); updating = false;
                Change();
            };
            panel.Children.Add(slider);
        }
        text.TextChanged += (_, _) =>
        {
            if (updating) return;
            if (slider is not null && double.TryParse(text.Text?.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && double.IsFinite(n))
            { updating = true; slider.Value = Math.Clamp(n, slider.Minimum, slider.Maximum); updating = false; }
            Change();
        };
        return () =>
        {
            if (text.Text == initialText) return value?.DeepClone();
            var parsed = ModSettingsForm.Read(setting, text.Text, out string? error);
            if (error is not null) throw new InvalidDataException(error);
            return parsed;
        };
    }
    Func<JsonNode?> Collection(StackPanel panel, ModSetting setting, JsonNode? value)
    {
        var rows = new StackPanel(); panel.Children.Add(rows);
        var entries = new List<(StackPanel Row, Func<JsonNode?> Read, Func<string>? Key)>();
        var item = setting.Type == "choices" ? setting with { Type = "choice" } : setting.Type == "map" ? setting.Value! : setting.Item!;
        if (setting.Type != "choices") item = item with { Name = setting.Type == "map" ? L.Text("Value") : L.Text("Entry") };
        void Add(JsonNode? current, string key = "")
        {
            var row = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
            Func<string>? readKey = null;
            if (setting.Type == "map")
            {
                row.Children.Add(Label(L.Text("Key")));
                if (setting.Options.Count > 0)
                {
                    var keys = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
                    foreach (var option in setting.Options) keys.Items.Add(new ComboBoxItem { Content = option.Label, Tag = ModSettingsForm.Show(option.Value) });
                    keys.SelectedItem = keys.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string?)i.Tag == key);
                    row.Children.Add(keys); keys.SelectionChanged += (_, _) => Change(); readKey = () => (keys.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
                }
                else { var keyBox = new TextBox { Text = key }; row.Children.Add(keyBox); keyBox.TextChanged += (_, _) => Change(); readKey = () => keyBox.Text ?? ""; }
            }
            var read = Field(row, item, current);
            var remove = Button(L.Text("Remove"), () => { rows.Children.Remove(row); entries.RemoveAll(e => e.Row == row); Change(); });
            row.Children.Add(remove); entries.Add((row, read, readKey)); rows.Children.Add(row);
        }
        if (value is JsonArray list) foreach (var n in list) Add(n);
        if (value is JsonObject map) foreach (var (k, n) in map) Add(n, k);
        panel.Children.Add(Button(L.Text("Add entry"), () =>
        {
            if (entries.Count >= (setting.MaxItems ?? 1000)) return;
            Add(Initial(item)); Change();
        }));
        return () =>
        {
            if (setting.Type != "map") return new JsonArray(entries.Select(e => e.Read()?.DeepClone()).ToArray());
            var map = new JsonObject();
            foreach (var entry in entries)
            {
                string key = entry.Key!();
                if (key.Length == 0 || map.ContainsKey(key)) throw new InvalidDataException(L.Text("Every map entry needs a unique, non-empty key."));
                map[key] = entry.Read()?.DeepClone();
            }
            return map;
        };
    }
    static JsonNode? Initial(ModSetting setting) => setting.Default?.DeepClone() ?? setting.Type switch
    {
        "bool" => JsonValue.Create(false), "integer" or "number" => JsonValue.Create(setting.Min ?? 0),
        "range" => new JsonArray(JsonValue.Create(setting.Min ?? 0), JsonValue.Create(setting.Min ?? 0)),
        "list" or "choices" => new JsonArray(), "map" or "group" => new JsonObject(),
        "choice" when setting.Options.Count > 0 => setting.Options[0].Value.DeepClone(), _ => JsonValue.Create(""),
    };
    static string Heading(ModSetting s) => s.Title + (s.Format == "percent" ? " (%)" : s.Format == "duration" ? " (" + L.Text("seconds") + ")" : s.Format == "multiplier" ? " (×)" : s.Unit.Length > 0 ? " (" + s.Unit + ")" : "");
    static void Description(StackPanel panel, ModSetting s)
    {
        if (s.Description.Length > 0) panel.Children.Add(Note(s.Description));
        if (s.Source.Length > 0) panel.Children.Add(Note(L.Text("Enter a game value of type: {0}", s.Source)));
        if (s.Applies == "restart") panel.Children.Add(Note(L.Text("Applies when the Gateway restarts.")));
        if (s.Applies == "nextArea") panel.Children.Add(Note(L.Text("Applies in the next area.")));
    }
    public static TextBlock Label(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
    public static TextBlock Note(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 4, 0, 8) };
    public static Button Button(string text, Action action)
    { var button = new Button { Content = text, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 8, 4), Padding = new Thickness(12, 6, 12, 6) }; button.Click += (_, _) => action(); return button; }
    static void CheckChanged(CheckBox box, Action action)
    {
#if WINDOWS
        box.Checked += (_, _) => action(); box.Unchecked += (_, _) => action();
#else
        box.IsCheckedChanged += (_, _) => action();
#endif
    }
    public static void Show(Element element, bool visible)
    {
#if WINDOWS
        element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
#else
        element.IsVisible = visible;
#endif
    }
}
