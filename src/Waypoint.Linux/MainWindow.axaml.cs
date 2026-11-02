using System.ComponentModel;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Material.Icons;

namespace Waypoint;

/// <summary>A section of the sidebar and the view it shows.</summary>
sealed record Section(string Name, MaterialIconKind Icon, Control View)
{
    public string DisplayName => L.Text(Name);
}

/// <summary>A service's lamp in the status strip. An optional one shows only while it runs: stopped is its normal state.</summary>
sealed class StatusLight(Service service, string name, bool optional = false) : INotifyPropertyChanged
{
    public Service Service { get; } = service;
    public string Name => L.Text(name);
    public IBrush Lamp { get; private set; } = Brushes.Gray;
    public string Tip { get; private set; } = "";
    public bool Shown { get; private set; } = !optional;

    public void Update(ServiceSample? sample)
    {
        (string lamp, string state) = sample switch
        {
            { Running: true, Listening: true } => ("Shell.Good", "Running"),
            { Running: true } when Service.Ports.Length == 0 => ("Shell.Good", "Running"),
            { Running: true } => ("Shell.Warning", "Starting"),
            { Listening: true } => ("Shell.Error", "Port held by another"),
            _ => ("Shell.Idle", "Stopped"),
        };
        Lamp = App.Brush(lamp);
        Tip = $"{Name}: {L.Text(state)}";
        Shown = !optional || sample is { Running: true };
        PropertyChanged?.Invoke(this, new(null));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public partial class MainWindow : Window
{
    readonly Shared _shared = new();
    readonly Section[] _sections;
    readonly StatusLight[] _lights =
    [
        new(Service.Patch, "Patch"), new(Service.Gateway, "Gateway"), new(Service.Auth, "Auth"), new(Service.Client, "Game"),
        new(Service.DevHost, "Dev host", optional: true),
    ];
    readonly ControlTab Control = new();
    readonly PlayTab PlayView = new();
    readonly InstallTab InstallView = new();
    readonly SetupTab Setup = new();
    readonly VersionsTab Versions = new();
    readonly ReleasesTab ReleasesView = new();
    readonly ModsTab Mods = new();
    readonly BackupsTab Backups = new();
    readonly AccountsTab Accounts = new();
    readonly PerformanceTab Performance = new();
    readonly TimingsTab Timings = new();
    readonly LogsTab LogsView = new();
    readonly ConsoleTab ConsoleView = new();
    readonly ToolsTab ToolsView = new();
    readonly SettingsTab SettingsView = new();
    bool _closing, _deciding;

    public MainWindow()
    {
        InitializeComponent();
        VersionText.Text = $"v{Updates.Current}";
        SimpleMode.Content = L.Text("Simple");
        AdvancedMode.Content = L.Text("Advanced");
        Lights.ItemsSource = _lights;
        Control.Attach(_shared);
        PlayView.Attach(_shared, Control);
        InstallView.Attach(_shared);
        ReleasesView.Attach(_shared);
        Setup.Attach(_shared);
        Versions.Attach(_shared);
        Mods.Attach(_shared);
        Backups.Attach(_shared);
        Accounts.Attach(_shared);
        Performance.Attach(_shared);
        Timings.Attach(_shared);
        LogsView.Attach(_shared);
        ConsoleView.Attach(_shared);
        ToolsView.Attach(_shared);
        SettingsView.Attach(_shared);
        _sections =
        [
            new("Play", MaterialIconKind.Play, PlayView),
            new("Play", MaterialIconKind.Play, InstallView),
            new("Control", MaterialIconKind.ViewDashboardOutline, Control),
            new("Setup", MaterialIconKind.ClipboardCheckOutline, Setup),
            new("Versions", MaterialIconKind.SourceBranch, Versions),
            new("Versions", MaterialIconKind.SourceBranch, ReleasesView),
            new("Mods", MaterialIconKind.PuzzleOutline, Mods),
            new("Backups", MaterialIconKind.BackupRestore, Backups),
            new("Accounts", MaterialIconKind.AccountMultipleOutline, Accounts),
            new("Performance", MaterialIconKind.ChartLine, Performance),
            new("Server timings", MaterialIconKind.TimerOutline, Timings),
            new("Logs", MaterialIconKind.TextBoxOutline, LogsView),
            new("Console", MaterialIconKind.Console, ConsoleView),
            new("Tools", MaterialIconKind.Toolbox, ToolsView),
            new("Settings", MaterialIconKind.CogOutline, SettingsView),
        ];
        foreach (var section in _sections) { section.View.IsVisible = false; SectionHost.Children.Add(section.View); }
        _shared.Monitor.Sampled += samples => Dispatcher.UIThread.Post(() => ShowStatus(samples));
        _shared.LiveRunning += _ => Dispatcher.UIThread.Post(() => ShowStatus(_shared.Monitor.Latest));
        _shared.Workspace.Failed += (stage, message, log) => Dispatcher.UIThread.Post(() => ShowFailure(stage, message, log));

        Versions.Switched += CheckoutMoved;
        PlayView.CheckoutChanged += CheckoutMoved;
        PlayView.SetupRequested += Setup.OpenSetupGuide;
        InstallView.SetupRequested += Setup.OpenSetupGuide;
        PlayView.DetailsRequested += () => ShowSection(LogsView);
        InstallView.DetailsRequested += () => ShowSection(LogsView);
        PlayView.ChangeFolderRequested += Setup.ChangeFolder;
        InstallView.ChangeFolderRequested += Setup.ChangeFolder;
        ReleasesView.ChangeFolderRequested += Setup.ChangeFolder;
        Control.ChangeFolderRequested += Setup.ChangeFolder;
        PlayView.NewCopyRequested += Setup.SetUpNewCopy;
        InstallView.NewCopyRequested += Setup.SetUpNewCopy;
        ReleasesView.NewCopyRequested += Setup.SetUpNewCopy;
        Control.NewCopyRequested += Setup.SetUpNewCopy;
        ToolsView.NewCopyRequested += Setup.SetUpNewCopy;
        ReleasesView.CheckRequested += () => _ = CheckLocalExile(manual: true);
        SettingsView.CheckUpdatesRequested += () => _ = CheckLocalExile(manual: true);
        SettingsView.DeveloperModeChanged += ShowMode;
        Control.InstallPlayRequested += () => { ShowSection(InstallView); _ = InstallView.PlayAsync(); };
        Control.InstallServersRequested += () => { ShowSection(InstallView); _ = InstallView.StartServersAsync(); };
        Versions.PlayRequested += () => { ShowSection(Control); _ = Control.PlayAsync(); };
        Control.ShowLogRequested += source => { LogsView.Show(source); ShowSection(LogsView); };
        Control.ShowSetupRequested += () => { if (Repo.IsInstall) Setup.OpenSetupGuide(); else ShowSection(Setup); };
        Mods.ShowGatewayLog += () => { LogsView.Show("Gateway"); ShowSection(LogsView); };
        Setup.CreateAccountRequested += CreateFirstAccount;
        Setup.SetupFinished += () => { _ = Control.RefreshSetup(); InstallView.InstallChanged(); };
        ShowMode();
        Opened += (_, _) =>
        {
            _ = _shared.Workspace.RefreshAsync();
            if (Repo.IsInstall && _shared.Settings.KeepUpToDate) _ = CheckLocalExile();
            if (_shared.Settings.CreateAccountOnStart)
            {
                _shared.Settings.CreateAccountOnStart = false;
                _shared.Settings.Save();
                CreateFirstAccount();
            }
        };
        Closing += WindowClosing;
        Closed += (_, _) => _shared.Dispose();
    }

    /// <summary>Simple mode's sections and nothing about the servers, or all of them. The section on show stays when the new
    /// list has it. Play and Versions each have a checkout's view and a release install's.</summary>
    void ShowMode()
    {
        bool advanced = _shared.Settings.Advanced;
        Control[] others = Repo.IsInstall ? [PlayView, Versions] : [InstallView, ReleasesView];
        var sections = _sections.Where(s => !others.Contains(s.View) && (advanced
            ? s.View is not (PlayTab or InstallTab or SetupTab) || s.View == Setup
            : s.View is PlayTab or InstallTab or ModsTab or BackupsTab or AccountsTab or LogsTab or ConsoleTab or ToolsTab or SettingsTab)).ToArray();
        ModeSwitch.IsVisible = _shared.Settings.Developer;
        var shown = Sections.SelectedItem as Section;
        Sections.ItemsSource = sections;
        Sections.SelectedItem = sections.Contains(shown) ? shown : sections[0];
        StatusStrip.IsVisible = advanced;
        BranchChip.IsVisible = advanced && !Repo.IsInstall;
        BranchName.Text = Repo.Branch() ?? L.Text("no git checkout");
        SimpleMode.IsChecked = !advanced;
        AdvancedMode.IsChecked = advanced;
        SettingsView.ShowMode();
    }

    void ModeChosen(object? sender, RoutedEventArgs e)
    {
        bool advanced = AdvancedMode.IsChecked == true;
        if (advanced == _shared.Settings.Advanced) return;
        _shared.Settings.AdvancedMode = advanced;
        _shared.Settings.Save();
        ShowMode();
    }

    void CheckoutMoved()
    {
        BranchName.Text = Repo.Branch() ?? L.Text("no git checkout");
        _ = Control.RefreshSetup();
        _ = _shared.Workspace.RefreshAsync();
    }

    void SectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (Sections.SelectedItem is Section shown) Show(shown);
    }

    void Show(Section shown)
    {
        foreach (var s in _sections) s.View.IsVisible = s == shown;
        SectionTitle.Text = shown.DisplayName;
    }

    /// <summary>A link can lead to a section Simple mode's list does not have: it then shows without being selected.</summary>
    void ShowSection(Control view)
    {
        var section = _sections.First(s => s.View == view);
        if (Sections.ItemsSource is Section[] listed && listed.Contains(section)) Sections.SelectedItem = section;
        else
        {
            Sections.SelectedItem = null;
            Show(section);
        }
    }

    /// <summary>Ctrl+1 to Ctrl+9 choose a section and F5 inspects the checkout again, as on Windows.</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyModifiers == KeyModifiers.Control && e.Key is >= Key.D1 and <= Key.D9 && e.Key - Key.D1 < Sections.ItemCount) Sections.SelectedIndex = e.Key - Key.D1;
        else if (e.KeyModifiers == KeyModifiers.None && e.Key == Key.F5) _ = _shared.Workspace.RefreshAsync();
    }

    void ShowFailure(string stage, string message, string? log)
    {
        int chosen = Dialog.Show(this, DialogKind.Error, L.Text(stage) + L.Text(" failed: "), L.Describe(message), null,
            log is not null && File.Exists(log) ? [L.Text("Open log"), L.Text("Close")] : [L.Text("Close")]);
        if (chosen == 0 && log is not null && File.Exists(log)) Process.Start(new ProcessStartInfo("xdg-open", [log]) { UseShellExecute = false })?.Dispose();
    }

    void ShowStatus(IReadOnlyList<ServiceSample> samples)
    {
        foreach (var light in _lights) light.Update(samples.FirstOrDefault(s => s.Service == light.Service));
        StatusText.Text = _shared.IsLiveRunning ? L.Text("{0} is still running", _shared.BusyWith) : "";
    }

    /// <summary>The end of the setup guide: the Accounts section's Create dialog.</summary>
    async void CreateFirstAccount()
    {
        ShowSection(Accounts);
        await Accounts.CreateAccountAsync();
    }

    /// <summary>A release's card for a newer LocalExile, as on Windows; a manual check says what it found.</summary>
    async Task CheckLocalExile(bool manual = false)
    {
        if (!Repo.IsInstall) return;
        try
        {
            using var client = Updates.Client();
            var latest = await LocalExileReleases.LatestAsync(client);
            var installed = LocalExileReleases.Installed(Environment.CurrentDirectory);
            bool newer = latest is not null && (installed is null || latest.Version.IsNewerThan(installed));
            if (newer) _shared.Logs.Write($"LocalExile {latest!.Version} is out", Severity.Info);
            if (manual) Dialog.Tell(this, DialogKind.Info, "LocalExile", newer ? L.Text("LocalExile {0} is out; you have {1}. Your characters, keys and game client are kept.", latest!.Version,
                installed?.ToString() ?? L.Text("an unknown version")) : L.Text("You have the latest version."));
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            _shared.Logs.Write($"LocalExile update check failed: {ex.Message}", Severity.Warning);
            if (manual) Dialog.Tell(this, DialogKind.Warning, L.Text("Update check failed"), ex.Message);
        }
    }

    /// <summary>As on Windows, without a tray to hide in: a run in progress keeps the window open, and running services are
    /// stopped or left as the settings say, after asking when they say to ask.</summary>
    void WindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closing) return;
        if (_deciding) { e.Cancel = true; return; }
        var settings = _shared.Settings;
        bool anything = _shared.Monitor.Latest.Any(s => s.Running && s.Service.Kind is not ServiceKind.Recorder);
        if (_shared.IsLiveRunning)
        {
            e.Cancel = true;
            Dialog.Tell(this, DialogKind.Info, L.Text("{0} is still running", _shared.BusyWith), L.Text("Close Waypoint when it is done."));
            return;
        }
        bool stop = settings.StopServicesOnExit;
        if (anything && settings.AlwaysAskOnClose)
        {
            e.Cancel = true;
            _deciding = true;
            Dispatcher.UIThread.Post(AskClose);
            return;
        }
        if (stop && anything)
        {
            e.Cancel = true;
            Dispatcher.UIThread.Post(StopAndClose);
            return;
        }
        _closing = true;
    }

    void AskClose()
    {
        _deciding = false;
        int chosen = Dialog.Show(this, DialogKind.Question, L.Text("When Waypoint exits"), L.Text("Close Waypoint completely."), null,
            [L.Text("Stop services and exit"), L.Text("Exit Waypoint"), L.Text("Cancel")]);
        if (chosen == 0) StopAndClose();
        else if (chosen == 1) { _closing = true; Close(); }
    }

    async void StopAndClose()
    {
        IsEnabled = false;
        Title = L.Text("Waypoint: stopping …");
        await _shared.StopAllAsync(_ => { });
        _closing = true;
        Close();
    }
}
