using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using MaterialDesignThemes.Wpf;

namespace Waypoint;

/// <summary>
/// The game clients this PC has for LocalExile, as the sidebar's folder list has its folders: the clients of the folder in
/// use first (the one it uses, those it set aside: <see cref="ClientVersions"/>), then those of the other LocalExile folders
/// Waypoint knows (<see cref="FolderList"/>). Only the folder in use is changed here: which of its own clients it uses,
/// and, apart from the list, a version downloaded from Steam into it, of those its LocalExile lists
/// (<see cref="LocalExileBuilds"/>, <see cref="SteamDownload"/>).
/// <para>The window says little by itself. The title and the list carry what it is; a button shows only for the line it
/// applies to; the one button that is the likely next step has the primary style; and a sentence appears only where
/// something is blocked or not safe, or when a thing happens. A download signs in to Steam when it needs to, by the tool's
/// QR code (<see cref="SteamSignInWindow"/>) or, from there, by its own password prompt. With a client already there, how
/// to move is asked each time (update the one client where it is, or keep both) unless Settings names one. Nothing here
/// runs while the game or a server runs, and LocalExile's own setup prepares a client that just came in.</para>
/// <para>It opens from the sidebar's folder list, for a release and for a source copy, in every mode. A client that is
/// no version its LocalExile runs says so on its line, and when a client that fits is on this PC, "Use another
/// client…" hands over to the move that comes before a Play (<see cref="ClientUpdate"/>). A source copy gets no Steam
/// download: Waypoint's adapter prepares only a standalone client for it.</para>
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
    /// <param name="Several">Its version is more than one build in its folder's list, so the line names the build.</param>
    sealed record Row(KnownFolder Folder, string Client, ClientLayout Files, bool InUse, string? Version, ListedBuild? Build,
        KeptClient? Kept = null, string? LinkTarget = null, bool Shared = false, string? HalfWayTo = null, bool Several = false)
    {
        /// <summary>A client of the folder in use: the only ones this window changes.</summary>
        public bool Here => Folder.Current;
        /// <summary>The version a client that is not whole was on its way to, whether it is in use or kept.</summary>
        public string? Unfinished => InUse ? HalfWayTo : Kept?.HalfWayTo;
    }

    readonly Shared _shared;
    readonly string _folder = Environment.CurrentDirectory;
    readonly bool _downloadNewest;
    readonly ListBox _list = new() { MinHeight = 170, Margin = new Thickness(0, 0, 16, 0), HorizontalContentAlignment = HorizontalAlignment.Stretch };
    /// <summary>Why nothing can be changed just now (the game or a server runs): above the list, because it is the reason
    /// every changing button is off.</summary>
    readonly TextBlock _blocked = new() { Margin = new Thickness(0, 0, 0, 10), Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
    /// <summary>Under the list, of the line chosen: only what stops it from being used or changed here.</summary>
    readonly TextBlock _detail = new() { Margin = new Thickness(0, 12, 0, 0), Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
    readonly TextBlock _status = new() { Margin = new Thickness(0, 12, 0, 0), Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
    readonly ProgressBar _bar = new() { Height = 4, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed, Minimum = 0, Maximum = 1 };
    /// <summary>Beside the download button, only when there is a reason it is off or something it will go on from.</summary>
    readonly TextBlock _note = new() { Margin = new Thickness(12, 0, 0, 0), Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    /// <summary>The Steam account a download uses, once there is one.</summary>
    readonly TextBlock _steam = new() { Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
    readonly Button _use, _move, _copy, _delete, _open, _download, _forget, _stopButton, _close;
    /// <summary>The folder in use is a source copy: no Steam download and no copy of a linked client, which are a release's.</summary>
    readonly bool _source = !Repo.IsInstall;
    /// <summary>The versions the folder's LocalExile runs, oldest first; empty when nothing says.</summary>
    IReadOnlyList<string> _runs = [];
    /// <summary>For a client in use that is no version its LocalExile runs: the move to one found on this PC, when there is one.</summary>
    ClientUpdate.Plan? _plan;
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
    /// <summary>A download ended with a client that can be played: closing the window is the next step.</summary>
    bool _ready;
    /// <summary>The window is closed: nothing more is started from it.</summary>
    bool _closed;

    /// <summary>The folder's client changed: the window that opened this one reads the folder again.</summary>
    public bool Changed { get; private set; }

    /// <summary>The user asked for a copy in place of the folder's linked client: the Play page makes it, with its own
    /// question and progress, once this window is closed.</summary>
    public bool CopyRequested { get; private set; }

    /// <summary>The user asked for another client in place of one this LocalExile does not run: what opened this window
    /// carries the move out once it is closed, with the question and the steps a Play would have.</summary>
    public bool MoveRequested { get; private set; }

    static ClientVersionsWindow()
    {
        DepotTool.Bundled ??= () => typeof(ClientVersionsWindow).Assembly.GetManifestResourceStream("Bundled/DepotDownloader.zip");
    }

    /// <param name="downloadNewest">Opened by the Play page's Finish setup for a folder with no client, on the user's word
    /// there: the newest version its LocalExile runs is asked for at once (the sign-in and the size question still come).</param>
    public ClientVersionsWindow(Window? owner, Shared shared, bool downloadNewest = false)
    {
        _shared = shared;
        _downloadNewest = downloadNewest;
        DialogLayout.Configure(this, owner, L.Text("Game client"), 760);
        var root = new DockPanel { Margin = new Thickness(24) };

        // At the bottom: the Steam account, when there is one, and Close.
        var footer = new DockPanel { Margin = new Thickness(0, 20, 0, 0) };
        _close = new Button { Content = L.Text("Close"), IsCancel = true, MinWidth = 84 };
        _close.Click += (_, _) => Close();
        DockPanel.SetDock(_close, Dock.Right);
        footer.Children.Add(_close);
        var account = new StackPanel { Orientation = Orientation.Horizontal };
        _forget = new Button { Content = L.Text("Forget the sign-in"), ToolTip = L.Text("Waypoint forgets which Steam account to use. The next download asks to sign in again.") };
        _forget.Click += (_, _) => Forget();
        account.Children.Add(_steam);
        account.Children.Add(_forget);
        footer.Children.Add(account);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

        // Under the list: what stops the chosen line, what is happening, and, apart from the list, getting a version.
        var below = new StackPanel();
        _stopButton = new Button { Content = L.Text("Stop"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
        _stopButton.Click += (_, _) => _stop?.Cancel();
        below.Children.Add(_detail);
        below.Children.Add(_status);
        below.Children.Add(_bar);
        below.Children.Add(_stopButton);
        var line = new Border { Height = 1, Margin = new Thickness(0, 18, 0, 0) };
        line.SetResourceReference(Border.BackgroundProperty, "Shell.Border");
        below.Children.Add(line);
        var get = new DockPanel { Margin = new Thickness(0, 18, 0, 0) };
        _download = new Button { Content = L.Text("Download a version…") };
        _download.Click += (_, _) => _ = DownloadAsync();
        ToolTipService.SetShowOnDisabled(_download, true);
        DockPanel.SetDock(_download, Dock.Left);
        get.Children.Add(_download);
        get.Children.Add(_note);
        below.Children.Add(get);
        DockPanel.SetDock(below, Dock.Bottom);
        root.Children.Add(below);

        DockPanel.SetDock(_blocked, Dock.Top);
        root.Children.Add(_blocked);

        // Beside the list: only the buttons the chosen line has a use for.
        _use = Action(L.Text("Use this version"), () => _ = UseAsync());
        _move = Action(L.Text("Use another client…"), () => { MoveRequested = true; Close(); });
        _copy = Action(L.Text("Make a copy…"), () => { CopyRequested = true; Close(); });
        _delete = Action(L.Text("Delete this copy…"), () => _ = DeleteAsync());
        _open = Action(L.Text("Open folder"), OpenFolder);
        var buttons = new StackPanel { Width = 170 };
        foreach (var button in new[] { _use, _move, _copy, _delete, _open })
        {
            button.Visibility = Visibility.Collapsed;
            buttons.Children.Add(button);
        }
        DockPanel.SetDock(buttons, Dock.Right);
        root.Children.Add(buttons);
        root.Children.Add(_list);
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        _list.SelectionChanged += (_, _) => Enable();
        foreach (var text in new[] { _detail, _status, _note, _steam }) text.SetResourceReference(StyleProperty, "Shell.Secondary");
        // The secondary style is not the window's own text style: these lines wrap by their own word.
        foreach (var text in new[] { _detail, _status, _note }) text.TextWrapping = TextWrapping.Wrap;
        _blocked.SetResourceReference(ForegroundProperty, "Shell.Warning");
        _blocked.Text = L.Text("Close the game and stop the servers to change the game client.");
        // The buttons follow what runs: LocalExile's own setup starts its tools for a moment, and a game may close meanwhile.
        void Sampled(IReadOnlyList<ServiceSample> _) => Dispatcher.BeginInvoke(() => { if (!_busy) Enable(); });
        _shared.Monitor.Sampled += Sampled;
        Closed += (_, _) =>
        {
            _closed = true;
            _shared.Monitor.Sampled -= Sampled;
        };
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
                    Say(L.Text("Deleting …"), working: true);
                    await Task.Run(() => ClientVersions.FinishDeletes(_folder));
                    Say("");
                }, stoppable: false);
            // The window can be closed while the folders are read: a closed window starts no download.
            if (_downloadNewest && !_closed && !Running && _layout == ClientLayout.None && !Rows.Any(row => row.Here) && ClientVersions.Newest(_offered) is { } newest)
                await GetAsync(newest);
        };
    }

    Button Action(string text, Action run)
    {
        var button = new Button { Content = text, Margin = new Thickness(0, 0, 0, 8), HorizontalAlignment = HorizontalAlignment.Stretch };
        button.Click += (_, _) => run();
        return button;
    }

    IEnumerable<Row> Rows => _list.Items.OfType<ListBoxItem>().Select(item => item.Tag).OfType<Row>();
    Row? Selected => (_list.SelectedItem as ListBoxItem)?.Tag as Row;
    /// <summary>The build a client of the folder in use that is half-way to another one was on its way to. A mark that
    /// names only the version means the latest build of it that LocalExile lists (a hotfix, not its release).</summary>
    ListedBuild? Unfinished => _unfinished is null ? null : (_builds ?? []).LastOrDefault(b => b.FromSteam && Resumes(b));
    bool Running => _shared.Monitor.Latest.Any(s => s.Running) || _shared.IsLiveRunning;
    string? Account => SteamDownload.IsAccountName(_shared.Settings.SteamAccount) ? _shared.Settings.SteamAccount : null;
    /// <summary>A build by its version and LocalExile's own name for it: "Path of Exile 3.29.3c: 3.29.3.5 hotfix of
    /// 2026-11-09, build 328126". One version can be several builds.</summary>
    static string Build(ListedBuild build) => build.Name.Length > 0 ? Version(build.Version) + ": " + build.Name : Version(build.Version);

    static string Version(string? version) => version is null ? L.Text("Path of Exile, version not known") : L.Text("Path of Exile {0}", version);

    /// <summary>What a question calls a client: its version, and which build of it when the list has several.</summary>
    string Label(string? version, ListedBuild? build) => build is not null && ClientVersions.Several(_builds ?? [], build.Version) ? Build(build) : Version(build?.Version ?? version);

    /// <summary>Whether the client in use is half-way to this very build.</summary>
    bool Resumes(ListedBuild build) => _unfinished == build.Version && (_unfinishedHash is null || _unfinishedHash == build.ExecutableSha256);

    /// <summary>Reads the folders again, off the UI thread: sizes are summed and executables hashed.</summary>
    async Task RefreshAsync()
    {
        string? chosen = Selected?.Client;
        var paths = FolderList.Paths(_shared.Settings, _folder);
        var (layout, current, unfinished, builds, rows, started, linked, tooOld, runs, plan) = await Task.Run(() =>
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
                    bool Several(ListedBuild? build) => build is not null && theirs is not null && ClientVersions.Several(theirs, build.Version);
                    string client = Path.Combine(known.Path, "Client");
                    if (ClientVersions.FilesOf(client) is var files and not ClientLayout.None)
                    {
                        // A linked client is shown where it is used, with where it leads: another LocalExile folder's
                        // client, or a game folder outside LocalExile (the retail game's own).
                        string? target = Launcher.Core.ClientSettings.IsLink(client) ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(ClientLocator.RealPath(client))) : null;
                        var build = Named(client);
                        found.Add(new(known, client, files, InUse: true, FolderList.ClientOf(known.Path), build, LinkTarget: target,
                            Shared: target is not null && OwnClient.InLocalExile(target), HalfWayTo: ClientVersions.Unfinished(known.Path), Several: Several(build)));
                    }
                    foreach (var kept in ClientVersions.Kept(known.Path))
                    {
                        var build = Named(kept.Folder);
                        found.Add(new(known, kept.Folder, ClientVersions.FilesOf(kept.Folder), InUse: false, kept.Version, build, kept, Several: Several(build)));
                    }
                }
                // One folder that cannot be read (a drive gone, a launcher that will not start) leaves the others their lines.
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or InvalidOperationException
                    or System.ComponentModel.Win32Exception or System.Security.SecurityException or AggregateException) { }
            }
            string next = Path.Combine(_folder, ClientVersions.NextFolder);
            // A client that is no version this LocalExile runs: a quick look for one that is, as before a Play.
            var mine = found.FirstOrDefault(r => r.Here && r.InUse);
            IReadOnlyList<string> versions = [.. ClientLocator.Supported(_folder).Reverse()];
            var move = mine is not null && !Fits(mine, listed, versions) ? ClientUpdate.Check(_folder) : null;
            return (ClientVersions.Layout(_folder), FolderList.ClientOf(_folder), ClientVersions.UnfinishedBuild(_folder), listed, found,
                Directory.Exists(next) ? ClientVersions.SizeOf(next) : 0, _source ? null : OwnClient.Check(_folder), LocalExileBuilds.TooOld(_folder), versions, move);
        });
        _started = started;
        _linked = linked;
        (_runs, _plan) = (runs, plan);
        (_layout, _current, _unfinished, _unfinishedHash, _builds) = (layout, current, unfinished?.Version, unfinished?.ExecutableSha256, builds);
        _currentBuild = rows.FirstOrDefault(r => r.Here && r.InUse)?.Build;
        // A copy set aside half-way is not that build whole: the build stays on offer.
        _offered = ClientVersions.Offered(builds ?? [], _currentBuild, _unfinished is null, rows.Where(r => r.Here && r.Kept is { HalfWayTo: null }).Select(r => r.Build));

        // This folder's clients first; the rest under their heading, quieter, as the game settings window lists folders.
        _list.Items.Clear();
        var here = rows.Where(r => r.Here).ToList();
        var others = rows.Where(r => !r.Here).ToList();
        foreach (var row in here) Add(row, here);
        if (here.Count == 0) _list.Items.Add(Heading(L.Text("No game client in this folder yet."), FontWeights.Normal, top: 0));
        if (others.Count > 0)
        {
            _list.Items.Add(Heading(L.Text("Other folders"), FontWeights.SemiBold, top: 8));
            foreach (var row in others) Add(row, here);
        }
        var lines = _list.Items.OfType<ListBoxItem>().Where(item => item.Tag is Row).ToList();
        _list.SelectedItem = lines.FirstOrDefault(item => ((Row)item.Tag).Client == chosen) ?? lines.FirstOrDefault(item => ((Row)item.Tag).Here);

        // Beside the download button: only why it is off, or what it will go on from.
        string note = _source ? L.Text("Waypoint cannot prepare a client from Steam for a source copy yet.")
            : !DepotTool.Supported ? L.Text("The Steam download works on 64-bit Windows only for now.")
            // No list is a LocalExile from before it had one, or one that did not answer just now: not the same thing.
            : builds is null ? (tooOld ? L.Text("This LocalExile lists no versions to download. LocalExile v0.5.0-beta and later do.")
                : L.Text("Waypoint could not read the versions this LocalExile runs. Open this window again."))
            : _offered.Count == 0 && builds.Any(b => b.FromSteam) ? L.Text("This folder already has every version Steam has for this LocalExile.") : "";
        if (started > 100_000_000) note = (note.Length > 0 ? note + " " : "") + L.Text("{0:0.#} GB of an unfinished download is kept in this folder.", started / 1e9);
        _note.Text = note;
        _note.Visibility = note.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        Enable();
    }

    /// <summary>Whether a client in use is a version its folder's LocalExile runs: one of the builds it lists, or, for a
    /// LocalExile with no list, a version it names. A client half-way to another version says that instead, and one
    /// nothing can be said about counts as fitting: LocalExile's own check has the last word.</summary>
    static bool Fits(Row row, IReadOnlyList<ListedBuild>? builds, IReadOnlyList<string> runs) =>
        row.HalfWayTo is not null || (builds is not null ? row.Build is not null : row.Version is null || runs.Count == 0 || runs.Contains(row.Version));

    /// <summary>The client the folder in use takes is no version its LocalExile runs.</summary>
    bool Unfit(Row? row) => row is { Here: true, InUse: true } && !Fits(row, _builds, _runs);

    /// <summary>The versions this LocalExile runs, as a line says them: "3.29.3c, 3.29.3d".</summary>
    string Runs => string.Join(", ", _runs);

    /// <summary>A line of the list that is no client: the heading of the other folders, or the word that this one has none.</summary>
    static ListBoxItem Heading(string text, FontWeight weight, double top)
    {
        var item = new ListBoxItem { Content = text, IsEnabled = false, Focusable = false, Margin = new Thickness(0, top, 0, 0), FontWeight = weight };
        item.SetResourceReference(ForegroundProperty, "Shell.TextSecondary");
        return item;
    }

    /// <summary>What the second line of a client says, piece by piece (true: in the warning colour). A client of the folder
    /// in use says what it is and what is particular about it; one of another folder also says whose it is and where.</summary>
    List<(string Text, bool Warn)> About(Row row, IReadOnlyList<Row> here)
    {
        var parts = new List<(string, bool)> { (row.Files == ClientLayout.Steam ? L.Text("Steam version") : L.Text("Standalone version"), false) };
        if (!row.Here) parts.Add((row.Folder.Title, false));
        if (row.Kept is { } kept) parts.Add((L.Text("kept, {0:0.#} GB", kept.Bytes / 1e9), false));
        if (row.Unfinished is { } unfinished) parts.Add((L.Text("half-way to {0}", Version(unfinished)), row.Here));
        if (Unfit(row)) parts.Add((L.Text("does not fit this LocalExile"), true));
        if (row.LinkTarget is not null) parts.Add((row.Shared ? L.Text("shares another LocalExile folder's client") : L.Text("linked to a game folder"), false));
        // Two kept copies that read the same are told apart by their folders' names.
        if (row is { Here: true, Kept: { } twin } && here.Any(o => o != row && o.Kept is not null && o.Version == row.Version
                && o.Build?.ExecutableSha256 == row.Build?.ExecutableSha256 && o.Unfinished == row.Unfinished))
            parts.Add((twin.Name, false));
        return parts;
    }

    /// <summary>Where a client is. For a linked one, the link and then where it leads: the end of the line is what stays
    /// when it is too long, and there the files are.</summary>
    static string Where(Row row) => row.LinkTarget is { } target ? row.Client + "  →  " + target : row.Client;

    /// <summary>A line drawn as the sidebar's folder list draws a folder: an icon, the version, what it is underneath, then
    /// where it is (<see cref="PathLine"/>: the end of the path stays), and "In use" on the client the folder in use takes.</summary>
    void Add(Row row, IReadOnlyList<Row> here)
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
        var version = new Run(Version(row.Version)) { FontWeight = FontWeights.SemiBold };
        if (!row.Here) version.SetResourceReference(TextElement.ForegroundProperty, "Shell.TextSecondary");
        title.Inlines.Add(version);
        // Which build of the version, in LocalExile's own words, where the version is more than one (a hotfix).
        if (row is { Several: true, Build.Name: { Length: > 0 } name })
        {
            var build = new Run("  " + name);
            build.SetResourceReference(TextElement.ForegroundProperty, "Shell.TextSecondary");
            title.Inlines.Add(build);
        }
        var parts = About(row, here);
        var about = new TextBlock { FontSize = ThemeResources.Font(11.5), Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap };
        about.SetResourceReference(TextBlock.ForegroundProperty, "Shell.TextSecondary");
        foreach (var (text, warn) in parts)
        {
            if (about.Inlines.Count > 0) about.Inlines.Add(new Run("  ·  "));
            var part = new Run(text);
            if (warn) part.SetResourceReference(TextElement.ForegroundProperty, "Shell.Warning");
            about.Inlines.Add(part);
        }
        var where = new TextBlock { FontSize = ThemeResources.Font(11.5), Margin = new Thickness(0, 2, 0, 0) };
        where.SetResourceReference(TextBlock.ForegroundProperty, "Shell.TextSecondary");
        PathLine.SetPath(where, Where(row));
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        words.Children.Add(title);
        words.Children.Add(about);
        words.Children.Add(where);
        line.Children.Add(words);
        var item = new ListBoxItem { Content = line, Tag = row, Padding = new Thickness(10, 9, 10, 10), HorizontalContentAlignment = HorizontalAlignment.Stretch,
            ToolTip = row.Build is { Name.Length: > 0 } named ? Where(row) + "\n" + Build(named) : Where(row) };
        AutomationProperties.SetName(item, string.Join(", ", new[] { Version(row.Version), row.Build?.Name, string.Join(", ", parts.Select(p => p.Text)),
            row.Here && row.InUse ? L.Text("In use") : null, row.Client }.Where(part => part is { Length: > 0 })));
        _list.Items.Add(item);
    }

    /// <summary>Why the folder in use, whose client is a link, takes no other client in its place.</summary>
    string LinkReason()
    {
        if (_linked is not null) return L.Text("This folder's game client is a link to another folder. Make a copy of it first.");
        var used = Rows.FirstOrDefault(row => row.Here && row.InUse);
        // A link that leads to no game has no line in the list: where it leads is read from the folder.
        return used is { Shared: true } ? L.Text("This folder shares the game client of another LocalExile folder: what changes there changes here.")
            : L.Text("Client is a link to {0}, not a copy.", used?.LinkTarget ?? ClientLocator.RealPath(Path.Combine(_folder, "Client")));
    }

    /// <summary>Under the list, of the line chosen: what stops it from being used or changed here. Nothing for a line that
    /// is simply fine.</summary>
    string Detail(Row? row)
    {
        if (row is null) return "";
        if (!row.Here)
            return row.Folder.Kind == FolderKind.Source ? L.Text("This client belongs to a source copy of LocalExile, which looks after its own client.")
                : L.Text("To change this client, choose its folder in the sidebar.");
        if (row.Kept is { HalfWayTo: not null } half)
            return L.Text("This client is incomplete and cannot be used.")
                // Not knowing the list is not the same as the list lacking the build: nothing then says "delete it".
                + (_builds is null ? " " + L.Text("Waypoint could not read the versions this LocalExile runs. Open this window again.")
                    : HalfWay(half) is null ? " " + L.Text("Steam does not have that version for this LocalExile. This copy can only be deleted.") : "");
        if (row.Kept is not null)
            return _unfinished is not null ? L.Text("The game client in use is half-way to another version. Finish that switch first.")
                : _layout == ClientLayout.Link ? LinkReason() : "";
        // What stops it from being played comes before what is only worth knowing about it.
        if (Unfit(row))
            return (_runs.Count > 0 ? L.Text("This LocalExile runs Path of Exile {0}.", Runs) : ClientExtractionFailure.Refused)
                + (_plan is { Kind: ClientUpdate.Kind.Replace, Found: { } found } ? " " + L.Text("A {0} client is on this PC: {1}", _plan.Need, found) : "");
        if (row.LinkTarget is { } target)
            return row.Shared ? L.Text("This folder shares the game client of another LocalExile folder: what changes there changes here.")
                // LocalExile's warning is its release launcher's: a source copy links on its developer's own word.
                : _source ? "" : OwnClient.Notice(target);
        return row.HalfWayTo is not null ? L.Text("This client is incomplete and cannot be used.") : "";
    }

    /// <summary>The buttons follow the chosen line and what runs. A button the line has no use for is not shown; one that
    /// cannot run just now is shown off, with its reason on the detail line, the warning line or its tooltip. The likely
    /// next step, and only that, has the primary style.</summary>
    void Enable()
    {
        var row = Selected;
        bool free = !_busy && !Running;
        // A link is someone else's folder: nothing is swapped into its place or downloaded over it, and a switch that did
        // not finish there is that folder's to finish.
        bool link = _layout == ClientLayout.Link;
        bool resume = !link && row is { Here: true, InUse: true } && Unfinished is not null;
        // A copy that was set aside half-way is made whole where it is, as the build it was on its way to.
        bool half = row is { Here: true, Kept.HalfWayTo: not null };
        bool finish = half && HalfWay(row!.Kept!) is not null && DepotTool.Supported;
        bool kept = row is { Here: true, Kept.HalfWayTo: null };
        bool canUse = kept && !link && _unfinished is null || resume || finish;
        _use.Visibility = kept || resume || finish ? Visibility.Visible : Visibility.Collapsed;
        _use.IsEnabled = free && canUse;
        _use.Content = resume ? L.Text("Finish the switch") : half ? L.Text("Finish this copy") : L.Text("Use this version");
        // A client this LocalExile does not run is not worth a copy: the way on is one that fits, when this PC has it.
        bool unfit = Unfit(row);
        bool canMove = unfit && _plan is { Kind: ClientUpdate.Kind.Replace, Found: not null };
        _move.Visibility = canMove ? Visibility.Visible : Visibility.Collapsed;
        _move.IsEnabled = free;
        _copy.Visibility = row is { Here: true, InUse: true } && _linked is not null && !unfit ? Visibility.Visible : Visibility.Collapsed;
        _copy.IsEnabled = free;
        _delete.Visibility = row is { Here: true, Kept: not null } ? Visibility.Visible : Visibility.Collapsed;
        _delete.IsEnabled = free;
        _open.Visibility = row is not null ? Visibility.Visible : Visibility.Collapsed;
        _detail.Text = Detail(row);
        _detail.Visibility = _detail.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        bool canDownload = !link && !_source && DepotTool.Supported && _offered.Count > 0;
        _download.IsEnabled = free && canDownload;
        _download.ToolTip = canDownload ? null : link && !_source ? LinkReason() : _note.Text.Length > 0 ? _note.Text : null;
        _steam.Text = Account is { } account ? L.Text("Steam: signed in as {0}", account) : "";
        _steam.Visibility = _forget.Visibility = Account is null ? Visibility.Collapsed : Visibility.Visible;
        _forget.IsEnabled = free;
        _blocked.Visibility = !_busy && Running ? Visibility.Visible : Visibility.Collapsed;
        Lead(_ready && !_busy ? _close
            : _use.Visibility == Visibility.Visible && canUse ? _use
            : canMove ? _move
            : _copy.Visibility == Visibility.Visible ? _copy
            : canDownload ? _download : null);
    }

    /// <summary>The one button that is the likely next step: it guides in place of a sentence.</summary>
    void Lead(Button? lead)
    {
        foreach (var button in new[] { _use, _move, _copy, _download, _close })
        {
            if (button == lead) button.SetResourceReference(StyleProperty, "Primary");
            else button.ClearValue(StyleProperty);
        }
    }

    void Say(string text, double? fraction = null, bool working = false)
    {
        _status.Text = text;
        _status.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
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
        _ready = false;
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

    /// <summary>The Steam sign-in, when a download needs one: the tool's QR code, or from that window its password prompt.</summary>
    async Task<bool> SignInAsync(CancellationToken stop)
    {
        if (AnyBuild is not { } build || !await ToolReadyAsync(stop)) return false;
        Say(L.Text("Waiting for the Steam sign-in …"), working: true);
        var window = new SteamSignInWindow(this, Tool, build, Scratch, Log);
        window.ShowDialog();
        string? account = window.UsePassword ? await PasswordAsync(build, stop) : window.Account;
        if (account is null || !SteamDownload.IsAccountName(account))
        {
            Say(L.Text("Not signed in to Steam."));
            return false;
        }
        _shared.Settings.SteamAccount = account;
        _shared.Settings.Save();
        Enable();
        return true;
    }

    /// <summary>For someone without the Steam mobile app: the tool's own prompts, in its own console window. The account's
    /// name when Steam took the sign-in.</summary>
    async Task<string?> PasswordAsync(ListedBuild build, CancellationToken stop)
    {
        if (NameDialog.Ask(this, L.Text("Sign in with a password"), L.Text("Your Steam account name (what you sign in to Steam with):"), Account ?? "") is not { } name) return null;
        name = name.Trim();
        if (!SteamDownload.IsAccountName(name))
        {
            Ask(DialogKind.Warning, L.Text("Sign in with a password"), L.Text("That does not look like a Steam account name. It is the name you sign in with, not your profile's display name."), null, L.Text("OK"));
            return null;
        }
        Say(L.Text("Enter your password and Steam Guard code in the window that opened. Waypoint does not see them."), working: true);
        return await SteamDownload.PasswordSignInAsync(Tool, build, name, stop) ? name : null;
    }

    void Forget()
    {
        _shared.Settings.SteamAccount = null;
        _shared.Settings.Save();
        Say(L.Text("Forgotten. The download tool keeps its own sign-in on this PC until Steam ends it."));
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
    ListedBuild? HalfWay(KeptClient kept) => (_builds ?? []).LastOrDefault(b => b.FromSteam && b.Version == kept.HalfWayTo
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
            if (result.Outcome == SteamOutcome.SignInNeeded && await SignInAsync(stop))
            {
                Say(L.Text("Downloading from Steam. {0}", ""), working: true);
                result = await Run();
            }
            Say(result.Outcome == SteamOutcome.Done ? L.Text("{0} is whole now, and kept in this folder.", Label(build.Version, build)) : SteamDownload.Explain(result));
        });
    }

    /// <summary>Apart from the list: which of the versions this folder's LocalExile runs to download from Steam into it.</summary>
    async Task DownloadAsync()
    {
        if (_busy || _offered.Count == 0) return;
        var pick = new PickWindow(this, _offered);
        if (pick.ShowDialog() == true && pick.Chosen is { } build) await GetAsync(build);
    }

    /// <summary>The versions a download can be, newest first, each with LocalExile's own name for the build. Its title is
    /// its instruction.</summary>
    sealed class PickWindow : FormWindow
    {
        public ListedBuild? Chosen { get; private set; }

        public PickWindow(Window owner, IReadOnlyList<ListedBuild> builds)
        {
            DialogLayout.Configure(this, owner, L.Text("Download a game version"), 520);
            var root = new DockPanel { Margin = new Thickness(24) };
            var list = new ListBox { HorizontalContentAlignment = HorizontalAlignment.Stretch };
            void Accept()
            {
                if ((list.SelectedItem as ListBoxItem)?.Tag is not ListedBuild build) return;
                Chosen = build;
                DialogResult = true;
            }
            var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
            // Enter is Cancel, as in every question of this window: with a way named in Settings no question follows the pick.
            var download = new Button { Content = L.Text("Download"), Margin = new Thickness(0, 0, 8, 0) };
            download.SetResourceReference(StyleProperty, "Primary");
            download.Click += (_, _) => Accept();
            buttons.Children.Add(download);
            buttons.Children.Add(new Button { Content = L.Text("Cancel"), IsCancel = true, IsDefault = true });
            DockPanel.SetDock(buttons, Dock.Bottom);
            root.Children.Add(buttons);
            foreach (var build in builds)
            {
                var text = new TextBlock { FontSize = ThemeResources.Font(13), TextTrimming = TextTrimming.CharacterEllipsis };
                text.Inlines.Add(new Run(Version(build.Version)) { FontWeight = FontWeights.SemiBold });
                var name = new Run("  " + build.Name);
                name.SetResourceReference(TextElement.ForegroundProperty, "Shell.TextSecondary");
                text.Inlines.Add(name);
                var item = new ListBoxItem { Content = text, Tag = build, Padding = new Thickness(10, 9, 10, 10) };
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
    /// room, the switch, and LocalExile's setup of what came in. Each step says itself as it starts.</summary>
    async Task GetAsync(ListedBuild build)
    {
        await WorkAsync(L.Text("Game client"), async stop =>
        {
            if (_layout == ClientLayout.Link)
            {
                Say(LinkReason());
                return;
            }
            if (!await ToolReadyAsync(stop)) return;
            if (Account is null && !await SignInAsync(stop)) return;
            Say(L.Text("Asking Steam how large {0} is …", Version(build.Version)), working: true);
            var (bytes, sized) = await SteamDownload.SizeAsync(Tool, build, Account!, Scratch, Log, stop);
            if (sized.Outcome == SteamOutcome.SignInNeeded && await SignInAsync(stop))
            {
                // The sign-in's own words are over: the step says itself again.
                Say(L.Text("Asking Steam how large {0} is …", Version(build.Version)), working: true);
                (bytes, sized) = await SteamDownload.SizeAsync(Tool, build, Account!, Scratch, Log, stop);
            }
            if (bytes is not { } size)
            {
                Say(SteamDownload.Explain(sized));
                return;
            }
            long current = _layout == ClientLayout.None ? 0 : await Task.Run(() => ClientVersions.SizeOf(Path.Combine(_folder, "Client")));
            if (Way(build, size, current, ClientCopy.FreeSpace(_folder)) is not { } way)
            {
                Say("");
                return;
            }
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
    /// question, with only the ways the drive has room for. Null: not now. The question's title names the build; its text
    /// gives the size and the room, and for each way what it costs.</summary>
    SwitchWay? Way(ListedBuild build, long buildBytes, long currentBytes, long? free)
    {
        string target = Label(build.Version, build), title = L.Text("Get {0}?", target);
        long inPlace = ClientVersions.Needed(_layout, SwitchWay.UpdateInPlace, buildBytes, currentBytes), both = ClientVersions.Needed(_layout, SwitchWay.KeepBoth, buildBytes, currentBytes);
        // What an earlier try left in the folder is room this one does not need again; the sentence gives the drive's own figure.
        bool canInPlace = free is not { } room || room + _started >= inPlace, canBoth = free is not { } space || space + _started >= both;
        string roomText = free is { } f ? L.Text("{0:0.#} GB is free on this drive.", f / 1e9) : "";
        // A client from Steam is brought to the other version where it is, so only what differs comes; anything else is
        // downloaded whole.
        bool shares = _layout == ClientLayout.Steam;
        string facts = (shares ? "" : L.Text("The download is {0:0.#} GB.", buildBytes / 1e9) + " ") + roomText;
        if (_layout == ClientLayout.None)
        {
            if (canBoth) return Ask(DialogKind.Question, title, facts, null, L.Text("Download"), L.Text("Cancel")) == 0 ? SwitchWay.KeepBoth : null;
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
            return Ask(DialogKind.Question, title, L.Text("The client in use is half-way to {0} and cannot be kept. It is updated in place.", Version(_unfinished))
                + " " + roomText, null, L.Text("Update in place"), L.Text("Cancel")) == 0 ? SwitchWay.UpdateInPlace : null;
        }
        string setting = _shared.Settings.ClientSwitch;
        // "Always update in place" is for a client from Steam. A standalone one would be deleted for good, so it is asked about.
        // It also asks when an unfinished keep-both left a download behind, which updating in place deletes: the question says so.
        if (setting == nameof(SwitchWay.UpdateInPlace) && canInPlace && shares && _started <= 100_000_000) return SwitchWay.UpdateInPlace;
        if (setting == nameof(SwitchWay.KeepBoth) && canBoth) return SwitchWay.KeepBoth;
        if (!canInPlace && !canBoth)
        {
            Ask(DialogKind.Warning, title, L.Text("The drive is too full: {0} needs {1:0.#} GB.", target, Math.Min(inPlace, both) / 1e9) + " " + roomText, null, L.Text("OK"));
            return null;
        }
        string replace = shares
            ? L.Text("Update in place: it needs no extra space, but {0} is removed from this PC.", Label(_current, _currentBuild))
                // What an unfinished keep-both left is not needed for this way, and goes.
                + (_started > 100_000_000 ? " " + L.Text("It also deletes the {0:0.#} GB already downloaded.", _started / 1e9) : "")
            : L.Text("Replace: the client in use is deleted permanently. A standalone client cannot be downloaded again.");
        string keep = L.Text("Keep both: it needs {0:0.#} GB more, and you can switch back at any time.", Math.Max(0, both - ClientVersions.Margin - _started) / 1e9);
        var texts = new List<string> { facts };
        var ways = new List<(string Button, SwitchWay Way)>();
        // A standalone client cannot come back from Steam, so keeping it is the first choice there.
        if (canBoth && !shares) { texts.Add(keep); ways.Add((L.Text("Keep both"), SwitchWay.KeepBoth)); }
        if (canInPlace) { texts.Add(replace); ways.Add((shares ? L.Text("Update in place") : L.Text("Replace"), SwitchWay.UpdateInPlace)); }
        if (canBoth && shares) { texts.Add(keep); ways.Add((L.Text("Keep both"), SwitchWay.KeepBoth)); }
        if (ways.Count == 1) texts.Add(L.Text("The drive has room for only one way."));
        int chosen = Ask(DialogKind.Question, title, string.Join("\n\n", texts.Where(text => text.Length > 0)), null, [.. ways.Select(w => w.Button), L.Text("Cancel")]);
        return chosen >= 0 && chosen < ways.Count ? ways[chosen].Way : null;
    }

    /// <summary>LocalExile's own setup, which makes Game.exe and the copy patched for this folder's keys from the client
    /// that just came in. A folder that still needs something from its user (an account) is finished from the Play page,
    /// which says so itself.</summary>
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
            _ready = result.Code == 0;
            Say(_ready ? L.Text("{0} is ready to play.", Version(build.Version)) : L.Text("{0} is downloaded.", Version(build.Version)));
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            Say(L.Text("{0} is downloaded.", Version(build.Version)));
            Log(e.Message);
        }
    }
}
