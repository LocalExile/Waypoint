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
    enum Page { Welcome, Source, Install, Requirements, Checkout, Client, Account, Run, Done }

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
    bool _searchedCheckouts, _clientsSearched, _closeWhenStopped, _portsHeld, _accountAsked;
    /// <summary>The option LocalExile.exe stopped to ask for: the page that gives it shows next.</summary>
    string? _neededOption;
    readonly bool _newCopy;
    /// <summary>The data folder with the keys that fit the chosen client, when it can be shared.</summary>
    string? _sharedKeys;

    /// <summary>The checkout the guide ended with; null when it ended without one.</summary>
    public string? Checkout { get; private set; }
    /// <summary>The player skipped the guide.</summary>
    public bool Skipped { get; private set; }
    /// <summary>The player wants the Accounts section's Create dialog next.</summary>
    public bool CreateAccountNext { get; private set; }

    /// <param name="newCopy">Opens on the source route, with cloning chosen: a developer's new copy.</param>
    internal SetupWizard(Settings settings, Window? owner, string? current, bool newCopy = false)
    {
        _newCopy = newCopy;
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
        InstallFolder.Text = LocalExileReleases.IsInstall(current) ? current! : SuggestedInstallFolder();
        (newCopy || Repo.IsRoot(current) ? SourceChoice : ReleaseChoice).IsChecked = true;
        Closed += (_, _) =>
        {
            _closing.Cancel();
            ForgetPassword();
        };
        Show(newCopy ? Page.Requirements : Page.Welcome);
    }

    bool Running => _running is { IsCompleted: false };

    /// <summary>The ready-to-play release rather than a source checkout.</summary>
    bool UsingRelease => ReleaseChoice.IsChecked == true;

    /// <summary>A new install has no saves, so no account yet; LocalExile.exe asks when one with saves has none.</summary>
    bool AccountNeeded => UsingRelease && NoClient.IsChecked != true && (_accountAsked || ChosenCheckout() is not { } folder || !LocalExileReleases.HasSaves(folder));

    /// <summary>The pages of the chosen route, in order.</summary>
    Page[] Flow => UsingRelease
        ? AccountNeeded ? [Page.Welcome, Page.Source, Page.Install, Page.Client, Page.Account, Page.Run, Page.Done] : [Page.Welcome, Page.Source, Page.Install, Page.Client, Page.Run, Page.Done]
        : [Page.Welcome, Page.Source, Page.Requirements, Page.Checkout, Page.Client, Page.Run, Page.Done];

    static string PageName(Page page) => L.Text(page switch
    {
        Page.Welcome => "Welcome",
        Page.Source => "Choose",
        Page.Requirements => "Tools",
        Page.Install or Page.Checkout => "LocalExile",
        Page.Client => "Game client",
        Page.Account => "Account",
        _ => "Set up",
    });

    void Show(Page page)
    {
        _page = page;
        (FrameworkElement, Page)[] pages = [(WelcomePage, Page.Welcome), (SourcePage, Page.Source), (InstallPage, Page.Install), (RequirementsPage, Page.Requirements), (CheckoutPage, Page.Checkout),
            (ClientPage, Page.Client), (AccountPage, Page.Account), (RunPage, Page.Run), (DonePage, Page.Done)];
        foreach (var (element, shown) in pages) element.Visibility = shown == page ? Visibility.Visible : Visibility.Collapsed;
        if (page == Page.Account) _accountAsked = true;
        ShowRail();
        UpdateButtons();
        switch (page)
        {
            case Page.Install: ShowInstallNote(); break;
            case Page.Requirements: _ = CheckRequirementsAsync(); break;
            case Page.Checkout when !_searchedCheckouts: _ = FindCheckoutsAsync(); break;
            case Page.Client when !_clientsSearched: _ = FindClientsAsync(); break;
            case Page.Run when !Running && _outcome != SetupOutcome.Finished: PrepareRun(); break;
        }
    }

    /// <summary>The steps of the guide, with where the player is. The route's choice changes them.</summary>
    void ShowRail()
    {
        Rail.Children.Clear();
        Page[] flow = Flow;
        int at = Array.IndexOf(flow, _page);
        for (int i = 0; i < flow.Length - 1; i++)
        {
            var step = new TextBlock { Text = PageName(flow[i]), Margin = new Thickness(0, 0, 0, 10), FontWeight = at == i ? FontWeights.SemiBold : FontWeights.Normal };
            step.SetResourceReference(TextBlock.ForegroundProperty, at >= i ? "Shell.SidebarStrong" : "Shell.SidebarText");
            step.Opacity = at >= i ? 1 : .65;
            Rail.Children.Add(step);
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
            Page.Install or Page.Checkout => ChosenCheckout() is not null,
            Page.Account => AccountProblem() is null,
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
            case Page.Welcome or Page.Source or Page.Requirements or Page.Client or Page.Account: Show(Next()); break;
            case Page.Install or Page.Checkout:
                _clientsSearched = false;
                Show(Next());
                break;
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

    Page Next() => Flow[Array.IndexOf(Flow, _page) + 1];

    void BackClicked(object sender, RoutedEventArgs e) => Show(Flow[Math.Max(Array.IndexOf(Flow, _page) - 1, 0)]);

    void SourceChanged(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized) return;
        ShowRail();
        UpdateButtons();
    }

    // --- Where the release goes ---

    /// <summary>The usual folder, or one beside it when that holds something else (a source checkout, usually).</summary>
    static string SuggestedInstallFolder()
    {
        string folder = SuggestedFolder();
        return FolderProblem(folder) is null || LocalExileReleases.IsInstall(folder) ? folder : folder + " Release";
    }

    void InstallFolderChanged(object sender, TextChangedEventArgs e) => ShowInstallNote();

    void ShowInstallNote()
    {
        if (InstallNote is null) return;
        string folder = InstallFolder.Text.Trim();
        InstallNote.Text = LocalExileReleases.IsInstall(folder) ? L.Text("LocalExile {0} is already installed here.", LocalExileReleases.Installed(folder)?.ToString() ?? "")
            : FolderProblem(folder) ?? (ClientCopy.FreeSpace(folder) is { } free ? L.Text("{0:0.#} GB free on this drive; LocalExile with its client needs about {1:0} GB.", free / 1e9, NeededBytes / 1e9) : "");
        UpdateButtons();
    }

    void ChooseInstallFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = L.Text("Choose where to install LocalExile") };
        if (dialog.ShowDialog(this) != true) return;
        // An empty folder or an install is taken as it is; otherwise LocalExile gets a folder of its own inside.
        InstallFolder.Text = LocalExileReleases.IsInstall(dialog.FolderName) || !Directory.EnumerateFileSystemEntries(dialog.FolderName).Any() ? dialog.FolderName : Path.Combine(dialog.FolderName, "LocalExile");
    }

    // --- The account of a release install ---

    string? AccountProblem() => LocalExileReleases.AccountProblem(AccountName.Text.Trim(), AccountPassword.Password, AccountAgain.Password);

    void AccountChanged(object sender, RoutedEventArgs e)
    {
        bool typed = AccountName.Text.Length > 0 || AccountPassword.Password.Length > 0;
        AccountNote.Text = typed ? AccountProblem() ?? "" : "";
        UpdateButtons();
    }

    void ForgetPassword()
    {
        AccountPassword.Clear();
        AccountAgain.Clear();
    }

    /// <summary>Skip on any page. Later, after a stop, it reads Finish later: the checkout stays, and the guide can be run again
    /// from the Setup section.</summary>
    void SkipClicked(object sender, RoutedEventArgs e)
    {
        if (_outcome is null && !Dialog.Ask(this, DialogKind.Question, L.Text("Skip setup?"),
                L.Text("LocalExile won't be ready to play until setup is finished. You can run this guide again later."), L.Text("Skip setup"))) return;
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
            if (_checkouts.Count == 0 || _newCopy) DownloadChoice.IsChecked = true;
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
        if (UsingRelease) return InstallFolder.Text.Trim() is var install && (FolderProblem(install) is null || LocalExileReleases.IsInstall(install)) ? install : null;
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
            // A client that can be shared comes first: it saves the whole copy.
            FoundClient[] ready = _clients.Where(c => c.Kind == ClientKind.Ready).ToArray();
            var shareable = checkout is null ? null : await Task.Run(() => ready.FirstOrDefault(c => ClientCopy.SharedKeys(c.Folder, checkout, others) is not null), _closing.Token);
            ClientList.SelectedItem = _clients.FirstOrDefault(c => c.Folder.Equals(_settings.SetupClient, StringComparison.OrdinalIgnoreCase))
                ?? shareable ?? _clients.FirstOrDefault(c => c.Usable && c.KnownBuild == true) ?? _clients.FirstOrDefault(c => c.Usable);
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

    async void ClientSelected(object sender, SelectionChangedEventArgs e)
    {
        var client = ClientList.SelectedItem as FoundClient;
        _sharedKeys = null;
        ShareClient.Visibility = Visibility.Collapsed;
        LinkClient.Visibility = UsingRelease && client is not null ? Visibility.Visible : Visibility.Collapsed;
        if (client is not null) ClientNote.Text = ClientWarning(client);
        UpdateButtons();
        if (UsingRelease || client is not { Kind: ClientKind.Ready } || ChosenCheckout() is not { } checkout) return;
        string[] others = _checkouts.Select(c => c.Root).Where(r => !r.Equals(checkout, StringComparison.OrdinalIgnoreCase)).ToArray();
        string? keys = await Task.Run(() => ClientCopy.SharedKeys(client.Folder, checkout, others));
        if (keys is null || !Equals(ClientList.SelectedItem, client)) return;
        _sharedKeys = keys;
        ShareClient.Content = new TextBlock { Text = L.Text("Share this client instead of copying it (saves {0:0} GB). Both LocalExile folders will use the same game files.", client.Bytes / 1e9), TextWrapping = TextWrapping.Wrap };
        ShareClient.IsChecked = true;
        ShareClient.Visibility = Visibility.Visible;
        ClientNote.Text = ClientWarning(client);
    }

    void ShareChanged(object sender, RoutedEventArgs e)
    {
        if (ClientList.SelectedItem is FoundClient client) ClientNote.Text = ClientWarning(client);
    }

    /// <summary>The chosen client is shared through a link, with the keys of this folder, instead of copied.</summary>
    bool Sharing => _sharedKeys is not null && ShareClient.IsChecked == true;

    /// <summary>A wrong client only fails later, at the keys, after a long copy: this says so now. It stays the player's call,
    /// because a build Waypoint does not know may be one a newer server supports.</summary>
    string ClientWarning(FoundClient client)
    {
        if (client.KnownBuild == false) return L.Text("This is not the 3.29.3b build LocalExile needs, so preparing it will probably fail. You can still try, but check the version first: the copy is large.");
        if (Sharing || LinkClient.IsChecked == true) return "";
        var plan = ChosenCheckout() is { } checkout ? ClientCopy.Plan(client.Folder, checkout) : null;
        return plan is not null && ClientCopy.FreeSpace(plan.Target) is { } free && free < plan.Remaining
            ? L.Text("Not enough free space: the client needs {0:0.#} GB more on this drive, and {1:0.#} GB is free.", plan.Remaining / 1e9, free / 1e9) : "";
    }

    void NoClientChanged(object sender, RoutedEventArgs e)
    {
        ShowRail();
        UpdateButtons();
    }

    // --- Setting up ---

    /// <summary>The steps this run will take, in order, for the Run page to show before Start.</summary>
    IReadOnlyList<SetupTask> Plan(string checkout, bool download, string? client, string? sharedKeys, bool prepare)
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
        if (client is not null && sharedKeys is not null)
            tasks.Add(new(L.Text("Share the game client"), () => HasClient(checkout), (detail, stop) => ClientCopy.ShareAsync(client, checkout, sharedKeys, line => { Write(line); detail(line); }, stop)));
        else if (client is not null)
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

    /// <summary>A release's steps: the zip, then LocalExile.exe's own setup, which goes on from wherever it stopped.</summary>
    IReadOnlyList<SetupTask> ReleasePlan(string folder, string? client, bool prepare)
    {
        var tasks = new List<SetupTask> { new(L.Text("Download LocalExile"), () => LocalExileReleases.IsInstall(folder), (detail, stop) => DownloadReleaseAsync(folder, detail, stop)) };
        if (prepare) tasks.Add(new(L.Text("Prepare LocalExile"), null, (detail, stop) => PrepareReleaseAsync(folder, client, detail, stop)));
        return tasks;
    }

    async Task DownloadReleaseAsync(string folder, Action<string> detail, CancellationToken stop)
    {
        try
        {
            using var client = Updates.Client();
            var release = await LocalExileReleases.LatestAsync(client, stop) ?? throw new InvalidOperationException(L.Text("LocalExile has no release to download yet."));
            Write($"> {release.Tag}: {release.ZipUrl}");
            bool extracting = false;
            await LocalExileReleases.InstallAsync(client, release, folder, (done, total) => Dispatcher.BeginInvoke(() =>
            {
                detail(extracting ? L.Text("Installing: {0:0} of {1:0} MB", done / 1e6, total / 1e6) : L.Text("Downloading: {0:0} of {1:0} MB", done / 1e6, total / 1e6));
                ShowProgress(done, total);
            }), () => extracting = true, stop);
            Dispatcher.Invoke(() => Progress.IsIndeterminate = true);
            Remember(folder);
        }
        // The sequence reports what it knows how to; these are the same to the player.
        catch (Exception e) when (e is System.Net.Http.HttpRequestException or InvalidDataException or TaskCanceledException && !stop.IsCancellationRequested)
        {
            throw new IOException(L.Text("The download failed: {0}", e.Message), e);
        }
    }

    void ShowProgress(long done, long total)
    {
        Progress.IsIndeterminate = false;
        Progress.Value = total == 0 ? 0 : 100d * done / total;
    }

    /// <summary><c>LocalExile.exe setup --json</c>. A stop ends the run unless it is in a step that writes the client or the keys:
    /// that one finishes first. A question it stops with sends the player to the page that answers it (<see cref="Finish"/>).</summary>
    async Task PrepareReleaseAsync(string folder, string? client, Action<string> detail, CancellationToken stop)
    {
        List<string> arguments = ["setup"];
        if (client is not null && !HasClient(folder)) arguments.AddRange(["--client", client, "--client-mode", LinkClient.IsChecked == true ? "link" : "copy"]);
        string? password = null;
        if (AccountProblem() is null)
        {
            arguments.AddRange(["--account", AccountName.Text.Trim(), "--password-stdin"]);
            password = AccountPassword.Password;
        }
        using var end = new CancellationTokenSource();
        string? stage = null;
        bool ending = false;
        using var stopping = stop.Register(() =>
        {
            if (LocalExileReleases.MustFinish(Volatile.Read(ref stage))) Volatile.Write(ref ending, true);
            else end.Cancel();
        });
        var result = await LocalExileTool.RunAsync(folder, arguments, item =>
        {
            if (item.Name is "step" or "progress")
            {
                Volatile.Write(ref stage, item.Id);
                if (Volatile.Read(ref ending) && !LocalExileReleases.MustFinish(item.Id)) end.Cancel();
                string name = LocalExileReleases.Stage(item.Id, item.Message ?? "");
                detail(item.Name == "progress" && item.Message is { } message ? $"{name}: {message}" : name);
                if (item.Current is { } done && item.Total is { } total) Dispatcher.BeginInvoke(() => ShowProgress(done, total));
                else if (item.Name == "step") Dispatcher.BeginInvoke(() => Progress.IsIndeterminate = !Theme.Current.ReduceMotion);
            }
            if (item.Message is { } text) Write(text);
        }, Write, password, end.Token);
        if (result.NeededOption is { } option)
        {
            _neededOption = option;
            throw new InvalidOperationException(L.Text("LocalExile needs a little more from you."));
        }
        if (result.Code != 0) throw new InvalidOperationException(result.Error ?? L.Text("LocalExile.exe stopped with exit code {0}.", result.Code));
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
        if (UsingRelease) return ReleasePlan(checkout, HasClient(checkout) ? null : client?.Folder, hasClient);
        return Plan(checkout, DownloadChoice.IsChecked == true, HasClient(checkout) ? null : client?.Folder, Sharing ? _sharedKeys : null, hasClient);
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
        if (RepositoryDiscovery.IsLocalExile(checkout) || LocalExileReleases.IsInstall(checkout)) Remember(checkout);
        _settings.SetupClient = (ClientList.SelectedItem as FoundClient)?.Folder;
        _settings.Save();
        _stop = new();
        _outcome = null;
        _portsHeld = false;
        _neededOption = null;
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
        if (_neededOption is { } option)
        {
            _neededOption = null;
            _outcome = null;
            UpdateButtons();
            if (!_closeWhenStopped) Show(option is "--account" or "--password-stdin" ? Page.Account : Page.Client);
            else Close();
            return;
        }
        switch (_outcome)
        {
            case SetupOutcome.Finished:
                Remember(checkout);
                _settings.SetupClient = null;
                _settings.Save();
                DoneText.Text = HasClient(checkout) ? L.Text("Everything is in place. Press Play in Waypoint to start the servers and the game. In the game, set Options → Game → Network Mode to Predictive the first time.")
                    : L.Text("LocalExile is built. Add your game client with this guide when you have it: Setup → Run setup guide.");
                bool accounts = HasClient(checkout) && !UsingRelease;
                if (UsingRelease) DoneText.Text = HasClient(checkout) ? L.Text("Everything is in place. Press Play in Waypoint to start the game. Log in with your account: its name goes in the Email field. In the game, set Options → Game → Network Mode to Predictive the first time.")
                    : L.Text("LocalExile is installed. Run this guide again when you have your game client: Play → Finish setup.");
                CreateAccount.Visibility = accounts ? Visibility.Visible : Visibility.Collapsed;
                CreateAccount.IsChecked = accounts;
                ForgetPassword();
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
