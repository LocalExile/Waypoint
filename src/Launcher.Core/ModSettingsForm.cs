using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Launcher.Core;

/// <summary>One setting in LocalExile's shared declaration contract, including nested collection and group controls.</summary>
public sealed record ModSetting(string Name, string Type, JsonNode? Default, string Label, string Description, string Unit, string Format,
    double? Min, double? Max, IReadOnlyList<(JsonNode Value, string Label)> Options, string Source, int? MinItems, int? MaxItems,
    int? MinLength, int? MaxLength, string Applies, bool Advanced,
    double? Step = null, string Pattern = "", bool Integers = false, ModSetting? Item = null, ModSetting? Value = null,
    IReadOnlyList<ModSetting>? Children = null, JsonObject? When = null)
{
    public static readonly string[] EditableTypes = ["bool", "integer", "number", "range", "choice", "choices", "text", "list", "map", "group"];
    public bool Editable => EditableTypes.Contains(Type);
    public string Title => Label.Length > 0 ? Label : Name;
    /// <summary>A percent is kept as a fraction (0.15) and shown as 15.</summary>
    public bool IsPercent => Format == "percent" && Type is "number" or "integer";
    public double DisplayScale => Format == "percent" ? 100 : 1;
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
    public string UserHash { get; private set; } = "missing";

    ModSettingsForm(string id, IReadOnlyList<ModSetting> settings, JsonObject user) => (Id, Settings, UserValues) = (id, settings, user);

    /// <summary>The player's values file of a mod, relative to the checkout's root.</summary>
    public static bool ValidSettingsId(string id, string kind) => kind == "leagues"
        ? id.Length is > 0 and <= 128 && id is not ("." or "..") && id.IndexOfAny(['/', '\\', ':', '*', '?', '"', '<', '>', '|']) < 0 && !id.Any(char.IsControl)
        : ModCatalog.ValidId(id);
    public static string UserFile(string id, string kind = "mods") => ValidSettingsId(id, kind)
        ? Path.Combine("data", ValidKind(kind), id + ".json") : throw new ArgumentException(L.Text("Unsupported settings ID."));
    static string ValidKind(string kind) => kind is "mods" or "modules" or "leagues" ? kind : throw new ArgumentException(L.Text("Unknown settings kind."));

    /// <summary>Reads the declarations and the player's values; throws for a declarations file Waypoint cannot read.</summary>
    public static ModSettingsForm Load(string id, string declarationsPath, string root, string kind = "mods")
    {
        if (!ValidSettingsId(id, kind)) throw new InvalidDataException(L.Text("Unsupported settings ID."));
        if (!ModCatalog.Physical(declarationsPath)) throw new IOException(L.Text("Redirected settings paths are not supported."));
        if (new FileInfo(declarationsPath).Length > 256 * 1024) throw new InvalidDataException(L.Text("{0} is larger than 256 KB", DeclarationsFile));
        string declarations = File.ReadAllText(declarationsPath);
        var settings = Parse(declarations);
        var user = new JsonObject();
        string userPath = Path.Combine(root, UserFile(id, kind));
        string userHash = GameSystems.Hash(userPath);
        if (File.Exists(userPath))
        {
            if (!ModCatalog.Physical(userPath) || new FileInfo(userPath).Length > 256 * 1024) throw new IOException(L.Text("The existing settings cannot be safely read. Nothing was changed."));
            user = JsonNode.Parse(File.ReadAllText(userPath), NodeOptions, Jsonc) as JsonObject
                ?? throw new InvalidDataException(L.Text("Settings must be a JSON object."));
        }
        if (JsonNode.Parse(declarations, NodeOptions, Jsonc)?["renamed"] is JsonObject renamed)
            foreach (var (old, next) in renamed)
                if (next is JsonValue name && name.TryGetValue(out string? target) && settings.Any(s => s.Name == target)
                    && user[old] is { } previous)
                { if (!user.ContainsKey(target!)) user[target!] = previous.DeepClone(); user.Remove(old); }
        if (userHash != GameSystems.Hash(userPath)) throw new IOException(L.Text("Settings changed while reading. Refresh before editing."));
        return new(id, settings, user) { UserHash = userHash };
    }

    internal static IReadOnlyList<ModSetting> Parse(string json)
    {
        if (JsonNode.Parse(json, NodeOptions, Jsonc) is not JsonObject root || root["settings"] is not JsonObject settings)
            throw new InvalidDataException(L.Text("{0} has no settings object", DeclarationsFile));
        return ParseSettings(settings, 0);
    }

    static IReadOnlyList<ModSetting> ParseSettings(JsonObject settings, int depth, bool needsDefault = true)
    {
        if (depth > 12 || settings.Count > 1000) throw new InvalidDataException(L.Text("Settings are too deeply nested or too large."));
        var result = new List<ModSetting>();
        foreach (var (name, node) in settings)
        {
            if (node is not JsonObject d) throw new InvalidDataException(L.Text("Invalid setting: {0}", name));
            InvalidDataException Invalid() => new(L.Text("Invalid setting: {0}", name));
            string Text(string key) => d[key] is null ? "" : d[key] is JsonValue v && v.TryGetValue(out string? s) ? s : throw Invalid();
            double? Number(string key) => d[key] is null ? null : d[key] is JsonValue v && v.TryGetValue(out double x) && double.IsFinite(x) ? x : throw Invalid();
            int? Whole(string key) => d[key] is null ? null : d[key] is JsonValue v && v.TryGetValue(out int x) && x >= 0 ? x : throw Invalid();
            var options = d["options"] is JsonArray list
                ? list.Where(o => o is not null).Select(o => o is JsonObject labelled && labelled["value"] is { } value
                    ? (value.DeepClone(), labelled["label"] is JsonValue l && l.TryGetValue(out string? label) ? label : Show(value))
                    : (o!.DeepClone(), Show(o!))).ToList()
                : [];
            string type = Text("type");
            if (!ModSetting.EditableTypes.Contains(type)) throw new InvalidDataException(L.Text("Unsupported setting type: {0}", type));
            if (d["when"] is not (null or JsonObject) || d["advanced"] is { } advanced && advanced.GetValueKind() is not (JsonValueKind.True or JsonValueKind.False)
                || d["options"] is not null && (d["options"] is not JsonArray { Count: > 0 } || options.Count == 0 || d["source"] is not null)
                || type is "choice" or "choices" && options.Count == 0 && Text("source").Length == 0
                || Text("format") is not ("" or "percent" or "duration" or "multiplier")
                || Text("applies") is not ("" or "now" or "nextArea" or "restart")
                || Text("of") is not ("" or "integer" or "number") || Text("of").Length > 0 && type != "range") throw Invalid();
            if (Text("pattern").Length > 0)
                try { _ = new System.Text.RegularExpressions.Regex(Text("pattern"), System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromMilliseconds(100)); }
                catch (ArgumentException) { throw Invalid(); }
            var children = type == "group" && d["settings"] is JsonObject group ? ParseSettings(group, depth + 1) : null;
            ModSetting? Nested(string key) => d[key] is JsonObject child ? ParseSettings(new JsonObject { [key] = child.DeepClone() }, depth + 1, needsDefault: false)[0] : null;
            JsonNode? defaults = children is null ? d["default"]?.DeepClone() : new JsonObject(children.Select(c => KeyValuePair.Create(c.Name, c.Default?.DeepClone())));
            result.Add(new(name, type, defaults, Text("label"), Text("description"), Text("unit"), Text("format"),
                Number("min"), Number("max"), options, Text("source"), Whole("minItems"), Whole("maxItems"), Whole("minLength"), Whole("maxLength"),
                Text("applies") is { Length: > 0 } applies ? applies : "now", d["advanced"] is JsonValue a && a.TryGetValue(out bool adv) && adv,
                Number("step"), Text("pattern"), Text("of") == "integer", type == "list" ? Nested("item") : null,
                type == "map" ? Nested("value") : null, children, d["when"] as JsonObject));
            var setting = result[^1];
            if (setting.Min > setting.Max || setting.Step is <= 0 || type == "list" && setting.Item is null || type == "map" && setting.Value is null || type == "group" && children is null)
                throw new InvalidDataException(L.Text("Invalid setting: {0}", name));
            if (needsDefault && (setting.Default is null || Validate(setting, setting.Default) is not null)) throw new InvalidDataException(L.Text("Invalid default for setting: {0}", name));
        }
        return result;
    }

    /// <summary>The value in effect: the player's when it is set, the default otherwise.</summary>
    public JsonNode? Current(ModSetting setting) =>
        Merge(setting, UserValues.FirstOrDefault(p => p.Key.Equals(setting.Name, StringComparison.OrdinalIgnoreCase)).Value);

    public static JsonNode? Merge(ModSetting setting, JsonNode? value)
    {
        if (setting.Type == "group" && setting.Children is { } children)
            return new JsonObject(children.Select(c => KeyValuePair.Create(c.Name, Merge(c, (value as JsonObject)?.FirstOrDefault(p => p.Key.Equals(c.Name, StringComparison.OrdinalIgnoreCase)).Value))));
        return (value ?? setting.Default)?.DeepClone();
    }

    /// <summary>A value as the form shows it (a percent as its hundredfold).</summary>
    public static string Show(JsonNode? value, ModSetting? setting = null)
    {
        if (value is JsonValue v && v.TryGetValue(out double number) && v.GetValueKind() == JsonValueKind.Number)
            return (setting?.IsPercent == true ? number * 100 : number).ToString("G", CultureInfo.InvariantCulture);
        if (value is JsonValue s && s.TryGetValue(out string? text)) return text;
        return value?.ToJsonString() ?? "";
    }

    /// <summary>What the player typed or chose, as the setting's value; null with <paramref name="error"/> when it does not fit
    /// its declaration. A choice is one of its options, or, for one from the game's values, any text.</summary>
    public static JsonNode? Read(ModSetting setting, object? input, out string? error)
    {
        var value = input is JsonNode node ? node.DeepClone() : ReadInput(setting, input, out error);
        error = Validate(setting, value);
        return error is null ? value : null;
    }

    static JsonNode? ReadInput(ModSetting setting, object? input, out string? error)
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
                return JsonValue.Create(number);
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

    public static string? Validate(ModSetting setting, JsonNode? value)
    {
        string? Invalid() => L.Text("{0}: the value does not match its declaration", setting.Title);
        if (value is null) return Invalid();
        var kind = value.GetValueKind();
        double Num(JsonNode n) => double.Parse(n.ToJsonString(), CultureInfo.InvariantCulture);
        bool Numeric(JsonNode? n, bool whole = false) => n?.GetValueKind() == JsonValueKind.Number && Num(n) is var d && double.IsFinite(d)
            && (!whole || d == Math.Floor(d)) && !(d < setting.Min || d > setting.Max)
            && (setting.Step is not { } step || Math.Abs((d - (setting.Min ?? 0)) / step - Math.Round((d - (setting.Min ?? 0)) / step)) <= 1e-9);
        bool Allowed(JsonNode? n) => setting.Options.Count > 0 ? setting.Options.Any(o => Equal(o.Value, n))
            : setting.Source.Length > 0 && n?.GetValueKind() == JsonValueKind.String && n.GetValue<string>().Length > 0;
        bool Count(int n) => !(n < setting.MinItems || n > setting.MaxItems);
        switch (setting.Type)
        {
            case "bool": return kind is JsonValueKind.True or JsonValueKind.False ? null : Invalid();
            case "number" or "integer": return Numeric(value, setting.Type == "integer") ? null : Invalid();
            case "range": return value is JsonArray { Count: 2 } pair && pair.All(n => Numeric(n, setting.Integers))
                && Num(pair[0]!) <= Num(pair[1]!) ? null : Invalid();
            case "choice": return Allowed(value) ? null : Invalid();
            case "choices": return value is JsonArray choices && Count(choices.Count) && choices.All(Allowed)
                && !choices.Where((n, i) => choices.Take(i).Any(p => Equal(p, n))).Any() ? null : Invalid();
            case "text":
                if (kind != JsonValueKind.String) return Invalid();
                string text = value.GetValue<string>();
                if (text.Length < setting.MinLength || text.Length > setting.MaxLength) return Invalid();
                try { return setting.Pattern.Length == 0 || System.Text.RegularExpressions.Regex.IsMatch(text, "\\A(?:" + setting.Pattern + ")\\z",
                    System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromMilliseconds(100)) ? null : Invalid(); }
                catch (Exception e) when (e is ArgumentException or System.Text.RegularExpressions.RegexMatchTimeoutException) { return Invalid(); }
            case "list": return value is JsonArray list && Count(list.Count) && setting.Item is { } item
                && list.All(n => Validate(item, n) is null) ? null : Invalid();
            case "map": return value is JsonObject map && setting.Value is { } entry
                && map.All(p => (setting.Options.Count == 0 && setting.Source.Length == 0 || Allowed(JsonValue.Create(p.Key)))
                    && Validate(entry, p.Value) is null) ? null : Invalid();
            case "group": return value is JsonObject group && setting.Children is { } children
                && children.All(c => group.FirstOrDefault(p => p.Key.Equals(c.Name, StringComparison.OrdinalIgnoreCase)).Value is not { } child
                    || Validate(c, child) is null) ? null : Invalid();
            default: return Invalid();
        }
    }

    public static bool Equal(JsonNode? a, JsonNode? b) => (a, b) switch
    {
        (JsonValue x, JsonValue y) when x.GetValueKind() == JsonValueKind.Number && y.GetValueKind() == JsonValueKind.Number =>
            double.Parse(x.ToJsonString(), CultureInfo.InvariantCulture) == double.Parse(y.ToJsonString(), CultureInfo.InvariantCulture),
        (JsonArray x, JsonArray y) => x.Count == y.Count && x.Zip(y).All(p => Equal(p.First, p.Second)),
        (JsonObject x, JsonObject y) => x.Count == y.Count && x.All(p => y.TryGetPropertyValue(p.Key, out var v) && Equal(p.Value, v)),
        _ => JsonNode.DeepEquals(a, b),
    };

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
            var preserved = PreserveUnknown(setting, value, UserValues.FirstOrDefault(p => p.Key.Equals(setting.Name, StringComparison.OrdinalIgnoreCase)).Value);
            if (setting.Type == "group" ? preserved is JsonObject { Count: > 0 } : !Equal(value, setting.Default)) result[setting.Name] = preserved;
        }
        return result;
    }

    static JsonNode? PreserveUnknown(ModSetting setting, JsonNode? value, JsonNode? previous)
    {
        if (setting.Type != "group" || value is not JsonObject group) return value?.DeepClone();
        var old = previous as JsonObject ?? new JsonObject();
        var saved = (JsonObject)old.DeepClone();
        foreach (var child in setting.Children ?? [])
        {
            foreach (string key in saved.Select(p => p.Key).Where(k => k.Equals(child.Name, StringComparison.OrdinalIgnoreCase)).ToArray()) saved.Remove(key);
            if (group[child.Name] is not { } edited) continue;
            var preserved = PreserveUnknown(child, edited, old.FirstOrDefault(p => p.Key.Equals(child.Name, StringComparison.OrdinalIgnoreCase)).Value);
            if (child.Type == "group" ? preserved is JsonObject { Count: > 0 } : !Equal(edited, child.Default)) saved[child.Name] = preserved;
        }
        return saved;
    }

}
