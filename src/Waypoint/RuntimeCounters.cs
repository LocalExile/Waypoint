using System.Collections.Concurrent;
using System.Diagnostics.Tracing;
using System.Globalization;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;

namespace Waypoint;

/// <summary>
/// The .NET runtime's own counters of one server process (the <c>System.Runtime</c> event source, what
/// <c>dotnet-counters</c> shows), read through the runtime's diagnostics port: GC heap and collections, allocation rate,
/// thread pool, lock contention, exceptions. Nothing in the server changes; a session only subscribes to the counters,
/// once a second. Given <see cref="OperationTimings"/>, the same session also reads the Gateway's duration histogram
/// from the <c>System.Diagnostics.Metrics</c> event source (what <c>dotnet-counters</c> shows for a meter).
/// </summary>
sealed class RuntimeCounters : IDisposable
{
    public const string GcHeap = "gc-heap-size", AllocRate = "alloc-rate", TimeInGc = "time-in-gc",
        Gen0 = "gen-0-gc-count", Gen1 = "gen-1-gc-count", Gen2 = "gen-2-gc-count",
        PoolThreads = "threadpool-thread-count", PoolQueue = "threadpool-queue-length",
        LockContention = "monitor-lock-contention-count", Exceptions = "exception-count";

    readonly ConcurrentDictionary<string, double> _values = new();
    readonly EventPipeSession? _session;
    readonly OperationTimings? _timings;
    readonly Func<double> _clock;
    // The metrics event source serves several sessions at once (dotnet-counters beside the launcher) and tags its events
    // with the session's id.
    readonly string _metricsSession = Guid.NewGuid().ToString();

    public int Pid { get; }
    public string? Error { get; private set; }
    /// <summary>Why the operation timings stopped or never came (the event source's own error), else null.</summary>
    public string? TimingsError { get; private set; }

    public RuntimeCounters(int pid, OperationTimings? timings = null, Func<double>? clock = null)
    {
        Pid = pid;
        _timings = timings;
        _clock = clock ?? (() => 0);
        try
        {
            List<EventPipeProvider> providers = [new("System.Runtime", EventLevel.Informational, -1,
                new Dictionary<string, string> { ["EventCounterIntervalSec"] = "1" })];
            // Keywords: Messages (errors) and TimeSeriesValues. Every (phase, operation) is a histogram of its own, one per
            // client message id among them: far more than the default limit of 20.
            if (timings is not null)
                providers.Add(new("System.Diagnostics.Metrics", EventLevel.Informational, 0x3, new Dictionary<string, string>
                {
                    ["SessionId"] = _metricsSession, ["Metrics"] = OperationTimings.Meter, ["RefreshInterval"] = "1",
                    ["MaxHistograms"] = "5000", ["MaxTimeSeries"] = "5000",
                }));
            _session = new DiagnosticsClient(pid).StartEventPipeSession(providers, requestRundown: false, circularBufferMB: 16);
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
                else if (_timings is not null && e.ProviderName == "System.Diagnostics.Metrics") Metric(e);
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

    /// <summary>An event of the metrics event source: a histogram's interval for the timings, or why there are none.</summary>
    void Metric(TraceEvent e)
    {
        string? Field(string name) => e.PayloadByName(name)?.ToString();
        // Events of a dotnet-counters session running beside this one carry its id.
        string? session = Field("sessionId");
        if (session is not null && session.Length > 0 && session != _metricsSession) return;
        switch (e.EventName)
        {
            case "HistogramValuePublished" when Field("meterName") == OperationTimings.Meter && Field("instrumentName") == OperationTimings.Instrument:
                if (OperationTimings.Tags(Field("tags")) is not var (phase, operation)) return;
                double count = double.TryParse(Field("count"), CultureInfo.InvariantCulture, out double c) ? c : 0;
                double sum = double.TryParse(Field("sum"), CultureInfo.InvariantCulture, out double s) ? s : 0;
                var quantiles = OperationTimings.Quantiles(Field("quantiles"));
                _timings!.Add(_clock(), phase, operation, quantiles is null ? 0 : count, sum, quantiles?.P95 ?? 0, quantiles?.P99 ?? 0);
                break;
            case "Error" or "MultipleSessionsNotSupportedError" or "MultipleSessionsConfiguredIncorrectlyError"
                or "HistogramLimitReached" or "TimeSeriesLimitReached" or "ObservableInstrumentCallbackError":
                TimingsError = Field("errorMessage") is { Length: > 0 } message ? $"{e.EventName}: {message}" : e.EventName;
                break;
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
