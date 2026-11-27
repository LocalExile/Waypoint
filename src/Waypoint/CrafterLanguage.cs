using System.Diagnostics;
using System.Text.Json;

namespace Waypoint;

/// <summary>Display-only translations for the Gateway's item catalog, with optional overrides imported from the player's client.</summary>
sealed class CrafterLanguage(CrafterCatalog catalog)
{
    readonly Dictionary<(string Kind, string Id), string> _names = [];
    readonly Dictionary<(int List, uint Key), string> _words = [];
    readonly Dictionary<string, StatDescription> _descriptions = new(StringComparer.Ordinal);

    public static CrafterLanguage Load(CrafterCatalog catalog) =>
        Load(catalog, L.Current.Language, SettingsStore.DataDirectory);

    internal static CrafterLanguage Load(CrafterCatalog catalog, string language, string dataDirectory)
    {
        string path = Path.Combine(dataDirectory, "localization", $"crafter.{language}.jsonl");
        try
        {
            if (File.Exists(path)) return Parse(catalog, File.ReadLines(path).Concat(BundledLines(language)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException
            or KeyNotFoundException or FormatException or ArgumentException)
        {
            Trace.TraceWarning("Could not load item crafter translations from {0}: {1}", path, e.Message);
        }
        return Parse(catalog, BundledLines(language));
    }

    static IEnumerable<string> BundledLines(string language)
    {
        using var stream = typeof(CrafterLanguage).Assembly.GetManifestResourceStream($"Localization/crafter.{language}.jsonl");
        if (stream is null) yield break;
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line) yield return line;
    }

    internal static CrafterLanguage Parse(CrafterCatalog catalog, IEnumerable<string> lines)
    {
        var language = new CrafterLanguage(catalog);
        foreach (string line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var row = JsonDocument.Parse(line);
            var value = row.RootElement;
            string? kind = value.GetProperty("type").GetString();
            switch (kind)
            {
                case "word":
                    if (value.GetProperty("text").GetString() is { Length: > 0 } word)
                        language._words.TryAdd((value.GetProperty("list").GetInt32(), value.GetProperty("key").GetUInt32()), word);
                    break;
                case "desc":
                    var description = CrafterCatalog.ParseDescription(value);
                    if (description.Stats.Count == 0 || description.Variants.Count == 0) break;
                    // Match the Gateway's grouping of stats, so the localized text reads the same set of values.
                    if (catalog.Description(description.Stats[0]) is not { } original || !original.Stats.SequenceEqual(description.Stats)) break;
                    description = description with { Variants = [.. description.Variants, .. original.Variants] };
                    foreach (string stat in description.Stats) language._descriptions.TryAdd(stat, description);
                    break;
                case "class" or "base" or "mod" or "theme":
                    if (value.GetProperty("id").GetString() is { Length: > 0 } id && value.GetProperty("name").GetString() is { Length: > 0 } name)
                        language._names.TryAdd((kind, id), name);
                    break;
            }
        }
        return language;
    }

    string Name(string kind, string id, string fallback) => _names.GetValueOrDefault((kind, id), fallback);

    public string ClassName(string id) => Name("class", id, catalog.Classes.GetValueOrDefault(id, id));
    public string BaseName(CraftBase item) => Name("base", item.Id, item.Name);
    public string ModName(CraftMod? mod) => mod is null ? "" : Name("mod", mod.Id, mod.Name);
    public string ThemeName(CraftTheme theme) => Name("theme", theme.Name, theme.Name);
    public string WordText(RareWord word) => _words.GetValueOrDefault((word.List, word.Key), word.Text);
    public bool HasDescriptions => _descriptions.Count > 0;

    StatDescription? Description(string stat) => _descriptions.GetValueOrDefault(stat) ?? catalog.Description(stat);

    public IReadOnlyList<string> ThemeLines(CraftTheme theme) =>
        [.. CrafterCatalog.ThemeStats(theme, Description).Select(line => L.Text("Added Small Passive Skills grant: {0}", line))];

    public IReadOnlyList<string> Lines(CraftMod mod, IReadOnlyList<int>? values = null) => ModText.Lines(mod, Description, values);

    public IReadOnlyList<string> Lines(CraftBase? item, CraftedMod crafted, CraftMod mod) =>
        item?.Cluster?.Passives == mod.Id && catalog.Theme(item, [crafted]) is { } theme
            ? [.. Lines(mod, crafted.Values), .. ThemeLines(theme)] : Lines(mod, crafted.Values);

    public string StatLabel(CraftStat stat)
    {
        if (_descriptions.GetValueOrDefault(stat.Id) is { Stats.Count: 1 } description)
        {
            if (ModText.Format(description, [(stat.Min, stat.Max)]) is { Length: > 0 } label)
                return label.Split('\n')[0];
        }
        return stat.Id.Replace('_', ' ');
    }
}
