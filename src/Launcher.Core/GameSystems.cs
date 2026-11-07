using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace Launcher.Core;

public sealed record ContentEnvironment(string Modules, string Leagues, string Choices, bool CanChoose, bool CanEditSettings = false, bool CanCreate = false);
public sealed record GameSystem(string Id, string Path, string Name, string Description, string[] Depends, string Folder)
{
    public string SettingsFile => System.IO.Path.Combine(Folder, "settings.json");
    public override string ToString() => Name;
}
public sealed record GameLeague(string Name, string Folder, string[] BaseSystems, string[] Systems, string Description, bool Creatable, bool Ended,
    bool Hardcore = false, bool Ssf = false, string Form = "", string Parent = "", string EntryOf = "", string EntryKind = "",
    string Base = "", string DataSet = "", bool Custom = false, string Template = "")
{
    public string SettingsFile => Path.Combine(Folder, "settings.json");
    public override string ToString() => Name;
}
public sealed record GameLeagueFamily(string Name, GameLeague[] Variants)
{
    public GameLeague Primary => Variants.FirstOrDefault(l => l.Name.Equals(Name, StringComparison.OrdinalIgnoreCase)) ?? Variants[0];
    public override string ToString() => Name;
}

/// <summary>Read-only public manifests. No server assemblies are loaded. User overrides are planned against the reviewed file.</summary>
public sealed partial class GameSystems
{
    public IReadOnlyList<GameSystem> Systems { get; }
    public IReadOnlyList<GameLeague> Leagues { get; }
    public IReadOnlyList<GameLeagueFamily> Families => Leagues.GroupBy(l => (l.Custom ? "custom/" + l.Name : "family/" + SourceFamilyName(l)), StringComparer.OrdinalIgnoreCase)
        .Select(g => new GameLeagueFamily(g.First().Custom ? g.First().Name : SourceFamilyName(g.First()), g.ToArray()))
        .OrderBy(f => f.Name == "Standard" ? "" : f.Name).ToArray();
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
    static IEnumerable<string> Manifests(string folder, string manifest)
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
        var systems = new List<GameSystem>();
        foreach (string file in Manifests(environment.Modules, "module.json"))
        {
            var json = Read(file); string folder = Path.GetDirectoryName(file)!;
            string path = Normal(Path.GetRelativePath(environment.Modules, folder));
            string id = Text(json, "id", Path.GetFileName(folder));
            if (!ModCatalog.ValidId(id) || id != Path.GetFileName(folder) || systems.Any(s => s.Id.Equals(id, StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException(L.Text("Invalid or duplicate module ID."));
            systems.Add(new(id, path, Text(json, "name", id), Text(json, "description"), Strings(json["depends"]), folder));
        }
        Systems = systems.OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        ChoicesHash = Hash(environment.Choices);
        Choices = File.Exists(environment.Choices) ? Read(environment.Choices) : new();
        var leagues = new List<GameLeague>();
        foreach (string file in Manifests(environment.Leagues, "league.json"))
        {
            var json = Read(file); string folder = Path.GetDirectoryName(file)!;
            string name = Text(json, "name", Path.GetFileName(folder));
            if (!ModSettingsForm.ValidSettingsId(Path.GetFileName(folder), "leagues") || leagues.Any(l => l.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException(L.Text("Invalid or duplicate league."));
            string[] original = Strings(Read(Path.Combine(folder, "modules.json"))["modules"]).Select(Normal).ToArray();
            var selected = original.ToList();
            var entry = Choices.FirstOrDefault(p => p.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
            if (entry is not (null or JsonObject)) throw new InvalidDataException(L.Text("Invalid module list."));
            var changes = entry as JsonObject;
            if (changes is not null)
            {
                foreach (string removed in Strings(changes["remove"])) if (!Id(removed).Equals("Game", StringComparison.OrdinalIgnoreCase)) selected.RemoveAll(p => Id(p).Equals(Id(removed), StringComparison.OrdinalIgnoreCase));
                foreach (string added in Strings(changes["add"])) if (!selected.Any(p => Id(p).Equals(Id(added), StringComparison.OrdinalIgnoreCase))) selected.Add(Normal(added));
            }
            leagues.Add(new(name, folder, original, selected.ToArray(), Text(json, "description", "The " + name + " league."), json["creatable"]?.GetValue<bool>() ?? true, json["ended"]?.GetValue<bool>() ?? false,
                json["hardcore"]?.GetValue<bool>() ?? false, json["ssf"]?.GetValue<bool>() ?? false, Text(json, "form"), Text(json, "parent"), Text(json, "entryOf"), Text(json, "entryKind"),
                Text(json, "base", name), Text(json, "dataSet"), File.Exists(Path.Combine(folder, ".waypoint-league.json")),
                File.Exists(Path.Combine(folder, ".waypoint-league.json")) ? Text(Read(Path.Combine(folder, ".waypoint-league.json")), "template") : ""));
        }
        Leagues = leagues.OrderBy(l => l.Name == "Standard" ? "" : l.Name).ToArray();
        if (!JsonNode.DeepEquals(reviewed, CaptureReview(environment))) throw new IOException(L.Text("Settings changed while reading. Refresh before editing."));
        Review = reviewed;
    }
    public JsonObject Plan(GameLeague league, IEnumerable<string> selected)
    {
        if (!Environment.CanChoose) throw new InvalidOperationException(L.Text("This server does not expose supported league system overrides. Update LocalExile to edit these choices."));
        if (league.Systems.Any(p => !Systems.Any(s => s.Path.Equals(p, StringComparison.OrdinalIgnoreCase)))) throw new InvalidOperationException(L.Text("A selected module is missing."));
        if (!Configurable(league)) throw new InvalidOperationException(L.Text("Ended or non-playable league definitions are read-only."));
        var paths = selected.Select(Normal).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var active = paths.Select(p => Systems.SingleOrDefault(s => s.Path.Equals(p, StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidOperationException(L.Text("A selected module is missing."))).ToArray();
        if (league.Creatable && !active.Any(s => s.Id.Equals("Game", StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException(L.Text("The Game module is required for a playable league."));
        foreach (var system in active)
            foreach (string dependency in system.Depends)
                if (!active.Any(s => s.Id.Equals(dependency, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException(L.Text("{0} needs {1}. Enable the required module first.", system.Name, dependency));
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
