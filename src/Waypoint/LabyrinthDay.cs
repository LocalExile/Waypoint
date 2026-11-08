using System.Globalization;
using System.Text.Json;

namespace Waypoint;

/// <summary>A secret a room holds: its LabyrinthSecrets Id and name, and the number a key shares with its door.</summary>
sealed record LabSecret(string Id, string Name, int Number)
{
    /// <summary>What it is on the map, or null for one the map does not show.</summary>
    public LabFeature? Feature => LabFeatures.Of(Id);
}

/// <param name="Slot">Its section, 0-2.</param>
/// <param name="X">Its map position within the section: 0-3 from the way in to Izaro.</param>
/// <param name="Y">0-2, top to bottom.</param>
/// <param name="Links">The rooms it leads to; -1 is the way out (the plaza before the first room, Izaro's last fight after
/// the last).</param>
/// <param name="Izaro">On a section's last room, which of Izaro's fights it holds (1-3), else 0.</param>
sealed record LabRoom(int Index, string? Area, string? Name, int Slot, int Place, double X, double Y, IReadOnlyList<int> Links,
    IReadOnlyList<LabSecret> Secrets, int Izaro)
{
    /// <summary>Its number on the map (1-based, left to right).</summary>
    public int Number { get; internal set; }
}

/// <summary>One tier's Labyrinth of the day.</summary>
sealed class LabTier(int tier, string name, IReadOnlyList<LabRoom> rooms)
{
    public int Tier { get; } = tier;
    public string Name { get; } = name;
    public IReadOnlyList<LabRoom> Rooms { get; } = rooms;

    /// <summary>The rooms on a shortest way from the first room to the last (every one when several are as short), and the
    /// connections between them, each as (lower index, higher index).</summary>
    public (HashSet<int> Rooms, HashSet<(int, int)> Links, int Length) ShortestWay()
    {
        if (Rooms.Count == 0) return ([], [], 0);
        int last = Rooms.Count - 1;
        int[] from = Distances(0), to = Distances(last);
        int length = from[last];
        var rooms = new HashSet<int>();
        var links = new HashSet<(int, int)>();
        if (length < 0) return (rooms, links, 0);
        for (int i = 0; i < Rooms.Count; i++)
        {
            if (from[i] < 0 || to[i] < 0 || from[i] + to[i] != length) continue;
            rooms.Add(i);
            foreach (int l in Neighbours(i))
                if (from[l] == from[i] + 1 && to[l] == to[i] - 1) links.Add((Math.Min(i, l), Math.Max(i, l)));
        }
        return (rooms, links, length + 1);
    }

    // The layout lists a connection on both of its rooms; one listed on a single room still counts both ways.
    IEnumerable<int> Neighbours(int room) => Rooms[room].Links.Where(l => l >= 0 && l < Rooms.Count)
        .Concat(Rooms.Where(r => r.Links.Contains(room)).Select(r => r.Index)).Distinct();

    int[] Distances(int start)
    {
        var distance = Enumerable.Repeat(-1, Rooms.Count).ToArray();
        var queue = new Queue<int>();
        distance[start] = 0;
        queue.Enqueue(start);
        while (queue.TryDequeue(out int room))
            foreach (int next in Neighbours(room))
                if (distance[next] < 0) { distance[next] = distance[room] + 1; queue.Enqueue(next); }
        return distance;
    }

    /// <summary>Where each of a room's connections leaves its circle on the map, a schematic side rather than a door's
    /// place: angles in degrees, counter-clockwise from the right. In an Izaro room the doors within its section are on
    /// the left and the way on (the next section, the way out) on the right. In any other room the way in (the plaza's
    /// door, else the neighbour nearest the start) is at the bottom (6 o'clock), and the other doors each at one of 10, 12
    /// and 2 o'clock, in the left-to-right order of their neighbours on the map.</summary>
    public IReadOnlyDictionary<int, IReadOnlyDictionary<int, double>> DoorSides()
    {
        var sides = new Dictionary<int, IReadOnlyDictionary<int, double>>();
        if (Rooms.Count == 0) return sides;
        int[] start = Distances(0);
        int Far(int to) => start[to] < 0 ? int.MaxValue : start[to];
        foreach (var room in Rooms)
        {
            var links = room.Links.Where(l => l < Rooms.Count).Distinct().ToList();
            var placed = new Dictionary<int, double>();
            // The way in: the plaza's door in the first room, else the neighbour nearest the start.
            int? way = room.Index == 0 && links.Contains(-1) ? -1
                : links.Where(l => l >= 0).OrderBy(Far).ThenBy(l => l).Cast<int?>().FirstOrDefault();
            if (room.Izaro > 0)
            {
                // The way on: the next section's room, or the way out; every other door is back into its own section.
                var on = links.Where(l => l < 0 || Rooms[l].Slot > room.Slot).ToList();
                // Two on one side at 10 and 8 o'clock (2 and 4 on the right), the upper neighbour higher; more an hour apart.
                Spread(placed, [.. links.Except(on).OrderByDescending(l => Upward(room, l))], 180);
                Spread(placed, [.. on.OrderByDescending(l => Upward(room, l))], 0);
            }
            else
            {
                if (way is int inWay) placed[inWay] = 270;
                // The other doors left to right by where their neighbours are on the map (one below the room counts as far
                // left or far right), each at its own of 10, 12 and 2 o'clock: the order kept, as near its direction as
                // can be. A fourth or fifth door shares the place nearest its direction.
                var doors = links.Where(l => l != way)
                    .Select(l => (To: l, Toward: Angle(room, l) is double a ? (a > 270 ? a - 360 : a) : 90))
                    .OrderByDescending(d => d.Toward).ToList();
                double[] hours = [150, 90, 30];
                int[] best = [];
                double bestCost = double.MaxValue;
                int count = Math.Min(doors.Count, 3);
                foreach (int[] pick in Choose(3, count))
                {
                    double cost = pick.Select((h, i) => Math.Abs(hours[h] - Math.Clamp(doors[i].Toward, 30, 150))).Sum();
                    if (cost < bestCost) { bestCost = cost; best = pick; }
                }
                for (int i = 0; i < doors.Count; i++)
                    placed[doors[i].To] = i < count ? hours[best[i]]
                        : hours.OrderBy(h => Math.Abs(h - Math.Clamp(doors[i].Toward, 30, 150))).First();
            }
            sides[room.Index] = placed;
        }
        return sides;
    }

    // Doors on one side of an Izaro room: one at the side, two an hour above and below it, more an hour apart.
    static void Spread(Dictionary<int, double> placed, List<int> doors, double side)
    {
        double step = doors.Count == 2 ? 60 : 30, up = side == 0 ? 1 : -1;
        for (int i = 0; i < doors.Count; i++) placed[doors[i]] = Normalise(side - up * (i - (doors.Count - 1) / 2.0) * step);
    }

    // How far above the room a neighbour is on the map (the way out counts as level).
    double Upward(LabRoom room, int to) => to < 0 || to >= Rooms.Count ? 0 : room.Y - Rooms[to].Y;

    // Every increasing choice of k of 0..n-1.
    static IEnumerable<int[]> Choose(int n, int k, int from = 0)
    {
        if (k == 0) { yield return []; yield break; }
        for (int i = from; i <= n - k; i++)
            foreach (int[] rest in Choose(n, k - 1, i + 1)) yield return [i, .. rest];
    }

    static double Normalise(double degrees) => ((degrees % 360) + 360) % 360;

    /// <summary>The direction of a neighbour from a room on the map, in degrees counter-clockwise from the right, or null
    /// for the way out.</summary>
    double? Angle(LabRoom room, int target)
    {
        if (target < 0 || target >= Rooms.Count) return null;
        var other = Rooms[target];
        double dx = (other.Slot - room.Slot) * 4 + other.X - room.X, dy = other.Y - room.Y;
        return dx == 0 && dy == 0 ? null : Normalise(Math.Atan2(-dy, dx) * 180 / Math.PI);
    }

    /// <summary>The map's numbers of the rooms holding a feature.</summary>
    public IReadOnlyList<int> Holding(LabFeature feature) =>
        [.. Rooms.Where(r => r.Secrets.Any(s => s.Feature == feature)).Select(r => r.Number).Order()];

    /// <summary>Izaro's mechanics in each fight (1-3), in the order the rooms hold them.</summary>
    public IReadOnlyList<(int Fight, IReadOnlyList<string> Mechanics)> IzaroFights() =>
        [.. Rooms.Where(r => r.Izaro > 0).OrderBy(r => r.Izaro).Select(r => (r.Izaro,
            (IReadOnlyList<string>)[.. r.Secrets.Where(s => s.Feature is null).Select(s => LabFeatures.Mechanic(s.Id)).Distinct()]))];
}

/// <summary>
/// The day's Labyrinths from the Gateway itself (<c>GameServer.Gateway labyrinth [yyyy-mm-dd]</c>, the server's
/// <c>LabyrinthDay.Listing</c>): the layout the client builds from the day's seed, each room's area as the official server
/// picks it, its map position, connections and secrets. Nothing is downloaded: the checkout's own Gateway builds it from
/// the client's data.
/// </summary>
sealed class LabyrinthDay
{
    public uint Seed { get; }
    /// <summary>The UTC day, or null when the server's seed is fixed (<c>Game_LAB_SEED</c>).</summary>
    public DateOnly? Date { get; }
    public IReadOnlyList<LabTier> Tiers { get; }

    LabyrinthDay(uint seed, DateOnly? date, IReadOnlyList<LabTier> tiers) { Seed = seed; Date = date; Tiers = tiers; }

    static readonly Dictionary<(DateTime Built, DateOnly Day), LabyrinthDay> Loaded = [];

    /// <summary>The UTC day the Labyrinth is on now; a new layout comes at UTC midnight.</summary>
    public static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);

    /// <summary>A day's Labyrinths from the Gateway this checkout built, asked once per build and day.</summary>
    public static async Task<LabyrinthDay> LoadAsync(DateOnly day)
    {
        string dll = Repo.ServerDll("Gateway");
        if (!File.Exists(dll)) throw new FileNotFoundException(L.Text("{0} is not built yet: start the servers once (live.ps1 builds it)", dll));
        DateTime built = File.GetLastWriteTimeUtc(dll);
        lock (Loaded) if (Loaded.TryGetValue((built, day), out var known)) return known;
        // A Gateway built before the hook does not know `labyrinth` and would start for real: the saves, Auth, the port.
        // /lab_layout's date came with it, and `commands` is safe on every Gateway with the launcher hooks.
        if (!HasLayouts(await CommandCatalog.LoadAsync())) throw new InvalidOperationException(NoLayouts);
        var loaded = Parse(await CommandCatalog.AskGateway(TimeSpan.FromSeconds(60), dll, "labyrinth", day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
        if (loaded.Tiers.Count == 0) throw new InvalidOperationException(NoLayouts);
        lock (Loaded) Loaded[(built, day)] = loaded;
        return loaded;
    }

    static string NoLayouts => L.Text("This checkout's Gateway cannot list the Labyrinth's layouts yet: update LocalExile, then play or start the servers once to build it.");

    /// <summary>Whether this Gateway build has the `labyrinth` mode: its /lab_layout takes a date.</summary>
    internal static bool HasLayouts(CommandCatalog commands) =>
        commands.Of("/lab_layout")?.Usage.Contains("yyyy-mm-dd", StringComparison.Ordinal) == true;

    /// <summary>The `labyrinth` mode's JSON lines; other lines (the Gateway's own log) are skipped.</summary>
    internal static LabyrinthDay Parse(string jsonLines)
    {
        uint seed = 0;
        DateOnly? date = null;
        var names = new SortedDictionary<int, string>();
        var rooms = new Dictionary<int, List<LabRoom>>();
        foreach (string line in jsonLines.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!line.StartsWith('{')) continue;
            using var d = JsonDocument.Parse(line);
            var e = d.RootElement;
            switch (e.GetProperty("type").GetString())
            {
                case "day":
                    seed = e.GetProperty("seed").GetUInt32();
                    if (e.TryGetProperty("date", out var day) && DateOnly.TryParseExact(day.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                        date = parsed;
                    break;
                case "tier":
                    names[e.GetProperty("tier").GetInt32()] = e.GetProperty("name").GetString() ?? "";
                    break;
                case "room":
                    int tier = e.GetProperty("tier").GetInt32();
                    if (!rooms.TryGetValue(tier, out var list)) rooms[tier] = list = [];
                    list.Add(new LabRoom(e.GetProperty("index").GetInt32(), Text(e, "area"), Text(e, "name"), e.GetProperty("slot").GetInt32(),
                        e.GetProperty("place").GetInt32(), e.GetProperty("x").GetDouble(), e.GetProperty("y").GetDouble(),
                        [.. e.GetProperty("links").EnumerateArray().Select(l => l.GetInt32())],
                        [.. e.GetProperty("secrets").EnumerateArray().Select(s => new LabSecret(Text(s, "id") ?? "", Text(s, "name") ?? "", s.GetProperty("number").GetInt32()))],
                        e.GetProperty("izaro").GetInt32()));
                    break;
            }
        }
        var tiers = new List<LabTier>();
        foreach (var (tier, name) in names)
        {
            var list = rooms.GetValueOrDefault(tier, []).OrderBy(r => r.Index).ToList();
            // Numbered as the map reads, section by section, left to right, then top to bottom.
            int number = 1;
            foreach (var room in list.OrderBy(r => r.Slot).ThenBy(r => r.X).ThenBy(r => r.Y)) room.Number = number++;
            tiers.Add(new LabTier(tier, name, list));
        }
        return new LabyrinthDay(seed, date, tiers);
    }

    static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

/// <summary>What a room's secret is on the map: the ones a player looks for. Izaro's mechanics (the secrets on a section's
/// last room) are listed per fight instead.</summary>
enum LabFeature { Argus, TrapGauntlet, EscortGauntlet, Puzzle, Darkshrine, SilverKey, SilverCache, GoldKey, GoldDoor, Reliquary, SecretPassage }

static class LabFeatures
{
    // LabyrinthSecrets Ids. "Hidden" is a Darkshrine: on poelab.com's maps the Darkshrines are in exactly the rooms holding it.
    public static LabFeature? Of(string id) => id switch
    {
        "Argus" => LabFeature.Argus,
        "Gauntlet" => LabFeature.TrapGauntlet,
        "Payload" => LabFeature.EscortGauntlet,
        "Puzzle" => LabFeature.Puzzle,
        "Hidden" => LabFeature.Darkshrine,
        "SilverKey" => LabFeature.SilverKey,
        "SilverDoorReward" => LabFeature.SilverCache,
        "GoldKey" => LabFeature.GoldKey,
        "GoldDoor" or "GoldDoorModal" => LabFeature.GoldDoor,
        "Trinket" => LabFeature.Reliquary,
        "SecretPassage" => LabFeature.SecretPassage,
        _ => null,
    };

    public static string Label(LabFeature feature) => feature switch
    {
        LabFeature.Argus => L.Text("Argus"),
        LabFeature.TrapGauntlet => L.Text("Trap gauntlet"),
        LabFeature.EscortGauntlet => L.Text("Escort gauntlet"),
        LabFeature.Puzzle => L.Text("Switch puzzle"),
        LabFeature.Darkshrine => L.Text("Darkshrine"),
        LabFeature.SilverKey => L.Text("Silver key"),
        LabFeature.SilverCache => L.Text("Silver cache"),
        LabFeature.GoldKey => L.Text("Gold key"),
        LabFeature.GoldDoor => L.Text("Gold door"),
        LabFeature.Reliquary => L.Text("Forgotten Reliquary"),
        _ => L.Text("Secret passage"),
    };

    /// <summary>An Izaro mechanic's name from its Id (ChargeDisruptorsEndgame1 → Charge disruptors). The client's own words
    /// where it has them: Power Pylons are the Fonts (of Lethargy, Fragility, Elements), Portals summon Izaro's Guardians.
    /// A mechanic's "2" (its companion) carries it into the later fights.</summary>
    public static string Mechanic(string id)
    {
        string key = id.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
        foreach (string tier in (string[])["Normal", "Cruel", "Merciless", "Endgame"])
            if (key.EndsWith(tier, StringComparison.Ordinal)) key = key[..^tier.Length];
        return key switch
        {
            "Lieutenants" => L.Text("Lieutenants"),
            "Gargoyles" => L.Text("Gargoyles"),
            "PowerSymbols" => L.Text("Power symbols"),
            "ToggleStatues" => L.Text("Toggle statues"),
            "PowerPylons" => L.Text("Fonts"),
            "Elemental" => L.Text("Elemental orbs"),
            "Portals" => L.Text("Guardian portals"),
            "ChronoStatues" => L.Text("Chrono statues"),
            "Pedestals" => L.Text("Pedestals"),
            "ChargeDisruptors" => L.Text("Charge disruptors"),
            "BossFriend" => L.Text("A second boss"),
            _ => key,
        };
    }
}
