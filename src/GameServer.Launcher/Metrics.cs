using System.Diagnostics;
using System.Windows.Media;

namespace GameServer.Launcher;

/// <summary>The history the performance tab draws, kept from the launcher's start whether the tab is shown or not: CPU
/// and memory of every service, the runtime counters of the .NET ones (one diagnostics session per process), and the
/// Gateway's operation timings.</summary>
sealed class Metrics : IDisposable
{
    public static readonly Dictionary<Service, Color> Colors = new()
    {
        [Service.Patch] = Color.FromRgb(0x8e, 0x6c, 0xef),
        [Service.Auth] = Color.FromRgb(0x2e, 0x86, 0xde),
        [Service.Gateway] = Color.FromRgb(0xe6, 0x7e, 0x22),
        [Service.DevHost] = Color.FromRgb(0x16, 0xa0, 0x85),
        [Service.Client] = Color.FromRgb(0xc0, 0x39, 0x2b),
        [Service.Recorder] = Color.FromRgb(0x7f, 0x8c, 0x8d),
    };

    /// <summary>Each runtime counter shown: its label, how a raw value becomes the shown one, and the chart it goes on.</summary>
    public static readonly (string Counter, string Label, double Scale, Color Color)[] Counters =
    [
        (RuntimeCounters.GcHeap, "GC heap", 1, Color.FromRgb(0x2e, 0x86, 0xde)),
        (RuntimeCounters.AllocRate, "allocated/s", 1 / 1048576.0, Color.FromRgb(0xe6, 0x7e, 0x22)),
        (RuntimeCounters.Gen0, "gen 0", 1, Color.FromRgb(0x27, 0xae, 0x60)),
        (RuntimeCounters.Gen1, "gen 1", 1, Color.FromRgb(0xe6, 0x7e, 0x22)),
        (RuntimeCounters.Gen2, "gen 2", 1, Color.FromRgb(0xc0, 0x39, 0x2b)),
        (RuntimeCounters.TimeInGc, "time in GC", 1, Color.FromRgb(0x8e, 0x6c, 0xef)),
        (RuntimeCounters.PoolThreads, "threads", 1, Color.FromRgb(0x2e, 0x86, 0xde)),
        (RuntimeCounters.PoolQueue, "queued", 1, Color.FromRgb(0xc0, 0x39, 0x2b)),
        (RuntimeCounters.LockContention, "lock contentions/s", 1, Color.FromRgb(0xe6, 0x7e, 0x22)),
        (RuntimeCounters.Exceptions, "exceptions/s", 1, Color.FromRgb(0xc0, 0x39, 0x2b)),
    ];

    readonly Stopwatch _clock = Stopwatch.StartNew();
    readonly Dictionary<Service, RuntimeCounters> _sessions = [];

    public Dictionary<Service, TimeSeries> Cpu { get; } = [];
    public Dictionary<Service, TimeSeries> Memory { get; } = [];
    public Dictionary<(Service, string), TimeSeries> Runtime { get; } = [];
    /// <summary>What each Gateway operation costs, read in the Gateway's counter session.</summary>
    public OperationTimings Timings { get; } = new();

    public double Now => _clock.Elapsed.TotalSeconds;

    public Metrics(ServiceMonitor monitor)
    {
        foreach (var s in Service.All)
        {
            Cpu[s] = new(s.Name, Colors[s]);
            Memory[s] = new(s.Name, Colors[s]);
            if (s.IsDotnet)
                foreach (var c in Counters) Runtime[(s, c.Counter)] = new(c.Label, c.Color);
        }
        monitor.Sampled += Add;
    }

    /// <summary>The runtime counter session of a service, or why there is none.</summary>
    public string CounterState(Service s)
    {
        lock (_sessions)
            return !_sessions.TryGetValue(s, out var session) ? "not running" : session.Error is { } e ? $"no counters: {e}" : $"pid {session.Pid}";
    }

    /// <summary>Where the Gateway's operation timings come from, or why they do not.</summary>
    public string TimingsState()
    {
        lock (_sessions)
            return !_sessions.TryGetValue(Service.Gateway, out var session) ? "Gateway not running"
                : (session.Error ?? session.TimingsError) is { } e ? $"no timings: {e}" : $"Gateway pid {session.Pid}";
    }

    void Add(IReadOnlyList<ServiceSample> samples)
    {
        double t = Now;
        foreach (var sample in samples)
        {
            var s = sample.Service;
            Cpu[s].Add(t, sample.Running ? sample.CpuPercent : double.NaN);
            Memory[s].Add(t, sample.Running ? sample.MemoryMb : double.NaN);
            if (!s.IsDotnet) continue;
            RuntimeCounters? session;
            lock (_sessions)
            {
                _sessions.TryGetValue(s, out session);
                if (session is not null && session.Pid != sample.Pid)
                {
                    session.Dispose();
                    _sessions.Remove(s);
                    session = null;
                }
                // A process a second old may not have opened its diagnostics port yet.
                if (session is null && sample.Pid is int pid && sample.Uptime > TimeSpan.FromSeconds(2))
                    _sessions[s] = session = s == Service.Gateway ? new RuntimeCounters(pid, Timings, () => Now) : new RuntimeCounters(pid);
            }
            foreach (var c in Counters) Runtime[(s, c.Counter)].Add(t, session is null ? double.NaN : session[c.Counter] * c.Scale);
        }
    }

    public void Dispose()
    {
        lock (_sessions)
        {
            foreach (var s in _sessions.Values) s.Dispose();
            _sessions.Clear();
        }
    }
}
