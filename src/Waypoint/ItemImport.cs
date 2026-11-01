using System.Globalization;
using System.Text.RegularExpressions;

namespace Waypoint;

/// <summary>How the import took a line of the text.</summary>
enum ImportStatus { Skipped, Used, Mod, Unmatched }

/// <summary>A line of the text (0-based, as a text box counts them), how the import took it, and a word on it.</summary>
sealed record ImportLine(int Number, string Text, ImportStatus Status, string Note);

/// <summary>Why the text gives no item, and the line that says so (-1 for the whole text).</summary>
sealed class ImportException(string message, int line = -1) : FormatException(message)
{
    public int Line { get; } = line;
}

/// <summary>An imported item: what the crafter opens, its name, and every line of the text with how it was taken.</summary>
sealed record ImportedItem(CraftedItem Item, string Name, IReadOnlyList<ImportLine> Lines)
{
    public IReadOnlyList<ImportLine> Unmatched => [.. Lines.Where(l => l.Status == ImportStatus.Unmatched)];
}

/// <summary>
/// Reads an item copied as text, in either of two forms.
/// <list type="bullet">
/// <item>Path of Building's (Ctrl+C on an item): "Rarity: RARE", the name and base, "Key: value" lines (Prefix and Suffix
/// with the Mods.dat Id when the item was crafted in PoB, Quality, Sockets, Item Level), "Implicits: N", then the implicit
/// and explicit lines with PoB's {tags} in front and (min-max) rolled at their {range}.</item>
/// <item>The game's (Ctrl+C on an item): "Item Class:", "Rarity:", the name and base, sections between "--------" lines,
/// "Item Level:", then the modifiers, implicits marked "(implicit)". With Ctrl+Alt+C each modifier has a header
/// ("{ Prefix Modifier "Virile" (Tier: 1) — Life }") and each value its tier's range ("+105(100-114) to maximum Life").</item>
/// </list>
/// Every modifier line is matched against the catalog's mods as the game words them: the Ids PoB gave and the game's
/// ranges and affix names first, then the base's own pools, then any mod; the values are the ones that give the line's
/// numbers. Uniques are not read: the crafter makes normal, magic and rare items.
/// </summary>
static class ItemImport
{
    /// <summary>A line with its numbers taken out: its words with # for each number, and the numbers.</summary>
    internal readonly record struct Shape(string Words, double[] Numbers);

    /// <summary>A modifier line: where it is in the text, its words and numbers, the tier's ranges the game showed for it,
    /// the game's block of lines that make one modifier (-1 when the text has none), and what the text says of it.</summary>
    sealed record ModLine(int Number, string Text, Shape Shape, string? Range, int Block, bool Implicit, bool Crafted, bool Enchant,
        string? Affix, string? Kind);

    sealed record Candidate(CraftMod Mod, int[] Values, double Distance, bool Outside, int Rank, int Count, List<Shape> Window, bool Ranges);

    static readonly Regex Number = new(@"[+-]?\d+(?:\.\d+)?", RegexOptions.Compiled);
    static readonly Regex Range = new(@"([+-]?)\((-?\d+(?:\.\d+)?)-(-?\d+(?:\.\d+)?)\)", RegexOptions.Compiled);
    static readonly Regex Tag = new(@"^\{([^}]*)\}", RegexOptions.Compiled);
    static readonly Regex Field = new(@"^([A-Za-z][A-Za-z ]*):\s*(.*)$", RegexOptions.Compiled);
    // The game's Ctrl+Alt+C: a modifier's header, and a value with its tier's range ("105(100-114)", "10(10)").
    static readonly Regex Header = new(@"^\{\s*(?<kind>[^""(—}]+?)\s*(?:""(?<affix>[^""]*)"")?\s*(?:\((?<grade>[^)]*)\))?\s*(?:—[^}]*)?\}$", RegexOptions.Compiled);
    static readonly Regex GameValue = new(@"([+-]?)(\d+(?:\.\d+)?)\((-?\d+(?:\.\d+)?)(?:-(-?\d+(?:\.\d+)?))?\)", RegexOptions.Compiled);
    // The game's plain Ctrl+C marks a line's kind after it.
    static readonly Regex Marker = new(@"\s+\((implicit|crafted|enchant|fractured|scourge)\)$", RegexOptions.Compiled);

    static readonly string[] Influences = ["shaper", "elder", "crusader", "redeemer", "hunter", "warlord"];
    static readonly string[] Instructions = ["Right click", "Place into", "Note:", "Travel to", "Can only"];

    /// <summary>The item in the text; an <see cref="ImportException"/> says why there is none.</summary>
    public static ImportedItem Read(string text, CrafterCatalog catalog)
    {
        var source = text.ReplaceLineEndings("\n").Split('\n');
        var notes = new (ImportStatus Status, string? Note)[source.Length];
        string Line(int i) => source[i].Trim();
        void Note(int i, ImportStatus status, string note = "") => notes[i] = (status, note);

        int at = 0;
        while (at < source.Length && Line(at).Length == 0) at++;
        if (at >= source.Length) throw new ImportException(NoItem);
        bool game = Line(at).StartsWith("Item Class:", StringComparison.OrdinalIgnoreCase) || source.Any(l => l.Trim() == "--------");
        if (Line(at).StartsWith("Item Class:", StringComparison.OrdinalIgnoreCase)) Note(at++, ImportStatus.Used, L.Text("Item class"));

        // PoB writes no rarity for a unique from its list.
        string rarity = "unique";
        if (at < source.Length && Line(at).StartsWith("Rarity:", StringComparison.OrdinalIgnoreCase))
        {
            rarity = Line(at)["Rarity:".Length..].Trim().ToLowerInvariant();
            Note(at++, ImportStatus.Used, L.Text("Rarity"));
        }
        else if (game) throw new ImportException(NoItem, at);
        if (at >= source.Length) throw new ImportException(NoItem);
        if (rarity is not ("normal" or "magic" or "rare"))
            throw rarity is "unique" or "relic" ? new ImportException(L.Text("{0} is a unique: the crafter makes normal, magic and rare items.", Line(at)), at)
                : new ImportException(NoItem, at - 1);
        string name = "";
        if (rarity == "rare" && at + 1 < source.Length)
        {
            name = Line(at);
            Note(at++, ImportStatus.Used, L.Text("Name"));
        }
        string baseLine = Line(at);
        var b = FindBase(catalog, baseLine) ?? throw new ImportException(L.Text("This Gateway has no base for \"{0}\".", baseLine), at);
        Note(at++, ImportStatus.Used, L.Text("Base: {0}", b.Name));

        var item = new CraftedItem { Base = b.Id, Rarity = rarity, Sockets = null };
        var ids = new Dictionary<string, (double Range, int Line)>(StringComparer.Ordinal);
        var lines = new List<ModLine>();
        int levelGiven = 0;

        // A line the crafter takes as it is: a state, the item level, quality, sockets, PoB's modifier Ids.
        bool Take(int i, string line)
        {
            if (State(line) is { } state)
            {
                if (!item.States.Contains(state)) item.States.Add(state);
                Note(i, ImportStatus.Used, L.Text("State"));
                return true;
            }
            if (line.Equals("Unidentified", StringComparison.OrdinalIgnoreCase))
            {
                item.Identified = false;
                Note(i, ImportStatus.Used, L.Text("State"));
                return true;
            }
            if (Field.Match(line) is not { Success: true } field) return false;
            string value = field.Groups[2].Value.Trim();
            switch (field.Groups[1].Value)
            {
                case "Item Level":
                    levelGiven = Math.Clamp(Int(value), 1, 100);
                    Note(i, ImportStatus.Used, L.Text("Item level {0}", levelGiven));
                    return true;
                case "Quality":
                    item.Quality = Math.Clamp(Int(value), 0, 30);
                    Note(i, ImportStatus.Used, L.Text("Quality {0}", item.Quality));
                    return true;
                case "Sockets":
                    item.Sockets = string.Concat(value.ToUpperInvariant().Where(c => c is 'R' or 'G' or 'B' or 'W' or '-' or ' ')).Trim();
                    Note(i, ImportStatus.Used, L.Text("Sockets"));
                    return true;
                case "Prefix" or "Suffix" when !game && value != "None":
                    var (range, id) = Tags(value, out _, out _);
                    ids[id] = (range ?? .5, i);
                    Note(i, ImportStatus.Used, L.Text("Modifier Id"));
                    return true;
            }
            return false;
        }

        if (game)
        {
            // Sections up to the one with the item level describe the item; the modifiers come after it.
            bool modifiers = false, levelSection = false;
            int block = -1;
            string? kind = null, affix = null;
            for (; at < source.Length; at++)
            {
                string line = Line(at);
                if (line.Length == 0) continue;
                if (line == "--------")
                {
                    modifiers |= levelSection;
                    kind = affix = null;
                    continue;
                }
                if (Header.Match(line) is { Success: true } header)
                {
                    modifiers = true;
                    block++;
                    kind = header.Groups["kind"].Value;
                    affix = header.Groups["affix"].Success ? header.Groups["affix"].Value : null;
                    continue;
                }
                if (!modifiers)
                {
                    if (Take(at, line) && line.StartsWith("Item Level:", StringComparison.OrdinalIgnoreCase)) levelSection = true;
                    continue;
                }
                // Reminder text, and what to do with the item.
                if ((line.StartsWith('(') && line.EndsWith(')')) || Instructions.Any(s => line.StartsWith(s, StringComparison.OrdinalIgnoreCase))) continue;
                if (Take(at, line)) continue;
                bool isImplicit = kind?.Contains("Implicit", StringComparison.OrdinalIgnoreCase) == true;
                bool crafted = kind?.Contains("Crafted", StringComparison.OrdinalIgnoreCase) == true;
                bool enchant = kind?.Contains("Enchant", StringComparison.OrdinalIgnoreCase) == true;
                if (Marker.Match(line) is { Success: true } marker)
                {
                    line = line[..marker.Index];
                    isImplicit |= marker.Groups[1].Value == "implicit";
                    crafted |= marker.Groups[1].Value == "crafted";
                    enchant |= marker.Groups[1].Value == "enchant";
                }
                string values = GameValue.Replace(line, m => m.Groups[1].Value + m.Groups[2].Value);
                string? ranges = GameValue.IsMatch(line)
                    ? GameValue.Replace(line, m => m.Groups[1].Value + (m.Groups[4].Success ? $"({m.Groups[3].Value}-{m.Groups[4].Value})" : m.Groups[3].Value))
                    : null;
                lines.Add(new ModLine(at, values, ShapeOf(values), ranges, kind is null ? -1 : block, isImplicit, crafted, enchant, affix, kind));
            }
        }
        else
        {
            int implicitCount = -1, implicits = 0;
            for (; at < source.Length; at++)
            {
                string line = Line(at);
                if (line.Length == 0) continue;
                if (implicitCount < 0)
                {
                    // Before "Implicits: N" every line is a field; what the crafter does not take is skipped.
                    if (Field.Match(line) is { Success: true } field && field.Groups[1].Value == "Implicits") implicitCount = Math.Max(0, Int(field.Groups[2].Value));
                    else Take(at, line);
                    continue;
                }
                if (State(line) is not null) { Take(at, line); continue; }
                var (range, rest) = Tags(line, out bool crafted, out bool enchant);
                rest = Rolled(rest, range ?? .5);
                bool isImplicit = implicits++ < implicitCount;
                // PoB marks an enchantment it does not know as a crafted implicit.
                lines.Add(new ModLine(at, rest, ShapeOf(rest), null, -1, isImplicit, crafted, enchant || (isImplicit && crafted), null, null));
            }
            if (implicitCount < 0) throw new ImportException(NoItem);
        }

        var mods = new List<CraftedMod>();
        var pools = b.Pools.ToDictionary(p => p.Key, p => catalog.Pool(b, p.Key).Select(m => m.Id).ToHashSet(StringComparer.Ordinal));
        HashSet<string> Pool(string key) => pools.GetValueOrDefault(key) ?? [];
        var baseImplicits = b.Implicits.ToHashSet(StringComparer.Ordinal);
        // The game's header names the affix and whether it is a prefix or a suffix.
        static int Hint(CraftMod mod, ModLine line) =>
            (line.Affix is { Length: > 0 } affix && !string.Equals(affix, mod.Name, StringComparison.OrdinalIgnoreCase) ? 20 : 0)
            + (line.Kind is { } kind && ((kind.Contains("Prefix", StringComparison.Ordinal) && !mod.Prefix) || (kind.Contains("Suffix", StringComparison.Ordinal) && !mod.Suffix)) ? 20 : 0);

        Match(catalog, [.. lines.Where(l => l.Implicit)], "implicit", mods, Note, (mod, line) =>
            (baseImplicits.Contains(mod.Id) ? 1 : Pool("corrupted").Contains(mod.Id) ? 2 : Pool("eldritch").Contains(mod.Id) ? 3
            : mod.Generation is 3 or 5 or 28 or 29 ? 4 : 5) + Hint(mod, line));
        Match(catalog, [.. lines.Where(l => !l.Implicit)], "explicit", mods, Note, (mod, line) =>
            (ids.ContainsKey(mod.Id) ? 0
            : line.Crafted && Pool("crafted").Contains(mod.Id) ? 1
            : Pool("pool").Contains(mod.Id) ? 2
            : Pool("crafted").Contains(mod.Id) ? 3
            : Influences.Any(i => Pool(i).Contains(mod.Id)) ? 4
            : mod.Generation is 1 or 2 ? 5 : 6) + Hint(mod, line));
        foreach (var m in mods) ids.Remove(m.Id);
        // Ids PoB gave whose lines were not found (a mod the game shows no line for): rolled where PoB rolled them.
        foreach (var (id, (range, line)) in ids)
        {
            if (catalog.Mods.GetValueOrDefault(id) is not { } mod)
            {
                Note(line, ImportStatus.Unmatched, L.Text("This Gateway has no modifier {0}", id));
                continue;
            }
            mods.Add(new CraftedMod { Id = id, Values = [.. mod.Stats.Select(s => (int)Math.Round(s.Min + range * (s.Max - s.Min)))] });
            Note(line, ImportStatus.Mod, Describe(mod, false));
        }
        // A mod only an influence brings makes the item that influence's.
        foreach (var m in mods.Where(m => m.Section == "explicit" && !Pool("pool").Contains(m.Id)))
            if (Influences.FirstOrDefault(i => Pool(i).Contains(m.Id)) is { } influence && !item.States.Contains(influence)) item.States.Add(influence);
        item.Mods = mods;

        // Without an item level the item gets one every modifier fits.
        int needed = mods.Select(m => catalog.Mods.GetValueOrDefault(m.Id)?.Level ?? 1).DefaultIfEmpty(1).Max();
        item.ItemLevel = levelGiven > 0 ? levelGiven : Math.Clamp(Math.Max(item.ItemLevel, needed), 1, 100);
        if (rarity == "rare" && name.Length > 0)
        {
            item.Label = name;
            item.Name = RareName(catalog, name);
        }
        var report = Enumerable.Range(0, source.Length).Where(i => Line(i).Length > 0)
            .Select(i => new ImportLine(i, source[i].TrimEnd(), notes[i].Status, notes[i].Note ?? "")).ToList();
        return new ImportedItem(item, name.Length > 0 ? name : baseLine, report);
    }

    static string NoItem => L.Text("This is not an item: copy one with Ctrl+C in Path of Building or in the game.");

    static int Int(string value) => Number.Match(value) is { Success: true } m && int.TryParse(m.Value.TrimStart('+'), out int n) ? n : 0;

    static string Describe(CraftMod mod, bool outside) =>
        string.Join(" · ", new[] { mod.Name, mod.Id, outside ? L.Text("values outside its tier") : "" }.Where(s => s.Length > 0));

    /// <summary>The state an item's line stands for ("Corrupted", "Shaper Item" …), or null.</summary>
    static string? State(string line)
    {
        string state = line.EndsWith(" Item", StringComparison.OrdinalIgnoreCase) ? line[..^" Item".Length] : line;
        state = state.ToLowerInvariant();
        return state is "corrupted" or "mirrored" or "split" or "synthesised" || Influences.Contains(state) ? state : null;
    }

    /// <summary>The base the line names: exactly, without "Superior" or "Synthesised", or the longest base name in it (a
    /// magic item's name has its affixes around the base).</summary>
    static CraftBase? FindBase(CrafterCatalog catalog, string line)
    {
        foreach (string word in (string[])["Superior ", "Synthesised "])
            if (line.StartsWith(word, StringComparison.OrdinalIgnoreCase)) line = line[word.Length..];
        return catalog.Bases.FirstOrDefault(b => string.Equals(b.Name, line, StringComparison.OrdinalIgnoreCase))
            ?? catalog.Bases.Where(b => Regex.IsMatch(line, $@"(^|\s){Regex.Escape(b.Name)}($|\s)", RegexOptions.IgnoreCase))
                .OrderByDescending(b => b.Name.Length).FirstOrDefault();
    }

    /// <summary>The two Words.dat keys a rare's name is made of, or null when it is not two of the game's words.</summary>
    static List<uint>? RareName(CrafterCatalog catalog, string name)
    {
        foreach (var first in catalog.Words.Where(w => w.List == 1 && name.StartsWith(w.Text, StringComparison.OrdinalIgnoreCase)))
            if (catalog.Words.FirstOrDefault(w => w.List == 2 && string.Equals(first.Text + w.Text, name, StringComparison.OrdinalIgnoreCase)) is { } second)
                return [first.Key, second.Key];
        return null;
    }

    /// <summary>PoB's {tags} in front of a line: its {range:r}, whether it is crafted or an enchantment, and the rest.</summary>
    static (double? Range, string Text) Tags(string line, out bool crafted, out bool enchant)
    {
        double? range = null;
        crafted = enchant = false;
        while (Tag.Match(line) is { Success: true } tag)
        {
            string value = tag.Groups[1].Value;
            if (value.StartsWith("range:", StringComparison.Ordinal) && double.TryParse(value[6..], CultureInfo.InvariantCulture, out double r)) range = r;
            else if (value == "crafted") crafted = true;
            else if (value == "enchant") enchant = true;
            line = line[tag.Length..];
        }
        return (range, line.Trim());
    }

    /// <summary>The line with each "(min-max)" rolled where PoB rolls it: at the line's range, 0.5 without one.</summary>
    internal static string Rolled(string line, double range) => Range.Replace(line, m =>
    {
        double min = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture), max = double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
        double value = min + range * (max - min);
        if (m.Groups[1].Value == "-") value = -value;
        string number = value.ToString("0.##", CultureInfo.InvariantCulture);
        return m.Groups[1].Value == "+" && value > 0 ? "+" + number : number;
    });

    internal static Shape ShapeOf(string line)
    {
        var numbers = new List<double>();
        string words = Number.Replace(line, m =>
        {
            numbers.Add(double.Parse(m.Value.TrimStart('+'), CultureInfo.InvariantCulture));
            return "#";
        });
        return new Shape(words, [.. numbers]);
    }

    static bool Same(Shape a, Shape b) => a.Numbers.Length == b.Numbers.Length && string.Equals(a.Words, b.Words, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether the game showed these lines with the mod's own ranges: then it is that tier.</summary>
    static bool SameRanges(CrafterCatalog catalog, CraftMod mod, List<ModLine> window)
    {
        if (window.Any(l => l.Range is null)) return false;
        var mine = catalog.Lines(mod).Where(l => l.Length > 0).Select(l => l.ToLowerInvariant()).Order(StringComparer.Ordinal);
        return mine.SequenceEqual(window.Select(l => l.Range!.ToLowerInvariant()).Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// The lines as mods of the section: at each line, of the mods that word it, the one whose lines with the best values
    /// are nearest the item's (a mod of several lines takes them together, in any order; a block of the game's is one mod),
    /// then the lowest rank, then the most lines. When the game showed the tiers' ranges only the mods with those ranges
    /// count. Values outside a mod's ranges are tried only when none in them comes within 1 (PoB rolls 7.5 between two
    /// whole numbers), and then come after the mods that give the line within theirs.
    /// </summary>
    static void Match(CrafterCatalog catalog, List<ModLine> lines, string section, List<CraftedMod> into, Action<int, ImportStatus, string> note,
        Func<CraftMod, ModLine, int> rank)
    {
        var index = IndexOf(catalog);
        int i = 0;
        while (i < lines.Count)
        {
            var line = lines[i];
            int block = line.Block < 0 ? 0 : lines.Skip(i).TakeWhile(l => l.Block == line.Block).Count();
            var found = new List<Candidate>();
            IEnumerable<(CraftMod Mod, int Count)> candidates = line.Enchant ? [] : index.GetValueOrDefault(line.Shape.Words.ToLowerInvariant(), []);
            foreach (var (mod, count) in candidates)
            {
                if (block > 0 ? count != block : i + count > lines.Count) continue;
                var window = lines.GetRange(i, count);
                var shapes = window.Select(l => l.Shape).ToList();
                var (values, distance) = Solve(catalog, mod, shapes, [.. mod.Stats.Select(s => Math.Max(s.Min, s.Max))], 0);
                if (!double.IsInfinity(distance)) found.Add(new Candidate(mod, values, distance, false, rank(mod, line), count, shapes, SameRanges(catalog, mod, window)));
            }
            if (found.Any(f => f.Ranges)) found.RemoveAll(f => !f.Ranges);
            if (found.Count > 0 && found.Min(f => f.Distance) >= 1)
                foreach (var f in found.OrderBy(f => (f.Rank, -f.Count)).ToList())
                {
                    int wider = (int)Math.Min(1_000_000, f.Window.SelectMany(w => w.Numbers).Select(Math.Abs).DefaultIfEmpty(0).Max() * 1000 + 100);
                    var (values, distance) = Solve(catalog, f.Mod, f.Window, [.. f.Values], wider);
                    if (distance < f.Distance) found.Add(f with { Values = values, Distance = distance, Outside = true });
                    if (distance == 0) break;
                }
            if (found.Count == 0)
            {
                for (int k = 0; k < Math.Max(1, block); k++)
                    note(lines[i + k].Number, ImportStatus.Unmatched, line.Enchant ? L.Text("The crafter makes no enchantments") : L.Text("No modifier is worded like this"));
                i += Math.Max(1, block);
                continue;
            }
            var best = found.MinBy(f => (f.Distance, f.Outside, f.Rank, -f.Count))!;
            into.Add(new CraftedMod { Id = best.Mod.Id, Section = section, Values = [.. best.Values] });
            for (int k = 0; k < best.Count; k++) note(lines[i + k].Number, ImportStatus.Mod, Describe(best.Mod, best.Outside));
            i += best.Count;
        }
    }

    /// <summary>The values nearest these lines, in the mod's ranges widened by <paramref name="wider"/> on each side: a
    /// stat at a time from <paramref name="values"/>, its whole range tried (in steps first when it is wide), twice over.
    /// Infinity when the mod never words them so.</summary>
    static (int[] Values, double Distance) Solve(CrafterCatalog catalog, CraftMod mod, IReadOnlyList<Shape> lines, int[] values, int wider)
    {
        double best = Distance(catalog, mod, values, lines);
        for (int pass = 0; pass < 2 && best > 0; pass++)
            for (int s = 0; s < values.Length && best > 0; s++)
            {
                int lo = Math.Min(mod.Stats[s].Min, mod.Stats[s].Max) - wider, hi = Math.Max(mod.Stats[s].Min, mod.Stats[s].Max) + wider;
                int step = Math.Max(1, (hi - lo) / 500);
                for (; ; step = Math.Max(1, step / 10))
                {
                    int keep = values[s];
                    for (int v = Math.Max(lo, keep - step * 500); v <= Math.Min(hi, keep + step * 500); v += step)
                    {
                        values[s] = v;
                        double d = Distance(catalog, mod, values, lines);
                        if (d < best) { best = d; keep = v; }
                    }
                    values[s] = keep;
                    if (step == 1) break;
                }
            }
        return (values, best);
    }

    /// <summary>How far the mod's lines with these values are from the item's: the difference of their numbers, infinity
    /// when their words differ.</summary>
    static double Distance(CrafterCatalog catalog, CraftMod mod, int[] values, IReadOnlyList<Shape> lines)
    {
        var mine = catalog.Lines(mod, values).Where(l => l.Length > 0).ToList();
        if (mine.Count != lines.Count) return double.PositiveInfinity;
        var used = new bool[lines.Count];
        double total = 0;
        foreach (string text in mine)
        {
            var shape = ShapeOf(text);
            int at = Enumerable.Range(0, lines.Count).FirstOrDefault(i => !used[i] && Same(shape, lines[i]), -1);
            if (at < 0) return double.PositiveInfinity;
            used[at] = true;
            for (int n = 0; n < shape.Numbers.Length; n++) total += Math.Abs(shape.Numbers[n] - lines[at].Numbers[n]);
        }
        return total;
    }

    static readonly Lock IndexLock = new();
    static (CrafterCatalog Catalog, Dictionary<string, List<(CraftMod, int)>> Index)? _index;

    /// <summary>Every mod by the words of each of its lines (at its highest and lowest values, which may word it apart:
    /// "increased" and "reduced"), with how many lines it has. Made once per catalog.</summary>
    static Dictionary<string, List<(CraftMod Mod, int Count)>> IndexOf(CrafterCatalog catalog)
    {
        lock (IndexLock)
        {
            if (_index is { } cached && cached.Catalog == catalog) return cached.Index;
            var index = new Dictionary<string, List<(CraftMod, int)>>(StringComparer.Ordinal);
            foreach (var mod in catalog.Mods.Values)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var values in (int[][])[[.. mod.Stats.Select(s => Math.Max(s.Min, s.Max))], [.. mod.Stats.Select(s => Math.Min(s.Min, s.Max))]])
                {
                    var lines = catalog.Lines(mod, values).Where(l => l.Length > 0).ToList();
                    foreach (string line in lines)
                    {
                        string words = ShapeOf(line).Words.ToLowerInvariant();
                        if (!seen.Add(words)) continue;
                        if (!index.TryGetValue(words, out var list)) index[words] = list = [];
                        list.Add((mod, lines.Count));
                    }
                }
            }
            _index = (catalog, index);
            return index;
        }
    }
}
