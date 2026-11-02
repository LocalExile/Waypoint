using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Material.Icons;

namespace Waypoint;

/// <summary>
/// A release install's home. Play is one run of <c>LocalExile.exe --json</c>, which prepares what it must, starts the servers and
/// the client, and stays alive until the client closes. It holds the install's lock only until the game is ready, so that the
/// window can close or hide while the game runs. A question it stops with (no client, no account) goes to the setup guide.
/// </summary>
partial class InstallTab : UserControl
{
    /// <summary>A step of the run with what is happening in it, updated in place. (On Windows it is the setup guide's.)</summary>
    sealed class StepRow(string name) : INotifyPropertyChanged
    {
        public string Name { get; } = name;
        public string Detail { get; private set; } = "";
        public bool DetailShown => Detail.Length > 0;
        public SetupState State { get; private set; } = SetupState.Pending;
        public MaterialIconKind Icon => State switch
        {
            SetupState.Running => MaterialIconKind.ProgressClock,
            SetupState.Done or SetupState.Skipped => MaterialIconKind.CheckCircleOutline,
            SetupState.Stopped => MaterialIconKind.PauseCircleOutline,
            SetupState.Failed => MaterialIconKind.CloseCircleOutline,
            _ => MaterialIconKind.CircleOutline,
        };
        public IBrush Brush => App.Brush(State switch
        {
            SetupState.Running => "Shell.Accent",
            SetupState.Done or SetupState.Skipped => "Shell.Good",
            SetupState.Stopped => "Shell.Warning",
            SetupState.Failed => "Shell.Error",
            _ => "Shell.Idle",
        });

        public void Show(SetupState state, string detail)
        {
            State = state;
            Detail = detail;
            PropertyChanged?.Invoke(this, new(null));
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    Shared _shared = null!;
    bool _working, _stopping, _failed, _needsSetup, _withClient = true;
    string? _stage;
    long? _current, _total;
    /// <summary>The words shown, as functions, so that a language change says them again.</summary>
    Func<string>? _headline, _note;
    readonly ObservableCollection<StepRow> _steps = [];
    readonly StringBuilder _log = new();
    /// <summary>The step that runs now.</summary>
    StepRow? _row;
    readonly Stopwatch _clock = new();

    /// <summary>The window runs the setup guide.</summary>
    internal event Action? SetupRequested;
    /// <summary>The window opens the logs.</summary>
    internal event Action? DetailsRequested;
    /// <summary>The player wants another folder.</summary>
    internal event Action? ChangeFolderRequested;
    /// <summary>The player wants a new git copy set up.</summary>
    internal event Action? NewCopyRequested;

    public InstallTab() => InitializeComponent();

    Window? Owner => TopLevel.GetTopLevel(this) as Window;
    bool GameRunning => _shared.Monitor.Latest.Any(s => s.Service == Service.Client && s.Running && Ours(s));
    bool AnythingRunning => _shared.Monitor.Latest.Any(s => s.Running && Ours(s));

    /// <summary>The monitor sees the whole machine. A source checkout's servers are no game of this install's: taking them for
    /// one hides Play and offers a Stop that cannot reach them. An install's programs work from its folder, or its Client.</summary>
    bool Ours(ServiceSample sample)
    {
        try
        {
            string? working = new DirectoryInfo($"/proc/{sample.Pid}/cwd").ResolveLinkTarget(false)?.FullName;
            return working is not null && (working == Folder || working.StartsWith(Folder + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || working == ClientLocator.RealPath(Path.Combine(Folder, "Client")));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }
    static string Folder => Environment.CurrentDirectory;

    public void Attach(Shared shared)
    {
        _shared = shared;
        if (!Repo.IsInstall) return;
        StepList.ItemsSource = _steps;
        shared.Monitor.Sampled += _ => Dispatcher.UIThread.Post(Show);
        shared.LiveRunning += _ => Dispatcher.UIThread.Post(Show);
        L.Current.PropertyChanged += (_, _) => Show();
        Show();
    }

    /// <summary>Something changed the install from outside (the setup guide, an update).</summary>
    internal void InstallChanged(Func<string>? note = null)
    {
        _needsSetup = false;
        _failed = false;
        _note = note;
        Show();
    }

    void Show()
    {
        bool needsSetup = _needsSetup || !LocalExileReleases.IsSetUp(Folder);
        bool busy = _working || _shared.IsLiveRunning;
        Headline.Text = _headline?.Invoke() ?? (GameRunning ? L.Text("The game is running")
            : needsSetup ? L.Text("LocalExile isn't ready yet")
            : L.Text("Ready to play"));
        MainButton.Content = needsSetup ? L.Text("Finish setup") : L.Text("Play");
        MainButton.IsVisible = !(busy || GameRunning || AnythingRunning);
        Working.IsVisible = busy && (_shared.Settings.Appearance?.ReduceMotion != true || _total is > 0);
        Working.IsIndeterminate = _total is not > 0;
        if (_total is > 0 and { } total) Working.Value = 100d * (_current ?? 0) / total;
        string? note = _note?.Invoke();
        Note.Text = note ?? "";
        Note.IsVisible = note is not null;
        DetailsLink.IsVisible = _failed && !busy;
        // A step that writes the client or the keys is not cut short.
        StopLink.IsVisible = !_stopping && !LocalExileReleases.MustFinish(_stage) && (_working || AnythingRunning);
        FolderText.Text = Folder;
        Elapsed.Text = _working ? L.Text("Running for {0}", _clock.Elapsed.ToString(@"m\:ss")) : "";
        VersionText.Text = LocalExileReleases.Installed(Folder) is { } version ? L.Text("LocalExile {0}", version) : "";
    }

    void MainClicked(object? sender, RoutedEventArgs e)
    {
        if (_needsSetup || !LocalExileReleases.IsSetUp(Folder)) { SetupRequested?.Invoke(); return; }
        _ = PlayAsync();
    }

    internal Task PlayAsync() => RunAsync(["run", "--no-update-check"], true);

    /// <summary>The Control section's Start servers. One LocalExile.exe holds an install, so the game cannot join them later.</summary>
    internal Task StartServersAsync() => RunAsync(["run", "--no-client", "--no-update-check"], false);

    async Task RunAsync(string[] arguments, bool withClient)
    {
        if (_working || _shared.IsLiveRunning || !await BackUp()) return;
        // Another folder's servers on these ports: the same question as a checkout's, instead of a refusal from the run.
        if (await PortCheck.ClearAsync(ViewTools.Owner(this), PortCheck.ServerPorts, [], line => _shared.Logs.Write(line)) != PortAnswer.Free) return;
        _working = true;
        _failed = false;
        _stopping = false;
        _note = null;
        _withClient = withClient;
        _headline = () => withClient ? L.Text("Starting the game") : L.Text("Starting the servers");
        _steps.Clear();
        _row = null;
        _log.Clear();
        Log.Clear();
        Activity.IsVisible = true;
        _clock.Restart();
        Show();
        var ready = new TaskCompletionSource();
        LocalExileResult? result = null;
        string? failure = null;
        try
        {
            // Queued: the events come from the tool's threads.
            var run = LocalExileTool.RunAsync(Folder, arguments, item => Dispatcher.UIThread.Post(() => Event(item, ready)), line => Dispatcher.UIThread.Post(() =>
            {
                _shared.Logs.Write($"LocalExile: {line}");
                Write(line);
            }));
            await _shared.RunExclusiveAsync("LocalExile.exe", () => Task.WhenAny(ready.Task, run));
            result = await run;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            failure = ex.Message;
            _shared.Logs.Write($"LocalExile.exe could not run: {ex.Message}", Severity.Error);
        }
        _working = false;
        _clock.Stop();
        _stage = null;
        _current = _total = null;
        _headline = null;
        Finished(result, failure);
        Show();
    }

    /// <summary>As on the Control section: a start gets a backup of the saves first, or the player's word to go on without.</summary>
    async Task<bool> BackUp()
    {
        if (!_shared.Settings.AutomaticBackups || !LocalExileReleases.HasSaves(Folder)) return true;
        _headline = () => L.Text("Backing up your saves");
        Show();
        var result = await _shared.Workspace.OperateAsync("Backup", L.Text("Back up now"), quiet: true);
        _headline = null;
        Show();
        return result is { Ok: true } || Dialog.Ask(Owner, DialogKind.Warning, L.Text("Start without a backup?"),
            L.Text("The saves could not be backed up. Automatic backups can be turned off on the Backups section."), L.Text("Start anyway"), cancelByDefault: true);
    }

    void Event(LocalExileEvent item, TaskCompletionSource ready)
    {
        if (item.Message is { } message)
        {
            _shared.Logs.Write($"LocalExile: {message}", item.Name == "error" ? Severity.Error : item.Name == "warning" ? Severity.Warning : null);
            Write(item.Name is "warning" or "error" ? $"{item.Name}: {message}" : message);
        }
        switch (item.Name)
        {
            case "step" or "progress":
                _stage = item.Id;
                string name = LocalExileReleases.Stage(item.Id, item.Message ?? L.Text("Starting the game"));
                if (_row?.Name != name)
                {
                    _row?.Show(SetupState.Done, "");
                    _steps.Add(_row = new StepRow(name));
                    StepScroll.ScrollToEnd();
                    _current = _total = null;
                }
                // "Still working" reports count nothing: the last count stays on the bar.
                if (item.Total is not null)
                {
                    _current = item.Current;
                    _total = item.Total;
                }
                _row!.Show(SetupState.Running, item.Message ?? "");
                _headline = () => LocalExileReleases.Stage(item.Id, item.Message ?? L.Text("Starting the game"));
                _note = null;
                break;
            case "ready":
                _stage = null;
                _current = _total = null;
                _headline = null;
                _row?.Show(SetupState.Done, "");
                _row = null;
                var running = new StepRow(_withClient ? L.Text("The game is running") : L.Text("The servers are running"));
                running.Show(SetupState.Done, "");
                _steps.Add(running);
                StepScroll.ScrollToEnd();
                _note = _withClient ? () => L.Text("Log in with your LocalExile account: its name goes in the Email field. Closing the game stops the servers.") : null;
                ready.TrySetResult();
                break;
        }
        Show();
    }

    void Finished(LocalExileResult? result, string? failure)
    {
        if (_stopping || result is { Code: 0 })
        {
            _row?.Show(_stopping ? SetupState.Stopped : SetupState.Done, "");
            _note = null;
            return;
        }
        if (result?.NeededOption is not null || result?.Code == LocalExileResult.NeedsInput)
        {
            _needsSetup = true;
            _row?.Show(SetupState.Stopped, "");
            _note = () => L.Text("LocalExile needs a little more from you. Finish setup to give it.");
            return;
        }
        _failed = true;
        string? reason = failure ?? result?.Error?.Split('\n')[0];
        _row?.Show(SetupState.Failed, reason ?? "");
        _note = () => L.Text("The game didn't start.") + (reason is null ? "" : "\n" + reason);
    }

    void Write(string line)
    {
        _log.AppendLine($"{DateTime.Now:HH:mm:ss}  {line.TrimEnd()}");
        if (_log.Length > 100_000) _log.Remove(0, _log.Length - 80_000);
        Log.Text = _log.ToString();
        Log.ScrollToEnd();
    }

    async void StopClicked(object? sender, RoutedEventArgs e)
    {
        if (_stopping) return;
        _stopping = true;
        _note = null;
        _headline = () => L.Text("Stopping");
        Show();
        await _shared.StopAllAsync(_ => { });
        _stopping = _working;
        _headline = _working ? _headline : null;
        Show();
    }

    void ChangeFolderClicked(object? sender, RoutedEventArgs e) => ChangeFolderRequested?.Invoke();

    void NewCopyClicked(object? sender, RoutedEventArgs e) => NewCopyRequested?.Invoke();

    void DetailsClicked(object? sender, RoutedEventArgs e) => DetailsRequested?.Invoke();
}
