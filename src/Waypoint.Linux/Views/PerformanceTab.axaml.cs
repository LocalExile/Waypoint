using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Threading;

namespace Waypoint;

partial class PerformanceTab : UserControl
{
    Shared _shared = null!;
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    LineChart[] Charts => [CpuChart, MemoryChart, HeapChart, GcChart, PoolChart, ContentionChart];

    public PerformanceTab()
    {
        InitializeComponent();
        ShowLanguage();
    }

    /// <summary>The texts Windows gives through {l:Loc}.</summary>
    void ShowLanguage()
    {
        WindowLabel.Text = L.Text("Window");
        CountersLabel.Text = L.Text(".NET counters of");
        string[] windows = ["1 min", "5 min", "15 min", "60 min"];
        for (int i = 0; i < windows.Length; i++) ((ComboBoxItem)WindowPicker.Items[i]!).Content = L.Text(windows[i]);
        CpuChart.Title = L.Text("CPU");
        CpuChart.Unit = L.Text("% of the machine");
        MemoryChart.Title = L.Text("Memory");
        MemoryChart.Unit = L.Text("MB, working set");
        HeapChart.Title = L.Text("GC heap and allocation");
        HeapChart.Unit = L.Text("MB, MB/s");
        GcChart.Unit = L.Text("collections/s, % time");
        PoolChart.Title = L.Text("Thread pool");
        PoolChart.Unit = L.Text("threads, queued work items");
        ContentionChart.Title = L.Text("Contention and exceptions");
        ContentionChart.Unit = L.Text("per s");
    }

    public void Attach(Shared shared)
    {
        _shared = shared;
        // Every service on CPU and memory; the .NET charts follow the chosen server.
        foreach (var s in Service.All)
        {
            CpuChart.Series.Add(shared.Metrics.Cpu[s]);
            MemoryChart.Series.Add(shared.Metrics.Memory[s]);
        }
        Server.ItemsSource = Service.All.Where(s => s.IsDotnet).ToList();
        Server.DisplayMemberBinding = new ReflectionBinding(nameof(Service.Name));
        Server.SelectedItem = Service.Gateway;
        WindowChanged(this, null!);
        _timer.Tick += (_, _) => Redraw();
        if (IsVisible) { _timer.Start(); Redraw(); }
        L.Current.PropertyChanged += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            ShowLanguage();
            Reselect(WindowPicker);
            Redraw();
        });
    }

    /// <summary>What IsVisibleChanged does on Windows: the charts draw only while they show.</summary>
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

    void ServerChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (Server.SelectedItem is not Service s || _shared is null) return;
        TimeSeries C(string counter) => _shared.Metrics.Runtime[(s, counter)];
        Set(HeapChart, C(RuntimeCounters.GcHeap), C(RuntimeCounters.AllocRate));
        Set(GcChart, C(RuntimeCounters.Gen0), C(RuntimeCounters.Gen1), C(RuntimeCounters.Gen2), C(RuntimeCounters.TimeInGc));
        Set(PoolChart, C(RuntimeCounters.PoolThreads), C(RuntimeCounters.PoolQueue));
        Set(ContentionChart, C(RuntimeCounters.LockContention), C(RuntimeCounters.Exceptions));
        Redraw();
    }

    static void Set(LineChart chart, params TimeSeries[] series)
    {
        chart.Series.Clear();
        chart.Series.AddRange(series);
    }

    void WindowChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_shared is null || WindowPicker.SelectedItem is not ComboBoxItem { Tag: string seconds }) return;
        foreach (var chart in Charts) chart.WindowSeconds = double.Parse(seconds, System.Globalization.CultureInfo.InvariantCulture);
        Redraw();
    }

    void Redraw()
    {
        if (_shared is null) return;
        double now = _shared.Metrics.Now;
        foreach (var chart in Charts)
        {
            chart.Now = now;
            chart.InvalidateVisual();
        }
        if (Server.SelectedItem is Service s) CounterState.Text = _shared.Metrics.CounterState(s);
    }
}
