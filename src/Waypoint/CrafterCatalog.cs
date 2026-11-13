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

/// <summary>A mod a new item of a base has in its enchant list (the catalog's "enchants"): a value per stat, and the
/// range the base gives each value. A cluster jewel's passives come from these.</summary>
sealed record CraftEnchant(string Id, IReadOnlyList<int> Values, IReadOnlyList<(int Min, int Max)> Ranges);

/// <summary>What a cluster jewel's small passives are: one of its base's "themes".</summary>
/// <param name="Value">What the first value of the passives mod is for this theme.</param>
/// <param name="Stats">What a small passive grants (each stat's Min and Max are its one value).</param>
/// <param name="Pool">The affix pool of a jewel with this theme: its notables are in it.</param>
sealed record CraftTheme(int Value, string Name, string Tag, IReadOnlyList<CraftStat> Stats, int Pool);

/// <summary>What a cluster jewel base's enchantments can be changed to (the catalog's "cluster").</summary>
/// <param name="Passives">The mod whose values are the theme and the passive count.</param>
/// <param name="Sockets">The mod for each jewel socket count; none on a small jewel.</param>
sealed record CraftCluster(int Size, string Passives, int Min, int Max, IReadOnlyList<(int Count, string Mod)> Sockets,
    IReadOnlyList<CraftTheme> Themes);

/// <param name="Pools">"pool" (prefixes and suffixes), "crafted", "corrupted", "eldritch", and "shaper" … "warlord" (what
/// an influence adds).</param>
/// <param name="Enchants">Empty for most bases, and for every base of a Gateway that does not list them yet.</param>
sealed record CraftBase(string Id, string Name, string Class, int Width, int Height, int DropLevel, IReadOnlyList<int> Requirements,
    int Sockets, bool Quality, IReadOnlyList<string> Implicits, IReadOnlyDictionary<string, int> Pools, IReadOnlyList<CraftEnchant> Enchants,
    CraftCluster? Cluster = null);

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
        // A Gateway built before the crafter hook does not know `crafter` and starts for real: the saves, Auth, the port.
        // Its assembly says whether it has the hook (the ItemCrafter type), read without running it. /item_give, which
        // came with the hook, moved to the gm-commands mod in v0.4.0-beta, so `commands` no longer lists it everywhere.
        if (!HasCrafter(GatewayAssembly(dll))) throw new InvalidOperationException(NoCrafter);
        string output = await CommandCatalog.AskGateway(TimeSpan.FromSeconds(90), dll, "crafter");
        var catalog = Parse(output);
        if (catalog.Bases.Count == 0) throw new InvalidOperationException(NoCrafter);
        catalog.Built = built;
        return _loaded = catalog;
    }

    static string NoCrafter => L.Text("This checkout's Gateway has no item crafter yet: update LocalExile, then play or start the servers once to build it.");

    /// <summary>The Gateway's managed assembly: a release's <c>app\GameServer.Gateway.exe</c> (or the Linux host without an
    /// extension) is an apphost beside <c>GameServer.Gateway.dll</c>; a checkout's build is the .dll itself.</summary>
    internal static string GatewayAssembly(string gateway) =>
        gateway.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? gateway
        : gateway.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? gateway[..^4] + ".dll"
        : gateway + ".dll";

    /// <summary>Whether this Gateway build has the crafter hook: its assembly defines <c>ItemCrafter</c> (LocalExile's
    /// GameServer.Gateway/Item/ItemCrafter.cs, which came with the <c>crafter</c> mode). Read from the metadata alone.</summary>
    internal static bool HasCrafter(string assembly)
    {
        try
        {
            using var stream = File.OpenRead(assembly);
            using var pe = new System.Reflection.PortableExecutable.PEReader(stream);
            if (!pe.HasMetadata) return false;
            var metadata = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
            return metadata.TypeDefinitions.Any(t => metadata.StringComparer.Equals(metadata.GetTypeDefinition(t).Name, "ItemCrafter"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or BadImageFormatException or InvalidOperationException)
        {
            return false;
        }
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
                        e.GetProperty("quality").GetBoolean(), [.. e.GetProperty("implicits").EnumerateArray().Select(m => m.GetString()!)], pools,
                        e.TryGetProperty("enchants", out var enchants) ? [.. enchants.EnumerateArray().Select(Enchant)] : [],
                        e.TryGetProperty("cluster", out var cluster) ? Cluster(cluster) : null));
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

    /// <summary>One of a base's "enchants": {"id", "values", "ranges": [[min, max] …]}.</summary>
    static CraftEnchant Enchant(JsonElement e) => new(Str(e, "id"),
        [.. e.GetProperty("values").EnumerateArray().Select(v => v.GetInt32())],
        e.TryGetProperty("ranges", out var ranges) ? [.. ranges.EnumerateArray().Select(r => (r[0].GetInt32(), r[1].GetInt32()))] : []);

    /// <summary>A base's "cluster": {"size", "passives": {"mod", "min", "max"}, "sockets": [{"count", "mod"}],
    /// "themes": [{"value", "name", "tag", "stats": [{"id", "value"}], "pool"}]}.</summary>
    static CraftCluster Cluster(JsonElement e)
    {
        var passives = e.GetProperty("passives");
        return new(Int(e, "size"), Str(passives, "mod"), Int(passives, "min"), Int(passives, "max"),
            [.. e.GetProperty("sockets").EnumerateArray().Select(s => (Int(s, "count"), Str(s, "mod")))],
            [.. e.GetProperty("themes").EnumerateArray().Select(t => new CraftTheme(Int(t, "value"), Str(t, "name"), Str(t, "tag"),
                [.. t.GetProperty("stats").EnumerateArray().Select(s => new CraftStat(Str(s, "id"), Int(s, "value"), Int(s, "value")))], Int(t, "pool")))]);
    }

    static string Str(JsonElement e, string name) => e.GetProperty(name).GetString() ?? "";
    static int Int(JsonElement e, string name) => e.GetProperty(name).GetInt32();

    /// <summary>A base's pool, for an item with these mods: a cluster jewel's affixes ("pool") are its theme's, where its
    /// notables are; without a theme, and for every other pool, the base's own.</summary>
    public IReadOnlyList<CraftMod> Pool(CraftBase b, string key, IEnumerable<CraftedMod>? mods = null) =>
        Pools.TryGetValue(PoolId(b, key, mods), out var pool) ? pool : [];

    /// <summary>The id of that pool, or -1 when the base has none of the kind.</summary>
    public int PoolId(CraftBase b, string key, IEnumerable<CraftedMod>? mods = null) =>
        key == "pool" && mods is not null && Theme(b, mods) is { } theme ? theme.Pool : b.Pools.GetValueOrDefault(key, -1);

    /// <summary>The stat of a cluster jewel's passives mod whose value is the theme.</summary>
    public const string ThemeStat = "local_jewel_expansion_passive_node_index";

    /// <summary>Which of the passives mod's values is the theme, or -1 (not a cluster jewel, or a Gateway without it).</summary>
    public int ThemeIndex(CraftBase b) =>
        b.Cluster is { } cluster && Mods.GetValueOrDefault(cluster.Passives) is { } mod ? mod.Stats.ToList().FindIndex(s => s.Id == ThemeStat) : -1;

    /// <summary>The theme a cluster jewel's mods name, or null: no cluster jewel, or a value that is no theme of its size.</summary>
    public CraftTheme? Theme(CraftBase b, IEnumerable<CraftedMod> mods)
    {
        int at = ThemeIndex(b);
        return at >= 0 && mods.FirstOrDefault(m => m.Id == b.Cluster!.Passives) is { } passives && at < passives.Values.Count
            ? b.Cluster!.Themes.FirstOrDefault(t => t.Value == passives.Values[at]) : null;
    }

    /// <summary>The lines the game shows for a theme, which it words from the theme's value alone: "Added Small Passive
    /// Skills grant: 12% increased Fire Damage", one for each thing a small passive grants.</summary>
    public IReadOnlyList<string> ThemeLines(CraftTheme theme) =>
        [.. ModText.Lines(new CraftMod("", "", 0, 0, 0, [], [], theme.Stats), Description, null).Where(l => l.Length > 0).Select(l => "Added Small Passive Skills grant: " + l)];

    /// <summary>The lines of a mod on an item of the base, with its values. A cluster jewel's passives mod also reads
    /// with its theme's lines.</summary>
    public IReadOnlyList<string> Lines(CraftBase? b, CraftedMod m, CraftMod mod) =>
        b?.Cluster?.Passives == mod.Id && Theme(b, [m]) is { } theme ? [.. Lines(mod, m.Values), .. ThemeLines(theme)] : Lines(mod, m.Values);

    public StatDescription? Description(string stat) => _descriptions.GetValueOrDefault(stat);

    /// <summary>The mods a new item of the base has: its enchantments with the base's values (a cluster jewel adds no
    /// passive without them), then its implicits at their best.</summary>
    public List<CraftedMod> NewMods(CraftBase b) =>
        [.. b.Enchants.Where(e => Mods.ContainsKey(e.Id)).Select(e => new CraftedMod { Id = e.Id, Section = "enchant", Values = [.. e.Values] }),
         .. b.Implicits.Select(Mods.GetValueOrDefault).OfType<CraftMod>().Select(m => NewMod(b, m, "implicit"))];

    /// <summary>A mod as it is added to an item of the base: an enchantment of the base with the base's values, any other
    /// at the top of its range.</summary>
    public static CraftedMod NewMod(CraftBase? b, CraftMod mod, string section) =>
        section == "enchant" && b?.Enchants.FirstOrDefault(e => e.Id == mod.Id) is { } enchant
            ? new() { Id = mod.Id, Section = section, Values = [.. enchant.Values] }
            : new() { Id = mod.Id, Section = section, Values = [.. mod.Stats.Select(s => Math.Max(s.Min, s.Max))] };

    /// <summary>The range of one value of a mod on an item of the base: the base's for one of its enchantments (the game's
    /// data gives a cluster jewel's passive count no range of its own), else the mod's.</summary>
    public static (int Min, int Max) Range(CraftBase? b, CraftedMod m, CraftMod mod, int index)
    {
        if (m.Section == "enchant" && b?.Enchants.FirstOrDefault(e => e.Id == m.Id) is { } enchant && index < enchant.Ranges.Count)
            return enchant.Ranges[index];
        var stat = mod.Stats[index];
        return (Math.Min(stat.Min, stat.Max), Math.Max(stat.Min, stat.Max));
    }

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
        // The game words this value as the passive of that number (a cluster jewel's "Added Small Passive Skills grant: …"),
        // which the catalog does not hold: no line, rather than the bare number.
        if (variant.Handlers.Any(h => h.Name == "tree_expansion_jewel_passive")) return null;
        int next = 0;
        // A line break in a description is written "\n", as in the client's file.
        return Placeholder.Replace(variant.Text.Replace("\\n", "\n"), m =>
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
