using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;

namespace GameServer.Launcher;

public partial class LogsTab : UserControl
{
    Shared _shared = null!;
    ObservableCollection<LogLine> _shown = [];
    /// <summary>The most lines shown; past it the view starts again from the newest <see cref="ShownAfterTrim"/> (removing
    /// the oldest one by one would move every line each time).</summary>
    const int ShownMax = 6000, ShownAfterTrim = 5000;

    public LogsTab() => InitializeComponent();

    LogSource? Selected => SourceList.SelectedItem as LogSource;

    internal void Attach(Shared shared)
    {
        _shared = shared;
        Lines.ItemsSource = _shown;
        SourceList.ItemsSource = shared.Logs.Sources;
        SourceList.SelectedItem = shared.Logs.Sources.First(s => s.Name == "Gateway");
        shared.Logs.Appended += (source, lines, restarted) => Dispatcher.BeginInvoke(() => Appended(source, lines, restarted));
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
        && (Filter.Text.Length == 0 || line.Text.Contains(Filter.Text, StringComparison.OrdinalIgnoreCase));

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
        Count.Text = $"{_shown.Count} lines";
        if (Follow.IsChecked == true && _shown.Count > 0) Lines.ScrollIntoView(_shown[^1]);
    }

    void SourceChanged(object sender, SelectionChangedEventArgs e) => Rebuild();

    void FilterChanged(object sender, RoutedEventArgs e)
    {
        if (_shared is not null) Rebuild();
    }

    void Copy(object sender, RoutedEventArgs e)
    {
        var lines = Lines.SelectedItems.Count > 0 ? Lines.SelectedItems.Cast<LogLine>() : _shown;
        try { Clipboard.SetText(string.Join(Environment.NewLine, lines.Select(l => l.Text))); }
        catch (System.Runtime.InteropServices.ExternalException) { }
    }

    void OpenFile(object sender, RoutedEventArgs e)
    {
        if (Selected?.File is not { } file || !File.Exists(file)) return;
        Process.Start(new ProcessStartInfo(Path.GetFullPath(file)) { UseShellExecute = true })?.Dispose();
    }

    void Clear(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } source) return;
        _shared.Logs.Clear(source);
        source.DisplayChanged();
        Rebuild();
    }
}
