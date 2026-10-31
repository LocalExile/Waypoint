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
    public void Histogram_tags_and_quantiles_give_the_operation_and_its_tail()
    {
        Assert.Equal(("Message", "0x0025"), OperationTimings.Tags("operation=0x0025,phase=Message"));
        Assert.Equal(("Handler", "all"), OperationTimings.Tags("phase=Handler"));
        Assert.Null(OperationTimings.Tags(""));
        Assert.Equal((4.5, 6.75), OperationTimings.Quantiles("0.5=1.25;0.95=4.5;0.99=6.75"));
        Assert.Null(OperationTimings.Quantiles(""));
    }

    [Fact]
    public void The_window_sums_calls_and_time_and_keeps_the_worst_seconds_tail()
    {
        var timings = new OperationTimings();
        timings.Add(0, "Message", "0x0025", count: 10, sum: 20, p95: 3, p99: 4);
        timings.Add(1, "Message", "0x0025", count: 30, sum: 30, p95: 1.5, p99: 9);
        timings.Add(1, "LevelGen", "1_1_1", count: 1, sum: 400, p95: 400, p99: 400);
        timings.Add(1, "Handler", "all", count: 0, sum: 0, p95: 0, p99: 0);

        var rows = timings.Window(from: -9, now: 1);
        Assert.Equal(["1_1_1", "0x0025"], rows.Select(r => r.Label));   // busiest first; nothing called, no row
        var move = rows[1];
        Assert.Equal(OperationTimings.Messages, move.Kind);
        Assert.Equal(40, move.PerSecond, 6);   // one second seen since the first interval
        Assert.Equal(50.0 / 40, move.Average, 6);
        Assert.Equal(3, move.P95);
        Assert.Equal(9, move.P99);

        Assert.Equal(["0x0025"], timings.Window(from: 0.5, now: 1).Where(r => r.Kind == OperationTimings.Messages).Select(r => r.Label));
        Assert.Equal(1.5, timings.Window(from: 0.5, now: 1)[1].P95);
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
