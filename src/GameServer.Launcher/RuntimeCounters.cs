using System.Collections.Concurrent;
using System.Diagnostics.Tracing;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;

namespace GameServer.Launcher;

/// <summary>
/// The .NET runtime's own counters of one server process (the <c>System.Runtime</c> event source, what
/// <c>dotnet-counters</c> shows), read through the runtime's diagnostics port: GC heap and collections, allocation rate,
/// thread pool, lock contention, exceptions. Nothing in the server changes; a session only subscribes to the counters,
/// once a second.
/// </summary>
sealed class RuntimeCounters : IDisposable
{
    public const string GcHeap = "gc-heap-size", AllocRate = "alloc-rate", TimeInGc = "time-in-gc",
        Gen0 = "gen-0-gc-count", Gen1 = "gen-1-gc-count", Gen2 = "gen-2-gc-count",
        PoolThreads = "threadpool-thread-count", PoolQueue = "threadpool-queue-length",
        LockContention = "monitor-lock-contention-count", Exceptions = "exception-count";

    readonly ConcurrentDictionary<string, double> _values = new();
    readonly EventPipeSession? _session;

    public int Pid { get; }
    public string? Error { get; private set; }

    public RuntimeCounters(int pid)
    {
        Pid = pid;
        try
        {
            var provider = new EventPipeProvider("System.Runtime", EventLevel.Informational, -1,
                new Dictionary<string, string> { ["EventCounterIntervalSec"] = "1" });
            _session = new DiagnosticsClient(pid).StartEventPipeSession([provider], requestRundown: false, circularBufferMB: 16);
            var thread = new Thread(Read) { IsBackground = true, Name = $"counters {pid}" };
            thread.Start(_session);
        }
        // Whatever a session fails with, the charts only lose this process's counters; the launcher goes on.
        catch (Exception e)
        {
            Error = e.Message;
        }
    }

    void Read(object? state)
    {
        try
        {
            using var source = new EventPipeEventSource(((EventPipeSession)state!).EventStream);
            source.Dynamic.All += e =>
            {
                if (e.EventName == "EventCounters" && Counter(e.PayloadValue(0)) is var (name, value)) _values[name] = value;
            };
            source.Process();
        }
        // A server that live.ps1 stops ends the stream anywhere, even inside a block (TraceEvent's FormatException "Read
        // past end of stream"); an exception left on this thread would end the whole launcher.
        catch (Exception e)
        {
            Error ??= e.Message;
        }
    }

    /// <summary>An EventCounters payload's name and value: the mean of a polling counter, the change of an incrementing
    /// one over the interval.</summary>
    internal static (string Name, double Value)? Counter(object? payload)
    {
        if (payload is not IDictionary<string, object> outer || outer.TryGetValue("Payload", out var inner) is false
            || inner is not IDictionary<string, object> fields || fields.TryGetValue("Name", out var name) is false || name is not string n)
            return null;
        string key = fields.TryGetValue("CounterType", out var type) && type as string == "Sum" ? "Increment" : "Mean";
        return fields.TryGetValue(key, out var value) && value is not null
            ? (n, Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture)) : null;
    }

    /// <summary>The latest value of a counter, NaN before the first one arrived.</summary>
    public double this[string counter] => _values.TryGetValue(counter, out double v) ? v : double.NaN;

    public void Dispose()
    {
        // The process is often gone already.
        try
        {
            _session?.Stop();
            _session?.Dispose();
        }
        catch (Exception) { }
    }
}
