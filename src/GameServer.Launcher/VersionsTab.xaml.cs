using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace GameServer.Launcher;

sealed record BranchRow(RemoteBranch Branch, bool Current, bool IsDefault)
{
    public string Name => Branch.Name;
    public string Subject => Branch.Subject;
    public string Author => Branch.Author;
    public string Updated => VersionsTab.Ago(Branch.Updated);
    public string UpdatedExact => Branch.Updated.LocalDateTime.ToString("g", CultureInfo.CurrentCulture);
    public Visibility DefaultBadge => IsDefault ? Visibility.Visible : Visibility.Collapsed;
    public Visibility LocalBadge => Branch.LocalOnly ? Visibility.Visible : Visibility.Collapsed;
    public bool CanSwitch => !Current;
    public string Action => Current ? "Current" : "Switch";
}

sealed record PullRow(PullRequest Pull, bool Current)
{
    public string NumberText => $"#{Pull.Number}";
    public string Title => Pull.Title;
    public string Author => Pull.Author;
    public string Source => $"From {Pull.Source}";
    public string Updated => VersionsTab.Ago(Pull.Updated);
    public string UpdatedExact => Pull.Updated.LocalDateTime.ToString("g", CultureInfo.CurrentCulture);
    public Visibility DraftBadge => Pull.Draft ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ForkBadge => Pull.FromFork ? Visibility.Visible : Visibility.Collapsed;
    public bool CanSwitch => !Current;
    public string Action => Current ? "Current" : "Switch";
}

/// <summary>
/// The Versions section: what the server checkout runs (a branch of origin, or an open pull request fetched into
/// <c>pr/&lt;n&gt;</c>) and switching it. A switch never loses work: uncommitted changes are stashed (and offered back on
/// return), running servers and the game are stopped first, a pull request from a fork is run only after the player
/// agreed to, once per pull request. Everything is git in the checkout and GitHub's public API; nothing in the server.
/// </summary>
public partial class VersionsTab : UserControl
{
    Shared _shared = null!;
    string? _repository;
    string _default = "main";
    string? _current;
    List<RemoteBranch> _branches = [];
    List<PullRequest> _pulls = [];
    List<LauncherStash> _stashes = [];
    DateTime _fetchedAt = DateTime.MinValue;
    readonly StringBuilder _output = new();

    /// <summary>The checkout changed branch: the header and the setup checks read it again.</summary>
    internal event Action? Switched;
    /// <summary>The player wants the new version built and started (Play on the Control section).</summary>
    internal event Action? PlayRequested;

    public VersionsTab() => InitializeComponent();

    internal void Attach(Shared shared)
    {
        _shared = shared;
        // Fetching reaches the network: on the first showing, then when the lists are five minutes old.
        IsVisibleChanged += async (_, e) =>
        {
            if ((bool)e.NewValue) await LoadAsync(fetch: DateTime.UtcNow - _fetchedAt > TimeSpan.FromMinutes(5));
        };
        shared.LiveRunning += running => Dispatcher.BeginInvoke(() =>
        {
            Toolbar.IsEnabled = !running;
            BranchList.IsEnabled = PullList.IsEnabled = !running;
        });
    }

    async Task LoadAsync(bool fetch)
    {
        try
        {
            if (fetch)
            {
                bool ran = await _shared.RunGitAsync(async () =>
                {
                    RunState.Text = "Fetching …";
                    var r = await Git.FetchAsync(Log);
                    _fetchedAt = DateTime.UtcNow;
                    RunState.Text = r.Ok ? "Ready." : "git fetch failed: the lists are what the checkout already knew.";
                });
                if (!ran) RunState.Text = $"{_shared.BusyWith} is running; the lists are what the checkout already knew.";
            }
            await ReadLocalAsync();
            if (_repository is not null)
            {
                try { _pulls = await PullRequests.OpenAsync(_repository, refresh: fetch); }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
                {
                    Log($"pull requests: {e.Message}");
                    RunState.Text = "The pull requests could not be read from GitHub (see the output).";
                }
            }
            Show();
        }
        // No git on PATH, or a checkout git does not know.
        catch (System.ComponentModel.Win32Exception e)
        {
            RunState.Text = $"git could not run: {e.Message}. Install Git for Windows and start the launcher again.";
        }
    }

    /// <summary>The checkout's state: its branch, its changes, its stashes, origin's branches.</summary>
    async Task ReadLocalAsync()
    {
        _current = Repo.Branch();
        _repository ??= await Git.GitHubRepositoryAsync();
        _default = await Git.DefaultBranchAsync();
        _branches = await Git.BranchesAsync();
        _stashes = await Git.StashesAsync();
        var changes = await Git.ChangesAsync();

        int? pull = Git.PullRequestOf(_current);
        string? title = pull is int n ? _pulls.FirstOrDefault(p => p.Number == n)?.Title : null;
        CurrentName.Text = pull is int number ? $"Pull request #{number}" + (title is null ? "" : $": {title}") : _current ?? "(no branch)";
        CurrentDetail.Text = changes.Count == 0 ? "no local changes" : $"{changes.Count} uncommitted change(s)";
        RestoreLink.Visibility = _stashes.Any(s => s.Branch == _current) ? Visibility.Visible : Visibility.Collapsed;
        BackText.Text = $"Back to {_default}";
        BackButton.IsEnabled = _current != _default;
        UpdateButton.IsEnabled = _current is not null;
        UpdateButton.ToolTip = pull is int p
            ? $"Fetches pull request #{p} again: its newest commits, also after a force-push"
            : $"git pull --ff-only: {_current} as origin has it now";
    }

    void Show()
    {
        string q = Search.Text.Trim();
        bool Has(string? s) => q.Length == 0 || s?.Contains(q, StringComparison.OrdinalIgnoreCase) == true;
        int? currentPull = Git.PullRequestOf(_current);
        var branches = _branches.Where(b => Has(b.Name) || Has(b.Subject) || Has(b.Author))
            .Select(b => new BranchRow(b, b.Name == _current, b.Name == _default))
            // The default branch, then this checkout's own branches (what one works on), then origin's, newest first.
            .OrderByDescending(r => r.IsDefault).ThenByDescending(r => r.Branch.LocalOnly).ToList();
        var pulls = _pulls.Where(p => Has(p.Title) || Has(p.Author) || Has($"#{p.Number}") || Has(p.Number.ToString(CultureInfo.InvariantCulture)))
            .Select(p => new PullRow(p, p.Number == currentPull)).ToList();
        BranchList.ItemsSource = branches;
        PullList.ItemsSource = pulls;
        ShowBranches.Content = $"Branches ({_branches.Count})";
        ShowPulls.Content = $"Pull requests ({_pulls.Count})";
        bool showingPulls = ShowPulls.IsChecked == true;
        int shown = showingPulls ? pulls.Count : branches.Count;
        Empty.Text = q.Length > 0 ? "Nothing matches the search." : showingPulls ? "No open pull requests." : "No branches: Fetch reads them.";
        Empty.Visibility = shown == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void ListChanged(object sender, RoutedEventArgs e)
    {
        if (BranchList is null) return;
        bool pulls = ShowPulls.IsChecked == true;
        BranchHeader.Visibility = BranchList.Visibility = pulls ? Visibility.Collapsed : Visibility.Visible;
        PullHeader.Visibility = PullList.Visibility = pulls ? Visibility.Visible : Visibility.Collapsed;
        Show();
    }

    void SearchChanged(object sender, TextChangedEventArgs e)
    {
        if (_shared is not null) Show();
    }

    async void RefreshClicked(object sender, RoutedEventArgs e) => await LoadAsync(fetch: true);

    async void SwitchBranch(object sender, RoutedEventArgs e)
    {
        var row = (BranchRow)((FrameworkElement)sender).DataContext;
        await SwitchAsync(row.Name, row.Name, log => Git.SwitchToBranchAsync(row.Name, log), null);
    }

    async void SwitchPull(object sender, RoutedEventArgs e)
    {
        var pull = ((PullRow)((FrameworkElement)sender).DataContext).Pull;
        await SwitchAsync(Git.PullRequestBranch(pull.Number), $"#{pull.Number}", log => Git.SwitchToPullRequestAsync(pull.Number, log), pull);
    }

    async void BackClicked(object sender, RoutedEventArgs e) =>
        await SwitchAsync(_default, _default, log => Git.SwitchToBranchAsync(_default, log), null);

    async void UpdateClicked(object sender, RoutedEventArgs e)
    {
        if (Git.PullRequestOf(_current) is int n)
        {
            var pull = _pulls.FirstOrDefault(p => p.Number == n);
            await SwitchAsync(Git.PullRequestBranch(n), $"#{n}", log => Git.SwitchToPullRequestAsync(n, log), pull, update: true);
            return;
        }
        bool ran = await _shared.RunGitAsync(async () =>
        {
            RunState.Text = $"Updating {_current} …";
            var r = await Git.PullAsync(Log);
            RunState.Text = r.Ok ? $"{_current} is up to date. Restart servers (or Play) builds it." : $"Not updated: {LastLine(r.Output)}";
        });
        if (!ran) RunState.Text = $"{_shared.BusyWith} is running; update when it is done.";
        await ReadLocalAsync();
        Show();
        Switched?.Invoke();
    }

    /// <summary>Switches the checkout to <paramref name="target"/> (a branch name, or pr/&lt;n&gt;) with every guard: the
    /// fork question, the uncommitted changes, the running servers.</summary>
    async Task SwitchAsync(string target, string label, Func<Action<string>, Task<Git.Result>> doSwitch, PullRequest? pull, bool update = false)
    {
        var owner = Window.GetWindow(this);
        if (_shared.IsLiveRunning) { RunState.Text = $"{_shared.BusyWith} is running; switch when it is done."; return; }

        if (pull is { FromFork: true } && !_shared.Settings.TrustedPullRequests.Contains(pull.Number))
        {
            if (!Dialog.Ask(owner, DialogKind.Warning, $"#{pull.Number} comes from a fork",
                    $"\"{pull.Title}\" comes from {pull.Source}: its code is not the LocalExile maintainers'.\n\n" +
                    "Switching to it builds and runs that code on this computer with your rights, live.ps1 and the other " +
                    "scripts included. Run it only if you trust its author or have read the changes (Open on GitHub).",
                    $"Switch to #{pull.Number}", cancelByDefault: true)) return;
            _shared.Settings.TrustedPullRequests.Add(pull.Number);
            _shared.Settings.Save();
        }

        var changes = await Git.ChangesAsync();
        bool stash = changes.Count > 0;
        if (stash)
        {
            if (!Dialog.Ask(owner, DialogKind.Question, "Uncommitted changes",
                    $"The checkout has {changes.Count} uncommitted change(s). They are stashed before the switch and come back " +
                    $"with \"Restore stashed changes\" when you return to {_current ?? "this commit"}.",
                    "Stash and switch", details: string.Join("\n", changes))) return;
        }

        // A watched Gateway would rebuild halfway through the switch, and every server runs the old build.
        bool running = _shared.Monitor.Latest.Any(s => s.Running);
        if (running)
        {
            if (!Dialog.Ask(owner, DialogKind.Question, "The servers are running",
                    $"The servers or the game are running. They stop before the {(update ? "update" : "switch")}: a running " +
                    "Gateway would rebuild halfway through it.",
                    update ? "Stop and update" : "Stop and switch")) return;
            RunState.Text = "Stopping the servers and the game …";
            int? stopped = await _shared.StopAllAsync(Log);
            if (stopped is null) { RunState.Text = $"{_shared.BusyWith} is running; switch when it is done."; return; }
            // A checkout older than the launcher hooks has a live.ps1 without -Stop: switching to one that has it is the
            // way out, so the player may go on with the servers still running.
            if (stopped != 0 && !Dialog.Ask(owner, DialogKind.Warning, "The servers did not stop",
                    $"live.ps1 -Stop failed (exit {stopped}); the output below says why. This version's live.ps1 may have no " +
                    "-Stop (older than the launcher hooks). Switch anyway, with the servers still running? A watched Gateway " +
                    "may rebuild or restart on its own; Stop all works again on a version that has -Stop.",
                    update ? "Update anyway" : "Switch anyway", cancelByDefault: true))
            {
                RunState.Text = "Not switched: the servers did not stop.";
                return;
            }
        }

        bool ok = false;
        string? from = _current;
        bool ran = await _shared.RunGitAsync(async () =>
        {
            RunState.Text = update ? $"Updating {label} …" : $"Switching to {label} …";
            if (stash)
            {
                var s = await Git.StashAsync(from ?? "detached HEAD", target, Log);
                if (!s.Ok) { RunState.Text = $"git stash failed, nothing switched: {LastLine(s.Output)}"; return; }
            }
            var r = await doSwitch(Log);
            ok = r.Ok;
            RunState.Text = r.Ok ? (update ? $"{label} is up to date." : $"Switched to {label}.") : $"Not switched: {LastLine(r.Output)}";
            // The switch failed: the changes just stashed go back where they were.
            if (!r.Ok && stash) await Git.RunAsync(Log, "stash", "pop");
        });
        if (!ran) { RunState.Text = $"{_shared.BusyWith} is running; switch when it is done."; return; }

        await ReadLocalAsync();
        Show();
        Switched?.Invoke();
        if (!ok) return;

        if (!update && _stashes.FirstOrDefault(s => s.Branch == _current) is { } back
            && Dialog.Ask(owner, DialogKind.Question, "Stashed changes",
                $"You stashed changes when you left {_current}. Restore them now?", "Restore", "Not now"))
            await RestoreAsync(back);

        if (Dialog.Ask(owner, DialogKind.Info, update ? $"{label} updated" : $"Switched to {label}",
                "Build it and start the servers and the game now?", "Play", "Later"))
            PlayRequested?.Invoke();
    }

    async void RestoreClicked(object sender, RoutedEventArgs e)
    {
        if (_stashes.FirstOrDefault(s => s.Branch == _current) is { } stash) await RestoreAsync(stash);
    }

    async Task RestoreAsync(LauncherStash stash)
    {
        bool ran = await _shared.RunGitAsync(async () =>
        {
            var r = await Git.RestoreAsync(stash, Log);
            RunState.Text = r.Ok ? "Stashed changes restored." : $"Not restored (the stash is kept): {LastLine(r.Output)}";
        });
        if (!ran) RunState.Text = $"{_shared.BusyWith} is running; restore when it is done.";
        await ReadLocalAsync();
        Show();
    }

    async void CleanUpClicked(object sender, RoutedEventArgs e)
    {
        var open = _pulls.Select(p => p.Number).ToHashSet();
        var stale = (await Git.LocalBranchesAsync())
            .Where(b => Git.PullRequestOf(b) is int n && !open.Contains(n) && b != _current).Order().ToList();
        if (stale.Count == 0) { RunState.Text = "No branches of closed pull requests to delete."; return; }
        if (!Dialog.Ask(Window.GetWindow(this), DialogKind.Question, "Clean up",
                "Delete the local branches of pull requests that are no longer open?", "Delete",
                details: string.Join("\n", stale))) return;
        await _shared.RunGitAsync(async () =>
        {
            foreach (string b in stale) await Git.DeletePullRequestAsync(Git.PullRequestOf(b)!.Value, Log);
            RunState.Text = $"Deleted {stale.Count} branch(es).";
        });
    }

    void OpenPull(object sender, RoutedEventArgs e)
    {
        string page = ((PullRow)((FrameworkElement)sender).DataContext).Pull.Page;
        if (page.Length > 0) Process.Start(new ProcessStartInfo(page) { UseShellExecute = true })?.Dispose();
    }

    void Log(string line)
    {
        _shared.Logs.Write($"git: {line}");
        Dispatcher.BeginInvoke(() =>
        {
            _output.AppendLine(line);
            Output.Text = _output.ToString();
            Output.ScrollToEnd();
        });
    }

    void ClearOutput(object sender, RoutedEventArgs e)
    {
        _output.Clear();
        Output.Clear();
    }

    static string LastLine(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "git failed";

    /// <summary>"5 min ago", "3 h ago", "12 d ago", or the date when older.</summary>
    internal static string Ago(DateTimeOffset at, DateTimeOffset? now = null)
    {
        if (at == default) return "";
        var age = (now ?? DateTimeOffset.Now) - at;
        return age.TotalMinutes < 1 ? "just now"
            : age.TotalHours < 1 ? $"{(int)age.TotalMinutes} min ago"
            : age.TotalHours < 48 ? $"{(int)age.TotalHours} h ago"
            : age.TotalDays < 60 ? $"{(int)age.TotalDays} d ago"
            : at.LocalDateTime.ToString("d", CultureInfo.CurrentCulture);
    }
}
