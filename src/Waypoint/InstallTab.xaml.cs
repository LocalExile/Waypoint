using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace Waypoint;

/// <summary>
/// A release install's home. Play is one run of <c>LocalExile.exe --json</c>, which prepares what it must, starts the servers and
/// the client, and stays alive until the client closes. It holds the install's lock only until the game is ready, so that the
/// window can close or hide while the game runs. A question it stops with (no client, no account) goes to the setup guide.
/// </summary>
public partial class InstallTab : UserControl
{
    Shared _shared = null!;
    bool _working, _checking, _stopping, _failed, _needsSetup, _withClient = true;
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
    /// <summary>The player has only an older client: the window sets up the last LocalExile made for it, in a new folder.</summary>
    internal event Action? OlderReleaseRequested;
    /// <summary>This release was updated from here: the window drops its update card.</summary>
    internal event Action? Updated;
    /// <summary>The window opens the logs.</summary>
    internal event Action? DetailsRequested;
    /// <summary>The player wants another folder.</summary>
    internal event Action? ChangeFolderRequested;
    /// <summary>The player wants a new git copy set up.</summary>
    internal event Action? NewCopyRequested;

    public InstallTab() => InitializeComponent();

    bool GameRunning => _shared.Monitor.Latest.Any(s => s.Service == Service.Client && s.Running);
    bool AnythingRunning => _shared.Monitor.Latest.Any(s => s.Running);
    static string Folder => Environment.CurrentDirectory;

    internal void Attach(Shared shared)
    {
        _shared = shared;
        if (!Repo.IsInstall) return;
        _ = LookForOlderAsync();
        Offer.Attach(shared.Settings);
        Edition.Attach(shared);
        ShowOffers();
        Profile.Attach(shared);
        StepList.ItemsSource = _steps;
        shared.Monitor.Sampled += _ => Dispatcher.BeginInvoke(Show);
        shared.LiveRunning += _ => Dispatcher.BeginInvoke(Show);
        L.Current.PropertyChanged += (_, _) => Show();
        Show();
    }

    /// <summary>Something changed the install from outside (the setup guide, an update). A source checkout has no Install
    /// section: nothing of it is attached, so there is nothing to show.</summary>
    internal void InstallChanged(Func<string>? note = null)
    {
        if (!Repo.IsInstall) return;
        _ = LookForOlderAsync();
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
        string? need = ClientLocator.Needed(folder);
        // Looked for again only when the folder's LocalExile changed (an update, a move to another version).
        if (need is not null && need == _olderLookedFor) return;
        _olderLookedFor = need;
        _olderBuild = need is not null && ClientLocator.IsOlder(need, ClientLocator.Build)
            && await Task.Run(() => ClientUpdate.FindClient(ClientLocator.Build, default)) is null ? need : null;
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
        MainButton.Visibility = busy || GameRunning || AnythingRunning ? Visibility.Collapsed : Visibility.Visible;
        Working.Visibility = busy && (!Theme.Current.ReduceMotion || _total is > 0) ? Visibility.Visible : Visibility.Collapsed;
        Working.IsIndeterminate = _total is not > 0;
        if (_total is > 0 and { } total) Working.Value = 100d * (_current ?? 0) / total;
        string? note = _note?.Invoke() ?? (_olderBuild is { } older ? ClientUpdate.OlderNoteFor(older) : null);
        Note.Text = note ?? "";
        Note.Visibility = note is null ? Visibility.Collapsed : Visibility.Visible;
        DetailsLink.Visibility = _failed && !busy ? Visibility.Visible : Visibility.Collapsed;
        // A step that writes the client or the keys is not cut short.
        StopLink.Visibility = !_stopping && !LocalExileReleases.MustFinish(_stage) && (_working || AnythingRunning) ? Visibility.Visible : Visibility.Collapsed;
        FolderText.Text = Folder;
        Elapsed.Text = _working ? L.Text("Running for {0}", _clock.Elapsed.ToString(@"m\:ss")) : "";
        VersionText.Text = LocalExileReleases.Installed(Folder) is { } version ? L.Text("LocalExile {0}", version) : "";
        Offer.Show(!busy && !AnythingRunning && !needsSetup);
        string? from = _bringLater ? null : Editions.BringFrom(_shared.Settings);
        BringCard.Visibility = from is not null && !busy && !AnythingRunning && !needsSetup ? Visibility.Visible : Visibility.Collapsed;
        if (from is not null) BringText.Text = L.Text("Your characters are still in {0}. Waypoint can bring them here when this release is the same as or newer than that copy.", from);
    }

    /// <summary>"Not now" on the characters card: until Waypoint starts again.</summary>
    bool _bringLater;

    async void BringClicked(object sender, RoutedEventArgs e)
    {
        var owner = Window.GetWindow(this);
        bool brought = await Editions.BringToReleaseAsync(_shared, (kind, title, message, buttons) => Dialog.Show(owner, kind, title, message, null, buttons));
        if (brought) _note = () => L.Text("Your characters came with you. Log in as usual.");
        Show();
    }

    void BringLaterClicked(object sender, RoutedEventArgs e)
    {
        _bringLater = true;
        Show();
    }

    void MainClicked(object sender, RoutedEventArgs e)
    {
        if (_needsSetup || !LocalExileReleases.IsSetUp(Folder)) { SetupRequested?.Invoke(); return; }
        _ = PlayAsync();
    }

    void RunSetupClicked(object sender, RoutedEventArgs e)
    {
        if (!_working && !_checking && !_shared.IsLiveRunning && !AnythingRunning) SetupRequested?.Invoke();
    }

    internal Task PlayAsync() => RunAsync(["run", "--no-update-check"], true);

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
        Activity.Visibility = Visibility.Visible;
        _clock.Restart();
        Show();
        var ready = new TaskCompletionSource();
        LocalExileResult? result = null;
        string? failure = null;
        try
        {
            // Queued: the events come from the tool's threads.
            var run = LocalExileTool.RunAsync(Folder, arguments, item => Dispatcher.BeginInvoke(() => Event(item, ready)), line => Dispatcher.BeginInvoke(() =>
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
    /// <summary>A run with a client of another build than this release needs would fail at the login (<see cref="ClientUpdate"/>).
    /// A player with a client of the current build is moved to it without a question: this release is updated to the newest
    /// (saves copied first), an older client is taken out, and LocalExile.exe is given the found one; the arguments to add, or
    /// null when nothing starts. Only a player who has nothing newer than an older client is asked, about the older version.</summary>
    async Task<string[]?> ClientFits(bool again = false)
    {
        var owner = Window.GetWindow(this);
        var (outcome, plan) = await ClientUpdate.EnsureAsync(Folder, lookForNewer: true,
            p => ClientUpdate.AskMove(p, (title, message, details, buttons) => Dialog.Show(owner, DialogKind.Question, title, message, details, buttons), path => Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { path }, UseShellExecute = false })?.Dispose()),
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
                updated = await ClientUpdate.UpdateReleaseAsync(http, Folder, _shared.Settings.AutomaticBackups, text => Dispatcher.BeginInvoke(() => { _note = () => text; Show(); }));
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
        Dialog.Tell(Window.GetWindow(this), DialogKind.Warning, L.Text("LocalExile update"), reason);
        Show();
        return false;
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
        return result is { Ok: true } || Dialog.Ask(Window.GetWindow(this), DialogKind.Warning, L.Text("Start without a backup?"),
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

    async void StopClicked(object sender, RoutedEventArgs e)
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

    void ChangeFolderClicked(object sender, RoutedEventArgs e) => ChangeFolderRequested?.Invoke();

    void NewCopyClicked(object sender, RoutedEventArgs e) => NewCopyRequested?.Invoke();

    /// <summary>The Latest side's two offers, the switch and the copy from source, only for those who asked
    /// (<see cref="Editions.Offered"/>). The setup guide below stays for everyone.</summary>
    internal void ShowOffers()
    {
        if (_shared is null) return;
        Edition.Refresh();
        NewCopyLink.Visibility = Editions.Offered(_shared.Settings) ? Visibility.Visible : Visibility.Collapsed;
    }

    void DetailsClicked(object sender, RoutedEventArgs e) => DetailsRequested?.Invoke();
}
