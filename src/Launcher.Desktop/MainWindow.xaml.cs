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
    readonly DispatcherTimer operationTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly Stopwatch operationClock = new();
    readonly HashSet<string> privateNames = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, (int Pid, string Started, double Cpu, DateTime Time)> samples = new();
    LauncherSettings settings = new();
    RuntimeSnapshot? snapshot;
    string operationText = "";
    string logSource = "Operations";
    bool busy;
    bool closing;
    string? lastError;
    string currentStage = "";
    string stageDetail = "";
    string operationLogPath = "";
    string stageLogPath = "";
    string selectedLogText = "";
    (string Action, string Service)? retry;
    string ElapsedText => $"{(int)operationClock.Elapsed.TotalHours:00}:{operationClock.Elapsed.Minutes:00}:{operationClock.Elapsed.Seconds:00}";

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
        operationTimer.Tick += (_, _) => UpdateOperationLabel();
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
            SetupRequirementList.ItemsSource = null;
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
        var steps = state.Requirements.Select(r => SetupGuidance.Describe(r, state.Root)).ToArray();
        RequirementList.ItemsSource = steps;
        SetupRequirementList.ItemsSource = steps;
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

    async Task OperateAsync(string action, string service = "All", AccountRequest? account = null)
    {
        if (busy) return;
        if (string.IsNullOrWhiteSpace(settings.RepositoryPath)) { Pages.SelectedItem = SettingsPage; return; }
        busy = true;
        await gate.WaitAsync();
        lastError = null;
        retry = action == "Register" ? null : (action, service);
        FailureActions.Visibility = Visibility.Collapsed;
        operationLogPath = stageLogPath = "";
        currentStage = "Checking setup";
        stageDetail = "Checking prerequisites and the selected installation.";
        operationClock.Restart();
        operationTimer.Start();
        BusyProgress.Visibility = Visibility.Visible;
        UpdateOperationLabel();
        if (snapshot is not null) Render(snapshot);
        try
        {
            var result = await runtime.RunAsync(settings, action, service, text => Dispatcher.Invoke(() => AppendLog(text)),
                progress: update => Dispatcher.Invoke(() =>
                {
                    currentStage = update.Stage;
                    stageDetail = update.Detail;
                    if (!string.IsNullOrEmpty(update.LogPath))
                    {
                        stageLogPath = update.LogPath;
                        if (Path.GetFileName(update.LogPath) == "operation.log") operationLogPath = update.LogPath;
                    }
                    UpdateOperationLabel();
                }), account: account);
            if (!result.Ok)
            {
                currentStage = result.FailedStage ?? currentStage;
                throw new IOException(result.Message);
            }
            snapshot = result.Snapshot;
            StatusLabel.Text = result.Message;
            AppendLog(result.Message);
        }
        catch (Exception ex)
        {
            lastError = ex.Message;
            StatusLabel.Text = currentStage + " failed: " + ex.Message;
            AppendLog("ERROR in " + currentStage + ": " + ex.Message);
            FailureActions.Visibility = Visibility.Visible;
            logSource = "Operations";
            RefreshLog();
        }
        finally
        {
            busy = false;
            operationClock.Stop();
            operationTimer.Stop();
            OperationDetail.Text = $"{currentStage} · elapsed {ElapsedText}";
            BusyProgress.Visibility = Visibility.Collapsed;
            gate.Release();
            await RefreshAsync();
            if (lastError is not null) StatusLabel.Text = currentStage + " failed: " + lastError;
        }
    }

    void AppendLog(string text)
    {
        operationText += $"[{DateTime.Now:T}] {text}{Environment.NewLine}";
        if (operationText.Length > 150_000) operationText = operationText[^100_000..];
        if (logSource == "Operations") { selectedLogText = operationText; ApplyLogFilter(); }
    }
    void RefreshLog()
    {
        if (logSource == "Operations") selectedLogText = operationText;
        else if (logSource == "Game") selectedLogText = LogReader.Tail(LogReader.GameLog(settings.RepositoryPath));
        else
        {
            string stem = Path.Combine(settings.RepositoryPath, "logs", "live-" + logSource.ToLowerInvariant());
            selectedLogText = $"{logSource} output (latest 64 KB per file)\n\n" + LogReader.Tail(stem + ".log") + "\n\nSTANDARD ERROR\n\n" + LogReader.Tail(stem + ".err");
        }
        ApplyLogFilter();
    }
    void ApplyLogFilter()
    {
        if (LogText is null || LogSearchBox is null || WarningsOnlyBox is null) return;
        string filtered = LogReader.Filter(selectedLogText, LogSearchBox.Text, WarningsOnlyBox.IsChecked == true);
        if (LogText.Text != filtered) LogText.Text = filtered;
    }

    void UpdateOperationLabel()
    {
        if (!busy) return;
        StatusLabel.Text = currentStage + "…";
        OperationDetail.Visibility = Visibility.Visible;
        OperationDetail.Text = $"{stageDetail} · elapsed {ElapsedText}";
    }
    void LogSearch_Changed(object sender, TextChangedEventArgs e) => ApplyLogFilter();
    void LogFilter_Changed(object sender, RoutedEventArgs e) => ApplyLogFilter();
    void DiagnosticReport_Click(object sender, RoutedEventArgs e)
    {
        RefreshLog();
        var state = snapshot ?? new RuntimeSnapshot(settings.RepositoryPath, "Unavailable", "", "", "", "",
            [new("Installation inspection", false, "Select and check a valid checkout first.")], [], []);
        new DiagnosticPreviewDialog(this, additional => DiagnosticReport.Create(state, VersionLabel.Text, operationText,
            lastError, logSource, selectedLogText, privateNames.Concat(additional))).ShowDialog();
    }
    void ShowSetup_Click(object sender, RoutedEventArgs e) => Pages.SelectedItem = SetupPage;
    void ShowSettings_Click(object sender, RoutedEventArgs e) => Pages.SelectedItem = SettingsPage;
    void OpenClient_Click(object sender, RoutedEventArgs e)
    {
        string path = Path.Combine(settings.RepositoryPath, "Client");
        OpenFolder(Directory.Exists(path) ? path : settings.RepositoryPath);
    }
    async void SetupStep_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is SetupStep step) await RunSetupStep(step);
    }
    async Task RunSetupStep(SetupStep step)
    {
        if (busy) return;
        if (new SetupInstructionsDialog(this, step, settings.RepositoryPath).ShowDialog() != true) return;
        switch (step.Action)
        {
            case "PrepareClient": await OperateAsync("PrepareClient"); break;
            case "Dependencies": await OperateAsync("Dependencies"); break;
            case "Dotnet": OpenDownload("https://dotnet.microsoft.com/en-us/download/dotnet/10.0"); break;
            case "Python": OpenDownload("https://www.python.org/downloads/windows/"); break;
            case "Node": OpenDownload("https://nodejs.org/en/download"); break;
            default: OpenGuide_Click(this, new RoutedEventArgs()); break;
        }
    }
    async void Prepare_Click(object sender, RoutedEventArgs e) => await RunSetupStep(SetupGuidance.Describe(new("Prepared client", false, ""), settings.RepositoryPath));
    async void Dependencies_Click(object sender, RoutedEventArgs e) => await RunSetupStep(SetupGuidance.Describe(new("Bundle decompressor", false, ""), settings.RepositoryPath));
    async void CreateAccount_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        if (snapshot is null) { StatusLabel.Text = "Select and check an installation first."; return; }
        var dialog = new CreateAccountDialog(this, snapshot.SaveDatabase);
        if (dialog.ShowDialog() == true && dialog.Request is { } request)
        {
            privateNames.Add(request.Name);
            await OperateAsync("Register", account: request);
        }
    }
    void OpenDownload(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { StatusLabel.Text = "Could not open download page: " + ex.Message; }
    }
    void OpenOperationLog_Click(object sender, RoutedEventArgs e)
    {
        string path = File.Exists(stageLogPath) ? stageLogPath : operationLogPath;
        if (File.Exists(path + ".err") && new FileInfo(path + ".err").Length > 0) path += ".err";
        if (File.Exists(path)) Process.Start(new ProcessStartInfo("notepad.exe") { ArgumentList = { path }, UseShellExecute = false });
        else ViewOperations_Click(sender, e);
    }
    void ViewOperations_Click(object sender, RoutedEventArgs e) { logSource = "Operations"; RefreshLog(); Pages.SelectedItem = LogsPage; }
    async void Retry_Click(object sender, RoutedEventArgs e)
    {
        if (retry is { } operation) await OperateAsync(operation.Action, operation.Service);
        else StatusLabel.Text = "For account creation, open Create local account again; passwords are never kept for retry.";
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
        operationTimer.Stop();
    }
}

public sealed record ServiceRow(string Name, string Status, string PortText, string Metrics, string Detail,
    string Action, bool CanAct, bool CanRestart, string StateColor, string Cpu, string Memory, string Uptime);
