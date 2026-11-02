using System.Diagnostics;
using System.Management;
using System.Windows;

namespace Waypoint;

/// <summary>A program listening on a port LocalExile needs.</summary>
sealed record PortHolder(int Pid, int ParentPid, string Name, string? Path, string CommandLine, DateTime Started, int[] Ports)
{
    static readonly string WindowsFolder = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    /// <summary>Windows' own processes are never Waypoint's to end: PID 4 answers for everything behind http.sys.</summary>
#if WINDOWS
    public bool CanEnd => Pid > 4 && (Path is null || !Path.StartsWith(WindowsFolder, StringComparison.OrdinalIgnoreCase));
#else
    public bool CanEnd => Pid > 1;
#endif

    public string Description
    {
        get
        {
            string ports = string.Join(", ", Ports);
            string identity = L.Text("Port {0}: {1} (PID {2}), started {3:g}", ports, Name, Pid, Started);
            return identity + "\n" + (CommandLine.Length > 0 ? CommandLine : Path ?? "");
        }
    }

    /// <summary>The folder that says whose it is: a server's own, rather than the folder of dotnet.exe that hosts it. Null when
    /// nothing tells: a host started with a relative path.</summary>
    public string? Folder
    {
        get
        {
            if (Name.Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
            {
                string? library = CommandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(a => a.Trim('"')).FirstOrDefault(a => a.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));
                return library is not null && System.IO.Path.IsPathFullyQualified(library) ? System.IO.Path.GetDirectoryName(library) : null;
            }
            return Path is null ? null : System.IO.Path.GetDirectoryName(Path);
        }
    }
}

/// <summary>What the player chose about a port in use.</summary>
enum PortAnswer { Free, Left }

/// <summary>
/// Before something needs the servers' ports, finds what holds them and asks: nothing is ended without the player saying so.
/// Ending gives a console server its Ctrl+C first, which is how a Gateway saves, and only then ends the process.
/// </summary>
static class PortCheck
{
    const int AuthControlPort = 20495;

    /// <summary>The ports the three servers listen on (the adapter's list).</summary>
    public static int[] ServerPorts => [.. Service.All.Where(s => s.Kind == ServiceKind.Server).SelectMany(s => s.Ports), AuthControlPort];

    /// <summary>The programs listening on <paramref name="ports"/>, but for <paramref name="own"/>, the services this checkout
    /// runs already.</summary>
    public static List<PortHolder> Find(IEnumerable<int> ports, IEnumerable<int> own)
    {
        var wanted = ports.ToHashSet();
        var mine = own.ToHashSet();
        var byProcess = new Dictionary<int, SortedSet<int>>();
        var scope = new ManagementScope(@"\\.\ROOT\StandardCimv2");
        using (var listeners = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT LocalPort, OwningProcess FROM MSFT_NetTCPConnection WHERE State = 2")))
        using (var results = listeners.Get())
            foreach (ManagementBaseObject listener in results)
                using (listener)
                {
                    int port = Convert.ToInt32(listener["LocalPort"]), pid = Convert.ToInt32(listener["OwningProcess"]);
                    if (!wanted.Contains(port) || mine.Contains(pid)) continue;
                    if (!byProcess.TryGetValue(pid, out var held)) byProcess[pid] = held = [];
                    held.Add(port);
                }
        return byProcess.Select(p => Describe(p.Key, p.Value.ToArray())).ToList();
    }

    static PortHolder Describe(int pid, int[] ports)
    {
        using var searcher = new ManagementObjectSearcher($"SELECT ParentProcessId, Name, ExecutablePath, CommandLine, CreationDate FROM Win32_Process WHERE ProcessId = {pid}");
        using var results = searcher.Get();
        foreach (ManagementBaseObject process in results)
            using (process)
                return new(pid, Convert.ToInt32(process["ParentProcessId"]), process["Name"] as string ?? "?", process["ExecutablePath"] as string,
                    process["CommandLine"] as string ?? "",
                    process["CreationDate"] is string created ? ManagementDateTimeConverter.ToDateTime(created) : default, ports);
        return new(pid, 0, "?", null, "", default, ports);
    }

    /// <summary>Asks about what holds <paramref name="ports"/>. <see cref="PortAnswer.Free"/> when nothing does any more, and
    /// <see cref="PortAnswer.Left"/> when the player kept it running: whatever needed the ports does not go on.</summary>
    public static async Task<PortAnswer> ClearAsync(Window? owner, IEnumerable<int> ports, IEnumerable<int> own, Action<string> output)
    {
        int[] needed = ports.ToArray();
        int[] mine = own.ToArray();
        List<PortHolder> holders;
        try { holders = await Task.Run(() => Find(needed, mine)); }
        // Without WMI the check is only lost: the adapter and live.ps1 still say what stopped them.
        catch (Exception e) when (e is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            output($"ports not checked: {e.Message}");
            return PortAnswer.Free;
        }
        if (holders.Count == 0) return PortAnswer.Free;

        string details = string.Join("\n\n", holders.Select(h => h.Description));
        bool canEnd = holders.All(h => h.CanEnd);
        string title = L.Text("A server port is in use");
        string message = canEnd
            ? L.Text("LocalExile needs these ports, and another program is listening on them. It may be a server from another LocalExile folder, or something else entirely. Waypoint can end it, or you can look at it first. A server gets the chance to save before it is ended; another program may lose unsaved work.")
            : L.Text("LocalExile needs these ports, and Windows itself is using one of them, so Waypoint will not end it. Look at what uses it, free the port, and try again.");
        string[] buttons = canEnd ? [L.Text("Check it first"), L.Text("End it and continue"), L.Text("Cancel")] : [L.Text("Check it first"), L.Text("Cancel")];
        int chosen = Dialog.Show(owner, DialogKind.Warning, title, message, details, buttons);
        if (chosen == 0)
        {
            Inspect(holders[0]);
            return PortAnswer.Left;
        }
        if (!canEnd || chosen != 1) return PortAnswer.Left;
        foreach (var holder in holders) await EndAsync(holder, output);
        // Windows frees a port a moment after its owner has gone.
        for (int attempt = 0; attempt < 25; attempt++)
        {
            if ((await Task.Run(() => Find(needed, mine))).Count == 0) return PortAnswer.Free;
            await Task.Delay(200);
        }
        Dialog.Tell(owner, DialogKind.Error, title, L.Text("The port is still in use, so Waypoint did not go on."), details);
        return PortAnswer.Left;
    }

    /// <summary>Shows where the holder lives, or Task Manager when nothing tells.</summary>
    static void Inspect(PortHolder holder)
    {
        string? folder = holder.Folder;
#if WINDOWS
        if (folder is not null && Directory.Exists(folder))
            Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { folder }, UseShellExecute = false })?.Dispose();
        else Process.Start(new ProcessStartInfo("taskmgr.exe") { UseShellExecute = true })?.Dispose();
#else
        if (folder is not null && Directory.Exists(folder)) Process.Start(new ProcessStartInfo("xdg-open", [folder]) { UseShellExecute = false })?.Dispose();
#endif
    }

    /// <summary>Ctrl+C for a server in its console (the adapter's helper checks the process is the one it was asked about, and
    /// fails closed on a shared console), up to 20 seconds to save and go, then the end of it. A server run under `dotnet
    /// watch` goes with its watcher, which would otherwise start it again.</summary>
    internal static async Task EndAsync(PortHolder holder, Action<string> output)
    {
        output($"ending {holder.Name} (pid {holder.Pid})");
        using var process = TryOpen(holder.Pid);
        if (process is null) return;
#if !WINDOWS
        // `dotnet watch` is a few dotnet processes above a server here, and would otherwise wait to start it again. Found first:
        // the processes between them end with the server. Only that chain counts: a shell above it can have any words on its
        // command line.
        int top = 0;
        for (int id = holder.ParentPid, hop = 0; id > 1 && hop < 4; hop++)
        {
            var above = Describe(id, []);
            if (above.Name != "dotnet.exe") break;
            if (above.CommandLine.Contains("dotnet-watch", StringComparison.Ordinal) || above.CommandLine.Contains(" watch ", StringComparison.Ordinal)) top = id;
            id = above.ParentPid;
        }
#endif
        try
        {
#if WINDOWS
            if (holder.Name.Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
#else
            // A watched Gateway is its own program here.
            if (holder.Name.Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase) || holder.Name.StartsWith("GameServer.", StringComparison.Ordinal))
#endif
            {
#if WINDOWS
                string helper = Path.Combine(Path.GetDirectoryName(BridgeScripts.Extract())!, "ConsoleSignal.ps1");
                string started = holder.Started.ToUniversalTime().ToString("o");
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                int signal = await ToolProcess.RunAsync("powershell.exe", ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", helper,
                    "-TargetId", holder.Pid.ToString(), "-ParentId", holder.ParentPid.ToString(), "-StartedUtc", started], null, output, timeout.Token);
#else
                // Ctrl+C is SIGINT here, and needs no console.
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                int signal = await ToolProcess.RunAsync("kill", ["-INT", holder.Pid.ToString()], null, output, timeout.Token);
#endif
                if (signal == 0)
                {
                    using var saving = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                    try { await process.WaitForExitAsync(saving.Token); }
                    catch (OperationCanceledException) { }
                }
            }
        }
        catch (Exception e) when (e is OperationCanceledException or System.ComponentModel.Win32Exception or IOException)
        {
            output($"no console stop for pid {holder.Pid}: {e.Message}");
        }
#if !WINDOWS
        if (top != 0) TryOpen(top)?.Kill(entireProcessTree: true);
#endif
        if (process.HasExited) return;
        using var watcher = holder.ParentPid > 0 && Describe(holder.ParentPid, []) is { Name: "dotnet.exe" } parent && parent.CommandLine.Contains(" watch ", StringComparison.Ordinal) ? TryOpen(holder.ParentPid) : null;
        var target = watcher ?? process;
        try
        {
            target.Kill(entireProcessTree: true);
            await target.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or OperationCanceledException)
        {
            output($"could not end pid {target.Id}: {e.Message}");
        }
    }

    static Process? TryOpen(int pid)
    {
        try { return Process.GetProcessById(pid); }
        catch (ArgumentException) { return null; }
    }
}
