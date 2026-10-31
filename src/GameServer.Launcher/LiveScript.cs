using System.Diagnostics;

namespace GameServer.Launcher;

enum LiveMode { All, Servers, Client, Restart, Stop, StopServers }

/// <summary>One run of scripts/dev/live.ps1. The launcher never starts a server or the client itself: every button is a
/// live.ps1 switch (Agents/GettingStarted.md, "Play"), so the script's checks and the patching-server argument apply.</summary>
/// <param name="NoHotReload">Starts the built Gateway instead of running it under `dotnet watch` (live.ps1's default).</param>
sealed record LiveRequest(LiveMode Mode, string? Server = null, bool Walkmaps = false, bool NoBuild = false, bool NoExtract = false,
    bool NoHotReload = false)
{
    public static readonly string[] Servers = ["Gateway", "Auth", "Patch"];

    /// <summary>The script's switches. -ClientOnly takes none (the script refuses -Walkmaps with it and skips the rest);
    /// -Stop takes only -Only.</summary>
    public IReadOnlyList<string> Arguments()
    {
        var args = new List<string>();
        switch (Mode)
        {
            case LiveMode.Client: return ["-ClientOnly"];
            case LiveMode.Stop:
                args.Add("-Stop");
                if (Server is not null) args.AddRange(["-Only", CheckedServer]);
                return args;
            case LiveMode.StopServers: return ["-Stop", "-NoClient"];
            case LiveMode.Servers: args.Add("-NoClient"); break;
            case LiveMode.Restart: args.AddRange(["-Only", CheckedServer]); break;
        }
        if (Walkmaps) args.Add("-Walkmaps");
        if (NoBuild) args.Add("-NoBuild");
        if (NoExtract) args.Add("-NoExtract");
        // Only where a Gateway starts: a restart of Auth or Patch leaves the running Gateway as it is.
        if (NoHotReload && (Mode != LiveMode.Restart || Server == "Gateway")) args.Add("-NoHotReload");
        return args;
    }

    string CheckedServer => Servers.Contains(Server) ? Server! : throw new ArgumentException($"not a live.ps1 server: {Server}");

    public string Describe() => $"live.ps1 {string.Join(' ', Arguments())}".TrimEnd();
}

/// <summary>Runs a PowerShell script of the repository and streams its output lines.</summary>
static class Scripts
{
    /// <summary>Runs <paramref name="script"/> with Windows PowerShell (the version live.ps1 is written for) and returns its
    /// exit code. Completion is the process's exit, not the end of its output: a server started by the script could still
    /// hold the pipe (live.ps1 starts them without inherited handles, and MSBuild's reused nodes are turned off here, the
    /// fix GettingStarted's troubleshooting names).</summary>
    public static async Task<int> RunAsync(string script, IReadOnlyList<string> arguments, Action<string> output,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        var info = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Environment.CurrentDirectory,
        };
        foreach (string a in (string[])["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script]) info.ArgumentList.Add(a);
        foreach (string a in arguments) info.ArgumentList.Add(a);
        info.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        info.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
        if (environment is not null)
            foreach (var (key, value) in environment)
            {
                if (value is null) info.Environment.Remove(key);
                else info.Environment[key] = value;
            }

        using var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        var exited = new TaskCompletionSource();
        var outDone = new TaskCompletionSource();
        var errDone = new TaskCompletionSource();
        process.Exited += (_, _) => exited.TrySetResult();
        process.OutputDataReceived += (_, e) => { if (e.Data is null) outDone.TrySetResult(); else output(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is null) errDone.TrySetResult(); else output(e.Data); };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await exited.Task;
        // The last lines, unless a child still holds the pipe.
        await Task.WhenAny(Task.WhenAll(outDone.Task, errDone.Task), Task.Delay(1000));
        return process.ExitCode;
    }

    public static Task<int> RunAsync(LiveRequest request, Action<string> output) =>
        RunAsync(Repo.LiveScript, request.Arguments(), output, request.Mode == LiveMode.Client ? ClientOnlyEnvironment : null);

    /// <summary>What the control bridge sets for -ClientOnly (client_control.py, open): an unreachable proxy, so a client
    /// that tried the official services anyway gets nowhere, and no redirected client configuration.</summary>
    static readonly Dictionary<string, string?> ClientOnlyEnvironment = new()
    {
        ["HTTP_PROXY"] = "http://127.0.0.1:1",
        ["HTTPS_PROXY"] = "http://127.0.0.1:1",
        ["ALL_PROXY"] = "http://127.0.0.1:1",
        ["NO_PROXY"] = "localhost,127.0.0.1,::1",
        ["GGG_CONFIG_FILE_NAME"] = null,
    };
}
