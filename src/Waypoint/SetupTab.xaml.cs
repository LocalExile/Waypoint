using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MaterialDesignThemes.Wpf;

namespace Waypoint;

sealed record SetupRow(SetupStep Step)
{
    public PackIconKind Icon => Step.Ready ? PackIconKind.CheckCircleOutline : PackIconKind.AlertCircleOutline;
    public Brush Brush => Theme.Brush(Step.Ready ? "Shell.Good" : "Shell.Warning");
    /// <summary>A requirement that is met has nothing to do.</summary>
    public Visibility ActionShown => Step.Ready ? Visibility.Collapsed : Visibility.Visible;
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
        ServerFolder.ToolTip = Environment.CurrentDirectory;
        shared.Workspace.Changed += () => Dispatcher.BeginInvoke(Show);
        shared.LiveRunning += running => Dispatcher.BeginInvoke(() => Toolbar.IsEnabled = PrepareButton.IsEnabled = DependenciesButton.IsEnabled = Steps.IsEnabled = !running);
        Theme.Changed += Show;
        L.Current.PropertyChanged += (_, _) => Show();
        IsVisibleChanged += async (_, e) => { if ((bool)e.NewValue) await shared.Workspace.RefreshAsync(); };
        Show();
    }

    void Show()
    {
        var state = _shared.Workspace.Latest;
        var rows = state?.Requirements.Select(r => new SetupRow(SetupGuidance.Describe(r, state.Root))).ToList() ?? [];
        Steps.ItemsSource = rows;
        int missing = rows.Count(r => !r.Step.Ready);
        bool Needs(params string[] names) => state is null || rows.Any(r => !r.Step.Ready && names.Contains(r.Step.Name));
        PrepareButton.Visibility = Needs("Prepared client", "Login keys") ? Visibility.Visible : Visibility.Collapsed;
        DependenciesButton.Visibility = Needs("Bundle decompressor") ? Visibility.Visible : Visibility.Collapsed;
        Summary.Text = state is null ? "" : missing == 0 ? L.Text("Setup checks · all passed") : L.Text("Setup checks · {0} need attention", missing);
        Empty.Text = _shared.Workspace.InspectError is { } error ? L.Text("Check failed: ") + error : L.Text("Checking the selected installation…");
        Empty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    async void CheckAgain(object sender, RoutedEventArgs e) => await _shared.Workspace.RefreshAsync();

    /// <summary>Simple mode's way to the guide.</summary>
    internal void OpenSetupGuide() => RunGuide(this, new RoutedEventArgs());

    /// <summary>The guide holds the checkout while it is open, as a live.ps1 run does. Choosing another checkout in it starts
    /// Waypoint again on that one.</summary>
    async void RunGuide(object sender, RoutedEventArgs e)
    {
        if (Busy()) return;
        var owner = Window.GetWindow(this);
        string current = Environment.CurrentDirectory;
        var guide = new SetupWizard(_shared.Settings, owner, current);
        await _shared.RunExclusiveAsync(L.Text("Setup guide"), () => { guide.ShowDialog(); return Task.CompletedTask; });
        if (guide.Checkout is { } checkout && !checkout.Equals(current, StringComparison.OrdinalIgnoreCase))
        {
            // The guide has saved its checkout already; SwitchServer acts on a change.
            _shared.Settings.ServerPath = current;
            App.SwitchServer(owner, _shared.Settings, checkout);
            return;
        }
        SetupFinished?.Invoke();
        await _shared.Workspace.RefreshAsync();
        if (guide.CreateAccountNext) CreateAccountRequested?.Invoke();
    }

    void FindCheckout(object sender, RoutedEventArgs e)
    {
        if (Busy()) return;
        var owner = Window.GetWindow(this);
        var dialog = new CheckoutDialog(owner, _shared.Settings.ServerPath);
        if (dialog.ShowDialog() == true) App.SwitchServer(owner, _shared.Settings, dialog.Chosen);
    }

    void ChangeCheckout(object sender, RoutedEventArgs e)
    {
        if (!Busy()) App.SwitchServer(Window.GetWindow(this), _shared.Settings);
    }

    bool Busy()
    {
        if (!_shared.IsLiveRunning) return false;
        Dialog.Tell(Window.GetWindow(this), DialogKind.Info, L.Text("{0} is still running", _shared.BusyWith), L.Text("Wait for the current operation before changing checkout."));
        return true;
    }

    async void RunStep(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is SetupRow row) await RunStepAsync(row.Step);
    }

    async void Prepare(object sender, RoutedEventArgs e) => await RunStepAsync(SetupGuidance.Describe(new("Prepared client", false, ""), Environment.CurrentDirectory));

    async void Dependencies(object sender, RoutedEventArgs e) => await RunStepAsync(SetupGuidance.Describe(new("Bundle decompressor", false, ""), Environment.CurrentDirectory));

    /// <summary>A step's instructions first; what it then does depends on the step.</summary>
    async Task RunStepAsync(SetupStep step)
    {
        string action = step.Action is "Guide" or "ClientFolder" ? L.Text("Open setup guide") : step.ActionLabel;
        int chosen = Dialog.Show(Window.GetWindow(this), DialogKind.Info, step.DisplayName, step.Instructions,
            L.Text("Selected checkout: ") + Environment.CurrentDirectory, [action, L.Text("Close")]);
        if (chosen != 0) return;
        switch (step.Action)
        {
            case "PrepareClient": await _shared.Workspace.OperateAsync("PrepareClient", L.Text("Prepare client")); break;
            case "Dependencies": await _shared.Workspace.OperateAsync("Dependencies", L.Text("Install dependency")); break;
            case "Dotnet": Open("https://dotnet.microsoft.com/en-us/download/dotnet/10.0"); break;
            case "Python": Open("https://www.python.org/downloads/windows/"); break;
            case "Node": Open("https://nodejs.org/en/download"); break;
            default: OpenGuide(this, new RoutedEventArgs()); break;
        }
    }

    void CreateAccount(object sender, RoutedEventArgs e) => CreateAccountRequested?.Invoke();

    void OpenClient(object sender, RoutedEventArgs e) =>
        Open(Path.GetFullPath(Directory.Exists("Client") ? "Client" : "."));

    void OpenGuide(object sender, RoutedEventArgs e)
    {
        string guide = Path.GetFullPath(Path.Combine("Agents", "GettingStarted.md"));
        if (File.Exists(guide)) Open(guide);
    }

    static void Open(string target) => Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
}
