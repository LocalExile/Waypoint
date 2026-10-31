using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MaterialDesignThemes.Wpf;

namespace GameServer.Launcher;

/// <summary>A service's row on the Control section, updated in place each second (a new list would close an open menu
/// and lose the hover).</summary>
sealed class ServiceRow(Service service, string description, PackIconKind icon) : INotifyPropertyChanged
{
    public Service Service { get; } = service;
    public string Name => Service.Name;
    public string Description { get; } = description;
    public PackIconKind Icon { get; } = icon;

    public bool Running { get; private set; }
    public string State { get; private set; } = "";
    public Brush Lamp { get; private set; } = Brushes.Gray;
    public string Ports => Service.Ports.Length > 0 ? Service.PortList : "—";
    public string Pid { get; private set; } = "—";
    public string Uptime { get; private set; } = "—";
    public string Cpu { get; private set; } = "—";
    public string Memory { get; private set; } = "—";
    /// <summary>What the state means where it is not obvious: a Gateway under hot reload.</summary>
    public string? StateTip { get; private set; }

    /// <summary>The recorder starts only with a live.ps1 run (Walk maps); the rest start alone.</summary>
    public bool CanAct => !_live && (Running || Service.Kind != ServiceKind.Recorder);
    public bool CanRestart => !_live && Service.Kind == ServiceKind.Server;
    public string ActionText => Running ? "Stop" : "Start";
    public PackIconKind ActionIcon => Running ? PackIconKind.Stop : PackIconKind.Play;
    public Brush ActionBrush => Theme.Brush(Running ? "Shell.Error" : "Shell.Good");
    public string ActionTip => (Service.Kind, Running) switch
    {
        (ServiceKind.Server, false) => $"live.ps1 -Only {Name}: builds and starts it",
        (ServiceKind.Server, true) => $"live.ps1 -Stop -Only {Name}",
        (ServiceKind.Client, false) => "live.ps1 -ClientOnly: the game, against the running servers",
        (ServiceKind.Client, true) => "Closes the game; the servers keep running",
        (ServiceKind.DevHost, false) => "Builds and starts the account service (the Accounts section starts it too)",
        (ServiceKind.DevHost, true) => "Stops the account service",
        (ServiceKind.Recorder, false) => "Starts with Play or Start servers while Walk maps is on",
        _ => "Stops the walk-map recorder",
    };

    bool _live;

    public void Update(ServiceSample sample, bool live)
    {
        _live = live;
        Running = sample.Running;
        bool hasPorts = Service.Ports.Length > 0;
        (string lamp, State) = (sample.Running, sample.Listening) switch
        {
            (true, true) => ("Shell.Good", sample.HotReload ? "Running · hot reload" : "Running"),
            (true, false) when !hasPorts => ("Shell.Good", "Running"),
            (true, false) => ("Shell.Warning", "Starting"),
            (false, true) => ("Shell.Error", "Port held by another"),
            _ => ("Shell.Idle", "Stopped"),
        };
        Lamp = Theme.Brush(lamp);
        StateTip = sample.HotReload
            ? "Under dotnet watch: a saved edit to the Gateway's code applies within seconds, the game staying in; an edit hot " +
              "reload cannot apply restarts the Gateway by itself. Edits to Auth, Patch or shared projects need Restart servers."
            : null;
        Pid = sample.Pid?.ToString() ?? "—";
        Uptime = sample.Running ? $"{(int)sample.Uptime.TotalHours}:{sample.Uptime:mm\\:ss}" : "—";
        Cpu = sample.Running ? $"{sample.CpuPercent:0.0}%" : "—";
        Memory = sample.Running ? $"{sample.MemoryMb:0} MB" : "—";
        PropertyChanged?.Invoke(this, new(null));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

sealed record SetupCheck(bool? Ok, string Text, string Hint = "")
{
    public PackIconKind Icon => Ok switch { true => PackIconKind.CheckCircleOutline, false => PackIconKind.CloseCircleOutline, null => PackIconKind.AlertCircleOutline };
    public Brush Brush => Theme.Brush(Ok switch { true => "Shell.Good", false => "Shell.Error", null => "Shell.Warning" });
}

public partial class ControlTab : UserControl
{
    Shared _shared = null!;
    readonly StringBuilder _output = new();
    const int OutputMax = 200_000;
    readonly List<ServiceRow> _rows =
    [
        new(Service.Patch, "Patch server: the client's update check stays local", PackIconKind.Download),
        new(Service.Auth, "Login and character selection", PackIconKind.ShieldAccount),
        new(Service.Gateway, "The game world", PackIconKind.Earth),
        new(Service.DevHost, "Accounts and characters", PackIconKind.AccountGroup),
        new(Service.Client, "Path of Exile, patched for the local servers", PackIconKind.GamepadVariant),
        new(Service.Recorder, "Records each area's walk map with Frida", PackIconKind.MapMarkerPath),
    ];
    bool _live;

    /// <summary>A row's Show log: the window opens the Logs section on that source.</summary>
    internal event Action<string>? ShowLogRequested;

    public ControlTab() => InitializeComponent();

    internal void Attach(Shared shared)
    {
        _shared = shared;
        Walkmaps.IsChecked = shared.Settings.Walkmaps;
        NoBuild.IsChecked = shared.Settings.NoBuild;
        NoExtract.IsChecked = shared.Settings.NoExtract;
        HotReload.IsChecked = shared.Settings.HotReload;
        ServerFolder.Text = Environment.CurrentDirectory;
        ServerFolder.ToolTip = Environment.CurrentDirectory;
        ServiceList.ItemsSource = _rows;
        shared.Monitor.Sampled += samples => Dispatcher.BeginInvoke(() => ShowServices(samples));
        shared.LiveRunning += running => Dispatcher.BeginInvoke(() =>
        {
            _live = running;
            Toolbar.IsEnabled = !running;
            ShowServices(_shared.Monitor.Latest);
        });
        // The lamps and buttons take the new theme's colours at once.
        Theme.Changed += () => ShowServices(_shared.Monitor.Latest);
        _ = RefreshSetup();
    }

    void ShowServices(IReadOnlyList<ServiceSample> samples)
    {
        foreach (var row in _rows)
            row.Update(samples.FirstOrDefault(s => s.Service == row.Service) ?? new(row.Service, null, false, 0, 0, 0, 0, TimeSpan.Zero), _live);
        // With servers up the same live.ps1 run restarts them: what is needed after edits hot reload does not cover.
        bool anyUp = samples.Any(s => s.Service.Kind == ServiceKind.Server && s.Running);
        StartServersText.Text = anyUp ? "Restart servers" : "Start servers";
        StartServersIcon.Kind = anyUp ? PackIconKind.Refresh : PackIconKind.Play;
        StartServersButton.ToolTip = anyUp
            ? "live.ps1 -NoClient: rebuilds and restarts Patch, Auth and Gateway (after a pull, or edits outside the Gateway); " +
              "the Gateway's own code edits apply by themselves with hot reload. A running client stays."
            : "live.ps1 -NoClient: builds and starts Patch, Auth and Gateway; a running client stays";
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

    Task Run(LiveRequest request) => Run(request, _shared.RunLiveAsync);

    async Task Run(LiveRequest request, Func<LiveRequest, Action<string>, Task<int?>> run)
    {
        Append($"> {request.Describe()}");
        RunState.Text = $"Running {request.Describe()} …";
        var started = Stopwatch.StartNew();
        int? code = await run(request, line => Dispatcher.BeginInvoke(() => Append(line)));
        RunState.Text = code switch
        {
            null => "live.ps1 is already running.",
            0 => $"Done: {request.Describe()} ({started.Elapsed.TotalSeconds:0} s).",
            _ => $"Failed: {request.Describe()} exited with {code}; see the output and the Logs section.",
        };
        Append(code is null ? "" : $"(exit {code})");
    }

    async void Play(object sender, RoutedEventArgs e) => await PlayAsync();

    /// <summary>Play: also what the Versions section starts after a switch.</summary>
    internal async Task PlayAsync()
    {
        if (ClientRunning && !Confirm("The client is running: Play closes it and starts it again. Go on?")) return;
        await Run(Request(LiveMode.All));
    }

    async void StartServers(object sender, RoutedEventArgs e) => await Run(Request(LiveMode.Servers));

    /// <summary>The client, the recorder, the servers and the dev host.</summary>
    async void StopAll(object sender, RoutedEventArgs e) => await Run(Request(LiveMode.Stop), (_, output) => _shared.StopAllAsync(output));

    /// <summary>The servers and the recorder; the client stays (it drops to the login screen).</summary>
    async void StopServers(object sender, RoutedEventArgs e) => await Run(Request(LiveMode.StopServers));

    static ServiceRow RowOf(object sender) => (ServiceRow)((FrameworkElement)sender).DataContext;

    /// <summary>A row's one button: starts or stops that service alone.</summary>
    async void Toggle(object sender, RoutedEventArgs e)
    {
        var row = RowOf(sender);
        switch (row.Service.Kind, row.Running)
        {
            case (ServiceKind.Server, false): await Run(Request(LiveMode.Restart, row.Name)); break;
            case (ServiceKind.Server, true): await Run(new LiveRequest(LiveMode.Stop, row.Name)); break;
            case (ServiceKind.Client, false): await StartGame(); break;
            case (ServiceKind.Client, true): StopProcess(row, "Close the game client? The server saves progress as it happens."); break;
            case (ServiceKind.DevHost, false): await StartDevHost(); break;
            case (ServiceKind.DevHost, true):
                _shared.StopDevHost();
                Append("dev host stopped");
                break;
            case (ServiceKind.Recorder, true): StopProcess(row, null); break;
        }
    }

    async Task StartGame()
    {
        bool serversUp = _shared.Monitor.Latest.Where(s => s.Service.Kind == ServiceKind.Server).All(s => s.Listening);
        if (!serversUp)
        {
            MessageBox.Show(Window.GetWindow(this), "The servers are not all running: use Play, or Start servers first.", "Start game");
            return;
        }
        await Run(Request(LiveMode.Client));
    }

    async Task StartDevHost()
    {
        RunState.Text = "Starting the dev host …";
        try
        {
            await DevHost.EnsureRunningAsync(line => Dispatcher.BeginInvoke(() => Append(line)));
            RunState.Text = "Dev host running.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException or System.ComponentModel.Win32Exception)
        {
            RunState.Text = $"The dev host did not start: {ex.Message}";
        }
    }

    /// <summary>Ends a process the launcher may end itself: the client (its player closes it too) or the recorder.</summary>
    void StopProcess(ServiceRow row, string? confirm)
    {
        if (_shared.Monitor.Latest.FirstOrDefault(s => s.Service == row.Service)?.Pid is not int pid) return;
        if (confirm is not null && !Confirm(confirm)) return;
        try
        {
            using var p = Process.GetProcessById(pid);
            p.Kill();
            Append($"{row.Name} (pid {pid}) closed");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Append($"could not close {row.Name}: {ex.Message}");
        }
    }

    void RowMenu(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        button.ContextMenu.PlacementTarget = button;
        button.ContextMenu.IsOpen = true;
    }

    async void RestartRow(object sender, RoutedEventArgs e) => await Run(Request(LiveMode.Restart, RowOf(sender).Name));

    void ShowRowLog(object sender, RoutedEventArgs e) => ShowLogRequested?.Invoke(RowOf(sender).Name);

    bool Confirm(string text) =>
        MessageBox.Show(Window.GetWindow(this), text, "LocalExile Launcher", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;

    void Append(string line)
    {
        _output.AppendLine(line);
        if (_output.Length > OutputMax) _output.Remove(0, _output.Length - OutputMax);
        Output.Text = _output.ToString();
        Output.ScrollToEnd();
    }

    void ClearOutput(object sender, RoutedEventArgs e)
    {
        _output.Clear();
        Output.Clear();
    }

    async void CheckSetup(object sender, RoutedEventArgs e) => await RefreshSetup();

    /// <summary>The setup checks again (also after the Versions section switched the checkout).</summary>
    internal async Task RefreshSetup()
    {
        SetupList.ItemsSource = new[] { new SetupCheck(null, "checking …") };
        SetupList.ItemsSource = await Task.Run(SetupChecks.Run);
    }

    async void CheckData(object sender, RoutedEventArgs e)
    {
        if (_shared.IsLiveRunning) { RunState.Text = "live.ps1 is running; check the game data when it is done."; return; }
        Append($"> {Repo.ExtractScript} -List");
        int code = await Scripts.RunAsync(Repo.ExtractScript, ["-List"], line => Dispatcher.BeginInvoke(() => Append(line)));
        Append($"(exit {code})");
    }

    void OpenLogs(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(Repo.Logs);
        Process.Start(new ProcessStartInfo("explorer.exe", Path.GetFullPath(Repo.Logs)) { UseShellExecute = true })?.Dispose();
    }

    void ChangeServer(object sender, RoutedEventArgs e)
    {
        if (_shared.IsLiveRunning) { RunState.Text = "live.ps1 is running; change the server when it is done."; return; }
        App.SwitchServer(Window.GetWindow(this), _shared.Settings);
    }
}
