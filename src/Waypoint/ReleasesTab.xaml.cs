using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;

namespace Waypoint;

/// <param name="Badge">Where it stands against the install.</param>
sealed record ReleaseRow(string Tag, string Badge, string Published, string Notes, string Page);

/// <summary>
/// The Versions section of a release install: its version and every release's notes, from GitHub's public API. Installing
/// goes through the update card, and only forward: an older release may not read saves a newer one has changed.
/// </summary>
public partial class ReleasesTab : UserControl
{
    bool _loaded;
    IReadOnlyList<LocalExileRelease> _releases = [];

    /// <summary>Check for updates: the window has the update card.</summary>
    internal event Action? CheckRequested;
    /// <summary>The player wants another folder.</summary>
    internal event Action? ChangeFolderRequested;
    /// <summary>The player wants a new git copy set up.</summary>
    internal event Action? NewCopyRequested;

    public ReleasesTab() => InitializeComponent();

    internal void Attach(Shared shared)
    {
        if (!Repo.IsInstall) return;
        IsVisibleChanged += async (_, e) =>
        {
            ShowRows();
            if ((bool)e.NewValue && !_loaded) await LoadAsync();
        };
        L.Current.PropertyChanged += (_, _) => ShowRows();
    }

    async Task LoadAsync()
    {
        Status.Text = L.Text("Looking for releases …");
        try
        {
            using var client = Updates.Client();
            _releases = await LocalExileReleases.ListAsync(client);
            _loaded = true;
            Status.Text = "";
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            Status.Text = L.Text("Could not read the releases: {0}", e.Message);
        }
        ShowRows();
    }

    void ShowRows()
    {
        var installed = LocalExileReleases.Installed(Environment.CurrentDirectory);
        Installed.Text = installed is null ? L.Text("LocalExile") : L.Text("LocalExile {0}", installed);
        List.ItemsSource = _releases.Select(r => new ReleaseRow(r.Tag,
            installed is null ? "" : r.Version.CompareTo(installed) == 0 ? L.Text("Installed") : r.Version.IsNewerThan(installed) ? L.Text("Newer") : "",
            r.Published?.LocalDateTime.ToString("d", CultureInfo.CurrentCulture) ?? "",
            Plain(r.Notes), r.Page)).ToList();
    }

    /// <summary>The notes without what only an HTML page hides.</summary>
    static string Plain(string notes) => Regex.Replace(notes.Replace("\r\n", "\n"), @"<!--.*?-->\n*", "", RegexOptions.Singleline).Trim();

    async void RefreshClicked(object sender, RoutedEventArgs e) => await LoadAsync();

    void CheckClicked(object sender, RoutedEventArgs e) => CheckRequested?.Invoke();

    void ChangeFolderClicked(object sender, RoutedEventArgs e) => ChangeFolderRequested?.Invoke();

    void NewCopyClicked(object sender, RoutedEventArgs e) => NewCopyRequested?.Invoke();

    void OpenClicked(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is ReleaseRow { Page.Length: > 0 } row)
            Process.Start(new ProcessStartInfo(row.Page) { UseShellExecute = true })?.Dispose();
    }
}
