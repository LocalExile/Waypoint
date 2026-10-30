using System.Diagnostics;
using System.Management;
using System.Net.NetworkInformation;

namespace GameServer.Launcher;

enum ServiceKind { Server, DevHost, Client, Recorder }

/// <summary>A process the launcher watches. The servers and the dev host are dotnet processes found by their DLL on the
/// command line (live.ps1 runs <c>dotnet src\GameServer.X\bin\…\GameServer.X.dll</c>), the client by its executable and the
/// walk-map recorder by its script.</summary>
sealed record Service(string Name, ServiceKind Kind, int[] Ports, string Match)
{
    static int Port(string variable, int fallback) => int.TryParse(Environment.GetEnvironmentVariable(variable), out int p) ? p : fallback;
    static int PatchWebPort => Environment.GetEnvironmentVariable("Game_PATCH_URL") is { } url
        && Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Port : 8080;

    public static Service Patch { get; } = new("Patch", ServiceKind.Server, [Port("Game_PATCH_TCP_PORT", 12995), PatchWebPort], "GameServer.Patch.dll");
    public static Service Auth { get; } = new("Auth", ServiceKind.Server, [Port("Game_LOGIN_PORT", 20493)], "GameServer.Auth.dll");
    public static Service Gateway { get; } = new("Gateway", ServiceKind.Server, [Port("Game_GATEWAY_PORT", 6112)], "GameServer.Gateway.dll");
    public static Service DevHost { get; } = new("Dev host", ServiceKind.DevHost, [Port("Game_DEV_PORT", 7000)], "GameServer.Host.dll");
    public static Service Client { get; } = new("Client", ServiceKind.Client, [], "Game.patched.exe");
    public static Service Recorder { get; } = new("Walk maps", ServiceKind.Recorder, [], "walkmaps.py");

    public static IReadOnlyList<Service> All { get; } = [Patch, Auth, Gateway, DevHost, Client, Recorder];

    /// <summary>A .NET process, whose runtime counters can be read.</summary>
    public bool IsDotnet => Kind is ServiceKind.Server or ServiceKind.DevHost;

    public string PortList => Ports.Length == 0 ? "" : string.Join(", ", Ports);

    public bool Matches(string processName, string? commandLine) => Kind switch
    {
        ServiceKind.Client => processName.Equals(Match, StringComparison.OrdinalIgnoreCase),
        ServiceKind.Recorder => processName.StartsWith("python", StringComparison.OrdinalIgnoreCase)
            && commandLine?.Contains(Match, StringComparison.OrdinalIgnoreCase) == true,
        _ => processName.Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase)
            && commandLine?.Contains(Match, StringComparison.OrdinalIgnoreCase) == true,
    };
}

sealed record ServiceSample(Service Service, int? Pid, bool Listening, double CpuPercent, double MemoryMb, int Threads,
    int Handles, TimeSpan Uptime)
{
    public bool Running => Pid is not null;
}

/// <summary>Samples every <see cref="Service"/> once a second: its process (found again every two seconds), the share of
/// the machine's CPU it used since the last sample, its memory, threads and handles, and whether its ports listen.</summary>
sealed class ServiceMonitor : IDisposable
{
    public event Action<IReadOnlyList<ServiceSample>>? Sampled;
    /// <summary>A process that was running is gone (service, its pid).</summary>
    public event Action<Service, int>? Exited;
    /// <summary>A sample failed (WMI, the port table, or a listener).</summary>
    public event Action<Exception>? Failed;

    readonly Timer _timer;
    readonly Dictionary<Service, int> _pids = [];
    readonly Dictionary<int, (TimeSpan Cpu, long At)> _cpu = [];
    int _ticks, _busy;

    public ServiceMonitor() => _timer = new Timer(_ => Tick(), null, 0, 1000);

    public IReadOnlyList<ServiceSample> Latest { get; private set; } = [];

    void Tick()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            if (_ticks++ % 2 == 0) Rescan();
            var listening = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Select(e => e.Port).ToHashSet();
            var samples = Service.All.Select(s => Sample(s, listening)).ToList();
            Latest = samples;
            Sampled?.Invoke(samples);
        }
        // An exception left on a timer thread would end the launcher: a failed sample is reported and the next one tried.
        catch (Exception e) { Failed?.Invoke(e); }
        finally { Volatile.Write(ref _busy, 0); }
    }

    void Rescan()
    {
        var found = new Dictionary<Service, int>();
        using var searcher = new ManagementObjectSearcher(
            "SELECT ProcessId, Name, CommandLine FROM Win32_Process " +
            "WHERE Name = 'dotnet.exe' OR Name = 'Game.patched.exe' OR Name LIKE 'python%'");
        foreach (ManagementBaseObject p in searcher.Get())
        {
            using (p)
            {
                string name = p["Name"] as string ?? "";
                string? commandLine = p["CommandLine"] as string;
                int pid = Convert.ToInt32(p["ProcessId"]);
                if (Service.All.FirstOrDefault(s => s.Matches(name, commandLine)) is { } service && !found.ContainsKey(service))
                    found[service] = pid;
            }
        }
        lock (_pids)
        {
            foreach (var (service, pid) in _pids.Where(kv => !found.TryGetValue(kv.Key, out int now) || now != kv.Value).ToList())
                Gone(service, pid);
            foreach (var (service, pid) in found) _pids[service] = pid;
        }
    }

    void Gone(Service service, int pid)
    {
        _pids.Remove(service);
        _cpu.Remove(pid);
        Exited?.Invoke(service, pid);
    }

    ServiceSample Sample(Service service, HashSet<int> listening)
    {
        bool up = service.Ports.Length > 0 && service.Ports.All(listening.Contains);
        int pid;
        lock (_pids)
            if (!_pids.TryGetValue(service, out pid)) return new(service, null, up, 0, 0, 0, 0, TimeSpan.Zero);
        try
        {
            using var p = Process.GetProcessById(pid);
            if (p.HasExited) throw new InvalidOperationException();
            TimeSpan cpu = p.TotalProcessorTime;
            long now = Stopwatch.GetTimestamp();
            double percent = 0;
            if (_cpu.TryGetValue(pid, out var last))
                percent = CpuPercent(cpu - last.Cpu, Stopwatch.GetElapsedTime(last.At, now), Environment.ProcessorCount);
            _cpu[pid] = (cpu, now);
            return new(service, pid, up, percent, p.WorkingSet64 / 1048576.0, p.Threads.Count, p.HandleCount, DateTime.Now - p.StartTime);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            lock (_pids) if (_pids.TryGetValue(service, out int still) && still == pid) Gone(service, pid);
            return new(service, null, up, 0, 0, 0, 0, TimeSpan.Zero);
        }
    }

    /// <summary>The share of all cores' time a process used in a wall-clock interval, 0–100.</summary>
    public static double CpuPercent(TimeSpan used, TimeSpan wall, int cores) =>
        wall <= TimeSpan.Zero || cores <= 0 ? 0 : Math.Clamp(used / wall / cores * 100, 0, 100);

    public void Dispose() => _timer.Dispose();
}
