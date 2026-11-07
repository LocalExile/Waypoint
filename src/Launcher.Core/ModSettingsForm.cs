using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Launcher.Core;

/// <summary>One declared setting (the server's Agents/docs/dev/modding.md, "Settings"). Waypoint edits the common types;
/// <see cref="Editable"/> is false for the others, which the player edits in the file.</summary>
public sealed record ModSetting(string Name, string Type, JsonNode? Default, string Label, string Description, string Unit, string Format,
    double? Min, double? Max, IReadOnlyList<(JsonNode Value, string Label)> Options, string Source, int? MinItems, int? MaxItems,
    int? MinLength, int? MaxLength, string Applies, bool Advanced)
{
    public static readonly string[] EditableTypes = ["bool", "integer", "number", "choice", "choices", "text"];
    public bool Editable => EditableTypes.Contains(Type);
    public string Title => Label.Length > 0 ? Label : Name;
    /// <summary>A percent is kept as a fraction (0.15) and shown as 15.</summary>
    public bool IsPercent => Format == "percent" && Type is "number" or "integer";
}

/// <summary>A mod's settings as Waypoint edits them: the declarations in its <c>settings.json</c>, and the player's own values in
/// the checkout's <c>data/mods/&lt;id&gt;.json</c>, which hold only what differs from a default. The adapter writes that file
/// (SaveModSettings); the Gateway reloads the mod when it changes.</summary>
public sealed class ModSettingsForm
{
    public const string DeclarationsFile = "settings.json";
    static readonly JsonDocumentOptions Jsonc = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
    static readonly JsonNodeOptions NodeOptions = new() { PropertyNameCaseInsensitive = false };

    public string Id { get; }
    public IReadOnlyList<ModSetting> Settings { get; }
    /// <summary>The player's values as read, those no longer declared included: saving keeps them, as the Gateway does.</summary>
    public JsonObject UserValues { get; }

    ModSettingsForm(string id, IReadOnlyList<ModSetting> settings, JsonObject user) => (Id, Settings, UserValues) = (id, settings, user);

    /// <summary>The player's values file of a mod, relative to the checkout's root.</summary>
    public static string UserFile(string id) => Path.Combine("data", "mods", id + ".json");

    /// <summary>Reads the declarations and the player's values; throws for a declarations file Waypoint cannot read.</summary>
    public static ModSettingsForm Load(string id, string declarationsPath, string root)
    {
        if (!ModCatalog.ValidId(id)) throw new InvalidDataException("Unsupported mod ID.");
        if (new FileInfo(declarationsPath).Length > 256 * 1024) throw new InvalidDataException(L.Text("{0} is larger than 256 KB", DeclarationsFile));
        var settings = Parse(File.ReadAllText(declarationsPath));
        var user = new JsonObject();
        string userPath = Path.Combine(root, UserFile(id));
        if (File.Exists(userPath) && ModCatalog.Physical(userPath) && new FileInfo(userPath).Length < 256 * 1024)
        {
            try { if (JsonNode.Parse(File.ReadAllText(userPath), NodeOptions, Jsonc) is JsonObject read) user = read; }
            catch (JsonException) { }
        }
        return new(id, settings, user);
    }

    internal static IReadOnlyList<ModSetting> Parse(string json)
    {
        if (JsonNode.Parse(json, NodeOptions, Jsonc) is not JsonObject root || root["settings"] is not JsonObject settings)
            throw new InvalidDataException(L.Text("{0} has no settings object", DeclarationsFile));
        var result = new List<ModSetting>();
        foreach (var (name, node) in settings)
        {
            if (node is not JsonObject d) continue;
            string Text(string key) => d[key] is JsonValue v && v.TryGetValue(out string? s) ? s : "";
            double? Number(string key) => d[key] is JsonValue v && v.TryGetValue(out double x) ? x : null;
            int? Whole(string key) => d[key] is JsonValue v && v.TryGetValue(out int x) ? x : null;
            var options = d["options"] is JsonArray list
                ? list.Where(o => o is not null).Select(o => o is JsonObject labelled && labelled["value"] is { } value
                    ? (value.DeepClone(), labelled["label"] is JsonValue l && l.TryGetValue(out string? label) ? label : Show(value))
                    : (o!.DeepClone(), Show(o!))).ToList()
                : [];
            result.Add(new(name, Text("type"), d["default"]?.DeepClone(), Text("label"), Text("description"), Text("unit"), Text("format"),
                Number("min"), Number("max"), options, Text("source"), Whole("minItems"), Whole("maxItems"), Whole("minLength"), Whole("maxLength"),
                Text("applies") is { Length: > 0 } applies ? applies : "now", d["advanced"] is JsonValue a && a.TryGetValue(out bool adv) && adv));
        }
        return result;
    }

    /// <summary>The value in effect: the player's when it is set, the default otherwise.</summary>
    public JsonNode? Current(ModSetting setting) =>
        (UserValues.FirstOrDefault(p => p.Key.Equals(setting.Name, StringComparison.OrdinalIgnoreCase)).Value ?? setting.Default)?.DeepClone();

    /// <summary>A value as the form shows it (a percent as its hundredfold).</summary>
    public static string Show(JsonNode? value, ModSetting? setting = null)
    {
        if (value is JsonValue v && v.TryGetValue(out double number) && v.GetValueKind() == JsonValueKind.Number)
            return (setting?.IsPercent == true ? number * 100 : number).ToString("0.######", CultureInfo.InvariantCulture);
        if (value is JsonValue s && s.TryGetValue(out string? text)) return text;
        return value?.ToJsonString() ?? "";
    }

    /// <summary>What the player typed or chose, as the setting's value; null with <paramref name="error"/> when it does not fit
    /// its declaration. A choice is one of its options, or, for one from the game's values, any text.</summary>
    public static JsonNode? Read(ModSetting setting, object? input, out string? error)
    {
        error = null;
        switch (setting.Type)
        {
            case "bool":
                return JsonValue.Create(input is true);
            case "integer" or "number":
            {
                string text = (input as string ?? "").Trim().Replace(',', '.');
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) || !double.IsFinite(number))
                { error = L.Text("{0}: not a number", setting.Title); return null; }
                if (setting.IsPercent) number /= 100;
                if (setting.Type == "integer" && number != Math.Floor(number)) { error = L.Text("{0}: not a whole number", setting.Title); return null; }
                if (number < setting.Min || number > setting.Max)
                { error = L.Text("{0}: from {1} to {2}", setting.Title, Show(JsonValue.Create(setting.Min ?? double.MinValue), setting), Show(JsonValue.Create(setting.Max ?? double.MaxValue), setting)); return null; }
                return setting.Type == "integer" ? JsonValue.Create((long)number) : JsonValue.Create(number);
            }
            case "text":
            {
                string text = input as string ?? "";
                if (text.Length < (setting.MinLength ?? 0) || text.Length > (setting.MaxLength ?? int.MaxValue))
                { error = L.Text("{0}: from {1} to {2} characters", setting.Title, setting.MinLength ?? 0, setting.MaxLength ?? int.MaxValue); return null; }
                return JsonValue.Create(text);
            }
            case "choice":
            {
                if (input is JsonNode chosen) return chosen.DeepClone();
                string text = (input as string ?? "").Trim();
                var option = setting.Options.FirstOrDefault(o => Show(o.Value) == text || o.Label == text);
                if (option.Value is not null) return option.Value.DeepClone();
                if (setting.Source.Length > 0 && text.Length > 0) return JsonValue.Create(text);
                error = L.Text("{0}: choose one of its options", setting.Title);
                return null;
            }
            case "choices":
            {
                var values = input as IEnumerable<JsonNode> ?? (input as string ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(t => (JsonNode)JsonValue.Create(t));
                var list = new JsonArray(values.Select(v => v.DeepClone()).ToArray());
                if (list.Count < (setting.MinItems ?? 0) || list.Count > (setting.MaxItems ?? int.MaxValue))
                { error = L.Text("{0}: choose from {1} to {2}", setting.Title, setting.MinItems ?? 0, setting.MaxItems ?? list.Count); return null; }
                return list;
            }
            default:
                error = L.Text("{0}: edit this setting in the file", setting.Title);
                return null;
        }
    }

    /// <summary>The player's file after a change: each edited value that differs from its default, every other value the
    /// player had (those no longer declared too), and nothing equal to a default.</summary>
    public JsonObject Values(IReadOnlyDictionary<string, JsonNode?> edited)
    {
        var result = new JsonObject();
        foreach (var (name, value) in UserValues)
            if (!Settings.Any(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && edited.ContainsKey(s.Name))) result[name] = value?.DeepClone();
        foreach (var setting in Settings)
        {
            if (!edited.TryGetValue(setting.Name, out var value)) continue;
            if (!JsonNode.DeepEquals(Normalize(value), Normalize(setting.Default))) result[setting.Name] = value?.DeepClone();
        }
        return result;
    }

    /// <summary>Numbers compare by value (2 equals 2.0).</summary>
    static JsonNode? Normalize(JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() == JsonValueKind.Number && v.TryGetValue(out double d) ? JsonValue.Create(d) : node;
}
