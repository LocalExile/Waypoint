using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using MaterialDesignThemes.Wpf;

namespace GameServer.Launcher;

/// <summary>A section of the sidebar and the view it shows.</summary>
sealed record Section(string Name, PackIconKind Icon, FrameworkElement View);

public partial class MainWindow : Window
{
    readonly Shared _shared = new();
    readonly Tray _tray = new();
    bool _closing;
    /// <summary>Whether the client ran at the last sample; null before the first.</summary>
    bool? _clientRunning;
    /// <summary>The window went to the tray for a game session, and comes back when it ends.</summary>
    bool _hiddenForGame;

    public MainWindow()
    {
        InitializeComponent();
        Control.Attach(_shared);
        Versions.Attach(_shared);
        Performance.Attach(_shared);
        Timings.Attach(_shared);
        LogsView.Attach(_shared);
        Accounts.Attach(_shared);
        ConsoleView.Attach(_shared);

        _tray.ShowRequested += ShowFromTray;
        _tray.StopAllRequested += async () => await _shared.StopAllAsync(_ => { });
        _tray.ExitRequested += () => { ShowFromTray(); Close(); };
        _shared.Monitor.Sampled += samples => Dispatcher.BeginInvoke(() =>
        {
            ClientSeen(samples.Any(s => s.Service == Service.Client && s.Running));
            ShowStatus(samples);
        });
        _shared.LiveRunning += running => Dispatcher.BeginInvoke(() => { _live = running; ShowStatus(_shared.Monitor.Latest); });

        Sections.ItemsSource = new Section[]
        {
            new("Control", PackIconKind.ViewDashboardOutline, Control),
            new("Versions", PackIconKind.SourceBranch, Versions),
            new("Performance", PackIconKind.ChartLine, Performance),
            new("Server timings", PackIconKind.TimerOutline, Timings),
            new("Logs", PackIconKind.TextBoxOutline, LogsView),
            new("Accounts", PackIconKind.AccountMultipleOutline, Accounts),
            new("Console", PackIconKind.Console, ConsoleView),
        };
        Sections.SelectedIndex = 0;
        Versions.Switched += () =>
        {
            BranchName.Text = Repo.Branch() ?? "no git checkout";
            _ = Control.RefreshSetup();
        };
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
        string? branch = Repo.Branch();
        BranchName.Text = branch ?? "no git checkout";
        BranchChip.ToolTip = $"The server checkout's branch ({Environment.CurrentDirectory}): click to switch it";
        TrayWhileGame.IsChecked = _shared.Settings.TrayWhileGameRuns;
        CheckAtStart.IsChecked = _shared.Settings.CheckForUpdates;
        ShowTheme();
        Theme.TitleBar(this);

        Title = Updates.IsReleaseBuild ? $"LocalExile Launcher {Updates.Current}" : "LocalExile Launcher (built from source)";
        Version.Text = Updates.IsReleaseBuild ? $"Launcher {Updates.Current}" : "Launcher (built from source)";
        Updates.CleanUp();
        if (_shared.Settings.CheckForUpdates && Updates.IsReleaseBuild) Loaded += (_, _) => _ = CheckForUpdates(manual: false);
    }

    bool _live;

    void SectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Sections.SelectedItem is not Section shown) return;
        foreach (Section s in Sections.Items) s.View.Visibility = s == shown ? Visibility.Visible : Visibility.Collapsed;
        SectionTitle.Text = shown.Name;
    }

    void ShowSection(FrameworkElement view) => Sections.SelectedItem = Sections.Items.Cast<Section>().First(s => s.View == view);

    void OpenLogsSection(object sender, RoutedEventArgs e) => ShowSection(LogsView);

    void OpenVersionsSection(object sender, RoutedEventArgs e) => ShowSection(Versions);

    /// <summary>The status strip: the servers, the game, and a live.ps1 run.</summary>
    void ShowStatus(IReadOnlyList<ServiceSample> samples)
    {
        int up = samples.Count(s => s.Service.Kind == ServiceKind.Server && s.Running && s.Listening);
        int servers = Service.All.Count(s => s.Kind == ServiceKind.Server);
        bool game = samples.Any(s => s.Service == Service.Client && s.Running);
        StatusText.Text = up == servers ? "Servers running" : up == 0 ? "Servers stopped" : $"{up} of {servers} servers up";
        StatusLamp.Fill = Theme.Brush(up == servers ? "Shell.Good" : up == 0 ? "Shell.Idle" : "Shell.Warning");
        StatusDetail.Text = $"  ·  game {(game ? "running" : "not running")}" + (_live ? $"  ·  {_shared.BusyWith} is running …" : "");
    }

    void CycleTheme(object sender, RoutedEventArgs e)
    {
        var next = (ThemeChoice)(((int)Theme.Mode + 1) % 3);
        Theme.Apply(next);
        _shared.Settings.Theme = next;
        _shared.Settings.Save();
        ShowTheme();
        ShowStatus(_shared.Monitor.Latest);
    }

    void ShowTheme()
    {
        ThemeIcon.Kind = Theme.Mode switch { ThemeChoice.Light => PackIconKind.WhiteBalanceSunny, ThemeChoice.Dark => PackIconKind.WeatherNight, _ => PackIconKind.ThemeLightDark };
        ThemeButton.ToolTip = $"Theme: {Theme.Mode} (click for {(ThemeChoice)(((int)Theme.Mode + 1) % 3)}); System follows Windows";
    }

    void TrayChanged(object sender, RoutedEventArgs e)
    {
        _shared.Settings.TrayWhileGameRuns = TrayWhileGame.IsChecked == true;
        _shared.Settings.Save();
    }

    void CheckAtStartChanged(object sender, RoutedEventArgs e)
    {
        _shared.Settings.CheckForUpdates = CheckAtStart.IsChecked == true;
        _shared.Settings.Save();
    }

    void CheckUpdatesClicked(object sender, RoutedEventArgs e) => _ = CheckForUpdates(manual: true);

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
                UpdateText.Text = $"Version {_release.Version} is out; this is {(Updates.IsReleaseBuild ? Updates.Current.ToString() : "a build from source")}. " +
                    "The servers and the game keep running while it updates.";
                UpdateNow.IsEnabled = Updates.RunningExe is not null;
                UpdateNow.ToolTip = Updates.RunningExe is null ? "Only the published LocalExileLauncher.exe updates itself" : null;
                UpdateBar.Visibility = Visibility.Visible;
            }
            else if (manual)
                Dialog.Tell(this, DialogKind.Info, "No update",
                    $"No newer release than {(Updates.IsReleaseBuild ? Updates.Current.ToString() : "this build from source")}.");
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            _shared.Logs.Write($"update check failed: {ex.Message}", Severity.Warning);
            if (manual) Dialog.Tell(this, DialogKind.Warning, "Update check failed", ex.Message);
        }
    }

    async void UpdateClicked(object sender, RoutedEventArgs e)
    {
        if (_release is null) return;
        if (!Dialog.Ask(this, DialogKind.Question, $"Update to {_release.Version}?",
                "The launcher downloads the new version and restarts; the servers and the game keep running.", "Update and restart")) return;
        UpdateNow.IsEnabled = false;
        try
        {
            await Updates.ApplyAsync(_release, new Progress<string>(text => UpdateText.Text = text));
            // The new launcher runs: this one closes without asking about the servers.
            _closing = true;
            Close();
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException or IOException
            or UnauthorizedAccessException or InvalidOperationException)
        {
            UpdateNow.IsEnabled = true;
            UpdateText.Text = $"The update failed: {ex.Message}";
            _shared.Logs.Write($"update failed: {ex.Message}", Severity.Error);
        }
    }

    void ReleasePageClicked(object sender, RoutedEventArgs e)
    {
        if (_release?.Page is { Length: > 0 } page)
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(page) { UseShellExecute = true })?.Dispose();
    }

    void LaterClicked(object sender, RoutedEventArgs e) => UpdateBar.Visibility = Visibility.Collapsed;

    /// <summary>A client that starts sends the launcher to the tray; when no client is left it comes back. A client that
    /// Play closes and starts again (one live.ps1 run) keeps it in the tray, and a launcher opened while the game already
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
            _tray.Notify("The game is running: the launcher waits here and comes back when the game closes.");
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

    /// <summary>The servers and the client run on without the launcher; closing it asks whether to stop them.</summary>
    protected override async void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_closing) return;
        bool anything = _shared.Monitor.Latest.Any(s => s.Running && s.Service.Kind is not ServiceKind.Recorder);
        if (anything)
        {
            int answer = Dialog.Show(this, DialogKind.Question, "Close the launcher",
                "The servers or the game are still running. They keep running without the launcher, or stop now.", null,
                ["Stop them and close", "Leave them running", "Cancel"]);
            if (answer is 2 or -1) { e.Cancel = true; return; }
            if (answer == 0)
            {
                e.Cancel = true;
                if (_shared.IsLiveRunning)
                {
                    Dialog.Tell(this, DialogKind.Info, $"{_shared.BusyWith} is still running", "Close the launcher when it is done.");
                    return;
                }
                IsEnabled = false;
                Title = "LocalExile Launcher: stopping …";
                await _shared.StopAllAsync(_ => { });
                _closing = true;
                Close();
                return;
            }
        }
        _closing = true;
    }

    protected override void OnClosed(EventArgs e)
    {
        _tray.Dispose();
        _shared.Dispose();
        base.OnClosed(e);
    }
}
