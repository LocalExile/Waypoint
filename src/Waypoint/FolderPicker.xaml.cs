using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MaterialDesignThemes.Wpf;

namespace Waypoint;

/// <summary>A row of the sidebar's folder list.</summary>
sealed class FolderRow(KnownFolder folder)
{
    public KnownFolder Folder { get; } = folder;
    public string Title => Folder.Title;
    /// <summary>Which version of the game the folder's client is, beside its title; empty when that is not known.</summary>
    public string Client => Folder.ClientText ?? "";
    public string Path => Folder.Path;
    /// <summary>The row has room for about this much of a path; the tooltip has all of it.</summary>
    public string ShortPath => FolderList.Short(Folder.Path, 52);
    public bool InUse => Folder.Current;
    public PackIconKind Icon => FolderPicker.IconOf(Folder.Kind);
    public Visibility InUseShown => Folder.Current ? Visibility.Visible : Visibility.Collapsed;
    public Visibility MissingShown => !Folder.Current && Folder.Kind == FolderKind.Missing ? Visibility.Visible : Visibility.Collapsed;
    /// <summary>The folder in use stays: Waypoint is working in it.</summary>
    public Visibility ForgetShown => Folder.Current ? Visibility.Collapsed : Visibility.Visible;
    /// <summary>A folder that is not there is still a row, greyed: its user may want to find it, or take it off.</summary>
    public double Strength => Folder.Kind == FolderKind.Missing ? 0.6 : 1;
    public string Tip => Folder.Path;
    public string Spoken => Folder.Current ? L.Text("{0}, {1}, in use", Folder.Title, Folder.Path)
        : Folder.Kind == FolderKind.Missing ? L.Text("{0}, {1}, not found", Folder.Title, Folder.Path)
        : Folder.ClientText is { } client ? $"{Folder.Title}, {client}, {Folder.Path}" : $"{Folder.Title}, {Folder.Path}";
    /// <summary>What a screen reader is given for the row as an item of the list.</summary>
    public override string ToString() => Spoken;
}

/// <summary>
/// The folder Waypoint works on, at the top of the sidebar, and the list of the others it knows (<see cref="FolderList"/>):
/// a click on one is a change of folder (<see cref="App.SwitchServerAsync"/>), which moves no file and asks before it stops
/// anything. Under the list, a folder already on this PC can be added, and a new copy set up.
/// </summary>
public partial class FolderPicker : UserControl
{
    Shared _shared = null!;

    /// <summary>"Set up a new copy…": the setup guide's source route.</summary>
    internal event Action? NewCopyRequested;

    public FolderPicker() => InitializeComponent();

    internal static PackIconKind IconOf(FolderKind kind) => kind switch
    {
        FolderKind.Release => PackIconKind.PackageVariantClosed,
        FolderKind.Source => PackIconKind.SourceBranch,
        _ => PackIconKind.FolderAlertOutline,
    };

    internal void Attach(Shared shared)
    {
        _shared = shared;
        L.Current.PropertyChanged += (_, _) => ShowCurrent();
        ShowCurrent();
    }

    /// <summary>The folder in use, read again: its branch or its release's version may have changed.</summary>
    internal void ShowCurrent()
    {
        if (_shared is null) return;
        var folder = FolderList.Read(Environment.CurrentDirectory, current: true);
        CurrentIcon.Kind = IconOf(folder.Kind);
        CurrentTitle.Text = folder.Title;
        CurrentPath.Text = FolderList.Short(folder.Path);
        ShowCurrentClient(folder, _client.Path == folder.Path ? _client.Version : null);
        _ = ReadCurrentClientAsync(folder);
    }

    /// <summary>The version of the client in the folder in use, as last read: shown at once while it is read again.</summary>
    (string Path, string? Version) _client = ("", null);

    /// <summary>The client's version under the folder's name, once it is read (off the UI thread: the first reading hashes
    /// the executable).</summary>
    async Task ReadCurrentClientAsync(KnownFolder folder)
    {
        string? version = await Task.Run(() => FolderList.ClientOf(folder.Path));
        if (!FolderList.Same(folder.Path, Environment.CurrentDirectory)) return;
        _client = (folder.Path, version);
        ShowCurrentClient(folder, version);
    }

    void ShowCurrentClient(KnownFolder folder, string? version)
    {
        string? client = FolderList.ClientText(version);
        CurrentClient.Text = client ?? "";
        CurrentClient.Visibility = client is null ? Visibility.Collapsed : Visibility.Visible;
        string named = client is null ? folder.Title : $"{folder.Title}, {client}";
        // The box may be too narrow for a long branch name or path: all of it is here.
        Current.ToolTip = $"{folder.Title}{(client is null ? "" : "\n" + client)}\n{folder.Path}\n{L.Text("Click for your other LocalExile folders")}";
        AutomationProperties.SetName(Current, L.Text("LocalExile folder: {0}, {1}", named, folder.Path));
    }

    async void OpenList(object sender, RoutedEventArgs e)
    {
        await FillAsync();
        // Unchecked meanwhile: the list was asked for and given up again.
        if (Current.IsChecked != true) return;
        // Developer Mode on a release, or a source copy, whose Control has the same button.
        NewCopy.Visibility = !Repo.IsInstall || Editions.Offered(_shared.Settings) ? Visibility.Visible : Visibility.Collapsed;
        NewCopy.ToolTip = Repo.IsInstall
            ? L.Text("Sets up a second LocalExile from its source code, which you can change and build yourself. You can bring your characters.")
            : L.Text("Clones LocalExile with git into a folder you choose and sets it up, for branches and pull requests");
        // The window's zoom does not reach a popup, which is a window of its own.
        Card.LayoutTransform = Theme.Zoom == 1 ? Transform.Identity : new ScaleTransform(Theme.Zoom, Theme.Zoom);
        List.IsOpen = true;
    }

    /// <summary>The rows, read from the folders off the UI thread: one on a drive that has to wake up must not hold the
    /// window.</summary>
    async Task FillAsync()
    {
        string current = Environment.CurrentDirectory;
        var paths = FolderList.Paths(_shared.Settings, current);
        var folders = await Task.Run(() => FolderList.Read(paths, current, client: true));
        Rows.ItemsSource = folders.Select(folder => new FolderRow(folder)).ToArray();
    }

    void ListOpened(object? sender, EventArgs e)
    {
        // A click on the box while the list is open closes the list, as a click anywhere outside it does; without this
        // the same click would open it again.
        Current.IsHitTestVisible = false;
        Dispatcher.BeginInvoke(() => Card.MoveFocus(new TraversalRequest(FocusNavigationDirection.First)), DispatcherPriority.Input);
    }

    void ListClosed(object? sender, EventArgs e)
    {
        Current.IsChecked = false;
        Dispatcher.BeginInvoke(() => Current.IsHitTestVisible = true, DispatcherPriority.Input);
    }

    void ListKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        List.IsOpen = false;
        Current.Focus();
        e.Handled = true;
    }

    Window Owner => Window.GetWindow(this);

    /// <summary>The change of folder, with what the way from a release to a source copy asks (<see cref="Editions.BringAlongAsync"/>).</summary>
    Task<bool> GoAsync(string path)
    {
        var owner = Owner;
        return App.SwitchServerAsync(owner, _shared, path,
            before: () => Editions.BringAlongAsync(_shared, path, (kind, title, message, buttons) => Dialog.Show(owner, kind, title, message, null, buttons)));
    }

    async void RowClicked(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not FolderRow row) return;
        List.IsOpen = false;
        if (row.Folder.Current) return;
        // As it is now: the list may have been open for a while.
        if (FolderList.Read(row.Path, current: false).Kind == FolderKind.Missing) await FindAsync(row.Path);
        else await GoAsync(row.Path);
    }

    /// <summary>A folder of the list that is not there: found again, taken off the list, or left as it is.</summary>
    async Task FindAsync(string path)
    {
        var owner = Owner;
        int answer = Dialog.Show(owner, DialogKind.Warning, L.Text("This folder is not there"),
            L.Text("{0} can't be found: it may have been moved or renamed, or its drive is not connected. Find it to put it back in the list, or take it off the list. Nothing in the folder changes either way.", path),
            null, [L.Text("Find it…"), L.Text("Take it off the list"), L.Text("Cancel")]);
        if (answer == 1) Forget(path);
        if (answer != 0 || App.ChooseServer(owner) is not { } found) return;
        var settings = _shared.Settings;
        FolderList.Forget(settings, path);
        FolderList.Add(settings, found);
        settings.Save();
        _shared.Logs.Write($"folder list: {path} found again as {found}", Severity.Info);
        await GoAsync(found);
    }

    void Forget(string path)
    {
        if (!FolderList.Forget(_shared.Settings, path)) return;
        _shared.Settings.Save();
        _shared.Logs.Write($"folder list: {path} taken off the list", Severity.Info);
    }

    async void ForgetClicked(object sender, RoutedEventArgs e)
    {
        // The row under this button is a button too: the click is this one's alone.
        e.Handled = true;
        if (((FrameworkElement)sender).DataContext is not FolderRow row || row.Folder.Current) return;
        Forget(row.Path);
        await FillAsync();
    }

    async void AddClicked(object sender, RoutedEventArgs e)
    {
        List.IsOpen = false;
        var owner = Owner;
        if (_shared.IsLiveRunning)
        {
            Dialog.Tell(owner, DialogKind.Info, L.Text("{0} is still running", _shared.BusyWith), L.Text("Wait for the current operation before changing checkout."));
            return;
        }
        if (App.ChooseServer(owner) is not { } root) return;
        // In the list from now on, whether or not the change of folder goes on.
        if (FolderList.Add(_shared.Settings, root)) _shared.Settings.Save();
        await GoAsync(root);
    }

    void NewCopyClicked(object sender, RoutedEventArgs e)
    {
        List.IsOpen = false;
        NewCopyRequested?.Invoke();
    }
}
