using System.Text.Json;

namespace Launcher.Core;

public sealed record Requirement(string Name, bool Ready, string Detail);
public sealed record ServiceStatus(string Name, string Status, int ProcessId, string StartedUtc,
    string Detail, int[] Ports, double MemoryMb, double CpuSeconds, string Uptime, int WatchProcessId = 0)
{
    public bool IsRunning => ProcessId != 0 || WatchProcessId != 0;
}
public sealed record RuntimeSnapshot(string Root, string Profile, string SaveDatabase, string SourceCommit,
    string SourceBranch, string BuildCommit, Requirement[] Requirements, ServiceStatus[] Services, string[] Conflicts, string SourceLabel = "", ModEnvironment? ModEnvironment = null, string AccountWriteBlockedReason = "", CaptureSnapshot? Capture = null,
    ContentEnvironment? Content = null)
{
    public bool SetupReady => Requirements.All(r => r.Ready);
    public bool ServersReady => Services.Where(s => s.Name != "Game").All(s => s.Status == "Ready");
    public bool HasServers => Services.Any(s => s.Name != "Game" && s.IsRunning);
    public bool GameRunning => Services.Any(s => s.Name == "Game" && s.ProcessId != 0);
    public bool SafeToLaunch => SetupReady && Conflicts.Length == 0;
}

public sealed record BridgeResult(bool Ok, string Message, RuntimeSnapshot? Snapshot, string? FailedStage = null, AccountInventory? AccountData = null,
    bool Cancelled = false);
public sealed record LauncherSettings(string RepositoryPath = "", bool UseLocalProfile = true,
    bool AutomaticBackups = true, bool HotReload = true, string Language = "en", AppearanceSettings? Appearance = null,
    bool CloseToTray = false, bool CloseBehaviorConfirmed = false, bool StopServicesOnExit = false, bool AlwaysAskOnClose = false,
    bool CaptureAreaLayouts = false);

public sealed record CaptureSnapshot(bool Supported, bool DependenciesReady, bool GatewayEnabled, string State,
    string Detail, int ProcessId = 0, string LastArea = "", string LastCaptureUtc = "", string OutputDirectory = "", string LogPath = "");

// Presentation preferences never reach the emulator adapter or game/save data.
public sealed record AppearanceSettings(string Mode = "System", string Scheme = "blue", string CustomBase = "blue",
    Dictionary<string, string>? LightColors = null, Dictionary<string, string>? DarkColors = null,
    string Vision = "Standard", string Font = "Default", int TextScale = 100, bool StrongFocus = false,
    bool IncreaseContrast = false, bool ReduceMotion = false, bool ProtectContrast = true);

public static class SettingsStore
{
    // This identity is deliberately independent of the product's display name.
    /// <summary>Where Waypoint's data goes when the player has chosen nothing: what every install before the choice used.</summary>
    public static string DefaultDataDirectory => Path.Combine(Platform.LocalData, "EmulatorWorkspaceLauncher");

    static string? _data, _backups;

    /// <summary>Operation logs, the adapter's copy of its scripts, its capture and mod notes, and the backups unless
    /// <see cref="BackupsDirectory"/> is elsewhere: the player's folder (Settings, or <c>WAYPOINT_DATA</c>), else the default.
    /// The adapter is told it as <c>WAYPOINT_DATA</c>.</summary>
    public static string DataDirectory
    {
        get => _data ?? Variable("WAYPOINT_DATA") ?? DefaultDataDirectory;
        set => _data = value;
    }

    /// <summary>The save backups: the player's folder (Settings, or <c>WAYPOINT_BACKUPS</c>), else <c>backups</c> in the data
    /// folder. The adapter is told it as <c>WAYPOINT_BACKUPS</c>.</summary>
    public static string BackupsDirectory
    {
        get => _backups ?? Variable("WAYPOINT_BACKUPS") ?? Path.Combine(DataDirectory, "backups");
        set => _backups = value;
    }

    static string? Variable(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : null;
    public static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    public static LauncherSettings Load(string? path = null)
    {
        path ??= Path.Combine(DefaultDataDirectory, "settings.json");
        if (!File.Exists(path)) return new();
        return JsonSerializer.Deserialize<LauncherSettings>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException("The settings file is empty. Choose a checkout and save settings again.");
    }
    public static void Save(LauncherSettings settings, string? path = null)
    {
        path ??= Path.Combine(DefaultDataDirectory, "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(temporary, path, true);
    }
}
