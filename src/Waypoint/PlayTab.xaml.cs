using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;

namespace Waypoint;

/// <summary>
/// Simple mode's home: Play, and nothing to choose. Play updates a plain copy of LocalExile at most once a day
/// (<see cref="CheckoutUpdates"/>), then starts everything the way the Control section does: through live.ps1. A failed
/// update never stops the player: without a network or a backup the game starts as it is, and an update that does not start
/// goes back to the version before it, saves included.
/// </summary>
public partial class PlayTab : UserControl
{
    Shared _shared = null!;
    ControlTab _control = null!;
    bool _working, _failed;
    /// <summary>How many commits LocalExile's main is ahead of this plain copy, as last looked; null when not known or not one.</summary>
    int? _behind;
    readonly System.Windows.Threading.DispatcherTimer _lookAgain = new() { Interval = TimeSpan.FromMinutes(30) };
    /// <summary>The words shown, as functions, so that a language change says them again.</summary>
    Func<string>? _stage, _note;

    /// <summary>The window runs the setup guide.</summary>
    internal event Action? SetupRequested;
    /// <summary>The window opens the logs.</summary>
    internal event Action? DetailsRequested;
    /// <summary>The player wants another folder.</summary>
    internal event Action? ChangeFolderRequested;
    /// <summary>The player wants a new git copy set up.</summary>
    internal event Action? NewCopyRequested;
    /// <summary>The Latest card's "Use Advanced mode".</summary>
    internal event Action? AdvancedRequested;
    /// <summary>The checkout moved: what reads it reads it again.</summary>
    internal event Action? CheckoutChanged;

    public PlayTab() => InitializeComponent();

    Settings S => _shared.Settings;
    bool GameRunning => _shared.Monitor.Latest.Any(s => s.Service == Service.Client && s.Running);
    bool AnythingRunning => _shared.Monitor.Latest.Any(s => s.Running);
    /// <summary>Not known yet: the first inspection of the checkout has not answered.</summary>
    bool Checking => _shared.Workspace.Latest is null && _shared.Workspace.InspectError is null;
    /// <summary>The setup guide is what helps, also when the checkout could not be inspected at all.</summary>
    bool NeedsSetup => _shared.Workspace.Latest is { SetupReady: false } || (_shared.Workspace.Latest is null && _shared.Workspace.InspectError is not null);

    internal void Attach(Shared shared, ControlTab control)
    {
        _shared = shared;
        if (!Repo.IsInstall) _ = LookForOlderAsync();
        _control = control;
        Offer.Attach(shared.Settings);
        Edition.Attach(shared);
        Profile.Attach(shared);
        shared.Monitor.Sampled += _ => Dispatcher.BeginInvoke(Show);
        shared.Workspace.Changed += () => Dispatcher.BeginInvoke(Show);
        shared.Workspace.Changed += () => Dispatcher.BeginInvoke(async () => await LookForOlderAsync());
        shared.LiveRunning += _ => Dispatcher.BeginInvoke(Show);
        L.Current.PropertyChanged += (_, _) => Show();
        // LocalExile changes many times a day: Play says how far behind it is, without waiting for its daily update.
        _lookAgain.Tick += async (_, _) => await LookForChangesAsync();
        _lookAgain.Start();
        Dispatcher.BeginInvoke(async () => await LookForChangesAsync());
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
        bool needsSetup = NeedsSetup;
        bool busy = _working || _shared.IsLiveRunning;
        // Simple mode plays the release: on a source copy this page asks for the release or Advanced mode instead of Play.
        bool choose = Editions.MustChoose(S);
        FolderText.Text = Environment.CurrentDirectory;
        Headline.Text = _stage?.Invoke() ?? (GameRunning ? L.Text("The game is running")
            : choose ? L.Text("Simple mode plays the release")
            : Checking ? L.Text("Getting ready")
            : needsSetup ? L.Text("LocalExile isn't ready yet")
            : L.Text("Ready to play"));
        MainButton.Content = needsSetup ? L.Text("Finish setup") : L.Text("Play");
        MainButton.Visibility = choose || busy || GameRunning || Checking ? Visibility.Collapsed : Visibility.Visible;
        Working.Visibility = busy && !Theme.Current.ReduceMotion ? Visibility.Visible : Visibility.Collapsed;
        string? note = _note?.Invoke() ?? (_olderBuild is { } older ? ClientUpdate.OlderNoteFor(older) : null);
        Note.Text = note ?? "";
        Note.Visibility = note is null ? Visibility.Collapsed : Visibility.Visible;
        DetailsLink.Visibility = _failed && !busy ? Visibility.Visible : Visibility.Collapsed;
        StopLink.Visibility = !busy && AnythingRunning ? Visibility.Visible : Visibility.Collapsed;
        BehindPanel.Visibility = !choose && !busy && !GameRunning && !needsSetup && _behind is > 0 ? Visibility.Visible : Visibility.Collapsed;
        BehindText.Text = _behind is { } behind ? L.Text("{0} new change(s) in LocalExile since your version.", behind) : "";
        UpdateNowLink.IsEnabled = !AnythingRunning;
        RunSetupLink.IsEnabled = !busy && !AnythingRunning && !Checking;
        GoBackLink.Visibility = !choose && !busy && !AnythingRunning && !needsSetup && CheckoutUpdates.CanGoBack(S.LastUpdate, DateTimeOffset.UtcNow)
            ? Visibility.Visible : Visibility.Collapsed;
        RemoveOldCard.Visibility = !busy && !AnythingRunning && Replaced is not null ? Visibility.Visible : Visibility.Collapsed;
        if (Replaced is { } replaced) RemoveOldText.Text = L.Text("Has the new copy worked for you? You can delete your old LocalExile in {0} now.", replaced.Old);
        // While something runs the choice waits, and "Stop playing" is what shows.
        LatestCard.Visibility = choose && !busy && !AnythingRunning ? Visibility.Visible : Visibility.Collapsed;
        Offer.Show(!busy && !AnythingRunning && !needsSetup && !Checking);
    }

    /// <summary>The release this copy replaces, once the player has played here.</summary>
    ReleaseToRemove? Replaced => S.RemoveRelease is { Played: true } release && IsThisCopy(release) ? release : null;

    static bool IsThisCopy(ReleaseToRemove release) => release.Copy.Equals(Environment.CurrentDirectory, StringComparison.OrdinalIgnoreCase);

    void Say(Func<string> stage)
    {
        _stage = stage;
        Show();
    }

    void Log(string line) => _shared.Logs.Write($"git: {line}");

    async void MainClicked(object sender, RoutedEventArgs e)
    {
        if (NeedsSetup) { SetupRequested?.Invoke(); return; }
        await PlayAsync();
    }

    void RunSetupClicked(object sender, RoutedEventArgs e)
    {
        if (!_working && !_shared.IsLiveRunning && !AnythingRunning && !Checking) SetupRequested?.Invoke();
    }

    async Task PlayAsync()
    {
        if (_working || _shared.IsLiveRunning) return;
        _working = true;
        _failed = false;
        _note = null;
        try
        {
            var update = await UpdateAsync();
            Say(() => L.Text("Starting the game"));
            int? code = await _control.PlayAsync();
            if (update is not null && code is not (null or 0))
            {
                Say(() => L.Text("That update didn't work. Going back to the version you had"));
                await GoBackAsync(update);
                code = await _control.PlayAsync();
                _note = () => L.Text("That update didn't work, so you're on the version you had.");
            }
            else if (update is not null) _note = () => L.Text("LocalExile was updated.");
            _failed = code is not (null or 0);
            if (_failed) _note = _control.ClientSetupFailed
                ? () => ClientExtractionFailure.RecoveryHint
                : () => L.Text("The game didn't start.");
            if (code == 0 && S.RemoveRelease is { Played: false } release && IsThisCopy(release))
            {
                S.RemoveRelease = release with { Played = true };
                S.Save();
            }
        }
        finally
        {
            _working = false;
            _stage = null;
            Show();
        }
    }

    /// <summary>Fetches and compares: the branch, how far it is from LocalExile's, and whether it is a plain copy Play may move.
    /// <paramref name="record"/>: this is Play's daily look, which the next one waits a day after.</summary>
    async Task<(string? Branch, (int Ahead, int Behind)? Compared, bool Plain)> LookAsync(DateTimeOffset now, bool record)
    {
        string? branch = null;
        (int Ahead, int Behind)? compared = null;
        bool plain = false;
        await _shared.RunGitAsync(async () =>
        {
            // No network is no reason not to play; Play looks again next time.
            if (!(await Git.FetchAsync(Log)).Ok) return;
            if (record)
            {
                S.LastUpdateCheck = now;
                S.Save();
            }
            branch = Repo.Branch();
            compared = branch is null ? null : await Git.CompareAsync(branch);
            plain = CheckoutUpdates.PlainCopy(branch, await Git.DefaultBranchAsync(), await Git.GitHubRepositoryAsync(), (await Git.ChangesAsync()).Count, compared);
        });
        _behind = plain ? compared?.Behind : null;
        return (branch, compared, plain);
    }

    /// <summary>The indicator's look, every half hour and when Play opens; never while something runs or holds the checkout.</summary>
    async Task LookForChangesAsync()
    {
        if (Repo.IsInstall || _working || _shared.IsLiveRunning || AnythingRunning || NeedsSetup) return;
        await LookAsync(DateTimeOffset.UtcNow, record: false);
        Show();
    }

    /// <summary>Moves a plain copy to the newest LocalExile when one is due, or at once when the player asked
    /// (<paramref name="now"/>, Update now); null when it did not.</summary>
    async Task<CheckoutUpdate?> UpdateAsync(bool now = false)
    {
        var at = DateTimeOffset.UtcNow;
        if ((!now && !CheckoutUpdates.Due(S.KeepUpToDate, S.LastUpdateCheck, S.UpdatesPausedUntil, at)) || AnythingRunning) return null;
        Say(() => L.Text("Looking for updates"));
        var (branch, compared, plain) = await LookAsync(at, record: true);
        if (!plain || compared is not (_, > 0)) return null;
        if (!await ClientAllowsUpdateAsync(branch!)) return null;

        string? before = NewestBackup();
        if (S.AutomaticBackups)
        {
            Say(() => L.Text("Backing up your saves"));
            if (await _shared.Workspace.OperateAsync("Backup", L.Text("Back up now"), quiet: true) is not { Ok: true }) return null;
        }
        string? backup = NewestBackup();

        Say(() => L.Text("Getting the latest update"));
        if (await Git.HeadAsync() is not { } previous) return null;
        bool moved = false;
        await _shared.RunGitAsync(async () => moved = (await Git.MoveForwardAsync(branch!, Log)).Ok);
        if (!moved) return null;
        _behind = 0;
        S.LastUpdate = new(previous, backup != before ? backup : null, at);
        S.Save();
        CheckoutChanged?.Invoke();
        return S.LastUpdate;
    }

    /// <summary>A newer LocalExile may need another client build (<see cref="ClientUpdate.BeforeUpdate"/>): settled before the copy
    /// moves, so that a Not now, or a player with only the older client, keeps the version that works.</summary>
    async Task<bool> ClientAllowsUpdateAsync(string branch)
    {
        string folder = Environment.CurrentDirectory;
        string? next = ClientLocator.NeededIn(await Git.FileAtSourceAsync(branch, "scripts/release/build-release.ps1"));
        var (go, ask, staying) = await Task.Run(() => ClientUpdate.BeforeUpdate(folder, next));
        if (ask is not null)
        {
            var owner = Window.GetWindow(this);
            go = ClientUpdate.AskMove(ask, (title, message, details, buttons) => Dialog.Show(owner, DialogKind.Question, title, message, details, buttons), path => Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { path }, UseShellExecute = false })?.Dispose());
            staying = ClientUpdate.Answer(ask, go);
        }
        if (staying is not null)
        {
            _shared.Logs.Write(staying, Severity.Info);
            _note = () => staying;
        }
        return go;
    }

    string? NewestBackup() => _shared.Workspace.Latest is { } state ? BackupCatalog.Read(state).FirstOrDefault()?.Id : null;

    /// <summary>The checkout and the saves as they were before <paramref name="update"/>. The saves first: restoring keeps
    /// a copy of the current ones, and that needs nothing of the newer version.</summary>
    async Task<bool> GoBackAsync(CheckoutUpdate update)
    {
        await _shared.StopAllAsync(Log);
        if (update.Backup is { } backup && await _shared.Workspace.OperateAsync("RestoreBackup", L.Text("Restore saves"), backupId: backup, quiet: true) is not { Ok: true })
            return false;
        bool back = false;
        await _shared.RunGitAsync(async () => back = (await Git.ResetToAsync(update.Commit, Log)).Ok);
        if (!back) return false;
        S.LastUpdate = null;
        S.Save();
        CheckoutChanged?.Invoke();
        return true;
    }

    async void GoBackClicked(object sender, RoutedEventArgs e)
    {
        if (_working || S.LastUpdate is not { } update) return;
        if (!Dialog.Ask(Window.GetWindow(this), DialogKind.Question, L.Text("Go back to the version you had?"),
                L.Text("LocalExile and your saves go back to how they were before the update. Your progress since then is kept in a backup. Updates pause for a week."),
                L.Text("Go back"))) return;
        _working = true;
        _failed = false;
        _note = null;
        Say(() => L.Text("Going back to the version you had"));
        try
        {
            if (await GoBackAsync(update))
            {
                S.UpdatesPausedUntil = DateTimeOffset.UtcNow + CheckoutUpdates.Week;
                S.Save();
                _note = () => L.Text("You're back on the version you had.");
            }
            else
            {
                _failed = true;
                _note = () => L.Text("Waypoint couldn't go back to that version.");
            }
        }
        finally
        {
            _working = false;
            _stage = null;
            Show();
        }
    }

    /// <summary>The update Play makes once a day, now: saves backed up, the checkout moved forward, the way back kept. The game is
    /// not started.</summary>
    async void UpdateNowClicked(object sender, RoutedEventArgs e)
    {
        if (_working || _shared.IsLiveRunning || AnythingRunning) return;
        _working = true;
        _failed = false;
        _note = null;
        try
        {
            var update = await UpdateAsync(now: true);
            _note = update is not null ? () => L.Text("LocalExile was updated. Press Play to start it.")
                : _behind is > 0 ? () => L.Text("LocalExile couldn't be updated now. Play starts the version you have.")
                : () => L.Text("LocalExile is up to date.");
        }
        finally
        {
            _working = false;
            _stage = null;
            Show();
        }
    }

    async void StopClicked(object sender, RoutedEventArgs e)
    {
        if (_working) return;
        _working = true;
        _note = null;
        Say(() => L.Text("Stopping"));
        try { await _shared.StopAllAsync(Log); }
        finally
        {
            _working = false;
            _stage = null;
            Show();
        }
    }

    async void RemoveOldClicked(object sender, RoutedEventArgs e)
    {
        if (_working || Replaced is not { } release) return;
        if (!Dialog.Ask(Window.GetWindow(this), DialogKind.Warning, L.Text("Delete your old LocalExile?"),
                L.Text("This deletes the program files in {0}, and cannot be undone. Your old saves and mods stay there until you delete them yourself. A game client this copy uses moves here first when it is on the same drive.", release.Old),
                L.Text("Delete"))) return;
        _working = true;
        _failed = false;
        _note = null;
        Say(() => L.Text("Deleting your old LocalExile"));
        try
        {
            var removed = await Task.Run(() => SwitchToSource.RemoveRelease(release.Old, Environment.CurrentDirectory));
            S.RemoveRelease = null;
            S.Save();
            _note = removed.ClientMoved ? () => L.Text("Your old LocalExile is deleted, and the game client moved here. Your old saves are still in {0}.", Path.Combine(release.Old, "data"))
                : () => L.Text("Your old LocalExile is deleted. Your old saves are still in {0}.", Path.Combine(release.Old, "data"));
            CheckoutChanged?.Invoke();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _failed = true;
            _note = () => L.Text("Waypoint couldn't delete your old LocalExile: {0}", ex.Message);
        }
        finally
        {
            _working = false;
            _stage = null;
            Show();
        }
    }

    void KeepOldClicked(object sender, RoutedEventArgs e)
    {
        S.RemoveRelease = null;
        S.Save();
        Show();
    }

    void ChangeFolderClicked(object sender, RoutedEventArgs e) => ChangeFolderRequested?.Invoke();

    void NewCopyClicked(object sender, RoutedEventArgs e) => NewCopyRequested?.Invoke();

    /// <summary>As the release's view: the switch again when Developer Mode changes.</summary>
    internal void ShowOffers() => Edition.Refresh();

    // The choice in place of Play (Editions.MustChoose). The release is the switch's own way there, with its question about characters.
    void GuideReleaseClicked(object sender, RoutedEventArgs e) => Edition.Choose(release: true);

    void GuideAdvancedClicked(object sender, RoutedEventArgs e) => AdvancedRequested?.Invoke();

    void DetailsClicked(object sender, RoutedEventArgs e) => DetailsRequested?.Invoke();
}
