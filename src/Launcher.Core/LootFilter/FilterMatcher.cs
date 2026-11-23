using System.Globalization;
using System.Text.RegularExpressions;

namespace Launcher.Core.LootFilter;

/// <summary>An item as the filter sees it, for the editor's drop preview and item import. Anything not set is what a plain
/// drop has: no mods, influence, sockets or special state.</summary>
public sealed class SimItem
{
    public required string BaseType { get; init; }
    public required string Class { get; init; }
    public string Rarity { get; init; } = "Normal";
    public int ItemLevel { get; init; } = 1;
    public int DropLevel { get; init; } = 1;
    public int StackSize { get; init; } = 1;
    public int Width { get; init; } = 1;
    public int Height { get; init; } = 1;
    public int Quality { get; init; }
    public int MapTier { get; init; }
    public int GemLevel { get; init; }
    public int BaseArmour { get; init; }
    public int BaseEvasion { get; init; }
    public int BaseEnergyShield { get; init; }
    public bool Identified { get; init; }
    public bool Corrupted { get; init; }
    public bool Mirrored { get; init; }
    /// <summary>Linked groups of socket colours (R, G, B, W, A, D), one string per group: "RGB", "B".</summary>
    public List<string> SocketGroups { get; init; } = [];
    public List<string> Influences { get; init; } = [];
    /// <summary>The names of the item's prefixes and suffixes ("Merciless", "of the Ice"), as an advanced copy shows them.</summary>
    public List<string> ExplicitMods { get; init; } = [];
    /// <summary>Boolean conditions that hold for the item: FracturedItem, SynthesisedItem, Replica, AnyEnchantment …</summary>
    public HashSet<string> Flags { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Other numeric conditions: HasSearingExarchImplicit, HasEaterOfWorldsImplicit, MemoryStrands …</summary>
    public Dictionary<string, int> Numbers { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public int Sockets => SocketGroups.Sum(g => g.Length);
    public int LinkedSockets => SocketGroups.Count == 0 ? 0 : SocketGroups.Max(g => g.Length);
}

/// <summary>What the filter does with an item: the deciding rule's Show, Hide or Minimal, every action that applies (a
/// Continue rule's actions first, then the later rules' over them), and the rules that matched, in order.</summary>
public sealed record FilterMatch(string Visibility, IReadOnlyDictionary<string, string> Actions, IReadOnlyList<FilterBlock> Rules);

/// <summary>
/// Runs an item through a filter as the game does: rules in order, disabled ones skipped; the first that matches decides,
/// unless it has Continue, whose actions apply and the search goes on. An item no rule matches is shown as the game's default.
/// Conditions the item does not say anything about (enchantment names, cluster passives) are those of a plain drop.
/// </summary>
public static partial class FilterMatcher
{
    static readonly string[] Rarities = ["Normal", "Magic", "Rare", "Unique"];

    public static FilterMatch Match(FilterDocument filter, SimItem item, int areaLevel)
    {
        var actions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var matched = new List<FilterBlock>();
        foreach (var block in filter.Blocks)
        {
            if (block.Disabled || !Matches(block, item, areaLevel)) continue;
            matched.Add(block);
            foreach (var line in block.Lines.Where(l => FilterSyntax.IsAction(l.Keyword) && l.Keyword != "Continue"))
                actions[line.Keyword] = line.Value;
            if (block.Find("Continue") is null) return new FilterMatch(block.Action, actions, matched);
        }
        return new FilterMatch(matched.Count > 0 ? matched[^1].Action : "Show", actions, matched);
    }

    public static bool Matches(FilterBlock block, SimItem item, int areaLevel) =>
        block.Lines.Where(l => !l.IsBlank && !FilterSyntax.IsAction(l.Keyword)).All(l => Condition(l.Keyword, l.Value, item, areaLevel));

    static bool Condition(string keyword, string value, SimItem item, int areaLevel)
    {
        var m = Operator().Match(value);
        string op = m.Groups[1].Value;
        var values = Values(m.Groups[2].Value);
        bool negate = op is "!=" or "!";
        switch (keyword)
        {
            case "BaseType":
                return negate ^ values.Any(v => op is "==" ? v.Equals(item.BaseType, StringComparison.OrdinalIgnoreCase)
                    : item.BaseType.Contains(v, StringComparison.OrdinalIgnoreCase));
            case "Class":
                return negate ^ values.Any(v => op is "==" ? v.Equals(item.Class, StringComparison.OrdinalIgnoreCase)
                    : item.Class.Contains(v, StringComparison.OrdinalIgnoreCase));
            case "Rarity":
            {
                int have = Array.IndexOf(Rarities, item.Rarity);
                return values.Any(v => Array.IndexOf(Rarities, v) is >= 0 and var want && Compare(op, have, want));
            }
            case "HasInfluence":
                return negate ^ values.Any(v => v.Equals("None", StringComparison.OrdinalIgnoreCase)
                    ? item.Influences.Count == 0 : item.Influences.Contains(v, StringComparer.OrdinalIgnoreCase));
            case "HasExplicitMod":
            {
                // "HasExplicitMod >=2 "Merciless" "Tyrannical"" counts the listed names the item has; without a count, one is enough.
                bool counted = values.Count > 0 && int.TryParse(values[0], out _);
                var names = counted ? values.Skip(1) : values;
                int have = names.Count(n => item.ExplicitMods.Contains(n, StringComparer.OrdinalIgnoreCase));
                return counted ? Compare(op, have, int.Parse(values[0], CultureInfo.InvariantCulture)) : negate ^ have > 0;
            }
            case "HasEnchantment" or "HasImplicitMod" or "EnchantmentPassiveNode" or "ArchnemesisMod" when values.Count > 0 && !IsBool(values[0]):
            {
                bool counted = int.TryParse(values[0], out int want);
                return counted ? Compare(op, 0, want) : negate;
            }
            case "SocketGroup":
                return negate ^ values.Any(v => item.SocketGroups.Any(g => Fits(g, v)));
            case "Sockets" or "LinkedSockets" when values.Any(v => v.Any(char.IsLetter)):
            {
                string all = keyword == "Sockets" ? string.Concat(item.SocketGroups) : item.SocketGroups.MaxBy(g => g.Length) ?? "";
                return negate ^ values.Any(v => Fits(all, v));
            }
        }
        if (values.Count == 1 && IsBool(values[0]))
            return Bool(keyword, item) == values[0].Equals("True", StringComparison.OrdinalIgnoreCase);
        if (values.Count > 0 && values.All(v => int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)))
        {
            int have = Number(keyword, item, areaLevel);
            return values.Any(v => Compare(op, have, int.Parse(v, CultureInfo.InvariantCulture)));
        }
        // A list of names the item says nothing about (enchantments, cluster passives …): it has none.
        return negate;
    }

    /// <summary>Whether sockets <paramref name="have"/> ("RGBB") hold a spec: "5", "RGB", "5GGG" (at least five, three green).</summary>
    static bool Fits(string have, string spec)
    {
        int count = int.TryParse(new string(spec.TakeWhile(char.IsDigit).ToArray()), out int n) ? n : 0;
        if (have.Length < count) return false;
        return spec.Where(char.IsLetter).GroupBy(char.ToUpperInvariant).All(g => have.Count(c => char.ToUpperInvariant(c) == g.Key) >= g.Count());
    }

    static bool IsBool(string v) => v.Equals("True", StringComparison.OrdinalIgnoreCase) || v.Equals("False", StringComparison.OrdinalIgnoreCase);

    static bool Bool(string keyword, SimItem item) => keyword switch
    {
        "Identified" => item.Identified,
        "Corrupted" => item.Corrupted,
        "Mirrored" => item.Mirrored,
        _ => item.Flags.Contains(keyword),
    };

    static int Number(string keyword, SimItem item, int areaLevel) => keyword switch
    {
        "AreaLevel" => areaLevel,
        "ItemLevel" => item.ItemLevel,
        "DropLevel" => item.DropLevel,
        "StackSize" => item.StackSize,
        "Quality" => item.Quality,
        "Sockets" => item.Sockets,
        "LinkedSockets" => item.LinkedSockets,
        "Width" => item.Width,
        "Height" => item.Height,
        "MapTier" => item.MapTier,
        "GemLevel" => item.GemLevel,
        "BaseArmour" => item.BaseArmour,
        "BaseEvasion" => item.BaseEvasion,
        "BaseEnergyShield" => item.BaseEnergyShield,
        _ => item.Numbers.GetValueOrDefault(keyword),
    };

    static bool Compare(string op, int have, int want) => op switch
    {
        "<" => have < want,
        "<=" => have <= want,
        ">" => have > want,
        ">=" => have >= want,
        "!=" or "!" => have != want,
        _ => have == want,
    };

    static List<string> Values(string text)
    {
        var values = new List<string>();
        foreach (Match m in Token().Matches(text)) values.Add(m.Groups[1].Success ? m.Groups[1].Value : m.Value);
        return values;
    }

    [GeneratedRegex(@"^\s*(==|!=|<=|>=|=|<|>|!)?\s*(.*)$")] private static partial Regex Operator();
    [GeneratedRegex("\"([^\"]*)\"|\\S+")] private static partial Regex Token();
}
