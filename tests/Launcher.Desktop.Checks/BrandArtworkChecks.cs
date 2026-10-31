using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Launcher.Core;
using Launcher.Desktop;

static class BrandArtworkChecks
{
    static byte[] Pixels(BitmapSource source)
    {
        var data = new byte[source.PixelWidth * source.PixelHeight * 4];
        new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0).CopyPixels(data, source.PixelWidth * 4, 0);
        return data;
    }
    public static void Run(App app, MainWindow owner, Action<bool, string> check)
    {
        var original = app.Appearance;
        var image = (Border)owner.FindName("BrandEmblem");
        var brush = (ImageBrush)image.Background;
        try
        {
            foreach (var scheme in ThemePalette.Schemes)
                foreach (var mode in new[] { ("Dark", false), ("Light", false), ("System", false), ("System", true) })
                    foreach (string vision in new[] { "Standard", "RedGreen", "BlueYellow", "Grayscale" })
                    {
                        var settings = new AppearanceSettings(Mode: mode.Item1, Scheme: scheme.Id, Vision: vision);
                        ThemeResources.Apply(app.Resources, settings, mode.Item2, false);
                        var artwork = (BitmapSource)brush.ImageSource;
                        var bytes = Pixels(artwork);
                        int visible = 0; bool monochrome = true;
                        for (int i = 0; i < bytes.Length; i += 4)
                            if (bytes[i + 3] > 0) { visible++; monochrome &= bytes[i] == bytes[i + 1] && bytes[i + 1] == bytes[i + 2]; }
                        check(artwork.IsFrozen && artwork.PixelWidth == 192 && visible > 5000 && visible < 35000 && bytes[3] == 0 &&
                            (vision != "Grayscale" || monochrome),
                            $"Brand {scheme.Id}/{mode}/{vision} uses actual themeable artwork, transparent corners and the selected vision treatment.");
                    }
            var custom = new AppearanceSettings(Scheme: "custom", ProtectContrast: false, DarkColors: new() {
                ["Accent"] = "#FF8020", ["AccentSurface"] = "#102030", ["Muted"] = "#B0BCC8" });
            app.ApplyAppearance(custom); var warm = (BitmapSource)brush.ImageSource;
            var cool = custom with { DarkColors = new(custom.DarkColors!) { ["Accent"] = "#20E0FF" } };
            app.ApplyAppearance(cool);
            check(!Pixels(warm).SequenceEqual(Pixels((BitmapSource)brush.ImageSource)), "Custom accent edits recolor the etched glyphs instead of retaining fixed blue artwork.");
            var beforeSurface = Pixels((BitmapSource)brush.ImageSource);
            app.ApplyAppearance(cool with { DarkColors = new(cool.DarkColors!) { ["AccentSurface"] = "#422419" } });
            check(!beforeSurface.SequenceEqual(Pixels((BitmapSource)brush.ImageSource)), "The existing Logo background control changes the stone surface.");
            app.ApplyAppearance(new()); var normal = (BitmapSource)brush.ImageSource;
            app.ApplyAppearance(new(ReduceMotion: true, Font: "OpenDyslexic", TextScale: 150, StrongFocus: true));
            check(ReferenceEquals(normal, brush.ImageSource) && !image.Focusable && !image.IsHitTestVisible && UIElementAutomationPeer.CreatePeerForElement(image) is null,
                "Reading preferences and reduced motion retain the static emblem without an extra keyboard stop.");
            app.ApplyAppearance(new(IncreaseContrast: true)); var strong = (BitmapSource)brush.ImageSource;
            app.ApplyContrast(true); var contrast = (BitmapSource)brush.ImageSource; var hc = Pixels(contrast);
            bool systemInk = true; int lit = 0;
            for (int i = 0; i < hc.Length; i += 4)
                if (hc[i + 3] > 0) { lit++; systemInk &= hc[i] == SystemColors.WindowTextColor.B && hc[i + 1] == SystemColors.WindowTextColor.G && hc[i + 2] == SystemColors.WindowTextColor.R; }
            check(systemInk && lit > 500 && !Pixels(strong).SequenceEqual(hc),
                "Windows High Contrast uses crisp system-colored engravings and takes precedence over enhanced-contrast artwork.");
            using var stream = Application.GetResourceStream(new Uri($"pack://application:,,,/{typeof(App).Assembly.GetName().Name};component/app.ico"))!.Stream;
            var icon = new IconBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            check(icon.Frames.Select(f => f.PixelWidth).Order().SequenceEqual(new[] { 16,20,24,32,40,48,64,96,128,256 }),
                "The embedded window/desktop icon contains all ten native sizes, including small taskbar and 256-pixel artwork.");
        }
        finally { app.ApplyContrast(false); app.ApplyAppearance(original); }
    }
}
