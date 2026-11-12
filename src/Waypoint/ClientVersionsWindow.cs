using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;

namespace Waypoint;

/// <summary>
/// The versions of the game a release holds (<see cref="ClientVersions"/>): the one in use, those set aside, and those its
/// LocalExile lists that Steam can deliver (<see cref="LocalExileBuilds"/>, <see cref="SteamDownload"/>). A version from
/// Steam needs a Steam sign-in once, by the tool's own QR code (<see cref="SteamSignInWindow"/>) or, without the Steam
/// mobile app, by its own password prompt in a console window. With a client already there, how to move is asked each
/// time (update the one client where it is, or keep both) unless Settings names one. Nothing here runs while the game
/// or a server runs, and LocalExile's own setup prepares a client that just came in.
/// </summary>
sealed class ClientVersionsWindow : FormWindow
{
    /// <summary>A line of the list: the client in use, one set aside, or a version Steam can deliver.</summary>
    /// <param name="Detail">Under the list while the line is chosen: which build it is in LocalExile's own words, and where.</param>
    sealed record Row(string Text, string Detail, bool InUse = false, KeptClient? Kept = null, ListedBuild? Build = null);

    readonly Shared _shared;
    readonly string _folder = Environment.CurrentDirectory;
    readonly ListBox _list = new() { MinHeight = 170, Margin = new Thickness(0, 0, 12, 0) };
    readonly TextBlock _intro = new() { Margin = new Thickness(0, 0, 0, 12) };
    readonly TextBlock _detail = new() { Margin = new Thickness(0, 10, 0, 0) };
    readonly TextBlock _steam = new() { Margin = new Thickness(0, 12, 0, 6) };
    readonly TextBlock _status = new() { Margin = new Thickness(0, 12, 0, 0) };
    readonly ProgressBar _bar = new() { Height = 4, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed, Minimum = 0, Maximum = 1 };
    readonly Button _use, _delete, _open, _signIn, _password, _forget, _stopButton;
    IReadOnlyList<ListedBuild>? _builds;
    ClientLayout _layout;
    string? _current, _unfinished;
    CancellationTokenSource? _stop;
    bool _busy;

    /// <summary>The folder's client changed: the window that opened this one reads the folder again.</summary>
    public bool Changed { get; private set; }

    static ClientVersionsWindow()
    {
        DepotTool.Bundled ??= () => typeof(ClientVersionsWindow).Assembly.GetManifestResourceStream("Bundled/DepotDownloader.zip");
    }

    public ClientVersionsWindow(Window? owner, Shared shared)
    {
        _shared = shared;
        DialogLayout.Configure(this, owner, L.Text("Game client"), 760);
        var root = new DockPanel { Margin = new Thickness(24) };

        var close = new Button { Content = L.Text("Close"), IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        close.Click += (_, _) => Close();
        DockPanel.SetDock(close, Dock.Bottom);
        root.Children.Add(close);

        DockPanel.SetDock(_intro, Dock.Top);
        root.Children.Add(_intro);

        // Under the list: what is happening, and the Steam sign-in.
        var below = new StackPanel();
        _stopButton = new Button { Content = L.Text("Stop"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
        _stopButton.Click += (_, _) => _stop?.Cancel();
        below.Children.Add(_detail);
        below.Children.Add(_status);
        below.Children.Add(_bar);
        below.Children.Add(_stopButton);
        var line = new Border { Height = 1, Margin = new Thickness(0, 14, 0, 0) };
        line.SetResourceReference(Border.BackgroundProperty, "Shell.Border");
        below.Children.Add(line);
        below.Children.Add(_steam);
        var steamButtons = new WrapPanel();
        _signIn = Small(L.Text("Sign in with the Steam app…"), L.Text("A code to scan with the Steam mobile app. Waypoint sees no password."), () => _ = SignInAsync(null));
        _password = Small(L.Text("Sign in with a password…"), L.Text("Without the Steam mobile app: the download tool asks for the password and the Steam Guard code in a window of its own. Waypoint sees neither."), () => _ = PasswordAsync());
        _forget = Small(L.Text("Forget the sign-in"), L.Text("Waypoint forgets which Steam account to use. The next download asks to sign in again."), Forget);
        foreach (var button in new[] { _signIn, _password, _forget }) steamButtons.Children.Add(button);
        below.Children.Add(steamButtons);
        DockPanel.SetDock(below, Dock.Bottom);
        root.Children.Add(below);

        _use = Action(L.Text("Use this version"), () => _ = UseAsync());
        _delete = Action(L.Text("Delete this copy…"), () => _ = DeleteAsync());
        _open = Action(L.Text("Open folder"), OpenFolder);
        var buttons = new StackPanel { Width = 170 };
        foreach (var button in new[] { _use, _delete, _open }) buttons.Children.Add(button);
        DockPanel.SetDock(buttons, Dock.Right);
        root.Children.Add(buttons);
        root.Children.Add(_list);
        _list.SelectionChanged += (_, _) => Enable();
        _status.SetResourceReference(StyleProperty, "Shell.Secondary");
        _detail.SetResourceReference(StyleProperty, "Shell.Secondary");
        _steam.SetResourceReference(StyleProperty, "Shell.Secondary");
        Content = root;
        Closing += (_, e) =>
        {
            // A switch under way is not left running behind a closed window: Stop first, and what it fetched is kept.
            if (_busy) e.Cancel = true;
        };
        Loaded += async (_, _) => await RefreshAsync();
    }

    Button Action(string text, Action run)
    {
        var button = new Button { Content = text, Margin = new Thickness(0, 0, 0, 8), HorizontalAlignment = HorizontalAlignment.Stretch };
        button.Click += (_, _) => run();
        return button;
    }

    static Button Small(string text, string tip, Action run)
    {
        var button = new Button { Content = text, Margin = new Thickness(0, 0, 8, 6), ToolTip = tip };
        button.Click += (_, _) => run();
        return button;
    }

    Row? Selected => (_list.SelectedItem as ListBoxItem)?.Tag as Row;
    bool Running => _shared.Monitor.Latest.Any(s => s.Running) || _shared.IsLiveRunning;
    string? Account => SteamDownload.IsAccountName(_shared.Settings.SteamAccount) ? _shared.Settings.SteamAccount : null;
    /// <summary>A build by its version and LocalExile's own name for it: "Path of Exile 3.29.3c: 3.29.3.5 hotfix of
    /// 2026-11-09, build 328126". One version can be several builds.</summary>
    static string Build(ListedBuild build) => build.Name.Length > 0 ? Version(build.Version) + ": " + build.Name : Version(build.Version);

    static string Version(string? version) => version is null ? L.Text("Path of Exile, version not known") : L.Text("Path of Exile {0}", version);

    /// <summary>Reads the folder again, off the UI thread: sizes are summed and executables hashed.</summary>
    async Task RefreshAsync()
    {
        string client = Path.Combine(_folder, "Client");
        var (layout, current, unfinished, builds, kept, names) = await Task.Run(() =>
        {
            var listed = LocalExileBuilds.Of(_folder);
            var held = ClientVersions.Kept(_folder);
            // Which build each client is, in LocalExile's own words: one version can be several builds (a hotfix).
            var named = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string folder in held.Select(k => k.Folder).Prepend(client))
                if (listed is not null && Directory.Exists(folder) && ClientLocator.ListedBuildOf(folder, listed) is { } build) named[folder] = Build(build);
            return (ClientVersions.Layout(_folder), FolderList.ClientOf(_folder), ClientVersions.Unfinished(_folder), listed, held, named);
        });
        (_layout, _current, _unfinished, _builds) = (layout, current, unfinished, builds);
        string Named(string folder) => names.TryGetValue(folder, out var name) && name.Length > 0 ? name + "\n" + folder : folder;
        _list.Items.Clear();
        if (layout != ClientLayout.None)
            Add(new(Version(current) + "  ·  " + (unfinished is null ? L.Text("in use") : L.Text("in use, half-way to {0}", unfinished))
                + (layout == ClientLayout.Link ? "  ·  " + L.Text("a link to another folder") : ""), Named(client), InUse: true), bold: true);
        foreach (var kept1 in kept)
            Add(new(Version(kept1.Version) + "  ·  " + L.Text("kept, {0:0.#} GB", kept1.Bytes / 1e9), Named(kept1.Folder), Kept: kept1));
        // One line a version Steam can deliver: its newest build, unless that version is here already.
        foreach (var build in (builds ?? []).Where(b => b.FromSteam).GroupBy(b => b.Version).Select(g => g.Last()).Reverse())
            if (build.Version != current || unfinished is not null || layout == ClientLayout.None)
                if (!kept.Any(k => k.Version == build.Version))
                    Add(new(Version(build.Version) + "  ·  " + L.Text("from Steam"),
                        L.Text("{0}. It is downloaded from Steam with your Steam account.", Build(build)), Build: build));
        var runs = ClientLocator.Supported(_folder);
        _intro.Text = builds is null
            ? L.Text("This LocalExile does not say which game versions it runs, so nothing can be fetched from Steam for it. LocalExile v0.5.0-beta and later do.")
            : L.Text("This LocalExile runs Path of Exile {0}. The version in use is the one in its Client folder; others can be kept beside it, or come from Steam with your own Steam account.",
                runs.Count <= 1 ? string.Concat(runs) : string.Join(", ", runs.Take(runs.Count - 1)) + " " + L.Text("and") + " " + runs[^1]);
        if (_list.Items.Count > 0 && _list.SelectedItem is null) _list.SelectedIndex = 0;
        Enable();
    }

    void Add(Row row, bool bold = false) => _list.Items.Add(new ListBoxItem { Content = row.Text, Tag = row, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal });

    void Enable()
    {
        var row = Selected;
        bool free = !_busy && !Running;
        _use.IsEnabled = free && row is not null && (row.Kept is not null || row.Build is not null);
        _use.Content = row?.Build is not null ? (_unfinished == row.Build.Version ? L.Text("Finish the switch") : L.Text("Get this version…")) : L.Text("Use this version");
        _delete.IsEnabled = free && row?.Kept is not null;
        _open.IsEnabled = row is { InUse: true } or { Kept: not null };
        _detail.Text = row?.Detail ?? "";
        _signIn.IsEnabled = _password.IsEnabled = free && DepotTool.Supported && _builds is { Count: > 0 };
        _forget.IsEnabled = free && Account is not null;
        _steam.Text = !DepotTool.Supported ? L.Text("The Steam download works on 64-bit Windows only for now.")
            : Account is { } account ? L.Text("Steam: signed in as {0}. The download tool keeps the sign-in; Waypoint keeps only this name.", account)
            : L.Text("Steam: not signed in. A version from Steam needs your Steam account, with Path of Exile in its library, once.");
        if (!_busy && Running) Say(L.Text("Close the game and stop the servers to change the game client."));
    }

    void Say(string text, double? fraction = null, bool working = false)
    {
        _status.Text = text;
        _bar.Visibility = working ? Visibility.Visible : Visibility.Collapsed;
        _bar.IsIndeterminate = fraction is null;
        if (fraction is { } done) _bar.Value = Math.Clamp(done, 0, 1);
    }

    void Log(string line) => _shared.Logs.Write("Steam: " + line);

    int Ask(DialogKind kind, string title, string message, string? details, params string[] buttons) =>
        // The last button is the one Enter presses: a box that opens while its user types starts nothing long.
        Dialog.Show(this, kind, title, message, details, buttons, buttons.Length - 1);

    /// <summary>One piece of work at a time, with the folder held as any other work holds it.</summary>
    async Task WorkAsync(string name, Func<CancellationToken, Task> work, bool stoppable = true)
    {
        if (_busy) return;
        _busy = true;
        using var stop = _stop = new CancellationTokenSource();
        _stopButton.Visibility = stoppable ? Visibility.Visible : Visibility.Collapsed;
        Enable();
        try
        {
            bool ran = await _shared.RunExclusiveAsync(name, async () =>
            {
                try { await work(stop.Token); }
                catch (OperationCanceledException) { Say(L.Text("Stopped. What was downloaded is kept, and the next try goes on from it.")); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Net.Http.HttpRequestException or System.ComponentModel.Win32Exception)
                {
                    Say(e.Message);
                    Log(e.Message);
                }
            });
            if (!ran) Say(L.Text("Something else is using LocalExile. Try again when it is done."));
        }
        finally
        {
            _stop = null;
            _busy = false;
            _stopButton.Visibility = Visibility.Collapsed;
            _bar.Visibility = Visibility.Collapsed;
            await RefreshAsync();
        }
    }

    string Tool => DepotTool.Executable(Settings.Home);
    string Scratch => Path.Combine(DepotTool.Folder(Settings.Home), "signin");

    /// <summary>The download tool, unpacked from Waypoint.exe the first time (a build without it fetches its release).</summary>
    async Task<bool> ToolReadyAsync(CancellationToken stop)
    {
        if (!DepotTool.Supported) return false;
        if (DepotTool.Installed(Settings.Home)) return true;
        Say(L.Text("Getting the download tool ready …"), working: true);
        await DepotTool.InstallAsync(Settings.Home, Updates.Client, (done, total) => Dispatcher.BeginInvoke(() => Say(L.Text("Getting the download tool ready …"), total == 0 ? null : (double)done / total, working: true)), stop);
        return DepotTool.Installed(Settings.Home);
    }

    /// <summary>The newest build Steam can deliver, for a sign-in that has no version of its own to fetch.</summary>
    ListedBuild? AnyBuild => (_builds ?? []).LastOrDefault(b => b.FromSteam);

    /// <summary>The QR sign-in; inside a piece of work when <paramref name="within"/> carries its stop.</summary>
    async Task<bool> SignInAsync(CancellationToken? within)
    {
        async Task<bool> Run(CancellationToken stop)
        {
            if (AnyBuild is not { } build || !await ToolReadyAsync(stop)) return false;
            Say(L.Text("Waiting for the Steam sign-in …"), working: true);
            var window = new SteamSignInWindow(this, Tool, build, Scratch, Log);
            window.ShowDialog();
            if (window.Account is not { } account || !SteamDownload.IsAccountName(account))
            {
                Say(L.Text("Steam was not signed in to."));
                return false;
            }
            _shared.Settings.SteamAccount = account;
            _shared.Settings.Save();
            Say(L.Text("Signed in to Steam."));
            return true;
        }
        if (within is { } stopNow) return await Run(stopNow);
        bool signedIn = false;
        await WorkAsync(L.Text("Steam sign-in"), async stop => signedIn = await Run(stop), stoppable: false);
        return signedIn;
    }

    /// <summary>For someone without the Steam mobile app: the tool's own prompts, in its own console window.</summary>
    async Task PasswordAsync()
    {
        if (AnyBuild is not { } build) return;
        if (NameDialog.Ask(this, L.Text("Sign in with a password"), L.Text("Your Steam account name (what you sign in to Steam with). The password and the Steam Guard code are asked next, in the download tool's own window:"), Account ?? "") is not { } name) return;
        name = name.Trim();
        if (!SteamDownload.IsAccountName(name))
        {
            Ask(DialogKind.Warning, L.Text("Sign in with a password"), L.Text("That does not look like a Steam account name. It is the name you sign in with, not your profile's display name."), null, L.Text("OK"));
            return;
        }
        await WorkAsync(L.Text("Steam sign-in"), async stop =>
        {
            if (!await ToolReadyAsync(stop)) return;
            Say(L.Text("Sign in in the window that opened. It closes itself when Steam accepts the sign-in."), working: true);
            if (!await SteamDownload.PasswordSignInAsync(Tool, build, name, stop))
            {
                Say(L.Text("Steam was not signed in to."));
                return;
            }
            _shared.Settings.SteamAccount = name;
            _shared.Settings.Save();
            Say(L.Text("Signed in to Steam."));
        });
    }

    void Forget()
    {
        _shared.Settings.SteamAccount = null;
        _shared.Settings.Save();
        Say(L.Text("Waypoint no longer names a Steam account. The download tool's own remembered sign-in stays on this PC until Steam ends it."));
        Enable();
    }

    void OpenFolder()
    {
        string? folder = Selected switch { { Kept: { } kept } => kept.Folder, { InUse: true } => Path.Combine(_folder, "Client"), _ => null };
        if (folder is not null && Directory.Exists(folder)) Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { folder }, UseShellExecute = false })?.Dispose();
    }

    async Task UseAsync()
    {
        if (Selected is not { } row) return;
        if (row.Kept is { } kept)
        {
            await WorkAsync(L.Text("Game client"), async _ =>
            {
                await Task.Run(() => ClientVersions.Use(_folder, kept.Name));
                Changed = true;
                Say(L.Text("{0} is in use now. The version it replaced is kept.", Version(kept.Version)));
            }, stoppable: false);
            return;
        }
        if (row.Build is { } build) await GetAsync(build);
    }

    async Task DeleteAsync()
    {
        if (Selected?.Kept is not { } kept) return;
        if (Ask(DialogKind.Warning, L.Text("Delete this copy of the game?"),
                L.Text("{0}, {1:0.#} GB, is deleted for good. The version in use is not touched.", Version(kept.Version), kept.Bytes / 1e9), kept.Folder,
                L.Text("Delete"), L.Text("Cancel")) != 0) return;
        await WorkAsync(L.Text("Game client"), async _ =>
        {
            Say(L.Text("Deleting …"), working: true);
            await Task.Run(() => ClientVersions.Delete(_folder, kept.Name));
            Say(L.Text("Deleted."));
        }, stoppable: false);
    }

    /// <summary>A version from Steam: the sign-in when there is none, its size, how to move when there is a client, the
    /// room, the switch, and LocalExile's setup of what came in.</summary>
    async Task GetAsync(ListedBuild build)
    {
        await WorkAsync(L.Text("Game client"), async stop =>
        {
            if (!await ToolReadyAsync(stop)) return;
            if (Account is null && !await SignInAsync(stop)) return;
            Say(L.Text("Asking Steam how large {0} is …", Version(build.Version)), working: true);
            var (bytes, sized) = await SteamDownload.SizeAsync(Tool, build, Account!, Scratch, Log, stop);
            if (sized.Outcome == SteamOutcome.SignInNeeded && await SignInAsync(stop))
                (bytes, sized) = await SteamDownload.SizeAsync(Tool, build, Account!, Scratch, Log, stop);
            if (bytes is not { } size)
            {
                Say(SteamDownload.Explain(sized));
                return;
            }
            long current = _layout == ClientLayout.None ? 0 : await Task.Run(() => ClientVersions.SizeOf(Path.Combine(_folder, "Client")));
            if (Way(build, size, current, ClientCopy.FreeSpace(_folder)) is not { } way) return;
            var result = await Task.Run(() => ClientVersions.SwitchAsync(_folder, build, way, Tool, Account!,
                progress => Dispatcher.BeginInvoke(() => Say((progress.Stage == "copy" ? L.Text("Copying the client in use as a start: {0}", progress.Detail) : L.Text("Downloading from Steam. {0}", progress.Detail)), progress.Fraction, working: true)),
                Log, stop), CancellationToken.None);
            if (result.Outcome != SteamOutcome.Done)
            {
                Say(SteamDownload.Explain(result));
                return;
            }
            Changed = true;
            await PrepareAsync(build, stop);
        });
    }

    /// <summary>How to move to the build with a client already there: Settings' choice when it fits the drive, else the
    /// question, with only the ways the drive has room for. Null: not now.</summary>
    SwitchWay? Way(ListedBuild build, long buildBytes, long currentBytes, long? free)
    {
        string target = Version(build.Version), title = L.Text("Get {0}?", target);
        long inPlace = ClientVersions.Needed(_layout, SwitchWay.UpdateInPlace, buildBytes, currentBytes), both = ClientVersions.Needed(_layout, SwitchWay.KeepBoth, buildBytes, currentBytes);
        bool canInPlace = free is not { } room || room >= inPlace, canBoth = free is not { } space || space >= both;
        string roomText = free is { } f ? L.Text("{0:0.#} GB is free on this drive.", f / 1e9) : "";
        if (_layout == ClientLayout.None)
        {
            if (canBoth) return Ask(DialogKind.Question, title, L.Text("{0} is downloaded from Steam into this LocalExile folder: {1:0.#} GB.", target, buildBytes / 1e9) + " " + roomText, null,
                L.Text("Download"), L.Text("Cancel")) == 0 ? SwitchWay.KeepBoth : null;
            Ask(DialogKind.Warning, title, L.Text("The drive is too full: {0} needs {1:0.#} GB.", target, both / 1e9) + " " + roomText, null, L.Text("OK"));
            return null;
        }
        if (_unfinished == build.Version && canInPlace) return SwitchWay.UpdateInPlace;
        string setting = _shared.Settings.ClientSwitch;
        if (setting == nameof(SwitchWay.UpdateInPlace) && canInPlace) return SwitchWay.UpdateInPlace;
        if (setting == nameof(SwitchWay.KeepBoth) && canBoth) return SwitchWay.KeepBoth;
        if (!canInPlace && !canBoth)
        {
            Ask(DialogKind.Warning, title, L.Text("The drive is too full: {0} needs {1:0.#} GB.", target, Math.Min(inPlace, both) / 1e9) + " " + roomText, null, L.Text("OK"));
            return null;
        }
        // A client from Steam is brought to the other version where it is; a standalone one shares nothing with Steam's files.
        bool shares = _layout == ClientLayout.Steam;
        string replace = shares
            ? L.Text("Update in place: the one client becomes {0}. Only what differs is downloaded and it needs no more room, but {1} is then gone from this PC.", target, Version(_current))
            : L.Text("Replace: {0} is downloaded whole ({1:0.#} GB), then the client in use is deleted. A standalone client shares nothing with Steam's files.", target, buildBytes / 1e9);
        string keep = shares
            ? L.Text("Keep both: the client in use is set aside and {0} is made from a copy of it, so only what differs is downloaded. It needs {1:0.#} GB more, and going back later is instant.", target, both / 1e9)
            : L.Text("Keep both: {0} is downloaded whole ({1:0.#} GB) and the client in use is set aside. Going back later is instant.", target, buildBytes / 1e9);
        var texts = new List<string>();
        var ways = new List<(string Button, SwitchWay Way)>();
        if (canInPlace) { texts.Add(replace); ways.Add((shares ? L.Text("Update in place") : L.Text("Replace"), SwitchWay.UpdateInPlace)); }
        if (canBoth) { texts.Add(keep); ways.Add((L.Text("Keep both"), SwitchWay.KeepBoth)); }
        string note = canInPlace && canBoth ? L.Text("Settings can make one of these the way every time.") : L.Text("The drive has room for only one way.");
        int chosen = Ask(DialogKind.Question, title, string.Join("\n\n", texts) + "\n\n" + roomText + " " + note, null, [.. ways.Select(w => w.Button), L.Text("Cancel")]);
        return chosen >= 0 && chosen < ways.Count ? ways[chosen].Way : null;
    }

    /// <summary>LocalExile's own setup, which makes Game.exe and the copy patched for this folder's keys from the client
    /// that just came in. A folder that still needs something from its user (an account) is finished from the Play page.</summary>
    async Task PrepareAsync(ListedBuild build, CancellationToken stop)
    {
        Say(L.Text("Preparing {0} for LocalExile …", Version(build.Version)), working: true);
        try
        {
            var result = await LocalExileTool.RunAsync(_folder, ["setup", "--no-update-check"], item => Dispatcher.BeginInvoke(() =>
            {
                if (item.Message is { } message) Log("LocalExile: " + message);
                if (item.Name is "step" or "progress") Say(LocalExileReleases.Stage(item.Id, item.Message ?? ""), working: true);
            }), line => Dispatcher.BeginInvoke(() => Log("LocalExile: " + line)), stop: stop);
            Say(result.Code == 0 ? L.Text("{0} is in place and ready. Close this window and press Play.", Version(build.Version))
                : L.Text("{0} is in place. Finish its setup from the Play page.", Version(build.Version)));
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            Say(L.Text("{0} is in place. Finish its setup from the Play page.", Version(build.Version)));
            Log(e.Message);
        }
    }
}
