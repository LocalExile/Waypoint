using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Launcher.Core;
using Microsoft.Win32;

namespace Launcher.Desktop;

public partial class MainWindow : Window
{
    readonly RuntimeClient runtime = new();
    readonly SemaphoreSlim gate = new(1, 1);
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(5) };
    readonly Dictionary<string, (int Pid, string Started, double Cpu, DateTime Time)> samples = new();
    LauncherSettings settings = new();
    RuntimeSnapshot? snapshot;
    string operationText = "";
    string logSource = "Operations";
    bool busy;
    bool closing;
    string? lastError;

    public MainWindow()
    {
        InitializeComponent();
        try
        {
            using var brand = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "brand.json")));
            Title = BrandName.Text = brand.RootElement.GetProperty("displayName").GetString() ?? "Launcher";
            BrandSubtitle.Text = brand.RootElement.GetProperty("subtitle").GetString();
            VersionLabel.Text = "Version " + brand.RootElement.GetProperty("version").GetString();
            settings = SettingsStore.Load();
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException)
        { lastError = "Could not load branding/settings: " + ex.Message; }
        string[] args = Environment.GetCommandLineArgs();
        int repoArg = Array.IndexOf(args, "--repo");
        if (repoArg >= 0 && repoArg + 1 < args.Length) settings = settings with { RepositoryPath = args[repoArg + 1] };
        RepositoryPathBox.Text = settings.RepositoryPath;
        UseProfileBox.IsChecked = settings.UseLocalProfile;
        Loaded += async (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(settings.RepositoryPath)) Pages.SelectedItem = SettingsPage;
            else await RefreshAsync();
            if (lastError is not null) StatusLabel.Text = lastError;
            timer.Start();
        };
        timer.Tick += async (_, _) => { if (!closing && !busy) await RefreshAsync(); };
        Closing += OnClosing;
    }

    async Task RefreshAsync()
    {
        if (string.IsNullOrWhiteSpace(settings.RepositoryPath) || !await gate.WaitAsync(0)) return;
        try
        {
            var result = await runtime.RunAsync(settings, "Inspect");
            if (closing) return;
            if (!result.Ok || result.Snapshot is null) throw new IOException(result.Message);
            snapshot = result.Snapshot;
            Render(snapshot);
            if (lastError is null) StatusLabel.Text = result.Snapshot.Conflicts.Length > 0
                ? result.Snapshot.Conflicts[0] : "Environment checked · " + result.Snapshot.Profile;
        }
        catch (Exception ex)
        {
            snapshot = null;
            ServiceList.ItemsSource = null;
            PerformanceList.ItemsSource = null;
            RequirementList.ItemsSource = null;
            PlayButton.IsEnabled = false;
            StatusLabel.Text = "Check failed: " + ex.Message;
            ReadinessLabel.Text = "Resolve the check failure in Settings, then refresh.";
        }
        finally { gate.Release(); }
    }

    void Render(RuntimeSnapshot state)
    {
        CheckoutLabel.Text = state.Root;
        SidebarBranch.Text = string.IsNullOrWhiteSpace(state.SourceBranch) ? "Detached source" : state.SourceBranch;
        SourceIdentity.Text = $"{state.SourceBranch}  ·  {state.SourceCommit}";
        BuildIdentity.Text = state.BuildCommit.Replace("; ", Environment.NewLine);
        EnvironmentIdentity.Text = $"Launch profile: {state.Profile}\nSave database: {state.SaveDatabase}\nClient: {Path.Combine(state.Root, "Client", "Game.patched.exe")}";
        ReadinessLabel.Text = state.Conflicts.Length > 0 ? string.Join("\n", state.Conflicts)
            : !state.SetupReady ? "Setup needs attention. See the checks below."
            : state.GameRunning ? "Game running. Close it before stopping or restarting services."
            : state.ServersReady ? "Servers are ready. Play opens the prepared client."
            : "Play prepares and starts the servers, then opens the game.";
        PlayButton.IsEnabled = !busy && state.SafeToLaunch;
        PlayButton.Content = state.GameRunning ? "✓  Game running" : "▶  Play";
        RequirementList.ItemsSource = state.Requirements.Select(r => new { r.Name, r.Detail, Symbol = r.Ready ? "✓" : "!", Color = r.Ready ? "#A4E7C4" : "#FFCB87" }).ToArray();
        var rows = new List<ServiceRow>();
        DateTime now = DateTime.UtcNow;
        foreach (var service in state.Services)
        {
            string cpu = "—";
            if (service.ProcessId > 0 && samples.TryGetValue(service.Name, out var sample)
                && sample.Pid == service.ProcessId && sample.Started == service.StartedUtc && now > sample.Time)
            {
                double value = Math.Clamp((service.CpuSeconds - sample.Cpu) / (now - sample.Time).TotalSeconds / Environment.ProcessorCount * 100, 0, 100);
                cpu = $"{value:0.0}% CPU";
            }
            if (service.ProcessId > 0) samples[service.Name] = (service.ProcessId, service.StartedUtc, service.CpuSeconds, now);
            else samples.Remove(service.Name);
            rows.Add(new(service.Name, service.Status, service.Ports.Length == 0 ? "Client" : string.Join(" / ", service.Ports),
                service.ProcessId > 0 ? $"PID {service.ProcessId} · {service.MemoryMb:N0} MB · {cpu}" : "—",
                service.Detail, service.ProcessId > 0 ? service.Name == "Game" ? "Close" : "Stop" : service.Name == "Game" ? "Play" : "Start",
                !busy && (service.ProcessId > 0 || state.SafeToLaunch),
                !busy && service.Name != "Game" && service.ProcessId > 0 && !state.GameRunning,
                service.ProcessId > 0 ? "#A4E7C4" : "#9DABBE", cpu,
                service.ProcessId > 0 ? $"{service.MemoryMb:N0} MB" : "—", service.ProcessId > 0 ? service.Uptime : "Stopped"));
        }
        ServiceList.ItemsSource = rows;
        PerformanceList.ItemsSource = rows;
        LastCheckedLabel.Text = $"Checked {DateTime.Now:T} · process ownership and required listening ports verified locally";
        RefreshLog();
    }

    async Task OperateAsync(string action, string service = "All")
    {
        if (busy) return;
        if (string.IsNullOrWhiteSpace(settings.RepositoryPath)) { Pages.SelectedItem = SettingsPage; return; }
        await gate.WaitAsync();
        busy = true;
        lastError = null;
        BusyProgress.Visibility = Visibility.Visible;
        StatusLabel.Text = action == "Start" || action == "Play" ? "Preparing environment · extraction and builds may take several minutes…" : action + " in progress…";
        if (snapshot is not null) Render(snapshot);
        try
        {
            var result = await runtime.RunAsync(settings, action, service, text => Dispatcher.Invoke(() => AppendLog(text)));
            if (!result.Ok) throw new IOException(result.Message);
            snapshot = result.Snapshot;
            StatusLabel.Text = result.Message;
            AppendLog(result.Message);
        }
        catch (Exception ex)
        {
            lastError = ex.Message;
            StatusLabel.Text = "Operation failed: " + ex.Message;
            AppendLog("ERROR: " + ex.Message);
            logSource = "Operations";
            RefreshLog();
        }
        finally
        {
            busy = false;
            BusyProgress.Visibility = Visibility.Collapsed;
            gate.Release();
            await RefreshAsync();
            if (lastError is not null) StatusLabel.Text = "Operation failed: " + lastError;
        }
    }

    void AppendLog(string text)
    {
        operationText += $"[{DateTime.Now:T}] {text}{Environment.NewLine}";
        if (operationText.Length > 150_000) operationText = operationText[^100_000..];
        if (logSource == "Operations") { LogText.Text = operationText; LogText.ScrollToEnd(); }
    }
    void RefreshLog()
    {
        if (logSource == "Operations") { LogText.Text = operationText; return; }
        string stem = Path.Combine(settings.RepositoryPath, "logs", "live-" + logSource.ToLowerInvariant());
        LogText.Text = $"{logSource} output (latest 64 KB per file)\n\n" + Tail(stem + ".log") + "\n\nSTANDARD ERROR\n\n" + Tail(stem + ".err");
    }
    static string Tail(string path)
    {
        if (!File.Exists(path)) return "No log file yet.";
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            stream.Seek(Math.Max(0, stream.Length - 65_536), SeekOrigin.Begin);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (IOException ex) { return "Could not read log: " + ex.Message; }
    }

    async void Play_Click(object sender, RoutedEventArgs e) => await OperateAsync("Play");
    async void Start_Click(object sender, RoutedEventArgs e) => await OperateAsync("Start");
    async void Stop_Click(object sender, RoutedEventArgs e) => await OperateAsync("Stop");
    async void CloseGame_Click(object sender, RoutedEventArgs e) => await OperateAsync("CloseGame");
    async void Build_Click(object sender, RoutedEventArgs e) => await OperateAsync("Build");
    async void Refresh_Click(object sender, RoutedEventArgs e) { lastError = null; await RefreshAsync(); }
    async void Service_Click(object sender, RoutedEventArgs e)
    {
        string name = (string)((Button)sender).Tag;
        var state = snapshot?.Services.FirstOrDefault(s => s.Name == name);
        if (name == "Game") await OperateAsync(state?.ProcessId > 0 ? "CloseGame" : "Play");
        else await OperateAsync(state?.ProcessId > 0 ? "Stop" : "Start", name);
    }
    async void Restart_Click(object sender, RoutedEventArgs e) => await OperateAsync("Restart", (string)((Button)sender).Tag);
    void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose the LocalExile folder containing GameServer.slnx" };
        if (dialog.ShowDialog(this) == true) RepositoryPathBox.Text = dialog.FolderName;
    }
    async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (busy) { StatusLabel.Text = "Wait for the current operation before changing checkout."; return; }
        try
        {
            string path = Path.GetFullPath(RepositoryPathBox.Text.Trim());
            if (!File.Exists(Path.Combine(path, "GameServer.slnx"))) throw new IOException("Choose the repository folder containing GameServer.slnx.");
            settings = new(path, UseProfileBox.IsChecked == true);
            SettingsStore.Save(settings);
            samples.Clear();
            lastError = null;
            await RefreshAsync();
        }
        catch (Exception ex) { StatusLabel.Text = "Could not save settings: " + ex.Message; }
    }
    void LogSource_Click(object sender, RoutedEventArgs e) { logSource = (string)((Button)sender).Tag; RefreshLog(); }
    void OpenLogs_Click(object sender, RoutedEventArgs e) => OpenFolder(Path.Combine(settings.RepositoryPath, "logs"));
    void OpenCheckout_Click(object sender, RoutedEventArgs e) => OpenFolder(settings.RepositoryPath);
    void OpenData_Click(object sender, RoutedEventArgs e) => OpenFolder(SettingsStore.DataDirectory);
    void OpenGuide_Click(object sender, RoutedEventArgs e)
    {
        string path = Path.Combine(settings.RepositoryPath, "Agents", "GettingStarted.md");
        if (File.Exists(path)) Process.Start(new ProcessStartInfo("notepad.exe") { ArgumentList = { path }, UseShellExecute = false });
        else StatusLabel.Text = "Select a valid checkout first.";
    }
    void OpenFolder(string path)
    {
        try
        {
            if (!Directory.Exists(path)) { StatusLabel.Text = "Folder does not exist yet: " + path; return; }
            Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { path }, UseShellExecute = false });
        }
        catch (Exception ex) { StatusLabel.Text = "Could not open folder: " + ex.Message; }
    }
    void OnClosing(object? sender, CancelEventArgs e)
    {
        if (busy) { e.Cancel = true; StatusLabel.Text = "Wait for the current build or runtime operation to finish before closing."; return; }
        closing = true;
        timer.Stop();
    }
}

public sealed record ServiceRow(string Name, string Status, string PortText, string Metrics, string Detail,
    string Action, bool CanAct, bool CanRestart, string StateColor, string Cpu, string Memory, string Uptime);
