namespace Waypoint.Tests;

public class MapMarkersTests
{
    [Fact]
    public void WaypointsAndCraftingUnlocksHaveDistinctMarkersEvenNearExits()
    {
        var data = new Waypoint.MapData("Area", "test", 1, 1, 1, 1, [0], [], [],
            [new Waypoint.MapExit(23, 46, "Entrance", 1, null, null)], [],
            [new Waypoint.MapObject(23, 46, "Metadata/MiscellaneousObjects/Waypoint"),
             new Waypoint.MapObject(69, 92, "Metadata/MiscellaneousObjects/CraftRecipeUnlockMarker")], [], []);
        var markers = Waypoint.MapMarkers.Create(data);
        var waypoint = Assert.Single(markers, m => m.Key == new Waypoint.MapMarkerKey("object", 0));
        var crafting = Assert.Single(markers, m => m.Key == new Waypoint.MapMarkerKey("object", 1));
        Assert.NotEqual(waypoint.Icon, crafting.Icon);
        Assert.NotEqual(MaterialDesignThemes.Wpf.PackIconKind.CubeOutline, waypoint.Icon);
        Assert.NotEqual(MaterialDesignThemes.Wpf.PackIconKind.CubeOutline, crafting.Icon);
    }

    [Fact]
    public void HitTestingReturnsEveryOverlappingMarkerWithoutHidingNearbyNpcs()
    {
        var data = new Waypoint.MapData("Area", "test", 1, 1, 1, 1, [0], [], [],
            [new Waypoint.MapExit(23, 46, "Teleport", 1, null, null)], [],
            [new Waypoint.MapObject(23, 46, "Metadata/NPC/Test"), new Waypoint.MapObject(69, 92, "Metadata/NPC/Other"), new Waypoint.MapObject(23, 46, "Metadata/MiscellaneousObjects/AreaTransition")],
            [new Waypoint.MapPoint(23, 46, "mapboss1")], []);
        var markers = Waypoint.MapMarkers.Create(data);
        Assert.DoesNotContain(markers, m => m.Key == new Waypoint.MapMarkerKey("object", 2));
        Assert.Equal(new[] { new Waypoint.MapMarkerKey("exit", 0), new Waypoint.MapMarkerKey("point", 0), new Waypoint.MapMarkerKey("object", 0) }, Waypoint.MapMarkers.FindAll(markers, 1, 2, .5).Select(m => m.Key));
        Assert.Equal(new Waypoint.MapMarkerKey("exit", 0), Waypoint.MapMarkers.Find(markers, 1, 2, .5)?.Key);
        Assert.Equal(new Waypoint.MapMarkerKey("object", 1), Waypoint.MapMarkers.Find(markers, 3, 4, .5)?.Key);
        Assert.Null(Waypoint.MapMarkers.Find(markers, 20, 20, .5));
    }

    [Fact]
    public void BossMarkerUsesResolvedNameAndKeepsIdentifier()
    {
        const string json = """{"x":23,"y":46,"name":"mapboss2","room":"boss.arm","bosses":[{"id":"Metadata/Monsters/Brute/BossBrute","name":"Brutus, Lord Incarcerator"}],"bossesAreCandidates":false}""";
        var point = System.Text.Json.JsonSerializer.Deserialize<Waypoint.MapPoint>(json, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(point);
        Assert.Equal("Brutus, Lord Incarcerator", point.Display);
        Assert.Equal("Metadata/Monsters/Brute/BossBrute", Assert.Single(point.Bosses!).Id);
        Assert.Equal("mapboss2", new Waypoint.MapPoint(1, 2, "mapboss2").Display);
        var candidate = new Waypoint.MapPoint(1, 2, "mapboss", Bosses: [new Waypoint.MapBoss("boss/id", "Fire Fury")], BossesAreCandidates: true);
        Assert.Contains("Fire Fury", candidate.Display);
        Assert.NotEqual("Fire Fury", candidate.Display);
    }
}
