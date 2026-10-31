using Waypoint;

namespace Waypoint.Tests;

public class CheckoutUpdatesTests
{
    static readonly DateTimeOffset Now = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void An_update_is_due_once_a_day()
    {
        Assert.True(CheckoutUpdates.Due(true, null, null, Now));
        Assert.True(CheckoutUpdates.Due(true, Now.AddDays(-1), null, Now));
        Assert.False(CheckoutUpdates.Due(true, Now.AddHours(-23), null, Now));
    }

    [Fact]
    public void A_player_can_turn_updates_off() => Assert.False(CheckoutUpdates.Due(false, null, null, Now));

    [Fact]
    public void Going_back_pauses_updates_until_the_pause_ends()
    {
        Assert.False(CheckoutUpdates.Due(true, null, Now.AddHours(1), Now));
        Assert.True(CheckoutUpdates.Due(true, null, Now, Now));
    }

    [Fact]
    public void A_plain_copy_of_LocalExile_may_be_updated() =>
        Assert.True(CheckoutUpdates.PlainCopy("main", "main", "LocalExile/LocalExile", 0, (0, 5)));

    [Theory]
    [InlineData("feature/x", "LocalExile/LocalExile", 0, 0)]    // another branch
    [InlineData("main", "evrydayjunglist/evryPath", 0, 0)]      // a fork as origin
    [InlineData("main", null, 0, 0)]                            // not from GitHub
    [InlineData("main", "LocalExile/LocalExile", 1, 0)]         // uncommitted changes
    [InlineData("main", "LocalExile/LocalExile", 0, 2)]         // commits of the player's own
    public void Anything_of_the_players_own_keeps_the_checkout_as_it_is(string branch, string? origin, int changes, int ahead) =>
        Assert.False(CheckoutUpdates.PlainCopy(branch, "main", origin, changes, (ahead, 5)));

    [Fact]
    public void A_checkout_LocalExile_cannot_be_compared_with_is_left_alone() =>
        Assert.False(CheckoutUpdates.PlainCopy("main", "main", "LocalExile/LocalExile", 0, null));

    [Fact]
    public void The_way_back_is_offered_for_a_week()
    {
        Assert.False(CheckoutUpdates.CanGoBack(null, Now));
        Assert.True(CheckoutUpdates.CanGoBack(new("abc", null, Now.AddDays(-6)), Now));
        Assert.False(CheckoutUpdates.CanGoBack(new("abc", null, Now.AddDays(-7)), Now));
    }
}
