using Waypoint;

namespace Waypoint.Tests;

// Simple mode unless Developer Mode has unlocked Advanced.
public class DeveloperModeTests
{
    [Theory]
    [InlineData(null, null, false)]    // a new player
    [InlineData(null, false, false)]
    [InlineData(true, false, false)]   // unlocked, not chosen
    [InlineData(false, true, false)]   // chosen, then locked
    [InlineData(null, true, false)]
    [InlineData(true, true, true)]
    public void Advanced_needs_Developer_Mode(bool? developer, bool? advanced, bool expected) =>
        Assert.Equal(expected, new Settings { DeveloperMode = developer, AdvancedMode = advanced }.Advanced);

    // Simple mode's Logs, Console and Tools: off for someone new, kept for someone who had Waypoint before.
    [Fact]
    public void Simple_mode_keeps_its_further_sections_for_settings_saved_before_the_short_list()
    {
        Assert.False(new Settings().More);
        Assert.True(Settings.Saved("{}").More);
        Assert.True(Settings.Saved("""{"AdvancedMode":false,"DeveloperMode":false}""").More);
        // Once chosen, the choice is what is saved.
        Assert.False(Settings.Saved("""{"MoreSections":false}""").More);
        Assert.True(Settings.Saved("""{"MoreSections":true}""").More);
        // The other fill-ins are as they were.
        Assert.True(Settings.Saved("{}").Advanced);
        Assert.False(Settings.Saved("""{"AdvancedMode":false}""").Advanced);
    }
}
