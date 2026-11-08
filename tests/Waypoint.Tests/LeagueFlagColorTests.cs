using System.Windows.Media;

namespace Waypoint.Tests;

public class LeagueFlagColorTests
{
    [Fact]
    public void Invalid_preferences_fall_back_to_the_theme_and_custom_hex_is_canonical()
    {
        Assert.Equal("auto", LeagueFlagColor.Normalize("not-a-color"));
        Assert.Equal("#12ABEF", LeagueFlagColor.Normalize("#12abef"));
        Assert.False(LeagueFlagColor.TryHex("# 12345", out _));
        Assert.Equal("Shell.Error", LeagueFlagColor.Role(null, hardcore: true));
        Assert.Equal("Shell.Accent", LeagueFlagColor.Role(null, hardcore: false));
        Assert.Equal("Shell.TextSecondary", LeagueFlagColor.Role("neutral", hardcore: true));
    }
    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public void Flag_frames_stay_readable_in_every_scheme_and_grayscale(string mode)
    {
        static uint Rgb(Color c) => (uint)(c.R << 16 | c.G << 8 | c.B);
        foreach (var scheme in Waypoint.ThemePalette.Schemes)
        foreach (bool grayscale in new[] { false, true })
        {
            var settings = Waypoint.ThemePalette.Normalize(new AppearanceSettings(Mode: mode, Scheme: scheme.Id, Vision: grayscale ? "Grayscale" : "Normal"));
            var shell = Waypoint.ThemePalette.Shell(Waypoint.ThemePalette.Build(settings, mode == "Light"), settings, mode == "Dark");
            uint[] surfaces = [Rgb(shell["Shell.Background"]), Rgb(shell["Shell.Panel"]), Rgb(shell["Shell.Banner"])];
            foreach (uint original in new[] { Rgb(shell["Shell.Error"]), Rgb(shell["Shell.Accent"]), 0u, 0xffffffu, 0x123456u })
            {
                uint color = LeagueFlagColor.Readable(original, surfaces, grayscale);
                Assert.All(surfaces, surface => Assert.True(LeagueFlagColor.Contrast(color, surface) >= 3, $"{scheme.Id} {mode}: {color:X6}"));
                if (grayscale) { Assert.Equal(color >> 16 & 255, color & 255); Assert.Equal(color >> 8 & 255, color & 255); }
            }
        }
    }
}
