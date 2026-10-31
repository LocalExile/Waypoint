using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace GameServer.Launcher;

sealed record ServiceRow(string Name, Brush Lamp, string State, string Ports, string Pid, string Uptime, string Cpu, string Memory);

sealed record SetupCheck(bool? Ok, string Text, string Hint = "")
{
    public string Glyph => Ok switch { true => "✓", false => "✗", null => "!" };
    public Brush Brush => Ok switch { true => Brushes.SeaGreen, false => Brushes.Firebrick, null => Brushes.DarkOrange };
}

public partial class ControlTab : UserControl
{
    Shared _shared = null!;
    readonly StringBuilder _output = new();
    const int OutputMax = 200_000;

    public ControlTab() => InitializeComponent();

    internal void Attach(Shared shared)
    {
        _shared = shared;
        Walkmaps.IsChecked = shared.Settings.Walkmaps;
        NoBuild.IsChecked = shared.Settings.NoBuild;
        NoExtract.IsChecked = shared.Settings.NoExtract;
        HotReload.IsChecked = shared.Settings.HotReload;
        TrayWhileGame.IsChecked = shared.Settings.TrayWhileGameRuns;
        ServerFolder.Text = Environment.CurrentDirectory;
        ServerFolder.ToolTip = Environment.CurrentDirectory;
        shared.Monitor.Sampled += samples => Dispatcher.BeginInvoke(() => ShowServices(samples));
        shared.LiveRunning += running => Dispatcher.BeginInvoke(() => Actions.IsEnabled = !running);
        _ = RefreshSetup();
    }

    void ShowServices(IReadOnlyList<ServiceSample> samples)
    {
        ServiceList.ItemsSource = samples.Select(s =>
        {
            bool hasPorts = s.Service.Ports.Length > 0;
            (Brush lamp, string state) = (s.Running, s.Listening) switch
            {
                (true, true) => (Brushes.SeaGreen, "running"),
                (true, false) when !hasPorts => (Brushes.SeaGreen, "running"),
                (true, false) => (Brushes.DarkOrange, "starting (port not open)"),
                (false, true) => (Brushes.Firebrick, "port held by another process"),
                _ => (Brushes.Gray, "stopped"),
            };
            return new ServiceRow(s.Service.Name, lamp, state, s.Service.PortList, s.Pid?.ToString() ?? "",
                s.Running ? $"{(int)s.Uptime.TotalHours}:{s.Uptime:mm\\:ss}" : "",
                s.Running ? $"{s.CpuPercent:0.0}%" : "", s.Running ? $"{s.MemoryMb:0} MB" : "");
        }).ToList();

        // The cards' lamps: how many of the three servers listen, and whether the game runs.
        int up = samples.Count(s => s.Service.Kind == ServiceKind.Server && s.Running && s.Listening);
        int servers = samples.Count(s => s.Service.Kind == ServiceKind.Server);
        (ServersLamp.Text, ServersLamp.Foreground) = up == servers ? ("● running", Brushes.SeaGreen)
            : up == 0 ? ("● stopped", Brushes.Gray) : ($"● {up} of {servers} up", Brushes.DarkOrange);
        bool game = samples.Any(s => s.Service == Service.Client && s.Running);
        (GameLamp.Text, GameLamp.Foreground) = game ? ("● running", Brushes.SeaGreen) : ("● not running", Brushes.Gray);
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

    void TrayChanged(object sender, RoutedEventArgs e)
    {
        if (_shared is null) return;
        _shared.Settings.TrayWhileGameRuns = TrayWhileGame.IsChecked == true;
        _shared.Settings.Save();
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
            _ => $"Failed: {request.Describe()} exited with {code}; see the output and the Logs tab.",
        };
        Append(code is null ? "" : $"(exit {code})");
    }

    async void Play(object sender, RoutedEventArgs e)
    {
        if (ClientRunning && !Confirm("The client is running: Play closes it and starts it again. Go on?")) return;
        await Run(Request(LiveMode.All));
    }

    async void StartServers(object sender, RoutedEventArgs e) => await Run(Request(LiveMode.Servers));

    async void StartGame(object sender, RoutedEventArgs e)
    {
        bool serversUp = _shared.Monitor.Latest.Where(s => s.Service.Kind == ServiceKind.Server).All(s => s.Listening);
        if (!serversUp)
        {
            MessageBox.Show(Window.GetWindow(this), "The servers are not all running: use Play, or Start servers first.", "Start game");
            return;
        }
        await Run(Request(LiveMode.Client));
    }

    /// <summary>The client, the recorder, the servers and the dev host.</summary>
    async void StopAll(object sender, RoutedEventArgs e) => await Run(Request(LiveMode.Stop), (_, output) => _shared.StopAllAsync(output));

    /// <summary>The servers and the recorder; the client stays (it drops to the login screen).</summary>
    async void StopServers(object sender, RoutedEventArgs e) => await Run(Request(LiveMode.StopServers));

    async void Restart(object sender, RoutedEventArgs e) => await Run(Request(LiveMode.Restart, (string)((Button)sender).Tag));

    void StopGame(object sender, RoutedEventArgs e)
    {
        if (_shared.Monitor.Latest.FirstOrDefault(s => s.Service == Service.Client)?.Pid is not int pid) return;
        if (!Confirm("Close the game client? Unsaved progress is saved by the server as it happens.")) return;
        try
        {
            using var p = Process.GetProcessById(pid);
            p.Kill();
            Append($"client (pid {pid}) closed");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Append($"could not close the client: {ex.Message}");
        }
    }

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

    async Task RefreshSetup()
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
