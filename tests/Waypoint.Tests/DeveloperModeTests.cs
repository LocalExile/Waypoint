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
}
