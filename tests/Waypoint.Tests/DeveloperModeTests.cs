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

    // What an older Waypoint did not save yet is filled in so that nothing changes for the settings' owner.
    [Fact]
    public void Saved_settings_keep_their_owner_where_they_were()
    {
        // From before the modes: every section.
        Assert.True(Settings.Saved("{}").Advanced);
        Assert.False(Settings.Saved("""{"AdvancedMode":false}""").Advanced);
        // The choice on a source copy is not made for anyone: whoever was in Simple mode there is asked (EditionsTests).
        Assert.Null(Settings.Saved("""{"AdvancedMode":false,"DeveloperMode":false}""").SourceChoiceMade);
        Assert.True(Settings.Saved("""{"SourceChoiceMade":true}""").SourceChoiceMade);
        // A setting an earlier build of this branch wrote is ignored, not an error.
        Assert.False(Settings.Saved("""{"AdvancedMode":false,"MoreSections":true}""").Advanced);
    }
}
