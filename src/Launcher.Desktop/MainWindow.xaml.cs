using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
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
    readonly CancellationTokenSource discoveryCancellation = new();
    readonly Dictionary<string, (int Pid, string Started, double Cpu, DateTime Time)> samples = new();
    LauncherSettings settings = new();
    string brandSubtitle = "Workspace Manager for LocalExile";
    string brandVersion = "";
    RuntimeSnapshot? snapshot;
    string operationText = "";
    string logSource = "Operations";
    bool busy;
    bool closing;
    bool discovering;
    int locationRevision;
    string? headingRoot;
    int headingRevision;
    string? lastError;
    string currentStage = "";
    string stageDetail = "";
    string operationLogPath = "";
    string stageLogPath = "";
    string selectedLogText = "";
    (string Action, string Service)? retry;
    ModRequest? retryMod;
    string ElapsedText => $"{(int)operationClock.Elapsed.TotalHours:00}:{operationClock.Elapsed.Minutes:00}:{operationClock.Elapsed.Seconds:00}";

    public MainWindow()
    {
        InitializeComponent();
        try
        {
            using var brand = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "brand.json")));
            Title = BrandName.Text = brand.RootElement.GetProperty("displayName").GetString() ?? "Launcher";
            brandSubtitle = brand.RootElement.GetProperty("subtitle").GetString() ?? brandSubtitle;
            brandVersion = brand.RootElement.GetProperty("version").GetString() ?? "";
            settings = SettingsStore.Load();
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException)
        { lastError = "Could not load branding/settings: " + ex.Message; }
        string[] args = Environment.GetCommandLineArgs();
        int repoArg = Array.IndexOf(args, "--repo");
        if (repoArg >= 0 && repoArg + 1 < args.Length) settings = settings with { RepositoryPath = args[repoArg + 1] };
        RepositoryPathBox.Text = settings.RepositoryPath;
        UseProfileBox.IsChecked = settings.UseLocalProfile;
        LoadDevelopmentPreferences();
        InitializeInteraction();
        InitializeAccounts();
        InitializeWindowFrame();
        InitializeAppearance();
        ApplyLanguage();
        Pages.SelectionChanged += async (sender, e) =>
        {
            if (e.Source != Pages || !IsLoaded) return;
            if (Pages.SelectedItem == BackupsPage) RefreshBackups();
            if (Pages.SelectedItem == BuildsPage) await LoadSourcesAsync(false);
            if (Pages.SelectedItem == AccountsPage) await LoadAccountsAsync();
        };
        Loaded += async (_, _) =>
        {
            UpdateWorkspaceHeading(settings.RepositoryPath);
            if (string.IsNullOrWhiteSpace(settings.RepositoryPath))
            {
                Pages.SelectedItem = SettingsPage;
                _ = FindRepositoriesAsync();
            }
            else
            {
                DiscoveryStatus.Text = L.Text("Using your selected checkout. Change location or search again at any time.");
                await RefreshAsync();
                if (!RepositoryDiscovery.IsLocalExile(settings.RepositoryPath))
                {
                    Pages.SelectedItem = SettingsPage;
                    _ = FindRepositoriesAsync();
                }
            }
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
            var inspectedSettings = settings;
            var result = await runtime.RunAsync(inspectedSettings, "Inspect");
            if (closing || inspectedSettings != settings) return;
            if (!result.Ok || result.Snapshot is null) throw new IOException(result.Message);
            snapshot = result.Snapshot;
            Render(snapshot);
            SetFeedback(lastError is not null);
            if (lastError is null) StatusLabel.Text = result.Snapshot.Conflicts.Length > 0
                ? L.Describe(result.Snapshot.Conflicts[0]) : L.Text("Environment checked · ") + L.Text(result.Snapshot.Profile);
        }
        catch (Exception ex)
        {
            snapshot = null;
            UpdateSourceSelection();
            UpdateBackupSelection();
            HotReloadStateLabel.Text = L.Text("Unavailable");
            HotReloadDetail.Text = L.Text("Refresh after resolving the installation check failure.");
            serviceRows.Clear();
            requirementRows.Clear();
            modEntries = [];
            ApplyModFilter();
            CheckModsButton.IsEnabled = BuildAllModsButton.IsEnabled = OpenModsButton.IsEnabled = false;
            ModsStatus.Text = L.Text("Workspace inspection failed. Resolve the problem in Settings, then refresh.");
            RefreshBackups();
            PlayButton.IsEnabled = false;
            StartServersButton.IsEnabled = StopServersButton.IsEnabled = CloseGameButton.IsEnabled = false;
            ReadinessTitle.Text = L.Text("Workspace needs attention");
            StatusLabel.Text = L.Text("Check failed: ") + ex.Message;
            ReadinessLabel.Text = L.Text("Resolve the check failure in Settings, then refresh.");
            SetFeedback(true);
        }
        finally { gate.Release(); }
        RenderAccounts(snapshot);
        if (Pages.SelectedItem == AccountsPage && accountsContext.Length == 0) await LoadAccountsAsync();
    }

    static string BuildCommitLabels(string components) => string.Join(Environment.NewLine,
        components.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(component =>
        {
            var parts = component.Split('=', 2);
            string metadata = parts.Length == 2 ? parts[1] : "";
            string commit = Regex.Match(metadata, @"\b(?:[a-fA-F0-9]{64}|[a-fA-F0-9]{40})\b").Value;
            return $"{parts[0]} · {(commit.Length > 0 ? commit : metadata == "not built" ? L.Text("not built") : L.Text("commit unavailable"))}";
        }));

    void UpdateWorkspaceHeading(string path)
    {
        CheckoutLabel.Text = string.IsNullOrWhiteSpace(path) ? L.Text("Select a LocalExile checkout in Settings.") : path;
        string root = RepositoryDiscovery.IsLocalExile(path) ? path : "";
        if (string.Equals(root, headingRoot, StringComparison.OrdinalIgnoreCase)) return;
        headingRoot = root;
        int revision = ++headingRevision;
        WorkspaceTitle.Text = root.Length == 0 ? L.Text("Workspace") : RepositoryNaming.FolderName(root);
        if (root.Length > 0) _ = ResolveWorkspaceHeadingAsync(root, revision);
    }

    async Task ResolveWorkspaceHeadingAsync(string root, int revision)
    {
        try
        {
            string name = await RepositoryNaming.ResolveAsync(root, discoveryCancellation.Token);
            if (!closing && revision == headingRevision) WorkspaceTitle.Text = name;
        }
        catch (OperationCanceledException) when (discoveryCancellation.IsCancellationRequested) { }
    }

    void Render(RuntimeSnapshot state)
    {
        RenderMods(state);
        RenderDevelopment(state);
        RenderAccounts(state);
        UpdateWorkspaceHeading(state.Root);
        SidebarBranch.Text = !string.IsNullOrWhiteSpace(state.SourceBranch) ? state.SourceBranch : !string.IsNullOrWhiteSpace(state.SourceLabel) ? state.SourceLabel : L.Text("Detached source");
        SourceIdentity.Value = $"{SidebarBranch.Text}  ·  {state.SourceCommit}";
        BuildIdentity.Value = BuildCommitLabels(state.BuildCommit);
        EnvironmentIdentity.Text = L.Text("Launch profile: {0}\nSave database: {1}\nClient: {2}", L.Text(state.Profile), state.SaveDatabase, Path.Combine(state.Root, "Client", "Game.patched.exe"));
        ReadinessLabel.Text = state.Conflicts.Length > 0 ? string.Join("\n", state.Conflicts.Select(L.Describe))
            : !state.SetupReady ? L.Text("Setup needs attention. See the checks below.")
            : state.GameRunning ? L.Text("Game running. Close it before stopping or restarting services.")
            : state.ServersReady ? L.Text("Servers are ready. Play opens the prepared client.")
            : L.Text("Play prepares and starts the servers, then opens the game.");
        ReadinessTitle.Text = state.Conflicts.Length > 0 ? L.Text("Resolve the workspace conflict") : !state.SetupReady ? L.Text("Finish setup to play")
            : state.GameRunning ? L.Text("Game is running") : state.ServersReady ? L.Text("Servers ready") : L.Text("Ready to play");
        SetupSummary.Header = state.SetupReady ? L.Text("Setup checks · all passed") : L.Text("Setup checks · {0} need attention", state.Requirements.Count(r => !r.Ready));
        StartServersButton.IsEnabled = !busy && state.SafeToLaunch && !state.ServersReady;
        StopServersButton.IsEnabled = !busy && state.HasServers && !state.GameRunning;
        StopServersButton.ToolTip = state.GameRunning ? L.Text("Close the game before stopping servers.") : L.Text("Gracefully stop servers for this checkout.");
        CloseGameButton.IsEnabled = !busy && state.GameRunning;
        PlayButton.IsEnabled = !busy && state.SafeToLaunch && !state.GameRunning;
        PlayButton.Content = state.GameRunning ? L.Text("✓  Game running") : L.Text("▶  Play");
        var steps = state.Requirements.Select(r => SetupGuidance.Describe(r, state.Root)).ToArray();
        UiCollection.Reconcile(requirementRows, steps, step => step.Name);
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
                service.Detail, service.IsRunning ? service.Name == "Game" ? "Close" : "Stop" : service.Name == "Game" ? "Play" : "Start",
                !busy && (service.IsRunning || state.SafeToLaunch),
                !busy && service.Name != "Game" && service.IsRunning && !state.GameRunning,
                service.ProcessId > 0 ? "#A4E7C4" : "#9DABBE", cpu,
                service.ProcessId > 0 ? $"{service.MemoryMb:N0} MB" : "—", service.ProcessId > 0 ? service.Uptime : service.IsRunning ? "Waiting for process" : "Stopped"));
        }
        UiCollection.Reconcile(serviceRows, rows.Select(row => new ServicePresentation(row)), row => row.Value.Name,
            update: (existing, incoming) => existing.Update(incoming.Value));
        LastCheckedLabel.Text = L.Text("Checked {0:T} · process ownership and required listening ports verified locally", DateTime.Now);
        RefreshLog();
    }

    async Task OperateAsync(string action, string service = "All", AccountRequest? account = null,
        SourceSwitchRequest? source = null, string? backupId = null, ModRequest? mod = null, AccountDataRequest? accountData = null)
    {
        if (busy) return;
        if (string.IsNullOrWhiteSpace(settings.RepositoryPath)) { Pages.SelectedItem = SettingsPage; return; }
        busy = true;
        await gate.WaitAsync();
        lastError = null;
        retry = action is "Register" or "Switch" or "RestoreBackup" or "DeleteBackup" or "RestoreModChoices" or "ManageAccount" ? null : (action, service);
        retryMod = retry is not null ? mod : null;
        FailureActions.Visibility = Visibility.Collapsed;
        operationLogPath = stageLogPath = "";
        currentStage = "Checking setup";
        stageDetail = "Checking prerequisites and the selected installation.";
        operationClock.Restart();
        operationTimer.Start();
        BusyProgress.Visibility = Visibility.Visible;
        SetFeedback();
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
                }), account: account, source: source, backupId: backupId, mod: mod, accountData: accountData);
            if (!result.Ok)
            {
                currentStage = result.FailedStage ?? currentStage;
                throw new IOException(result.Message);
            }
            snapshot = result.Snapshot;
            if (result.AccountData is not null) { accountsContext = CurrentAccountContext; AccountsView.SetInventory(result.AccountData); }
            if (action == "Switch") FreshSavesBox.IsChecked = false;
            StatusLabel.Text = L.Text(result.Message);
            AnnounceStatus();
            AppendLog(result.Message);
        }
        catch (Exception ex)
        {
            lastError = ex.Message;
            StatusLabel.Text = L.Text(currentStage) + L.Text(" failed: ") + ex.Message;
            SetFeedback(true);
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
            OperationDetail.Text = L.Text("{0} · elapsed {1}", L.Text(currentStage), ElapsedText);
            BusyProgress.Visibility = Visibility.Collapsed;
            gate.Release();
            await RefreshAsync();
            if (action is "Switch" or "Fetch") await LoadSourcesAsync(false);
            RefreshBackups();
            if (lastError is not null) StatusLabel.Text = L.Text(currentStage) + L.Text(" failed: ") + lastError;
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
        SelectedLogLabel.Text = logSource == "Operations" ? L.Text("Operation output") : L.Text(logSource) + L.Text(" output");
        foreach (var choice in LogSources.Children.OfType<RadioButton>()) choice.IsChecked = (string)choice.Tag == logSource;
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
        if (LogText.Text == filtered) return;
        bool following = LogText.SelectionLength == 0 && LogText.VerticalOffset >= LogText.ExtentHeight - LogText.ViewportHeight - 2;
        int start = LogText.SelectionStart, length = LogText.SelectionLength;
        double offset = LogText.VerticalOffset;
        LogText.Text = filtered;
        LogText.Select(Math.Min(start, filtered.Length), Math.Min(length, Math.Max(0, filtered.Length - start)));
        if (following) Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (LogText.Text == filtered && LogText.SelectionLength == 0) LogText.ScrollToEnd();
        }));
        else LogText.ScrollToVerticalOffset(offset);
    }

    void UpdateOperationLabel()
    {
        if (!busy) return;
        StatusLabel.Text = L.Text(currentStage) + "…";
        AnnounceStatus();
        OperationDetail.Visibility = Visibility.Visible;
        OperationDetail.Text = L.Text("{0} · elapsed {1}", L.Text(stageDetail), ElapsedText);
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
        if (snapshot is null) { StatusLabel.Text = L.Text("Select and check an installation first."); return; }
        string reviewedDatabase = snapshot.SaveDatabase;
        var dialog = new CreateAccountDialog(this, snapshot.SaveDatabase);
        if (dialog.ShowDialog() == true && dialog.Request is { } request)
        {
            privateNames.Add(request.Name);
            await OperateAsync("Register", account: request with { ExpectedDatabase = reviewedDatabase });
        }
    }
    void OpenDownload(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { StatusLabel.Text = L.Text("Could not open download page: ") + ex.Message; }
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
        if (retry is { } operation) await OperateAsync(operation.Action, operation.Service, mod: retryMod);
        else StatusLabel.Text = L.Text("Select the account, revision or backup action again to review its current options.");
    }

    async void Play_Click(object sender, RoutedEventArgs e) => await OperateAsync("Play");
    async void Start_Click(object sender, RoutedEventArgs e) => await OperateAsync("Start");
    async void Stop_Click(object sender, RoutedEventArgs e) => await OperateAsync("Stop");
    async void CloseGame_Click(object sender, RoutedEventArgs e) => await OperateAsync("CloseGame");
    async void Build_Click(object sender, RoutedEventArgs e) => await OperateAsync("Build");
    async void Refresh_Click(object sender, RoutedEventArgs e) { lastError = null; await RefreshAsync(); if (Pages.SelectedItem == AccountsPage) await LoadAccountsAsync(); }
    async void Service_Click(object sender, RoutedEventArgs e)
    {
        string name = (string)((Button)sender).Tag;
        var state = snapshot?.Services.FirstOrDefault(s => s.Name == name);
        if (name == "Game") await OperateAsync(state?.ProcessId > 0 ? "CloseGame" : "Play");
        else await OperateAsync(state?.IsRunning == true ? "Stop" : "Start", name);
    }
    async void Restart_Click(object sender, RoutedEventArgs e) => await OperateAsync("Restart", (string)((Button)sender).Tag);
    async void Browse_Click(object sender, RoutedEventArgs e)
    {
        locationRevision++; // Opening manual selection takes priority over an in-flight automatic result.
        var dialog = new OpenFolderDialog { Title = L.Text("Choose the LocalExile folder containing GameServer.slnx") };
        if (dialog.ShowDialog(this) == true) await SelectRepositoryAsync(dialog.FolderName);
    }
    void ChangeLocation_Click(object sender, RoutedEventArgs e) { Pages.SelectedItem = SettingsPage; Browse_Click(sender, e); }
    void RepositoryPath_Changed(object sender, TextChangedEventArgs e) => locationRevision++;
    async void FindRepository_Click(object sender, RoutedEventArgs e)
    {
        Pages.SelectedItem = SettingsPage;
        await FindRepositoriesAsync();
    }
    async Task FindRepositoriesAsync()
    {
        if (discovering || closing) return;
        discovering = true;
        int revision = locationRevision;
        FindRepositoryButton.IsEnabled = false;
        DiscoveryStatus.Text = L.Text("Checking upstream HEAD and searching common local folders… You can still Browse to choose a location.");
        try
        {
            var roots = RepositoryDiscovery.DefaultRoots(AppContext.BaseDirectory, Environment.CurrentDirectory, settings.RepositoryPath);
            var result = await RepositoryLocator.FindAsync(roots, discoveryCancellation.Token);
            if (closing) return;
            DiscoveredRepositories.ItemsSource = result.Repositories;
            string upstream = result.Upstream is { } tip
                ? L.Text("Upstream {0}: {1} (checked {2:t}).", tip.Branch, tip.Commit, tip.CheckedAt.ToLocalTime())
                : result.UpstreamError ?? L.Text("Upstream HEAD unavailable.");
            UpstreamIdentity.Value = result.Upstream is not null ? upstream : "";
            UpstreamIdentity.Visibility = result.Upstream is not null ? Visibility.Visible : Visibility.Collapsed;
            int matches = result.Repositories.Count(r => r.MatchesUpstream);
            string choices = matches > 1 ? L.Text("{0} checkouts match upstream HEAD. Choose one below, or Browse elsewhere.", matches)
                : matches == 1 ? L.Text("One checkout matches upstream HEAD. You can use it below or Browse elsewhere.")
                : result.Repositories.Length > 0 ? L.Text("No matching upstream HEAD found. Other LocalExile checkouts are listed below; you can use any of them or Browse elsewhere.")
                : L.Text("No LocalExile checkout found in the searched locations. Browse to your checkout instead.");
            DiscoveryStatus.Text = (result.Upstream is null ? upstream + "\n" : "") + choices +
                (result.Limited ? L.Text("\nSearch limits reached; other checkouts may exist. Browse to any location.") : "");
            // Only a fresh, untouched choice can be adopted automatically. Never replace a saved or manual selection.
            if (result.SingleMatch is { } match && string.IsNullOrWhiteSpace(settings.RepositoryPath) &&
                string.IsNullOrWhiteSpace(RepositoryPathBox.Text) && locationRevision == revision && lastError is null && !busy)
            {
                if (await SelectRepositoryAsync(match.Root) && settings.RepositoryPath == match.Root) DiscoveryStatus.Text = L.Text("Found and using: ") + match.Root + L.Text("\nChange location above to choose somewhere else.") +
                    (match.Head.HasTrackedChanges == true ? L.Text("\nThis checkout has modified tracked files.") : "");
            }
        }
        catch (OperationCanceledException) when (discoveryCancellation.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        { if (!closing) DiscoveryStatus.Text = L.Text("Automatic lookup could not finish. Browse to your checkout instead. ") + ex.Message; }
        finally
        {
            discovering = false;
            if (!closing) FindRepositoryButton.IsEnabled = true;
        }
    }
    async void UseDiscoveredRepository_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not DiscoveredRepository repository) return;
        await SelectRepositoryAsync(repository.Root);
    }
    async Task<bool> SelectRepositoryAsync(string path)
    {
        if (busy) { StatusLabel.Text = L.Text("Wait for the current operation before changing checkout."); return false; }
        busy = true;
        await gate.WaitAsync();
        try
        {
            if (closing) return false;
            path = Path.GetFullPath(path.Trim());
            if (!RepositoryDiscovery.IsLocalExile(path)) throw new IOException("Choose the LocalExile folder containing GameServer.slnx, its Core/Gateway projects and scripts/dev/live.ps1.");
            var selected = settings with { RepositoryPath = path, UseLocalProfile = UseProfileBox.IsChecked == true };
            SettingsStore.Save(selected);
            settings = selected;
            UpdateWorkspaceHeading(path);
            RepositoryPathBox.Text = path;
            samples.Clear();
            snapshot = null;
            sourceChoices = [];
            SourcesList.ItemsSource = null;
            BackupsList.ItemsSource = null;
            SourcesStatus.Text = L.Text("Load the list for the selected checkout.");
            serviceRows.Clear();
            requirementRows.Clear();
            modEntries = [];
            ApplyModFilter();
            CheckModsButton.IsEnabled = BuildAllModsButton.IsEnabled = OpenModsButton.IsEnabled = false;
            PlayButton.IsEnabled = false;
            SidebarBranch.Text = L.Text("Checking checkout…");
            SourceIdentity.Value = BuildIdentity.Value = EnvironmentIdentity.Text = "";
            ReadinessLabel.Text = L.Text("Checking the selected installation…");
            lastError = null;
            OperationDetail.Visibility = FailureActions.Visibility = Visibility.Collapsed;
            retry = null;
            DiscoveryStatus.Text = L.Text("Using: ") + path + L.Text("\nChange location above to choose somewhere else.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { StatusLabel.Text = L.Text("Could not select checkout: ") + ex.Message; return false; }
        finally { busy = false; gate.Release(); }
        await RefreshAsync();
        return true;
    }
    async void Save_Click(object sender, RoutedEventArgs e)
    {
        await SelectRepositoryAsync(RepositoryPathBox.Text);
    }
    void LogSource_Click(object sender, RoutedEventArgs e) { logSource = (string)((RadioButton)sender).Tag; RefreshLog(); }
    void OpenLogs_Click(object sender, RoutedEventArgs e) => OpenFolder(Path.Combine(settings.RepositoryPath, "logs"));
    void OpenCheckout_Click(object sender, RoutedEventArgs e) => OpenFolder(settings.RepositoryPath);
    void OpenData_Click(object sender, RoutedEventArgs e) => OpenFolder(SettingsStore.DataDirectory);
    void OpenGuide_Click(object sender, RoutedEventArgs e)
    {
        string path = Path.Combine(settings.RepositoryPath, "Agents", "GettingStarted.md");
        if (File.Exists(path)) Process.Start(new ProcessStartInfo("notepad.exe") { ArgumentList = { path }, UseShellExecute = false });
        else StatusLabel.Text = L.Text("Select a valid checkout first.");
    }
    void OpenFolder(string path)
    {
        try
        {
            if (!Directory.Exists(path)) { StatusLabel.Text = L.Text("Folder does not exist yet: ") + path; return; }
            Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { path }, UseShellExecute = false });
        }
        catch (Exception ex) { StatusLabel.Text = L.Text("Could not open folder: ") + ex.Message; }
    }
    void OnClosing(object? sender, CancelEventArgs e)
    {
        if (busy) { e.Cancel = true; StatusLabel.Text = L.Text("Wait for the current build or runtime operation to finish before closing."); return; }
        closing = true;
        discoveryCancellation.Cancel();
        timer.Stop();
        operationTimer.Stop();
    }
}

public sealed record ServiceRow(string Name, string Status, string PortText, string Metrics, string Detail,
    string Action, bool CanAct, bool CanRestart, string StateColor, string Cpu, string Memory, string Uptime)
{
    public string DisplayName => L.Text(Name);
    public string DisplayStatus => L.Text(Status);
    public string DisplayDetail => L.Describe(Detail);
    public string DisplayAction => L.Text(Action);
    public string DisplayPortText => L.Text(PortText);
    public string DisplayUptime => L.Text(Uptime);
    public string RestartName => L.Text("Restart {0}", DisplayName);
}
