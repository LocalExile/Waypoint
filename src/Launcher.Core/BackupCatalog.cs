using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Launcher.Core;

public sealed record SaveBackup(string Id, string Directory, DateTimeOffset CreatedUtc, string SourceCommit,
    string SourceBranch, string LastBuildCommit, string RequestedSource, long Bytes, string ModSummary = "", bool HasModChoices = false, string ModHistory = "")
{
    public string Summary => $"{CreatedUtc.ToLocalTime():g} · {Bytes / 1024d:N0} KB · {(string.IsNullOrWhiteSpace(SourceBranch) ? "Detached source" : SourceBranch)}";
    public string Identity => "Last launched Gateway: " + (LastBuildCommit.Length > 0 ? LastBuildCommit : "unknown") +
        " · Source at backup: " + (SourceCommit.Length > 0 ? SourceCommit : "unknown") +
        (HasModChoices ? " · Mod history recorded" : " · Mod history not recorded");
    public string Details => Identity + (ModSummary.Length > 0 ? "\nMods at backup: " + ModSummary : "") +
        (ModHistory.Length > 0 ? "\n" + ModHistory : "");
    public override string ToString() => Summary;
}

public static class BackupCatalog
{
    public static string Key(string database) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(database).ToLowerInvariant())))[..16];
    public static string DirectoryFor(string database) => Path.Combine(SettingsStore.DataDirectory, "backups", Key(database));

    public static SaveBackup[] Read(string database)
    {
        string directory = DirectoryFor(database);
        if (!System.IO.Directory.Exists(directory) || Redirected(directory)) return [];
        var result = new List<SaveBackup>();
        foreach (string path in System.IO.Directory.EnumerateDirectories(directory))
        {
            string id = Path.GetFileName(path);
            if (!Regex.IsMatch(id, @"\A\d{8}-\d{6}-[0-9a-f]{8}\z") || Redirected(path)) continue;
            string metadata = Path.Combine(path, "backup.json"), save = Path.Combine(path, "saves.db");
            if (!File.Exists(metadata) || !File.Exists(save) || Redirected(metadata) || Redirected(save) || new FileInfo(metadata).Length > 4 * 1024 * 1024) continue;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(metadata));
                string Value(string key) => doc.RootElement.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : "";
                string Hash(string key) => Regex.IsMatch(Value(key), @"\A[0-9a-fA-F]{40}(?:[0-9a-fA-F]{24})?\z") ? Value(key) : "";
                if (!DateTimeOffset.TryParse(Value("CreatedUtc"), out var created) || !string.Equals(Path.GetFullPath(Value("SaveDatabase")), Path.GetFullPath(database), StringComparison.OrdinalIgnoreCase)) continue;
                string label = Value("SourceLabel");
                bool hasMods = doc.RootElement.TryGetProperty("Mods", out var mods) && mods.ValueKind == JsonValueKind.Object;
                string modSummary = "";
                var modHistory = new List<string>();
                if (hasMods && mods.TryGetProperty("Inventory", out var inventory) && inventory.ValueKind == JsonValueKind.Array)
                {
                    var names = new List<string>();
                    var choices = new Dictionary<string, bool>(StringComparer.Ordinal);
                    if (mods.TryGetProperty("Choices", out var choiceText) && choiceText.ValueKind == JsonValueKind.String)
                        foreach (string line in (choiceText.GetString() ?? "").Split('\n'))
                        {
                            var words = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                            if (words is [var choiceId, "on" or "off"] && ModCatalog.ValidId(choiceId)) choices[choiceId] = words[1] == "on";
                        }
                    foreach (var mod in inventory.EnumerateArray().Take(512))
                    {
                        if (!mod.TryGetProperty("Id", out var modId) || modId.ValueKind != JsonValueKind.String) continue;
                        string version = "?";
                        bool? enabled = null;
                        if (mod.TryGetProperty("Manifest", out var manifest) && manifest.ValueKind == JsonValueKind.String)
                        {
                            try
                            {
                                var defaults = ModCatalog.ManifestDefaults(modId.GetString()!, manifest.GetString()!);
                                version = defaults.Version; enabled = defaults.Enabled;
                            }
                            catch (Exception ex) when (ex is JsonException or InvalidDataException or InvalidOperationException or ArgumentException) { }
                        }
                        names.Add(modId.GetString() + " " + version);
                        string modKey = modId.GetString()!;
                        bool? choice = choices.TryGetValue(modKey, out bool savedChoice) ? savedChoice : enabled;
                        modHistory.Add($"{modKey}: {(choice is true ? "enabled" : choice is false ? "disabled" : "enable choice unknown")} at backup");
                        if (mod.TryGetProperty("Assemblies", out var assemblies) && assemblies.ValueKind == JsonValueKind.Object)
                            foreach (var assembly in assemblies.EnumerateObject())
                                if (assembly.Value.ValueKind == JsonValueKind.String && Regex.IsMatch(assembly.Value.GetString()!, @"\A[0-9a-fA-F]{64}\z"))
                                    modHistory.Add(assembly.Name + " SHA-256: " + assembly.Value.GetString());
                    }
                    modSummary = names.Count == 0 ? "No installed mods" : string.Join(", ", names);
                    if (mods.TryGetProperty("CheckedUtc", out var checkedAt) && checkedAt.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(checkedAt.GetString()))
                    {
                        modHistory.Add("Runtime last checked: " + checkedAt.GetString());
                        if (mods.TryGetProperty("RuntimeReport", out var report) && report.ValueKind == JsonValueKind.String) modHistory.Add(report.GetString()!);
                    }
                }
                result.Add(new(id, path, created, Hash("SourceCommit"), label.Length > 0 ? label : Value("SourceBranch"), Hash("LastBuildCommit"), Value("RequestedSource"), new FileInfo(save).Length, modSummary, hasMods, string.Join("\n", modHistory)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException) { }
        }
        return result.OrderByDescending(b => b.CreatedUtc).ToArray();
    }

    static bool Redirected(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
        return false;
    }
}
