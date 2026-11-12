using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Waypoint;

/// <summary>
/// A folder's path on one line of a place too narrow for it. The end of a path says which folder it is, so the end is
/// what stays and the start is what goes, behind one "…": "…\Games\LocalExile\Client", never dots at both ends and never
/// the end cut off. The text is fitted to the width its panel gives it, and again whenever that width changes. (A text
/// block wider than its place keeps its own full width and is only clipped, so the panel's width is the one to fit to.)
/// For a line in a panel whose width does not depend on this text: a fixed sidebar, a row of a list.
/// </summary>
static class PathLine
{
    public static readonly DependencyProperty PathProperty = DependencyProperty.RegisterAttached("Path", typeof(string), typeof(PathLine),
        new PropertyMetadata(null, (target, _) =>
        {
            if (target is not TextBlock line) return;
            line.TextTrimming = TextTrimming.None;
            line.TextWrapping = TextWrapping.NoWrap;
            line.Loaded -= Loaded;
            line.Loaded += Loaded;
            Follow(line);
            Fit(line);
        }));

    /// <summary>The panel whose width the line is fitted to, once it follows it.</summary>
    static readonly DependencyProperty PanelProperty = DependencyProperty.RegisterAttached("Panel", typeof(FrameworkElement), typeof(PathLine));

    public static string? GetPath(TextBlock line) => (string?)line.GetValue(PathProperty);
    public static void SetPath(TextBlock line, string? path) => line.SetValue(PathProperty, path);

    static void Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBlock line) return;
        Follow(line);
        Fit(line);
    }

    /// <summary>Fits the line again whenever its panel's width changes. A line is given its path before it has a panel,
    /// so this is tried again when it is loaded.</summary>
    static void Follow(TextBlock line)
    {
        if (line.Parent is not FrameworkElement panel || ReferenceEquals(line.GetValue(PanelProperty), panel)) return;
        line.SetValue(PanelProperty, panel);
        panel.SizeChanged += (_, e) => { if (e.WidthChanged) Fit(line); };
    }

    static void Fit(TextBlock line)
    {
        string path = GetPath(line) ?? "";
        double width = (line.Parent as FrameworkElement)?.ActualWidth - line.Margin.Left - line.Margin.Right ?? 0;
        string text = path;
        // Before the first layout there is no width to fit to: the whole path stands until there is.
        if (width > 0 && Width(line, path) > width)
        {
            // The longest end of the path that fits behind the dots.
            int low = 0, high = path.Length - 1;
            while (low < high)
            {
                int kept = (low + high + 1) / 2;
                if (Width(line, "…" + path[^kept..]) <= width) low = kept; else high = kept - 1;
            }
            text = "…" + path[^low..];
        }
        if (line.Text != text) line.Text = text;
    }

    static double Width(TextBlock line, string text) =>
        new FormattedText(text, CultureInfo.CurrentUICulture, line.FlowDirection, new Typeface(line.FontFamily, line.FontStyle, line.FontWeight, line.FontStretch),
            line.FontSize, Brushes.Black, VisualTreeHelper.GetDpi(line).PixelsPerDip).WidthIncludingTrailingWhitespace;
}
