using System.Text.Json;

namespace Waypoint.Tests;

public class MapPresentationTests
{
    static Waypoint.MapData Map() => new("Area", "test", 1, 1, 1, 1, [0], [], [],
        [new(23, 46, "Unused", 0, null, null), new(23, 46, "Teleport", 1, null, null, Enabled: false)],
        [new(23, 46, 1, Enabled: false)],
        [new(23, 46, "Metadata/MiscellaneousObjects/Environment/FlowSource"), new(23, 46, "Metadata/Critters/Crow/Test"),
         new(23, 46, "Metadata/NPC/Test"), new(23, 46, "Metadata/MiscellaneousObjects/Waypoint"),
         new(23, 46, "Metadata/Chests/StrongBoxes/Arcanist")], [], [],
        PackAnchors: [new(23, 46, "%area%-camp", "room.arm", ["test-camp"]), new(23, 46, "rare", "room.arm", ["test-camp"], "rare")],
        MonsterPacks: [new("test-camp", 2, 4, [new("cannibal", "Cannibal")], [], 0, 0)],
        Encounters: [new(23, 46, "Strongbox", "Metadata/Chests/StrongBoxes/Arcanist", IsObject: true)]);

    [Fact]
    public void QuietDefaultsKeepLandmarksAndMuteArrivalOnlyOrDisabledTransitions()
    {
        var markers = Waypoint.MapMarkers.Create(Map());
        Assert.DoesNotContain(markers, m => m.Key.Kind is "pack" or "encounter" || m.Key == new Waypoint.MapMarkerKey("exit", 0));
        Assert.DoesNotContain(markers, m => m.Key.Kind == "object" && m.Key.Index is 0 or 1 or 4);
        Assert.Contains(markers, m => m.Key == new Waypoint.MapMarkerKey("object", 2));
        Assert.Contains(markers, m => m.Key == new Waypoint.MapMarkerKey("object", 3));
        Assert.All(markers.Where(m => m.Key.Kind is "entrance" or "exit"), m => Assert.Equal("Shell.TextSecondary", m.Brush));
        var full = Waypoint.MapMarkers.Create(Map(), internalMarkers: true, monsterPacks: true, encounters: true);
        Assert.Equal(markers.Count + 6, full.Count);
        Assert.Single(full, m => m.Key.Kind == "encounter");
        Assert.Equal(2, full.Count(m => m.Key.Kind == "pack"));
    }

    [Fact]
    public void OptionalLayersRemainSelectableWithLandmarksHiddenAndKeepAllOverlappingAnchors()
    {
        var markers = Waypoint.MapMarkers.Create(Map(), monsterPacks: true, encounters: true, landmarks: false);
        var hits = Waypoint.MapMarkers.FindAll(markers, 1, 2, .5);
        Assert.Equal(3, hits.Count);
        Assert.Equal(3, hits.Select(m => m.Key).Distinct().Count());
        Assert.DoesNotContain(hits, m => m.Key.Kind is "object" or "exit" or "entrance");
        var anchor = Map().PackAnchors![0];
        var pack = Assert.Single(Waypoint.MapPacks.Candidates(Map(), anchor));
        Assert.Equal("Cannibal", Assert.Single(pack.Monsters).Name);
        Assert.Contains(Waypoint.MapPacks.Describe(Map(), anchor, true), line => line == "room.arm");
    }

    [Fact]
    public void CopiedReportKeepsActualLevelFullUnsignedSeedRequestedSeedRevisionAndWarnings()
    {
        const string json = """{"area":"The Coast","id":"1_1_2","width":10,"height":11,"walkWidth":1,"walkHeight":1,"walk":"AA==","tiles":[],"catalog":[],"exits":[],"entrances":[],"objects":[],"points":[],"warnings":["Collision incomplete"],"seed":4294967295,"requestedSeed":1,"level":83}""";
        var map = JsonSerializer.Deserialize<Waypoint.MapData>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.Equal(uint.MaxValue, map.Seed);
        Assert.Equal(1u, map.RequestedSeed);
        Assert.Equal(83, map.Level);
        map = map with { CheckoutHead = new("1234567890123456789012345678901234567890", "main", true) };
        string report = Waypoint.MapDetails.Format(map);
        Assert.Contains("1_1_2", report);
        Assert.Contains("4294967295", report);
        Assert.Contains("83", report);
        Assert.Contains("1234567890123456789012345678901234567890", report);
        Assert.Contains("Collision incomplete", report);
        Assert.Contains("Requested seed: 1", report);
        Assert.Contains("Tracked files modified", report);
    }
}
