using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Launcher.Core;

/// <summary>Runs a setup tool with its output as lines. Stopping ends it and everything it started, so only tools that are
/// safe to end go through here. Completion is the process's exit, not the end of its output: a tool's child may keep the
/// pipe open.</summary>
public static class ToolProcess
{
    /// <param name="input">A line for the tool's standard input, which then closes.</param>
    /// <param name="utf8">The tool writes UTF-8 whatever the console's code page.</param>
    public static async Task<int> RunAsync(string file, IEnumerable<string> arguments, string? directory, Action<string> output, CancellationToken stop, string? input = null, bool utf8 = false)
    {
        var info = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = input is not null };
        if (utf8) info.StandardOutputEncoding = info.StandardErrorEncoding = System.Text.Encoding.UTF8;
        if (directory is not null) info.WorkingDirectory = directory;
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        foreach (string key in info.Environment.Keys.Where(k => k.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray()) info.Environment.Remove(key);
        info.Environment["GIT_TERMINAL_PROMPT"] = "0";
        info.Environment["GCM_INTERACTIVE"] = "never";
        info.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        info.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
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
        if (input is not null)
        {
            // A tool that needs no input may have ended already.
            try
            {
                await process.StandardInput.WriteLineAsync(input);
                process.StandardInput.Close();
            }
            catch (IOException) { }
        }
        using (stop.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception e) when (e is InvalidOperationException or Win32Exception) { }
        }))
            await exited.Task;
        await Task.WhenAny(Task.WhenAll(outDone.Task, errDone.Task), Task.Delay(1000, CancellationToken.None));
        stop.ThrowIfCancellationRequested();
        return process.ExitCode;
    }

    /// <summary>A tool's first line of output, or null when it does not run.</summary>
    public static async Task<(int Code, string Output)?> AskAsync(string file, params string[] arguments)
    {
        var lines = new List<string>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { return (await RunAsync(file, arguments, null, line => { lock (lines) lines.Add(line); }, timeout.Token), string.Join("\n", lines)); }
        catch (Exception e) when (e is Win32Exception or OperationCanceledException or IOException) { return null; }
    }
}

public sealed record Prerequisite(string Name, bool Ready, string Detail);

/// <summary>What must be installed before a checkout can be set up. Each comes from the player's own installer: the guide
/// opens its download page and checks again.</summary>
public static class Prerequisites
{
    /// <summary>Tools installed while Waypoint runs: Windows gives a process its PATH once, so it is read again.</summary>
    public static void RefreshPath()
    {
        if (!OperatingSystem.IsWindows()) return;
        string current = Environment.GetEnvironmentVariable("PATH") ?? "";
        string merged = MergedPath(current, new[] { EnvironmentVariableTarget.Machine, EnvironmentVariableTarget.User }
            .SelectMany(t => (Environment.GetEnvironmentVariable("PATH", t) ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)));
        if (merged != current) Environment.SetEnvironmentVariable("PATH", merged);
    }

    /// <summary>The process's PATH with what the system's has gained since the process started, in front: an installer's entry
    /// then wins over an older one, such as the Microsoft Store's python shortcut.</summary>
    public static string MergedPath(string current, IEnumerable<string> system)
    {
        var known = current.Split(';', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = system.Where(known.Add).ToArray();
        return added.Length == 0 ? current : string.Join(';', added) + ";" + current.TrimStart(';');
    }

    /// <summary>What Check again does before it looks, and the start once: the PATH read again, and a Python installed without
    /// its PATH option adopted (<see cref="PythonLookup"/>), so Waypoint need not restart after an install. Windows only:
    /// elsewhere a package manager's python3 is on PATH from the start.</summary>
    public static async Task RefreshToolsAsync()
    {
        if (OperatingSystem.IsWindows())
        {
            RefreshPath();
            PythonLookup.Adopt(await PythonLookup.FindAsync());
        }
        // The player's chosen programs stay first, whatever PATH gained.
        ToolPaths.Apply();
    }

    public static async Task<Prerequisite[]> CheckAsync()
    {
        RefreshPath();
        ToolPaths.Apply();
        var dotnet = ToolProcess.AskAsync("dotnet", "--list-sdks");
        var git = ToolProcess.AskAsync("git", "--version");
        var python = PythonLookup.FindAsync();
        var node = ToolProcess.AskAsync("node", "--version");
        await Task.WhenAll(dotnet, git, python, node);
        PythonLookup.Adopt(await python);
        ToolPaths.Apply();
        return
        [
            new(".NET 10 SDK", (await dotnet) is { Code: 0 } sdks && HasSdk(sdks.Output), "Install .NET 10 SDK; check with dotnet --list-sdks"),
            new("Git", (await git) is { Code: 0 }, "Git downloads LocalExile and switches its branches"),
            new("Python", (await python).Usable is not null, PythonLookup.Describe(await python)),
            new("Node.js", (await node) is { Code: 0 }, "Node.js is required for bundle decompression"),
        ];
    }

    public static bool HasSdk(string listing) => listing.Split('\n').Any(line => line.TrimStart().StartsWith("10.", StringComparison.Ordinal));
}

public enum SetupState { Pending, Running, Done, Skipped, Stopped, Failed }
public enum SetupOutcome { Finished, Stopped, Failed }

/// <param name="Done">True when a step has nothing left to do, so a run started again goes straight to the next one.</param>
/// <param name="Run">The step; it reports what it is doing, and ends when stopped.</param>
public sealed record SetupTask(string Name, Func<bool>? Done, Func<Action<string>, CancellationToken, Task> Run);

/// <summary>Runs the steps in order. A stop ends the step that runs and leaves the rest; because every step knows when it is
/// already done, or goes on from where it stopped, the same list simply runs again.</summary>
public static class SetupSequence
{
    public static async Task<SetupOutcome> RunAsync(IReadOnlyList<SetupTask> tasks, Action<int, SetupState, string> changed, CancellationToken stop)
    {
        for (int i = 0; i < tasks.Count; i++)
        {
            int index = i;
            var task = tasks[i];
            if (stop.IsCancellationRequested) { changed(i, SetupState.Stopped, ""); return SetupOutcome.Stopped; }
            if (task.Done?.Invoke() == true) { changed(i, SetupState.Skipped, L.Text("Already done")); continue; }
            changed(i, SetupState.Running, "");
            try
            {
                await task.Run(detail => changed(index, SetupState.Running, detail), stop);
                changed(i, SetupState.Done, "");
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                changed(i, SetupState.Stopped, "");
                return SetupOutcome.Stopped;
            }
            catch (Exception e) when (e is IOException or InvalidOperationException or UnauthorizedAccessException or Win32Exception or ArgumentException)
            {
                changed(i, SetupState.Failed, e.Message);
                return SetupOutcome.Failed;
            }
        }
        return SetupOutcome.Finished;
    }

    /// <summary>Downloads LocalExile into an empty folder. Stopped or failed, the unfinished folder is removed, since git cannot
    /// go on from it; a folder that held anything is refused, so nothing of the player's is ever in it.</summary>
    public static async Task CloneAsync(string url, string target, Action<string> output, CancellationToken stop)
    {
        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
            throw new IOException(L.Text("{0} is not empty. Choose an empty folder for the download.", target));
        Directory.CreateDirectory(target);
        try
        {
            int code = await ToolProcess.RunAsync("git", ["-c", "credential.helper=", "clone", "--progress", url, target], null, output, stop);
            if (code != 0) throw new IOException(L.Text("The download failed (git exit {0}). Check your connection and try again.", code));
        }
        catch (Exception e) when (e is OperationCanceledException or IOException)
        {
            await RemovePartialClone(target);
            throw;
        }
    }

    static async Task RemovePartialClone(string target)
    {
        // git keeps its pack files read-only, and a stopped git may hold them for a moment.
        for (int attempt = 0; attempt < 10 && Directory.Exists(target); attempt++)
        {
            try
            {
                foreach (string file in Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(target, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { await Task.Delay(200, CancellationToken.None); }
        }
    }

    /// <summary>Whether the checkout's extract-data.ps1 takes -Servers: from LocalExile's database split on, the servers read
    /// the client tables from client.db and live.ps1 extracts only what they still read.</summary>
    public static bool ExtractsForServers(string checkout)
    {
        try { return Regex.IsMatch(File.ReadAllText(Path.Combine(checkout, "scripts", "dev", "extract-data.ps1")), @"\[switch\]\$Servers\b", RegexOptions.IgnoreCase); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Generates the game data, what the servers read when the script can tell (<see cref="ExtractsForServers"/>). Each
    /// entry is stamped only once it is complete, so an entry cut short runs again.</summary>
    public static async Task ExtractAsync(string checkout, Action<string> output, CancellationToken stop)
    {
        string powershell = Platform.PowerShell;
        string last = "";
        bool hncFailure = false;
        string[] arguments = ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Path.Combine("scripts", "dev", "extract-data.ps1"),
            .. (ExtractsForServers(checkout) ? new[] { "-Servers" } : [])];
        int code = await ToolProcess.RunAsync(powershell, arguments,
            checkout, line =>
            {
                if (line.Trim().Length > 0) last = line;
                hncFailure |= ClientExtractionFailure.IsHncFailure(line);
                output(line);
            }, stop);
        if (code != 0)
        {
            string failure = last.Length > 0 ? last : L.Text("The game data could not be extracted (exit {0}).", code);
            throw new IOException(hncFailure ? ClientExtractionFailure.Describe(checkout, failure) : failure);
        }
    }
}
