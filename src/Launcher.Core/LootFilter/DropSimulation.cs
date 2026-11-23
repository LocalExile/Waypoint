using System.Globalization;
using System.Text;

namespace Launcher.Core.LootFilter;

/// <summary>A base type as FilterBlade's BaseTypes.csv describes it: the facts a filter can test without mods.</summary>
public sealed record CatalogItem(string Class, string BaseType, int DropLevel, int Width, int Height, int Armour, int Evasion, int EnergyShield);

/// <summary>Every base type the editor knows, from FilterBlade's BaseTypes.csv, by name.</summary>
public sealed class ItemCatalog
{
    public IReadOnlyDictionary<string, CatalogItem> ByName { get; }

    ItemCatalog(Dictionary<string, CatalogItem> byName) => ByName = byName;

    /// <summary>This catalog with more bases; a name it already has keeps its entry.</summary>
    public ItemCatalog With(IEnumerable<CatalogItem> more)
    {
        var byName = new Dictionary<string, CatalogItem>(ByName, StringComparer.OrdinalIgnoreCase);
        foreach (var item in more) byName.TryAdd(item.BaseType, item);
        return new ItemCatalog(byName);
    }

    public static ItemCatalog Parse(string csv)
    {
        var rows = Rows(csv).ToList();
        var header = rows[0];
        int Column(string name) => header.IndexOf(name);
        int cls = Column("Class"), name = Column("BaseType"), drop = Column("DropLevel"), height = Column("Height"), width = Column("Width");
        int armour = Column("Game:Armour"), evasion = Column("Game:Evasion"), shield = Column("Game:Energy Shield");
        int Int(List<string> row, int i) => i >= 0 && i < row.Count && int.TryParse(row[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : 0;
        var items = new Dictionary<string, CatalogItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows.Skip(1).Where(r => r.Count > name && r[name].Length > 0))
            items.TryAdd(row[name], new CatalogItem(row[cls], row[name], Int(row, drop), Math.Max(1, Int(row, width)), Math.Max(1, Int(row, height)),
                Int(row, armour), Int(row, evasion), Int(row, shield)));
        return new ItemCatalog(items);
    }

    /// <summary>RFC 4180 rows: commas inside quotes, doubled quotes.</summary>
    static IEnumerable<List<string>> Rows(string csv)
    {
        var row = new List<string>();
        var field = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < csv.Length; i++)
        {
            char c = csv[i];
            if (quoted)
            {
                if (c != '"') field.Append(c);
                else if (i + 1 < csv.Length && csv[i + 1] == '"') { field.Append('"'); i++; }
                else quoted = false;
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { row.Add(field.ToString()); field.Clear(); }
            else if (c == '\n')
            {
                row.Add(field.ToString().TrimEnd('\r')); field.Clear();
                yield return row;
                row = [];
            }
            else field.Append(c);
        }
        if (field.Length > 0 || row.Count > 0) { row.Add(field.ToString().TrimEnd('\r')); yield return row; }
    }
}

/// <summary>
/// Sample drops for the editor: items picked from the base filter's own tier lists (so "valuable currency" means what
/// NeverSink tiers high), never from the player's changes, which are what the sample is there to test.
/// </summary>
public static class DropSimulation
{
    public static readonly IReadOnlyList<string> Presets = ["currency", "valuable", "uniques", "cards", "mixed"];

    /// <summary>The base filter's tier lists: every BaseType in a rule of <paramref name="type"/>, with the rule's tier.</summary>
    static IEnumerable<(string Tier, string Item)> Listed(FilterDocument filter, string type, Func<string, bool>? tier = null) =>
        filter.Blocks.Where(b => b.Type == type && b.Tier is { } t && (tier?.Invoke(t) ?? true) && b.Action == "Show")
            .SelectMany(b => FilterSyntax.List(b.Find("BaseType")?.Value ?? "").Values.Select(v => (b.Tier!, v)));

    static readonly string[] Gear = ["Body Armours", "Boots", "Gloves", "Helmets", "Shields", "Rings", "Amulets", "Belts", "Bows", "Claws", "Daggers",
        "One Hand Axes", "One Hand Maces", "One Hand Swords", "Sceptres", "Staves", "Two Hand Axes", "Two Hand Maces", "Two Hand Swords", "Wands", "Quivers"];

    public static List<SimItem> Generate(string preset, int count, int areaLevel, int seed, FilterDocument filter, ItemCatalog catalog)
    {
        var random = new Random(seed);
        var drops = new List<SimItem>();
        SimItem? Make(string name, string rarity = "Normal", int stack = 1)
        {
            if (!catalog.ByName.TryGetValue(name, out var c)) return null;
            return new SimItem
            {
                BaseType = c.BaseType, Class = c.Class, Rarity = rarity, StackSize = stack, DropLevel = c.DropLevel,
                ItemLevel = Math.Max(1, areaLevel + random.Next(0, 3)), Width = c.Width, Height = c.Height,
                BaseArmour = c.Armour, BaseEvasion = c.Evasion, BaseEnergyShield = c.EnergyShield,
            };
        }
        // Lower tiers drop more often: tier n of a type weighs n squared, so a t9 shows up 81 times as often as a t1.
        List<(string Item, int Weight)> Weighted(IEnumerable<(string Tier, string Item)> listed) =>
            listed.Select(l => (l.Item, Weight: Math.Max(1, TierNumber(l.Tier) is int n and > 0 ? n * n : 40))).DistinctBy(l => l.Item).ToList();
        string Pick(List<(string Item, int Weight)> pool)
        {
            int roll = random.Next(pool.Sum(p => p.Weight));
            foreach (var (item, weight) in pool) if ((roll -= weight) < 0) return item;
            return pool[^1].Item;
        }

        var currency = Weighted(Listed(filter, "currency", t => t.StartsWith('t')));
        var cards = Weighted(Listed(filter, "divination", t => t.StartsWith('t')));
        var uniques = Weighted(Listed(filter, "uniques", t => t.StartsWith('t')));
        var gear = catalog.ByName.Values.Where(c => Gear.Contains(c.Class) && c.DropLevel <= areaLevel).Select(c => c.BaseType).ToList();
        int Stack() => random.Next(10) == 0 ? random.Next(2, 6) : 1;

        switch (preset)
        {
            case "valuable":
                // NeverSink's four top currency tiers taken in turn, so each shows up: the looks to compare side by side.
                var tiers = Listed(filter, "currency", t => t is "t1exalted" or "t2divine" or "t3annul" or "t4chaos")
                    .GroupBy(l => l.Tier).Select(g => g.Select(l => l.Item).Distinct().OrderBy(_ => random.Next()).ToList()).ToList();
                for (int i = 0; drops.Count < count && tiers.Any(t => i < t.Count); i++)
                    foreach (var tier in tiers)
                        if (i < tier.Count && drops.Count < count && Make(tier[i]) is { } valuable) drops.Add(valuable);
                break;
            default:
                for (int i = 0; drops.Count < count && i < count * 4; i++)
                {
                    SimItem? drop = preset switch
                    {
                        "currency" => currency.Count > 0 ? Make(Pick(currency), stack: Stack()) : null,
                        "uniques" => uniques.Count > 0 ? Make(Pick(uniques), "Unique") : null,
                        "cards" => cards.Count > 0 ? Make(Pick(cards)) : null,
                        _ => random.Next(100) switch
                        {
                            < 35 when currency.Count > 0 => Make(Pick(currency), stack: Stack()),
                            < 42 when cards.Count > 0 => Make(Pick(cards)),
                            < 45 when uniques.Count > 0 => Make(Pick(uniques), "Unique"),
                            var r when gear.Count > 0 => Make(gear[random.Next(gear.Count)], r < 60 ? "Rare" : r < 85 ? "Magic" : "Normal"),
                            _ => null,
                        },
                    };
                    if (drop is not null) drops.Add(drop);
                }
                break;
        }
        return drops;
    }

    /// <summary>Plain drops of named items, for the search's "where does this item go".</summary>
    public static List<SimItem> Named(IEnumerable<string> names, int areaLevel, ItemCatalog catalog) =>
        names.Select(n => catalog.ByName.TryGetValue(n, out var c) ? new SimItem
        {
            BaseType = c.BaseType, Class = c.Class, DropLevel = c.DropLevel, ItemLevel = areaLevel, Width = c.Width, Height = c.Height,
            BaseArmour = c.Armour, BaseEvasion = c.Evasion, BaseEnergyShield = c.EnergyShield,
        } : null).OfType<SimItem>().ToList();

    static int? TierNumber(string tier) => tier.Length > 1 && tier[0] == 't' && char.IsDigit(tier[1])
        ? int.Parse(new string(tier.Skip(1).TakeWhile(char.IsDigit).ToArray()), CultureInfo.InvariantCulture) : null;
}
