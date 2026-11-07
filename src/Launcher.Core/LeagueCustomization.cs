using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Launcher.Core;

public sealed partial class GameSystems
{
    public JsonObject Review { get; }

    // A full inventory prevents stale drafts from accepting changed definitions or a newly conflicting profile.
    public static JsonObject CaptureReview(ContentEnvironment environment)
    {
        var result = new JsonObject { ["choices"] = Hash(environment.Choices) };
        void Add(string category, string root, string file)
        {
            if (!ModCatalog.Physical(file) || new FileInfo(file).Length > 1024 * 1024)
                throw new IOException(L.Text("The existing settings cannot be safely read. Nothing was changed."));
            result[category + Path.GetRelativePath(root, file).Replace('\\', '/')] = Hash(file);
        }
        foreach (var file in Manifests(environment.Modules, "module.json")) Add("module/", environment.Modules, file);
        foreach (var manifest in Manifests(environment.Leagues, "league.json"))
        {
            var folder = Path.GetDirectoryName(manifest)!;
            foreach (var name in new[] { "league.json", "modules.json", "settings.json", ".waypoint-league.json" })
                if (File.Exists(Path.Combine(folder, name))) Add("league/", environment.Leagues, Path.Combine(folder, name));
            string source = Path.Combine(folder, "Source");
            if (Directory.Exists(source))
            {
                if (!ModCatalog.Physical(source)) throw new IOException(L.Text("Redirected module paths are not supported."));
                // Only flat, physical source is eligible for creation; inventory every file, including unsupported ones.
                foreach (string file in Directory.GetFiles(source)) Add("league/", environment.Leagues, file);
                if (Directory.GetDirectories(source).Length > 0) result["nested/" + Path.GetFileName(folder)] = "unsupported";
            }
            if (result.Count > 10000) throw new IOException(L.Text("Settings are too deeply nested or too large."));
        }
        return result;
    }

    public bool Rooted(GameLeague league)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (GameLeague? at = league; at is not null; at = Leagues.FirstOrDefault(l => l.Name.Equals(at.Parent, StringComparison.OrdinalIgnoreCase)))
        {
            if (!seen.Add(at.Name)) return false;
            if (at.Parent.Length == 0) return at.Name.Equals("Standard", StringComparison.OrdinalIgnoreCase) || at.Name.Equals("Void", StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }
    public string SourceFamilyName(GameLeague league)
    {
        if (league.Custom) return Leagues.FirstOrDefault(l => !l.Custom && l.Name.Equals(league.Template, StringComparison.OrdinalIgnoreCase)) is { } template
            ? SourceFamilyName(template) : league.Name;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var at = league; seen.Add(at.Name);)
        {
            // Parent is a migration destination, not inheritance. Only a Hardcore death counterpart implies a mode family.
            string parent = at.EntryOf;
            if (parent.Length == 0 && at.Hardcore && !at.Parent.Equals("Void", StringComparison.OrdinalIgnoreCase)) parent = at.Parent;
            var next = Leagues.FirstOrDefault(l => !l.Custom && l.Name.Equals(parent, StringComparison.OrdinalIgnoreCase));
            if (next is null || parent.Length == 0 || !next.Base.Equals(at.Base, StringComparison.OrdinalIgnoreCase)) return at.Name;
            at = next;
        }
        return league.Name;
    }
    public bool Configurable(GameLeague league) => !league.Ended && Rooted(league) && !league.Name.Equals("Void", StringComparison.OrdinalIgnoreCase)
        && league.BaseSystems.Any(p => Id(p).Equals("Game", StringComparison.OrdinalIgnoreCase));
    public IEnumerable<GameLeague> Profiles(GameLeague league) => Leagues.Where(l => !l.Custom && Configurable(l)
        && SourceFamilyName(l).Equals(SourceFamilyName(league), StringComparison.OrdinalIgnoreCase) && l.Base.Equals(league.Base, StringComparison.OrdinalIgnoreCase));
    static bool SameMode(GameLeague a, GameLeague b) => a.Hardcore == b.Hardcore && a.Ssf == b.Ssf
        && a.Form == b.Form && a.DataSet == b.DataSet;
    public string Modes(GameLeague league) => string.Join(" · ", new[] { league.Hardcore ? L.Text("Hardcore") : "",
        league.Ssf ? L.Text("Solo self-found") : "", league.Form.Length > 0 ? L.Text("Ruthless") : "" }.Where(s => s.Length > 0)) is { Length: > 0 } modes
        ? modes : L.Text("Normal");

    public IReadOnlySet<string> RequiredModules(GameLeague league)
    {
        // Core features can be customized. Dedicated rules/content bundles remain intact without an opt-out contract.
        var required = league.BaseSystems.Where(p => !p.StartsWith("Core/", StringComparison.OrdinalIgnoreCase)
            || Id(p).Equals("Game", StringComparison.OrdinalIgnoreCase)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        void AddDependencies(string path)
        {
            foreach (var dependency in Systems.FirstOrDefault(s => s.Path.Equals(path, StringComparison.OrdinalIgnoreCase))?.Depends ?? [])
            {
                var system = Systems.FirstOrDefault(s => s.Id.Equals(dependency, StringComparison.OrdinalIgnoreCase));
                if (system is not null && required.Add(system.Path)) AddDependencies(system.Path);
            }
        }
        foreach (var path in required.ToArray()) AddDependencies(path);
        return required;
    }

    public string? CompositionError(GameLeague league, IEnumerable<string> paths)
    {
        try { ValidateComposition(league, paths.ToArray()); return null; }
        catch (InvalidOperationException e) { return e.Message; }
    }
    public void ValidateComposition(GameLeague league, IReadOnlyCollection<string> paths)
    {
        if (!Rooted(league)) throw new InvalidOperationException(L.Text("This league has a missing or cyclic parent chain. Restore its definition before editing."));
        var required = RequiredModules(league);
        if (required.Any(p => !paths.Contains(p, StringComparer.OrdinalIgnoreCase)))
            throw new InvalidOperationException(L.Text("Keep the required gameplay bundle enabled. Its modules are not declared independently optional."));
        if (!Profiles(league).Where(l => SameMode(l, league)).Any(l => paths.All(p => l.BaseSystems.Contains(p, StringComparer.OrdinalIgnoreCase))))
            throw new InvalidOperationException(L.Text("This module combination is not defined for this league and mode. Unknown combinations are blocked."));
        var active = paths.Select(p => Systems.SingleOrDefault(s => s.Path.Equals(p, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(L.Text("A selected module is missing."))).ToArray();
        if (!active.Any(s => s.Id.Equals("Game", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException(L.Text("The Game module is required for a playable league."));
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Visit(GameSystem module)
        {
            if (done.Contains(module.Id)) return;
            if (!visiting.Add(module.Id)) throw new InvalidOperationException(L.Text("The selected modules contain a dependency cycle."));
            foreach (string id in module.Depends)
                Visit(active.FirstOrDefault(s => s.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException(L.Text("{0} needs {1}. Enable the required module first.", module.Name, id)));
            visiting.Remove(module.Id); done.Add(module.Id);
        }
        foreach (var module in active) Visit(module);
    }
    public bool ModuleOffered(GameLeague league, string path) => Profiles(league).Where(l => SameMode(l, league))
        .Any(l => l.BaseSystems.Contains(path, StringComparer.OrdinalIgnoreCase));

    public IReadOnlyList<GameLeague> VisibilityPlan(GameLeague league, bool visible)
    {
        if (!Environment.CanCreate || league.Ended || !Rooted(league)) throw new InvalidOperationException(L.Text("This league cannot change creation visibility."));
        var changes = new List<GameLeague>(); var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Visit(GameLeague item)
        {
            if (!seen.Add(item.Name)) throw new InvalidOperationException(L.Text("The league card links contain a cycle."));
            if (visible)
            {
                if (item.EntryOf.Length > 0)
                {
                    var parent = Leagues.FirstOrDefault(l => l.Name.Equals(item.EntryOf, StringComparison.OrdinalIgnoreCase));
                    if (parent is null || item.EntryKind is not ("ssf" or "hardcore" or "form")) throw new InvalidOperationException(L.Text("A visible mode option needs its parent card."));
                    Visit(parent);
                }
                if (item.Ended || !Rooted(item) || item.Name.Equals("Void", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException(L.Text("This league cannot change creation visibility."));
                if (!item.Creatable) changes.Add(item);
            }
            else
            {
                foreach (var child in Leagues.Where(l => l.EntryOf.Equals(item.Name, StringComparison.OrdinalIgnoreCase))) Visit(child);
                if (item.Creatable) changes.Add(item);
            }
        }
        Visit(league);
        bool Offered(GameLeague item) => (changes.Contains(item) ? visible : item.Creatable) && !item.Ended && Rooted(item);
        if (!Systems.Any(s => s.Id.Equals("Game", StringComparison.OrdinalIgnoreCase)) || !Leagues.Any(l => Offered(l) && l.EntryOf.Length == 0
            && l.BaseSystems.Any(p => Id(p).Equals("Game", StringComparison.OrdinalIgnoreCase))))
            throw new InvalidOperationException(L.Text("Keep at least one playable league card available for new characters."));
        foreach (var item in Leagues.Where(l => Offered(l) && l.EntryOf.Length > 0))
            if (!Leagues.Any(l => l.Name.Equals(item.EntryOf, StringComparison.OrdinalIgnoreCase) && Offered(l)))
                throw new InvalidOperationException(L.Text("A visible mode option needs its parent card."));
        return changes;
    }

    public static bool EmptyTemplate(string folder)
    {
        string source = Path.Combine(folder, "Source");
        if (!Directory.Exists(source) || !ModCatalog.Physical(source) || Directory.GetDirectories(source).Length > 0) return false;
        string[] files = Directory.GetFiles(source);
        if (files.Length != 1 || Path.GetExtension(files[0]) != ".cs" || !ModCatalog.Physical(files[0]) || new FileInfo(files[0]).Length > 16384) return false;
        string text = Regex.Replace(File.ReadAllText(files[0]), @"/\*[\s\S]*?\*/|//[^\r\n]*", "");
        return Regex.IsMatch(text, @"\A\s*public\s+sealed\s+class\s+[A-Za-z_][A-Za-z0-9_]*\s*:\s*League\s*\{\s*\}\s*\z");
    }
    public static bool SafeLeagueName(string name) => Regex.IsMatch(name, @"\A[A-Za-z][A-Za-z0-9 _-]{2,47}\z") && name == name.Trim()
        && !Regex.IsMatch(name, @"\A(?:CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])\z", RegexOptions.IgnoreCase);

    public string? NewNameError(string name)
    {
        if (!SafeLeagueName(name)) return L.Text("Use 3–48 English letters, numbers, spaces, underscores or hyphens, starting with a letter.");
        if (Leagues.Any(l => l.Name.Equals(name, StringComparison.OrdinalIgnoreCase) || Path.GetFileName(l.Folder).Equals(name, StringComparison.OrdinalIgnoreCase))
            || Choices.Any(p => p.Key.Equals(name, StringComparison.OrdinalIgnoreCase)) || Directory.Exists(Path.Combine(Environment.Leagues, name))
            || File.Exists(Path.Combine(Environment.Leagues, name))) return L.Text("That league name is already in use. Choose a new name.");
        return null;
    }

    public JsonObject PlanCreate(GameLeague template, string name, string description, IEnumerable<string> selected)
    {
        if (!Environment.CanCreate) throw new InvalidOperationException(L.Text("This checkout does not expose supported league creation. Existing leagues remain available for inspection."));
        if (NewNameError(name) is { } nameError) throw new InvalidOperationException(nameError);
        if (template.Custom || !Configurable(template) || !EmptyTemplate(template.Folder))
            throw new InvalidOperationException(L.Text("This league has custom code or no reusable source template. It cannot be safely copied."));
        if (!Leagues.Any(l => l.Name.Equals("Void", StringComparison.OrdinalIgnoreCase) && l.Parent.Length == 0 && !l.Creatable && l.BaseSystems.Length == 0
            && (Directory.Exists(Path.Combine(l.Folder, "Source")) || File.Exists(Path.Combine(l.Folder, "Binaries/league.dll")))))
            throw new InvalidOperationException(L.Text("The Void destination is missing. Restore it before creating an isolated league."));
        if (string.IsNullOrWhiteSpace(description) || description.Length > 2000 || description.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t')))
            throw new InvalidOperationException(L.Text("Enter a description of 1–2000 characters."));
        string[] paths = selected.Select(Normal).ToArray(); ValidateComposition(template, paths);
        return new() { ["template"] = Path.GetFileName(template.Folder), ["name"] = name, ["description"] = description,
            ["modules"] = new JsonArray(paths.Select(p => (JsonNode)JsonValue.Create(p)).ToArray()), ["review"] = Review.DeepClone() };
    }
}
