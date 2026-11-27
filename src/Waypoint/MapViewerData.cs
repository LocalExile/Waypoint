namespace Waypoint;

// What the map viewer reads from its helper and shows, apart from its window: the Windows app and the Linux one (which links
// this file) share it. The area list's natural sort is each window's own (MapAreaComparer).

record MapArea(string Id, string Name, int[]? TopologiesKeys = null) { public string Display => $"{Name}  ({Id})"; }
static class MapAreaSearch
{
    public static bool Matches(string id, string name, string query) => query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
        .All(word => id.Contains(word, StringComparison.OrdinalIgnoreCase) || name.Contains(word, StringComparison.OrdinalIgnoreCase));
    public static MapArea? Resolve(IEnumerable<MapArea> areas, string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;
        var candidates = areas.ToList();
        return candidates.FirstOrDefault(a => a.Id.Equals(query.Trim(), StringComparison.OrdinalIgnoreCase)
            || a.Display.Equals(query.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? candidates.FirstOrDefault(a => Matches(a.Id, a.Name, query));
    }

    /// <summary>Runs of ASCII digits compare as numbers, everything else without case (as Windows' StrCmpLogicalW does).</summary>
    public static int Natural(string a, string b)
    {
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsAsciiDigit(a[i]) && char.IsAsciiDigit(b[j]))
            {
                int si = i, sj = j;
                while (i < a.Length && char.IsAsciiDigit(a[i])) i++;
                while (j < b.Length && char.IsAsciiDigit(b[j])) j++;
                string x = a[si..i].TrimStart('0'), y = b[sj..j].TrimStart('0');
                int byLength = x.Length.CompareTo(y.Length);
                if (byLength != 0) return byLength;
                int byDigits = string.CompareOrdinal(x, y);
                if (byDigits != 0) return byDigits;
                continue;
            }
            int c = char.ToUpperInvariant(a[i]).CompareTo(char.ToUpperInvariant(b[j]));
            if (c != 0) return c;
            i++; j++;
        }
        return (a.Length - i).CompareTo(b.Length - j);
    }
}
record MapListItem(string Label, string? Path, int? X, int? Y, MapMarkerKey? Marker = null);
record MapRequest(string AreaId, string Seed, string Level);
record MapTile(int X, int Y, string Path, int Orientation, int Height);
record MapCatalog(string Path, int Count);
record MapExit(int X, int Y, string Kind, int Number, string? Destination, string? Label,
    string? DestinationId = null, int? TargetX = null, int? TargetY = null, bool Enabled = true);
record MapEntrance(int X, int Y, int Number, string? Destination = null, string? DestinationId = null, bool Enabled = true);
record MapObject(int X, int Y, string Type, string? Model = null);
record MapBoss(string Id, string Name);
record MapPoint(int X, int Y, string Name, string? Room = null, IReadOnlyList<MapBoss>? Bosses = null, bool BossesAreCandidates = false)
{
    public string Display
    {
        get
        {
            string names = string.Join(" / ", (Bosses ?? []).Select(b => string.IsNullOrWhiteSpace(b.Name) ? b.Id : b.Name).Where(n => !string.IsNullOrWhiteSpace(n)));
            return names.Length == 0 ? Name : BossesAreCandidates ? L.Text("Possible: {0}", names) : names;
        }
    }
}
record MapData(string Area, string Id, int Width, int Height, int WalkWidth, int WalkHeight, byte[] Walk,
    List<MapTile> Tiles, List<MapCatalog> Catalog, List<MapExit> Exits, List<MapEntrance> Entrances, List<MapObject> Objects,
    List<MapPoint> Points, List<string> Warnings, uint? Seed = null, uint? RequestedSeed = null, int? Level = null,
    LocalRepositoryHead? CheckoutHead = null, List<MapPackAnchor>? PackAnchors = null, List<MapSpawnPack>? MonsterPacks = null,
    List<MapEncounterAnchor>? Encounters = null, List<string>? PackWarnings = null);

static class MapNavigation
{
    public static string? DestinationId(MapData map, MapMarkerKey key) => key switch
    {
        { Kind: "exit", Index: >= 0 } when key.Index < map.Exits.Count && map.Exits[key.Index].Kind == "Entrance" => map.Exits[key.Index].DestinationId,
        { Kind: "entrance", Index: >= 0 } when key.Index < map.Entrances.Count => map.Entrances[key.Index].DestinationId,
        _ => null
    };

    public static IEnumerable<(int X, int Y, int TargetX, int TargetY)> TeleportLinks(MapData map) => map.Exits
        .Where(e => e.Kind == "Teleport" && e.TargetX.HasValue && e.TargetY.HasValue)
        .Select(e => (e.X, e.Y, e.TargetX!.Value, e.TargetY!.Value));
}
