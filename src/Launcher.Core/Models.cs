using System.Text.Json;

namespace Launcher.Core;

public sealed record Requirement(string Name, bool Ready, string Detail);
public sealed record ServiceStatus(string Name, string Status, int ProcessId, string StartedUtc,
    string Detail, int[] Ports, double MemoryMb, double CpuSeconds, string Uptime, int WatchProcessId = 0)
{
    public bool IsRunning => ProcessId != 0 || WatchProcessId != 0;
}
public sealed record RuntimeSnapshot(string Root, string Profile, string SaveDatabase, string SourceCommit,
    string SourceBranch, string BuildCommit, Requirement[] Requirements, ServiceStatus[] Services, string[] Conflicts, string SourceLabel = "", ModEnvironment? ModEnvironment = null)
{
    public bool SetupReady => Requirements.All(r => r.Ready);
    public bool ServersReady => Services.Where(s => s.Name != "Game").All(s => s.Status == "Ready");
    public bool HasServers => Services.Any(s => s.Name != "Game" && s.IsRunning);
    public bool GameRunning => Services.Any(s => s.Name == "Game" && s.ProcessId != 0);
    public bool SafeToLaunch => SetupReady && Conflicts.Length == 0;
}

public sealed record BridgeResult(bool Ok, string Message, RuntimeSnapshot? Snapshot, string? FailedStage = null);
public sealed record LauncherSettings(string RepositoryPath = "", bool UseLocalProfile = true,
    bool AutomaticBackups = true, bool HotReload = true);

public static class SettingsStore
{
    // This identity is deliberately independent of the product's display name.
    public static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EmulatorWorkspaceLauncher");
    public static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    public static LauncherSettings Load(string? path = null)
    {
        path ??= Path.Combine(DataDirectory, "settings.json");
        if (!File.Exists(path)) return new();
        return JsonSerializer.Deserialize<LauncherSettings>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException("The settings file is empty. Choose a checkout and save settings again.");
    }
    public static void Save(LauncherSettings settings, string? path = null)
    {
        path ??= Path.Combine(DataDirectory, "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(temporary, path, true);
    }
}
