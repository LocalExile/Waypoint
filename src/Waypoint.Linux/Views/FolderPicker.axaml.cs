using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Material.Icons;

namespace Waypoint;

/// <summary>A row of the sidebar's folder list.</summary>
sealed class FolderRow(KnownFolder folder)
{
    public KnownFolder Folder { get; } = folder;
    public string Title => Folder.Title;
    public string Path => Folder.Path;
    /// <summary>The row has room for about this much of a path; the tooltip has all of it.</summary>
    public string ShortPath => FolderList.Short(Folder.Path, 52);
    public bool InUse => Folder.Current;
    public MaterialIconKind Icon => FolderPicker.IconOf(Folder.Kind);
    public bool MissingShown => !Folder.Current && Folder.Kind == FolderKind.Missing;
    /// <summary>The folder in use stays: Waypoint is working in it.</summary>
    public bool ForgetShown => !Folder.Current;
    /// <summary>A folder that is not there is still a row, greyed: its user may want to find it, or take it off.</summary>
    public double Strength => Folder.Kind == FolderKind.Missing ? 0.6 : 1;
    public string Tip => Folder.Path;
    public string Spoken => Folder.Current ? L.Text("{0}, {1}, in use", Folder.Title, Folder.Path)
        : Folder.Kind == FolderKind.Missing ? L.Text("{0}, {1}, not found", Folder.Title, Folder.Path)
        : $"{Folder.Title}, {Folder.Path}";
    /// <summary>What a screen reader is given for the row as an item of the list.</summary>
    public override string ToString() => Spoken;
}

/// <summary>
/// The folder Waypoint works on, at the top of the sidebar, and the list of the others it knows (<see cref="FolderList"/>),
/// as on Windows: a click on one is a change of folder (<see cref="App.SwitchServerAsync"/>), which moves no file and asks
/// before it stops anything. Under the list, a folder already on this PC can be added, and a new copy set up.
/// </summary>
public partial class FolderPicker : UserControl
{
    Shared _shared = null!;

    /// <summary>"Set up a new copy…": the setup guide's source route.</summary>
    internal event Action? NewCopyRequested;

    public FolderPicker() => InitializeComponent();

    internal static MaterialIconKind IconOf(FolderKind kind) => kind switch
    {
        FolderKind.Release => MaterialIconKind.PackageVariantClosed,
        FolderKind.Source => MaterialIconKind.SourceBranch,
        _ => MaterialIconKind.FolderAlertOutline,
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
        // The box may be too narrow for a long branch name or path: all of both is here.
        ToolTip.SetTip(Current, $"{folder.Title}\n{folder.Path}\n{L.Text("Click for your other LocalExile folders")}");
        AutomationProperties.SetName(Current, L.Text("LocalExile folder: {0}, {1}", folder.Title, folder.Path));
    }

    async void OpenList(object? sender, RoutedEventArgs e)
    {
        // A click outside the list closes it and goes no further, so this is always a click to open.
        if (List.IsOpen) return;
        await FillAsync();
        // Developer Mode on a release, or a source copy, whose Control has the same button.
        NewCopy.IsVisible = !Repo.IsInstall || Editions.Offered(_shared.Settings);
        ToolTip.SetTip(NewCopy, Repo.IsInstall
            ? L.Text("Sets up a second LocalExile from its source code, which you can change and build yourself. You can bring your characters.")
            : L.Text("Clones LocalExile with git into a folder you choose and sets it up, for branches and pull requests"));
        Current.Classes.Add("open");
        List.IsOpen = true;
        // The keyboard goes into the list: its first row, once the rows are there.
        Dispatcher.UIThread.Post(() => (Rows.GetVisualDescendants().OfType<Button>().FirstOrDefault() ?? AddFolder).Focus(), DispatcherPriority.Input);
    }

    /// <summary>The rows, read from the folders off the UI thread: one on a drive that has to wake up must not hold the
    /// window.</summary>
    async Task FillAsync()
    {
        string current = Environment.CurrentDirectory;
        var paths = FolderList.Paths(_shared.Settings, current);
        var folders = await Task.Run(() => FolderList.Read(paths, current));
        Rows.ItemsSource = folders.Select(folder => new FolderRow(folder)).ToArray();
    }

    void ListClosed(object? sender, EventArgs e) => Current.Classes.Remove("open");

    /// <summary>Escape closes the list; the arrows go from row to row and round again, as Tab does.</summary>
    void ListKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            List.IsOpen = false;
            Current.Focus();
            e.Handled = true;
        }
        else if (e.Key is Key.Down or Key.Up)
        {
            var rows = Card.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("folderRow") && b.IsEffectivelyVisible).ToList();
            if (rows.Count == 0) return;
            // The keyboard may be on a row's own small button: its row is where it is.
            var focused = TopLevel.GetTopLevel(Card)?.FocusManager?.GetFocusedElement() as Visual;
            int at = rows.FindIndex(row => row == focused || focused is not null && row.IsVisualAncestorOf(focused));
            int next = at < 0 ? 0 : (at + (e.Key == Key.Down ? 1 : rows.Count - 1)) % rows.Count;
            rows[next].Focus(NavigationMethod.Directional);
            e.Handled = true;
        }
    }

    Window? Owner => TopLevel.GetTopLevel(this) as Window;

    /// <summary>The change of folder, with what the way from a release to a source copy asks (<see cref="Editions.BringAlongAsync"/>).</summary>
    async Task GoAsync(string path)
    {
        if (Owner is not { } owner) return;
        await App.SwitchServerAsync(owner, _shared, path,
            before: () => Editions.BringAlongAsync(_shared, path, (kind, title, message, buttons) => Dialog.Show(owner, kind, title, message, null, buttons)));
    }

    async void RowClicked(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not FolderRow row) return;
        List.IsOpen = false;
        if (row.Folder.Current) return;
        // As it is now: the list may have been open for a while.
        if (FolderList.Read(row.Path, current: false).Kind == FolderKind.Missing) await FindAsync(row.Path);
        else await GoAsync(row.Path);
    }

    /// <summary>A folder of the list that is not there: found again, taken off the list, or left as it is.</summary>
    async Task FindAsync(string path)
    {
        if (Owner is not { } owner) return;
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

    async void ForgetClicked(object? sender, RoutedEventArgs e)
    {
        // The row under this button is a button too: the click is this one's alone.
        e.Handled = true;
        if ((sender as Control)?.DataContext is not FolderRow row || row.Folder.Current) return;
        Forget(row.Path);
        await FillAsync();
    }

    async void AddClicked(object? sender, RoutedEventArgs e)
    {
        List.IsOpen = false;
        if (Owner is not { } owner) return;
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

    void NewCopyClicked(object? sender, RoutedEventArgs e)
    {
        List.IsOpen = false;
        NewCopyRequested?.Invoke();
    }
}
