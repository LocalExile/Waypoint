using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Launcher.Core;
using Microsoft.Win32;

namespace Waypoint;

/// <summary>
/// Converts a loot filter made for a newer patch into one the checkout's client loads (<see cref="LootFilters"/>), with the
/// checkout's own item names (<see cref="ClientItemNames"/>), and saves it where the game lists filters.
/// </summary>
public partial class LootFilterWindow : FormWindow
{
    string _folder = LootFilters.FilterFolder();
    IReadOnlyList<FileInfo> _files = [];
    ClientItemNames? _names;

    internal LootFilterWindow()
    {
        InitializeComponent();
        Standalone = true;
        Loaded += (_, _) => { ShowFolder(); Refresh(null); };
    }

    void ShowFolder() => Folder.Text = _folder;

    /// <summary>Lists the filters, newest first, with <paramref name="select"/> (a file the player chose) selected.</summary>
    void Refresh(string? select)
    {
        var files = LootFilters.Candidates(_folder).ToList();
        if (select is not null && !files.Any(f => Same(f.FullName, select))) files.Insert(0, new FileInfo(select));
        _files = files;
        Filters.Items.Clear();
        foreach (var file in files)
        {
            var (name, version) = LootFilters.ReadHeader(file.FullName);
            string label = name is null ? file.Name : version is null ? name : $"{name} [{version}]";
            Filters.Items.Add(new ListBoxItem
            {
                Content = $"{label}    {Path.GetFileName(file.DirectoryName)} · {file.LastWriteTime:g}",
                ToolTip = file.FullName,
            });
        }
        int index = select is null ? 0 : files.FindIndex(f => Same(f.FullName, select));
        Filters.SelectedIndex = files.Count == 0 ? -1 : Math.Max(index, 0);
        if (files.Count == 0) Result.Text = L.Text("No loot filters found. Download one from FilterBlade, or sync it to your account, then choose it with Other file….");
    }

    static bool Same(string a, string b) => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    void ChooseFile(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = L.Text("Choose a loot filter"),
            Filter = L.Text("Loot filters") + " (*.filter)|*.filter|" + L.Text("All files") + " (*.*)|*.*",
            InitialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
        };
        if (dialog.ShowDialog(this) == true) Refresh(dialog.FileName);
    }

    void ChooseFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = L.Text("Choose Documents\\My Games\\Path of Exile"), InitialDirectory = _folder };
        if (dialog.ShowDialog(this) != true) return;
        string resolved = LootFilters.ResolveFilterFolder(dialog.FolderName);
        Result.Text = Same(resolved, dialog.FolderName) ? ""
            : L.Text("The game reads filters only from the Path of Exile folder itself, so they are saved in {0}.", resolved);
        _folder = resolved;
        ShowFolder();
        Refresh(null);
    }

    void OpenFolder(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_folder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_folder}\"") { UseShellExecute = true });
    }

    async void ConvertClicked(object sender, RoutedEventArgs e)
    {
        if (Filters.SelectedIndex < 0 || Filters.SelectedIndex >= _files.Count) return;
        string source = _files[Filters.SelectedIndex].FullName, folder = _folder;
        ConvertButton.IsEnabled = false;
        Result.Text = L.Text("Converting…");
        try
        {
            // Waypoint's working directory is the checkout's root, a release's or a source copy's alike.
            string root = Environment.CurrentDirectory;
            var (result, target) = await Task.Run(() =>
            {
                _names ??= ClientItemNames.Load(root);
                if (_names is null) throw new InvalidOperationException(L.Text("This LocalExile folder has no client tables ({0}).", ClientItemNames.TablesFolder));
                return LootFilters.ConvertFile(source, folder, _names);
            });
            var lines = new List<string>
            {
                L.Text("Saved {0}.", target),
                L.Text("{0} item names this client does not have were taken out.", result.RemovedNames),
                L.Text("{0} rules were turned off: they only cover items or conditions this client does not have.", result.DisabledBlocks),
            };
            if (result.RemovedSamples.Count > 0) lines.Add(string.Join(", ", result.RemovedSamples) + (result.RemovedNames > result.RemovedSamples.Count ? ", …" : ""));
            lines.Add(L.Text("In game: Options > Game > Item Filter, then pick {0}.", Path.GetFileNameWithoutExtension(target)));
            Result.Text = string.Join(Environment.NewLine, lines);
            Refresh(source);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Result.Text = ex.Message;
        }
        finally { ConvertButton.IsEnabled = true; }
    }
}
