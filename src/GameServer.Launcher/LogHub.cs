namespace GameServer.Launcher;

/// <summary>A log the launcher shows: a file and the error file beside it (live.ps1 writes each server's stdout to
/// logs\live-*.log and its stderr to .err), or only the launcher's own lines.</summary>
sealed class LogSource(string name, string? log, string? err = null) : System.ComponentModel.INotifyPropertyChanged
{
    public const int MaxLines = 20000;

    public string Name { get; } = name;
    public string? File { get; } = log;
    public LogTail? Log { get; } = log is null ? null : new LogTail(log);
    public LogTail? Err { get; } = err is null ? null : new LogTail(err);
    public List<LogLine> Lines { get; } = [];
    public int Errors { get; set; }

    /// <summary>The source list's label: the name and its error count.</summary>
    public string Display => Errors > 0 ? $"{Name}  ({Errors} errors)" : Name;

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    public void DisplayChanged() => PropertyChanged?.Invoke(this, new(nameof(Display)));
}

/// <summary>Follows every log twice a second, keeps their newest lines, and collects every error line into one more
/// source, "All errors", together with the launcher's own reports (a server that exits while no live.ps1 runs).</summary>
sealed class LogHub : IDisposable
{
    public LogSource Launcher { get; } = new("Launcher", null);
    public LogSource AllErrors { get; } = new("All errors", null);
    public IReadOnlyList<LogSource> Sources { get; }

    /// <summary>On the polling thread: a source got lines (or started over: restarted = true).</summary>
    public event Action<LogSource, IReadOnlyList<LogLine>, bool>? Appended;

    readonly Timer _timer;
    readonly Lock _lock = new();
    int _busy;

    public LogHub()
    {
        string Logs(string file) => Path.Combine(Repo.Logs, file);
        Sources =
        [
            AllErrors,
            new("Gateway", Logs("live-gateway.log"), Logs("live-gateway.err")),
            new("Auth", Logs("live-auth.log"), Logs("live-auth.err")),
            new("Patch", Logs("live-patch.log"), Logs("live-patch.err")),
            new("Walk maps", Logs("walkmaps.log"), Logs("walkmaps.err")),
            new("Dev host", Logs("launcher-host.log"), Logs("launcher-host.err")),
            new("Client", Path.Combine("Client", "logs", "Client.txt")),
            Launcher,
        ];
        _timer = new Timer(_ => Poll(), null, 0, 500);
    }

    /// <summary>A line of the launcher's own (live.ps1 output, the launcher's reports).</summary>
    public void Write(string text, Severity? severity = null)
    {
        var line = new LogLine(Launcher.Name, text, severity ?? LogSeverity.Of(text), DateTime.Now);
        lock (_lock) Add(Launcher, [line], false);
    }

    void Poll()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            foreach (var source in Sources)
            {
                var lines = new List<LogLine>();
                bool restarted = false;
                foreach (var (tail, isErr) in new[] { (source.Log, false), (source.Err, true) })
                {
                    if (tail is null) continue;
                    var read = tail.Read();
                    restarted |= tail.Restarted && !isErr;
                    var now = DateTime.Now;
                    lines.AddRange(read.Select(l => new LogLine(source.Name, isErr ? $"[stderr] {l}" : l, LogSeverity.Of(l, isErr), now)));
                }
                if (lines.Count > 0 || restarted)
                    lock (_lock) Add(source, lines, restarted);
            }
        }
        // As the monitor's: an exception on the timer thread would end the launcher.
        catch (Exception e) { Write($"reading the logs failed: {e.Message}", Severity.Error); }
        finally { Volatile.Write(ref _busy, 0); }
    }

    void Add(LogSource source, List<LogLine> lines, bool restarted)
    {
        if (restarted) { source.Lines.Clear(); source.Errors = 0; }
        source.Lines.AddRange(lines);
        if (source.Lines.Count > LogSource.MaxLines) source.Lines.RemoveRange(0, source.Lines.Count - LogSource.MaxLines);
        var errors = lines.Where(l => l.Severity == Severity.Error).ToList();
        source.Errors += errors.Count;
        Appended?.Invoke(source, lines, restarted);
        if (errors.Count > 0 && source != AllErrors)
        {
            AllErrors.Lines.AddRange(errors);
            AllErrors.Errors += errors.Count;
            if (AllErrors.Lines.Count > LogSource.MaxLines) AllErrors.Lines.RemoveRange(0, AllErrors.Lines.Count - LogSource.MaxLines);
            Appended?.Invoke(AllErrors, errors, false);
        }
    }

    /// <summary>The lines of a source, a copy (the polling thread adds to them).</summary>
    public List<LogLine> Snapshot(LogSource source)
    {
        lock (_lock) return [.. source.Lines];
    }

    public void Clear(LogSource source)
    {
        lock (_lock) { source.Lines.Clear(); source.Errors = 0; }
    }

    public void Dispose() => _timer.Dispose();
}
