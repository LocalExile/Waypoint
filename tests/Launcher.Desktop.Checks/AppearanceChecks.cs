using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Launcher.Core;
using Launcher.Desktop;

static class AppearanceChecks
{
    public static void Run(App app, MainWindow owner, Action<bool, string> check)
    {
        var original = app.Appearance;
        try
        {
            foreach (var scheme in ThemePalette.Schemes)
                foreach (string mode in new[] { "Dark", "Light" })
                    foreach (string vision in new[] { "Standard", "RedGreen", "BlueYellow", "Grayscale" })
                        foreach (bool more in new[] { false, true })
                        {
                            var settings = new AppearanceSettings(Mode: mode, Scheme: scheme.Id, Vision: vision, IncreaseContrast: more);
                            var palette = ThemePalette.Build(settings, false); double ratio = more ? 7 : 4.5;
                            bool all = ThemePalette.Roles.All(r => palette.ContainsKey(r.Key));
                            foreach (string bg in new[] { "Background", "Surface", "Sidebar", "ControlSurface", "InputSurface", "HoverSurface", "StatusSurface" })
                                all &= ThemePalette.Contrast(palette["Text"], palette[bg]) >= ratio && ThemePalette.Contrast(palette["Muted"], palette[bg]) >= ratio;
                            foreach (var pair in new[] { ("AccentText", "PrimaryFill"), ("BadgeText", "BadgeSurface"), ("SelectedText", "SelectedSurface"), ("CheckMark", "Accent"), ("CloseText", "CloseHover") })
                                all &= ThemePalette.Contrast(palette[pair.Item1], palette[pair.Item2]) >= ratio;
                            check(all, $"{scheme.Id}/{mode}/{vision} contains all roles and meets {(more ? "enhanced" : "standard")} text contrast.");
                        }
            var system = new AppearanceSettings(Mode: "System", Scheme: "ocean");
            check(ThemePalette.Build(system, true)["Background"] != ThemePalette.Build(system, false)["Background"], "System appearance uses the current Windows light/dark value.");
            var custom = new AppearanceSettings(Scheme: "custom", LightColors: new() { ["PrimaryFill"] = "#FEAF42" }, DarkColors: new() { ["PrimaryFill"] = "#906EFF" });
            check(ThemePalette.Build(custom with { Mode = "Light" }, false)["PrimaryFill"] == ThemePalette.Parse("#FEAF42") && ThemePalette.Build(custom, false)["PrimaryFill"] == ThemePalette.Parse("#906EFF"), "Custom light/dark colors remain separate and primary buttons are independent of accents.");
            check(ThemePalette.Normalize(custom with { Mode = "unknown", Font = "missing", TextScale = 999, DarkColors = new() { ["PrimaryFill"] = "bad", ["untrusted"] = "#FFFFFF" } }) is { Mode: "Dark", Font: "Default", TextScale: 150, DarkColors.Count: 0 }, "Unsupported preferences and invalid custom colors fall back safely.");
            foreach (string hex in new[] { "#000000", "#FFFFFF", "#A431E9", "#0080FF", "#FABE31" })
            {
                var color = ThemePalette.Parse(hex); var hsv = ColorWheel.ToHsv(color); var back = ColorWheel.FromHsv(hsv.H, hsv.S, hsv.V);
                check(back == color, "Color wheel preserves exact RGB values through HSV conversion: " + hex);
            }
            app.ApplyAppearance(new(Font: "OpenDyslexic", TextScale: 125, StrongFocus: true, ReduceMotion: true));
            var family = (FontFamily)app.Resources["AppFont"];
            check(family.GetTypefaces().Any(t => t.TryGetGlyphTypeface(out var glyph) && glyph.FamilyNames.Values.Any(n => n.Contains("OpenDyslexic", StringComparison.OrdinalIgnoreCase))), "Bundled OpenDyslexic is actually loadable from WPF resources.");
            check((double)app.Resources["Font14"] == 17.5 && (Thickness)app.Resources["FocusThickness"] == new Thickness(4) && !(bool)app.Resources["AnimateProgress"] && !(bool)app.Resources["SidebarDetailsExpanded"], "Text scale, compact sidebar, stronger focus and reduced-motion feedback apply to shared resources.");
            app.ApplyContrast(true);
            check(ReferenceEquals(app.Resources["Background"], SystemColors.WindowBrush) && ReferenceEquals(app.Resources["PrimaryFill"], SystemColors.HighlightBrush) && ReferenceEquals(app.Resources["CheckMark"], SystemColors.HighlightTextBrush), "Windows High Contrast overrides custom, action and checkmark colors.");
            app.ApplyContrast(false); app.ApplyAppearance(original);
            var host = (PageHost)owner.FindName("Pages"); int pageBefore = host.SelectedIndex;
            foreach (var preference in new[] { new AppearanceSettings(Mode: "Light"), new AppearanceSettings(Font: "Verdana", TextScale: 125), new AppearanceSettings(Mode: "Light", Font: "OpenDyslexic", TextScale: 150) })
            {
                app.ApplyAppearance(preference);
                for (int page = 0; page < host.Items.Count; page++)
                {
                    host.SelectedIndex = page; var root = (FrameworkElement)owner.Content; root.Measure(new Size(960, 730)); root.Arrange(new Rect(0, 0, 960, 730)); root.UpdateLayout();
                    check(host.ActualHeight > 200 && host.ActualWidth > 650, $"Page {page} remains arranged with {preference.Font} at {preference.TextScale}% in {preference.Mode}.");
                }
            }
            host.SelectedIndex = pageBefore; app.ApplyAppearance(original);
            var dialog = new AppearanceDialog(owner, original, _ => throw new Exception("Unshown preview must never save"));
            dialog.Preview(new(Mode: "Light", Scheme: "rose", Font: "Verdana", TextScale: 150));
            check(app.Appearance is { Mode: "Light", Scheme: "rose", TextScale: 150 }, "The appearance editor previews settings before saving.");
            dialog.Close();
            check(app.Appearance == ThemePalette.Normalize(original) || app.Appearance.Mode == original.Mode && app.Appearance.Scheme == original.Scheme && app.Appearance.TextScale == original.TextScale, "Closing a preview restores the prior appearance.");
        }
        finally { app.ApplyContrast(false); app.ApplyAppearance(original); }
    }
}
