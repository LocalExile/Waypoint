using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MaterialDesignThemes.Wpf;

namespace Waypoint;

/// <summary>A line of the text as the import took it.</summary>
sealed record ImportRow(int Line, string Number, string Text, string Note, PackIconKind Icon, Brush Brush, Brush Back, double Opacity);

/// <summary>
/// The item crafter's import: the item's text (from the clipboard, editable), read again as it changes
/// (<see cref="ItemImport"/>), with every line and what it becomes; a line left out or the one that stops the import is
/// red, and clicking a line selects it in the text. Add hands the item to the crafter.
/// </summary>
public partial class ItemImportWindow : FormWindow
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
        Owner = owner;
        _timer.Tick += async (_, _) => await Read();
        Source.Text = text;
        Loaded += (_, _) => KeepOnScreen();
        Loaded += async (_, _) =>
        {
            Source.Focus();
            await Read();
        };
        Closed += (_, _) => _timer.Stop();
    }

    void SourceChanged(object sender, TextChangedEventArgs e)
    {
        AddButton.IsEnabled = false;
        _timer.Stop();
        _timer.Start();
    }

    async Task Read()
    {
        _timer.Stop();
        int version = ++_version;
        string text = Source.Text;
        if (text.Trim().Length == 0)
        {
            _read = null;
            Result.ItemsSource = null;
            Show(L.Text("Paste the item's text above."), "Shell.TextSecondary", PackIconKind.InformationOutline);
            return;
        }
        Show(L.Text("Reading …"), "Shell.TextSecondary", PackIconKind.InformationOutline);
        var (read, error) = await Task.Run<(ImportedItem?, ImportException?)>(() =>
        {
            try { return (ItemImport.Read(text, _catalog), null); }
            catch (ImportException e) { return (null, e); }
        });
        if (version != _version || !IsLoaded) return;
        _read = read;
        if (read is null)
        {
            // Nothing is taken; the line that stops it is marked.
            var lines = text.ReplaceLineEndings("\n").Split('\n');
            Result.ItemsSource = Enumerable.Range(0, lines.Length).Where(i => lines[i].Trim().Length > 0)
                .Select(i => Row(new ImportLine(i, lines[i].TrimEnd(), i == error!.Line ? ImportStatus.Unmatched : ImportStatus.Skipped,
                    i == error.Line ? error.Message : ""))).ToList();
            Show(error!.Message, "Shell.Error", PackIconKind.AlertCircleOutline);
            AddButton.IsEnabled = false;
            return;
        }
        Result.ItemsSource = read.Lines.Select(Row).ToList();
        int left = read.Unmatched.Count;
        if (left == 0) Show(L.Text("{0}: {1} modifiers, every line found.", read.Name, read.Item.Mods.Count), "Shell.Good", PackIconKind.CheckCircleOutline);
        else Show(L.Text("{0}: {1} modifiers. {2} lines are left out (red): the crafter has no modifier worded so.", read.Name, read.Item.Mods.Count, left),
            "Shell.Warning", PackIconKind.AlertOutline);
        AddButton.IsEnabled = true;
    }

    void Show(string text, string brush, PackIconKind icon)
    {
        Summary.Text = text;
        Summary.Foreground = SummaryIcon.Foreground = Theme.Brush(brush);
        SummaryIcon.Kind = icon;
    }

    static ImportRow Row(ImportLine line)
    {
        var (icon, brush) = line.Status switch
        {
            ImportStatus.Mod => (PackIconKind.CheckCircleOutline, Theme.Brush("Shell.Good")),
            ImportStatus.Used => (PackIconKind.Check, Theme.Brush("Shell.TextSecondary")),
            ImportStatus.Unmatched => (PackIconKind.AlertCircleOutline, Theme.Brush("Shell.Error")),
            _ => (PackIconKind.Minus, Theme.Brush("Shell.TextSecondary")),
        };
        Brush back = Brushes.Transparent;
        if (line.Status == ImportStatus.Unmatched)
        {
            var tint = new SolidColorBrush(((SolidColorBrush)Theme.Brush("Shell.Error")).Color) { Opacity = .14 };
            tint.Freeze();
            back = tint;
        }
        return new ImportRow(line.Number, (line.Number + 1).ToString(), line.Text, line.Note, icon, brush, back, line.Status == ImportStatus.Skipped ? .55 : 1);
    }

    /// <summary>The chosen line, selected in the text.</summary>
    void ResultChosen(object sender, SelectionChangedEventArgs e)
    {
        if (Result.SelectedItem is not ImportRow row || row.Line >= Source.LineCount) return;
        int start = Source.GetCharacterIndexFromLineIndex(row.Line);
        if (start < 0) return;
        Source.Select(start, Source.GetLineText(row.Line).TrimEnd('\r', '\n').Length);
        Source.ScrollToLine(row.Line);
    }

    void AddClicked(object sender, RoutedEventArgs e)
    {
        if (_read is null) return;
        Imported = _read;
        DialogResult = true;
    }
}
