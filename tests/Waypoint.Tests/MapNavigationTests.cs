namespace Waypoint.Tests;

public class MapNavigationTests
{
    static Waypoint.MapData Map() => new("Area", "test", 1, 1, 1, 1, [0], [], [],
        [new Waypoint.MapExit(23, 46, "Entrance", 1, "Next", null, DestinationId: "next"),
         new Waypoint.MapExit(50, 60, "Teleport", 2, null, null, TargetX: 100, TargetY: 120),
         new Waypoint.MapExit(70, 80, "Teleport", 3, null, null),
         new Waypoint.MapExit(5, 5, "Unused", 0, "Not connected", null, DestinationId: "wrong")],
        [new Waypoint.MapEntrance(25, 40, 1, Destination: "Previous", DestinationId: "previous")], [], [], []);

    [Fact]
    public void OnlyAreaTransitionsAndArrivalPointsHaveDestinations()
    {
        var map = Map();
        Assert.Equal("next", Waypoint.MapNavigation.DestinationId(map, new("exit", 0)));
        Assert.Equal("previous", Waypoint.MapNavigation.DestinationId(map, new("entrance", 0)));
        Assert.Null(Waypoint.MapNavigation.DestinationId(map, new("exit", 1)));
        Assert.Null(Waypoint.MapNavigation.DestinationId(map, new("exit", 3)));
        Assert.Null(Waypoint.MapNavigation.DestinationId(map, new("exit", 50)));
    }

    [Fact]
    public void TeleportLinesUseOnlyExplicitTargets()
    {
        var links = Waypoint.MapNavigation.TeleportLinks(Map()).ToList();
        var link = Assert.Single(links);
        Assert.Equal((50, 60, 100, 120), link);
    }

    [Fact]
    public void ServiceResponseKeepsOptionalTargetsAndDisabledState()
    {
        const string json = """{"x":50,"y":60,"kind":"Teleport","number":2,"destination":null,"label":null,"destinationId":null,"targetX":100,"targetY":120,"enabled":false}""";
        var exit = System.Text.Json.JsonSerializer.Deserialize<Waypoint.MapExit>(json, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(exit);
        Assert.False(exit.Enabled);
        Assert.Equal(100, exit.TargetX);
        Assert.Equal(120, exit.TargetY);
        Assert.Null(exit.DestinationId);
    }
}
