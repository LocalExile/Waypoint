using System.Diagnostics;

namespace GameServer.Launcher;

/// <summary>The one-time setup of Agents/GettingStarted.md, each step's result: what live.ps1 would refuse to start
/// without (✗), and what it starts without but plays worse (!).</summary>
static class SetupChecks
{
    public static List<SetupCheck> Run()
    {
        var checks = new List<SetupCheck>
        {
            OnPath("dotnet.exe") ? new(true, ".NET SDK") : new(false, ".NET SDK not found (dotnet)", "https://dotnet.microsoft.com/download"),
            File("Client/Game.exe", "client copied into Client\\ (step 3)", "robocopy your 3.29.3b client to Client, then copy PathOfExile.exe to Game.exe"),
            File("Client/Content.ggpk", "standalone client layout (Content.ggpk)", "Steam installs have no Content.ggpk"),
            File("data/login-keys.json", "login keys (step 4)", "dotnet run --project src/GameServer.Auth -- keygen Client/Game.exe"),
            File("Client/Game.patched.exe", "patched client (step 5)", "dotnet run --project src/GameServer.Auth -- patch Client/Game.exe Client/Game.patched.exe"),
            System.IO.File.Exists("data/gateway_list.original.txt") ? new(true, "gateway list points at the local login server (step 6)")
                : new(null, "gateway list not patched yet (step 6)", "live.ps1 selects the local gateway itself; step 6 keeps a backup"),
            System.IO.File.Exists("data/extract-stamps.json") ? new(true, "game data extracted (step 7)")
                : new(null, "game data not extracted yet (step 7)", "Play runs extract-data.ps1 first; it takes a few minutes the first time"),
            OnPath("node.exe") && Directory.Exists("Tools/ooz/node_modules/ooz-wasm") ? new(true, "Node.js and ooz (data extraction)")
                : new(false, "Node.js or Tools\\ooz\\node_modules missing (step 7)", "install Node.js, then: cd Tools\\ooz; npm install"),
            ServerHooks(),
            NetworkMode(),
            Frida(),
        };
        return checks;
    }

    /// <summary>The server checkout has what the launcher drives it with (live.ps1 -Stop, the Gateway's commands and
    /// values, the dev host's accounts; they came together).</summary>
    static SetupCheck ServerHooks() =>
        System.IO.File.ReadAllText(Repo.LiveScript).Contains("[switch]$Stop", StringComparison.Ordinal)
            ? new(true, "server checkout supports the launcher")
            : new(false, "server checkout too old for the launcher (no live.ps1 -Stop)", "pull the server's main branch");

    static SetupCheck File(string path, string what, string hint) =>
        System.IO.File.Exists(path) ? new(true, what) : new(false, $"{what}: {path} missing", hint);

    static bool OnPath(string exe) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(dir => { try { return System.IO.File.Exists(Path.Combine(dir.Trim('"'), exe)); } catch (ArgumentException) { return false; } });

    /// <summary>The server supports only Predictive; "auto" picks lockstep on localhost (GettingStarted step 10).</summary>
    static SetupCheck NetworkMode()
    {
        const string hint = "Esc → Options → Game → Network Mode → Predictive, then log out to the character screen and Play again";
        if (!System.IO.File.Exists(Repo.ClientConfig)) return new(null, "network mode not set yet: set Predictive in game", hint);
        string? mode = System.IO.File.ReadLines(Repo.ClientConfig)
            .Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("networking_type=", StringComparison.OrdinalIgnoreCase))?.Split('=', 2)[1];
        return string.Equals(mode, "predictive", StringComparison.OrdinalIgnoreCase)
            ? new(true, "network mode Predictive") : new(false, $"network mode is {mode ?? "auto"}: the character won't move", hint);
    }

    static SetupCheck Frida()
    {
        try
        {
            var info = new ProcessStartInfo(Repo.Python, "-c \"import frida\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            using var p = Process.Start(info)!;
            if (!p.WaitForExit(10000)) { p.Kill(); return new(null, "Python/frida check timed out"); }
            return p.ExitCode == 0 ? new(true, "Python with frida (walk maps, console)")
                : new(null, "frida missing: no walk maps, no console", "python -m venv .venv; .venv\\Scripts\\python.exe -m pip install frida");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return new(null, "Python not found: no walk maps, no console", "install Python 3.9+ from python.org (GettingStarted step 9)");
        }
    }
}
