using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace Launcher.Core;

/// <summary>Where a folder's modules, leagues and its user's module choices are, and what the adapter lets Waypoint change
/// there. <paramref name="ShippedLeaguesFixed"/>: a release, whose own leagues' files an update writes over, so only a
/// league made in Waypoint is hidden or shown there. <paramref name="Mods"/>: the folder of the installed mods when the
/// servers load mods, else "": a mod may belong to leagues and bring leagues (LocalExile's mod guide, "Leagues").</summary>
public sealed record ContentEnvironment(string Modules, string Leagues, string Choices, bool CanChoose, bool CanEditSettings = false, bool CanCreate = false,
    bool ShippedLeaguesFixed = false, string Mods = "");
/// <summary>What a league lists: a module (<c>Modules/**/module.json</c>, <paramref name="Path"/> under Modules/), or a mod
/// of the leagues that list it (mod.json "leagues": "listed"; <paramref name="Path"/> is its id).</summary>
public sealed record GameSystem(string Id, string Path, string Name, string Description, string[] Depends, string Folder, bool Mod = false)
{
    public string SettingsFile => System.IO.Path.Combine(Folder, "settings.json");
    public override string ToString() => Name;
}
public sealed record GameLeague(string Name, string Folder, string[] BaseSystems, string[] Systems, string Description, bool Creatable, bool Ended,
    bool Hardcore = false, bool Ssf = false, string Form = "", string Parent = "", string EntryOf = "", string EntryKind = "",
    string Base = "", string DataSet = "", bool Custom = false, string Template = "", bool HasEntryOf = false)
{
    public string SettingsFile => Path.Combine(Folder, "settings.json");
    /// <summary>The mod that brings it (its Leagues/ folder), or "" for one of the checkout's own.</summary>
    public string Mod { get; init; } = "";
    /// <summary>The mods its league.json names ("mods"): without one that is on, LocalExile does not load it.</summary>
    public string[] Mods { get; init; } = [];
    /// <summary>Its modules.json and "mods" name one entry twice, as written: LocalExile does not load it.</summary>
    public bool Twice { get; init; }
    public bool IsMain => !HasEntryOf && EntryOf.Length == 0;
    public override string ToString() => Name;
}
public sealed record GameLeagueCard(string Name, GameLeague[] Variants, bool Visible = true, bool IsFlag = true)
{
    public GameLeague Primary => Variants.FirstOrDefault(l => l.Name.Equals(Name, StringComparison.OrdinalIgnoreCase)) ?? Variants[0];
    public override string ToString() => Visible ? Name : L.Text("{0} (not shown)", Name);
}

/// <summary>Read-only public manifests. No server assemblies are loaded. User overrides are planned against the reviewed file.</summary>
public sealed partial class GameSystems
{
    public IReadOnlyList<GameSystem> Systems { get; }
    public IReadOnlyList<GameLeague> Leagues { get; }
    readonly string[] _definitionOrder;
    readonly HashSet<string> _onMods;
    /// <summary>What each module and each mod that is on declares about the company it keeps (<see cref="Limits"/>), by id.</summary>
    readonly Dictionary<string, (string[] Conflicts, string[]? Only)> _limits = new(StringComparer.OrdinalIgnoreCase);
    public JsonObject Choices { get; }
    public string ChoicesHash { get; }
    public ContentEnvironment Environment { get; }
    static readonly System.Text.Json.JsonDocumentOptions Jsonc = new() { CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true };
    public static string Hash(string file) => File.Exists(file) ? Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file))) : "missing";
    static JsonObject Read(string file)
    {
        if (!ModCatalog.Physical(file) || new FileInfo(file).Length > 1024 * 1024) throw new IOException(L.Text("The existing settings cannot be safely read. Nothing was changed."));
        return JsonNode.Parse(File.ReadAllText(file), documentOptions: Jsonc) as JsonObject ?? throw new InvalidDataException(L.Text("Settings must be a JSON object."));
    }
    static string Text(JsonObject json, string key, string fallback = "") => json[key]?.GetValue<string>() ?? fallback;
    static string[] Strings(JsonNode? node) => node is null ? [] : node is JsonArray list
        ? list.Select(n => n is JsonValue v && v.TryGetValue(out string? text) ? text : throw new InvalidDataException(L.Text("Invalid module list."))).ToArray()
        : throw new InvalidDataException(L.Text("Invalid module list."));
    static string Normal(string path)
    {
        string value = path.Replace('\\', '/');
        if (value.Split('/').Any(p => p is "" or "." or "..") || value.Contains(':')) throw new InvalidDataException(L.Text("Invalid module path."));
        return value;
    }
    static string Id(string path) => Normal(path).Split('/')[^1];
    static JsonNode? Field(JsonObject json, string key) => json.FirstOrDefault(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value;
    /// <summary>A manifest's "conflicts" and "only", as LocalExile's ModFolders.Limits reads them: these keys exactly, lists
    /// of ids, or no limits from it at all when one of them is something else (LocalExile logs that manifest).
    /// scripts/LeagueData.py's limits too.</summary>
    static (string[] Conflicts, string[]? Only) Limits(JsonObject json)
    {
        string[]? Ids(string key) => !json.TryGetPropertyValue(key, out var node) ? null : node is JsonArray list ? Strings(list) : throw new InvalidDataException();
        try { return (Ids("conflicts") ?? [], Ids("only")); }
        catch (InvalidDataException) { return ([], null); }
    }
    /// <summary>The mods that are on, in the order LocalExile reads them (its ModFolders.On): a folder with a readable
    /// mod.json, not turned off by it or by mod-state.txt. scripts/LeagueData.py reads them the same way.</summary>
    static IEnumerable<(string Id, string Folder, JsonObject Json)> OnMods(string mods)
    {
        if (mods.Length == 0 || !Directory.Exists(mods)) yield break;
        if (!ModCatalog.Physical(mods)) throw new IOException(L.Text("Redirected module paths are not supported."));
        var choices = ModCatalog.ReadChoices(mods);
        foreach (string folder in Directory.GetDirectories(mods).OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal).Take(512))
        {
            string id = Path.GetFileName(folder), file = Path.Combine(folder, "mod.json");
            if (!Readable(folder)) continue;
            JsonObject json;
            // No readable mod.json (a key twice shows when the object is first used): no mod.
            try { json = Read(file); _ = json.Count; }
            catch (Exception e) when (e is System.Text.Json.JsonException or InvalidDataException or InvalidOperationException or ArgumentException
                or IOException or UnauthorizedAccessException) { continue; }
            if (choices.GetValueOrDefault(id, Field(json, "enabled") is not JsonValue enabled || !enabled.TryGetValue(out bool on) || on)) yield return (id, folder, json);
        }
    }
    /// <summary>A mod folder GameSystems and the review read: a valid id, a physical folder and mod.json of at most 1 MB.
    /// scripts/LeagueData.py's mod_folders takes the same.</summary>
    internal static bool Readable(string folder)
    {
        string file = Path.Combine(folder, "mod.json");
        return ModCatalog.ValidId(Path.GetFileName(folder)) && File.Exists(file) && ModCatalog.Physical(folder) && ModCatalog.Physical(file)
            && new FileInfo(file).Length <= 1024 * 1024;
    }
    /// <summary>Whether this mod is on, for a dependency or a league that names it.</summary>
    public bool ModOn(string id) => _onMods.Contains(id);
    internal static IEnumerable<string> Manifests(string folder, string manifest)
    {
        if (!Directory.Exists(folder)) yield break;
        var pending = new Stack<(string Folder, int Depth)>(); pending.Push((folder, 0)); int count = 0;
        while (pending.TryPop(out var at))
        {
            if (++count > 5000 || at.Depth > 16) throw new IOException(L.Text("Settings are too deeply nested or too large."));
            if (!ModCatalog.Physical(at.Folder)) throw new IOException(L.Text("Redirected module paths are not supported."));
            string file = Path.Combine(at.Folder, manifest);
            if (File.Exists(file)) { yield return file; continue; }
            foreach (string child in Directory.GetDirectories(at.Folder).Where(p => Path.GetFileName(p) is not ("bin" or "obj" or ".git" or "Source" or "Binaries"))) pending.Push((child, at.Depth + 1));
        }
    }
    public GameSystems(ContentEnvironment environment)
    {
        Environment = environment;
        var reviewed = CaptureReview(environment);
        var mods = OnMods(environment.Mods).ToArray();
        _onMods = mods.Select(m => m.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // LocalExile reads the checkout's leagues first, then each mod's.
        (string Root, string Mod)[] roots = [(environment.Leagues, ""), .. mods.Select(m => (Path.Combine(m.Folder, "Leagues"), m.Id))];
        _definitionOrder = [.. roots.Where(r => Directory.Exists(r.Root)).SelectMany(r => Directory.GetDirectories(r.Root))];
        var systems = new List<GameSystem>();
        foreach (string file in Manifests(environment.Modules, "module.json"))
        {
            var json = Read(file); string folder = Path.GetDirectoryName(file)!;
            string path = Normal(Path.GetRelativePath(environment.Modules, folder));
            string id = Text(json, "id", Path.GetFileName(folder));
            if (!ModCatalog.ValidId(id) || id != Path.GetFileName(folder) || systems.Any(s => s.Id.Equals(id, StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException(L.Text("Invalid or duplicate module ID."));
            _limits[id] = Limits(json);
            systems.Add(new(id, path, Text(json, "name", id), Text(json, "description"), Strings(json["depends"]), folder));
        }
        // Every mod that is on declares its limits, as LocalExile reads them, but one with a module's id.
        foreach (var mod in mods) _limits.TryAdd(mod.Id, Limits(mod.Json));
        // A mod of the leagues that list it is listed by its id. One with a module's id does not load (the module runs), nor
        // does one named exactly as a league's folder, nor one whose depends is not a list of ids (LocalExile's ModHost).
        foreach (var mod in mods)
        {
            if (Field(mod.Json, "leagues") is not JsonValue scope || !scope.TryGetValue(out string? listed) || listed != "listed"
                || systems.Any(s => s.Id.Equals(mod.Id, StringComparison.OrdinalIgnoreCase)) || _definitionOrder.Any(d => Path.GetFileName(d) == mod.Id)) continue;
            if (mod.Json.Any(p => p.Key.Equals("depends", StringComparison.OrdinalIgnoreCase)) && Field(mod.Json, "depends") is not JsonArray) continue;
            string[] depends;
            try { depends = Strings(Field(mod.Json, "depends")); }
            catch (InvalidDataException) { continue; }
            systems.Add(new(mod.Id, mod.Id, Field(mod.Json, "name") is JsonValue name && name.TryGetValue(out string? title) && title.Length > 0 ? title : mod.Id,
                Field(mod.Json, "description") is JsonValue text && text.TryGetValue(out string? description) ? description : "", depends, mod.Folder, Mod: true));
        }
        // An entry names a mod by its id in any case, also as the last part of the path it had as a module.
        string Canon(string path) => systems.FirstOrDefault(s => s.Mod && s.Id.Equals(Id(path), StringComparison.OrdinalIgnoreCase))?.Path ?? Normal(path);
        Systems = systems.OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        ChoicesHash = Hash(environment.Choices);
        Choices = File.Exists(environment.Choices) ? Read(environment.Choices) : new();
        var leagues = new List<GameLeague>();
        // A mod's leagues as LocalExile reads them: the folders directly in its Leagues/, with a league.json and Source/ or
        // Binaries/. One that cannot be read is not there, as for LocalExile, which loads the others.
        var files = Manifests(environment.Leagues, "league.json").Select(f => (File: f, Mod: ""))
            .Concat(roots.Skip(1).Where(r => Directory.Exists(r.Root)).SelectMany(r => Directory.GetDirectories(r.Root).Order(StringComparer.Ordinal)
                .Where(d => File.Exists(Path.Combine(d, "league.json")) && (Directory.Exists(Path.Combine(d, "Source")) || Directory.Exists(Path.Combine(d, "Binaries"))))
                .Select(d => (File: Path.Combine(d, "league.json"), r.Mod))));
        foreach (var (file, mod) in files)
        {
            if (mod.Length == 0) { leagues.Add(League(file, mod)!); continue; }
            try { if (League(file, mod) is { } added) leagues.Add(added); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException
                or System.Text.Json.JsonException or ArgumentException or FormatException) { }
        }
        GameLeague? League(string file, string mod)
        {
            var json = Read(file); _ = json.Count; string folder = Path.GetDirectoryName(file)!;
            string name = Text(json, "name", Path.GetFileName(folder));
            bool known = leagues.Any(l => l.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            // A mod's league of a name that is taken is not loaded: the first of a name stays.
            if (mod.Length > 0 && known) return null;
            if (!ModSettingsForm.ValidSettingsId(Path.GetFileName(folder), "leagues") || known) throw new InvalidDataException(L.Text("Invalid or duplicate league."));
            // What it is made of: its modules, then the mods its league.json names (ids, as LocalExile reads them).
            var list = Read(Path.Combine(folder, "modules.json"));
            if (json.ContainsKey("mods") && json["mods"] is not JsonArray || list.ContainsKey("modules") && list["modules"] is not JsonArray)
                throw new InvalidDataException(L.Text("Invalid module list."));
            string[] own = Strings(json["mods"]);
            if (own.Any(m => m.IndexOfAny(['/', '\\']) >= 0)) throw new InvalidDataException(L.Text("Invalid module list."));
            string[] raw = [.. Strings(list["modules"]), .. own];
            string[] original = Strings(list["modules"]).Select(Canon).ToArray();
            original = [.. original, .. own.Select(Canon).Where(m => !original.Any(p => Id(p).Equals(Id(m), StringComparison.OrdinalIgnoreCase)))];
            var selected = original.ToList();
            var entry = Choices.FirstOrDefault(p => p.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
            if (entry is not (null or JsonObject)) throw new InvalidDataException(L.Text("Invalid module list."));
            var changes = entry as JsonObject;
            if (changes is not null)
            {
                foreach (string removed in Strings(changes["remove"])) if (!Id(removed).Equals("Game", StringComparison.OrdinalIgnoreCase)) selected.RemoveAll(p => Id(p).Equals(Id(removed), StringComparison.OrdinalIgnoreCase));
                foreach (string added in Strings(changes["add"])) if (!selected.Any(p => Id(p).Equals(Id(added), StringComparison.OrdinalIgnoreCase))) selected.Add(Canon(added));
                // A change that breaks what a module or mod declares is skipped by LocalExile (its ChangeModules): the league runs its own list.
                if (Clash(selected) is not null) selected = [.. original];
            }
            string description = Text(json, "description");
            return new(name, folder, original, selected.ToArray(), description.Length > 0 ? description : "The " + name + " league.", json["creatable"]?.GetValue<bool>() ?? true, json["ended"]?.GetValue<bool>() ?? false,
                json["hardcore"]?.GetValue<bool>() ?? false, json["ssf"]?.GetValue<bool>() ?? false, Text(json, "form"), Text(json, "parent"), Text(json, "entryOf"), Text(json, "entryKind"),
                Text(json, "base", name), Text(json, "dataSet"), File.Exists(Path.Combine(folder, ".waypoint-league.json")),
                File.Exists(Path.Combine(folder, ".waypoint-league.json")) ? Text(Read(Path.Combine(folder, ".waypoint-league.json")), "template") : "", json["entryOf"] is JsonValue)
                { Mod = mod, Mods = own, Twice = raw.Distinct(StringComparer.OrdinalIgnoreCase).Count() != raw.Length };
        }
        Leagues = leagues.OrderBy(l => l.Name == "Standard" ? "" : l.Name).ToArray();
        if (!JsonNode.DeepEquals(reviewed, CaptureReview(environment))) throw new IOException(L.Text("Settings changed while reading. Refresh before editing."));
        Review = reviewed;
    }
    public JsonObject Plan(GameLeague league, IEnumerable<string> selected)
    {
        if (!Environment.CanChoose) throw new InvalidOperationException(L.Text("This server does not expose supported league system overrides. Update LocalExile to edit these choices."));
        if (!Configurable(league)) throw new InvalidOperationException(L.Text("Ended or non-playable league definitions are read-only."));
        var paths = selected.Select(Normal).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        ValidateComposition(league, paths);
        var result = (JsonObject)Choices.DeepClone();
        string key = result.Select(p => p.Key).FirstOrDefault(k => k.Equals(league.Name, StringComparison.OrdinalIgnoreCase)) ?? league.Name;
        var change = result[key] as JsonObject ?? new JsonObject();
        change["add"] = new JsonArray(paths.Where(p => !league.BaseSystems.Contains(p, StringComparer.OrdinalIgnoreCase)).Select(p => (JsonNode)JsonValue.Create(p)).ToArray());
        change["remove"] = new JsonArray(league.BaseSystems.Where(p => !paths.Contains(p, StringComparer.OrdinalIgnoreCase)).Select(p => (JsonNode)JsonValue.Create(p)).ToArray());
        result[key] = change;
        return result;
    }
}
