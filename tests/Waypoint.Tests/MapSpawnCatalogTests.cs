using System.Text.Json;

namespace Waypoint.Tests;

public class MapSpawnCatalogTests : IDisposable
{
    readonly string _folder = Path.Combine(Path.GetTempPath(), "waypoint-pack-catalog-" + Guid.NewGuid().ToString("N"));
    readonly MapSpawnMonster[] _monsters = [new("wrong", "Wrong"), new("cannibal", "Cannibal"), new("fire", "Fire-eater"), new("leader", "Chief")];

    public MapSpawnCatalogTests() => Directory.CreateDirectory(_folder);
    void Write(string file, string json) => File.WriteAllText(Path.Combine(_folder, file), json);

    [Fact]
    public void CatalogKeepsNamedPacksSeparateAndResolvesSharedWeightsAndMapOnlyPacks()
    {
        Write("MonsterPacks.json", """
            [
              {"Id":"area-camp","MinCount":2,"MaxCount":4,"WorldAreasKeys":[7],"__col9":[0]},
              {"Id":"ordinary","MinCount":3,"MaxCount":5,"WorldAreasKeys":[7,8],"__col9":[100]},
              {"Id":"atlas-pack","MinCount":1,"MaxCount":2,"WorldAreasKeys":[],"__col9":[],"BossMonster_MonsterVarietiesKeys":[3,999],"BossMonsterSpawnChance":100,"BossMonsterCount":1}
            ]
            """);
        Write("MonsterPackEntries.json", """
            [
              {"MonsterPacksKey":0,"MonsterVarietiesKey":1,"Weight":100},
              {"MonsterPacksKey":0,"MonsterVarietiesKey":2,"Weight":100},
              {"MonsterPacksKey":0,"MonsterVarietiesKey":0,"Weight":0},
              {"MonsterPacksKey":1,"MonsterVarietiesKey":1,"Weight":1},
              {"MonsterPacksKey":2,"MonsterVarietiesKey":2,"Weight":1},
              {"MonsterPacksKey":0,"MonsterVarietiesKey":null,"Weight":100},
              {"MonsterPacksKey":null,"MonsterVarietiesKey":0,"Weight":100},
              {"MonsterPacksKey":0,"MonsterVarietiesKey":999,"Weight":1}
            ]
            """);
        Write("Maps.json", """[{"Regular_WorldAreasKey":7,"MonsterPacksKeys":[2,2,999]},{"Regular_WorldAreasKey":null,"MonsterPacksKeys":[0]}]""");
        var catalog = MapSpawnCatalog.Load(_folder, _monsters);
        var area = catalog.ForArea("area", 7);
        Assert.Empty(catalog.Warnings);
        Assert.Equal(3, area.Packs.Count);
        Assert.Equal(new[] { "ordinary", "atlas-pack" }, area.ScatteredIds);
        var camp = Assert.Single(area.Packs, p => p.Id == "area-camp");
        Assert.Equal((2, 4), (camp.MinCount, camp.MaxCount));
        Assert.Equal(new[] { "Cannibal", "Fire-eater" }, camp.Monsters.Select(m => m.Name));
        var atlas = Assert.Single(area.Packs, p => p.Id == "atlas-pack");
        Assert.Equal("Chief", Assert.Single(atlas.Leaders).Name);
        Assert.Equal((100, 1), (atlas.LeaderChance, atlas.LeaderCount));
        Assert.Equal("ordinary", Assert.Single(catalog.ForArea("other", 8).ScatteredIds));
    }

    [Fact]
    public void MissingOrMalformedOptionalTablesReturnDiagnosticsWithoutInventingCandidates()
    {
        Write("MonsterPacks.json", "{broken");
        var catalog = MapSpawnCatalog.Load(_folder, _monsters);
        Assert.Equal(new[] { "MonsterPackEntries.json", "MonsterPacks.json" }, catalog.Warnings);
        Assert.Empty(catalog.ForArea("area", 7).Packs);
        Assert.Empty(catalog.ForArea("area", 7).ScatteredIds);
    }

    [Theory]
    [InlineData("Metadata/Shrines/Shrine", "Shrine")]
    [InlineData("Metadata/Chests/StrongBoxes/Arcanist", "Strongbox")]
    [InlineData("Metadata/Monsters/Spirit/TormentedRogue", "Torment")]
    [InlineData("Metadata/Monsters/Exiles/ExileDuelist1", "Exile")]
    [InlineData("Metadata/Terrain/Leagues/Delve/Objects/EncounterSpawners/RogueExileEncounter/RogueExileEncounterTier1_1", "Exile")]
    [InlineData("Metadata/Terrain/Temple/Abyss/temple_abyss.gt", null)]
    [InlineData("Metadata/Terrain/Leagues/Incursion/Objects/Audio/TempleFeature_BreachRoomIII", null)]
    public void OnlyExplicitEncounterObjectsAreClassified(string type, string? kind) => Assert.Equal(kind, MapContent.EncounterObject(type));

    [Theory]
    [InlineData("shrine", "Shrine")]
    [InlineData("strongbox_2", "Strongbox")]
    [InlineData("torment_1", "Torment")]
    [InlineData("node_legion", "Legion")]
    [InlineData("encounter_league_breach", "Breach")]
    [InlineData("abyss", null)]
    [InlineData("waterabyss", null)]
    [InlineData("ritual_boss_spawn_override", null)]
    public void EncounterPointNamesDoNotMatchTerrainOrBossControls(string point, string? kind) => Assert.Equal(kind, MapContent.EncounterPoint(point));

    [Theory]
    [InlineData("Metadata/MiscellaneousObjects/ProphecyMonsterLocator", true)]
    [InlineData("Metadata/MiscellaneousObjects/AmbushPointSmall", true)]
    [InlineData("Metadata/MiscellaneousObjects/Environment/FlowSource", true)]
    [InlineData("Metadata/Critters/Crow/Stationary", true)]
    [InlineData("Metadata/NPC/Act1/Doorman", false)]
    [InlineData("Metadata/QuestObjects/QuestTrigger", false)]
    public void QuietModeHidesPlacementHelpersAndPreservesNpcsAndQuests(string type, bool hidden) => Assert.Equal(hidden, MapContent.IsInternalObject(type));

    public void Dispose() { if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true); }
}
