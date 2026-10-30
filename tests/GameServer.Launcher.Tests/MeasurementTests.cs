using GameServer.Launcher;

namespace GameServer.Launcher.Tests;

public class MeasurementTests
{
    [Theory]
    [InlineData(0, 1)]
    [InlineData(double.NaN, 1)]
    [InlineData(0.7, 1)]
    [InlineData(1, 1)]
    [InlineData(1.3, 2)]
    [InlineData(3, 5)]
    [InlineData(7, 10)]
    [InlineData(120, 200)]
    [InlineData(4096, 5000)]
    public void Chart_scales_end_at_one_two_or_five_times_a_power_of_ten(double max, double expected) =>
        Assert.Equal(expected, LineChart.NiceCeiling(max), 6);

    [Fact]
    public void Cpu_is_the_share_of_every_core_over_the_interval()
    {
        Assert.Equal(25, ServiceMonitor.CpuPercent(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 4), 6);
        Assert.Equal(100, ServiceMonitor.CpuPercent(TimeSpan.FromSeconds(9), TimeSpan.FromSeconds(1), 4), 6);
        Assert.Equal(0, ServiceMonitor.CpuPercent(TimeSpan.FromSeconds(1), TimeSpan.Zero, 4));
    }

    [Fact]
    public void Counter_payloads_give_a_polling_counters_mean_and_an_incrementing_ones_change()
    {
        static Dictionary<string, object> Payload(params (string, object)[] fields) =>
            new() { ["Payload"] = fields.ToDictionary(f => f.Item1, f => f.Item2) };

        Assert.Equal(("gc-heap-size", 42.5), RuntimeCounters.Counter(Payload(("Name", "gc-heap-size"), ("CounterType", "Mean"), ("Mean", 42.5))));
        Assert.Equal(("gen-0-gc-count", 3.0), RuntimeCounters.Counter(Payload(("Name", "gen-0-gc-count"), ("CounterType", "Sum"), ("Increment", 3.0), ("Mean", 0.0))));
        Assert.Null(RuntimeCounters.Counter(Payload(("Name", "x"), ("CounterType", "Sum"))));
        Assert.Null(RuntimeCounters.Counter("not a payload"));
    }

    [Fact]
    public void A_series_keeps_its_newest_points()
    {
        var s = new TimeSeries("cpu", System.Windows.Media.Colors.Red, capacity: 3);
        Assert.True(double.IsNaN(s.Last));
        for (int t = 0; t < 5; t++) s.Add(t, t * 10);
        Assert.Equal([(2.0, 20.0), (3.0, 30.0), (4.0, 40.0)], s.Since(0));
        Assert.Equal([(4.0, 40.0)], s.Since(3.5));
        Assert.Equal(40, s.Last);
    }
}
