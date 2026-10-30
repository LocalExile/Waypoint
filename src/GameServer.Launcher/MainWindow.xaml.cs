using System.ComponentModel;
using System.Windows;

namespace GameServer.Launcher;

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
        Performance.Attach(_shared);
        LogsView.Attach(_shared);
        Accounts.Attach(_shared);
        ConsoleView.Attach(_shared);

        _tray.ShowRequested += ShowFromTray;
        _tray.StopAllRequested += async () => await _shared.RunLiveAsync(new LiveRequest(LiveMode.Stop), _ => { });
        _tray.ExitRequested += () => { ShowFromTray(); Close(); };
        _shared.Monitor.Sampled += samples => Dispatcher.BeginInvoke(() =>
            ClientSeen(samples.Any(s => s.Service == Service.Client && s.Running)));
    }

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
            var answer = MessageBox.Show(this, "Stop the servers and the game as well?\n\nNo leaves them running.",
                "LocalExile Launcher", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Cancel) { e.Cancel = true; return; }
            if (answer == MessageBoxResult.Yes)
            {
                e.Cancel = true;
                if (_shared.IsLiveRunning)
                {
                    MessageBox.Show(this, "live.ps1 is still running; close the launcher when it is done.", "LocalExile Launcher");
                    return;
                }
                IsEnabled = false;
                Title = "LocalExile Launcher: stopping …";
                _shared.StopDevHost();
                await _shared.RunLiveAsync(new LiveRequest(LiveMode.Stop), _ => { });
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
