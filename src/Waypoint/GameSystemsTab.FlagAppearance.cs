#if WINDOWS
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
#else
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
#endif
using System.Security.Cryptography;
using System.Text;

namespace Waypoint;

public sealed partial class GameSystemsTab
{
    static string FlagScope => OperatingSystem.IsWindows() ? Environment.CurrentDirectory.ToUpperInvariant() : Environment.CurrentDirectory;
    static string FlagInk(string name, bool hardcore) => "Waypoint.Flag." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(FlagScope + "\n" + name.ToUpperInvariant() + hardcore)));
    string StoredFlagColor(string name) => (_shared.Settings.LeagueFlagColors ??= []).TryGetValue(FlagScope, out var flags)
        && flags is not null && flags.TryGetValue(name.ToUpperInvariant(), out var value) ? LeagueFlagColor.Normalize(value) : "auto";
    static uint ThemeRgb(string role)
    {
#if WINDOWS
        var color = ((SolidColorBrush)Theme.Brush(role)).Color;
#else
        var color = ((SolidColorBrush)App.Brush(role)).Color;
#endif
        return (uint)(color.R << 16 | color.G << 8 | color.B);
    }
    void RefreshFlagColors()
    {
        if (_catalog is null) return;
        foreach (var card in _catalog.Cards) { SetFlagColor(card.Name, false, StoredFlagColor(card.Name)); SetFlagColor(card.Name, true, StoredFlagColor(card.Name)); }
    }
    static void SetFlagColor(string name, bool hardcore, string choice)
    {
        uint rgb = LeagueFlagColor.TryHex(choice, out var custom) ? custom : ThemeRgb(LeagueFlagColor.Role(choice, hardcore));
#if WINDOWS
        if (SystemParameters.HighContrast) rgb = ThemeRgb("Shell.Text");
        else rgb = LeagueFlagColor.Readable(rgb, [ThemeRgb("Shell.Background"), ThemeRgb("Shell.Panel"), ThemeRgb("Shell.Banner")], Theme.Current.Vision == "Grayscale");
        Application.Current.Resources[FlagInk(name, hardcore)] = new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
#else
        rgb = LeagueFlagColor.Readable(rgb, [ThemeRgb("Shell.Background"), ThemeRgb("Shell.Panel"), ThemeRgb("Shell.Banner")]);
        App.Instance.Resources[FlagInk(name, hardcore)] = new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
#endif
    }
    void AddFlagAppearance(StackPanel panel, GameLeagueCard card)
    {
        panel.Children.Add(Type(L.Text("Flag appearance in Waypoint"), 16, true));
        var note = Type(L.Text("Changes apply immediately to this flag's frame in Waypoint only. The native game artwork stays unchanged."), 12, false, "Shell.TextSecondary");
        note.Margin = new Thickness(0, 6, 0, 12); panel.Children.Add(note);
        string selected = StoredFlagColor(card.Name);
        var choice = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 0, 0, 8) };
        panel.Children.Add(LabelFor(choice, L.Text("Flag frame color")));
        foreach (var (value, text) in new[] { ("auto", L.Text("Automatic — theme default")), ("accent", L.Text("Theme accent")),
                     ("red", L.Text("Theme red")), ("neutral", L.Text("Neutral")), ("custom", L.Text("Custom color…")) })
            choice.Items.Add(new ComboBoxItem { Content = text, Tag = value });
        void Select() => choice.SelectedItem = choice.Items.OfType<ComboBoxItem>().First(i => (string)i.Tag! == (selected.StartsWith('#') ? "custom" : selected));
        Select(); panel.Children.Add(choice);
        Button? customButton = null;
        customButton = SettingsEditor.Button(L.Text("Choose color…"), Pick);
        customButton.IsEnabled = selected.StartsWith('#'); panel.Children.Add(customButton);
        bool updating = false;
        void Save(string value)
        {
            selected = LeagueFlagColor.Normalize(value);
            if (!_shared.Settings.LeagueFlagColors.TryGetValue(FlagScope, out var flags) || flags is null) _shared.Settings.LeagueFlagColors[FlagScope] = flags = [];
            if (selected == "auto") flags.Remove(card.Name.ToUpperInvariant()); else flags[card.Name.ToUpperInvariant()] = selected;
            _shared.Settings.Save(); SetFlagColor(card.Name, false, selected); SetFlagColor(card.Name, true, selected);
            if (customButton is not null) customButton.IsEnabled = selected.StartsWith('#');
            SetStatus(L.Text("Flag appearance saved in Waypoint. LocalExile was not changed."));
        }
        void Pick()
        {
            uint rgb = LeagueFlagColor.TryHex(selected, out var custom) ? custom : ThemeRgb(LeagueFlagColor.Role(selected, card.Primary.Hardcore));
#if WINDOWS
            if (Owner is null) return;
            var picker = new ColorPickerDialog(Owner, L.Text("Flag frame color"), Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
            if (picker.ShowDialog() == true) Save(ThemePalette.Hex(picker.SelectedColor));
#else
            var picker = new FlagColorDialog(Owner, $"#{rgb:X6}");
            if (picker.ShowDialog() == true) Save(picker.Value);
#endif
            updating = true; Select(); updating = false;
        }
        choice.SelectionChanged += (_, _) =>
        {
            if (updating || (choice.SelectedItem as ComboBoxItem)?.Tag is not string value) return;
            if (value == "custom") Pick(); else Save(value);
        };
    }
#if !WINDOWS
    sealed class FlagColorDialog : FormWindow
    {
        public string Value { get; private set; }
        public FlagColorDialog(Window? owner, string initial)
        {
            Value = initial; DialogLayout.Configure(this, owner, L.Text("Flag frame color"), 420);
            var body = new StackPanel { Margin = new Thickness(20) };
            var input = new TextBox { Text = initial, MaxLength = 7 }; body.Children.Add(LabelFor(input, L.Text("Flag frame color"))); body.Children.Add(input);
            var error = Type("", 12, false, "Shell.Error"); body.Children.Add(error);
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(SettingsEditor.Button(L.Text("Apply"), () =>
            {
                if (!LeagueFlagColor.TryHex(input.Text, out _)) { error.Text = L.Text("Enter a color as #RRGGBB, for example #2584D7."); return; }
                Value = LeagueFlagColor.Normalize(input.Text); Accept();
            }));
            row.Children.Add(Dismiss(L.Text("Cancel"))); body.Children.Add(row); Content = body;
        }
    }
#endif
}
