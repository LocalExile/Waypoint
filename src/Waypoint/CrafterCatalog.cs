using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Waypoint;

sealed record CraftStat(string Id, int Min, int Max);

/// <param name="Generation">Mods.dat GenerationType: 1 prefix, 2 suffix, 3 implicit, 5 corruption, 28/29 Eldritch …</param>
sealed record CraftMod(string Id, string Name, int Generation, int Domain, int Level, IReadOnlyList<int> Families,
    IReadOnlyList<string> Tags, IReadOnlyList<CraftStat> Stats)
{
    public bool Prefix => Generation == 1;
    public bool Suffix => Generation == 2;
    /// <summary>Tiers of one modifier share it: the same kind, families and stats.</summary>
    public string Group => $"{Generation}/{string.Join(',', Families)}/{string.Join(',', Stats.Select(s => s.Id))}";
}

/// <param name="Pools">"pool" (prefixes and suffixes), "crafted", "corrupted", "eldritch", and "shaper" … "warlord" (what
/// an influence adds).</param>
sealed record CraftBase(string Id, string Name, string Class, int Width, int Height, int DropLevel, IReadOnlyList<int> Requirements,
    int Sockets, bool Quality, IReadOnlyList<string> Implicits, IReadOnlyDictionary<string, int> Pools);

sealed record DescriptionVariant(IReadOnlyList<string> When, string Text, IReadOnlyList<(string Name, int Stat)> Handlers);

sealed record StatDescription(IReadOnlyList<string> Stats, IReadOnlyList<DescriptionVariant> Variants);

sealed record RareWord(int List, uint Key, string Text);

/// <summary>
/// What the item crafter offers, from the Gateway itself (<c>GameServer.Gateway crafter</c>, the server's
/// Agents/docs/dev/item-crafter.md): item classes, bases, the mods each base can have, how the game words their stats,
/// and the rare name words. Read once per Gateway build.
/// </summary>
sealed class CrafterCatalog
{
    public IReadOnlyDictionary<string, string> Classes { get; }
    public IReadOnlyList<CraftBase> Bases { get; }
    public IReadOnlyDictionary<string, CraftMod> Mods { get; }
    public IReadOnlyDictionary<int, IReadOnlyList<CraftMod>> Pools { get; }
    public IReadOnlyList<RareWord> Words { get; }
    readonly Dictionary<string, StatDescription> _descriptions;
    readonly Dictionary<int, Dictionary<string, int>> _tiers = [];

    /// <summary>When the Gateway the catalog came from was built.</summary>
    public DateTime Built { get; private set; }

    CrafterCatalog(Dictionary<string, string> classes, List<CraftBase> bases, Dictionary<string, CraftMod> mods,
        Dictionary<int, IReadOnlyList<CraftMod>> pools, Dictionary<string, StatDescription> descriptions, List<RareWord> words)
    {
        Classes = classes;
        Bases = bases;
        Mods = mods;
        Pools = pools;
        _descriptions = descriptions;
        Words = words;
    }

    static CrafterCatalog? _loaded;

    /// <summary>The catalog of the Gateway the servers run, read again after it was built again.</summary>
    public static async Task<CrafterCatalog> LoadAsync()
    {
        string dll = Repo.ServerDll("Gateway");
        if (!File.Exists(dll)) throw new FileNotFoundException(L.Text("{0} is not built yet: start the servers once (live.ps1 builds it)", dll));
        DateTime built = File.GetLastWriteTimeUtc(dll);
        if (_loaded is { } loaded && loaded.Built == built) return loaded;
        string output = await CommandCatalog.AskGateway(TimeSpan.FromSeconds(90), dll, "crafter");
        var catalog = Parse(output);
        if (catalog.Bases.Count == 0)
            throw new InvalidOperationException(L.Text("This checkout's Gateway has no item crafter: pull the server's main branch."));
        catalog.Built = built;
        return _loaded = catalog;
    }

    /// <summary>The `crafter` mode's JSON lines; other lines (the Gateway's own log) are skipped.</summary>
    internal static CrafterCatalog Parse(string jsonLines)
    {
        var classes = new Dictionary<string, string>(StringComparer.Ordinal);
        var bases = new List<CraftBase>();
        var mods = new Dictionary<string, CraftMod>(StringComparer.Ordinal);
        var poolIds = new Dictionary<int, List<string>>();
        var descriptions = new Dictionary<string, StatDescription>(StringComparer.Ordinal);
        var words = new List<RareWord>();
        foreach (string line in jsonLines.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!line.StartsWith('{')) continue;
            using var d = JsonDocument.Parse(line);
            var e = d.RootElement;
            switch (e.GetProperty("type").GetString())
            {
                case "class":
                    classes[Str(e, "id")] = Str(e, "name");
                    break;
                case "pool":
                    poolIds[e.GetProperty("id").GetInt32()] = [.. e.GetProperty("mods").EnumerateArray().Select(m => m.GetString()!)];
                    break;
                case "mod":
                    var mod = new CraftMod(Str(e, "id"), Str(e, "name"), Int(e, "generation"), Int(e, "domain"), Int(e, "level"),
                        [.. e.GetProperty("families").EnumerateArray().Select(f => f.GetInt32())],
                        [.. e.GetProperty("tags").EnumerateArray().Select(t => t.GetString()!)],
                        [.. e.GetProperty("stats").EnumerateArray().Select(s => new CraftStat(Str(s, "id"), Int(s, "min"), Int(s, "max")))]);
                    mods[mod.Id] = mod;
                    break;
                case "base":
                    var pools = new Dictionary<string, int>(StringComparer.Ordinal);
                    foreach (string key in (string[])["pool", "crafted", "corrupted", "eldritch"])
                        if (e.TryGetProperty(key, out var p)) pools[key] = p.GetInt32();
                    if (e.TryGetProperty("influence", out var influence))
                        foreach (var i in influence.EnumerateObject()) pools[i.Name] = i.Value.GetInt32();
                    bases.Add(new CraftBase(Str(e, "id"), Str(e, "name"), Str(e, "class"), Int(e, "width"), Int(e, "height"),
                        Int(e, "dropLevel"), [.. e.GetProperty("requirements").EnumerateArray().Select(r => r.GetInt32())], Int(e, "sockets"),
                        e.GetProperty("quality").GetBoolean(), [.. e.GetProperty("implicits").EnumerateArray().Select(m => m.GetString()!)], pools));
                    break;
                case "desc":
                    var description = new StatDescription([.. e.GetProperty("stats").EnumerateArray().Select(s => s.GetString()!)],
                        [.. e.GetProperty("variants").EnumerateArray().Select(v => new DescriptionVariant(
                            [.. v.GetProperty("when").EnumerateArray().Select(w => w.GetString()!)], Str(v, "text"),
                            [.. v.GetProperty("handlers").EnumerateArray().Select(h => (h[0].GetString()!, h[1].GetInt32()))]))]);
                    foreach (string stat in description.Stats) descriptions.TryAdd(stat, description);
                    break;
                case "word":
                    words.Add(new RareWord(Int(e, "list"), e.GetProperty("key").GetUInt32(), Str(e, "text")));
                    break;
            }
        }
        var resolved = poolIds.ToDictionary(p => p.Key, p => (IReadOnlyList<CraftMod>)[.. p.Value.Select(mods.GetValueOrDefault).OfType<CraftMod>()]);
        return new(classes, bases, mods, resolved, descriptions, words);
    }

    static string Str(JsonElement e, string name) => e.GetProperty(name).GetString() ?? "";
    static int Int(JsonElement e, string name) => e.GetProperty(name).GetInt32();

    public IReadOnlyList<CraftMod> Pool(CraftBase b, string key) =>
        b.Pools.TryGetValue(key, out int id) && Pools.TryGetValue(id, out var pool) ? pool : [];

    public StatDescription? Description(string stat) => _descriptions.GetValueOrDefault(stat);

    /// <summary>The mod's tier among the pool's mods of its group: 1 the highest level.</summary>
    public int Tier(int pool, CraftMod mod)
    {
        lock (_tiers)
        {
            if (!_tiers.TryGetValue(pool, out var tiers))
            {
                tiers = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var group in Pools.GetValueOrDefault(pool, []).GroupBy(m => m.Group))
                {
                    int tier = 1;
                    foreach (var m in group.OrderByDescending(m => m.Level)) tiers[m.Id] = tier++;
                }
                _tiers[pool] = tiers;
            }
            return tiers.GetValueOrDefault(mod.Id, 1);
        }
    }

    /// <summary>The other tiers of a mod's group in the same pool, best first.</summary>
    public IReadOnlyList<CraftMod> Tiers(int pool, CraftMod mod) =>
        [.. Pools.GetValueOrDefault(pool, []).Where(m => m.Group == mod.Group).OrderByDescending(m => m.Level)];

    /// <summary>The lines the game shows for a mod: with these values, or its roll ranges ("(71-85) to maximum Life").</summary>
    public IReadOnlyList<string> Lines(CraftMod mod, IReadOnlyList<int>? values = null) => ModText.Lines(mod, Description, values);
}

/// <summary>
/// The game's wording of a mod's stats from its stat descriptions (the server's Agents/docs/dev/item-crafter.md): each
/// description is used once, its first variant whose conditions the values meet gives the line, and handlers change a
/// value before it is shown.
/// </summary>
static class ModText
{
    public static IReadOnlyList<string> Lines(CraftMod mod, Func<string, StatDescription?> describe, IReadOnlyList<int>? values)
    {
        var lines = new List<string>();
        var done = new HashSet<StatDescription>(ReferenceEqualityComparer.Instance);
        foreach (var stat in mod.Stats)
        {
            if (describe(stat.Id) is not { } d || !done.Add(d)) continue;
            var ranges = d.Stats.Select(id =>
            {
                int i = IndexOf(mod, id);
                if (i < 0) return (0, 0);
                return values is null ? (mod.Stats[i].Min, mod.Stats[i].Max) : (i < values.Count ? (values[i], values[i]) : (mod.Stats[i].Min, mod.Stats[i].Max));
            }).ToArray();
            if (Format(d, ranges) is { Length: > 0 } line) lines.AddRange(line.Split('\n'));
        }
        return lines;
    }

    static int IndexOf(CraftMod mod, string stat)
    {
        for (int i = 0; i < mod.Stats.Count; i++) if (mod.Stats[i].Id == stat) return i;
        return -1;
    }

    static readonly Regex Placeholder = new(@"\{(\d*)(?::([^}]*))?\}", RegexOptions.Compiled);

    /// <summary>The line for these values (min and max of each stat, the same for a rolled mod), or null when no variant
    /// applies (a value of 0 for a description that only words other values).</summary>
    internal static string? Format(StatDescription d, IReadOnlyList<(int Min, int Max)> ranges)
    {
        var variant = d.Variants.FirstOrDefault(v => Matches(v, ranges.Select(r => r.Max).ToList()))
            ?? d.Variants.FirstOrDefault(v => Matches(v, ranges.Select(r => r.Min).ToList()));
        if (variant is null) return null;
        int next = 0;
        return Placeholder.Replace(variant.Text, m =>
        {
            int index = m.Groups[1].Length > 0 ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : next++;
            if (index >= ranges.Count) return m.Value;
            bool plus = m.Groups[2].Value.StartsWith('+');
            var handlers = variant.Handlers.Where(h => h.Stat == index + 1).Select(h => h.Name).ToList();
            var (min, max) = ranges[index];
            double a = Handle(min, handlers, out int decimals, out bool fixedPoint), b = Handle(max, handlers, out _, out _);
            (a, b) = (Math.Min(a, b), Math.Max(a, b));
            string Number(double v) => v.ToString(fixedPoint ? "F" + decimals : "0." + new string('#', decimals), CultureInfo.InvariantCulture);
            if (a == b) return (plus && a > 0 ? "+" : "") + Number(a);
            if (a >= 0) return $"{(plus ? "+" : "")}({Number(a)}-{Number(b)})";
            if (b <= 0) return $"-({Number(-b)}-{Number(-a)})";
            return $"({Number(a)} to {Number(b)})";
        });
    }

    /// <summary>Every condition holds: "#" any, "5" exactly, "1|#" at least, "#|-1" at most, "1|5" between, "!0" not.</summary>
    static bool Matches(DescriptionVariant v, IReadOnlyList<int> values)
    {
        for (int i = 0; i < v.When.Count && i < values.Count; i++)
        {
            string c = v.When[i];
            int value = values[i];
            if (c == "#") continue;
            if (c.StartsWith('!')) { if (int.TryParse(c[1..], out int not) && value == not) return false; continue; }
            int bar = c.IndexOf('|');
            if (bar < 0) { if (int.TryParse(c, out int exact) && value != exact) return false; continue; }
            string lo = c[..bar], hi = c[(bar + 1)..];
            if (lo != "#" && int.TryParse(lo, out int min) && value < min) return false;
            if (hi != "#" && int.TryParse(hi, out int max) && value > max) return false;
        }
        return true;
    }

    /// <summary>A value through the description's handlers, and how many decimals it shows (fixed: always that many).</summary>
    static double Handle(int raw, IReadOnlyList<string> handlers, out int decimals, out bool fixedPoint)
    {
        double v = raw;
        decimals = 0;
        fixedPoint = false;
        foreach (string h in handlers)
        {
            (double factor, double add, int places) = h switch
            {
                "negate" => (-1, 0, 0),
                "negate_and_double" => (-2, 0, 0),
                "double" => (2, 0, 0),
                "times_twenty" => (20, 0, 0),
                "times_one_point_five" => (1.5, 0, 1),
                "30%_of_value" => (0.3, 0, 1),
                "60%_of_value" => (0.6, 0, 1),
                "plus_two_hundred" => (1, 200, 0),
                "multiplicative_damage_modifier" => (1, 100, 0),
                "divide_by_one_hundred_and_negate" => (-0.01, 0, 2),
                "permyriad_per_minute_to_%_per_second" => (1 / 6000d, 0, 2),
                "old_leech_percent" => (0.2, 0, 2),
                "old_leech_permyriad" => (1 / 500d, 0, 2),
                "locations_to_metres" => (0.1, 0, 1),
                "deciseconds_to_seconds" => (0.1, 0, 1),
                "divide_by_twenty_then_double_0dp" => (0.1, 0, 0),
                _ when h.StartsWith("divide_by_one_hundred", StringComparison.Ordinal) => (0.01, 0, 2),
                _ when h.StartsWith("divide_by_one_thousand", StringComparison.Ordinal) => (0.001, 0, 3),
                _ when h.StartsWith("per_minute_to_per_second", StringComparison.Ordinal) => (1 / 60d, 0, 1),
                _ when h.StartsWith("milliseconds_to_seconds", StringComparison.Ordinal) => (0.001, 0, 2),
                _ when h.StartsWith("divide_by_", StringComparison.Ordinal) => (1d / Divisor(h), 0, 1),
                _ => (1, 0, 0),
            };
            v = v * factor + add;
            decimals = Math.Max(decimals, places);
            // "_0dp", "_1dp", "_2dp" fix the decimals; "_if_required" only allows them.
            if (Regex.Match(h, @"_(\d)dp(_if_required)?$") is { Success: true } dp)
            {
                decimals = int.Parse(dp.Groups[1].Value, CultureInfo.InvariantCulture);
                fixedPoint = !dp.Groups[2].Success && decimals > 0;
            }
        }
        if (decimals == 0) v = Math.Round(v, MidpointRounding.ToZero);
        return Math.Round(v, decimals);
    }

    static double Divisor(string handler) => handler["divide_by_".Length..].Split('_')[0] switch
    {
        "two" => 2, "three" => 3, "four" => 4, "five" => 5, "six" => 6, "ten" => 10, "twelve" => 12, "fifteen" => 15,
        "twenty" => 20, "fifty" => 50, _ => 1,
    };
}
