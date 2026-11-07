using System.Text;
using System.Text.RegularExpressions;

namespace Waypoint;

/// <summary>
/// Linux's counterpart of the Windows app's own game settings (ClientSettings). There every client shares Documents, so a
/// config.json gives LocalExile files of its own. Here a client's Documents are those of the Wine prefix it runs in, and LocalExile
/// on its own uses one per install, lost with its data folder. So Waypoint names one prefix for everything it starts, in the
/// environment that live.ps1, a release and the client inherit: settings and saved login that every install shares, that
/// outlive them, and that no other Path of Exile uses. A prefix the shell names is the player's, used as it is and never written.
/// </summary>
static class GameSettings
{
    internal const string Documents = "drive_c/users/steamuser/Documents/My Games/Path of Exile", Config = "production_Config.ini";

    public static void Prepare(string install)
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WINEPREFIX"))) return;
        string prefix = Platform.GamePrefix;
        try { Initialize(prefix, install); }
        // Without first settings the client still starts, and asks for Predictive the way it always has.
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        Environment.SetEnvironmentVariable("WINEPREFIX", prefix);
    }

    /// <summary>The first settings: this install's own from before, when it has them, and Predictive, the preferred network mode. Once
    /// the file exists nothing here touches it, so what the player sets in game stays.</summary>
    internal static void Initialize(string prefix, string install)
    {
        string folder = Path.Combine(prefix, Documents), settings = Path.Combine(folder, Config);
        if (File.Exists(settings)) return;
        Directory.CreateDirectory(folder);
        string old = Path.Combine(install, "data", "wine-prefix", Documents);
        foreach (string name in new[] { "production_login.credentials", Config })
            if (File.Exists(Path.Combine(old, name))) File.Copy(Path.Combine(old, name), Path.Combine(folder, name));
        string text = File.Exists(settings) ? File.ReadAllText(settings) : "";
        text = NetworkMode().IsMatch(text) ? NetworkMode().Replace(text, "networking_type=predictive")
            : text.TrimEnd() + "\r\n[NETWORKING]\r\nnetworking_type=predictive\r\n";
        // The byte-order mark is how the client writes this file.
        File.WriteAllText(settings, text.TrimStart(), new UTF8Encoding(true));
    }

    static Regex NetworkMode() => new(@"(?m)^networking_type=[^\r\n]*");
}
