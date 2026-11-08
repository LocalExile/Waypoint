using System.Security.Cryptography;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Launcher.Core;

public sealed record ModEnvironment(bool Supported, string Folder, bool Enabled, string RuntimeReport = "", string CheckedUtc = "");
/// <summary>A mod operation's arguments. <paramref name="Staged"/> is a checked folder (InstallMod) or values file
/// (SaveModSettings) under Waypoint's own data folder; <paramref name="Target"/> is Binaries or Source.</summary>
/// <summary>A mod operation's mod; <paramref name="Batch"/> names several at once, for one InstallMod, UninstallMod or
/// BuildMod run over all of them.</summary>
public sealed record ModRequest(string Id = "", string Project = "", string Staged = "", string Target = "",
    IReadOnlyList<ModRequest>? Batch = null, string ExpectedHash = "");
public sealed record ModEntry(string Id, string Version, string Kind, string[] Depends, bool Enabled, string Status,
    string Detail, string Folder, string Project, string Fingerprint, string Name = "", string Description = "", string Author = "",
    bool IsScript = false, IReadOnlyList<string>? ClientVersions = null)
{
    /// <summary>The manifest's name, or the id for a mod without one.</summary>
    public string Title => Name.Length > 0 ? Name : Id;
    /// <summary>The declarations of its settings (settings.json beside its mod.json), when it has any.</summary>
    public string SettingsFile => Folder.Length > 0 && File.Exists(Path.Combine(Folder, ModSettingsForm.DeclarationsFile)) ? Path.Combine(Folder, ModSettingsForm.DeclarationsFile) : "";
    public string SearchText => $"{Id} {Name} {Author} {Description} {Version} {Kind} {Status} {Detail}";
    public string DisplayDetail => L.Describe(Detail);
    public string Summary => $"{Version} · {L.Text(Kind)} · " + (Status == "Disabled" ? L.Text("Disabled") : $"{L.Text(Enabled ? "Enabled" : "Disabled")} · {L.Text(Status)}");
}

/// <summary>Reads LocalExile's public mod file contract without loading mod assemblies or running project code.</summary>
public static class ModCatalog
{
    /// <summary>The source hook added in LocalExile #685. A release or unreadable/older source is unknown, not assumed live.</summary>
    public static bool SupportsLiveChoices(string root)
    {
        string path = Path.Combine(root, "src", "GameServer.Gateway", "Modding", "ModHost.cs");
        try
        {
            return File.Exists(path) && Regex.IsMatch(Read(path),
                @"\belse\s+if\s*\(\s*id\s*==\s*StateFile\s*\)\s*busy\s*=\s*!\s*ChoicesChanged\s*\(");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    static readonly ConcurrentDictionary<string, (long Length, DateTime Written, string Hash)> Fingerprints = new(StringComparer.OrdinalIgnoreCase);
    static readonly JsonDocumentOptions JsonOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
    public static bool ValidId(string value) => Regex.IsMatch(value, @"\A[A-Za-z0-9][A-Za-z0-9_.-]{0,127}\z") && value is not "." and not "..";
    public static bool Physical(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
        return true;
    }
    static string Read(string path)
    {
        if (!Physical(path)) throw new IOException("Redirected mod paths are not supported.");
        if (new FileInfo(path).Length > 128 * 1024) throw new IOException("Mod metadata exceeds 128 KB.");
        return File.ReadAllText(path);
    }
    static string Fingerprint(string path)
    {
        var info = new FileInfo(path);
        if (Fingerprints.TryGetValue(path, out var previous) && previous.Length == info.Length && previous.Written == info.LastWriteTimeUtc) return previous.Hash;
        using var stream = File.OpenRead(path);
        string hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        if (Fingerprints.Count > 1024) Fingerprints.Clear();
        Fingerprints[path] = (info.Length, info.LastWriteTimeUtc, hash);
        return hash;
    }
    sealed record Manifest(string Id, string Version, string Kind, string[] Depends, string Assembly, bool Enabled,
        string Name = "", string Description = "", string Author = "", string[]? ClientVersions = null);
    /// <summary>The assembly a manifest names (its id's .dll by default).</summary>
    internal static string AssemblyName(string folderId, string json) => ParseJson(folderId, json).Assembly;
    static Manifest Parse(string folderId, string path) => ParseJson(folderId, Read(path));
    internal static (string Version, bool Enabled) ManifestDefaults(string folderId, string json)
    {
        var manifest = ParseJson(folderId, json);
        if (manifest.Id != folderId) throw new InvalidDataException("Manifest ID does not match its recorded folder.");
        return (manifest.Version, manifest.Enabled);
    }
    static Manifest ParseJson(string folderId, string json)
    {
        using var doc = JsonDocument.Parse(json, JsonOptions);
        if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("mod.json must contain an object.");
        JsonElement Field(string name) => doc.RootElement.EnumerateObject().FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
        string Text(string name, string fallback) => Field(name).ValueKind == JsonValueKind.Undefined ? fallback
            : Field(name).ValueKind == JsonValueKind.String ? Field(name).GetString()! : throw new InvalidDataException($"{name} must be text.");
        string id = Text("id", folderId);
        if (!ValidId(id)) throw new InvalidDataException("The mod ID cannot be used safely in a command.");
        string kind = Text("kind", "feature").ToLowerInvariant();
        if (kind is not ("feature" or "league" or "script")) throw new InvalidDataException("Unknown mod kind.");
        var deps = Field("depends");
        if (deps.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Array)) throw new InvalidDataException("depends must be an array.");
        string[] dependencies = deps.ValueKind == JsonValueKind.Array ? deps.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString()! : throw new InvalidDataException("Dependency IDs must be text.")).ToArray() : [];
        if (dependencies.Any(d => !ValidId(d)) || dependencies.Contains(id, StringComparer.Ordinal)) throw new InvalidDataException("Invalid dependency IDs.");
        var enabled = Field("enabled");
        if (enabled.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.True or JsonValueKind.False)) throw new InvalidDataException("enabled must be true or false.");
        string assembly = Text("assembly", id + ".dll");
        if (Path.GetFileName(assembly) != assembly || assembly.IndexOfAny(['/', '\\', ':']) >= 0 || !assembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Assembly must be a DLL in the mod folder.");
        // name, description, author and compatibleClientVersions are Waypoint's (LocalExile-Mods' AGENTS.md); the Gateway
        // ignores them.
        return new(id, Text("version", "0"), kind, dependencies, assembly, enabled.ValueKind != JsonValueKind.False,
            Text("name", ""), Text("description", ""), Text("author", ""), ClientCompatibility.Read(Field(ClientCompatibility.Field)));
    }
    public static ModEntry[] Read(RuntimeSnapshot snapshot)
    {
        var environment = snapshot.ModEnvironment;
        if (environment is not { Supported: true }) return [];
        var entries = new Dictionary<string, ModEntry>(StringComparer.Ordinal);
        var source = new Dictionary<string, string>(StringComparer.Ordinal);
        string sourceRoot = Path.Combine(snapshot.Root, "Mods", "Source");
        if (Directory.Exists(sourceRoot) && Physical(sourceRoot))
        {
            foreach (string folder in Directory.EnumerateDirectories(sourceRoot).Take(512))
            {
                if (!Physical(folder)) continue;
                foreach (string project in Directory.EnumerateFiles(folder, "*.csproj", SearchOption.TopDirectoryOnly).Take(8))
                {
                    string id = Path.GetFileNameWithoutExtension(project).ToLowerInvariant();
                    string manifestPath = Path.Combine(folder, "mod.json");
                    try
                    {
                        var manifest = Parse(id, manifestPath);
                        if (!source.TryAdd(manifest.Id, Path.GetRelativePath(snapshot.Root, project))) throw new InvalidDataException("Multiple source projects use this ID.");
                        entries[manifest.Id] = new(manifest.Id, manifest.Version, manifest.Kind, manifest.Depends, manifest.Enabled,
                            "Not built", "Build this source mod before enabling it.", Path.Combine(environment.Folder, manifest.Id), source[manifest.Id], "",
                            manifest.Name, manifest.Description, manifest.Author, ClientVersions: manifest.ClientVersions);
                    }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or InvalidOperationException or ArgumentException)
                    { entries[id] = new(id, "?", "?", [], false, "Invalid source", ex.Message, folder, "", ""); }
                }
            }
        }
        var choices = ReadChoices(environment.Folder);
        if (Directory.Exists(environment.Folder) && Physical(environment.Folder))
        {
            foreach (string folder in Directory.EnumerateDirectories(environment.Folder).Take(512))
            {
                string id = Path.GetFileName(folder);
                if (!Physical(folder)) continue;
                // A script mod: loose .cs files the Gateway compiles when it loads the folder, with or without a mod.json.
                string[] scripts = Directory.EnumerateFiles(folder, "*.cs", SearchOption.TopDirectoryOnly).Take(256).Order(StringComparer.Ordinal).ToArray();
                bool hasManifest = File.Exists(Path.Combine(folder, "mod.json"));
                if (!hasManifest && scripts.Length == 0) continue;
                try
                {
                    var manifest = hasManifest ? Parse(id, Path.Combine(folder, "mod.json")) : new Manifest(id, "0", "script", [], id + ".dll", true);
                    if (!ValidId(id) || manifest.Id != id) throw new InvalidDataException("Manifest ID does not match its folder name.");
                    string dll = Path.Combine(folder, manifest.Assembly);
                    bool enabled = choices.GetValueOrDefault(id, manifest.Enabled);
                    bool assembly = File.Exists(dll) && Physical(dll);
                    bool script = !assembly && scripts.Length > 0 && scripts.All(Physical);
                    bool built = assembly || script;
                    string hash = assembly ? Fingerprint(dll) : script ? Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
                        string.Join('\n', scripts.Select(f => Path.GetFileName(f) + "|" + Fingerprint(f)))))) : "";
                    entries[id] = new(id, manifest.Version, manifest.Kind, manifest.Depends, enabled, built ? "Not checked" : "Missing DLL",
                        built ? "Runtime status has not been confirmed." : "Build this mod or install its DLL.", folder, source.GetValueOrDefault(id, ""), hash,
                        manifest.Name, manifest.Description, manifest.Author, script, manifest.ClientVersions);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or InvalidOperationException or ArgumentException)
                { entries[id] = new(id, "?", "?", [], false, "Invalid manifest", ex.Message, folder, source.GetValueOrDefault(id, ""), ""); }
            }
        }
        var runtime = ParseRuntimeReport(environment.RuntimeReport);
        foreach (var original in entries.Values.ToArray())
        {
            var entry = original with { Enabled = choices.GetValueOrDefault(original.Id, original.Enabled) };
            if (entry.Status.StartsWith("Invalid", StringComparison.Ordinal) || entry.Status == "Missing DLL") continue;
            string[] missing = entry.Depends.Where(id => !entries.TryGetValue(id, out var dep) || !dep.Enabled || dep.Status is "Missing DLL" or "Not built" || dep.Status.StartsWith("Invalid", StringComparison.Ordinal)).ToArray();
            string status = !environment.Enabled ? "Mods disabled globally" : !entry.Enabled ? "Disabled" : entry.Status == "Not built" ? "Not built"
                : missing.Length > 0 ? "Dependency blocked" : snapshot.Services.Any(s => s.Name == "Gateway" && s.IsRunning) ? "Not checked" : "Gateway stopped";
            string detail = missing.Length > 0 ? "Needs: " + string.Join(", ", missing) : entry.Detail;
            if (runtime.TryGetValue(entry.Id, out var confirmed))
            {
                status = confirmed.Status;
                string checkedAt = DateTimeOffset.TryParse(environment.CheckedUtc, out var time) ? time.ToLocalTime().ToString("T") : "unknown time";
                detail = confirmed.Detail + " · Checked " + checkedAt;
            }
            entries[entry.Id] = entry with { Status = status, Detail = detail };
        }
        return entries.Values.OrderBy(e => e.Id, StringComparer.Ordinal).ToArray();
    }
    /// <summary>Turns a mod on or off in the server's own choices file (<c>mod-state.txt</c>), replacing the file at once. A
    /// stopped Gateway reads it at its next start; a running one that watches the file (LocalExile since 2026-11-07) loads or
    /// unloads the mod within a second. Its other lines stay as they are.</summary>
    public static void WriteChoice(string folder, string id, bool enabled)
    {
        if (!ValidId(id)) throw new ArgumentException(L.Text("{0} is not a mod id Waypoint can use", id));
        if (!Physical(folder)) throw new IOException("Redirected mod paths are not supported.");
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "mod-state.txt");
        var lines = new List<string>();
        if (File.Exists(path))
            lines.AddRange(Read(path).Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0
                && (l.Split(' ', StringSplitOptions.RemoveEmptyEntries) is not [var other, "on" or "off"] || other != id)));
        lines.Add(id + (enabled ? " on" : " off"));
        string temporary = Path.Combine(folder, "waypoint-choices-" + Guid.NewGuid().ToString("N") + ".tmp");
        File.WriteAllLines(temporary, lines, new System.Text.UTF8Encoding(false));
        File.Move(temporary, path, overwrite: true);
    }

    public static Dictionary<string, bool> ReadChoices(string folder)
    {
        var choices = new Dictionary<string, bool>(StringComparer.Ordinal);
        string path = Path.Combine(folder, "mod-state.txt");
        if (!File.Exists(path)) return choices;
        foreach (string line in Read(path).Split('\n'))
        {
            string[] words = line.TrimEnd('\r').Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words is [var id, "on" or "off"] && ValidId(id)) choices[id] = words[1] == "on";
        }
        return choices;
    }
    public static Dictionary<string, (string Status, string Detail)> ParseRuntimeReport(string report)
    {
        var result = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        foreach (string line in report.Split('\n'))
        {
            var failed = Regex.Match(line.TrimEnd('\r'), @"\A([A-Za-z0-9][A-Za-z0-9_.-]{0,127}) - not loaded, (.+)\z");
            if (failed.Success) { result[failed.Groups[1].Value] = (failed.Groups[2].Value == "disabled" ? "Disabled" : "Load failed", failed.Groups[2].Value); continue; }
            var loaded = Regex.Match(line.TrimEnd('\r'), @"\A([A-Za-z0-9][A-Za-z0-9_.-]{0,127}) (.+) - (feature|league|script), ([0-9]+) hook\(s\)\z");
            if (loaded.Success) result[loaded.Groups[1].Value] = ("Loaded (last checked)", loaded.Groups[4].Value + " hooks · " + loaded.Groups[2].Value);
        }
        return result;
    }
}
