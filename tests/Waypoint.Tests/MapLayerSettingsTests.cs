using System.Text.Json;

namespace Waypoint.Tests;

public class MapLayerSettingsTests
{
    [Fact]
    public void LayersRoundTripAndOlderSettingsKeepExistingDefaults()
    {
        var settings = new Waypoint.Settings { MapLayers = new(false, true, false, true, true, true, true, true), Language = "zh-CN" };
        var restored = JsonSerializer.Deserialize<Waypoint.Settings>(JsonSerializer.Serialize(settings))!;
        Assert.Equal(settings.MapLayers, restored.MapLayers);
        Assert.Equal("zh-CN", restored.Language);
        var older = JsonSerializer.Deserialize<Waypoint.Settings>("""{"MapLayers":{"Walkability":false,"Grid":true,"Markers":true,"TeleportLinks":true}}""")!.MapLayers;
        Assert.True(older.Grid);
        Assert.True(older.TeleportLinks);
        Assert.False(older.MonsterPacks);
        Assert.False(older.Encounters);
        Assert.False(older.InternalMarkers);
        Assert.False(older.AllTiles);
        Assert.Equal(new Waypoint.MapLayerSettings(), JsonSerializer.Deserialize<Waypoint.Settings>("{}")!.MapLayers);
    }
}
