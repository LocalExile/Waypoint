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
    bool _working, _checking, _stopping, _failed, _needsSetup, _withClient = true;
    string? _stage;
    long? _current, _total;
    /// <summary>The words shown, as functions, so that a language change says them again.</summary>
    Func<string>? _headline, _note;
    /// <summary>While the game client is copied into this release (<see cref="OwnCopyAsync"/>): Stop ends the copy.</summary>
    CancellationTokenSource? _copyStop;
    /// <summary>This release's client is a link to the user's own Path of Exile folder: the page says what LocalExile says at
    /// every start (<see cref="OwnClient.Notice"/>) and offers the copy. Looked for in the background.</summary>
    OwnClient.Plan? _linked;
    /// <summary>The folder has no game client at all, in use or kept: the page says so.</summary>
    bool _noClient;
    /// <summary>The game folder outside LocalExile the client is a link to, also one Waypoint cannot copy.</summary>
    string? _linkedTo;

    /// <summary>Which version of the game this release's client is, under the Play button beside LocalExile's own version.</summary>
    string? _clientVersion;

    async Task LookForOwnFolderAsync()
    {
        string folder = Folder;
        (_linked, _clientVersion, _noClient, _linkedTo) = await Task.Run(() => (OwnClient.Check(folder), FolderList.ClientOf(folder),
            ClientVersions.Layout(folder) == ClientLayout.None && ClientVersions.Kept(folder).Count == 0, OwnClient.LinkedOutside(folder)));
        Show();
    }
    readonly ObservableCollection<StepRow> _steps = [];
    readonly StringBuilder _log = new();
    /// <summary>The step that runs now.</summary>
    StepRow? _row;
    readonly Stopwatch _clock = new();

    /// <summary>The window runs the setup guide.</summary>
    internal event Action? SetupRequested;
    /// <summary>The player has only an older client: the window sets up the last LocalExile made for it, in a new folder.</summary>
    internal event Action? OlderReleaseRequested;
    /// <summary>This release was updated from here: the window drops its update card.</summary>
    internal event Action? Updated;
    /// <summary>The window opens the logs.</summary>
    internal event Action? DetailsRequested;

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
        _ = LookForOlderAsync();
        // A move to a client of its own that was cut short as the copy took the link's place is finished first: until then the
        // folder has no client, and this page would send the user to the setup guide.
        try { OwnClient.Finish(Folder); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { shared.Logs.Write($"the game client's move was not finished: {e.Message}", Severity.Warning); }
        _ = LookForOwnFolderAsync();
        StepList.ItemsSource = _steps;
        shared.Monitor.Sampled += _ => Dispatcher.UIThread.Post(Show);
        shared.LiveRunning += _ => Dispatcher.UIThread.Post(Show);
        L.Current.PropertyChanged += (_, _) => Show();
        Show();
    }

    /// <summary>Something changed the install from outside (the setup guide, an update). A source checkout has no Install
    /// section: nothing of it is attached, so there is nothing to show.</summary>
    internal void InstallChanged(Func<string>? note = null)
    {
        if (!Repo.IsInstall) return;
        _ = LookForOlderAsync();
        _ = LookForOwnFolderAsync();
        _needsSetup = false;
        _failed = false;
        _note = note;
        Show();
    }

    /// <summary>The client build of the older LocalExile this folder runs, while a client of that build is all the player has:
    /// Play then says, quietly, that it won't last (<see cref="ClientUpdate.OlderNote"/>). Looked for in the background; a
    /// client of the current build found instead is Play's to move to.</summary>
    string? _olderBuild, _olderLookedFor;

    async Task LookForOlderAsync()
    {
        string folder = Environment.CurrentDirectory;
        string? need = await Task.Run(() => ClientLocator.Needed(folder));
        // Looked for again only when the folder's LocalExile changed (an update, a move to another version).
        if (need is not null && need == _olderLookedFor) return;
        _olderLookedFor = need;
        _olderBuild = await Task.Run(() => ClientUpdate.OlderVersionOf(folder) is { } made && ClientUpdate.FindCurrent() is null ? made : null);
        Show();
    }

    void Show()
    {
        bool needsSetup = _needsSetup || !LocalExileReleases.IsSetUp(Folder);
        bool busy = _working || _checking || _shared.IsLiveRunning;
        RunSetupLink.IsEnabled = !busy && !AnythingRunning;
        Headline.Text = _headline?.Invoke() ?? (GameRunning ? L.Text("The game is running")
            : needsSetup ? L.Text("LocalExile isn't ready yet")
            : L.Text("Ready to play"));
        MainButton.Content = needsSetup ? L.Text("Finish setup") : L.Text("Play");
        MainButton.IsVisible = !(busy || GameRunning || AnythingRunning);
        Working.IsVisible = busy && (_shared.Settings.Appearance?.ReduceMotion != true || _total is > 0);
        Working.IsIndeterminate = _total is not > 0;
        if (_total is > 0 and { } total) Working.Value = 100d * (_current ?? 0) / total;
        string? note = _note?.Invoke() ?? (_linked is { } link ? OwnClient.Notice(link)
            : _linkedTo is { } target ? OwnClient.Notice(target)
            : _noClient && !busy ? L.Text("No game client in this folder yet.")
            : _olderBuild is { } older ? ClientUpdate.OlderNoteFor(older) : null);
        Note.Text = note ?? "";
        Note.IsVisible = note is not null;
        DetailsLink.IsVisible = _failed && !busy;
        // A step that writes the client or the keys is not cut short.
        StopLink.IsVisible = !_stopping && !LocalExileReleases.MustFinish(_stage) && (_working || AnythingRunning || _copyStop is not null);
        StopLink.Content = _copyStop is not null ? L.Text("Stop the copy") : L.Text("Stop playing");
        OwnCopyLink.IsVisible = _linked is not null && _note is null && !busy && !AnythingRunning;
        Elapsed.Text = _working ? L.Text("Running for {0}", _clock.Elapsed.ToString(@"m\:ss")) : "";
        VersionText.Text = string.Join(" · ", new[] { LocalExileReleases.Installed(Folder) is { } version ? L.Text("LocalExile {0}", version) : null, FolderList.ClientText(_clientVersion) }.OfType<string>());
        string? from = _bringLater ? null : Editions.BringFrom(_shared.Settings);
        // The check holds the folder as any work does (busy): its card stays, and says what Waypoint is doing.
        BringCard.IsVisible = from is not null && (_bringChecking || !busy && !AnythingRunning) && !needsSetup;
        if (from is not null) BringText.Text = L.Text("Your characters are still in {0}. Waypoint can bring them here when this release is the same as or newer than that copy.", from);
        BringButton.Content = _bringChecking ? L.Text("Checking…") : L.Text("Bring my characters…");
        BringNeverLink.Content = L.Text("Don't offer again");
        BringButton.IsEnabled = BringLaterButton.IsEnabled = BringNeverLink.IsEnabled = !_bringChecking;
    }

    /// <summary>"Not now" on the characters card: until Waypoint starts again.</summary>
    bool _bringLater;
    /// <summary>Waypoint is finding out whether the characters fit, which may fetch LocalExile's newest code.</summary>
    bool _bringChecking;

    async void BringClicked(object? sender, RoutedEventArgs e)
    {
        var owner = ViewTools.Owner(this);
        bool brought = await Editions.BringToReleaseAsync(_shared, (kind, title, message, buttons) => Dialog.Show(owner, kind, title, message, null, buttons),
            checking => { _bringChecking = checking; Show(); });
        if (brought) _note = () => L.Text("Your characters came with you. Log in as usual.");
        Show();
    }

    /// <summary>"Don't offer again" on the characters card: for good, after one question.</summary>
    void BringNeverClicked(object? sender, RoutedEventArgs e)
    {
        var owner = ViewTools.Owner(this);
        Editions.StopOfferingBring(_shared, (kind, title, message, buttons) => Dialog.Show(owner, kind, title, message, null, buttons));
        Show();
    }

    void BringLaterClicked(object? sender, RoutedEventArgs e)
    {
        _bringLater = true;
        Show();
    }

    void MainClicked(object? sender, RoutedEventArgs e)
    {
        if (_needsSetup || !LocalExileReleases.IsSetUp(Folder)) { SetupRequested?.Invoke(); return; }
        _ = PlayAsync();
    }

    void RunSetupClicked(object? sender, RoutedEventArgs e)
    {
        if (!_working && !_checking && !_shared.IsLiveRunning && !AnythingRunning) SetupRequested?.Invoke();
    }

    internal Task PlayAsync() => RunAsync(["run", "--no-update-check"], true);
    /// <summary>A run with a client of another build than this release needs would fail at the login (<see cref="ClientUpdate"/>).
    /// A player with a client of the current build is moved to it without a question: this release is updated to the newest
    /// (saves copied first), an older client is taken out, and LocalExile.exe is given the found one; the arguments to add, or
    /// null when nothing starts. Only a player who has nothing newer than an older client is asked, about the older version.</summary>
    async Task<string[]?> ClientFits(bool again = false)
    {
        var owner = ViewTools.Owner(this);
        var (outcome, plan) = await ClientUpdate.EnsureAsync(Folder, lookForNewer: true,
            p => ClientUpdate.AskMove(p, (title, message, details, buttons) => Dialog.Show(owner, DialogKind.Question, title, message, details, buttons), ViewTools.Open),
            p => Dialog.Ask(owner, DialogKind.Question, ClientUpdate.Title(p),
                ClientUpdate.OlderText(p, release: true), ClientUpdate.Confirm(p)),
            text => Dialog.Tell(owner, DialogKind.Error, L.Text("Replace the game client?"), text),
            work => _shared.RunExclusiveAsync(L.Text("Replacing the game client"), work));
        switch (outcome)
        {
            case ClientUpdate.Outcome.Fits:
                return [];
            case ClientUpdate.Outcome.Replaced:
                _shared.Logs.Write(ClientUpdate.Moving(plan!), Severity.Info);
                _note = () => ClientUpdate.Moving(plan!);
                Show();
                return ClientUpdate.ClientArguments(plan!);
            case ClientUpdate.Outcome.UseOlder:
                OlderReleaseRequested?.Invoke();
                return null;
            case ClientUpdate.Outcome.UpdateLocalExile when !again:
                return await UpdateRelease(plan!) ? await ClientFits(again: true) : null;
            default:
                _note = () => L.Text("Not started: the game client does not fit this LocalExile.");
                Show();
                return null;
        }
    }

    /// <summary>The way out LocalExile's own warning names for a release whose client is a link to the user's Path of Exile
    /// folder: a copy of its own (<see cref="OwnClient"/>), made only on the user's word from the page's link. The link works
    /// until a whole copy takes its place, and nothing starts here.</summary>
    async void OwnCopyClicked(object? sender, RoutedEventArgs e)
    {
        if (_working || _checking || _shared.IsLiveRunning || AnythingRunning) return;
        _checking = true;
        Show();
        try { await OwnCopyAsync(); }
        finally { _checking = false; }
        Show();
    }

    async Task OwnCopyAsync()
    {
        var owner = ViewTools.Owner(this);
        using var stop = new CancellationTokenSource();
        var clock = new Stopwatch();
        long? startedAt = null;
        var result = await OwnClient.MakeCopyAsync(Folder,
            // Cancel is the button Enter presses: a box that opens while its user types must not start a long copy by accident.
            plan => OwnClient.Ask(plan, (title, message, details, buttons) => Dialog.Show(owner, DialogKind.Question, title, message, details, buttons, buttons.Length - 1), ViewTools.Open),
            copied => Dispatcher.UIThread.Post(() =>
            {
                if (_copyStop is null) return;
                startedAt ??= copied.Done;
                double rate = (copied.Done - startedAt.Value) / Math.Max(clock.Elapsed.TotalSeconds, .1);
                _current = copied.Done;
                _total = copied.Total;
                _note = () => L.Text("{0:0.0} of {1:0.0} GB · {2:0} MB/s · {3}", copied.Done / 1e9, copied.Total / 1e9, rate / 1e6, copied.File);
                Show();
            }),
            work =>
            {
                _copyStop = stop;
                clock.Start();
                _headline = () => L.Text("Copying the game client");
                _note = null;
                Show();
                return _shared.RunExclusiveAsync(L.Text("Copying the game client"), work);
            },
            stop.Token);
        _copyStop = null;
        _headline = null;
        _note = null;
        _current = _total = null;
        if (result.Outcome == OwnClient.Outcome.Moved) _linked = null;
        switch (result.Outcome)
        {
            case OwnClient.Outcome.Moved:
                _shared.Logs.Write($"the game client was copied from {result.Plan!.Source}; this release has its own now", Severity.Info);
                Show();
                Dialog.Tell(owner, DialogKind.Info, OwnClient.DoneTitle, OwnClient.DoneText(result.Plan));
                break;
            case OwnClient.Outcome.Failed:
                _shared.Logs.Write($"the game client was not copied: {result.Reason}", Severity.Warning);
                Show();
                Dialog.Tell(owner, DialogKind.Warning, OwnClient.FailedTitle, OwnClient.FailedText(result.Plan!, result.Reason ?? ""));
                break;
            case OwnClient.Outcome.Stopped:
                _note = () => L.Text("The copy was stopped, and nothing was changed. It goes on from there next time.");
                break;
        }
    }

    /// <summary>This release onto LocalExile's newest, with the steps on this page: the client the player has is newer than it.</summary>
    async Task<bool> UpdateRelease(ClientUpdate.Plan plan)
    {
        _shared.Logs.Write(ClientUpdate.Explain(plan), Severity.Info);
        _headline = () => L.Text("Updating LocalExile");
        Show();
        LocalExileRelease? updated = null;
        string? failure = null;
        bool ran = await _shared.RunExclusiveAsync(L.Text("LocalExile update"), async () =>
        {
            try
            {
                using var http = Updates.Client();
                updated = await ClientUpdate.UpdateReleaseAsync(http, Folder, _shared.Settings.AutomaticBackups, text => Dispatcher.UIThread.Post(() => { _note = () => text; Show(); }));
            }
            catch (Exception e) when (e is System.Net.Http.HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException
                or InvalidOperationException or InvalidDataException or System.Text.Json.JsonException)
            {
                failure = e.Message;
            }
        });
        _headline = null;
        _note = null;
        if (updated is not null)
        {
            _shared.Logs.Write($"LocalExile updated to {updated.Version}", Severity.Info);
            Updated?.Invoke();
            Show();
            return true;
        }
        // Nothing changed: the next Play asks again, rather than going on from the Continue given for this one.
        ClientUpdate.Forget(Folder);
        string reason = !ran ? L.Text("Something else is using LocalExile. Try again when it is done.")
            : failure is not null ? L.Text("The update failed: {0}", failure) + "\n\n" + L.Text("Nothing was changed. Press Play to try again.")
            : L.Text("LocalExile {0} is the newest release.", LocalExileReleases.Installed(Folder)?.ToString() ?? "");
        Dialog.Tell(ViewTools.Owner(this), DialogKind.Warning, L.Text("LocalExile update"), reason);
        Show();
        return false;
    }

    /// <summary>The Control section's Start servers. One LocalExile.exe holds an install, so the game cannot join them later.</summary>
    internal Task StartServersAsync() => RunAsync(["run", "--no-client", "--no-update-check"], false);

    async Task RunAsync(string[] arguments, bool withClient)
    {
        if (_working || _checking || _shared.IsLiveRunning) return;
        _checking = true;
        Show();
        string[]? client;
        try
        {
            client = withClient ? await ClientFits() : [];
            if (client is not null && !await BackUp()) client = null;
        }
        finally { _checking = false; }
        Show();
        if (client is null) return;
        // Another folder's servers on these ports: the same question as a checkout's, instead of a refusal from the run.
        if (await PortCheck.ClearAsync(ViewTools.Owner(this), PortCheck.ServerPorts, [], line => _shared.Logs.Write(line)) != PortAnswer.Free) return;
        if (client.Length > 0)
        {
            // The found client comes in through LocalExile.exe's setup, which starts nothing, so that the game settings can come
            // back before the game starts (ClientUpdate.AfterNewClient).
            if (await RunToolAsync(["setup", "--no-update-check", .. client], false, () => L.Text("Copy the game client")) is not { Code: 0 }) return;
            try { ClientUpdate.AfterNewClient(Folder, client[1]); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _shared.Logs.Write($"after the new client: {e.Message}", Severity.Warning); }
        }
        await RunToolAsync(arguments, withClient, () => withClient ? L.Text("Starting the game") : L.Text("Starting the servers"));
    }

    /// <summary>One run of LocalExile.exe, with its steps on this page; what it answered, or null when it could not run.</summary>
    async Task<LocalExileResult?> RunToolAsync(string[] arguments, bool withClient, Func<string> headline)
    {
        _working = true;
        _failed = false;
        _stopping = false;
        _note = null;
        _withClient = withClient;
        _headline = headline;
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
        return failure is null ? result : null;
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
        _note = ClientExtractionFailure.IsHncFailure(failure ?? result?.Error)
            ? () => ClientExtractionFailure.RecoveryHint
            : () => L.Text("The game didn't start.") + (reason is null ? "" : "\n" + reason);
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
        if (_copyStop is { } copy)
        {
            copy.Cancel();
            return;
        }
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

    void DetailsClicked(object? sender, RoutedEventArgs e) => DetailsRequested?.Invoke();
}
