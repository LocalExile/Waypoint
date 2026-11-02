using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Material.Icons;

namespace Waypoint;

sealed record SetupRow(SetupStep Step)
{
    public MaterialIconKind Icon => Step.Ready ? MaterialIconKind.CheckCircleOutline : MaterialIconKind.AlertCircleOutline;
    public IBrush Brush => App.Brush(Step.Ready ? "Shell.Good" : "Shell.Warning");
    /// <summary>A requirement that is met has nothing to do.</summary>
    public bool ActionShown => !Step.Ready;
}

/// <summary>The Setup section: what the checkout still needs, each with its next step. The requirements come from the
/// adapter's inspection; preparing the client and installing the extraction dependency are its operations, and they want the
/// game and the servers stopped.</summary>
public partial class SetupTab : UserControl
{
    Shared _shared = null!;

    /// <summary>The window opens the Accounts section on its Create dialog.</summary>
    internal event Action? CreateAccountRequested;
    /// <summary>The guide changed the checkout: the Control section's checks are read again.</summary>
    internal event Action? SetupFinished;

    public SetupTab() => InitializeComponent();

    internal void Attach(Shared shared)
    {
        _shared = shared;
        OutputView.Attach(shared);
        ServerFolder.Text = Environment.CurrentDirectory;
        ToolTip.SetTip(ServerFolder, Environment.CurrentDirectory);
        shared.Workspace.Changed += () => Dispatcher.UIThread.Post(Show);
        shared.LiveRunning += running => Dispatcher.UIThread.Post(() => Toolbar.IsEnabled = PrepareButton.IsEnabled = DependenciesButton.IsEnabled = Steps.IsEnabled = !running);
        L.Current.PropertyChanged += (_, _) => Show();
        Ui.VisibilityChanged(this, async shown => { if (shown) await shared.Workspace.RefreshAsync(); });
        Show();
    }

    void ShowText()
    {
        GuideButton.Content = L.Text("Run setup guide…");
        ToolTip.SetTip(GuideButton, L.Text("Goes through the setup step by step, and continues a setup that was stopped"));
        FindButton.Content = L.Text("Find automatically…");
        ToolTip.SetTip(FindButton, L.Text("Looks for LocalExile checkouts in the usual folders"));
        ChangeButton.Content = L.Text("Change…");
        ToolTip.SetTip(ChangeButton, L.Text("Pick another LocalExile server checkout; Waypoint starts again on it"));
        CheckButton.Content = L.Text("Check again");
        PrepareButton.Content = L.Text("Prepare client");
        ToolTip.SetTip(PrepareButton, L.Text("Creates the missing login keys and Game.patched.exe in your client copy; existing ones are kept"));
        DependenciesButton.Content = L.Text("Install dependency");
        ToolTip.SetTip(DependenciesButton, L.Text("npm ci in the checkout's Tools/ooz"));
        CreateAccountButton.Content = L.Text("Create account…");
        OpenClientButton.Content = L.Text("Open Client folder");
        OpenGuideButton.Content = L.Text("Setup guide");
        RequirementHeader.Text = L.Text("REQUIREMENT");
        DetailHeader.Text = L.Text("DETAIL");
    }

    void Show()
    {
        ShowText();
        var state = _shared.Workspace.Latest;
        var rows = state?.Requirements.Select(r => new SetupRow(SetupGuidance.Describe(r, state.Root))).ToList() ?? [];
        Steps.ItemsSource = rows;
        int missing = rows.Count(r => !r.Step.Ready);
        bool Needs(params string[] names) => state is null || rows.Any(r => !r.Step.Ready && names.Contains(r.Step.Name));
        PrepareButton.IsVisible = Needs("Prepared client", "Login keys");
        DependenciesButton.IsVisible = Needs("Bundle decompressor");
        Summary.Text = state is null ? "" : missing == 0 ? L.Text("Setup checks · all passed") : L.Text("Setup checks · {0} need attention", missing);
        Empty.Text = _shared.Workspace.InspectError is { } error ? L.Text("Check failed: ") + error : L.Text("Checking the selected installation…");
        Empty.IsVisible = rows.Count == 0;
    }

    async void CheckAgain(object? sender, RoutedEventArgs e) => await _shared.Workspace.RefreshAsync();

    /// <summary>Simple mode's way to the guide.</summary>
    internal void OpenSetupGuide() => RunGuide(this, new RoutedEventArgs());

    /// <summary>The guide on the source route: clones LocalExile into a chosen folder and sets it up.</summary>
    internal void SetUpNewCopy() => Guide(newCopy: true);

    /// <summary>Another checkout or release install; Waypoint starts again on it.</summary>
    internal void ChangeFolder() => ChangeCheckout(this, new RoutedEventArgs());

    /// <summary>The guide holds the checkout while it is open, as a live.ps1 run does. Choosing another checkout in it starts
    /// Waypoint again on that one.</summary>
    void RunGuide(object? sender, RoutedEventArgs e) => Guide(newCopy: false);

    async void Guide(bool newCopy)
    {
        if (Busy()) return;
        var owner = Ui.Owner(this);
        string current = Environment.CurrentDirectory;
        var guide = new SetupWizard(_shared.Settings, owner, current, newCopy);
        await _shared.RunExclusiveAsync(L.Text("Setup guide"), () => Ui.ShowAsync(guide, owner));
        if (guide.Checkout is { } checkout && !checkout.Equals(current, StringComparison.Ordinal))
        {
            // The guide has saved its checkout already; SwitchServer acts on a change.
            _shared.Settings.ServerPath = current;
            _shared.Settings.CreateAccountOnStart = guide.CreateAccountNext;
            App.SwitchServer(owner!, _shared.Settings, checkout);
            return;
        }
        SetupFinished?.Invoke();
        await _shared.Workspace.RefreshAsync();
        if (guide.CreateAccountNext) CreateAccountRequested?.Invoke();
    }

    async void FindCheckout(object? sender, RoutedEventArgs e)
    {
        if (Busy()) return;
        var owner = Ui.Owner(this);
        var dialog = new CheckoutDialog(owner, _shared.Settings.ServerPath);
        await Ui.ShowAsync(dialog, owner);
        if (dialog.Chosen is not null) App.SwitchServer(owner!, _shared.Settings, dialog.Chosen);
    }

    void ChangeCheckout(object? sender, RoutedEventArgs e)
    {
        if (!Busy()) App.SwitchServer(Ui.Owner(this)!, _shared.Settings);
    }

    bool Busy()
    {
        if (!_shared.IsLiveRunning) return false;
        Dialog.Tell(Ui.Owner(this), DialogKind.Info, L.Text("{0} is still running", _shared.BusyWith), L.Text("Wait for the current operation before changing checkout."));
        return true;
    }

    async void RunStep(object? sender, RoutedEventArgs e)
    {
        if (((Control)sender!).DataContext is SetupRow row) await RunStepAsync(row.Step);
    }

    async void Prepare(object? sender, RoutedEventArgs e) => await RunStepAsync(SetupGuidance.Describe(new("Prepared client", false, ""), Environment.CurrentDirectory));

    async void Dependencies(object? sender, RoutedEventArgs e) => await RunStepAsync(SetupGuidance.Describe(new("Bundle decompressor", false, ""), Environment.CurrentDirectory));

    /// <summary>A step's instructions first; what it then does depends on the step.</summary>
    async Task RunStepAsync(SetupStep step)
    {
        string action = step.Action is "Guide" or "ClientFolder" ? L.Text("Open setup guide") : step.ActionLabel;
        int chosen = Dialog.Show(Ui.Owner(this), DialogKind.Info, step.DisplayName, step.Instructions,
            L.Text("Selected checkout: ") + Environment.CurrentDirectory, [action, L.Text("Close")]);
        if (chosen != 0) return;
        switch (step.Action)
        {
            case "PrepareClient": await _shared.Workspace.OperateAsync("PrepareClient", L.Text("Prepare client")); break;
            case "Dependencies": await _shared.Workspace.OperateAsync("Dependencies", L.Text("Install dependency")); break;
            case "Dotnet": Ui.Open("https://dotnet.microsoft.com/en-us/download/dotnet/10.0"); break;
            case "Python": Ui.Open("https://www.python.org/downloads/"); break;
            case "Node": Ui.Open("https://nodejs.org/en/download"); break;
            default: OpenGuide(this, new RoutedEventArgs()); break;
        }
    }

    void CreateAccount(object? sender, RoutedEventArgs e) => CreateAccountRequested?.Invoke();

    void OpenClient(object? sender, RoutedEventArgs e) =>
        Ui.Open(Path.GetFullPath(Directory.Exists("Client") ? "Client" : "."));

    void OpenGuide(object? sender, RoutedEventArgs e)
    {
        string guide = Path.GetFullPath(Path.Combine("Agents", "GettingStarted.md"));
        if (File.Exists(guide)) Ui.Open(guide);
    }
}
