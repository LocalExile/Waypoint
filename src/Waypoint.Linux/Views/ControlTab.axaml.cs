using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Material.Icons;

namespace Waypoint;

/// <summary>A service's row on the Control section, updated in place each second (a new list would close an open menu
/// and lose the hover).</summary>
sealed class ServiceRow(Service service, Func<string> description, MaterialIconKind icon) : INotifyPropertyChanged
{
    public Service Service { get; } = service;
    public string Name => Service.Name;
    public string DisplayName => Service.DisplayName;
    public string Description => description();
    public MaterialIconKind Icon { get; } = icon;

    public bool Running { get; private set; }
    public string State { get; private set; } = "";
    public IBrush Lamp { get; private set; } = Brushes.Gray;
    public string Ports => Service.Ports.Length > 0 ? Service.PortList : "—";
    public string Pid { get; private set; } = "—";
    public string Uptime { get; private set; } = "—";
    public string Cpu { get; private set; } = "—";
    public string Memory { get; private set; } = "—";
    /// <summary>What the state means where it is not obvious: a Gateway under hot reload.</summary>
    public string? StateTip { get; private set; }

    /// <summary>The recorder starts only with a live.ps1 run (Walk maps); the rest start alone.</summary>
    public bool CanAct => !_live && (Running || Service.Kind != ServiceKind.Recorder);
    public bool CanRestart => !_live && Service.Kind == ServiceKind.Server && !Repo.IsInstall;
    /// <summary>A release starts and stops everything together, with one LocalExile.exe.</summary>
    public bool ActionShown => !Repo.IsInstall;
    public string ActionText => L.Text(Running ? "Stop" : "Start");
    public MaterialIconKind ActionIcon => Running ? MaterialIconKind.Stop : MaterialIconKind.Play;
    public IBrush ActionBrush => App.Brush(Running ? "Shell.Error" : "Shell.Good");
    public string ActionTip => (Service.Kind, Running) switch
    {
        (ServiceKind.Server, false) => L.Text("live.ps1 -Only {0}: builds and starts it", Name),
        (ServiceKind.Server, true) => $"live.ps1 -Stop -Only {Name}",
        (ServiceKind.Client, false) => L.Text("live.ps1 -ClientOnly: the game, against the running servers"),
        (ServiceKind.Client, true) => L.Text("Closes the game; the servers keep running"),
        (ServiceKind.DevHost, false) => L.Text("Builds and starts the account service (the Accounts section starts it too)"),
        (ServiceKind.DevHost, true) => L.Text("Stops the account service"),
        (ServiceKind.Recorder, false) => L.Text("Starts with Play or Start servers while Walk maps is on"),
        _ => L.Text("Stops the walk-map recorder"),
    };

    bool _live;

    public void Update(ServiceSample sample, bool live)
    {
        _live = live;
        Running = sample.Running;
        bool hasPorts = Service.Ports.Length > 0;
        (string lamp, State) = (sample.Running, sample.Listening) switch
        {
            (true, true) => ("Shell.Good", L.Text(sample.HotReload ? "Running · hot reload" : "Running")),
            (true, false) when !hasPorts => ("Shell.Good", L.Text("Running")),
            (true, false) => ("Shell.Warning", L.Text("Starting")),
            (false, true) => ("Shell.Error", L.Text("Port held by another")),
            _ => ("Shell.Idle", L.Text("Stopped")),
        };
        Lamp = App.Brush(lamp);
        StateTip = sample.HotReload
            ? L.Text("Under dotnet watch: a saved edit to the Gateway's code applies within seconds, the game staying in; an edit hot " +
              "reload cannot apply restarts the Gateway by itself. Edits to Auth, Patch or shared projects need Restart servers.")
            : null;
        Pid = sample.Pid?.ToString() ?? "—";
        Uptime = sample.Running ? $"{(int)sample.Uptime.TotalHours}:{sample.Uptime:mm\\:ss}" : "—";
        Cpu = sample.Running ? $"{sample.CpuPercent:0.0}%" : "—";
        Memory = sample.Running ? L.Text("{0:0} MB", sample.MemoryMb) : "—";
        PropertyChanged?.Invoke(this, new(null));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>A line of the Setup list: the check with the icon and colour its record has on Windows.</summary>
sealed record SetupRow(SetupCheck Check)
{
    public string Text => Check.Text;
    /// <summary>Null rather than empty: an empty tooltip would still open.</summary>
    public string? Hint => Check.Hint.Length > 0 ? Check.Hint : null;
    public MaterialIconKind Icon => Check.Ok switch { true => MaterialIconKind.CheckCircleOutline, false => MaterialIconKind.CloseCircleOutline, null => MaterialIconKind.AlertCircleOutline };
    public IBrush Brush => App.Brush(Check.Ok switch { true => "Shell.Good", false => "Shell.Error", null => "Shell.Warning" });
}

partial class ControlTab : UserControl
{
    Shared _shared = null!;
    readonly StringBuilder _output = new();
    const int OutputMax = 200_000;
    readonly List<ServiceRow> _rows =
    [
        new(Service.Patch, () => L.Text("Patch server: the client's update check stays local"), MaterialIconKind.Download),
        new(Service.Auth, () => L.Text("Login and character selection"), MaterialIconKind.ShieldAccount),
        new(Service.Gateway, () => L.Text("The game world"), MaterialIconKind.Earth),
        new(Service.DevHost, () => L.Text("Accounts and characters"), MaterialIconKind.AccountGroup),
        new(Service.Client, () => L.Text("Path of Exile, patched for the local servers"), MaterialIconKind.GamepadVariant),
        new(Service.Recorder, () => L.Text("Records each area's walk map with Frida"), MaterialIconKind.MapMarkerPath),
    ];
    bool _live;

    /// <summary>The status line as a function, so that a language change says it again.</summary>
    Func<string> _state = () => L.Text("Ready.");

    void Say(Func<string> state)
    {
        _state = state;
        RunState.Text = state();
    }


    /// <summary>A row's Show log: the window opens the Logs section on that source.</summary>
    internal event Action<string>? ShowLogRequested;
    /// <summary>Guided setup: the window opens the Setup section.</summary>
    internal event Action? ShowSetupRequested;
    /// <summary>The player wants a new git copy set up.</summary>
    internal event Action? NewCopyRequested;
    /// <summary>The player wants another folder (as on PlayTab; on Windows this section asks the application itself).</summary>
    internal event Action? ChangeFolderRequested;
    /// <summary>Play on a release install: the Play view runs it, with its steps.</summary>
    internal event Action? InstallPlayRequested;
    /// <summary>Start servers on a release install.</summary>
    internal event Action? InstallServersRequested;

    public ControlTab() => InitializeComponent();

    Window? Owner => TopLevel.GetTopLevel(this) as Window;

    public void Attach(Shared shared)
    {
        _shared = shared;
        // Walk maps stays off here, whatever the settings say: live.ps1 refuses -Walkmaps on Linux.
        NoBuild.IsChecked = shared.Settings.NoBuild;
        NoExtract.IsChecked = shared.Settings.NoExtract;
        HotReload.IsChecked = shared.Settings.HotReload;
        ServerFolder.Text = Environment.CurrentDirectory;
        ToolTip.SetTip(ServerFolder, Environment.CurrentDirectory);
        ServiceList.ItemsSource = Repo.IsInstall ? _rows.Where(r => r.Service.Kind is ServiceKind.Server or ServiceKind.Client).ToList()
            : _rows.Where(r => r.Service.Kind != ServiceKind.Recorder).ToList();
        if (Repo.IsInstall)
            foreach (var element in new Control[] { StopServersButton, RunOptions, CheckAgainButton, GameDataButton }) element.IsVisible = false;
        shared.Monitor.Sampled += samples => Dispatcher.UIThread.Post(() => ShowServices(samples));
        shared.LiveRunning += running => Dispatcher.UIThread.Post(() =>
        {
            _live = running;
            Toolbar.IsEnabled = !running;
            ShowServices(_shared.Monitor.Latest);
        });
        L.Current.PropertyChanged += (_, e) =>
        {
            ShowServices(_shared.Monitor.Latest);
            Say(_state);
            // The checks write their text as they run; once, not for each of the change's two notifications.
            if (e.PropertyName == nameof(L.Language)) _ = RefreshSetup();
        };
        _ = RefreshSetup();
    }

    void ShowServices(IReadOnlyList<ServiceSample> samples)
    {
        foreach (var row in _rows)
            row.Update(samples.FirstOrDefault(s => s.Service == row.Service) ?? new(row.Service, null, false, 0, 0, 0, 0, TimeSpan.Zero), _live);
        // With servers up the same live.ps1 run restarts them: what is needed after edits hot reload does not cover.
        bool anyUp = samples.Any(s => s.Service.Kind == ServiceKind.Server && s.Running);
        if (Repo.IsInstall)
        {
            bool anything = samples.Any(s => s.Running);
            StartServersText.Text = L.Text("Start servers");
            StartServersIcon.Kind = MaterialIconKind.Play;
            StartServersButton.IsEnabled = PlayButton.IsEnabled = !anything;
            ToolTip.SetTip(StartServersButton, L.Text("Patch, Auth and Gateway without the game. To play, stop them and press Play: one LocalExile.exe runs an install at a time."));
            ToolTip.SetTip(PlayButton, L.Text("LocalExile.exe: starts the servers, then the game"));
            return;
        }
        StartServersText.Text = L.Text(anyUp ? "Restart servers" : "Start servers");
        StartServersIcon.Kind = anyUp ? MaterialIconKind.Refresh : MaterialIconKind.Play;
        ToolTip.SetTip(StartServersButton, anyUp
            ? L.Text("live.ps1 -NoClient: rebuilds and restarts Patch, Auth and Gateway (after a pull, or edits outside the Gateway); " +
              "the Gateway's own code edits apply by themselves with hot reload. A running client stays.")
            : L.Text("live.ps1 -NoClient: builds and starts Patch, Auth and Gateway; a running client stays"));
        ToolTip.SetTip(PlayButton, PlaysOnlyTheGame(samples, Walkmaps.IsChecked == true, Repo.ClientOnlyTakesWalkmaps())
            ? L.Text("live.ps1 -ClientOnly: starts the game against the running servers (Restart servers rebuilds them)")
            : L.Text("live.ps1: builds and starts the servers, then the game (closes a running one)"));
    }

    LiveRequest Request(LiveMode mode, string? server = null)
    {
        var s = _shared.Settings;
        s.Walkmaps = Walkmaps.IsChecked == true;
        s.NoBuild = NoBuild.IsChecked == true;
        s.NoExtract = NoExtract.IsChecked == true;
        s.HotReload = HotReload.IsChecked == true;
        s.Save();
        return new LiveRequest(mode, server, s.Walkmaps, s.NoBuild, s.NoExtract, NoHotReload: !s.HotReload);
    }

    bool ClientRunning => _shared.Monitor.Latest.Any(s => s.Service == Service.Client && s.Running);

    Task<int?> Run(LiveRequest request) => Run(request, _shared.RunLiveAsync);

    /// <summary>A run that starts a Gateway gets a backup of the saves first. Without one it goes on only if the player says
    /// so.</summary>
    async Task<bool> BackUp(LiveRequest request)
    {
        bool startsGateway = request.Mode is LiveMode.All or LiveMode.Servers || request is { Mode: LiveMode.Restart, Server: "Gateway" };
        if (!startsGateway || !_shared.Settings.AutomaticBackups) return true;
        var result = await _shared.Workspace.OperateAsync("Backup", L.Text("Back up now"), quiet: true);
        return result is { Ok: true } || Dialog.Ask(Owner, DialogKind.Warning, L.Text("Start without a backup?"),
            L.Text("The saves could not be backed up. Automatic backups can be turned off on the Backups section."), L.Text("Start anyway"), cancelByDefault: true);
    }

    /// <summary>live.ps1 ends whatever holds a server's port without asking, so Waypoint asks first. Services this checkout
    /// runs do not count: restarting them is what the button is for.</summary>
    async Task<bool> PortsFree(LiveRequest request)
    {
        int[] ports = request.Mode switch
        {
            LiveMode.All or LiveMode.Servers => PortCheck.ServerPorts,
            LiveMode.Restart => [.. Service.All.First(s => s.Name == request.Server).Ports],
            _ => [],
        };
        if (ports.Length == 0) return true;
        int[] own = [.. _shared.Monitor.Latest.Where(s => s.Running && s.Pid is not null).Select(s => s.Pid!.Value)];
        if (await PortCheck.ClearAsync(Owner, ports, own, line => Dispatcher.UIThread.Post(() => Append(line))) == PortAnswer.Free) return true;
        RunState.Text = L.Text("Not started: a server port is still in use.");
        return false;
    }

    /// <summary>The exit code of what ran; null when nothing did.</summary>
    async Task<int?> Run(LiveRequest request, Func<LiveRequest, Action<string>, Task<int?>> run)
    {
        if (!await PortsFree(request)) return null;
        if (!await BackUp(request)) return null;
        Append($"> {request.Describe()}");
        Say(() => L.Text("Running {0} …", request.Describe()));
        var started = Stopwatch.StartNew();
        int? code = await run(request, line => Dispatcher.UIThread.Post(() => Append(line)));
        double seconds = started.Elapsed.TotalSeconds;
        Say(() => code switch
        {
            null => L.Text("live.ps1 is already running."),
            0 => L.Text("Done: {0} ({1:0} s).", request.Describe(), seconds),
            _ => L.Text("Failed: {0} exited with {1}; see the output and the Logs section.", request.Describe(), code),
        });
        Append(code is null ? "" : $"(exit {code})");
        return code;
    }

    async void Play(object? sender, RoutedEventArgs e)
    {
        if (Repo.IsInstall) InstallPlayRequested?.Invoke();
        else await PlayAsync();
    }

    /// <summary>Play: also what the Versions section starts after a switch, and Simple mode's Play. The exit code of the run;
    /// null when none ran.</summary>
    internal async Task<int?> PlayAsync()
    {
        if (PlaysOnlyTheGame(_shared.Monitor.Latest, Walkmaps.IsChecked == true, Repo.ClientOnlyTakesWalkmaps()))
        {
            // The servers stay as they are: only the game (again) against them.
            if (ClientRunning)
            {
                if (!Confirm(L.Text("Restart the game?"), L.Text("The game is running: Play closes it and starts it again; the servers keep running."),
                        L.Text("Restart the game"))) return null;
                // live.ps1 -ClientOnly refuses a second client: this one has to be gone first.
                if (!CloseClient()) return null;
            }
            return await Run(ClientRequest());
        }
        if (ClientRunning && !Confirm(L.Text("Restart the game?"), L.Text("The client is running: Play closes it and starts it again."), L.Text("Restart the game"))) return null;
        return await Run(Request(LiveMode.All));
    }

    /// <summary>Play starts only the game when the three servers already listen: a full live.ps1 run would rebuild and
    /// restart them for nothing (the Gateway's edits apply by hot reload; Restart servers is there for the rest). With
    /// Walk maps on, -ClientOnly starts the recorder too; a checkout whose live.ps1 cannot (<paramref name="clientTakesWalkmaps"/>
    /// false) needs the full run while no recorder runs.</summary>
    internal static bool PlaysOnlyTheGame(IReadOnlyList<ServiceSample> samples, bool walkmaps, bool clientTakesWalkmaps)
    {
        var servers = samples.Where(s => s.Service.Kind == ServiceKind.Server).ToList();
        bool serversUp = servers.Count == Service.All.Count(s => s.Kind == ServiceKind.Server) && servers.All(s => s.Running && s.Listening);
        bool recorderMissing = walkmaps && !clientTakesWalkmaps && !samples.Any(s => s.Service.Kind == ServiceKind.Recorder && s.Running);
        return serversUp && !recorderMissing;
    }

    /// <summary>The game alone, with the walk-map recorder when Walk maps is on and the checkout's live.ps1 can.</summary>
    LiveRequest ClientRequest()
    {
        var request = Request(LiveMode.Client);
        return Repo.ClientOnlyTakesWalkmaps() ? request : request with { Walkmaps = false };
    }

    /// <summary>Ends the game's process and waits for it to be gone; false when it would not end.</summary>
    bool CloseClient()
    {
        if (_shared.Monitor.Latest.FirstOrDefault(s => s.Service == Service.Client)?.Pid is not int pid) return true;
        try
        {
            using var p = Process.GetProcessById(pid);
            p.Kill();
            p.WaitForExit(10_000);
            Append(L.Text("client (pid {0}) closed", pid));
            return true;
        }
        catch (ArgumentException) { return true; }   // already gone
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Append(L.Text("could not close the client: {0}", ex.Message));
            return false;
        }
    }

    async void StartServers(object? sender, RoutedEventArgs e)
    {
        if (Repo.IsInstall) InstallServersRequested?.Invoke();
        else await Run(Request(LiveMode.Servers));
    }

    /// <summary>The client, the recorder, the servers and the dev host.</summary>
    async void StopAll(object? sender, RoutedEventArgs e)
    {
        if (Repo.IsInstall) await _shared.StopAllAsync(line => Dispatcher.UIThread.Post(() => Append(line)));
        else await Run(Request(LiveMode.Stop), (_, output) => _shared.StopAllAsync(output));
    }

    /// <summary>The servers and the recorder; the client stays (it drops to the login screen).</summary>
    async void StopServers(object? sender, RoutedEventArgs e) => await Run(Request(LiveMode.StopServers));

    static ServiceRow RowOf(object? sender) => (ServiceRow)((StyledElement)sender!).DataContext!;

    /// <summary>A row's one button: starts or stops that service alone.</summary>
    async void Toggle(object? sender, RoutedEventArgs e)
    {
        var row = RowOf(sender);
        switch (row.Service.Kind, row.Running)
        {
            case (ServiceKind.Server, false): await Run(Request(LiveMode.Restart, row.Name)); break;
            case (ServiceKind.Server, true): await Run(new LiveRequest(LiveMode.Stop, row.Name)); break;
            case (ServiceKind.Client, false): await StartGame(); break;
            case (ServiceKind.Client, true): StopProcess(row, L.Text("The server saves the character's progress as it happens.")); break;
            case (ServiceKind.DevHost, false): await StartDevHost(); break;
            case (ServiceKind.DevHost, true):
                _shared.StopDevHost();
                Append(L.Text("dev host stopped"));
                break;
            case (ServiceKind.Recorder, true): StopProcess(row, null); break;
        }
    }

    async Task StartGame()
    {
        bool serversUp = _shared.Monitor.Latest.Where(s => s.Service.Kind == ServiceKind.Server).All(s => s.Listening);
        if (!serversUp)
        {
            Dialog.Tell(Owner, DialogKind.Info, L.Text("The servers are not running"),
                L.Text("The game needs all three servers: use Play, or Start servers first."));
            return;
        }
        await Run(ClientRequest());
    }

    async Task StartDevHost()
    {
        Say(() => L.Text("Starting the dev host …"));
        try
        {
            await DevHost.EnsureRunningAsync(line => Dispatcher.UIThread.Post(() => Append(line)));
            Say(() => L.Text("Dev host running."));
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException or System.ComponentModel.Win32Exception)
        {
            Say(() => L.Text("The dev host did not start: {0}", ex.Message));
        }
    }

    /// <summary>Ends a process the launcher may end itself: the client (its player closes it too) or the recorder.</summary>
    void StopProcess(ServiceRow row, string? confirm)
    {
        if (_shared.Monitor.Latest.FirstOrDefault(s => s.Service == row.Service)?.Pid is not int pid) return;
        if (confirm is not null && !Confirm(L.Text("Close {0}?", row.DisplayName), confirm, L.Text("Close"))) return;
        try
        {
            using var p = Process.GetProcessById(pid);
            p.Kill();
            Append(L.Text("{0} (pid {1}) closed", row.DisplayName, pid));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Append(L.Text("could not close {0}: {1}", row.DisplayName, ex.Message));
        }
    }

    void RowMenu(object? sender, RoutedEventArgs e)
    {
        var button = (Button)sender!;
        button.ContextMenu!.Open(button);
    }

    async void RestartRow(object? sender, RoutedEventArgs e) => await Run(Request(LiveMode.Restart, RowOf(sender).Name));

    void ShowRowLog(object? sender, RoutedEventArgs e) => ShowLogRequested?.Invoke(RowOf(sender).Name);

    bool Confirm(string title, string text, string action) => Dialog.Ask(Owner, DialogKind.Question, title, text, action);

    void Append(string line)
    {
        _output.AppendLine(line);
        if (_output.Length > OutputMax) _output.Remove(0, _output.Length - OutputMax);
        Output.Text = _output.ToString();
        Output.ScrollToEnd();
    }

    void ClearOutput(object? sender, RoutedEventArgs e)
    {
        _output.Clear();
        Output.Clear();
    }

    async void CheckSetup(object? sender, RoutedEventArgs e) => await RefreshSetup();

    void OpenGuidedSetup(object? sender, RoutedEventArgs e) => ShowSetupRequested?.Invoke();

    void NewCopyClicked(object? sender, RoutedEventArgs e) => NewCopyRequested?.Invoke();

    /// <summary>The setup checks again (also after the Versions section switched the checkout).</summary>
    internal async Task RefreshSetup()
    {
        if (Repo.IsInstall) return;
        SetupList.ItemsSource = new[] { new SetupRow(new SetupCheck(null, L.Text("checking …"))) };
        SetupList.ItemsSource = (await Task.Run(SetupChecks.Run)).Select(check => new SetupRow(check)).ToList();
    }

    async void CheckData(object? sender, RoutedEventArgs e)
    {
        if (_shared.IsLiveRunning) { Say(() => L.Text("live.ps1 is running; check the game data when it is done.")); return; }
        Append($"> {Repo.ExtractScript} -List");
        int code = await Scripts.RunAsync(Repo.ExtractScript, ["-List"], line => Dispatcher.UIThread.Post(() => Append(line)));
        Append($"(exit {code})");
    }

    void OpenLogs(object? sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(Repo.Logs);
        // A desktop without xdg-open is no error of Waypoint's: nothing else here would catch it.
        try { Process.Start(new ProcessStartInfo("xdg-open", [Path.GetFullPath(Repo.Logs)]) { UseShellExecute = false })?.Dispose(); }
        catch (Win32Exception ex) { Append(ex.Message); }
    }

    void ChangeServer(object? sender, RoutedEventArgs e)
    {
        if (_shared.IsLiveRunning) { Say(() => L.Text("live.ps1 is running; change the server when it is done.")); return; }
        ChangeFolderRequested?.Invoke();
    }
}
