using System.Text.Json;

namespace GameServer.Launcher;

/// <summary>The launcher's own choices, per Windows user (%LOCALAPPDATA%\LocalExile\launcher.json): never in the
/// repository, and never game data or credentials.</summary>
sealed class Settings
{
    /// <summary>The LocalExile server checkout the launcher drives (chosen on the first start, <see cref="Repo"/>).</summary>
    public string? ServerPath { get; set; }
    public bool Walkmaps { get; set; }
    public bool NoBuild { get; set; }
    public bool NoExtract { get; set; }
    /// <summary>The Gateway under `dotnet watch` (live.ps1's default); off passes -NoHotReload.</summary>
    public bool HotReload { get; set; } = true;
    /// <summary>Look for a newer release at startup (a release build only).</summary>
    public bool CheckForUpdates { get; set; } = true;
    /// <summary>Hide in the tray while the game client runs, and come back when it closes.</summary>
    public bool TrayWhileGameRuns { get; set; } = true;
    /// <summary>Light, dark, or the Windows app theme.</summary>
    [System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<ThemeChoice>))]
    public ThemeChoice Theme { get; set; } = ThemeChoice.System;
    /// <summary>Pinned console lines, shown as buttons.</summary>
    public List<string> Favorites { get; set; } = ["/commands", "/pos", "/heal", "/god"];
    /// <summary>The console's last lines, newest last.</summary>
    public List<string> History { get; set; } = [];

    public const int HistoryMax = 200;

    static string File => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocalExile", "launcher.json");
    static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static Settings Load()
    {
        try { return System.IO.File.Exists(File) ? JsonSerializer.Deserialize<Settings>(System.IO.File.ReadAllText(File)) ?? new() : new(); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return new(); }
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
