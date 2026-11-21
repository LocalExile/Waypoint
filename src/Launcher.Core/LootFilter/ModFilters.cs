using System.Text.Json;
using System.Text.RegularExpressions;

namespace Launcher.Core.LootFilter;

/// <summary>A base type a mod drops or makes worth more, and the NeverSink tier it belongs in ("currency;t2divine").
/// Class, size and drop level describe a base the editor's catalog does not have, for the drop preview and item import.</summary>
public sealed record ModFilterItem(string BaseType, string Tier, string? Class = null, int Width = 1, int Height = 1, int DropLevel = 1);

/// <summary>A whole rule a mod adds at the top of the filter: its lines as in a .filter file, header first.</summary>
public sealed record ModFilterRule(string Id, string Title, string? Description, List<string> Lines);

/// <summary>One mod's loot filter part, read from the <c>loot-filter.json</c> beside its <c>mod.json</c>.</summary>
public sealed record ModFilter(string ModId, string ModName, List<ModFilterItem> Items, List<ModFilterRule> Rules);

/// <summary>
/// The convention through which a mod tells the loot filter editor what it changes: a <c>loot-filter.json</c> beside its
/// <c>mod.json</c>, read for every mod the checkout has on (Mods/Binaries, with mod-state.txt's choices). Its
/// <c>items</c> go into their tiers before the player's changes, so the player sees and moves them like NeverSink's own;
/// an item the player's changes leave out of every tier of its type goes back to the mod's. Its <c>rules</c> go at the top
/// of the filter, in mod order, between marker lines, and show in the editor's Mods chapter, where the player can restyle
/// or turn each off. A mod that writes its rule into .filter files itself between such marker lines keeps it: the editor
/// carries those lines over when it writes the file again.
/// </summary>
public static partial class ModFilters
{
    public const string FileName = "loot-filter.json";

    static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    sealed record FileShape(List<ModFilterItem>? Items, List<ModFilterRule>? Rules);
    sealed record ManifestShape(string? Name, bool? Enabled);

    public static string RuleId(string mod, string rule) => $"mod:{mod}/{rule}";
    static string Type(string mod) => "mod:" + mod;
    static string Start(string mod) => $"# >>> {mod} (LocalExile mod, from its {FileName}) >>>";
    static string End(string mod) => $"# <<< {mod} <<<";

    /// <summary>The loot filter parts of the mods the checkout has on, by mod id. A file that cannot be used is left out and
    /// named in <paramref name="problems"/>.</summary>
    public static List<ModFilter> Read(string? checkout, List<string> problems)
    {
        var mods = new List<ModFilter>();
        string folder = checkout is { Length: > 0 } ? Path.Combine(checkout, "Mods", "Binaries") : "";
        if (folder.Length == 0 || !Directory.Exists(folder)) return mods;
        var choices = ModCatalog.ReadChoices(folder);
        foreach (string dir in Directory.EnumerateDirectories(folder).Order(StringComparer.Ordinal).Take(512))
        {
            string id = Path.GetFileName(dir), file = Path.Combine(dir, FileName);
            if (!ModCatalog.ValidId(id) || !File.Exists(file)) continue;
            try
            {
                string manifestFile = Path.Combine(dir, "mod.json");
                var manifest = File.Exists(manifestFile) ? JsonSerializer.Deserialize<ManifestShape>(File.ReadAllText(manifestFile), Json) : null;
                if (!choices.GetValueOrDefault(id, manifest?.Enabled ?? true)) continue;
                var shape = JsonSerializer.Deserialize<FileShape>(File.ReadAllText(file), Json) ?? new(null, null);
                var items = shape.Items ?? [];
                var rules = shape.Rules ?? [];
                foreach (var item in items)
                    if (string.IsNullOrWhiteSpace(item.BaseType) || item.Tier?.Contains(';') != true)
                        throw new InvalidDataException("every item needs a baseType and a tier (\"type;tier\")");
                foreach (var rule in rules)
                    if (!ModCatalog.ValidId(rule.Id ?? "") || rule.Lines is not { Count: > 0 } || !Header().IsMatch(rule.Lines[0]))
                        throw new InvalidDataException("every rule needs an id and lines that start with Show, Hide or Minimal");
                if (rules.Select(r => r.Id).Distinct().Count() != rules.Count) throw new InvalidDataException("two rules have the same id");
                mods.Add(new ModFilter(id, manifest?.Name is { Length: > 0 } name ? name : id, items, rules));
            }
            catch (Exception e) when (e is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
            {
                problems.Add($"{id}/{FileName}: {e.Message}");
            }
        }
        return mods;
    }

    /// <summary>The editor's rules for the mods' rules, in a chapter of their own, each mod a section.</summary>
    public static OptionChapter? Chapter(IReadOnlyList<ModFilter> mods, string title)
    {
        var sections = mods.Where(m => m.Rules.Count > 0 || m.Items.Count > 0).Select(m => (OptionNode)new OptionSection(m.ModName, null,
            [
                .. m.Items.Count > 0 ? [new OptionText("Description", string.Join(", ", m.Items.Select(i => $"{i.BaseType} → {i.Tier.Replace(';', ' ')}")))] : Array.Empty<OptionNode>(),
                .. m.Rules.Select(r => (OptionNode)new OptionRule(RuleId(m.ModId, r.Id), $"{Type(m.ModId)};{r.Id}", 0, "SHD", [], r.Title, null, "", "BaseType", r.Description)),
            ])).ToList();
        return sections.Count > 0 ? new OptionChapter(title, sections) : null;
    }

    /// <summary>Puts the mods' rules at the top of <paramref name="filter"/> and their items into their tiers. A tier the
    /// filter does not have is named in <paramref name="problems"/>.</summary>
    public static void Apply(FilterDocument filter, IReadOnlyList<ModFilter> mods, List<string> problems)
    {
        var parts = new List<object>();
        foreach (var mod in mods.Where(m => m.Rules.Count > 0))
        {
            parts.Add(new FilterText(Start(mod.ModId)));
            foreach (var rule in mod.Rules)
            {
                var source = FilterDocument.Parse(string.Join("\n", rule.Lines) + "\n").Blocks.First();
                var block = new FilterBlock { Action = source.Action, HeaderRest = $" # $type->{Type(mod.ModId)} $tier->{rule.Id}" };
                block.Lines.AddRange(source.Lines.Where(l => !l.IsBlank).Select(l => new FilterLine(l.Keyword, l.Value)));
                parts.Add(block);
            }
            parts.Add(new FilterText(End(mod.ModId)));
            parts.Add(new FilterText(""));
        }
        filter.Parts.InsertRange(0, parts);
        Place(filter, mods, problems, missingOnly: false);
    }

    /// <summary>After the player's changes: an item that is in no tier of its type goes back to the mod's tier.</summary>
    public static void Restore(FilterDocument filter, IReadOnlyList<ModFilter> mods) => Place(filter, mods, [], missingOnly: true);

    static void Place(FilterDocument filter, IReadOnlyList<ModFilter> mods, List<string> problems, bool missingOnly)
    {
        var byKey = filter.ByKey();
        foreach (var mod in mods)
            foreach (var item in mod.Items)
            {
                if (!byKey.TryGetValue(item.Tier, out var target) || target.Find("BaseType") is null)
                {
                    problems.Add($"{mod.ModId}: {item.BaseType} → {item.Tier}: no such tier list in this filter");
                    continue;
                }
                var sameType = filter.Blocks.Where(b => b.Type == target.Type && b.Find("BaseType") is not null).ToList();
                bool listed = sameType.Any(b => FilterSyntax.List(b.Find("BaseType")!.Value).Values.Contains(item.BaseType, StringComparer.OrdinalIgnoreCase));
                if (missingOnly && listed) continue;
                // As the editor's "Move to": out of the type's other tiers, into the mod's.
                foreach (var block in sameType.Where(b => b != target))
                {
                    var (op, values) = FilterSyntax.List(block.Find("BaseType")!.Value);
                    if (values.RemoveAll(v => v.Equals(item.BaseType, StringComparison.OrdinalIgnoreCase)) == 0) continue;
                    block.Set("BaseType", values.Count > 0 ? FilterSyntax.Quote(op, values) : null);
                    if (values.Count == 0) block.Disabled = true;
                }
                var (targetOp, targetValues) = FilterSyntax.List(target.Find("BaseType")!.Value);
                if (!targetValues.Contains(item.BaseType, StringComparer.OrdinalIgnoreCase))
                    target.Set("BaseType", FilterSyntax.Quote(targetOp, [.. targetValues, item.BaseType]));
            }
    }

    /// <summary>The marker blocks (<c># &gt;&gt;&gt; id …&gt;&gt;&gt;</c> to <c># &lt;&lt;&lt; id &lt;&lt;&lt;</c>) a mod wrote into a .filter
    /// file itself. Left out: the blocks the editor wrote from a loot-filter.json (written again, or gone with their mod),
    /// and those of <paramref name="own"/>, mods whose loot-filter.json the editor uses now.</summary>
    public static List<string> Foreign(string text, IEnumerable<string> own)
    {
        var skip = new HashSet<string>(own, StringComparer.Ordinal);
        var lines = text.Replace("\r", "").Split('\n');
        var kept = new List<string>();
        for (int i = 0; i < lines.Length; i++)
        {
            if (Marker().Match(lines[i].Trim()) is not { Success: true } start) continue;
            string id = start.Groups[1].Value, end = End(id);
            int to = Array.FindIndex(lines, i + 1, l => l.Trim() == end);
            if (to < 0) continue;
            if (!skip.Contains(id) && !lines[i].Contains($"from its {FileName}", StringComparison.Ordinal)) { kept.AddRange(lines[i..(to + 1)]); kept.Add(""); }
            i = to;
        }
        return kept;
    }

    /// <summary>The catalog with the mods' own bases added, for the drop preview and item import.</summary>
    public static ItemCatalog Catalog(ItemCatalog catalog, IReadOnlyList<ModFilter> mods) =>
        catalog.With(mods.SelectMany(m => m.Items).Where(i => i.Class is { Length: > 0 } && !catalog.ByName.ContainsKey(i.BaseType))
            .Select(i => new CatalogItem(i.Class!, i.BaseType, i.DropLevel, i.Width, i.Height, 0, 0, 0)));

    [GeneratedRegex(@"^\s*(Show|Hide|Minimal)\b")] private static partial Regex Header();
    [GeneratedRegex(@"^# >>> ([A-Za-z0-9][A-Za-z0-9_.-]*)\b.*>>>$")] private static partial Regex Marker();
}
