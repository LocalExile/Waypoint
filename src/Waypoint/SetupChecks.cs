using System.Diagnostics;

namespace Waypoint;

/// <summary>The one-time setup of Agents/GettingStarted.md, each step's result: what live.ps1 would refuse to start
/// without (✗), and what it starts without but plays worse (!).</summary>
static class SetupChecks
{
    public static List<SetupCheck> Run()
    {
        var checks = new List<SetupCheck>
        {
#if WINDOWS
            OnPath("dotnet.exe") ? new(true, ".NET SDK") : new(false, L.Text(".NET SDK not found (dotnet)"), "https://dotnet.microsoft.com/download"),
#else
            OnPath("dotnet") ? new(true, ".NET SDK") : new(false, L.Text(".NET SDK not found (dotnet)"), "https://dotnet.microsoft.com/download"),
#endif
            File("Client/Game.exe", L.Text("client copied into Client\\ (step 3)"), L.Text("robocopy your 3.29.3c client to Client, then copy PathOfExile.exe to Game.exe")),
            File("Client/Content.ggpk", L.Text("standalone client layout (Content.ggpk)"), L.Text("Steam installs have no Content.ggpk")),
            File("data/login-keys.json", L.Text("login keys (step 4)"), "dotnet run --project src/GameServer.Auth -- keygen Client/Game.exe"),
            File("Client/Game.patched.exe", L.Text("patched client (step 5)"), "dotnet run --project src/GameServer.Auth -- patch Client/Game.exe Client/Game.patched.exe"),
            System.IO.File.Exists("data/gateway_list.original.txt") ? new(true, L.Text("gateway list points at the local login server (step 6)"))
                : new(null, L.Text("gateway list not patched yet (step 6)"), L.Text("live.ps1 selects the local gateway itself; step 6 keeps a backup")),
            System.IO.File.Exists("data/extract-stamps.json") ? new(true, L.Text("game data extracted (step 7)"))
                : new(null, L.Text("game data not extracted yet (step 7)"), L.Text("Play runs extract-data.ps1 first; it takes a few minutes the first time")),
#if WINDOWS
            OnPath("node.exe") && Directory.Exists("Tools/ooz/node_modules/ooz-wasm") ? new(true, L.Text("Node.js and ooz (data extraction)"))
#else
            OnPath("node") && Directory.Exists("Tools/ooz/node_modules/ooz-wasm") ? new(true, L.Text("Node.js and ooz (data extraction)"))
#endif
                : new(false, L.Text("Node.js or Tools\\ooz\\node_modules missing (step 7)"), L.Text("install Node.js, then: cd Tools\\ooz; npm install")),
            ServerHooks(),
            NetworkMode(),
            Frida(),
        };
#if WINDOWS
        // A Linux client has a Wine prefix of its own, so nothing is shared there.
        if (Directory.Exists("Client")) checks.Insert(checks.Count - 2, GameSettings());
#endif
        return checks;
    }

#if WINDOWS
    /// <summary>Whether LocalExile can change what the player's normal game uses (ClientSettings).</summary>
    static SetupCheck GameSettings()
    {
        var settings = ClientSettings.Read("Client");
        string hint = L.Text("Guided setup → Use separate game settings…");
        return settings.Kind switch
        {
            ClientSettingsKind.Separate => new(true, L.Text("game settings are separate ({0})", Path.GetRelativePath(ClientSettings.Documents, settings.ConfigFile))),
            ClientSettingsKind.Shared => new(null, L.Text("game settings are shared with your normal game"), hint),
            _ => new(null, L.Text("game settings: Waypoint can't tell what else uses them"), hint),
        };
    }
#endif

    /// <summary>The server checkout has what the launcher drives it with (live.ps1 -Stop, the Gateway's commands and
    /// values, the dev host's accounts; they came together).</summary>
    static SetupCheck ServerHooks() =>
        System.IO.File.ReadAllText(Repo.LiveScript).Contains("[switch]$Stop", StringComparison.Ordinal)
            ? new(true, L.Text("server checkout supports the launcher"))
            : new(false, L.Text("server checkout too old for the launcher (no live.ps1 -Stop)"), L.Text("pull the server's main branch"));

    static SetupCheck File(string path, string what, string hint) =>
        System.IO.File.Exists(path) ? new(true, what) : new(false, L.Text("{0}: {1} missing", what, path), hint);

    static bool OnPath(string exe) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(dir => { try { return System.IO.File.Exists(Path.Combine(dir.Trim('"'), exe)); } catch (ArgumentException) { return false; } });

    /// <summary>The game picks its network mode and a current server follows it; Predictive is the preferred one, and "auto"
    /// picks lockstep on localhost. An older server serves only Predictive (GettingStarted step 10).</summary>
    static SetupCheck NetworkMode()
    {
        string hint = L.Text("Esc → Options → Game → Networking Mode → Predictive, then log out to the character screen and Play again");
        if (!System.IO.File.Exists(Repo.ClientConfig)) return new(null, L.Text("network mode not set yet: set Predictive in game"), hint);
        string? mode = ClientSettings.NetworkMode(Repo.ClientConfig);
        if (string.Equals(mode, "predictive", StringComparison.OrdinalIgnoreCase)) return new(true, L.Text("network mode Predictive"));
        return Repo.ServesAnyNetworkMode() ? new(true, L.Text("network mode {0}: served, but Predictive is preferred", mode ?? "auto"), hint)
            : new(false, L.Text("network mode is {0}: the character won't move", mode ?? "auto"), hint);
    }

    static SetupCheck Frida()
    {
#if WINDOWS
        try
        {
            var info = new ProcessStartInfo(Repo.Python, "-c \"import frida; assert callable(frida.attach)\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            using var p = Process.Start(info)!;
            if (!p.WaitForExit(10000)) { p.Kill(); return new(null, L.Text("Frida is optional; its availability could not be checked.")); }
            return p.ExitCode == 0 ? new(true, L.Text("Frida installed (optional game tools)"))
                : new(null, L.Text("Frida is optional and not installed; normal play is available."),
                    L.Text("Waypoint offers to install it when you use direct console commands, Give item, live mod controls or walk maps."));
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return new(null, L.Text("Frida is optional; Python is needed for its game tools."),
                L.Text("Waypoint offers to install Frida when you use a feature that needs it."));
        }
#else
        return new(true, L.Text("Frida is optional; its game-control features are not supported on Linux yet."));
#endif
    }
}
