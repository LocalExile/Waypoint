using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MaterialDesignThemes.Wpf;
using Microsoft.Win32;

namespace Waypoint;

sealed record RequirementRow(Prerequisite Item, string Url)
{
    public string Name => L.Text(Item.Name);
    public string Detail => L.Text(Item.Detail);
    public PackIconKind Icon => Item.Ready ? PackIconKind.CheckCircleOutline : PackIconKind.AlertCircleOutline;
    public Brush Brush => Theme.Brush(Item.Ready ? "Shell.Good" : "Shell.Warning");
    public string Action => L.Text("Get {0}", Item.Name);
    public Visibility ActionShown => Item.Ready ? Visibility.Collapsed : Visibility.Visible;
}

sealed record CheckoutRow(string Root, string Summary);

/// <summary>A step of the setup with what is happening in it, updated in place.</summary>
sealed class StepRow(string name) : INotifyPropertyChanged
{
    public string Name { get; } = name;
    public string Detail { get; private set; } = "";
    public Visibility DetailShown => Detail.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public SetupState State { get; private set; } = SetupState.Pending;
    public PackIconKind Icon => State switch
    {
        SetupState.Running => PackIconKind.ProgressClock,
        SetupState.Done or SetupState.Skipped => PackIconKind.CheckCircleOutline,
        SetupState.Stopped => PackIconKind.PauseCircleOutline,
        SetupState.Failed => PackIconKind.CloseCircleOutline,
        _ => PackIconKind.CircleOutline,
    };
    public Brush Brush => Theme.Brush(State switch
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

/// <summary>
/// The setup guide: what the first start shows when there is no checkout yet, and the Setup section's Run setup guide later.
/// It only looks until the player presses Start: then every step runs through <see cref="SetupSequence"/>, and Stop ends the
/// one that runs. A step that must not be cut short finishes first, so the run stops within seconds at worst. Everything is
/// resumable, so Continue, or the guide opened another day, skips what is done.
/// </summary>
public partial class SetupWizard : FormWindow
{
    enum Page { Welcome, Requirements, Checkout, Client, Run, Done }

    static readonly string[] PageNames = ["Welcome", "Tools", "LocalExile", "Game client", "Set up"];
    /// <summary>Roughly what a checkout takes with its client and generated data.</summary>
    const long NeededBytes = 75_000_000_000;

    readonly Settings _settings;
    readonly string? _current;
    readonly RuntimeClient _bridge = new(BridgeScripts.Extract());
    readonly ObservableCollection<StepRow> _steps = [];
    readonly ObservableCollection<CheckoutRow> _checkouts = [];
    readonly ObservableCollection<FoundClient> _clients = [];
    readonly StringBuilder _log = new();
    readonly CancellationTokenSource _closing = new();
    Page _page = Page.Welcome;
    CancellationTokenSource? _stop;
    Task? _running;
    SetupOutcome? _outcome;
    bool _searchedCheckouts, _clientsSearched, _closeWhenStopped, _portsHeld;

    /// <summary>The checkout the guide ended with; null when it ended without one.</summary>
    public string? Checkout { get; private set; }
    /// <summary>The player skipped the guide.</summary>
    public bool Skipped { get; private set; }
    /// <summary>The player wants the Accounts section's Create dialog next.</summary>
    public bool CreateAccountNext { get; private set; }

    internal SetupWizard(Settings settings, Window? owner, string? current)
    {
        _settings = settings;
        _current = current;
        Checkout = current;
        InitializeComponent();
        DialogLayout.Configure(this, owner, L.Text("Set up LocalExile"), 880);
        SizeToContent = SizeToContent.Manual;
        MaxHeight = SystemParameters.WorkArea.Height - 20;
        MinHeight = 520;
        Height = Math.Min(640, MaxHeight);
        CheckoutList.ItemsSource = _checkouts;
        ClientList.ItemsSource = _clients;
        StepList.ItemsSource = _steps;
        DownloadFolder.Text = SuggestedFolder();
        ExistingChoice.IsChecked = true;
        Closed += (_, _) => _closing.Cancel();
        Show(Page.Welcome);
    }

    bool Running => _running is { IsCompleted: false };

    void Show(Page page)
    {
        _page = page;
        (FrameworkElement, Page)[] pages = [(WelcomePage, Page.Welcome), (RequirementsPage, Page.Requirements), (CheckoutPage, Page.Checkout),
            (ClientPage, Page.Client), (RunPage, Page.Run), (DonePage, Page.Done)];
        foreach (var (element, shown) in pages) element.Visibility = shown == page ? Visibility.Visible : Visibility.Collapsed;
        Rail.Children.Clear();
        for (int i = 0; i < PageNames.Length; i++)
        {
            var step = new TextBlock { Text = L.Text(PageNames[i]), Margin = new Thickness(0, 0, 0, 10), FontWeight = (int)page == i ? FontWeights.SemiBold : FontWeights.Normal };
            step.SetResourceReference(TextBlock.ForegroundProperty, (int)page >= i ? "Shell.SidebarStrong" : "Shell.SidebarText");
            step.Opacity = (int)page >= i ? 1 : .65;
            Rail.Children.Add(step);
        }
        UpdateButtons();
        switch (page)
        {
            case Page.Requirements: _ = CheckRequirementsAsync(); break;
            case Page.Checkout when !_searchedCheckouts: _ = FindCheckoutsAsync(); break;
            case Page.Client when !_clientsSearched: _ = FindClientsAsync(); break;
            case Page.Run when !Running && _outcome != SetupOutcome.Finished: PrepareRun(); break;
        }
    }

    void UpdateButtons()
    {
        bool running = Running;
        SkipButton.Visibility = _page == Page.Done ? Visibility.Collapsed : Visibility.Visible;
        SkipButton.IsEnabled = !running;
        SkipButton.Content = _page == Page.Run && _outcome is SetupOutcome.Stopped or SetupOutcome.Failed ? L.Text("Finish later") : L.Text("Skip setup");
        BackButton.Visibility = _page is Page.Welcome or Page.Done ? Visibility.Collapsed : Visibility.Visible;
        BackButton.IsEnabled = !running;
        NextButton.IsEnabled = _page switch
        {
            Page.Requirements => RequirementList.Items.OfType<RequirementRow>().Any() && RequirementList.Items.OfType<RequirementRow>().All(r => r.Item.Ready),
            Page.Checkout => ChosenCheckout() is not null,
            Page.Client => NoClient.IsChecked == true || ClientList.SelectedItem is FoundClient { Usable: true } || HasClient(ChosenCheckout()),
            Page.Run => !running || _stop?.IsCancellationRequested != true,
            _ => true,
        };
        NextButton.Content = _page switch
        {
            Page.Welcome => L.Text("Start"),
            Page.Run when running => _stop?.IsCancellationRequested == true ? L.Text("Stopping…") : L.Text("Stop"),
            Page.Run when _outcome == SetupOutcome.Failed => L.Text("Try again"),
            Page.Run when _outcome == SetupOutcome.Stopped => L.Text("Continue"),
            Page.Run => L.Text("Start setup"),
            Page.Done => L.Text("Open Waypoint"),
            _ => L.Text("Next"),
        };
    }

    void NextClicked(object sender, RoutedEventArgs e)
    {
        switch (_page)
        {
            case Page.Welcome: Show(Page.Requirements); break;
            case Page.Requirements: Show(Page.Checkout); break;
            case Page.Checkout:
                _clientsSearched = false;
                Show(Page.Client);
                break;
            case Page.Client: Show(Page.Run); break;
            case Page.Run when Running: RequestStop(); break;
            case Page.Run:
                _running = RunAsync();
                UpdateButtons();
                break;
            case Page.Done:
                CreateAccountNext = CreateAccount.IsChecked == true;
                Close();
                break;
        }
    }

    void BackClicked(object sender, RoutedEventArgs e) => Show(_page - 1);

    /// <summary>Skip on any page. Later, after a stop, it reads Finish later: the checkout stays, and the guide can be run again
    /// from the Setup section.</summary>
    void SkipClicked(object sender, RoutedEventArgs e)
    {
        Skipped = _outcome is null;
        Close();
    }

    void RequestStop()
    {
        _stop?.Cancel();
        RunStatus.Text = L.Text("Stopping: a step that writes the client or the keys finishes first.");
        UpdateButtons();
    }

    /// <summary>Closing the window during a run is a Stop: it closes once the run has stopped.</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (!Running) return;
        e.Cancel = true;
        _closeWhenStopped = true;
        RequestStop();
    }

    // --- Tools ---

    async void CheckRequirements(object sender, RoutedEventArgs e) => await CheckRequirementsAsync();

    async Task CheckRequirementsAsync()
    {
        RequirementsStatus.Text = L.Text("Checking…");
        var items = await Prerequisites.CheckAsync();
        RequirementList.ItemsSource = items.Select(i => new RequirementRow(i, i.Name switch
        {
            ".NET 10 SDK" => "https://dotnet.microsoft.com/en-us/download/dotnet/10.0",
            "Git" => "https://git-scm.com/downloads/win",
            "Python" => "https://www.python.org/downloads/windows/",
            _ => "https://nodejs.org/en/download",
        })).ToList();
        int missing = items.Count(i => !i.Ready);
        RequirementsStatus.Text = missing == 0 ? L.Text("Everything is installed.")
            : L.Text("{0} missing. Install them from their download pages, then check again; there is no need to restart Waypoint.", missing);
        UpdateButtons();
    }

    void GetRequirement(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is RequirementRow row) Open(row.Url);
    }

    static void Open(string target) => Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();

    // --- The checkout ---

    async Task FindCheckoutsAsync()
    {
        _searchedCheckouts = true;
        CheckoutStatus.Text = L.Text("Looking for LocalExile on this PC…");
        try
        {
            var roots = RepositoryDiscovery.DefaultRoots(AppContext.BaseDirectory, Environment.CurrentDirectory, _current ?? "");
            var result = await RepositoryLocator.FindAsync(roots, _closing.Token);
            foreach (var repository in result.Repositories)
                if (!_checkouts.Any(c => c.Root.Equals(repository.Root, StringComparison.OrdinalIgnoreCase))) _checkouts.Add(new(repository.Root, repository.Summary));
            CheckoutList.SelectedItem = _checkouts.FirstOrDefault(c => c.Root.Equals(_current, StringComparison.OrdinalIgnoreCase)) ?? _checkouts.FirstOrDefault();
            CheckoutStatus.Text = _checkouts.Count == 0 ? L.Text("LocalExile was not found on this PC. Download it, or browse to it if it is somewhere else.")
                : L.Text("Found {0} on this PC.", _checkouts.Count);
            if (_checkouts.Count == 0) DownloadChoice.IsChecked = true;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            CheckoutStatus.Text = L.Text("Automatic lookup could not finish. Browse to your checkout instead. ") + ex.Message;
        }
        UpdateButtons();
    }

    /// <summary>The drive of the player's home when the client fits there, else the one with the most room.</summary>
    static string SuggestedFolder()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (ClientCopy.FreeSpace(home) >= NeededBytes) return Path.Combine(home, "LocalExile");
        var best = DriveInfo.GetDrives().Where(d => d is { DriveType: DriveType.Fixed, IsReady: true }).MaxBy(d => d.AvailableFreeSpace);
        return best is null ? Path.Combine(home, "LocalExile") : Path.Combine(best.RootDirectory.FullName, "LocalExile");
    }

    /// <summary>Why a folder cannot take the download; null when it can.</summary>
    static string? FolderProblem(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathFullyQualified(folder)) return L.Text("Choose a full folder path.");
        // The adapter and live.ps1 pass the path through a command shell.
        if (folder.IndexOfAny(['&', '|', '<', '>', '^', '%', '!', '"']) >= 0) return L.Text("Choose a plain path: spaces are fine, but not & | < > ^ % ! or quotes.");
        if (Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any()) return L.Text("This folder is not empty. Choose a new or empty folder.");
        return null;
    }

    string? ChosenCheckout()
    {
        string folder = DownloadFolder.Text.Trim();
        // A download that finished is no longer an empty folder, and that is fine.
        if (DownloadChoice.IsChecked == true) return FolderProblem(folder) is null || RepositoryDiscovery.IsLocalExile(folder) ? folder : null;
        return (CheckoutList.SelectedItem as CheckoutRow)?.Root;
    }

    void CheckoutChoiceChanged(object sender, RoutedEventArgs e) => ShowDownloadNote();

    void CheckoutSelected(object sender, SelectionChangedEventArgs e)
    {
        if (CheckoutList.SelectedItem is not null) ExistingChoice.IsChecked = true;
        UpdateButtons();
    }

    void DownloadFolderChanged(object sender, TextChangedEventArgs e) => ShowDownloadNote();

    void ShowDownloadNote()
    {
        if (DownloadNote is null) return;
        string folder = DownloadFolder.Text.Trim();
        string? problem = FolderProblem(folder);
        DownloadNote.Text = RepositoryDiscovery.IsLocalExile(folder) ? L.Text("LocalExile is already downloaded here.") : problem ?? (ClientCopy.FreeSpace(folder) is { } free ? L.Text("{0:0.#} GB free on this drive; LocalExile with its client needs about {1:0} GB.", free / 1e9, NeededBytes / 1e9) : "");
        UpdateButtons();
    }

    void ChooseDownloadFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = L.Text("Choose where to download LocalExile") };
        if (dialog.ShowDialog(this) != true) return;
        DownloadChoice.IsChecked = true;
        // An empty folder is taken as it is; otherwise LocalExile gets a folder of its own inside.
        DownloadFolder.Text = !Directory.EnumerateFileSystemEntries(dialog.FolderName).Any() ? dialog.FolderName : Path.Combine(dialog.FolderName, "LocalExile");
    }

    void BrowseCheckout(object sender, RoutedEventArgs e)
    {
        if (App.ChooseServer(this) is not { } root) return;
        var row = _checkouts.FirstOrDefault(c => c.Root.Equals(root, StringComparison.OrdinalIgnoreCase));
        if (row is null) _checkouts.Add(row = new(root, L.Text("Chosen by you")));
        CheckoutList.SelectedItem = row;
        ExistingChoice.IsChecked = true;
    }

    // --- The client ---

    static bool HasClient(string? checkout) =>
        checkout is not null && ClientLocator.Classify(Path.Combine(checkout, "Client")) is ClientKind.Ready or ClientKind.Standalone;

    async void SearchClientsClicked(object sender, RoutedEventArgs e) => await FindClientsAsync();

    async Task FindClientsAsync()
    {
        _clientsSearched = true;
        string? checkout = ChosenCheckout();
        SearchAgain.IsEnabled = false;
        NoClient.IsChecked = false;
        _clients.Clear();
        ClientNote.Text = "";
        if (HasClient(checkout))
        {
            ClientStatus.Text = L.Text("This LocalExile folder already has a game client, so nothing needs copying.");
            SearchAgain.IsEnabled = true;
            UpdateButtons();
            return;
        }
        ClientStatus.Text = L.Text("Looking for a game client on this PC…");
        try
        {
            string[] others = _checkouts.Select(c => c.Root).Where(r => !r.Equals(checkout, StringComparison.OrdinalIgnoreCase)).ToArray();
            var result = await Task.Run(() => ClientLocator.Find(others, _closing.Token), _closing.Token);
            foreach (var client in result.Clients) _clients.Add(client);
            if (!string.IsNullOrEmpty(_settings.SetupClient) && ClientLocator.Describe(_settings.SetupClient) is { } earlier && !_clients.Any(c => c.Folder.Equals(earlier.Folder, StringComparison.OrdinalIgnoreCase)))
                _clients.Insert(0, earlier);
            ClientList.SelectedItem = _clients.FirstOrDefault(c => c.Folder.Equals(_settings.SetupClient, StringComparison.OrdinalIgnoreCase))
                ?? _clients.FirstOrDefault(c => c.Usable && c.KnownBuild == true) ?? _clients.FirstOrDefault(c => c.Usable);
            int usable = _clients.Count(c => c.Usable);
            ClientStatus.Text = usable > 0 ? L.Text("Found {0} on this PC. Choose the one to use.", usable)
                : L.Text("No usable client was found. Searched {0}.", string.Join(", ", result.Searched));
            string warning = ClientList.SelectedItem is FoundClient chosen ? ClientWarning(chosen) : "";
            ClientNote.Text = usable == 0 ? L.Text("Browse to a folder with PathOfExile.exe and Content.ggpk. It must be a standalone 3.29.3b install: Steam and Epic installs do not work, and Waypoint cannot download the game.")
                : warning.Length > 0 ? warning
                : result.Limited ? L.Text("The search stopped at its time limit; Browse if your client is not listed.") : "";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            ClientStatus.Text = L.Text("The search could not finish: ") + ex.Message;
        }
        SearchAgain.IsEnabled = true;
        UpdateButtons();
    }

    void BrowseClient(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = L.Text("Choose your Path of Exile folder (the one with Content.ggpk)") };
        if (dialog.ShowDialog(this) != true) return;
        var client = ClientLocator.Describe(dialog.FolderName);
        if (client is null)
        {
            ClientStatus.Text = L.Text("{0} has no Content.ggpk and PathOfExile.exe, so it is not a standalone client.", dialog.FolderName);
            return;
        }
        var existing = _clients.FirstOrDefault(c => c.Folder.Equals(client.Folder, StringComparison.OrdinalIgnoreCase));
        if (existing is null) _clients.Insert(0, existing = client);
        if (client.Usable) ClientList.SelectedItem = existing;
        else ClientNote.Text = L.Text("Steam and Epic installs have no Content.ggpk and do not work. Choose a standalone install.");
    }

    void ClientSelected(object sender, SelectionChangedEventArgs e)
    {
        var client = ClientList.SelectedItem as FoundClient;
        if (client is not null) ClientNote.Text = ClientWarning(client);
        UpdateButtons();
    }

    /// <summary>A wrong client only fails later, at the keys, after a long copy: this says so now. It stays the player's call,
    /// because a build Waypoint does not know may be one a newer server supports.</summary>
    string ClientWarning(FoundClient client)
    {
        if (client.KnownBuild == false) return L.Text("This is not the 3.29.3b build LocalExile needs, so preparing it will probably fail. You can still try, but check the version first: the copy is large.");
        var plan = ChosenCheckout() is { } checkout ? ClientCopy.Plan(client.Folder, checkout) : null;
        return plan is not null && ClientCopy.FreeSpace(plan.Target) is { } free && free < plan.Remaining
            ? L.Text("Not enough free space: the client needs {0:0.#} GB more on this drive, and {1:0.#} GB is free.", plan.Remaining / 1e9, free / 1e9) : "";
    }

    void NoClientChanged(object sender, RoutedEventArgs e) => UpdateButtons();

    // --- Setting up ---

    /// <summary>The steps this run will take, in order, for the Run page to show before Start.</summary>
    IReadOnlyList<SetupTask> Plan(string checkout, bool download, string? client, bool prepare)
    {
        LauncherSettings Options() => _settings.ForBridge with { RepositoryPath = checkout };
        async Task Bridge(string action, Action<string> detail, CancellationToken stop)
        {
            var result = await _bridge.RunAsync(Options(), action, log: Write, progress: update => detail(L.Text(update.Detail.Length > 0 ? update.Detail : update.Stage)), stop: stop);
            if (!result.Ok && stop.IsCancellationRequested) throw new OperationCanceledException(stop);
            if (!result.Ok) throw new InvalidOperationException(L.Describe(result.Message));
        }
        // First, so that the question is not waiting for the player at the end of a long copy.
        var tasks = new List<SetupTask> { new(L.Text("Check the server ports"), null, (_, _) => PortsFree()) };
        if (download)
            tasks.Add(new(L.Text("Download LocalExile"), () => RepositoryDiscovery.IsLocalExile(checkout), async (detail, stop) =>
            {
                await SetupSequence.CloneAsync(GitRepositoryProbe.UpstreamUrl, checkout, line => { Write(line); detail(line); }, stop);
                Remember(checkout);
            }));
        if (client is not null)
            tasks.Add(new(L.Text("Copy the game client"), () => HasClient(checkout), (detail, stop) => CopyAsync(client, checkout, detail, stop)));
        tasks.Add(new(L.Text("Install the extraction dependency"), () => File.Exists(Path.Combine(checkout, "Tools", "ooz", "node_modules", ".package-lock.json")), (detail, stop) => Bridge("Dependencies", detail, stop)));
        if (prepare)
            tasks.Add(new(L.Text("Prepare the client"), () => File.Exists(Path.Combine(checkout, "Client", "Game.patched.exe")) && File.Exists(Path.Combine(checkout, "data", "login-keys.json"))
                && File.Exists(Path.Combine(checkout, "data", "gateway_list.original.txt")), (detail, stop) => Bridge("PrepareClient", detail, stop)));
        tasks.Add(new(L.Text("Build LocalExile"), null, (detail, stop) => Bridge("Build", detail, stop)));
        if (prepare)
            tasks.Add(new(L.Text("Extract the game data"), null, (detail, stop) => SetupSequence.ExtractAsync(checkout, line => { Write(line); detail(line); }, stop)));
        return tasks;
    }

    /// <summary>The adapter refuses to prepare anything while another program holds a server's port, so the guide asks first.
    /// Kept running, that is a stop like the Stop button's: Continue asks again.</summary>
    async Task PortsFree()
    {
        if (await PortCheck.ClearAsync(this, PortCheck.ServerPorts, [], Write) == PortAnswer.Free) return;
        _portsHeld = true;
        _stop!.Cancel();
        _stop.Token.ThrowIfCancellationRequested();
    }

    async Task CopyAsync(string client, string checkout, Action<string> detail, CancellationToken stop)
    {
        var plan = ClientCopy.Plan(client, checkout);
        Write($"> copy {client} -> {plan.Target} ({plan.Remaining / 1e9:0.#} GB left of {plan.Total / 1e9:0.#} GB)");
        var clock = Stopwatch.StartNew();
        long startedAt = plan.Total - plan.Remaining;
        await ClientCopy.RunAsync(plan, copied => Dispatcher.BeginInvoke(() =>
        {
            double rate = (copied.Done - startedAt) / Math.Max(clock.Elapsed.TotalSeconds, .1);
            detail(L.Text("{0:0.0} of {1:0.0} GB · {2:0} MB/s · {3}", copied.Done / 1e9, copied.Total / 1e9, rate / 1e6, copied.File));
            Progress.IsIndeterminate = false;
            Progress.Value = copied.Total == 0 ? 0 : 100d * copied.Done / copied.Total;
        }), stop);
        Dispatcher.Invoke(() => Progress.IsIndeterminate = true);
    }

    void PrepareRun()
    {
        var tasks = Tasks();
        _steps.Clear();
        foreach (var task in tasks) _steps.Add(new(task.Name));
        RunTitle.Text = L.Text("Ready to set up");
        RunStatus.Text = L.Text("Start when you are ready. You can stop at any point, and the game can stay closed the whole time.");
        UpdateButtons();
    }

    IReadOnlyList<SetupTask> Tasks()
    {
        string checkout = ChosenCheckout() ?? Checkout ?? "";
        var client = NoClient.IsChecked == true ? null : ClientList.SelectedItem as FoundClient;
        bool hasClient = client is not null || HasClient(checkout);
        return Plan(checkout, DownloadChoice.IsChecked == true, HasClient(checkout) ? null : client?.Folder, hasClient);
    }

    void Remember(string checkout)
    {
        Checkout = checkout;
        _settings.ServerPath = checkout;
        _settings.Save();
    }

    async Task RunAsync()
    {
        string checkout = ChosenCheckout() ?? Checkout ?? "";
        var tasks = Tasks();
        // A step that was done when the page opened may have stopped since: the list is made again.
        _steps.Clear();
        foreach (var task in tasks) _steps.Add(new(task.Name));
        if (RepositoryDiscovery.IsLocalExile(checkout)) Remember(checkout);
        _settings.SetupClient = (ClientList.SelectedItem as FoundClient)?.Folder;
        _settings.Save();
        _stop = new();
        _outcome = null;
        _portsHeld = false;
        RunTitle.Text = L.Text("Setting up");
        RunStatus.Text = L.Text("You can stop at any point: what is finished stays finished.");
        Progress.Visibility = Visibility.Visible;
        Progress.IsIndeterminate = !Theme.Current.ReduceMotion;
        // Queued: a step reports from its tool's threads.
        var run = SetupSequence.RunAsync(tasks, (index, state, detail) => Dispatcher.BeginInvoke(() =>
        {
            _steps[index].Show(state, state == SetupState.Running && detail.Length > 110 ? detail[..110] : detail);
            if (state == SetupState.Running && detail.Length == 0) Write($"> {_steps[index].Name}");
        }), _stop.Token);
        _outcome = await run;
        Progress.Visibility = Visibility.Hidden;
        _running = null;
        Finish(checkout);
    }

    void Finish(string checkout)
    {
        var failed = _steps.FirstOrDefault(s => s.State == SetupState.Failed);
        switch (_outcome)
        {
            case SetupOutcome.Finished:
                Remember(checkout);
                _settings.SetupClient = null;
                _settings.Save();
                DoneText.Text = HasClient(checkout) ? L.Text("Everything is in place. Press Play in Waypoint to start the servers and the game. In the game, set Options → Game → Network Mode to Predictive the first time.")
                    : L.Text("LocalExile is built. Add your game client with this guide when you have it: Setup → Run setup guide.");
                CreateAccount.Visibility = HasClient(checkout) ? Visibility.Visible : Visibility.Collapsed;
                CreateAccount.IsChecked = HasClient(checkout);
                Show(Page.Done);
                break;
            case SetupOutcome.Stopped:
                RunTitle.Text = L.Text("Setup stopped");
                RunStatus.Text = _portsHeld ? L.Text("The port is still in use. When you have looked at it, press Continue: Waypoint will ask again.")
                    : L.Text("What is finished stays finished, and a partial copy is kept. Continue picks up from here, now or another day.");
                break;
            default:
                RunTitle.Text = L.Text("Setup did not finish");
                RunStatus.Text = L.Text("{0} failed. Fix what it says, then try again: finished steps are not repeated.", failed?.Name ?? "");
                break;
        }
        UpdateButtons();
        if (_closeWhenStopped) Close();
    }

    void Write(string line)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => Write(line)); return; }
        _log.AppendLine(line.TrimEnd());
        if (_log.Length > 100_000) _log.Remove(0, _log.Length - 80_000);
        Log.Text = _log.ToString();
        Log.ScrollToEnd();
    }
}
