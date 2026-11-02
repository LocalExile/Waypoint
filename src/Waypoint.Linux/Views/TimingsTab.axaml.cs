using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Color = System.Windows.Media.Color;

namespace Waypoint;

partial class TimingsTab : UserControl
{
    /// <summary>The colours of the busiest operations, by rank.</summary>
    static readonly Color[] Palette =
    [
        Color.FromRgb(0xe6, 0x7e, 0x22), Color.FromRgb(0x2e, 0x86, 0xde), Color.FromRgb(0x27, 0xae, 0x60),
        Color.FromRgb(0xc0, 0x39, 0x2b), Color.FromRgb(0x8e, 0x6c, 0xef),
    ];

    Shared _shared = null!;
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    LineChart[] Charts => [SlowChart, LoadChart];

    public TimingsTab()
    {
        InitializeComponent();
        ShowLanguage();
    }

    /// <summary>The texts Windows gives through {l:Loc}.</summary>
    void ShowLanguage()
    {
        WindowLabel.Text = L.Text("Window");
        ShowLabel.Text = L.Text("Show");
        string[] windows = ["30 s", "1 min", "5 min", "15 min"];
        for (int i = 0; i < windows.Length; i++) ((ComboBoxItem)WindowPicker.Items[i]!).Content = L.Text(windows[i]);
        string[] kinds = ["Everything", "Client messages", "Server phases", "Level generation"];
        for (int i = 0; i < kinds.Length; i++) ((ComboBoxItem)KindPicker.Items[i]!).Content = L.Text(kinds[i]);
        SlowChart.Title = L.Text("p95 per second, busiest operations");
        SlowChart.Unit = L.Text("ms");
        LoadChart.Title = L.Text("Server time");
        LoadChart.Unit = L.Text("ms per second");
        OperationHeader.Text = L.Text("Operation");
        KindHeader.Text = L.Text("Kind");
        Column(CallsHeader, "Calls/s", "Calls per second over the window");
        Column(AverageHeader, "Avg ms", "Total time divided by the calls in the window");
        Column(P95Header, "p95 ms", "The worst second's 95th percentile in the window (the server publishes quantiles per second)");
        Column(P99Header, "p99 ms", "The worst second's 99th percentile in the window");
        Column(LoadHeader, "ms/s", "Server time per second, all connections together; waits (queues, gates, locks) count their waiting time");
    }

    static void Column(TextBlock header, string text, string tip)
    {
        header.Text = L.Text(text);
        ToolTip.SetTip(header, L.Text(tip));
    }

    public void Attach(Shared shared)
    {
        _shared = shared;
        Changed(this, null!);
        _timer.Tick += (_, _) => Redraw();
        if (IsVisible) _timer.Start();
        L.Current.PropertyChanged += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            ShowLanguage();
            Reselect(WindowPicker);
            Reselect(KindPicker);
            Redraw();
        });
    }

    /// <summary>What IsVisibleChanged does on Windows: the table and charts follow only while they show.</summary>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != IsVisibleProperty || _shared is null) return;
        if (IsVisible) { _timer.Start(); Redraw(); } else _timer.Stop();
    }

    /// <summary>A closed ComboBox shows a copy of its selection's text: choosing it again brings the new language.</summary>
    static void Reselect(ComboBox box)
    {
        int selected = box.SelectedIndex;
        box.SelectedIndex = -1;
        box.SelectedIndex = selected;
    }

    double WindowSeconds => WindowPicker.SelectedItem is ComboBoxItem { Tag: string s } ? double.Parse(s, CultureInfo.InvariantCulture) : 60;

    void Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_shared is null) return;
        foreach (var chart in Charts) chart.WindowSeconds = WindowSeconds;
        Redraw();
    }

    void Redraw()
    {
        if (_shared is null) return;
        var timings = _shared.Metrics.Timings;
        double now = _shared.Metrics.Now;
        string kind = KindPicker.SelectedItem is ComboBoxItem { Tag: string k } ? k : "";
        var rows = timings.Window(now - WindowSeconds, now).Where(r => kind.Length == 0 || r.Kind == kind).ToList();
        Table.ItemsSource = rows;

        foreach (var chart in Charts) chart.Series.Clear();
        for (int i = 0; i < Math.Min(rows.Count, Palette.Length); i++)
        {
            var (p95, load) = timings.Series(rows[i].Key);
            p95.Color = load.Color = Palette[i];
            SlowChart.Series.Add(p95);
            LoadChart.Series.Add(load);
        }
        foreach (var chart in Charts)
        {
            chart.Now = now;
            chart.InvalidateVisual();
        }
        State.Text = _shared.Metrics.TimingsState();
    }
}
