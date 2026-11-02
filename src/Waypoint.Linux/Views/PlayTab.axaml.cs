using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Waypoint;

/// <summary>
/// Simple mode's home: Play, and nothing to choose. Play updates a plain copy of LocalExile at most once a day
/// (<see cref="CheckoutUpdates"/>), then starts everything the way the Control section does: through live.ps1. A failed
/// update never stops the player: without a network or a backup the game starts as it is, and an update that does not start
/// goes back to the version before it, saves included.
/// </summary>
partial class PlayTab : UserControl
{
    Shared _shared = null!;
    ControlTab _control = null!;
    bool _working, _failed;
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
    /// <summary>The checkout moved: what reads it reads it again.</summary>
    internal event Action? CheckoutChanged;

    public PlayTab() => InitializeComponent();

    Settings S => _shared.Settings;
    Window? Owner => TopLevel.GetTopLevel(this) as Window;
    bool GameRunning => _shared.Monitor.Latest.Any(s => s.Service == Service.Client && s.Running);
    bool AnythingRunning => _shared.Monitor.Latest.Any(s => s.Running);
    /// <summary>Not known yet: the first inspection of the checkout has not answered.</summary>
    bool Checking => _shared.Workspace.Latest is null && _shared.Workspace.InspectError is null;
    /// <summary>The setup guide is what helps, also when the checkout could not be inspected at all.</summary>
    bool NeedsSetup => _shared.Workspace.Latest is { SetupReady: false } || (_shared.Workspace.Latest is null && _shared.Workspace.InspectError is not null);

    public void Attach(Shared shared, ControlTab control)
    {
        _shared = shared;
        _control = control;
        shared.Monitor.Sampled += _ => Dispatcher.UIThread.Post(Show);
        shared.Workspace.Changed += () => Dispatcher.UIThread.Post(Show);
        shared.LiveRunning += _ => Dispatcher.UIThread.Post(Show);
        L.Current.PropertyChanged += (_, _) => Show();
        Show();
    }

    void Show()
    {
        bool needsSetup = NeedsSetup;
        bool busy = _working || _shared.IsLiveRunning;
        FolderText.Text = Environment.CurrentDirectory;
        Headline.Text = _stage?.Invoke() ?? (GameRunning ? L.Text("The game is running")
            : Checking ? L.Text("Getting ready")
            : needsSetup ? L.Text("LocalExile isn't ready yet")
            : L.Text("Ready to play"));
        MainButton.Content = needsSetup ? L.Text("Finish setup") : L.Text("Play");
        MainButton.IsVisible = !(busy || GameRunning || Checking);
        Working.IsVisible = busy && S.Appearance?.ReduceMotion != true;
        string? note = _note?.Invoke();
        Note.Text = note ?? "";
        Note.IsVisible = note is not null;
        DetailsLink.IsVisible = _failed && !busy;
        StopLink.IsVisible = !busy && AnythingRunning;
        GoBackLink.IsVisible = !busy && !AnythingRunning && !needsSetup && CheckoutUpdates.CanGoBack(S.LastUpdate, DateTimeOffset.UtcNow);
        RemoveOldCard.IsVisible = !busy && !AnythingRunning && Replaced is not null;
        if (Replaced is { } replaced) RemoveOldText.Text = L.Text("Has the new copy worked for you? You can delete your old LocalExile in {0} now.", replaced.Old);
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

    async void MainClicked(object? sender, RoutedEventArgs e)
    {
        if (NeedsSetup) { SetupRequested?.Invoke(); return; }
        await PlayAsync();
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
            if (_failed) _note = () => L.Text("The game didn't start.");
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

    /// <summary>Moves a plain copy to the newest LocalExile when one is due; null when it did not.</summary>
    async Task<CheckoutUpdate?> UpdateAsync()
    {
        var now = DateTimeOffset.UtcNow;
        if (!CheckoutUpdates.Due(S.KeepUpToDate, S.LastUpdateCheck, S.UpdatesPausedUntil, now) || AnythingRunning) return null;
        Say(() => L.Text("Looking for updates"));
        string? branch = null;
        (int Ahead, int Behind)? compared = null;
        bool plain = false;
        await _shared.RunGitAsync(async () =>
        {
            // No network is no reason not to play; Play looks again next time.
            if (!(await Git.FetchAsync(Log)).Ok) return;
            S.LastUpdateCheck = now;
            S.Save();
            branch = Repo.Branch();
            compared = branch is null ? null : await Git.CompareAsync(branch);
            plain = CheckoutUpdates.PlainCopy(branch, await Git.DefaultBranchAsync(), await Git.GitHubRepositoryAsync(), (await Git.ChangesAsync()).Count, compared);
        });
        if (!plain || compared is not (_, > 0)) return null;

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
        S.LastUpdate = new(previous, backup != before ? backup : null, now);
        S.Save();
        CheckoutChanged?.Invoke();
        return S.LastUpdate;
    }

    string? NewestBackup() => _shared.Workspace.Latest is { } state ? BackupCatalog.Read(state.SaveDatabase).FirstOrDefault()?.Id : null;

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

    async void GoBackClicked(object? sender, RoutedEventArgs e)
    {
        if (_working || S.LastUpdate is not { } update) return;
        if (!Dialog.Ask(Owner, DialogKind.Question, L.Text("Go back to the version you had?"),
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

    async void StopClicked(object? sender, RoutedEventArgs e)
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

    async void RemoveOldClicked(object? sender, RoutedEventArgs e)
    {
        if (_working || Replaced is not { } release) return;
        if (!Dialog.Ask(Owner, DialogKind.Warning, L.Text("Delete your old LocalExile?"),
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

    void KeepOldClicked(object? sender, RoutedEventArgs e)
    {
        S.RemoveRelease = null;
        S.Save();
        Show();
    }

    void ChangeFolderClicked(object? sender, RoutedEventArgs e) => ChangeFolderRequested?.Invoke();

    void NewCopyClicked(object? sender, RoutedEventArgs e) => NewCopyRequested?.Invoke();

    void DetailsClicked(object? sender, RoutedEventArgs e) => DetailsRequested?.Invoke();
}
