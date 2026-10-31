namespace Waypoint;

/// <summary>What the sections share: settings, the service monitor and its history, the logs, the client bridge, the
/// adapter's view of the checkout, and the one thing that may hold the checkout: a live.ps1 run, git work or an adapter
/// operation (a second would fight the first over the same processes and files).</summary>
sealed class Shared : IDisposable
{
    public Settings Settings { get; } = ((App)System.Windows.Application.Current).Settings;
    public ServiceMonitor Monitor { get; } = new();
    public Metrics Metrics { get; }
    public LogHub Logs { get; } = new();
    public ClientBridge Bridge { get; } = new();
    public Workspace Workspace { get; }

    int _running;
    /// <summary>A live.ps1 run or a git switch started or ended (true while it runs). Raised on the caller's thread.</summary>
    public event Action<bool>? LiveRunning;
    public bool IsLiveRunning => Volatile.Read(ref _running) == 1;
    /// <summary>What holds the checkout while <see cref="IsLiveRunning"/>: "live.ps1", "git" or an operation's name.</summary>
    public string BusyWith { get; private set; } = "live.ps1";
    DateTime _quietUntil;

    public Shared()
    {
        Metrics = new Metrics(Monitor);
        Workspace = new Workspace(this);
        Monitor.Failed += e => Logs.Write($"service sample failed: {e.Message}", Severity.Error);
        Monitor.Exited += (service, pid) =>
        {
            // Processes come and go while live.ps1 restarts them, and a client is closed by its player.
            if (IsLiveRunning || DateTime.UtcNow < _quietUntil) return;
            bool expected = service.Kind is ServiceKind.Client or ServiceKind.Recorder;
            Logs.Write($"{service.Name} (pid {pid}) exited", expected ? Severity.Info : Severity.Error);
        };
    }

    /// <summary>Runs live.ps1, its output into the launcher's log and <paramref name="output"/>; false when a run is
    /// already going on.</summary>
    public async Task<int?> RunLiveAsync(LiveRequest request, Action<string> output)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) == 1) return null;
        BusyWith = "live.ps1";
        LiveRunning?.Invoke(true);
        try
        {
            Logs.Write($"> {request.Describe()}", Severity.Info);
            void Line(string line) { Logs.Write(line); output(line); }
            int code = await Scripts.RunAsync(request, Line);
            Logs.Write($"live.ps1 exited with {code}", code == 0 ? Severity.Info : Severity.Error);
            return code;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Logs.Write($"live.ps1 could not run: {e.Message}", Severity.Error);
            output(e.Message);
            return -1;
        }
        finally
        {
            // The monitor rescans every two seconds: exits it notices just after the run were the run's.
            _quietUntil = DateTime.UtcNow.AddSeconds(3);
            Volatile.Write(ref _running, 0);
            LiveRunning?.Invoke(false);
        }
    }

    /// <summary>Runs git work on the checkout (the Versions section) while no live.ps1 runs, and keeps live.ps1 out
    /// meanwhile; false when something already runs.</summary>
    public Task<bool> RunGitAsync(Func<Task> work) => RunExclusiveAsync("git", work);

    /// <summary>Runs work that needs the checkout to itself, under <paramref name="name"/> in the status strip; false when
    /// something already runs.</summary>
    public async Task<bool> RunExclusiveAsync(string name, Func<Task> work)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) == 1) return false;
        BusyWith = name;
        LiveRunning?.Invoke(true);
        try { await work(); }
        finally
        {
            Volatile.Write(ref _running, 0);
            LiveRunning?.Invoke(false);
        }
        return true;
    }

    /// <summary>Ends the dev host the launcher started for the accounts tab (it is not one of live.ps1's servers).</summary>
    public void StopDevHost()
    {
        if (Monitor.Latest.FirstOrDefault(s => s.Service == Service.DevHost)?.Pid is not int pid) return;
        _quietUntil = DateTime.UtcNow.AddSeconds(3);
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            p.Kill();
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    /// <summary>Everything the launcher runs or started: live.ps1 -Stop (the client, the recorder, the servers) and the
    /// dev host. Null when a live.ps1 run is already going on (the dev host is stopped all the same).</summary>
    public Task<int?> StopAllAsync(Action<string> output)
    {
        if (Repo.IsInstall) return StopInstallAsync(output);
        if (Monitor.Latest.Any(s => s.Service == Service.DevHost && s.Running)) output("stopping the dev host");
        StopDevHost();
        return RunLiveAsync(new LiveRequest(LiveMode.Stop), output);
    }

    /// <summary>A release install's own stop. It needs no lock: it is also how a run that holds one ends.</summary>
    async Task<int?> StopInstallAsync(Action<string> output)
    {
        _quietUntil = DateTime.UtcNow.AddSeconds(3);
        try
        {
            var result = await LocalExileTool.RunAsync(Environment.CurrentDirectory, ["stop"], e => { if (e.Message is { } message) { Logs.Write($"LocalExile: {message}"); output(message); } });
            return result.Code;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            Logs.Write($"LocalExile.exe could not run: {e.Message}", Severity.Error);
            output(e.Message);
            return -1;
        }
    }

    public void Dispose()
    {
        Settings.Save();
        Monitor.Dispose();
        Metrics.Dispose();
        Logs.Dispose();
        Bridge.Dispose();
    }
}
