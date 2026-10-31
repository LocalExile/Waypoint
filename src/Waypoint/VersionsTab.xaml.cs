using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace Waypoint;

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
    public string Action => L.Text(Current ? "Current" : "Switch");
}

sealed record PullRow(PullRequest Pull, bool Current)
{
    public string NumberText => $"#{Pull.Number}";
    public string Title => Pull.Title;
    public string Author => Pull.Author;
    public string Source => L.Text("From {0}", L.Text(Pull.Source));
    public string Updated => VersionsTab.Ago(Pull.Updated);
    public string UpdatedExact => Pull.Updated.LocalDateTime.ToString("g", CultureInfo.CurrentCulture);
    public Visibility DraftBadge => Pull.Draft ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ForkBadge => Pull.FromFork ? Visibility.Visible : Visibility.Collapsed;
    public bool CanSwitch => !Current;
    public string Action => L.Text(Current ? "Current" : "Switch");
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
    int _changes;
    DateTime _fetchedAt = DateTime.MinValue;
    readonly StringBuilder _output = new();
    readonly HotReloadTracker _hotReload = new();

    /// <summary>The status line as a function, so that a language change says it again.</summary>
    Func<string> _state = () => L.Text("Ready.");

    void Say(Func<string> state)
    {
        _state = state;
        RunState.Text = state();
    }


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
            ShowBuild();
        });
        shared.Workspace.Changed += () => Dispatcher.BeginInvoke(ShowBuild);
        L.Current.PropertyChanged += (_, _) =>
        {
            ShowCurrent();
            Show();
            ShowBuild();
            Say(_state);
        };
        ShowBuild();
    }

    /// <summary>What the checkout is and what was built from it; a Gateway running from another commit shows here.</summary>
    void ShowBuild()
    {
        var state = _shared.Workspace.Latest;
        var gateway = state?.Services.FirstOrDefault(s => s.Name == "Gateway");
        var reload = _hotReload.Update(gateway, LogReader.Tail(Path.Combine(Repo.Logs, "live-gateway.log")));
        BuildIdentity.Text = state is null ? "" : $"{state.SourceCommit}\n{string.Join("\n", state.BuildCommit.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))}";
        BuildIdentity.ToolTip = L.Text(reload.Detail);
        HotReloadState.Text = $"{L.Text("Hot reload")}: {L.Text(reload.State)}";
        bool free = !_shared.IsLiveRunning && state is not null;
        BuildButton.IsEnabled = free && !state!.HasServers;
        ReloadDataButton.IsEnabled = free && state!.GameRunning && gateway?.Status == "Ready";
    }

    async void BuildClicked(object sender, RoutedEventArgs e) => await _shared.Workspace.OperateAsync("Build", L.Text("Build checkout"));

    async void ReloadDataClicked(object sender, RoutedEventArgs e) => await _shared.Workspace.OperateAsync("ReloadData", L.Text("Reload game data"));

    async void SwitchNumberClicked(object sender, RoutedEventArgs e)
    {
        if (int.TryParse(Search.Text.Trim().TrimStart('#'), NumberStyles.None, CultureInfo.InvariantCulture, out int number) && number > 0)
            await SwitchAsync(Git.PullRequestBranch(number), $"#{number}", log => Git.SwitchToPullRequestAsync(number, log), _pulls.FirstOrDefault(p => p.Number == number));
    }

    async Task LoadAsync(bool fetch)
    {
        try
        {
            if (fetch)
            {
                bool ran = await _shared.RunGitAsync(async () =>
                {
                    Say(() => L.Text("Fetching …"));
                    var r = await Git.FetchAsync(Log);
                    _fetchedAt = DateTime.UtcNow;
                    Say(() => r.Ok ? L.Text("Ready.") : L.Text("git fetch failed: the lists are what the checkout already knew."));
                });
                if (!ran) Say(() => L.Text("{0} is running; the lists are what the checkout already knew.", _shared.BusyWith));
            }
            await ReadLocalAsync();
            if (_repository is not null)
            {
                try { _pulls = await PullRequests.OpenAsync(_repository, refresh: fetch); }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
                {
                    Log($"pull requests: {e.Message}");
                    Say(() => L.Text("The pull requests could not be read from GitHub (see the output)."));
                }
            }
            Show();
        }
        // No git on PATH, or a checkout git does not know.
        catch (System.ComponentModel.Win32Exception e)
        {
            Say(() => L.Text("git could not run: {0}. Install Git for Windows and start the launcher again.", e.Message));
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
        _changes = (await Git.ChangesAsync()).Count;
        ShowCurrent();
    }

    /// <summary>The header: what the checkout runs, and what its buttons would do.</summary>
    void ShowCurrent()
    {
        int? pull = Git.PullRequestOf(_current);
        string? title = pull is int n ? _pulls.FirstOrDefault(p => p.Number == n)?.Title : null;
        CurrentName.Text = pull is int number
            ? title is null ? L.Text("Pull request #{0}", number) : L.Text("Pull request #{0}: {1}", number, title)
            : _current ?? L.Text("(no branch)");
        CurrentDetail.Text = _changes == 0 ? L.Text("no local changes") : L.Text("{0} uncommitted change(s)", _changes);
        RestoreLink.Visibility = _stashes.Any(s => s.Branch == _current) ? Visibility.Visible : Visibility.Collapsed;
        BackText.Text = L.Text("Back to {0}", _default);
        BackButton.IsEnabled = _current != _default;
        UpdateButton.IsEnabled = _current is not null;
        UpdateButton.ToolTip = pull is int p
            ? L.Text("Fetches pull request #{0} again: its newest commits, also after a force-push", p)
            : L.Text("git pull --ff-only: {0} as origin has it now", _current);
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
        ShowBranches.Content = L.Text("Branches ({0})", _branches.Count);
        ShowPulls.Content = L.Text("Pull requests ({0})", _pulls.Count);
        bool showingPulls = ShowPulls.IsChecked == true;
        int shown = showingPulls ? pulls.Count : branches.Count;
        Empty.Text = q.Length > 0 ? L.Text("Nothing matches the search.") : showingPulls ? L.Text("No open pull requests.") : L.Text("No branches: Fetch reads them.");
        Empty.Visibility = shown == 0 ? Visibility.Visible : Visibility.Collapsed;
        bool byNumber = showingPulls && shown == 0 && int.TryParse(q.TrimStart('#'), NumberStyles.None, CultureInfo.InvariantCulture, out int wanted) && wanted > 0;
        SwitchByNumber.Visibility = byNumber ? Visibility.Visible : Visibility.Collapsed;
        if (byNumber) SwitchByNumber.Content = L.Text("Switch to #{0}", q.TrimStart('#'));
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
        string? branch = _current;
        bool ran = await _shared.RunGitAsync(async () =>
        {
            Say(() => L.Text("Updating {0} …", branch));
            var r = await Git.PullAsync(Log);
            Say(() => r.Ok ? L.Text("{0} is up to date. Restart servers (or Play) builds it.", branch) : L.Text("Not updated: {0}", LastLine(r.Output)));
        });
        if (!ran) Say(() => L.Text("{0} is running; update when it is done.", _shared.BusyWith));
        await ReadLocalAsync();
        Show();
        Switched?.Invoke();
    }

    /// <summary>Switches the checkout to <paramref name="target"/> (a branch name, or pr/&lt;n&gt;) with every guard: the
    /// fork question, the uncommitted changes, the running servers.</summary>
    async Task SwitchAsync(string target, string label, Func<Action<string>, Task<Git.Result>> doSwitch, PullRequest? pull, bool update = false)
    {
        var owner = Window.GetWindow(this);
        if (_shared.IsLiveRunning) { SayBusy(); return; }

        if (pull is { FromFork: true } && !_shared.Settings.TrustedPullRequests.Contains(pull.Number))
        {
            if (!Dialog.Ask(owner, DialogKind.Warning, L.Text("#{0} comes from a fork", pull.Number),
                    L.Text("\"{0}\" comes from {1}: its code is not the LocalExile maintainers'.\n\n" +
                    "Switching to it builds and runs that code on this computer with your rights, live.ps1 and the other " +
                    "scripts included. Run it only if you trust its author or have read the changes (Open on GitHub).", pull.Title, L.Text(pull.Source)),
                    L.Text("Switch to #{0}", pull.Number), cancelByDefault: true)) return;
            _shared.Settings.TrustedPullRequests.Add(pull.Number);
            _shared.Settings.Save();
        }

        var changes = await Git.ChangesAsync();
        bool stash = changes.Count > 0;
        if (stash)
        {
            if (!Dialog.Ask(owner, DialogKind.Question, L.Text("Uncommitted changes"),
                    L.Text("The checkout has {0} uncommitted change(s). They are stashed before the switch and come back " +
                    "with \"Restore stashed changes\" when you return to {1}.", changes.Count, _current ?? L.Text("this commit")),
                    L.Text("Stash and switch"), details: string.Join("\n", changes))) return;
        }

        // A watched Gateway would rebuild halfway through the switch, and every server runs the old build.
        bool running = _shared.Monitor.Latest.Any(s => s.Running);
        if (running)
        {
            if (!Dialog.Ask(owner, DialogKind.Question, L.Text("The servers are running"),
                    update ? L.Text("The servers or the game are running. They stop before the update: a running Gateway would rebuild halfway through it.")
                        : L.Text("The servers or the game are running. They stop before the switch: a running Gateway would rebuild halfway through it."),
                    L.Text(update ? "Stop and update" : "Stop and switch"))) return;
            Say(() => L.Text("Stopping the servers and the game …"));
            int? stopped = await _shared.StopAllAsync(Log);
            if (stopped is null) { SayBusy(); return; }
            // A checkout older than the launcher hooks has a live.ps1 without -Stop: switching to one that has it is the
            // way out, so the player may go on with the servers still running.
            if (stopped != 0 && !Dialog.Ask(owner, DialogKind.Warning, L.Text("The servers did not stop"),
                    L.Text("live.ps1 -Stop failed (exit {0}); the output below says why. This version's live.ps1 may have no " +
                    "-Stop (older than the launcher hooks). Switch anyway, with the servers still running? A watched Gateway " +
                    "may rebuild or restart on its own; Stop all works again on a version that has -Stop.", stopped),
                    L.Text(update ? "Update anyway" : "Switch anyway"), cancelByDefault: true))
            {
                Say(() => L.Text("Not switched: the servers did not stop."));
                return;
            }
        }

        if (_shared.Settings.AutomaticBackups && await _shared.Workspace.OperateAsync("Backup", L.Text("Back up now"), quiet: true) is not { Ok: true }
            && !Dialog.Ask(owner, DialogKind.Warning, L.Text("Switch without a backup?"),
                L.Text("The saves could not be backed up. Automatic backups can be turned off on the Backups section."),
                L.Text(update ? "Update anyway" : "Switch anyway"), cancelByDefault: true))
        {
            Say(() => L.Text("Not switched: no backup."));
            return;
        }

        bool ok = false;
        string? from = _current;
        bool ran = await _shared.RunGitAsync(async () =>
        {
            Say(() => update ? L.Text("Updating {0} …", label) : L.Text("Switching to {0} …", label));
            if (stash)
            {
                var s = await Git.StashAsync(from ?? "detached HEAD", target, Log);
                if (!s.Ok) { Say(() => L.Text("git stash failed, nothing switched: {0}", LastLine(s.Output))); return; }
            }
            var r = await doSwitch(Log);
            ok = r.Ok;
            Say(() => r.Ok ? (update ? L.Text("{0} is up to date.", label) : L.Text("Switched to {0}.", label)) : L.Text("Not switched: {0}", LastLine(r.Output)));
            // The switch failed: the changes just stashed go back where they were.
            if (!r.Ok && stash) await Git.RunAsync(Log, "stash", "pop");
        });
        if (!ran) { SayBusy(); return; }

        await ReadLocalAsync();
        Show();
        Switched?.Invoke();
        if (!ok) return;

        if (!update && FreshSaves.IsChecked == true && await _shared.Workspace.OperateAsync("FreshSaves", L.Text("Start with fresh saves")) is { Ok: true })
            FreshSaves.IsChecked = false;

        if (!update && _stashes.FirstOrDefault(s => s.Branch == _current) is { } back
            && Dialog.Ask(owner, DialogKind.Question, L.Text("Stashed changes"),
                L.Text("You stashed changes when you left {0}. Restore them now?", _current), L.Text("Restore"), L.Text("Not now")))
            await RestoreAsync(back);

        if (Dialog.Ask(owner, DialogKind.Info, update ? L.Text("{0} updated", label) : L.Text("Switched to {0}", label),
                L.Text("Build it and start the servers and the game now?"), L.Text("Play"), L.Text("Later")))
            PlayRequested?.Invoke();
    }

    void SayBusy() => Say(() => L.Text("{0} is running; switch when it is done.", _shared.BusyWith));

    async void RestoreClicked(object sender, RoutedEventArgs e)
    {
        if (_stashes.FirstOrDefault(s => s.Branch == _current) is { } stash) await RestoreAsync(stash);
    }

    async Task RestoreAsync(LauncherStash stash)
    {
        bool ran = await _shared.RunGitAsync(async () =>
        {
            var r = await Git.RestoreAsync(stash, Log);
            Say(() => r.Ok ? L.Text("Stashed changes restored.") : L.Text("Not restored (the stash is kept): {0}", LastLine(r.Output)));
        });
        if (!ran) Say(() => L.Text("{0} is running; restore when it is done.", _shared.BusyWith));
        await ReadLocalAsync();
        Show();
    }

    async void CleanUpClicked(object sender, RoutedEventArgs e)
    {
        var open = _pulls.Select(p => p.Number).ToHashSet();
        var stale = (await Git.LocalBranchesAsync())
            .Where(b => Git.PullRequestOf(b) is int n && !open.Contains(n) && b != _current).Order().ToList();
        if (stale.Count == 0) { Say(() => L.Text("No branches of closed pull requests to delete.")); return; }
        if (!Dialog.Ask(Window.GetWindow(this), DialogKind.Question, L.Text("Clean up"),
                L.Text("Delete the local branches of pull requests that are no longer open?"), L.Text("Delete"),
                details: string.Join("\n", stale))) return;
        await _shared.RunGitAsync(async () =>
        {
            foreach (string b in stale) await Git.DeletePullRequestAsync(Git.PullRequestOf(b)!.Value, Log);
            Say(() => L.Text("Deleted {0} branch(es).", stale.Count));
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
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? L.Text("git failed");

    /// <summary>"5 min ago", "3 h ago", "12 d ago", or the date when older.</summary>
    internal static string Ago(DateTimeOffset at, DateTimeOffset? now = null)
    {
        if (at == default) return "";
        var age = (now ?? DateTimeOffset.Now) - at;
        return age.TotalMinutes < 1 ? L.Text("just now")
            : age.TotalHours < 1 ? L.Text("{0} min ago", (int)age.TotalMinutes)
            : age.TotalHours < 48 ? L.Text("{0} h ago", (int)age.TotalHours)
            : age.TotalDays < 60 ? L.Text("{0} d ago", (int)age.TotalDays)
            : at.LocalDateTime.ToString("d", CultureInfo.CurrentCulture);
    }
}
