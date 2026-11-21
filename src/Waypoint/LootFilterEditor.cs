using Launcher.Core.LootFilter;

namespace Waypoint;

/// <summary>The loot filter editor's server (Launcher.Core's FilterEditorServer): one per Waypoint, started when the player
/// first opens it from Tools, stopped with Waypoint.</summary>
static class LootFilterEditor
{
    static FilterEditorServer? _server;
    static string? _checkout;

    /// <summary>Where the game reads .filter files. On Windows every Path of Exile shares Documents\My Games\Path of Exile, a
    /// separate settings file (ClientSettings) included; on Linux it is the Wine prefix's.</summary>
#if WINDOWS
    static string GameFolder => ClientSettings.Documents;
#else
    static string GameFolder => Path.Combine(Platform.GamePrefix, "drive_c", "users", "steamuser", "Documents", "My Games", "Path of Exile");
#endif

    /// <summary>The editor's address, its server started first if it was not. <paramref name="checkout"/> is the LocalExile
    /// checkout Waypoint runs now: the editor plays its client's alert sounds and takes in its mods' loot-filter.json.</summary>
    internal static string Url(string? checkout)
    {
        _checkout = checkout;
        if (_server is null)
        {
            var server = new FilterEditorServer(GameFolder, FilterSources.CacheFolder, Updates.Client(), () => _checkout);
            try { server.Start(); }
            catch { server.Dispose(); throw; }
            _server = server;
            AppDomain.CurrentDomain.ProcessExit += (_, _) => server.Dispose();
        }
        return _server.Url;
    }
}
