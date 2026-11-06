using System.Text.Json;
using System.Text.RegularExpressions;

namespace Launcher.Core;

// Compiled into the launcher and its isolated map helper; no gameplay-server dependency.
public sealed record MapSpawnMonster(string Id, string Name);
public sealed record MapSpawnPack(string Id, int MinCount, int MaxCount, IReadOnlyList<MapSpawnMonster> Monsters,
    IReadOnlyList<MapSpawnMonster> Leaders, int LeaderChance, int LeaderCount);
public sealed record MapPackAnchor(int X, int Y, string Name, string Room, IReadOnlyList<string> PackIds, string? Rarity = null);
public sealed record MapEncounterAnchor(int X, int Y, string Kind, string Name, string? Room = null, bool IsObject = false);
public sealed record MapAreaPacks(IReadOnlyList<MapSpawnPack> Packs, IReadOnlyList<string> ScatteredIds);

public static class MapContent
{
    public static bool IsBlankTile(string path) => Path.GetFileName(path).ToLowerInvariant() is "blank.tdt" or "forced_blank.tdt" or "black.tdt";
    static readonly string[] InternalPrefixes = [
        "Metadata/Critters/", "Metadata/Effects/", "Metadata/MiscellaneousObjects/Environment/",
        "Metadata/MiscellaneousObjects/Lights/", "Metadata/Environment/",
        "Metadata/MiscellaneousObjects/ProphecyMonsterLocator", "Metadata/MiscellaneousObjects/AmbushPoint",
        "Metadata/MiscellaneousObjects/ArenaMiddle"
    ];
    public static bool IsInternalObject(string type) => InternalPrefixes.Any(prefix => type.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    // Match explicit encounter objects, never arbitrary terrain paths containing a mechanic's name.
    public static string? EncounterObject(string type)
    {
        if (type.Equals("Metadata/Shrines/Shrine", StringComparison.OrdinalIgnoreCase)) return "Shrine";
        if (type.StartsWith("Metadata/Chests/StrongBoxes/", StringComparison.OrdinalIgnoreCase)) return "Strongbox";
        if (type.StartsWith("Metadata/Monsters/Spirit/Tormented", StringComparison.OrdinalIgnoreCase)) return "Torment";
        if (type.StartsWith("Metadata/Monsters/Exiles/Exile", StringComparison.OrdinalIgnoreCase)) return "Exile";
        const string delve = "Metadata/Terrain/Leagues/Delve/Objects/EncounterSpawners/";
        if (type.StartsWith(delve, StringComparison.OrdinalIgnoreCase))
        {
            string group = type[delve.Length..].Split('/')[0];
            return group switch {
                "StrongboxEncounter" => "Strongbox", "TormentedSpiritEncounter" => "Torment",
                "RogueExileEncounter" => "Exile", "EssenceEncounter" => "Essence", _ => null
            };
        }
        return type.ToLowerInvariant() switch {
            "metadata/terrain/leagues/legion/objects/legion_encounterlocator" or
            "metadata/terrain/leagues/legion/objects/legionendgame_encounterlocator" or
            "metadata/terrain/leagues/legion/objects/driedlake_encounterlocator" or
            "metadata/terrain/leagues/legion/objects/legioncocooncrystal" => "Legion",
            "metadata/terrain/leagues/lake/objects/encounters/breachcontroller" or
            "metadata/terrain/leagues/delve/objects/encountercontrolobjects/breachencountercontroller" => "Breach",
            "metadata/terrain/leagues/lake/objects/encounters/deliriummirrormarker" => "Delirium",
            "metadata/terrain/leagues/blight/objects/blightpump" => "Blight",
            "metadata/miscellaneousobjects/expedition/expeditiondetonator" => "Expedition",
            _ => null
        };
    }

    static readonly Regex EncounterPointName = new(@"^(?:(?:node_|encounter_league_)(strongbox|torment|legion|breach|harbinger|essence|expedition|delirium)|(?<numbered>strongbox|torment|legion|breach)_[1-9][0-9]*)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    public static string? EncounterPoint(string name)
    {
        if (name.Equals("shrine", StringComparison.OrdinalIgnoreCase) || name.Equals("azmeri_shrine", StringComparison.OrdinalIgnoreCase)) return "Shrine";
        if (name.Equals("legion_locator", StringComparison.OrdinalIgnoreCase)) return "Legion";
        var match = EncounterPointName.Match(name);
        if (!match.Success) return null;
        string kind = match.Groups[1].Success ? match.Groups[1].Value : match.Groups["numbered"].Value;
        return kind.ToLowerInvariant() switch {
            "strongbox" => "Strongbox", "torment" => "Torment", "legion" => "Legion", "breach" => "Breach",
            "harbinger" => "Harbinger", "essence" => "Essence", "expedition" => "Expedition", "delirium" => "Delirium", _ => null
        };
    }

    public static bool IsPackPoint(string name) => name.StartsWith("%area%-", StringComparison.OrdinalIgnoreCase) || name is "normal" or "magic" or "rare";
}

/// <summary>Read-only pack candidates from the extracted tables. This does not plan or simulate monster spawns.</summary>
public sealed class MapSpawnCatalog
{
    sealed record Row(MapSpawnPack Pack, IReadOnlyDictionary<int, int> Areas);
    readonly List<Row> _rows = [];
    readonly Dictionary<int, HashSet<int>> _mapPacks = [];
    readonly List<string> _warnings = [];
    public IReadOnlyList<string> Warnings => _warnings;

    public static MapSpawnCatalog Load(string tables, IReadOnlyList<MapSpawnMonster> monsters)
    {
        var result = new MapSpawnCatalog();
        var entries = new Dictionary<int, HashSet<int>>();
        if (Read("MonsterPackEntries.json") is { } entryData)
            using (entryData)
                foreach (var row in entryData.RootElement.EnumerateArray())
                    if (Number(row, "MonsterPacksKey", -1) is >= 0 and var pack && Number(row, "MonsterVarietiesKey", -1) is >= 0 and var monster
                        && monster < monsters.Count && Number(row, "Weight") > 0)
                    {
                        if (!entries.TryGetValue(pack, out var members)) entries[pack] = members = [];
                        members.Add(monster);
                    }
        if (Read("MonsterPacks.json") is { } packData)
            using (packData)
                foreach (var row in packData.RootElement.EnumerateArray())
                {
                    int index = result._rows.Count;
                    var areas = new Dictionary<int, int>();
                    int[] areaKeys = Numbers(row, "WorldAreasKeys"), weights = Numbers(row, "__col9");
                    for (int i = 0; i < areaKeys.Length && weights.Length > 0; i++) areas[areaKeys[i]] = weights[Math.Min(i, weights.Length - 1)];
                    var leaders = Numbers(row, "BossMonster_MonsterVarietiesKeys").Where(i => i >= 0 && i < monsters.Count).Distinct().Select(i => monsters[i]).ToArray();
                    result._rows.Add(new(new(row.GetProperty("Id").GetString() ?? "", Number(row, "MinCount"), Number(row, "MaxCount"),
                        entries.GetValueOrDefault(index, []).Order().Select(i => monsters[i]).ToArray(), leaders,
                        Number(row, "BossMonsterSpawnChance"), Number(row, "BossMonsterCount")), areas));
                }
        if (Read("Maps.json", required: false) is { } mapData)
            using (mapData)
                foreach (var row in mapData.RootElement.EnumerateArray())
                    if (Number(row, "Regular_WorldAreasKey", -1) is >= 0 and var area)
                    {
                        if (!result._mapPacks.TryGetValue(area, out var ids)) result._mapPacks[area] = ids = [];
                        ids.UnionWith(Numbers(row, "MonsterPacksKeys"));
                    }
        return result;

        JsonDocument? Read(string name, bool required = true)
        {
            string path = Path.Combine(tables, name);
            if (!File.Exists(path)) { if (required) result._warnings.Add(name); return null; }
            try
            {
                var document = JsonDocument.Parse(File.ReadAllBytes(path));
                if (document.RootElement.ValueKind == JsonValueKind.Array) return document;
                document.Dispose();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
            result._warnings.Add(name);
            return null;
        }
    }

    static int Number(JsonElement row, string key, int fallback = 0) => row.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int n) ? n : fallback;
    static int[] Numbers(JsonElement row, string key) => row.TryGetProperty(key, out var values) && values.ValueKind == JsonValueKind.Array
        ? values.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.Number).Select(v => v.GetInt32()).ToArray() : [];

    public MapAreaPacks ForArea(string areaId, int areaRow)
    {
        var mapPacks = _mapPacks.GetValueOrDefault(areaRow, []);
        var scattered = _rows.Select((row, index) => (row, index))
            .Where(p => !p.row.Pack.Id.StartsWith(areaId + "-", StringComparison.OrdinalIgnoreCase)
                && (p.row.Areas.GetValueOrDefault(areaRow) > 0 || mapPacks.Contains(p.index)))
            .Select(p => p.row.Pack.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var packs = _rows.Where(r => r.Pack.Monsters.Count > 0 && (scattered.Contains(r.Pack.Id) || r.Pack.Id.StartsWith(areaId + "-", StringComparison.OrdinalIgnoreCase)))
            .Select(r => r.Pack).DistinctBy(p => p.Id, StringComparer.OrdinalIgnoreCase).ToArray();
        return new(packs, packs.Where(p => scattered.Contains(p.Id)).Select(p => p.Id).ToArray());
    }
}
