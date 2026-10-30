using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Launcher.Core;

public sealed record SaveBackup(string Id, string Directory, DateTimeOffset CreatedUtc, string SourceCommit,
    string SourceBranch, string LastBuildCommit, string RequestedSource, long Bytes)
{
    public string Summary => $"{CreatedUtc.ToLocalTime():g} · {Bytes / 1024d:N0} KB · {(string.IsNullOrWhiteSpace(SourceBranch) ? "Detached source" : SourceBranch)}";
    public string Identity => "Last launched Gateway: " + (LastBuildCommit.Length > 0 ? LastBuildCommit : "unknown") +
        " · Source at backup: " + (SourceCommit.Length > 0 ? SourceCommit : "unknown");
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
            if (!File.Exists(metadata) || !File.Exists(save) || Redirected(metadata) || Redirected(save) || new FileInfo(metadata).Length > 32768) continue;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(metadata));
                string Value(string key) => doc.RootElement.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : "";
                string Hash(string key) => Regex.IsMatch(Value(key), @"\A[0-9a-fA-F]{40}(?:[0-9a-fA-F]{24})?\z") ? Value(key) : "";
                if (!DateTimeOffset.TryParse(Value("CreatedUtc"), out var created) || !string.Equals(Path.GetFullPath(Value("SaveDatabase")), Path.GetFullPath(database), StringComparison.OrdinalIgnoreCase)) continue;
                string label = Value("SourceLabel");
                result.Add(new(id, path, created, Hash("SourceCommit"), label.Length > 0 ? label : Value("SourceBranch"), Hash("LastBuildCommit"), Value("RequestedSource"), new FileInfo(save).Length));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { }
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
