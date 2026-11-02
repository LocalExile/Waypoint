using System.Text.Json;
using System.Text.Json.Serialization;

namespace Waypoint;

/// <summary>Waypoint's own choices, per Windows user (%LOCALAPPDATA%\Waypoint\settings.json): never in the repository, and
/// never game data or credentials. The first start takes over what LocalExile Launcher and earlier Waypoint builds kept.</summary>
sealed class Settings
{
    /// <summary>The LocalExile server checkout Waypoint drives (chosen on the first start, <see cref="Repo"/>).</summary>
    public string? ServerPath { get; set; }
    /// <summary>Every section, or Simple mode's few. A new player starts in Simple mode; someone who used an earlier launcher,
    /// or this one before the modes, stays with every section (<see cref="Load"/>).</summary>
    public bool? AdvancedMode { get; set; }
    /// <summary>Settings' Developer Mode, which unlocks the Simple | Advanced switch. Someone already in Advanced mode has it on
    /// (<see cref="Load"/>).</summary>
    public bool? DeveloperMode { get; set; }
    [JsonIgnore]
    public bool Developer => DeveloperMode == true;
    [JsonIgnore]
    public bool Advanced => Developer && AdvancedMode == true;
    /// <summary>Simple mode's Play updates a plain copy of LocalExile (<see cref="CheckoutUpdates"/>); on a release install, a
    /// newer release shows the update card.</summary>
    public bool KeepUpToDate { get; set; } = true;
    /// <summary>When Play last looked for an update of LocalExile.</summary>
    public DateTimeOffset? LastUpdateCheck { get; set; }
    /// <summary>Going back to an earlier version pauses Play's updates until then.</summary>
    public DateTimeOffset? UpdatesPausedUntil { get; set; }
    /// <summary>The update Play made last, until the player went back from it.</summary>
    public CheckoutUpdate? LastUpdate { get; set; }
    /// <summary>The guide's "Remove the old one later", until the player has decided on Play.</summary>
    public ReleaseToRemove? RemoveRelease { get; set; }
    /// <summary>The guide ended in another folder and an account comes next: the start on that folder opens it.</summary>
    public bool CreateAccountOnStart { get; set; }
    public bool Walkmaps { get; set; }
    public bool NoBuild { get; set; }
    public bool NoExtract { get; set; }
    /// <summary>The Gateway under `dotnet watch` (live.ps1's default); off passes -NoHotReload.</summary>
    public bool HotReload { get; set; } = true;
    /// <summary>Look for a newer release at startup (a release build only).</summary>
    public bool CheckForUpdates { get; set; } = true;
    /// <summary>Hide in the tray while the game client runs, and come back when it closes.</summary>
    public bool TrayWhileGameRuns { get; set; } = true;
    /// <summary>Theme, scheme, font, text size and the accessibility options; null until changed.</summary>
    public AppearanceSettings? Appearance { get; set; }
    /// <summary>The interface's language: "en" or "ru".</summary>
    public string Language { get; set; } = "en";
    /// <summary>Back the saves up before a server start, a checkout switch or an account change.</summary>
    public bool AutomaticBackups { get; set; } = true;
    /// <summary>Use the checkout's scripts/dev/live.local.ps1 where it has one (its config/runtime.local.ps1 included).</summary>
    public bool UseLocalProfile { get; set; } = true;
    /// <summary>Closing the window hides it in the tray instead of exiting.</summary>
    public bool CloseToTray { get; set; }
    /// <summary>The player has answered what closing the window should do.</summary>
    public bool CloseBehaviorConfirmed { get; set; }
    /// <summary>Exiting stops the servers and the game first.</summary>
    public bool StopServicesOnExit { get; set; }
    /// <summary>Ask each time the window closes.</summary>
    public bool AlwaysAskOnClose { get; set; }
    /// <summary>The client the setup guide copies from, kept so that a stopped setup goes on from the same one.</summary>
    public string? SetupClient { get; set; }
    /// <summary>Pull requests from forks the player agreed to run (asked once each).</summary>
    public List<int> TrustedPullRequests { get; set; } = [];
    /// <summary>Pinned console lines, shown as buttons.</summary>
    public List<string> Favorites { get; set; } = ["/commands", "/pos", "/heal", "/god"];
    /// <summary>The console's last lines, newest last.</summary>
    public List<string> History { get; set; } = [];

    public const int HistoryMax = 200;

#if WINDOWS
    static string LocalData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
#else
    static string LocalData => Platform.LocalData;
#endif
    static string File => Path.Combine(LocalData, "Waypoint", "settings.json");
    static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    /// <summary>What the adapter needs to know; the checkout is the working directory.</summary>
    public LauncherSettings ForBridge => new(Environment.CurrentDirectory, UseLocalProfile, AutomaticBackups, HotReload, Language,
        Appearance, CloseToTray, CloseBehaviorConfirmed, StopServicesOnExit, AlwaysAskOnClose);

    public static Settings Load()
    {
        try
        {
            if (System.IO.File.Exists(File))
            {
                var saved = JsonSerializer.Deserialize<Settings>(System.IO.File.ReadAllText(File), Options) ?? new();
                saved.AdvancedMode ??= true;
                saved.DeveloperMode ??= saved.AdvancedMode == true;
                return saved;
            }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return new() { AdvancedMode = true, DeveloperMode = true }; }
        return Imported();
    }

    /// <summary>The settings of the two launchers this one replaces: LocalExile Launcher's, then Waypoint's on top of them.
    /// Either can be missing or unreadable; neither file is changed.</summary>
    static Settings Imported()
    {
        var settings = new Settings();
        try
        {
            string launcher = Path.Combine(LocalData, "LocalExile", "launcher.json");
            if (System.IO.File.Exists(launcher))
            {
                string text = System.IO.File.ReadAllText(launcher);
                settings = JsonSerializer.Deserialize<Settings>(text, Options) ?? settings;
                settings.AdvancedMode = true;
                using var document = JsonDocument.Parse(text);
                if (document.RootElement.TryGetProperty("Theme", out var theme) && theme.ValueKind == JsonValueKind.String)
                    settings.Appearance = new AppearanceSettings(Mode: theme.GetString() ?? "System");
            }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        try
        {
            string path = Path.Combine(SettingsStore.DataDirectory, "settings.json");
            if (System.IO.File.Exists(path))
            {
                var waypoint = SettingsStore.Load(path);
                settings.AdvancedMode = true;
                if (string.IsNullOrEmpty(settings.ServerPath) && waypoint.RepositoryPath.Length > 0) settings.ServerPath = waypoint.RepositoryPath;
                settings.UseLocalProfile = waypoint.UseLocalProfile;
                settings.AutomaticBackups = waypoint.AutomaticBackups;
                settings.HotReload = waypoint.HotReload;
                settings.Language = waypoint.Language;
                settings.Appearance = waypoint.Appearance ?? settings.Appearance;
                settings.CloseToTray = waypoint.CloseToTray;
                settings.CloseBehaviorConfirmed = waypoint.CloseBehaviorConfirmed;
                settings.StopServicesOnExit = waypoint.StopServicesOnExit;
                settings.AlwaysAskOnClose = waypoint.AlwaysAskOnClose;
            }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException or InvalidDataException) { }
        settings.AdvancedMode ??= false;
        settings.DeveloperMode ??= settings.AdvancedMode == true;
        return settings;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(File)!);
            System.IO.File.WriteAllText(File, JsonSerializer.Serialize(this, Options));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    public void Remember(string line)
    {
        History.Remove(line);
        History.Add(line);
        if (History.Count > HistoryMax) History.RemoveRange(0, History.Count - HistoryMax);
    }
}
