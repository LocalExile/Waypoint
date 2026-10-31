using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using MaterialDesignThemes.Wpf;

namespace Waypoint;

/// <summary>A section of the sidebar and the view it shows.</summary>
sealed record Section(string Name, PackIconKind Icon, FrameworkElement View)
{
    public string DisplayName => L.Text(Name);
}

/// <summary>A service's lamp in the status strip, updated in place. A running service glows; a stopped one stays quiet, and
/// red is kept for a real problem. The mark inside says the same without the colour. An optional service, one the game does
/// not need, shows only while it runs: stopped is its normal state, and a lamp for that would say nothing.</summary>
sealed class StatusLight(Service service, string name, bool optional = false) : INotifyPropertyChanged
{
    static readonly Geometry Running = Parse("M 1,4 L 3,6 L 7,2"), Stopped = Parse("M 1,4 L 7,4"),
        Starting = Parse("M 4,1 L 4,4 L 6,5"), Problem = Parse("M 4,1 L 4,5 M 4,7 L 4,7.2");

    public Service Service { get; } = service;
    public string Name => L.Text(name);
    public Brush Lamp { get; private set; } = Brushes.Gray;
    public Geometry Mark { get; private set; } = Stopped;
    public DropShadowEffect? Glow { get; private set; }
    public string Tip { get; private set; } = "";
    public Visibility Shown { get; private set; } = optional ? Visibility.Collapsed : Visibility.Visible;

    public void Update(ServiceSample? sample)
    {
        (string lamp, string state, Mark) = sample switch
        {
            { Running: true, Listening: true } => ("Shell.Good", "Running", Running),
            { Running: true } when Service.Ports.Length == 0 => ("Shell.Good", "Running", Running),
            { Running: true } => ("Shell.Warning", "Starting", Starting),
            { Listening: true } => ("Shell.Error", "Port held by another", Problem),
            _ => ("Shell.Idle", "Stopped", Stopped),
        };
        Lamp = Theme.Brush(lamp);
        bool glows = lamp != "Shell.Idle" && !Theme.Current.IncreaseContrast && !SystemParameters.HighContrast;
        Color color = Lamp is SolidColorBrush solid ? solid.Color : Colors.Gray;
        // Kept while it is right: this runs every second, and a new effect makes WPF render the lamp again.
        if (!glows) Glow = null;
        else if (Glow?.Color != color)
        {
            Glow = new DropShadowEffect { Color = color, BlurRadius = 10, ShadowDepth = 0, Opacity = .55, RenderingBias = RenderingBias.Performance };
            Glow.Freeze();
        }
        Tip = $"{Name}: {L.Text(state)}";
        Shown = !optional || sample is { Running: true } ? Visibility.Visible : Visibility.Collapsed;
        PropertyChanged?.Invoke(this, new(null));
    }

    static Geometry Parse(string path)
    {
        var geometry = Geometry.Parse(path);
        geometry.Freeze();
        return geometry;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public partial class MainWindow : Window
{
    readonly Shared _shared = new();
    readonly Tray _tray = new();
    readonly Section[] _sections;
    readonly StatusLight[] _lights =
    [
        new(Service.Patch, "Patch"), new(Service.Gateway, "Gateway"), new(Service.Auth, "Auth"), new(Service.Client, "Game"),
        new(Service.DevHost, "Dev host", optional: true), new(Service.Recorder, "Walk maps", optional: true),
    ];
    bool _closing, _explicitExit, _deciding;
    bool? _stopOverride;
    /// <summary>Whether the client ran at the last sample; null before the first.</summary>
    bool? _clientRunning;
    /// <summary>The window went to the tray for a game session, and comes back when it ends.</summary>
    bool _hiddenForGame;
    string _operation = "";

    public MainWindow()
    {
        InitializeComponent();
        InitializeWindowFrame();
        Control.Attach(_shared);
        PlayView.Attach(_shared, Control);
        Setup.Attach(_shared);
        Versions.Attach(_shared);
        Mods.Attach(_shared);
        Backups.Attach(_shared);
        Accounts.Attach(_shared);
        Performance.Attach(_shared);
        Timings.Attach(_shared);
        LogsView.Attach(_shared);
        ConsoleView.Attach(_shared);
        SettingsView.Attach(_shared);
        Lights.ItemsSource = _lights;

        _tray.ShowRequested += ShowFromTray;
        _tray.StopAllRequested += async () => await _shared.StopAllAsync(_ => { });
        _tray.ExitRequested += () => { ShowFromTray(); _explicitExit = true; Close(); };
        _shared.Monitor.Sampled += samples => Dispatcher.BeginInvoke(() =>
        {
            ClientSeen(samples.Any(s => s.Service == Service.Client && s.Running));
            ShowStatus(samples);
        });
        _shared.LiveRunning += running => Dispatcher.BeginInvoke(() =>
        {
            _live = running;
            BusyBar.Visibility = running && !Theme.Current.ReduceMotion ? Visibility.Visible : Visibility.Collapsed;
            ShowStatus(_shared.Monitor.Latest);
        });
        _shared.Workspace.Stage += (stage, _) => Dispatcher.BeginInvoke(() => { _operation = stage; ShowStatus(_shared.Monitor.Latest); });
        _shared.Workspace.Failed += (stage, message, log) => Dispatcher.BeginInvoke(() => ShowFailure(stage, message, log));

        _sections =
        [
            new("Play", PackIconKind.Play, PlayView),
            new("Control", PackIconKind.ViewDashboardOutline, Control),
            new("Setup", PackIconKind.ClipboardCheckOutline, Setup),
            new("Versions", PackIconKind.SourceBranch, Versions),
            new("Mods", PackIconKind.PuzzleOutline, Mods),
            new("Backups", PackIconKind.BackupRestore, Backups),
            new("Accounts", PackIconKind.AccountMultipleOutline, Accounts),
            new("Performance", PackIconKind.ChartLine, Performance),
            new("Server timings", PackIconKind.TimerOutline, Timings),
            new("Logs", PackIconKind.TextBoxOutline, LogsView),
            new("Console", PackIconKind.Console, ConsoleView),
            new("Settings", PackIconKind.CogOutline, SettingsView),
        ];
        ShowMode();
        Versions.Switched += CheckoutMoved;
        PlayView.CheckoutChanged += CheckoutMoved;
        PlayView.SetupRequested += Setup.OpenSetupGuide;
        PlayView.DetailsRequested += () => ShowSection(LogsView);
        Versions.PlayRequested += () =>
        {
            ShowSection(Control);
            _ = Control.PlayAsync();
        };
        Control.ShowLogRequested += source =>
        {
            LogsView.Show(source);
            OpenLogsSection(this, null!);
        };
        Control.ShowSetupRequested += () => ShowSection(Setup);
        Mods.ShowGatewayLog += () =>
        {
            LogsView.Show("Gateway");
            OpenLogsSection(this, null!);
        };
        Setup.CreateAccountRequested += CreateFirstAccount;
        Setup.SetupFinished += () => _ = Control.RefreshSetup();
        SettingsView.CheckUpdatesRequested += () => _ = CheckForUpdates(manual: true);
        L.Current.PropertyChanged += (_, _) => ShowLanguage();
        Theme.Changed += () =>
        {
            ShowTheme();
            ShowStatus(_shared.Monitor.Latest);
        };
        ShowLanguage();
        ShowTheme();
        Theme.Dress(this);
        Width = Math.Min(Width * Theme.Zoom, SystemParameters.WorkArea.Width);
        Height = Math.Min(Height * Theme.Zoom, SystemParameters.WorkArea.Height);

        Updates.CleanUp();
        if (_shared.Settings.CheckForUpdates && Updates.IsReleaseBuild) Loaded += (_, _) => _ = CheckForUpdates(manual: false);
        Loaded += (_, _) => _ = _shared.Workspace.RefreshAsync();
    }

    bool _live;

    /// <summary>Simple mode's sections and nothing about the servers, or all of them. The section on show stays when the new
    /// list has it.</summary>
    void ShowMode()
    {
        bool advanced = _shared.Settings.Advanced;
        var sections = _sections.Where(s => advanced ? s.View != PlayView : s.View is PlayTab or ModsTab or AccountsTab or SettingsTab).ToArray();
        var shown = Sections.SelectedItem as Section;
        Sections.ItemsSource = sections;
        Sections.SelectedItem = sections.Contains(shown) ? shown : sections[0];
        BranchChip.Visibility = StatusStrip.Visibility = advanced ? Visibility.Visible : Visibility.Collapsed;
        SimpleMode.IsChecked = !advanced;
        AdvancedMode.IsChecked = advanced;
        SettingsView.ShowMode();
    }

    /// <summary>The sidebar's switch.</summary>
    void ModeChosen(object sender, RoutedEventArgs e)
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

    void SectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Sections.SelectedItem is Section shown) Show(shown);
    }

    void Show(Section shown)
    {
        foreach (var s in _sections) s.View.Visibility = s == shown ? Visibility.Visible : Visibility.Collapsed;
        SectionTitle.Text = shown.DisplayName;
    }

    /// <summary>A link can lead to a section Simple mode's list does not have: it then shows without being selected.</summary>
    void ShowSection(FrameworkElement view)
    {
        var section = _sections.First(s => s.View == view);
        if (Sections.Items.Contains(section)) Sections.SelectedItem = section;
        else
        {
            Sections.SelectedItem = null;
            Show(section);
        }
    }

    void OpenLogsSection(object sender, RoutedEventArgs e) => ShowSection(LogsView);

    void OpenVersionsSection(object sender, RoutedEventArgs e) => ShowSection(Versions);

    void OpenControlSection(object sender, RoutedEventArgs e) => ShowSection(Control);

    void OpenSettingsSection(object sender, RoutedEventArgs e) => ShowSection(SettingsView);

    /// <summary>Ctrl+1 to Ctrl+9 and Ctrl+(Shift+)Tab choose a section, F5 inspects the checkout again, Ctrl+Shift+R restores
    /// the default appearance: the way back from a palette that left the window unreadable.</summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        var modifiers = Keyboard.Modifiers;
        int count = Sections.Items.Count;
        if (modifiers == ModifierKeys.Control && e.Key is >= Key.D1 and <= Key.D9 && e.Key - Key.D1 < count) Sections.SelectedIndex = e.Key - Key.D1;
        else if (modifiers == ModifierKeys.Control && e.Key == Key.Tab) Sections.SelectedIndex = (Sections.SelectedIndex + 1) % count;
        else if (modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.Tab) Sections.SelectedIndex = (Sections.SelectedIndex + count - 1) % count;
        else if (modifiers == ModifierKeys.None && e.Key == Key.F5) _ = _shared.Workspace.RefreshAsync();
        else if (modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.R)
        {
            _shared.Settings.Appearance = new AppearanceSettings();
            _shared.Settings.Save();
            Theme.Apply(_shared.Settings.Appearance);
        }
        else return;
        e.Handled = true;
    }

    /// <summary>What a language change reaches beyond the bound text: names written from code.</summary>
    void ShowLanguage()
    {
        Sections.Items.Refresh();
        if (Sections.SelectedItem is Section shown) SectionTitle.Text = shown.DisplayName;
        BranchName.Text = Repo.Branch() ?? L.Text("no git checkout");
        BranchChip.ToolTip = L.Text("The server checkout's branch ({0}): click to switch it", Environment.CurrentDirectory);
        Title = Updates.IsReleaseBuild ? $"Waypoint {Updates.Current}" : L.Text("Waypoint (built from source)");
        Version.Text = Updates.IsReleaseBuild ? L.Text("Version {0}", Updates.Current) : L.Text("Built from source");
        Language = System.Windows.Markup.XmlLanguage.GetLanguage(L.Current.Culture.Name);
        _tray.ShowLanguage();
        ShowRelease();
        UpdateWindowFrame();
        ShowTheme();
        ShowStatus(_shared.Monitor.Latest);
    }

    /// <summary>The status strip: each service's lamp, then the game and whatever holds the checkout.</summary>
    void ShowStatus(IReadOnlyList<ServiceSample> samples)
    {
        foreach (var light in _lights) light.Update(samples.FirstOrDefault(s => s.Service == light.Service));
        int up = samples.Count(s => s.Service.Kind == ServiceKind.Server && s.Running && s.Listening);
        int servers = Service.All.Count(s => s.Kind == ServiceKind.Server);
        bool game = samples.Any(s => s.Service == Service.Client && s.Running);
        StatusText.Text = up == servers ? L.Text("Servers running") : up == 0 ? L.Text("Servers stopped") : L.Text("{0} of {1} servers up", up, servers);
        string busy = _operation.Length > 0 ? L.Text(_operation) : _shared.BusyWith;
        StatusDetail.Text = "  ·  " + L.Text(game ? "game running" : "game not running") + (_live ? "  ·  " + L.Text("{0} is running …", busy) : "");
    }

    /// <summary>An adapter operation failed: what, why, and its log.</summary>
    void ShowFailure(string stage, string message, string? log)
    {
        int chosen = Dialog.Show(this, DialogKind.Error, L.Text(stage) + L.Text(" failed: "), L.Describe(message), null,
            log is not null && File.Exists(log) ? [L.Text("Open log"), L.Text("Close")] : [L.Text("Close")]);
        if (chosen == 0 && log is not null && File.Exists(log))
            Process.Start(new ProcessStartInfo("notepad.exe") { ArgumentList = { log }, UseShellExecute = false })?.Dispose();
    }

    /// <summary>The header's button goes Light, Dark, System and keeps the rest of the appearance.</summary>
    void CycleTheme(object sender, RoutedEventArgs e)
    {
        string next = Theme.Current.Mode switch { "Light" => "Dark", "Dark" => "System", _ => "Light" };
        _shared.Settings.Appearance = Theme.Current with { Mode = next };
        _shared.Settings.Save();
        Theme.Apply(_shared.Settings.Appearance);
    }

    void ShowTheme()
    {
        string mode = Theme.Current.Mode;
        ThemeIcon.Kind = mode switch { "Light" => PackIconKind.WhiteBalanceSunny, "Dark" => PackIconKind.WeatherNight, _ => PackIconKind.ThemeLightDark };
        string next = mode switch { "Light" => "Dark", "Dark" => "System", _ => "Light" };
        ThemeButton.ToolTip = L.Text("Theme: {0} (click for {1}); System follows Windows", L.Text(mode), L.Text(next));
    }

    Release? _release;

    /// <summary>Looks for a newer release and shows the bar when there is one. At startup quietly (no network, no
    /// release: nothing to say); asked for, it says what it found.</summary>
    async Task CheckForUpdates(bool manual)
    {
        try
        {
            _release = await Updates.CheckAsync();
            if (_release is not null)
            {
                ShowRelease();
                UpdateNow.IsEnabled = Updates.RunningExe is not null;
                UpdateBar.Visibility = Visibility.Visible;
            }
            else if (manual)
                Dialog.Tell(this, DialogKind.Info, L.Text("No update"),
                    L.Text("No newer release than {0}.", Updates.IsReleaseBuild ? Updates.Current.ToString() : L.Text("this build from source")));
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            _shared.Logs.Write($"update check failed: {ex.Message}", Severity.Warning);
            if (manual) Dialog.Tell(this, DialogKind.Warning, L.Text("Update check failed"), ex.Message);
        }
    }

    /// <summary>What the update bar says about the release found.</summary>
    void ShowRelease()
    {
        if (_release is null) return;
        UpdateText.Text = L.Text("Version {0} is out; this is {1}. The servers and the game keep running while it updates.", _release.Version,
            Updates.IsReleaseBuild ? Updates.Current.ToString() : L.Text("a build from source"));
        UpdateNow.ToolTip = Updates.RunningExe is null ? L.Text("Only the published Waypoint.exe updates itself") : null;
    }

    async void UpdateClicked(object sender, RoutedEventArgs e)
    {
        if (_release is null) return;
        if (!Dialog.Ask(this, DialogKind.Question, L.Text("Update to {0}?", _release.Version),
                L.Text("Waypoint downloads the new version and restarts; the servers and the game keep running."), L.Text("Update and restart"))) return;
        UpdateNow.IsEnabled = false;
        try
        {
            await Updates.ApplyAsync(_release, new Progress<string>(text => UpdateText.Text = text), ((App)Application.Current).ReleaseInstance);
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

    void ReleasePageClicked(object sender, RoutedEventArgs e)
    {
        if (_release?.Page is { Length: > 0 } page)
            Process.Start(new ProcessStartInfo(page) { UseShellExecute = true })?.Dispose();
    }

    void LaterClicked(object sender, RoutedEventArgs e) => UpdateBar.Visibility = Visibility.Collapsed;

    /// <summary>A client that starts sends the window to the tray; when no client is left it comes back. A client that
    /// Play closes and starts again (one live.ps1 run) keeps it in the tray, and a window opened while the game already
    /// runs stays open.</summary>
    void ClientSeen(bool running)
    {
        bool started = running && _clientRunning == false;
        _clientRunning = running;
        if (started && _shared.Settings.TrayWhileGameRuns && IsVisible && !_closing)
        {
            _hiddenForGame = true;
            Hide();
            _tray.Visible = true;
            _tray.Notify(L.Text("The game is running: Waypoint waits here and comes back when the game closes."));
        }
        else if (!running && _hiddenForGame && !_shared.IsLiveRunning)
        {
            ShowFromTray();
        }
    }

    void ShowFromTray()
    {
        _hiddenForGame = false;
        _tray.Visible = false;
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    /// <summary>A second start of Waypoint: this window comes forward; nothing in it changes.</summary>
    public AppActivationReply ActivateFromLaunch(AppInstanceIdentity identity, AppLaunchRequest request)
    {
        var reply = AppActivationPolicy.Evaluate(identity, request, Environment.CurrentDirectory, _shared.IsLiveRunning, _closing);
        if (!_closing)
        {
            ShowFromTray();
            WindowActivation.Focus(this);
        }
        return reply;
    }

    /// <summary>The servers and the client run on without Waypoint. Closing asks what to do until the player remembers an
    /// answer, but only when something runs: with nothing running there is nothing to decide.</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_closing) return;
        if (_deciding) { e.Cancel = true; return; }
        var settings = _shared.Settings;
        bool sessionEnding = Application.Current is App { IsSessionEnding: true };
        bool anything = _shared.Monitor.Latest.Any(s => s.Running && s.Service.Kind is not ServiceKind.Recorder);
        var action = WindowClosePolicy.Decide(settings.ForBridge, _shared.IsLiveRunning, _explicitExit, sessionEnding);
        _explicitExit = false;
        if (action == WindowCloseAction.Ask && !anything && !settings.CloseToTray && !settings.AlwaysAskOnClose) action = WindowCloseAction.Exit;
        switch (action)
        {
            case WindowCloseAction.Ask:
                e.Cancel = true;
                _deciding = true;
                Dispatcher.BeginInvoke(AskClose);
                return;
            case WindowCloseAction.Hide:
                e.Cancel = true;
                Dispatcher.BeginInvoke(HideToTray);
                return;
            case WindowCloseAction.Wait:
                e.Cancel = true;
                Dialog.Tell(this, DialogKind.Info, L.Text("{0} is still running", _shared.BusyWith), L.Text("Close Waypoint when it is done."));
                return;
        }
        bool stop = _stopOverride ?? settings.StopServicesOnExit;
        _stopOverride = null;
        if (stop && anything && !sessionEnding)
        {
            e.Cancel = true;
            Dispatcher.BeginInvoke(StopAndClose);
            return;
        }
        _closing = true;
    }

    /// <summary>The end of the setup guide: the Accounts section's Create dialog.</summary>
    internal async void CreateFirstAccount()
    {
        ShowSection(Accounts);
        await Accounts.CreateAccountAsync();
    }

    /// <summary>A real exit, whatever closing the window is set to do.</summary>
    internal void ExitNow()
    {
        _explicitExit = true;
        Close();
    }

    void AskClose()
    {
        try
        {
            var settings = _shared.Settings;
            var dialog = new CloseBehaviorDialog(this, settings.StopServicesOnExit, settings.CloseToTray, settings.AlwaysAskOnClose);
            if (dialog.ShowDialog() != true || dialog.Choice is not { } choice) return;
            bool stop = dialog.StopServices.IsChecked == true;
            if (dialog.RememberChoice.IsChecked == true)
            {
                settings.CloseToTray = choice == WindowCloseAction.Hide;
                settings.StopServicesOnExit = stop;
                settings.CloseBehaviorConfirmed = true;
                settings.AlwaysAskOnClose = false;
                settings.Save();
            }
            if (choice == WindowCloseAction.Hide) { HideToTray(); return; }
            _stopOverride = stop;
            _explicitExit = true;
            Dispatcher.BeginInvoke(Close);
        }
        finally { _deciding = false; }
    }

    void HideToTray()
    {
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

    protected override void OnClosed(EventArgs e)
    {
        _tray.Dispose();
        _shared.Dispose();
        base.OnClosed(e);
    }
}
