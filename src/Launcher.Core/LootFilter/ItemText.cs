using System.Globalization;
using System.Text.RegularExpressions;

namespace Launcher.Core.LootFilter;

/// <summary>What an item copied in game says about itself, as the filter sees it, and what it could not tell.</summary>
/// <summary>A note is an English text for L.Text with at most one {0}, and its argument.</summary>
public sealed record ImportedItem(SimItem Item, string? Name, List<(string Text, string? Arg)> Notes);

/// <summary>
/// Reads the text the game puts on the clipboard for Ctrl+C (and Ctrl+Alt+C, which adds the mod names) over an item:
/// "Item Class:", "Rarity:", the name lines, then sections split by "--------". The base type is checked against the
/// catalog, which also gives the base's drop level, size and defences.
/// </summary>
public static partial class ItemText
{
    static readonly string[] InfluenceNames = ["Shaper", "Elder", "Crusader", "Redeemer", "Hunter", "Warlord"];
    static readonly string[] EldritchLevels = ["Lesser", "Greater", "Grand", "Exceptional", "Exquisite", "Perfect"];

    public static bool Looks(string text) => text.TrimStart().StartsWith("Item Class:", StringComparison.OrdinalIgnoreCase);

    public static ImportedItem Parse(string text, ItemCatalog catalog)
    {
        var lines = text.Replace("\r", "").Split('\n').Select(l => l.Trim()).ToList();
        var sections = new List<List<string>> { new() };
        foreach (var l in lines)
            if (l.StartsWith("--------", StringComparison.Ordinal)) sections.Add([]);
            else if (l.Length > 0) sections[^1].Add(l);
        var head = sections[0];
        string? Field(string name) => lines.FirstOrDefault(l => l.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase)) is { } l ? l[(name.Length + 1)..].Trim() : null;

        string? cls = Field("Item Class");
        string rarityText = Field("Rarity") ?? "Normal";
        string rarity = rarityText is "Magic" or "Rare" or "Unique" ? rarityText : "Normal";
        var names = head.SkipWhile(l => !l.StartsWith("Rarity:", StringComparison.OrdinalIgnoreCase)).Skip(1).ToList();
        var notes = new List<(string, string?)>();

        // Two name lines are a name and a base type (rare, unique); one is the base type itself, maybe with a magic item's
        // affixes or a prefix such as "Superior" around it.
        string line = names.LastOrDefault() ?? "";
        CatalogItem? base_ = catalog.ByName.GetValueOrDefault(line)
            ?? catalog.ByName.Values.Where(c => Contains(line, c.BaseType)).MaxBy(c => c.BaseType.Length);
        if (base_ is null) notes.Add(("\"{0}\" is not a base type the editor knows; the filter sees it as written.", line));
        string? name = names.Count > 1 ? names[0] : null;

        var all = string.Join('\n', lines);
        var item = new SimItem
        {
            BaseType = base_?.BaseType ?? line,
            Class = cls ?? base_?.Class ?? "",
            Rarity = rarity,
            ItemLevel = Int(Field("Item Level")) ?? 1,
            DropLevel = base_?.DropLevel ?? 1,
            StackSize = Field("Stack Size") is { } stack ? Int(stack.Split('/')[0]) ?? 1 : 1,
            Width = base_?.Width ?? 1,
            Height = base_?.Height ?? 1,
            Quality = Int(Field("Quality")) ?? 0,
            MapTier = Int(Field("Map Tier")) ?? 0,
            // A gem's level is the first "Level:" outside its requirements.
            GemLevel = cls?.Contains("Gem", StringComparison.OrdinalIgnoreCase) == true
                ? sections.Skip(1).Where(s => !s.Contains("Requirements:")).SelectMany(s => s).Select(l => LevelLine().Match(l))
                    .FirstOrDefault(m => m.Success) is { } level ? int.Parse(level.Groups[1].Value, CultureInfo.InvariantCulture) : 0
                : 0,
            BaseArmour = base_?.Armour ?? 0,
            BaseEvasion = base_?.Evasion ?? 0,
            BaseEnergyShield = base_?.EnergyShield ?? 0,
            Identified = !lines.Contains("Unidentified"),
            Corrupted = lines.Contains("Corrupted"),
            Mirrored = lines.Contains("Mirrored"),
            SocketGroups = Field("Sockets") is { } sockets
                ? sockets.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(g => g.Replace("-", "")).ToList() : [],
            Influences = InfluenceNames.Where(i => lines.Contains($"{i} Item")).ToList(),
            ExplicitMods = ModHeader().Matches(all).Select(m => m.Groups[1].Value).ToList(),
        };
        void Flag(string keyword, bool on) { if (on) item.Flags.Add(keyword); }
        Flag("FracturedItem", lines.Contains("Fractured Item") || all.Contains("(fractured)"));
        Flag("SynthesisedItem", lines.Contains("Synthesised Item"));
        Flag("Replica", name?.StartsWith("Replica ", StringComparison.Ordinal) == true);
        Flag("AnyEnchantment", all.Contains("(enchant)"));
        Flag("HasImplicitMod", all.Contains("(implicit)") || all.Contains("Implicit Modifier"));
        Flag("BlightedMap", item.Class == "Maps" && item.BaseType.Contains("Blighted", StringComparison.Ordinal));
        Flag("UberBlightedMap", item.Class == "Maps" && item.BaseType.Contains("Blight-ravaged", StringComparison.Ordinal));
        Flag("Scourged", lines.Contains("Scourged"));
        Flag("TransfiguredGem", lines.Any(l => l.Contains("Transfigured", StringComparison.Ordinal)));
        foreach (var (keyword, label) in new[] { ("HasSearingExarchImplicit", "Searing Exarch"), ("HasEaterOfWorldsImplicit", "Eater of Worlds") })
            if (Regex.Match(all, label + @" Implicit Modifier \((\w+)\)") is { Success: true } e && Array.IndexOf(EldritchLevels, e.Groups[1].Value) is >= 0 and var tier)
                item.Numbers[keyword] = tier + 1;
        if (Int(Field("Memory Strands")) is int strands) item.Numbers["MemoryStrands"] = strands;

        if (rarity is "Magic" or "Rare" && item.Identified && item.ExplicitMods.Count == 0 && !all.Contains("{ "))
            notes.Add(("The copy has no mod names: copy with Ctrl+Alt+C for the rules that test mods.", null));
        return new ImportedItem(item, name, notes);
    }

    static bool Contains(string line, string baseType) =>
        line.Length >= baseType.Length && Regex.IsMatch(line, @"(^|\s)" + Regex.Escape(baseType) + @"($|\s)");

    static int? Int(string? text) =>
        text is null ? null : FirstNumber().Match(text.Replace(",", "").Replace(" ", "")) is { Success: true } m
            ? int.Parse(m.Value, CultureInfo.InvariantCulture) : null;

    [GeneratedRegex(@"\d+")] private static partial Regex FirstNumber();
    [GeneratedRegex(@"^Level: (\d+)")] private static partial Regex LevelLine();
    [GeneratedRegex(@"\{ [^{}]*?(?:Prefix|Suffix) Modifier ""([^""]+)""")] private static partial Regex ModHeader();
}
