namespace Waypoint.Tests;

public class MapAreaSearchTests
{
    [Theory]
    [InlineData("pri", true)]
    [InlineData("upp pri", true)]
    [InlineData("PRI UPP", true)]
    [InlineData("1_1_7_2", true)]
    [InlineData("upp coast", false)]
    [InlineData(" ", true)]
    public void MatchesAllSearchTerms(string search, bool expected)
    {
        Assert.Equal(expected, Waypoint.MapAreaSearch.Matches("1_1_7_2", "The Upper Prison", search));
    }

    [Fact]
    public void ResolvePrefersExactIdThenFirstFilteredArea()
    {
        Waypoint.MapArea[] areas = [new("1_1_7_1", "The Lower Prison"), new("1_1_7_2", "The Upper Prison")];
        Assert.Equal("1_1_7_2", Waypoint.MapAreaSearch.Resolve(areas, "1_1_7_2")?.Id);
        Assert.Equal("1_1_7_2", Waypoint.MapAreaSearch.Resolve(areas, "upp pri")?.Id);
        Assert.Null(Waypoint.MapAreaSearch.Resolve(areas, "no such area"));
        Assert.Null(Waypoint.MapAreaSearch.Resolve(areas, " "));
    }

    [Fact]
    public void AreaSortUsesNaturalIdOrderAndCanSortByName()
    {
        var first = new Waypoint.MapArea("1_1_2", "Zebra");
        var second = new Waypoint.MapArea("1_1_10", "Apple");
        Assert.True(new Waypoint.MapAreaComparer(false).Compare(first, second) < 0);
        Assert.True(new Waypoint.MapAreaComparer(true).Compare(first, second) > 0);
    }
}
