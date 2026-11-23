using System.Text;
using System.Text.RegularExpressions;

namespace Launcher.Core;

/// <summary>
/// The item names of the checkout's client: the Name column of its BaseItemTypes, ItemClasses and Mods tables, read from
/// the client tables as SQL (<c>sql/updates/client/*_&lt;Table&gt;.sql</c>), which a release carries as a source checkout
/// does. Nothing of the game is kept in Waypoint.
/// </summary>
public sealed class ClientItemNames
{
    public const string TablesFolder = "sql/updates/client";

    readonly Dictionary<LootFilterValueKind, (HashSet<string> Set, string Joined)> _names = [];

    public int Count(LootFilterValueKind kind) => _names.TryGetValue(kind, out var n) ? n.Set.Count : 0;

    /// <summary>The names of the client under <paramref name="root"/>, or null when it has no item tables.</summary>
    public static ClientItemNames? Load(string root)
    {
        string folder = Path.Combine(root, TablesFolder);
        if (!Directory.Exists(folder)) return null;
        var names = new ClientItemNames();
        foreach (var (kind, table) in new[] { (LootFilterValueKind.BaseType, "BaseItemTypes"), (LootFilterValueKind.Class, "ItemClasses"), (LootFilterValueKind.Mod, "Mods") })
        {
            var set = ReadNames(folder, table);
            if (set is not null) names._names[kind] = (set, "\n" + string.Join("\n", set) + "\n");
        }
        return names.Count(LootFilterValueKind.BaseType) > 0 ? names : null;
    }

    /// <summary>Names given directly (tests).</summary>
    public static ClientItemNames From(IEnumerable<string> baseTypes, IEnumerable<string> classes, IEnumerable<string>? mods = null)
    {
        var names = new ClientItemNames();
        void Add(LootFilterValueKind kind, IEnumerable<string> values)
        {
            var set = new HashSet<string>(values, StringComparer.Ordinal);
            names._names[kind] = (set, "\n" + string.Join("\n", set) + "\n");
        }
        Add(LootFilterValueKind.BaseType, baseTypes);
        Add(LootFilterValueKind.Class, classes);
        if (mods is not null) Add(LootFilterValueKind.Mod, mods);
        return names;
    }

    /// <summary>
    /// Whether the client has <paramref name="value"/>: the whole name with <c>==</c>, otherwise any name containing it,
    /// as the game matches. A kind without names keeps every value.
    /// </summary>
    public bool Has(LootFilterValueKind kind, string value, bool exact)
    {
        if (!_names.TryGetValue(kind, out var n)) return true;
        return exact ? n.Set.Contains(value) : n.Joined.Contains(value, StringComparison.Ordinal);
    }

    static readonly Regex Value = new(@"\s*('(?:[^']|'')*'|[^,]*)\s*(,|$)", RegexOptions.Compiled);

    /// <summary>One table's Name column: <c>INSERT INTO "T" ("_row", ..., "Name", ...) VALUES</c>, then a row per line.</summary>
    static HashSet<string>? ReadNames(string folder, string table)
    {
        var files = Directory.GetFiles(folder, $"*_{table}.sql").OrderBy(f => f, StringComparer.Ordinal).ToArray();
        if (files.Length == 0) return null;
        var set = new HashSet<string>(StringComparer.Ordinal);
        string head = $"INSERT INTO \"{table}\" (";
        foreach (string file in files)
        {
            int index = -1;
            foreach (string line in File.ReadLines(file, Encoding.UTF8))
            {
                if (line.StartsWith("INSERT INTO", StringComparison.Ordinal))
                {
                    index = -1;
                    int end = line.IndexOf(") VALUES", StringComparison.Ordinal);
                    if (line.StartsWith(head, StringComparison.Ordinal) && end > head.Length)
                        index = Array.IndexOf(line[head.Length..end].Split(',').Select(c => c.Trim().Trim('"')).ToArray(), "Name");
                    continue;
                }
                if (index < 0 || !line.StartsWith('(')) continue;
                int at = 1;
                string? cell = null;
                for (int k = 0; k <= index; k++)
                {
                    var m = Value.Match(line, at);
                    if (!m.Success) { cell = null; break; }
                    cell = m.Groups[1].Value;
                    at = m.Index + m.Length;
                }
                if (cell is { Length: > 2 } && cell[0] == '\'') set.Add(cell[1..^1].Replace("''", "'"));
            }
        }
        return set;
    }
}

public enum LootFilterValueKind { BaseType, Class, Mod }

/// <summary>What a conversion did: the new filter and its counts.</summary>
public sealed record LootFilterConversion(
    IReadOnlyList<string> Lines,
    string? OnlineName,
    int Blocks,
    int RemovedNames,
    int RemovedLines,
    IReadOnlyList<string> RemovedSamples,
    IReadOnlyDictionary<string, int> DisabledByCondition,
    int DisabledEmptyLists)
{
    public int DisabledBlocks => DisabledByCondition.Values.Sum() + DisabledEmptyLists;
}

/// <summary>
/// Makes a loot filter written for a newer patch (FilterBlade's and NeverSink's 3.30 filters) load in the LocalExile client.
/// The client refuses a filter that names an item, item class or modifier it doesn't have, or uses a condition it doesn't
/// know (3.30's <c>Fated</c>). This takes out the names the checkout's client lacks, and turns off (comments out) a block whose
/// list ends up empty or that uses an unknown condition. An unknown cosmetic action loses only its line. Everything else
/// stays as it is: styles, sounds, tiers and the player's FilterBlade changes.
/// </summary>
public static class LootFilters
{
    public const string Suffix = " - LocalExile";

    /// <summary>The conditions the 3.29.3 client knows (its Game.exe has each as a UTF-16 string).</summary>
    public static readonly IReadOnlySet<string> Conditions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "AreaLevel", "ItemLevel", "DropLevel", "Quality", "Rarity", "Class", "BaseType", "LinkedSockets", "SocketGroup", "Sockets",
        "Height", "Width", "HasExplicitMod", "HasImplicitMod", "AnyEnchantment", "HasEnchantment", "EnchantmentPassiveNode",
        "EnchantmentPassiveNum", "StackSize", "GemLevel", "AlternateQuality", "Replica", "Identified", "Corrupted", "CorruptedMods",
        "Mirrored", "ElderItem", "ShaperItem", "HasInfluence", "FracturedItem", "SynthesisedItem", "ShapedMap", "ElderMap",
        "BlightedMap", "UberBlightedMap", "MapTier", "HasSearingExarchImplicit", "HasEaterOfWorldsImplicit", "ArchnemesisMod",
        "Scourged", "TransfiguredGem", "HasCruciblePassiveTree", "BaseDefencePercentile", "BaseArmour", "BaseEnergyShield",
        "BaseEvasion", "BaseWard", "ZanaMemory", "MemoryStrands", "Imbued", "Foulborn", "Vestigial", "UnidentifiedItemTier",
    };

    /// <summary>The actions the 3.29.3 client knows.</summary>
    public static readonly IReadOnlySet<string> Actions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "SetBorderColor", "SetTextColor", "SetBackgroundColor", "SetFontSize", "PlayAlertSound", "PlayAlertSoundPositional",
        "CustomAlertSound", "CustomAlertSoundOptional", "DisableDropSound", "EnableDropSound", "DisableDropSoundIfAlertSound",
        "EnableDropSoundIfAlertSound", "MinimapIcon", "PlayEffect", "Continue", "Import",
    };

    static readonly string[] BlockStarts = ["Show", "Hide", "Minimal"];
    static readonly Regex Token = new("\"[^\"]*\"|\\S+", RegexOptions.Compiled);
    static readonly Regex Operator = new(@"^(==|=|!=|!|<=|>=|<|>)?\d*$", RegexOptions.Compiled);
    static readonly Regex HeaderField = new(@"^#([A-Za-z]+):(.*)$", RegexOptions.Compiled);

    public static LootFilterConversion Convert(IReadOnlyList<string> lines, ClientItemNames names, string source, DateTimeOffset now)
    {
        var output = new List<string>(lines.Count + 64);
        var removed = new SortedSet<string>(StringComparer.Ordinal);
        var byCondition = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int blocks = 0, removedNames = 0, removedLines = 0, emptyLists = 0;
        string? onlineName = null;

        int i = 0;
        // A filter synced to the account starts "#Online Item Filter" and #key:value lines; a local one has neither.
        if (lines.Count > 0 && lines[0].Trim() == "#Online Item Filter")
        {
            for (i = 1; i < lines.Count && HeaderField.Match(lines[i]) is { Success: true } m; i++)
                if (m.Groups[1].Value == "name") onlineName = m.Groups[2].Value.Trim();
        }
        output.Add($"# Converted for LocalExile by Waypoint on {now:yyyy-MM-dd HH:mm}");
        output.Add($"# Source: {source}");

        while (i < lines.Count)
        {
            if (!BlockStarts.Contains(Keyword(lines[i]))) { output.Add(lines[i++]); continue; }

            blocks++;
            var block = new List<string> { lines[i++] };
            while (i < lines.Count && lines[i].Trim().Length > 0 && !BlockStarts.Contains(Keyword(lines[i]))) block.Add(lines[i++]);

            var kept = new List<string> { block[0] };
            string? unknownCondition = null;
            bool emptyList = false;
            for (int b = 1; b < block.Count && unknownCondition is null && !emptyList; b++)
            {
                string line = block[b];
                var (code, comment) = SplitComment(line);
                string keyword = Keyword(line);
                if (keyword.Length == 0) { kept.Add(line); continue; }
                if (!Conditions.Contains(keyword) && !Actions.Contains(keyword))
                {
                    if (keyword.StartsWith("Set", StringComparison.Ordinal) || keyword.StartsWith("Play", StringComparison.Ordinal)
                        || keyword.StartsWith("Custom", StringComparison.Ordinal) || keyword.StartsWith("Enable", StringComparison.Ordinal)
                        || keyword.StartsWith("Disable", StringComparison.Ordinal))
                    { removedLines++; continue; }
                    unknownCondition = keyword;
                    break;
                }
                LootFilterValueKind? kind = keyword switch
                {
                    "BaseType" => LootFilterValueKind.BaseType,
                    "Class" => LootFilterValueKind.Class,
                    "HasExplicitMod" or "HasImplicitMod" => LootFilterValueKind.Mod,
                    _ => null,
                };
                if (kind is null) { kept.Add(line); continue; }

                string trimmed = code.Trim();
                var prefix = new List<string>();
                var values = new List<string>();
                foreach (Match t in Token.Matches(trimmed[keyword.Length..]))
                {
                    if (values.Count == 0 && !t.Value.StartsWith('"') && Operator.IsMatch(t.Value)) prefix.Add(t.Value);
                    else values.Add(t.Value);
                }
                string op = string.Concat(prefix);
                bool exact = op.StartsWith("==", StringComparison.Ordinal), negated = op.StartsWith('!');
                var known = new List<string>();
                foreach (string v in values)
                {
                    string name = v.Trim('"');
                    if (names.Has(kind.Value, name, exact)) known.Add(v);
                    else { removedNames++; removed.Add($"{keyword}: {name}"); }
                }
                if (known.Count == values.Count) { kept.Add(line); continue; }
                if (known.Count == 0)
                {
                    // "None of <nothing>" is always true, so a negated list just goes; any other list matches nothing.
                    if (negated) { removedLines++; continue; }
                    emptyList = true;
                    break;
                }
                string indent = code[..(code.Length - code.TrimStart().Length)];
                kept.Add(indent + keyword + " " + string.Join(' ', prefix.Concat(known)) + (comment.Length > 0 ? " " + comment : ""));
            }

            if (unknownCondition is not null || emptyList)
            {
                if (unknownCondition is not null) byCondition[unknownCondition] = byCondition.GetValueOrDefault(unknownCondition) + 1;
                else emptyLists++;
                output.Add(unknownCondition is not null
                    ? $"# [LocalExile] block turned off: this client doesn't know the condition {unknownCondition}"
                    : "# [LocalExile] block turned off: every name in one of its lists is from a newer patch");
                output.AddRange(block.Select(l => "#" + l));
            }
            else output.AddRange(kept);
        }
        return new LootFilterConversion(output, onlineName, blocks, removedNames, removedLines, removed.Take(12).ToArray(), byCondition, emptyLists);
    }

    /// <summary>The line's first word, outside a comment ("" for a blank or comment line).</summary>
    static string Keyword(string line)
    {
        string code = SplitComment(line).Code.Trim();
        int space = code.IndexOfAny([' ', '\t']);
        return space < 0 ? code : code[..space];
    }

    static (string Code, string Comment) SplitComment(string line)
    {
        int hash = line.IndexOf('#');   // item names never hold '#'
        return hash < 0 ? (line, "") : (line[..hash], line[hash..]);
    }

    // ---- Files and folders -----------------------------------------------------------------------------------------

    /// <summary>
    /// The folder the game lists loot filters from: <c>Documents\My Games\Path of Exile</c> (OneDrive's Documents when
    /// Windows moved it there), never a subfolder such as LocalExile's own settings folder. The first of the candidates the
    /// game has written settings in (<c>*_Config.ini</c>, there or one folder down), else the first that exists.
    /// </summary>
    public static string FilterFolder()
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roots = new List<string> { Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) };
        if (Environment.GetEnvironmentVariable("OneDrive") is { Length: > 0 } oneDrive) roots.Add(Path.Combine(oneDrive, "Documents"));
        roots.Add(Path.Combine(profile, "OneDrive", "Documents"));
        roots.Add(Path.Combine(profile, "Documents"));
        var candidates = roots.Where(r => r.Length > 0).Select(r => Path.Combine(r, "My Games", "Path of Exile"))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (string c in candidates.Where(Directory.Exists))
        {
            if (Directory.EnumerateFiles(c, "*_Config.ini").Any()) return c;
            if (Directory.EnumerateDirectories(c).Any(d => Directory.EnumerateFiles(d, "*_Config.ini").Any())) return c;
        }
        return candidates.FirstOrDefault(Directory.Exists) ?? candidates[0];
    }

    /// <summary>The game's filter folder for a folder the player picked: "Path of Exile" itself for any folder in it.</summary>
    public static string ResolveFilterFolder(string picked)
    {
        string folder = Path.TrimEndingDirectorySeparator(picked);
        for (string? at = folder; at is not null; at = Path.GetDirectoryName(at))
            if (string.Equals(Path.GetFileName(at), "Path of Exile", StringComparison.OrdinalIgnoreCase)
                && string.Equals(Path.GetFileName(Path.GetDirectoryName(at)), "My Games", StringComparison.OrdinalIgnoreCase))
                return at;
        if (string.Equals(Path.GetFileName(folder), "My Games", StringComparison.OrdinalIgnoreCase)) return Path.Combine(folder, "Path of Exile");
        if (Directory.Exists(Path.Combine(folder, "My Games"))) return Path.Combine(folder, "My Games", "Path of Exile");
        return folder;
    }

    /// <summary>The filters to offer, newest first: the folder's own, its OnlineFilters (synced ones) and Downloads' .filter files.</summary>
    public static IReadOnlyList<FileInfo> Candidates(string filterFolder)
    {
        var files = new List<FileInfo>();
        if (Directory.Exists(filterFolder))
        {
            files.AddRange(new DirectoryInfo(filterFolder).EnumerateFiles("*.filter")
                .Where(f => !Path.GetFileNameWithoutExtension(f.Name).EndsWith(Suffix, StringComparison.OrdinalIgnoreCase)));
            string online = Path.Combine(filterFolder, "OnlineFilters");
            if (Directory.Exists(online)) files.AddRange(new DirectoryInfo(online).EnumerateFiles());
        }
        string downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        if (Directory.Exists(downloads)) files.AddRange(new DirectoryInfo(downloads).EnumerateFiles("*.filter"));
        return files.OrderByDescending(f => f.LastWriteTimeUtc).ToArray();
    }

    /// <summary>A synced filter's name and patch from its header (#name:, #version:), or nulls.</summary>
    public static (string? Name, string? Version) ReadHeader(string path)
    {
        string? name = null, version = null;
        try
        {
            foreach (string line in File.ReadLines(path).Take(12))
            {
                var m = HeaderField.Match(line);
                if (!m.Success) continue;
                if (m.Groups[1].Value == "name") name = m.Groups[2].Value.Trim();
                else if (m.Groups[1].Value == "version") version = m.Groups[2].Value.Trim();
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return (name, version);
    }

    /// <summary>Where the converted copy goes: "&lt;name&gt; - LocalExile.filter" in the filter folder.</summary>
    public static string OutputPath(string filterFolder, string source, string? onlineName)
    {
        string name = onlineName is { Length: > 0 } ? onlineName : Path.GetFileNameWithoutExtension(source);
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c.ToString(), "");
        if (name.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase)) name = name[..^Suffix.Length];
        return Path.Combine(filterFolder, name.Trim() + Suffix + ".filter");
    }

    /// <summary>Reads, converts and writes one filter (UTF-8 without a byte order mark, as FilterBlade's).</summary>
    public static (LootFilterConversion Result, string Path) ConvertFile(string source, string filterFolder, ClientItemNames names)
    {
        var result = Convert(File.ReadAllLines(source, Encoding.UTF8), names, Path.GetFileName(source), DateTimeOffset.Now);
        string target = OutputPath(filterFolder, source, result.OnlineName);
        Directory.CreateDirectory(filterFolder);
        File.WriteAllLines(target, result.Lines, new UTF8Encoding(false));
        return (result, target);
    }
}
