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
        new(Service.DevHost, "Dev host", optional: true), new(Service.Recorder, "Walk maps", optional: true),
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
    bool _closing, _deciding, _explicitExit, _leaveRunning;
    /// <summary>The tray icon, while the window is hidden (none when only a picture is made).</summary>
    readonly Tray? _tray = Program.Rendering ? null : new();
    /// <summary>Whether the game ran at the last sample: null until the first one.</summary>
    bool? _clientRunning;
    /// <summary>The window went to the tray because the game started, and comes back when it closes.</summary>
    bool _hiddenForGame;

    public MainWindow()
    {
        InitializeComponent();
        Updates.CleanUp();
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
        _shared.Monitor.Sampled += samples => Dispatcher.UIThread.Post(() =>
        {
            ShowStatus(samples);
            ClientSeen(samples.Any(s => s.Service == Service.Client && s.Running));
        });
        if (_tray is not null)
        {
            _tray.ShowRequested += ShowFromTray;
            _tray.StopAllRequested += async () => await _shared.StopAllAsync(_ => { });
            _tray.ExitRequested += () => { ShowFromTray(); _explicitExit = true; Close(); };
            L.Current.PropertyChanged += (_, _) => _tray.ShowLanguage();
        }
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
        Folders.GameClientRequested += OpenGameClient;
        InstallView.ClientChanged += Folders.ShowCurrent;
        InstallView.ShowRequested += () => ShowSection(InstallView);
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
        ReleasesView.CheckRequested += () => _ = CheckForUpdates(manual: true);
        SettingsView.CheckUpdatesRequested += () => _ = CheckForUpdates(manual: true);
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
            if (_shared.Settings.CheckForUpdates && Updates.IsReleaseBuild) _ = CheckForUpdates(manual: false);
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
        Closed += (_, _) =>
        {
            _tray?.Dispose();
            _shared.Dispose();
        };
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

    /// <summary>The sidebar's "Game client…": the clients of the folder in use. A source copy's move to another client is
    /// Control's, the one that comes before its Play; a release's is its Play page's.</summary>
    void OpenGameClient()
    {
        if (Repo.IsInstall)
        {
            InstallView.OpenClientVersions();
            return;
        }
        var window = new ClientVersionsWindow(this, _shared);
        window.ShowDialog();
        if (window.Changed) Folders.ShowCurrent();
        if (!window.MoveRequested) return;
        ShowSection(Control);
        _ = Control.MoveClientAsync();
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

    Release? _release;

    /// <summary>Looks for a newer Waypoint and shows its card when there is one, as on Windows. At startup quietly (no network,
    /// no release: nothing to say); asked for, it says what it found. Only the published AppImage replaces itself.</summary>
    async Task CheckForUpdates(bool manual)
    {
        try
        {
            bool exile = Repo.IsInstall && manual && await CheckLocalExile(manual: true);
            _release = await Updates.CheckAsync();
            if (_release is not null)
            {
                ShowRelease();
                UpdateNow.IsEnabled = Updates.RunningExe is not null;
                UpdateBar.IsVisible = true;
            }
            else if (manual && !exile)
                Dialog.Tell(this, DialogKind.Info, L.Text("No update"),
                    L.Text("No newer release than {0}.", Updates.IsReleaseBuild ? Updates.Current.ToString() : L.Text("this build from source"))
                    + (Repo.IsInstall ? "\n" + L.Text("LocalExile {0} is the newest release.", LocalExileReleases.Installed(Environment.CurrentDirectory)?.ToString() ?? "") : ""));
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            _shared.Logs.Write($"update check failed: {ex.Message}", Severity.Warning);
            if (manual) Dialog.Tell(this, DialogKind.Warning, L.Text("Update check failed"), ex.Message);
        }
    }

    /// <summary>What the update card says about the release found.</summary>
    void ShowRelease()
    {
        if (_release is null) return;
        UpdateText.Text = L.Text("Version {0} is out; this is {1}. The servers and the game keep running while it updates.", _release.Version,
            Updates.IsReleaseBuild ? Updates.Current.ToString() : L.Text("a build from source"));
        ToolTip.SetTip(UpdateNow, Updates.RunningExe is null ? L.Text("Only the published AppImage updates itself") : null);
    }

    async void UpdateClicked(object? sender, RoutedEventArgs e)
    {
        if (_release is null) return;
        if (!Dialog.Ask(this, DialogKind.Question, L.Text("Update to {0}?", _release.Version),
                L.Text("Waypoint downloads the new version and restarts; the servers and the game keep running."), L.Text("Update and restart"))) return;
        UpdateNow.IsEnabled = false;
        try
        {
            // Linux has no single-instance claim to give up before the new Waypoint starts.
            await Updates.ApplyAsync(_release, new Progress<string>(text => UpdateText.Text = text), () => { });
            // The new Waypoint runs: this one closes without asking about the servers.
            _closing = true;
            Close();
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException or IOException
            or UnauthorizedAccessException or InvalidOperationException)
        {
            UpdateNow.IsEnabled = true;
            UpdateText.Text = L.Text("The update failed: {0}", ex.Message);
            _shared.Logs.Write($"update failed: {ex.Message}", Severity.Error);
        }
    }

    void ReleasePageClicked(object? sender, RoutedEventArgs e)
    {
        if (_release?.Page is { Length: > 0 } page) Ui.Open(page);
    }

    void LaterClicked(object? sender, RoutedEventArgs e) => UpdateBar.IsVisible = false;

    LocalExileRelease? _exileRelease;

    /// <summary>Asks GitHub for LocalExile's latest release, and shows its card when it is newer than this install. True when
    /// it is. Quiet at startup: no network is nothing to say.</summary>
    async Task<bool> CheckLocalExile(bool manual = false)
    {
        if (!Repo.IsInstall) return false;
        try
        {
            using var client = Updates.Client();
            var latest = await LocalExileReleases.LatestAsync(client);
            var installed = LocalExileReleases.Installed(Environment.CurrentDirectory);
            if (latest is null || (installed is not null && !latest.Version.IsNewerThan(installed))) return false;
            _exileRelease = latest;
            ShowExileRelease();
            ExileUpdateBar.IsVisible = true;
            return true;
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            _shared.Logs.Write($"LocalExile update check failed: {ex.Message}", Severity.Warning);
            if (manual) Dialog.Tell(this, DialogKind.Warning, L.Text("Update check failed"), ex.Message);
            return true;
        }
    }

    void ShowExileRelease()
    {
        if (_exileRelease is null) return;
        ExileUpdateText.Text = L.Text("LocalExile {0} is out; you have {1}. Your characters, keys and game client are kept.", _exileRelease.Version,
            LocalExileReleases.Installed(Environment.CurrentDirectory)?.ToString() ?? L.Text("an unknown version"));
    }

    /// <summary>Puts the new release over this install, as its README says, once the game is closed. Nothing is force-stopped
    /// for it: a Gateway needs the chance to save.</summary>
    async void ExileUpdateClicked(object? sender, RoutedEventArgs e)
    {
        if (_exileRelease is not { } release) return;
        if (_shared.IsLiveRunning || _shared.Monitor.Latest.Any(s => s.Running))
        {
            Dialog.Tell(this, DialogKind.Info, L.Text("Close the game first"), L.Text("LocalExile can only be updated while the game and its servers are closed. Close the game, wait for the servers to stop, then press Update now again."));
            return;
        }
        // A release for a newer client than the only one on this PC would leave the player unable to play (ClientUpdate).
        string clientFolder = Path.Combine(Environment.CurrentDirectory, "Client");
        if (ClientUpdate.ClientOf(release) is { } needs && Directory.Exists(clientFolder) && ClientLocator.BuildOf(clientFolder) is { } have
            && ClientLocator.IsOlder(have, needs) && await Task.Run(() => ClientUpdate.FindClient(needs, default)) is null
            && Dialog.Ask(this, DialogKind.Warning, L.Text("Keep this version?"),
                L.Text("LocalExile {0} needs the Path of Exile {1} client, and this PC has only {2}. After this update you could not play until you have a {1} client.", release.Version, needs, have),
                L.Text("Keep this version"), L.Text("Update anyway"))) return;
        if (!Dialog.Ask(this, DialogKind.Question, L.Text("Update LocalExile to {0}?", release.Version),
                L.Text("Waypoint downloads {0:0} MB, checks it, and puts it over this install. Your characters, keys and game client are kept, and your saves are copied first when Automatic backups is on. The next Play may extract game data again.", release.ZipBytes / 1e6),
                L.Text("Update"))) return;
        ExileUpdateNow.IsEnabled = ExileLater.IsEnabled = false;
        string folder = Environment.CurrentDirectory;
        void Say(string text) => Dispatcher.UIThread.Post(() => ExileUpdateText.Text = text);
        try
        {
            bool ran = await _shared.RunExclusiveAsync(L.Text("LocalExile update"), async () =>
            {
                if (_shared.Settings.AutomaticBackups)
                {
                    Say(L.Text("Copying your saves …"));
                    if (LocalExileReleases.BackUpSaves(folder, LocalExileReleases.Installed(folder), DateTime.Now) is { } backup) _shared.Logs.Write($"saves copied to {backup}");
                }
                using var client = Updates.Client();
                bool extracting = false;
                await LocalExileReleases.InstallAsync(client, release, folder,
                    (done, total) => Say(extracting ? L.Text("Installing: {0:0} of {1:0} MB", done / 1e6, total / 1e6) : L.Text("Downloading: {0:0} of {1:0} MB", done / 1e6, total / 1e6)),
                    () => extracting = true, CancellationToken.None);
            });
            if (!ran)
            {
                Say(L.Text("Something else is using LocalExile. Try again when it is done."));
                return;
            }
            _shared.Logs.Write($"LocalExile updated to {release.Version}", Severity.Info);
            _exileRelease = null;
            ExileUpdateBar.IsVisible = false;
            InstallView.InstallChanged(() => L.Text("LocalExile was updated."));
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException or IOException
            or UnauthorizedAccessException or InvalidOperationException or InvalidDataException)
        {
            Say(L.Text("The update failed: {0}", ex.Message));
            _shared.Logs.Write($"LocalExile update failed: {ex.Message}", Severity.Error);
        }
        finally { ExileUpdateNow.IsEnabled = ExileLater.IsEnabled = true; }
    }

    void ExilePageClicked(object? sender, RoutedEventArgs e)
    {
        if (_exileRelease?.Page is { Length: > 0 } page) Ui.Open(page);
    }

    void ExileLaterClicked(object? sender, RoutedEventArgs e) => ExileUpdateBar.IsVisible = false;

    /// <summary>A real exit, without the question about what runs: Waypoint starts again, on another folder say. What runs
    /// is left as it is, whatever is set for an exit: whoever asked for the change has answered that already ("Switch
    /// without stopping"), and stopping is not what starting again is for. A run in progress still keeps the window open.</summary>
    internal void ExitNow()
    {
        _explicitExit = true;
        _leaveRunning = true;
        Close();
    }

    /// <summary>As on Windows: a run in progress keeps the window open, closing hides to the tray when chosen and a tray is
    /// available, and running services are stopped or left as the settings say, after asking when they say to ask.</summary>
    void WindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closing) return;
        if (_deciding) { e.Cancel = true; return; }
        bool explicitExit = _explicitExit;
        bool leaveRunning = _leaveRunning;
        _explicitExit = _leaveRunning = false;
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
        // Kept in the tray, as the close choice says, where the desktop shows one: Waypoint and what runs go on.
        if (!explicitExit && settings.CloseToTray && Tray.Available && !settings.AlwaysAskOnClose)
        {
            e.Cancel = true;
            Dispatcher.UIThread.Post(HideToTray);
            return;
        }
        bool stop = settings.StopServicesOnExit;
        if ((anything || settings.CloseToTray && Tray.Available) && settings.AlwaysAskOnClose && !explicitExit)
        {
            e.Cancel = true;
            _deciding = true;
            Dispatcher.UIThread.Post(AskClose);
            return;
        }
        if (stop && anything && !leaveRunning)
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
        // Hiding to the tray is one of the answers only where the desktop shows a tray.
        bool tray = Tray.Available;
        string[] answers = tray
            ? [L.Text("Hide to tray"), L.Text("Stop services and exit"), L.Text("Exit Waypoint"), L.Text("Cancel")]
            : [L.Text("Stop services and exit"), L.Text("Exit Waypoint"), L.Text("Cancel")];
        int chosen = Dialog.Show(this, DialogKind.Question, L.Text("When Waypoint exits"), L.Text("Close Waypoint completely."), null, answers);
        if (tray) chosen--;
        if (chosen == -1 && tray) HideToTray();
        else if (chosen == 0) StopAndClose();
        else if (chosen == 1) { _closing = true; Close(); }
    }

    /// <summary>A client that starts sends the window to the tray; when no client is left it comes back. A client that
    /// Play closes and starts again (one live.ps1 run) keeps it in the tray, and a window opened while the game already
    /// runs stays open. Only where the desktop shows a tray, from which the window comes back.</summary>
    void ClientSeen(bool running)
    {
        bool started = running && _clientRunning == false;
        _clientRunning = running;
        if (started && _shared.Settings.TrayWhileGameRuns && Tray.Available && _tray is not null && IsVisible && !_closing)
        {
            _hiddenForGame = true;
            Hide();
            _tray.Visible = true;
            Tray.Notify(L.Text("The game is running: Waypoint waits here and comes back when the game closes."));
        }
        else if (!running && _hiddenForGame && !_shared.IsLiveRunning)
        {
            ShowFromTray();
        }
    }

    void ShowFromTray()
    {
        _hiddenForGame = false;
        if (_tray is not null) _tray.Visible = false;
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    void HideToTray()
    {
        if (_tray is null || !Tray.Available) return;
        _tray.Visible = true;
        Hide();
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
