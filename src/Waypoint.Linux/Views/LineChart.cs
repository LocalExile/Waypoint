using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Waypoint;

/// <summary>A small live line chart: the last <see cref="WindowSeconds"/> of each series, the Y axis from zero to a round
/// maximum, a legend with each series' latest value. Drawn directly, the same way as on Windows.</summary>
sealed class LineChart : Control
{
    public static readonly StyledProperty<string> TitleProperty = AvaloniaProperty.Register<LineChart, string>(nameof(Title), "");
    public static readonly StyledProperty<string> UnitProperty = AvaloniaProperty.Register<LineChart, string>(nameof(Unit), "");

    static LineChart() => AffectsRender<LineChart>(TitleProperty, UnitProperty);

    public string Title { get => GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Unit { get => GetValue(UnitProperty); set => SetValue(UnitProperty, value); }
    public string Format { get; set; } = "0.#";
    public List<TimeSeries> Series { get; } = [];
    public double WindowSeconds { get; set; } = 300;
    /// <summary>The right edge, in the series' seconds.</summary>
    public double Now { get; set; }

    const double Left = 52, Right = 10, Top = 24, Bottom = 20;

    static IBrush BrushOf(System.Windows.Media.Color c) => new SolidColorBrush(Color.FromArgb(c.A, c.R, c.G, c.B));

    public override void Render(DrawingContext dc)
    {
        double w = Bounds.Width, h = Bounds.Height;
        var fg = App.Brush("Shell.Text");
        var grid = new Pen(new SolidColorBrush(Color.FromArgb(40, 128, 128, 128)), 1);
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        if (w < Left + Right + 20 || h < Top + Bottom + 20) return;
        static FormattedText Text(string s, double size, IBrush brush, FontWeight weight = FontWeight.Normal) =>
            new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(FontFamily.Default, weight: weight), size, brush);

        // Title and legend on one line.
        var title = Text(Title + (Unit.Length > 0 ? $" ({Unit})" : ""), 12, fg, FontWeight.SemiBold);
        dc.DrawText(title, new Point(4, 2));
        double x0 = 4 + title.Width + 16;
        foreach (var s in Series)
        {
            double last = s.Last;
            // A service or counter by its catalogued name; an operation's label is not in the catalogs and stays as it is.
            var label = Text($"{L.Text(s.Name)} {(double.IsNaN(last) ? "—" : last.ToString(Format, CultureInfo.CurrentCulture))}", 11, fg);
            if (x0 + 14 + label.Width > w) break;
            dc.DrawRectangle(BrushOf(s.Color), null, new Rect(x0, 7, 9, 9));
            dc.DrawText(label, new Point(x0 + 12, 3));
            x0 += 12 + label.Width + 12;
        }

        var plot = new Rect(Left, Top, w - Left - Right, h - Top - Bottom);
        double from = Now - WindowSeconds;
        var data = Series.Select(s => (s, Points: s.Since(from))).ToList();
        double max = data.SelectMany(d => d.Points).Where(p => !double.IsNaN(p.V)).Select(p => p.V).DefaultIfEmpty(0).Max();
        double top = NiceCeiling(max);

        for (int i = 0; i <= 4; i++)
        {
            double y = plot.Bottom - plot.Height * i / 4;
            dc.DrawLine(grid, new Point(plot.Left, y), new Point(plot.Right, y));
            var label = Text((top * i / 4).ToString("0.##", CultureInfo.CurrentCulture), 10, fg);
            dc.DrawText(label, new Point(plot.Left - 6 - label.Width, y - label.Height / 2));
        }
        foreach (double f in (double[])[0, 0.5, 1])
        {
            double x = plot.Left + plot.Width * f;
            double ago = WindowSeconds * (1 - f);
            var label = Text(ago == 0 ? L.Text("now") : $"-{TimeSpan.FromSeconds(ago):m\\:ss}", 10, fg);
            dc.DrawText(label, new Point(Math.Clamp(x - label.Width / 2, plot.Left, plot.Right - label.Width), plot.Bottom + 3));
        }

        using (dc.PushClip(plot))
            foreach (var (s, points) in data)
            {
                var geometry = new StreamGeometry();
                using (var g = geometry.Open())
                {
                    bool open = false;
                    foreach (var (t, v) in points)
                    {
                        if (double.IsNaN(v))
                        {
                            if (open) g.EndFigure(false);
                            open = false;
                            continue;
                        }
                        var p = new Point(plot.Left + (t - from) / WindowSeconds * plot.Width, plot.Bottom - v / top * plot.Height);
                        if (!open) { g.BeginFigure(p, false); open = true; }
                        else g.LineTo(p);
                    }
                    if (open) g.EndFigure(false);
                }
                dc.DrawGeometry(null, new Pen(BrushOf(s.Color), 1.6), geometry);
            }
    }

    /// <summary>1, 2 or 5 times a power of ten, at least the value (1 for nothing to show).</summary>
    internal static double NiceCeiling(double value)
    {
        if (value <= 0 || double.IsNaN(value)) return 1;
        double power = Math.Pow(10, Math.Floor(Math.Log10(value)));
        foreach (double step in (double[])[1, 2, 5, 10])
            if (value <= step * power * 1.0000001) return step * power;
        return 10 * power;
    }
}
