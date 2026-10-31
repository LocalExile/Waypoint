using System.Security.Cryptography;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Launcher.Core;

public sealed record ModEnvironment(bool Supported, string Folder, bool Enabled, string RuntimeReport = "", string CheckedUtc = "");
public sealed record ModRequest(string Id = "", string Project = "");
public sealed record ModEntry(string Id, string Version, string Kind, string[] Depends, bool Enabled, string Status,
    string Detail, string Folder, string Project, string Fingerprint)
{
    public string SearchText => $"{Id} {Version} {Kind} {Status} {Detail}";
    public string Summary => $"{Version} · {Kind} · " + (Status == "Disabled" ? "Disabled" : $"{(Enabled ? "Enabled" : "Disabled")} · {Status}");
}

/// <summary>Reads LocalExile's public mod file contract without loading mod assemblies or running project code.</summary>
public static class ModCatalog
{
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
    sealed record Manifest(string Id, string Version, string Kind, string[] Depends, string Assembly, bool Enabled);
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
        return new(id, Text("version", "0"), kind, dependencies, assembly, enabled.ValueKind != JsonValueKind.False);
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
                            "Not built", "Build this source mod before enabling it.", Path.Combine(environment.Folder, manifest.Id), source[manifest.Id], "");
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
                if (!File.Exists(Path.Combine(folder, "mod.json"))) continue;
                try
                {
                    var manifest = Parse(id, Path.Combine(folder, "mod.json"));
                    if (manifest.Id != id) throw new InvalidDataException("Manifest ID does not match its folder name.");
                    string dll = Path.Combine(folder, manifest.Assembly);
                    bool enabled = choices.GetValueOrDefault(id, manifest.Enabled);
                    bool built = File.Exists(dll) && Physical(dll);
                    string hash = built ? Fingerprint(dll) : "";
                    entries[id] = new(id, manifest.Version, manifest.Kind, manifest.Depends, enabled, built ? "Not checked" : "Missing DLL",
                        built ? "Runtime status has not been confirmed." : "Build this mod or install its DLL.", folder, source.GetValueOrDefault(id, ""), hash);
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
