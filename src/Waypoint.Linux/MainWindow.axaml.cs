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
    readonly GameSystemsTab ModulesView = new();
    readonly GameSystemsTab LeaguesView = new() { LeagueView = true };
    readonly BackupsTab Backups = new();
    readonly AccountsTab Accounts = new();
    readonly PerformanceTab Performance = new();
    readonly TimingsTab Timings = new();
    readonly LogsTab LogsView = new();
    readonly ConsoleTab ConsoleView = new();
    readonly ToolsTab ToolsView = new();
    readonly SettingsTab SettingsView = new();
    bool _closing, _deciding, _explicitExit;

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
        ModulesView.Attach(_shared);
        ModulesView.OpenLeaguesRequested += () => ShowSection(LeaguesView);
        LeaguesView.Attach(_shared);
        LeaguesView.OpenModsRequested += () => ShowSection(Mods);
        Backups.Attach(_shared);
        Accounts.Attach(_shared);
        Performance.Attach(_shared);
        Timings.Attach(_shared);
        LogsView.Attach(_shared);
        ConsoleView.Attach(_shared);
        ToolsView.Attach(_shared);
        SettingsView.Attach(_shared);
        Folders.Attach(_shared);
        _sections =
        [
            new("Play", MaterialIconKind.Play, PlayView),
            new("Play", MaterialIconKind.Play, InstallView),
            new("Control", MaterialIconKind.ViewDashboardOutline, Control),
            new("Setup", MaterialIconKind.ClipboardCheckOutline, Setup),
            new("Versions", MaterialIconKind.SourceBranch, Versions),
            new("Versions", MaterialIconKind.SourceBranch, ReleasesView),
            new("Mods", MaterialIconKind.PuzzleOutline, Mods),
            new("Modules", MaterialIconKind.ViewGridOutline, ModulesView),
            new("Leagues", MaterialIconKind.FlagOutline, LeaguesView),
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
        Control.ClientGuideRequested += Setup.OpenSetupGuide;
        InstallView.OlderReleaseRequested += Setup.SetUpRelease;
        PlayView.DetailsRequested += () => ShowSection(LogsView);
        InstallView.DetailsRequested += () => ShowSection(LogsView);
        // The release in this folder is another version now.
        InstallView.Updated += Folders.ShowCurrent;
        ReleasesView.ChangeFolderRequested += Setup.ChangeFolder;
        Control.ChangeFolderRequested += Setup.ChangeFolder;
        Folders.NewCopyRequested += Setup.SetUpNewCopy;
        // The choice page's "Use Advanced mode": this copy is shown in Advanced mode from now on. The choice made on a
        // release (Developer Mode, Simple or Advanced) is not touched.
        PlayView.AdvancedRequested += () =>
        {
            _shared.Settings.SourceChoiceMade = true;
            _shared.Settings.Save();
            _shared.Logs.Write("choice on a source copy: Advanced mode", Severity.Info);
            ShowMode();
        };
        // Simple mode's Tools page leads to the two sections its list leaves out.
        ToolsView.ConsoleRequested += () => ShowSection(ConsoleView);
        ToolsView.LogsRequested += () => ShowSection(LogsView);
        // The choice page's "Switch to the release…".
        PlayView.ReleaseRequested += () => GoTo(release: true);
        ReleasesView.NewCopyRequested += Setup.SetUpNewCopy;
        Control.NewCopyRequested += Setup.SetUpNewCopy;
        // The crafter needs a source copy: the Latest one Waypoint knows, or a new one through the guide (its tested version).
        ToolsView.NewCopyRequested += () =>
        {
            if (Editions.Folder(_shared.Settings, release: false) is null) Setup.SetUpNewCopy();
            else GoTo(release: false);
        };
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
            // A chosen folder or tool that is not there now: said once, and Waypoint goes on with the default.
            if (Settings.PathProblems.Count > 0)
                Dialog.Tell(this, DialogKind.Warning, L.Text("Some of your folders or tools can't be used"),
                    string.Join("\n\n", Settings.PathProblems) + "\n\n" + L.Text("Waypoint works with the defaults until then. Change or clear them in Settings → Folders and tools."));
            if (Repo.IsInstall && _shared.Settings.KeepUpToDate) _ = CheckLocalExile();
            if (_shared.Settings.CreateAccountOnStart)
            {
                _shared.Settings.CreateAccountOnStart = false;
                _shared.Settings.Save();
                CreateFirstAccount();
            }
        };
        // Waypoint stops what runs before it leaves this folder (Shared.Leaving): until that has ended the window is not
        // usable, as when it stops what runs before it closes. A change of folder that does not go on gives it back.
        string? title = null;
        _shared.LeavingChanged += () =>
        {
            IsEnabled = !_shared.Leaving;
            if (_shared.Leaving) { title = Title; Title = L.Text("Waypoint: stopping …"); }
            else if (title is not null) Title = title;
        };
        Closing += WindowClosing;
        Closed += (_, _) => _shared.Dispose();
    }

    /// <summary>Simple mode's sections and nothing about the servers, or all of them. The section on show stays when the new
    /// list has it. Play and Versions each have a checkout's view and a release install's.</summary>
    void ShowMode()
    {
        // The mode follows the folder: a release shows its user's choice, a source copy Advanced mode.
        bool advanced = Editions.Advanced(_shared.Settings);
        Control[] others = Repo.IsInstall ? [PlayView, Versions] : [InstallView, ReleasesView];
        var sections = _sections.Where(s => !others.Contains(s.View) && (advanced
            ? s.View is not (PlayTab or InstallTab or SetupTab) || s.View == Setup
            // Simple mode's one list, Modules and Leagues among it: a release takes their changes too (the adapter's
            // Get-ContentEnvironment says which). Console and Logs open from Tools: ShowSection shows a section that is
            // not listed.
            : s.View is GameSystemsTab || s.View is PlayTab or InstallTab or ModsTab or BackupsTab or AccountsTab or ToolsTab or SettingsTab)).ToArray();
        // Developer Mode (Settings) unlocks the switch, on a release: a source copy has no Simple mode to switch to.
        ModeSwitch.IsVisible = Repo.IsInstall && _shared.Settings.Developer;
        var shown = Sections.SelectedItem as Section;
        Sections.ItemsSource = sections;
        Sections.SelectedItem = sections.Contains(shown) ? shown : sections[0];
        StatusStrip.IsVisible = advanced;
        BranchChip.IsVisible = advanced && !Repo.IsInstall;
        BranchName.Text = Repo.Branch() ?? L.Text("no git checkout");
        SimpleMode.IsChecked = !advanced;
        AdvancedMode.IsChecked = advanced;
        ToolsView.ShowMode(simple: !advanced);
        SettingsView.ShowMode();
    }

    /// <summary>To the release Waypoint remembers, or to its source copy ("Latest"), with the one question of that switch
    /// (<see cref="Editions.PrepareAsync"/>); the setup guide when there is none yet. The sidebar's list is the everyday
    /// way between folders: this is for the two places that name a side, not a folder.</summary>
    async void GoTo(bool release)
    {
        var (step, folder) = await Editions.PrepareAsync(_shared, release, (kind, title, message, buttons) => Dialog.Show(this, kind, title, message, null, buttons));
        if (step == EditionStep.SetUpRelease) Setup.SetUpRelease();
        else if (step == EditionStep.SetUpLatest) Setup.SetUpLatest();
        // PrepareAsync has made sure nothing runs.
        else if (step == EditionStep.Switch) await App.SwitchServerAsync(this, _shared, folder, asked: true);
    }

    /// <summary>The sidebar's switch, which only a release shows. ShowMode also sets the two buttons, and on a source copy the
    /// mode shown is not the saved choice: nothing is written there.</summary>
    void ModeChosen(object? sender, RoutedEventArgs e)
    {
        bool advanced = AdvancedMode.IsChecked == true;
        if (!Repo.IsInstall || advanced == _shared.Settings.Advanced) return;
        _shared.Settings.AdvancedMode = advanced;
        _shared.Settings.Save();
        ShowMode();
    }

    void CheckoutMoved()
    {
        BranchName.Text = Repo.Branch() ?? L.Text("no git checkout");
        Folders.ShowCurrent();
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
        bool hasLog = log is not null && File.Exists(log);
        // Enter stays what it was: the log when there is one, else Close.
        int chosen = Dialog.Show(this, DialogKind.Error, L.Text(stage) + L.Text(" failed: "), L.Describe(message), null,
            hasLog ? [L.Text("Open log"), L.Text("Diagnostic report"), L.Text("Close")] : [L.Text("Diagnostic report"), L.Text("Close")],
            hasLog ? 0 : 1);
        if (hasLog && chosen == 0) Process.Start(new ProcessStartInfo("xdg-open", [log!]) { UseShellExecute = false })?.Dispose();
        else if (chosen == (hasLog ? 1 : 0)) DiagnosticPreviewDialog.Show(this, _shared);
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

    /// <summary>A real exit, without the question about what runs: Waypoint starts again, on another folder say. What runs
    /// is left as it is, whatever is set for an exit: whoever asked for the change has answered that already ("Switch
    /// without stopping"), and stopping is not what starting again is for. A run in progress still keeps the window open.</summary>
    internal void ExitNow()
    {
        _explicitExit = true;
        Close();
    }

    /// <summary>As on Windows, without a tray to hide in: a run in progress keeps the window open, and running services are
    /// stopped or left as the settings say, after asking when they say to ask.</summary>
    void WindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closing) return;
        if (_deciding) { e.Cancel = true; return; }
        bool explicitExit = _explicitExit;
        _explicitExit = false;
        // Waypoint is stopping what runs before it leaves this folder, and closes by itself when that has ended. Nothing
        // is asked or said meanwhile: a question open when the wait ends would keep the window from closing.
        if (_shared.Leaving) { e.Cancel = true; return; }
        var settings = _shared.Settings;
        bool anything = _shared.Monitor.Latest.Any(s => s.Running && s.Service.Kind is not ServiceKind.Recorder);
        if (_shared.IsLiveRunning)
        {
            e.Cancel = true;
            Dialog.Tell(this, DialogKind.Info, L.Text("{0} is still running", _shared.BusyWith), L.Text("Close Waypoint when it is done."));
            return;
        }
        bool stop = settings.StopServicesOnExit;
        if (anything && settings.AlwaysAskOnClose && !explicitExit)
        {
            e.Cancel = true;
            _deciding = true;
            Dispatcher.UIThread.Post(AskClose);
            return;
        }
        if (stop && anything && !explicitExit)
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
