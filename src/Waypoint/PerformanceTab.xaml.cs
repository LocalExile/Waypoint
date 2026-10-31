using System.Windows.Controls;
using System.Windows.Threading;

namespace Waypoint;

public partial class PerformanceTab : UserControl
{
    Shared _shared = null!;
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    LineChart[] Charts => [CpuChart, MemoryChart, HeapChart, GcChart, PoolChart, ContentionChart];

    public PerformanceTab() => InitializeComponent();

    internal void Attach(Shared shared)
    {
        _shared = shared;
        // Every service on CPU and memory; the .NET charts follow the chosen server.
        foreach (var s in Service.All)
        {
            CpuChart.Series.Add(shared.Metrics.Cpu[s]);
            MemoryChart.Series.Add(shared.Metrics.Memory[s]);
        }
        Server.ItemsSource = Service.All.Where(s => s.IsDotnet).ToList();
        Server.DisplayMemberPath = nameof(Service.Name);
        Server.SelectedItem = Service.Gateway;
        WindowChanged(this, null!);
        _timer.Tick += (_, _) => Redraw();
        IsVisibleChanged += (_, e) => { if ((bool)e.NewValue) { _timer.Start(); Redraw(); } else _timer.Stop(); };
        L.Current.PropertyChanged += (_, _) =>
        {
            LocRefresh.Reselect(WindowPicker);
            Redraw();
        };
    }

    void ServerChanged(object sender, SelectionChangedEventArgs e)
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

    void WindowChanged(object sender, SelectionChangedEventArgs e)
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
