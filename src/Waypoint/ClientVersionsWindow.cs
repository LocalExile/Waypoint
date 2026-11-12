using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using MaterialDesignThemes.Wpf;

namespace Waypoint;

/// <summary>
/// The game clients this PC has for LocalExile, as the sidebar's folder list has its folders: every client in a LocalExile
/// folder Waypoint knows (<see cref="FolderList"/>), the one each folder uses and those it set aside
/// (<see cref="ClientVersions"/>), each with where it is and whether it came as the standalone game or from Steam. What is
/// changed here is the folder in use: which of its own clients it uses, and, apart from the list, a version downloaded
/// from Steam into it, of those its LocalExile lists (<see cref="LocalExileBuilds"/>, <see cref="SteamDownload"/>). A
/// download needs a Steam sign-in once, by the tool's own QR code (<see cref="SteamSignInWindow"/>) or, without the Steam
/// mobile app, by its own password prompt in a console window. With a client already there, how to move is asked each
/// time (update the one client where it is, or keep both) unless Settings names one. Nothing here runs while the game
/// or a server runs, and LocalExile's own setup prepares a client that just came in.
/// </summary>
sealed class ClientVersionsWindow : FormWindow
{
    /// <summary>A line of the list: one game client in one of the LocalExile folders Waypoint knows.</summary>
    /// <param name="Folder">The LocalExile folder it is in.</param>
    /// <param name="Client">The client's own folder.</param>
    /// <param name="Files">Standalone or from Steam (<see cref="ClientVersions.FilesOf"/>).</param>
    /// <param name="InUse">The client its folder uses (its <c>Client</c>); false for one set aside there.</param>
    /// <param name="Build">Which build it is, when its executable is one its LocalExile lists.</param>
    /// <param name="LinkTarget">Where a <c>Client</c> that is a link leads.</param>
    /// <param name="Shared">The link leads to another LocalExile folder's <c>Client</c> (<see cref="OwnClient.InLocalExile"/>);
    /// false for a link to a game folder outside LocalExile, which an update of that game can break.</param>
    /// <param name="HalfWayTo">For a client in use that carries the mark of an update in place that did not finish: the
    /// version it was on its way to. A kept one says so itself (<see cref="KeptClient.HalfWayTo"/>).</param>
    sealed record Row(KnownFolder Folder, string Client, ClientLayout Files, bool InUse, string? Version, ListedBuild? Build,
        KeptClient? Kept = null, string? LinkTarget = null, bool Shared = false, string? HalfWayTo = null)
    {
        /// <summary>A client of the folder in use: the only ones this window changes.</summary>
        public bool Here => Folder.Current;
    }

    readonly Shared _shared;
    readonly string _folder = Environment.CurrentDirectory;
    readonly ListBox _list = new() { MinHeight = 170, Margin = new Thickness(0, 0, 12, 0), HorizontalContentAlignment = HorizontalAlignment.Stretch };
    readonly TextBlock _empty = new() { Margin = new Thickness(12, 10, 24, 0), Visibility = Visibility.Collapsed, IsHitTestVisible = false };
    readonly TextBlock _intro = new() { Margin = new Thickness(0, 0, 0, 12) };
    /// <summary>Over the download button: which versions this folder's LocalExile runs, and where a download goes.</summary>
    readonly TextBlock _steamIntro = new() { Margin = new Thickness(0, 12, 0, 8) };
    readonly TextBlock _detail = new() { Margin = new Thickness(0, 10, 0, 0) };
    readonly TextBlock _steam = new() { Margin = new Thickness(0, 12, 0, 6) };
    readonly TextBlock _status = new() { Margin = new Thickness(0, 12, 0, 0) };
    /// <summary>Why nothing can be changed just now (the game or a server runs). Its own line: what the last piece of work
    /// said stays readable above it.</summary>
    readonly TextBlock _blocked = new() { Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
    readonly ProgressBar _bar = new() { Height = 4, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed, Minimum = 0, Maximum = 1 };
    readonly Button _use, _delete, _open, _copy, _download, _signIn, _password, _forget, _stopButton;
    /// <summary>The copy the folder in use can have in place of a link to a game folder outside LocalExile (<see cref="OwnClient.Check"/>).</summary>
    OwnClient.Plan? _linked;
    IReadOnlyList<ListedBuild>? _builds;
    /// <summary>The builds Steam can deliver that the folder in use does not hold: what the download offers.</summary>
    IReadOnlyList<ListedBuild> _offered = [];
    /// <summary>The bytes a version change that did not finish left in the folder in use; the next one goes on from them.</summary>
    long _started;
    ClientLayout _layout;
    string? _current, _unfinished, _unfinishedHash;
    /// <summary>The build of the client in use, when its executable is one LocalExile lists.</summary>
    ListedBuild? _currentBuild;
    CancellationTokenSource? _stop;
    bool _busy;

    /// <summary>The folder's client changed: the window that opened this one reads the folder again.</summary>
    public bool Changed { get; private set; }

    /// <summary>The user asked for a copy in place of the folder's linked client: the Play page makes it, with its own
    /// question and progress, once this window is closed.</summary>
    public bool CopyRequested { get; private set; }

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
        below.Children.Add(_blocked);
        below.Children.Add(_bar);
        below.Children.Add(_stopButton);
        var line = new Border { Height = 1, Margin = new Thickness(0, 14, 0, 0) };
        line.SetResourceReference(Border.BackgroundProperty, "Shell.Border");
        below.Children.Add(line);
        // Getting a version is apart from the list, as "Set up a new copy" is apart from the sidebar's folders.
        below.Children.Add(_steamIntro);
        _download = new Button { Content = L.Text("Download a version…"), HorizontalAlignment = HorizontalAlignment.Left,
            ToolTip = L.Text("Choose a version this LocalExile runs and download it from Steam into this folder.") };
        _download.SetResourceReference(StyleProperty, "Primary");
        _download.Click += (_, _) => _ = DownloadAsync();
        below.Children.Add(_download);
        _steam.Margin = new Thickness(0, 14, 0, 6);
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
        _copy = Action(L.Text("Make a copy…"), () => { CopyRequested = true; Close(); });
        _copy.ToolTip = L.Text("Copy the linked game into this LocalExile folder, so that an update of that game cannot break it. The Play page asks first and shows the copy.");
        _copy.Visibility = Visibility.Collapsed;
        var buttons = new StackPanel { Width = 170 };
        foreach (var button in new[] { _use, _delete, _open, _copy }) buttons.Children.Add(button);
        DockPanel.SetDock(buttons, Dock.Right);
        root.Children.Add(buttons);
        var clients = new Grid();
        clients.Children.Add(_list);
        clients.Children.Add(_empty);
        root.Children.Add(clients);
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        _empty.SetResourceReference(StyleProperty, "Shell.Secondary");
        _empty.VerticalAlignment = VerticalAlignment.Top;
        _steamIntro.SetResourceReference(StyleProperty, "Shell.Secondary");
        // The secondary style is not the window's own text style: these lines wrap by their own word.
        foreach (var text in new[] { _steamIntro, _steam, _detail, _status, _blocked, _empty }) text.TextWrapping = TextWrapping.Wrap;
        _intro.Text = L.Text("The game clients in your LocalExile folders. What you change here is the folder in use.");
        _list.SelectionChanged += (_, _) => Enable();
        _status.SetResourceReference(StyleProperty, "Shell.Secondary");
        _detail.SetResourceReference(StyleProperty, "Shell.Secondary");
        _blocked.SetResourceReference(ForegroundProperty, "Shell.Warning");
        // The buttons follow what runs: LocalExile's own setup starts its tools for a moment, and a game may close meanwhile.
        void Sampled(IReadOnlyList<ServiceSample> _) => Dispatcher.BeginInvoke(() => { if (!_busy) Enable(); });
        _shared.Monitor.Sampled += Sampled;
        Closed += (_, _) => _shared.Monitor.Sampled -= Sampled;
        _steam.SetResourceReference(StyleProperty, "Shell.Secondary");
        Content = root;
        Closing += (_, e) =>
        {
            // A switch under way is not left running behind a closed window: Stop first, and what it fetched is kept.
            if (_busy) e.Cancel = true;
        };
        Loaded += async (_, _) =>
        {
            await RefreshAsync();
            // A kept client whose deletion was cut short is still on the drive under a name the list does not show.
            if (!Running && await Task.Run(() => ClientVersions.HasUnfinishedDeletes(_folder)))
                await WorkAsync(L.Text("Game client"), async _ =>
                {
                    Say(L.Text("Finishing a deletion that was cut short …"), working: true);
                    await Task.Run(() => ClientVersions.FinishDeletes(_folder));
                    Say("");
                }, stoppable: false);
        };
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
    /// <summary>The build a client of the folder in use that is half-way to another one was on its way to.</summary>
    ListedBuild? Unfinished => _unfinished is null ? null : (_builds ?? []).FirstOrDefault(b => b.FromSteam && Resumes(b));
    bool Running => _shared.Monitor.Latest.Any(s => s.Running) || _shared.IsLiveRunning;
    string? Account => SteamDownload.IsAccountName(_shared.Settings.SteamAccount) ? _shared.Settings.SteamAccount : null;
    /// <summary>A build by its version and LocalExile's own name for it: "Path of Exile 3.29.3c: 3.29.3.5 hotfix of
    /// 2026-11-09, build 328126". One version can be several builds.</summary>
    static string Build(ListedBuild build) => build.Name.Length > 0 ? Version(build.Version) + ": " + build.Name : Version(build.Version);

    static string Version(string? version) => version is null ? L.Text("Path of Exile, version not known") : L.Text("Path of Exile {0}", version);

    /// <summary>What a line or a question calls a client: its version, and which build of it when Steam has several.</summary>
    string Label(string? version, ListedBuild? build) => build is not null && ClientVersions.Several(_builds ?? [], build.Version) ? Build(build) : Version(build?.Version ?? version);

    /// <summary>Whether the client in use is half-way to this very build.</summary>
    bool Resumes(ListedBuild build) => _unfinished == build.Version && (_unfinishedHash is null || _unfinishedHash == build.ExecutableSha256);

    /// <summary>Reads the folders again, off the UI thread: sizes are summed and executables hashed.</summary>
    async Task RefreshAsync()
    {
        string? chosen = Selected?.Client;
        var paths = FolderList.Paths(_shared.Settings, _folder);
        var (layout, current, unfinished, builds, rows, runs, started, linked) = await Task.Run(() =>
        {
            // In the background, with its user looking: a launcher that failed to answer earlier is asked once more.
            var listed = LocalExileBuilds.Of(_folder, again: true);
            var found = new List<Row>();
            // The folder in use first, then the others in the order the sidebar's list has them.
            foreach (var known in FolderList.Read(paths, _folder).Where(f => f.Kind != FolderKind.Missing).OrderByDescending(f => f.Current))
            {
                try
                {
                    // Which build a client is, in the words of its own folder's LocalExile: one version can be several builds.
                    var theirs = known.Current ? listed : LocalExileBuilds.Of(known.Path);
                    ListedBuild? Named(string folder) => theirs is null ? null : ClientLocator.ListedBuildOf(folder, theirs);
                    string client = Path.Combine(known.Path, "Client");
                    if (ClientVersions.FilesOf(client) is var files and not ClientLayout.None)
                    {
                        // A linked client is shown where it is used, with where it leads: another LocalExile folder's
                        // client, or a game folder outside LocalExile (the retail game's own).
                        string? target = Launcher.Core.ClientSettings.IsLink(client) ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(ClientLocator.RealPath(client))) : null;
                        found.Add(new(known, client, files, InUse: true, FolderList.ClientOf(known.Path), Named(client),
                            LinkTarget: target, Shared: target is not null && OwnClient.InLocalExile(target), HalfWayTo: ClientVersions.Unfinished(known.Path)));
                    }
                    foreach (var kept in ClientVersions.Kept(known.Path))
                        found.Add(new(known, kept.Folder, ClientVersions.FilesOf(kept.Folder), InUse: false, kept.Version, Named(kept.Folder), kept));
                }
                // One folder that cannot be read (a drive gone, a launcher that will not start) leaves the others their lines.
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or InvalidOperationException
                    or System.ComponentModel.Win32Exception or System.Security.SecurityException or AggregateException) { }
            }
            string next = Path.Combine(_folder, ClientVersions.NextFolder);
            return (ClientVersions.Layout(_folder), FolderList.ClientOf(_folder), ClientVersions.UnfinishedBuild(_folder), listed, found, ClientLocator.Supported(_folder),
                Directory.Exists(next) ? ClientVersions.SizeOf(next) : 0, OwnClient.Check(_folder));
        });
        _started = started;
        _linked = linked;
        (_layout, _current, _unfinished, _unfinishedHash, _builds) = (layout, current, unfinished?.Version, unfinished?.ExecutableSha256, builds);
        _currentBuild = rows.FirstOrDefault(r => r.Here && r.InUse)?.Build;
        // A copy set aside half-way is not that build whole: the build stays on offer.
        _offered = ClientVersions.Offered(builds ?? [], _currentBuild, _unfinished is null, rows.Where(r => r.Here && r.Kept is { HalfWayTo: null }).Select(r => r.Build));
        _list.Items.Clear();
        foreach (var row in rows) Add(row);
        _empty.Text = L.Text("No game client in your LocalExile folders yet.");
        _empty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _steamIntro.Text = builds is null
            ? L.Text("This LocalExile does not say which game versions it runs, so nothing can be fetched from Steam for it. LocalExile v0.5.0-beta and later do.")
            : L.Text("This LocalExile runs Path of Exile {0}. A version this folder does not have yet is downloaded from Steam into it, with your own Steam account.",
                  runs.Count <= 1 ? string.Concat(runs) : string.Join(", ", runs.Take(runs.Count - 1)) + " " + L.Text("and") + " " + runs[^1])
              + (_offered.Count == 0 && builds.Any(b => b.FromSteam) ? " " + L.Text("Every version it runs is already in this folder.") : "")
              + (layout == ClientLayout.Link ? " " + L.Text("This folder's game client is a link to another folder. Make a copy of it first.") : "")
              // Updating a client from Steam where it is needs no second folder, and removes what one was begun in.
              + (started <= 100_000_000 ? "" : " " + (layout == ClientLayout.Steam
                  ? L.Text("{0:0.#} GB of a version change that did not finish is kept in this folder. Keeping both goes on from it; updating in place deletes it.", started / 1e9)
                  : L.Text("{0:0.#} GB of a version change that did not finish is kept in this folder, and the next download goes on from it.", started / 1e9)));
        _list.SelectedItem = _list.Items.OfType<ListBoxItem>().FirstOrDefault(i => (i.Tag as Row)?.Client == chosen);
        if (_list.Items.Count > 0 && _list.SelectedItem is null) _list.SelectedIndex = 0;
        Enable();
    }

    /// <summary>What a line says after the version: standalone or from Steam, which LocalExile folder it is in, and what
    /// is particular about it.</summary>
    string About(Row row)
    {
        var parts = new List<string> { row.Files == ClientLayout.Steam ? L.Text("Steam version") : L.Text("Standalone version"), row.Folder.Title };
        if (row.Kept is { } kept) parts.Add(L.Text("kept, {0:0.#} GB", kept.Bytes / 1e9));
        if (row is { InUse: true, HalfWayTo: { } unfinished }) parts.Add(L.Text("half-way to {0}", Version(unfinished)));
        if (row.Kept is { HalfWayTo: { } other }) parts.Add(L.Text("half-way to {0}", Version(other)));
        if (row.LinkTarget is not null) parts.Add(row.Shared ? L.Text("shares another LocalExile folder's client") : L.Text("linked to a game folder"));
        return string.Join("  ·  ", parts);
    }

    /// <summary>Where a client's files are. For a linked one that is where the link leads, with the link after it: in a
    /// narrow line the end is what goes.</summary>
    static string Where(Row row) => row.LinkTarget is { } target ? target + "  ←  " + row.Client : row.Client;

    /// <summary>A line drawn as the sidebar's folder list draws a folder: an icon, the name with what it is beside it, where
    /// it is underneath, and "In use" on the client the folder in use takes.</summary>
    void Add(Row row)
    {
        var line = new DockPanel();
        var icon = new PackIcon { Kind = row.Files == ClientLayout.Steam ? PackIconKind.Steam : PackIconKind.GamepadVariantOutline, Width = 18, Height = 18,
            Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        icon.SetResourceReference(ForegroundProperty, "Shell.TextSecondary");
        DockPanel.SetDock(icon, Dock.Left);
        line.Children.Add(icon);
        if (row.Here && row.InUse)
        {
            var text = new TextBlock { Text = L.Text("In use") };
            text.SetResourceReference(TextBlock.ForegroundProperty, "Shell.Accent");
            var badge = new Border { Child = text };
            badge.SetResourceReference(StyleProperty, "Shell.Badge");
            badge.SetResourceReference(Border.BorderBrushProperty, "Shell.Accent");
            DockPanel.SetDock(badge, Dock.Right);
            line.Children.Add(badge);
        }
        var title = new TextBlock { FontSize = ThemeResources.Font(13), TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap };
        title.Inlines.Add(new Run(Version(row.Version)) { FontWeight = FontWeights.SemiBold });
        // Which build of the version, in LocalExile's own words: one version can be several builds (a hotfix).
        if (row.Build is { Name.Length: > 0 } build)
        {
            var name = new Run("  " + build.Name);
            name.SetResourceReference(TextElement.ForegroundProperty, "Shell.TextSecondary");
            title.Inlines.Add(name);
        }
        var about = new TextBlock { Text = About(row), FontSize = ThemeResources.Font(11.5), Margin = new Thickness(0, 1, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap };
        about.SetResourceReference(TextBlock.ForegroundProperty, "Shell.TextSecondary");
        var where = new TextBlock { Text = Where(row), FontSize = ThemeResources.Font(11.5), Margin = new Thickness(0, 1, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap };
        where.SetResourceReference(TextBlock.ForegroundProperty, "Shell.TextSecondary");
        var words = new StackPanel();
        words.Children.Add(title);
        words.Children.Add(about);
        words.Children.Add(where);
        line.Children.Add(words);
        var item = new ListBoxItem { Content = line, Tag = row, Padding = new Thickness(10, 7, 10, 8), HorizontalContentAlignment = HorizontalAlignment.Stretch, ToolTip = Where(row) };
        AutomationProperties.SetName(item, string.Join(", ", new[] { Version(row.Version), row.Build?.Name, About(row), row.Here && row.InUse ? L.Text("In use") : null, row.Client }.Where(part => part is { Length: > 0 })));
        _list.Items.Add(item);
    }

    /// <summary>Under the list, of the line chosen: why it cannot be changed from here, when it cannot.</summary>
    string Detail(Row? row)
    {
        if (row is null) return "";
        var lines = new List<string>();
        if (!row.Here)
            lines.Add(row.Folder.Kind == FolderKind.Source
                ? L.Text("This client belongs to a source copy of LocalExile, which looks after its own client.")
                : L.Text("This client belongs to another LocalExile folder. Choose that folder in the sidebar to change its client."));
        else if (row.Kept is { HalfWayTo: null } && _unfinished is not null)
            lines.Add(L.Text("The game client in use is half-way to another version. Finish that switch first."));
        else if (row.Kept is { HalfWayTo: { } other } half)
            lines.Add(_builds is null
                // Not knowing the list is not the same as the list lacking the build: nothing here says "delete it".
                ? L.Text("This copy was set aside half-way to {0}, so it is not whole. The versions this LocalExile runs could not be read just now: open this window again to finish the copy.", Version(other))
                : HalfWay(half) is null
                ? L.Text("This copy was set aside half-way to {0}, so it is not whole. Steam cannot deliver that version for this LocalExile, so the copy can only be deleted.", Version(other))
                : L.Text("This copy was set aside half-way to {0}, so it is not whole. Finish this copy downloads the rest from Steam; until then it cannot be used.", Version(other)));
        else if (row is { InUse: true, LinkTarget: { } target })
            lines.Add(row.Shared ? L.Text("This folder shares the game client of another LocalExile folder: what changes there changes here.")
                : _linked is { } plan ? OwnClient.Notice(plan) : L.Text("Client is a link to {0}, not a copy.", target));
        return string.Join("\n", lines);
    }

    void Enable()
    {
        var row = Selected;
        bool free = !_busy && !Running;
        // Only the folder in use is changed here: one of its kept clients comes back, or its client half-way to another
        // version is finished.
        // A link is someone else's folder: nothing is swapped into its place or downloaded over it, and a switch that did
        // not finish there is that folder's to finish.
        bool link = _layout == ClientLayout.Link;
        bool resume = !link && row is { Here: true, InUse: true } && Unfinished is not null;
        // A copy that was set aside half-way is made whole where it is, as the build it was on its way to.
        bool half = row is { Here: true, Kept.HalfWayTo: not null };
        bool finish = half && HalfWay(row!.Kept!) is not null && DepotTool.Supported;
        _use.IsEnabled = free && (row is { Here: true, Kept.HalfWayTo: null } && !link && _unfinished is null || resume || finish);
        _use.Content = resume ? L.Text("Finish the switch") : half ? L.Text("Finish this copy") : L.Text("Use this version");
        _copy.Visibility = row is { Here: true, InUse: true } && _linked is not null ? Visibility.Visible : Visibility.Collapsed;
        _copy.IsEnabled = free;
        _delete.IsEnabled = free && row is { Here: true, Kept: not null };
        _open.IsEnabled = row is not null;
        _detail.Text = Detail(row);
        _detail.Visibility = _detail.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _download.IsEnabled = free && !link && DepotTool.Supported && _offered.Count > 0;
        _signIn.IsEnabled = _password.IsEnabled = free && DepotTool.Supported && _builds is { Count: > 0 };
        _forget.IsEnabled = free && Account is not null;
        _steam.Text = !DepotTool.Supported ? L.Text("The Steam download works on 64-bit Windows only for now.")
            : Account is { } account ? L.Text("Steam: signed in as {0}. The download tool keeps the sign-in; Waypoint keeps only this name.", account)
            : L.Text("Steam: not signed in. A version from Steam needs your Steam account, with Path of Exile in its library, once.");
        _blocked.Text = L.Text("Close the game and stop the servers to change the game client.");
        _blocked.Visibility = !_busy && Running ? Visibility.Visible : Visibility.Collapsed;
    }

    void Say(string text, double? fraction = null, bool working = false)
    {
        _status.Text = text;
        _bar.Visibility = working ? Visibility.Visible : Visibility.Collapsed;
        _bar.IsIndeterminate = fraction is null;
        if (fraction is { } done) _bar.Value = Math.Clamp(done, 0, 1);
    }

    void Log(string line) => _shared.Logs.Write("Steam: " + line);
    void LogSetup(string line) => _shared.Logs.Write("LocalExile: " + line);

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
        string? folder = Selected?.Client;
        if (folder is not null && Directory.Exists(folder)) Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { folder }, UseShellExecute = false })?.Dispose();
    }

    async Task UseAsync()
    {
        if (Selected is not { Here: true } row) return;
        if (row.Kept is { HalfWayTo: not null } half)
        {
            if (HalfWay(half) is { } target) await FinishKeptAsync(half, target);
            return;
        }
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
        if (row.InUse && Unfinished is { } build) await GetAsync(build);
    }

    /// <summary>The build a copy that was set aside half-way was on its way to, when Steam can deliver it.</summary>
    ListedBuild? HalfWay(KeptClient kept) => (_builds ?? []).FirstOrDefault(b => b.FromSteam && b.Version == kept.HalfWayTo
        && (kept.HalfWayToBuild is null || kept.HalfWayToBuild == b.ExecutableSha256));

    /// <summary>Makes a copy that was set aside half-way whole where it is (<see cref="ClientVersions.FinishKeptAsync"/>).</summary>
    async Task FinishKeptAsync(KeptClient kept, ListedBuild build)
    {
        await WorkAsync(L.Text("Game client"), async stop =>
        {
            if (!await ToolReadyAsync(stop)) return;
            if (Account is null && !await SignInAsync(stop)) return;
            Task<SteamResult> Run() => Task.Run(() => ClientVersions.FinishKeptAsync(_folder, kept.Name, build, Tool, Account!,
                progress => Dispatcher.BeginInvoke(() => Say(L.Text("Downloading from Steam. {0}", progress.Detail), progress.Fraction, working: true)), Log, stop), CancellationToken.None);
            Say(L.Text("Downloading from Steam. {0}", ""), working: true);
            var result = await Run();
            if (result.Outcome == SteamOutcome.SignInNeeded && await SignInAsync(stop)) result = await Run();
            Say(result.Outcome == SteamOutcome.Done ? L.Text("{0} is whole now, and kept in this folder.", Label(build.Version, build)) : SteamDownload.Explain(result));
        });
    }

    /// <summary>Apart from the list: which of the versions this folder's LocalExile runs to download from Steam into it.</summary>
    async Task DownloadAsync()
    {
        if (_busy || _offered.Count == 0) return;
        var pick = new PickWindow(this, _offered, _folder);
        if (pick.ShowDialog() == true && pick.Chosen is { } build) await GetAsync(build);
    }

    /// <summary>The versions a download can be, each with LocalExile's own name for the build.</summary>
    sealed class PickWindow : FormWindow
    {
        public ListedBuild? Chosen { get; private set; }

        public PickWindow(Window owner, IReadOnlyList<ListedBuild> builds, string folder)
        {
            DialogLayout.Configure(this, owner, L.Text("Download a game version"), 560);
            var root = new DockPanel { Margin = new Thickness(24) };
            var list = new ListBox { MinHeight = 110, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            void Accept()
            {
                if ((list.SelectedItem as ListBoxItem)?.Tag is not ListedBuild build) return;
                Chosen = build;
                DialogResult = true;
            }
            var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
            // Enter is Cancel, as in every question of this window: with a way named in Settings no question follows the pick.
            var download = new Button { Content = L.Text("Download"), Margin = new Thickness(0, 0, 8, 0) };
            download.SetResourceReference(StyleProperty, "Primary");
            download.Click += (_, _) => Accept();
            buttons.Children.Add(download);
            buttons.Children.Add(new Button { Content = L.Text("Cancel"), IsCancel = true, IsDefault = true });
            DockPanel.SetDock(buttons, Dock.Bottom);
            root.Children.Add(buttons);
            var intro = new TextBlock { Text = L.Text("Choose the version to download from Steam into this folder. When the folder has a client, how to move to it is asked next, unless Settings names the way."),
                Margin = new Thickness(0, 0, 0, 4) };
            DockPanel.SetDock(intro, Dock.Top);
            root.Children.Add(intro);
            var where = new TextBlock { Text = folder, Margin = new Thickness(0, 0, 0, 12), TextTrimming = TextTrimming.CharacterEllipsis };
            where.SetResourceReference(StyleProperty, "Shell.Secondary");
            DockPanel.SetDock(where, Dock.Top);
            root.Children.Add(where);
            foreach (var build in builds)
            {
                var text = new TextBlock { FontSize = ThemeResources.Font(13), TextTrimming = TextTrimming.CharacterEllipsis };
                text.Inlines.Add(new Run(Version(build.Version)) { FontWeight = FontWeights.SemiBold });
                var name = new Run("  " + build.Name);
                name.SetResourceReference(TextElement.ForegroundProperty, "Shell.TextSecondary");
                text.Inlines.Add(name);
                var item = new ListBoxItem { Content = text, Tag = build, Padding = new Thickness(10, 7, 10, 8) };
                AutomationProperties.SetName(item, ClientVersionsWindow.Build(build));
                item.MouseDoubleClick += (_, _) => Accept();
                list.Items.Add(item);
            }
            list.SelectedIndex = 0;
            root.Children.Add(list);
            Content = root;
            Loaded += (_, _) => (list.SelectedItem as ListBoxItem)?.Focus();
        }
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
            if (_layout == ClientLayout.Link)
            {
                Say(L.Text("This folder's game client is a link to another folder. Make a copy of it first."));
                return;
            }
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
        string target = Label(build.Version, build), title = L.Text("Get {0}?", Version(build.Version));
        long inPlace = ClientVersions.Needed(_layout, SwitchWay.UpdateInPlace, buildBytes, currentBytes), both = ClientVersions.Needed(_layout, SwitchWay.KeepBoth, buildBytes, currentBytes);
        // What an earlier try left in the folder is room this one does not need again; the sentence gives the drive's own figure.
        bool canInPlace = free is not { } room || room + _started >= inPlace, canBoth = free is not { } space || space + _started >= both;
        string roomText = free is { } f ? L.Text("{0:0.#} GB is free on this drive.", f / 1e9) : "";
        if (_layout == ClientLayout.None)
        {
            if (canBoth) return Ask(DialogKind.Question, title, L.Text("{0} is downloaded from Steam into this LocalExile folder: {1:0.#} GB.", target, buildBytes / 1e9) + " " + roomText, null,
                L.Text("Download"), L.Text("Cancel")) == 0 ? SwitchWay.KeepBoth : null;
            Ask(DialogKind.Warning, title, L.Text("The drive is too full: {0} needs {1:0.#} GB.", target, both / 1e9) + " " + roomText, null, L.Text("OK"));
            return null;
        }
        if (_unfinished is not null)
        {
            // A client half-way to another version is made whole where it is, whichever build is asked for: it is not worth
            // setting aside, and Settings' choice is for a client that is whole.
            if (!canInPlace)
            {
                Ask(DialogKind.Warning, title, L.Text("The drive is too full: {0} needs {1:0.#} GB.", target, inPlace / 1e9) + " " + roomText, null, L.Text("OK"));
                return null;
            }
            if (Resumes(build)) return SwitchWay.UpdateInPlace;
            return Ask(DialogKind.Question, title, L.Text("The client in use is half-way to {0}, so it cannot be kept as it is. It becomes {1} where it is, and only what differs is downloaded.", Version(_unfinished), target)
                + " " + roomText, null, L.Text("Update in place"), L.Text("Cancel")) == 0 ? SwitchWay.UpdateInPlace : null;
        }
        string setting = _shared.Settings.ClientSwitch;
        // "Always update in place" is for a client from Steam. A standalone one would be deleted for good, so it is asked about.
        if (setting == nameof(SwitchWay.UpdateInPlace) && canInPlace && _layout == ClientLayout.Steam) return SwitchWay.UpdateInPlace;
        if (setting == nameof(SwitchWay.KeepBoth) && canBoth) return SwitchWay.KeepBoth;
        if (!canInPlace && !canBoth)
        {
            Ask(DialogKind.Warning, title, L.Text("The drive is too full: {0} needs {1:0.#} GB.", target, Math.Min(inPlace, both) / 1e9) + " " + roomText, null, L.Text("OK"));
            return null;
        }
        // A client from Steam is brought to the other version where it is; a standalone one shares nothing with Steam's files.
        bool shares = _layout == ClientLayout.Steam;
        string replace = shares
            ? L.Text("Update in place: the one client becomes {0}. Only what differs is downloaded and it needs no more room, but {1} is then gone from this PC.", target, Label(_current, _currentBuild))
            : L.Text("Replace: {0} is downloaded whole ({1:0.#} GB), then the client in use is deleted. A standalone client shares nothing with Steam's files.", target, buildBytes / 1e9);
        string keep = shares
            ? L.Text("Keep both: the client in use is set aside and {0} is made from a copy of it, so only what differs is downloaded. It needs {1:0.#} GB more, and going back later is instant.", target, both / 1e9)
            : L.Text("Keep both: {0} is downloaded whole ({1:0.#} GB) and the client in use is set aside. Going back later is instant.", target, buildBytes / 1e9);
        var texts = new List<string>();
        var ways = new List<(string Button, SwitchWay Way)>();
        // A standalone client cannot come back from Steam, so keeping it is the first choice there.
        if (canBoth && !shares) { texts.Add(keep); ways.Add((L.Text("Keep both"), SwitchWay.KeepBoth)); }
        if (canInPlace) { texts.Add(replace); ways.Add((shares ? L.Text("Update in place") : L.Text("Replace"), SwitchWay.UpdateInPlace)); }
        if (canBoth && shares) { texts.Add(keep); ways.Add((L.Text("Keep both"), SwitchWay.KeepBoth)); }
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
                if (item.Message is { } message) LogSetup(message);
                if (item.Name is "step" or "progress") Say(LocalExileReleases.Stage(item.Id, item.Message ?? ""), working: true);
            }), line => Dispatcher.BeginInvoke(() => LogSetup(line)), stop: stop);
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
