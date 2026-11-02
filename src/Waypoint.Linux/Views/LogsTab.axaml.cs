using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;

namespace Waypoint;

partial class LogsTab : UserControl
{
    Shared _shared = null!;
    ObservableCollection<LogLine> _shown = [];
    bool _following;
    /// <summary>The most lines shown; past it the view starts again from the newest <see cref="ShownAfterTrim"/> (removing
    /// the oldest one by one would move every line each time).</summary>
    const int ShownMax = 6000, ShownAfterTrim = 5000;

    /// <summary>A line's colour by what it reports.</summary>
    public static IValueConverter LineBrush { get; } = new FuncValueConverter<Severity, IBrush>(severity => App.Brush(severity switch
    {
        Severity.Error => "Shell.Error",
        Severity.Warning => "Shell.Warning",
        _ => "Shell.Text",
    }));

    public LogsTab() => InitializeComponent();

    LogSource? Selected => SourceList.SelectedItem as LogSource;

    public void Attach(Shared shared)
    {
        _shared = shared;
        Lines.ItemsSource = _shown;
        SourceList.ItemsSource = shared.Logs.Sources;
        SourceList.SelectedItem = shared.Logs.Sources.First(s => s.Name == "Gateway");
        shared.Logs.Appended += (source, lines, restarted) => Dispatcher.UIThread.Post(() => Appended(source, lines, restarted));
        L.Current.PropertyChanged += (_, _) =>
        {
            foreach (var source in shared.Logs.Sources) source.DisplayChanged();
            ShowTexts();
            ShowCount();
        };
        // Lines that came while another section was showing were not laid out.
        ViewTools.WhenShown(this, FollowEnd);
        ShowTexts();
        ShowCount();
    }

    void ShowTexts()
    {
        FilterLabel.Text = L.Text("Filter");
        ToolTip.SetTip(Filter, L.Text("Lines containing this text (case ignored)"));
        Problems.Content = L.Text("Errors and warnings only");
        Follow.Content = L.Text("Follow");
        ToolTip.SetTip(Follow, L.Text("Scroll to each new line"));
        CopyButton.Content = L.Text("Copy");
        ToolTip.SetTip(CopyButton, L.Text("The shown lines to the clipboard"));
        OpenFileButton.Content = L.Text("Open file");
        ClearButton.Content = L.Text("Clear");
        ToolTip.SetTip(ClearButton, L.Text("Clears the view; the file is not changed"));
        ReportButton.Content = L.Text("Diagnostic report");
        ToolTip.SetTip(ReportButton, L.Text("A redacted report for a bug: previewed first, copied only when you ask"));
    }

    /// <summary>Selects a source by name (a service's row on the Control section).</summary>
    public void Show(string source)
    {
        if (_shared?.Logs.Sources.FirstOrDefault(s => s.Name == source) is { } found) SourceList.SelectedItem = found;
    }

    void Appended(LogSource source, IReadOnlyList<LogLine> lines, bool restarted)
    {
        source.DisplayChanged();
        if (source != Selected) return;
        if (restarted) { Rebuild(); return; }
        var matching = lines.Where(Matches).ToList();
        if (matching.Count > 1000) { Rebuild(); return; }
        if (_shown.Count + matching.Count > ShownMax) { Rebuild(); return; }
        foreach (var line in matching) _shown.Add(Shown(source, line));
        AfterChange();
    }

    bool Matches(LogLine line) =>
        (Problems.IsChecked != true || line.Severity != Severity.Info)
        && (string.IsNullOrEmpty(Filter.Text) || line.Text.Contains(Filter.Text, StringComparison.OrdinalIgnoreCase));

    void Rebuild()
    {
        var source = Selected;
        // A new collection is one reset for the list, where clearing and adding would be an update per line.
        _shown = source is null ? [] : new(_shared.Logs.Snapshot(source).Where(Matches).TakeLast(ShownAfterTrim).Select(l => Shown(source, l)));
        Lines.ItemsSource = _shown;
        AfterChange();
    }

    /// <summary>"All errors" names each line's log and time.</summary>
    LogLine Shown(LogSource source, LogLine line) =>
        source == _shared.Logs.AllErrors ? line with { Text = $"{line.At:HH:mm:ss} {line.Source}: {line.Text}" } : line;

    void AfterChange()
    {
        ShowCount();
        FollowEnd();
    }

    /// <summary>To the newest line, once the list has laid out what was just added: before that, it has no such line to
    /// scroll to, where WPF's ScrollIntoView waits for layout by itself. Every line of a burst is one scroll.</summary>
    void FollowEnd()
    {
        if (_following || Follow.IsChecked != true) return;
        _following = true;
        Dispatcher.UIThread.Post(() =>
        {
            _following = false;
            if (Follow.IsChecked == true && _shown.Count > 0) Lines.ScrollIntoView(_shown[^1]);
        }, DispatcherPriority.Background);
    }

    void ShowCount() => Count.Text = L.Text("{0} lines", _shown.Count);

    void SourceChanged(object? sender, SelectionChangedEventArgs e) => Rebuild();

    void FilterChanged(object? sender, RoutedEventArgs e)
    {
        if (_shared is not null) Rebuild();
    }

    async void Copy(object? sender, RoutedEventArgs e)
    {
        var lines = Lines.SelectedItems is { Count: > 0 } selected ? selected.Cast<LogLine>() : _shown;
        await ViewTools.Copy(this, string.Join(Environment.NewLine, lines.Select(l => l.Text)));
    }

    void OpenFile(object? sender, RoutedEventArgs e)
    {
        if (Selected?.File is not { } file || !File.Exists(file)) return;
        ViewTools.Open(Path.GetFullPath(file));
    }

    void DiagnosticReportClicked(object? sender, RoutedEventArgs e)
    {
        var workspace = _shared.Workspace;
        var state = workspace.Latest ?? new RuntimeSnapshot(Environment.CurrentDirectory, "Unavailable", "", "", "", "",
            [new("Installation inspection", false, "Select and check a valid checkout first.")], [], []);
        string shown = string.Join(Environment.NewLine, _shown.Select(l => l.Text));
        new DiagnosticPreviewDialog(ViewTools.Owner(this), additional => DiagnosticReport.Create(state, Updates.Current.ToString(), workspace.OperationText,
            workspace.LastFailure, Selected?.Name ?? "", shown, workspace.PrivateNames.Concat(additional))).ShowDialog();
    }

    void Clear(object? sender, RoutedEventArgs e)
    {
        if (Selected is not { } source) return;
        _shared.Logs.Clear(source);
        source.DisplayChanged();
        Rebuild();
    }
}
