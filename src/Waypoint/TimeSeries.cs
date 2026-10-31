namespace Waypoint;

/// <summary>A named series of (seconds since the launcher started, value) points, the newest <see cref="Capacity"/> of
/// them. NaN marks a gap (the process was not running). Written by the sampler, read by the chart: both lock it.</summary>
sealed class TimeSeries(string name, System.Windows.Media.Color color, int capacity = 3600)
{
    readonly (double T, double V)[] _points = new (double, double)[capacity];
    int _start, _count;

    public string Name { get; } = name;
    public System.Windows.Media.Color Color { get; set; } = color;
    public int Capacity => _points.Length;

    public void Add(double t, double v)
    {
        lock (_points)
        {
            _points[(_start + _count) % _points.Length] = (t, v);
            if (_count < _points.Length) _count++;
            else _start = (_start + 1) % _points.Length;
        }
    }

    /// <summary>The points from <paramref name="from"/> on, oldest first.</summary>
    public List<(double T, double V)> Since(double from)
    {
        lock (_points)
        {
            var list = new List<(double, double)>();
            for (int i = 0; i < _count; i++)
            {
                var p = _points[(_start + i) % _points.Length];
                if (p.T >= from) list.Add(p);
            }
            return list;
        }
    }

    public double Last
    {
        get { lock (_points) return _count == 0 ? double.NaN : _points[(_start + _count - 1) % _points.Length].V; }
    }
}
