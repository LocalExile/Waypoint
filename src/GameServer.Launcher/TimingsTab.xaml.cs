using System.Globalization;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace GameServer.Launcher;

public partial class TimingsTab : UserControl
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

    public TimingsTab() => InitializeComponent();

    internal void Attach(Shared shared)
    {
        _shared = shared;
        Changed(this, null!);
        _timer.Tick += (_, _) => Redraw();
        IsVisibleChanged += (_, e) => { if ((bool)e.NewValue) { _timer.Start(); Redraw(); } else _timer.Stop(); };
    }

    double WindowSeconds => WindowPicker.SelectedItem is ComboBoxItem { Tag: string s } ? double.Parse(s, CultureInfo.InvariantCulture) : 60;

    void Changed(object sender, SelectionChangedEventArgs e)
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
