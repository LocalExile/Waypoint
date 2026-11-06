using MaterialDesignThemes.Wpf;

namespace Waypoint;

static class MapMarkers
{
    public static IReadOnlyList<MapMarker> FindAll(IReadOnlyList<MapMarker> markers, double x, double y, double radius) =>
        markers.Reverse().Select(o => (Marker: o, Distance: Math.Sqrt(Math.Pow(o.X / 23.0 - x, 2) + Math.Pow(o.Y / 23.0 - y, 2))))
            .Where(o => o.Distance <= radius).OrderBy(o => o.Distance).Select(o => o.Marker).ToArray();
    public static MapMarker? Find(IReadOnlyList<MapMarker> markers, double x, double y, double radius) => FindAll(markers, x, y, radius).FirstOrDefault();
    public static string Caption(MapMarker marker) => marker.Key.Kind == "object" ? Path.GetFileName(marker.Label) : marker.Label;

    public static List<MapMarker> Create(MapData map, bool internalMarkers = false, bool monsterPacks = false, bool encounters = false, bool landmarks = true)
    {
        var markers = new List<MapMarker>();
        if (landmarks)
        {
            for (int i = 0; i < map.Objects.Count; i++)
            {
                var o = map.Objects[i];
                if ((!internalMarkers && MapContent.IsInternalObject(o.Type)) || MapContent.EncounterObject(o.Type) is not null) continue;
                bool waypoint = o.Type.Equals("Metadata/MiscellaneousObjects/Waypoint", StringComparison.OrdinalIgnoreCase);
                bool crafting = o.Type.Equals("Metadata/MiscellaneousObjects/CraftRecipeUnlockMarker", StringComparison.OrdinalIgnoreCase);
                bool transition = o.Type.StartsWith("Metadata/MiscellaneousObjects/AreaTransition", StringComparison.OrdinalIgnoreCase);
                if (transition && map.Exits.Any(e => Math.Abs(e.X - o.X) <= 4 && Math.Abs(e.Y - o.Y) <= 4)) continue;
                bool npc = o.Type.StartsWith("Metadata/NPC/", StringComparison.OrdinalIgnoreCase);
                markers.Add(new(new("object", i), o.X, o.Y, o.Type, waypoint ? PackIconKind.MapMarker : crafting ? PackIconKind.BookOpenPageVariant : npc ? PackIconKind.Account : PackIconKind.CubeOutline,
                    npc ? "Shell.Good" : waypoint ? "Shell.Warning" : "Shell.Accent", npc || waypoint || crafting));
            }
            for (int i = 0; i < map.Points.Count; i++)
            {
                var p = map.Points[i];
                markers.Add(new(new("point", i), p.X, p.Y, p.Display, PackIconKind.Skull, "Shell.Error"));
            }
            for (int i = 0; i < map.Entrances.Count; i++)
            {
                var p = map.Entrances[i];
                string label = L.Text("Entrance {0}", p.Number);
                if (!p.Enabled) label = L.Text("{0} (arrival only)", label);
                markers.Add(new(new("entrance", i), p.X, p.Y, label, PackIconKind.DoorOpen, p.Enabled ? "Shell.Warning" : "Shell.TextSecondary"));
            }
            for (int i = 0; i < map.Exits.Count; i++)
            {
                var p = map.Exits[i];
                if (!internalMarkers && p.Kind == "Unused") continue;
                string label = p.Kind == "Teleport" ? L.Text("Area teleport") : p.Kind == "Unused" ? L.Text("Unused exit") : L.Text("Entrance and exit");
                label += $" {p.Number} · {p.Destination ?? p.Label}";
                if (!p.Enabled) label = L.Text("{0} (disabled)", label);
                markers.Add(new(new("exit", i), p.X, p.Y, label, p.Kind == "Teleport" ? PackIconKind.SwapHorizontal : PackIconKind.DoorOpen,
                    p.Enabled && p.Kind != "Unused" ? "Shell.Warning" : "Shell.TextSecondary"));
            }
        }
        if (monsterPacks)
            for (int i = 0; i < (map.PackAnchors?.Count ?? 0); i++)
            {
                var p = map.PackAnchors![i];
                markers.Add(new(new("pack", i), p.X, p.Y, MapPacks.Label(map, p), PackIconKind.AccountGroup,
                    p.Rarity == "rare" ? "Shell.Warning" : p.Rarity == "normal" ? "Shell.TextSecondary" : "Shell.Accent"));
            }
        if (encounters)
            for (int i = 0; i < (map.Encounters?.Count ?? 0); i++)
            {
                var p = map.Encounters![i];
                markers.Add(new(new("encounter", i), p.X, p.Y, MapEncounters.Label(p.Kind), PackIconKind.DiamondOutline, "Shell.Good", true));
            }
        return markers;
    }
}

static class MapPacks
{
    public static IReadOnlyList<MapSpawnPack> Candidates(MapData map, MapPackAnchor anchor) => (map.MonsterPacks ?? [])
        .Where(p => anchor.PackIds.Contains(p.Id, StringComparer.OrdinalIgnoreCase)).ToArray();

    public static string Label(MapData map, MapPackAnchor anchor)
    {
        if (anchor.Rarity is not null) return anchor.Rarity switch {
            "normal" => L.Text("Normal pack anchor"), "magic" => L.Text("Magic pack anchor"), "rare" => L.Text("Rare pack anchor"), _ => L.Text("Monster pack")
        };
        var names = Candidates(map, anchor).SelectMany(p => p.Monsters).Select(m => string.IsNullOrWhiteSpace(m.Name) ? m.Id : m.Name).Distinct().ToArray();
        return names.Length == 0 ? L.Text("Unresolved pack anchor") : L.Text("Monster pack: {0}", string.Join(" / ", names.Take(2)) + (names.Length > 2 ? " …" : ""));
    }

    public static IReadOnlyList<string> Describe(MapData map, MapPackAnchor anchor, bool sourceDetails)
    {
        var details = new List<string> { L.Text("Room-authored anchor; actual monsters and positions may differ.") };
        var packs = Candidates(map, anchor);
        if (packs.Count == 0) details.Add(L.Text("No matching pack data is available for this anchor."));
        foreach (var pack in packs)
        {
            details.Add("");
            if (packs.Count > 1 || sourceDetails) details.Add(pack.Id);
            details.Add(L.Text("Base pack size: {0}–{1}", pack.MinCount, pack.MaxCount));
            details.Add(L.Text("Possible monsters: {0}", string.Join(" / ", pack.Monsters.Select(m => string.IsNullOrWhiteSpace(m.Name) ? m.Id : m.Name).Distinct())));
            if (pack.LeaderChance > 0 && pack.LeaderCount > 0 && pack.Leaders.Count > 0)
                details.Add(L.Text("Possible pack leaders: {0}", string.Join(" / ", pack.Leaders.Select(m => string.IsNullOrWhiteSpace(m.Name) ? m.Id : m.Name).Distinct())));
        }
        if (sourceDetails) { details.Add(""); details.Add(anchor.Name); details.Add(anchor.Room); }
        return details;
    }
}

static class MapEncounters
{
    public static string Label(string kind) => kind switch {
        "Shrine" => L.Text("Shrine"), "Strongbox" => L.Text("Strongbox"), "Torment" => L.Text("Tormented spirit"), "Exile" => L.Text("Rogue exile"),
        "Legion" => L.Text("Legion"), "Breach" => L.Text("Breach"), "Harbinger" => L.Text("Harbinger"), "Essence" => L.Text("Essence"),
        "Expedition" => L.Text("Expedition"), "Delirium" => L.Text("Delirium"), "Blight" => L.Text("Blight"), _ => L.Text("Encounter")
    };
}

static class MapDetails
{
    public static string Format(MapData map)
    {
        var lines = new List<string> {
            L.Text("Area: {0} ({1})", map.Area, map.Id),
            L.Text("Seed: {0}", map.Seed?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? L.Text("Unknown")),
            L.Text("Area level: {0}", map.Level?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? L.Text("Unknown")),
            L.Text("Layout: {0} × {1} tiles", map.Width, map.Height),
            L.Text("Checkout revision: {0}", string.IsNullOrEmpty(map.CheckoutHead?.Commit) ? L.Text("Unknown") : map.CheckoutHead.Commit),
            L.Text("Working files: {0}", map.CheckoutHead?.HasTrackedChanges switch { true => L.Text("Tracked files modified"), false => L.Text("Tracked files unchanged"), _ => L.Text("Unknown") })
        };
        if (map.RequestedSeed is { } requested && requested != map.Seed)
            lines.Insert(2, L.Text("Requested seed: {0}", requested.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        lines.Add(L.Text("Warnings: {0}", map.Warnings.Count == 0 ? L.Text("None") : string.Join("; ", map.Warnings)));
        if (map.PackWarnings is { Count: > 0 }) lines.Add(L.Text("Monster pack data unavailable or incomplete: {0}", string.Join("; ", map.PackWarnings)));
        return string.Join(Environment.NewLine, lines);
    }
}
