using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Material.Icons;

namespace Waypoint;

/// <summary>A line of the text as the import took it.</summary>
sealed record ImportRow(int Line, string Number, string Text, string Note, MaterialIconKind Icon, IBrush Brush, IBrush Back, double Opacity);

/// <summary>
/// The item crafter's import: the item's text (from the clipboard, editable), read again as it changes
/// (<see cref="ItemImport"/>), with every line and what it becomes; a line left out or the one that stops the import is
/// red, and clicking a line selects it in the text. Add hands the item to the crafter. The Linux copy of
/// src/Waypoint/ItemImportWindow.xaml.cs, in Avalonia.
/// </summary>
partial class ItemImportWindow : FormWindow
{
    readonly CrafterCatalog _catalog;
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    int _version;
    ImportedItem? _read;

    /// <summary>The item to open, once Add was pressed.</summary>
    internal ImportedItem? Imported { get; private set; }

    internal ItemImportWindow(Window owner, CrafterCatalog catalog, string text)
    {
        _catalog = catalog;
        InitializeComponent();
        ShowOver(owner);
        _timer.Tick += async (_, _) => await Read();
        Source.Text = text;
        Opened += async (_, _) =>
        {
            Source.Focus();
            await Read();
        };
        Closed += (_, _) => _timer.Stop();
    }

    void SourceChanged(object? sender, TextChangedEventArgs e)
    {
        AddButton.IsEnabled = false;
        _timer.Stop();
        _timer.Start();
    }

    async Task Read()
    {
        _timer.Stop();
        int version = ++_version;
        string text = Source.Text ?? "";
        if (text.Trim().Length == 0)
        {
            _read = null;
            Result.ItemsSource = null;
            Show(L.Text("Paste the item's text above."), "Shell.TextSecondary", MaterialIconKind.InformationOutline);
            return;
        }
        Show(L.Text("Reading …"), "Shell.TextSecondary", MaterialIconKind.InformationOutline);
        var (read, error) = await Task.Run<(ImportedItem?, ImportException?)>(() =>
        {
            try { return (ItemImport.Read(text, _catalog), null); }
            catch (ImportException e) { return (null, e); }
        });
        if (version != _version || !IsVisible) return;
        _read = read;
        if (read is null)
        {
            // Nothing is taken; the line that stops it is marked.
            var lines = text.ReplaceLineEndings("\n").Split('\n');
            Result.ItemsSource = Enumerable.Range(0, lines.Length).Where(i => lines[i].Trim().Length > 0)
                .Select(i => Row(new ImportLine(i, lines[i].TrimEnd(), i == error!.Line ? ImportStatus.Unmatched : ImportStatus.Skipped,
                    i == error.Line ? error.Message : ""))).ToList();
            Show(error!.Message, "Shell.Error", MaterialIconKind.AlertCircleOutline);
            AddButton.IsEnabled = false;
            return;
        }
        Result.ItemsSource = read.Lines.Select(Row).ToList();
        int left = read.Unmatched.Count;
        if (left == 0) Show(L.Text("{0}: {1} modifiers, every line found.", read.Name, read.Item.Mods.Count), "Shell.Good", MaterialIconKind.CheckCircleOutline);
        else Show(L.Text("{0}: {1} modifiers. {2} lines are left out (red): the crafter has no modifier worded so.", read.Name, read.Item.Mods.Count, left),
            "Shell.Warning", MaterialIconKind.AlertOutline);
        AddButton.IsEnabled = true;
    }

    void Show(string text, string brush, MaterialIconKind icon)
    {
        Summary.Text = text;
        Summary.Foreground = SummaryIcon.Foreground = AppTheme.Brush(brush);
        SummaryIcon.Kind = icon;
    }

    static ImportRow Row(ImportLine line)
    {
        var (icon, brush) = line.Status switch
        {
            ImportStatus.Mod => (MaterialIconKind.CheckCircleOutline, AppTheme.Brush("Shell.Good")),
            ImportStatus.Used => (MaterialIconKind.Check, AppTheme.Brush("Shell.TextSecondary")),
            ImportStatus.Unmatched => (MaterialIconKind.AlertCircleOutline, AppTheme.Brush("Shell.Error")),
            _ => (MaterialIconKind.Minus, AppTheme.Brush("Shell.TextSecondary")),
        };
        IBrush back = Brushes.Transparent;
        if (line.Status == ImportStatus.Unmatched && AppTheme.Brush("Shell.Error") is ISolidColorBrush error)
            back = new SolidColorBrush(error.Color) { Opacity = .14 };
        return new ImportRow(line.Number, (line.Number + 1).ToString(), line.Text, line.Note, icon, brush, back, line.Status == ImportStatus.Skipped ? .55 : 1);
    }

    /// <summary>The chosen line, selected in the text.</summary>
    void ResultChosen(object? sender, SelectionChangedEventArgs e)
    {
        string text = Source.Text ?? "";
        if (Result.SelectedItem is not ImportRow row) return;
        // The line's place in the text, whichever line ends it has.
        int start = 0;
        for (int line = 0; line < row.Line; line++)
        {
            int end = text.IndexOf('\n', start);
            if (end < 0) return;
            start = end + 1;
        }
        int stop = text.IndexOf('\n', start) is var next and >= 0 ? next : text.Length;
        if (stop > start && text[stop - 1] == '\r') stop--;
        Source.SelectionStart = start;
        Source.SelectionEnd = stop;
        Source.CaretIndex = stop;
    }

    void AddClicked(object? sender, RoutedEventArgs e)
    {
        if (_read is null) return;
        Imported = _read;
        Accept();
    }

    void CancelClicked(object? sender, RoutedEventArgs e) => Close();
}
