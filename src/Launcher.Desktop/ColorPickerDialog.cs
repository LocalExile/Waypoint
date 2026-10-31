using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Launcher.Core;

namespace Launcher.Desktop;

/// <summary>Pointer wheel with equivalent standard, keyboard-accessible HSV sliders in the dialog.</summary>
public sealed class ColorWheel : FrameworkElement
{
    static readonly BitmapSource WheelImage = CreateWheel();
    public double Hue { get; set; }
    public double Saturation { get; set; }
    public double Brightness { get; set; } = 1;
    public event Action<double, double>? ColorSelected;
    public ColorWheel()
    {
        Width = Height = 240; Cursor = Cursors.Cross;
        AutomationProperties.SetName(this, L.Text("Color wheel; keyboard alternatives are below."));
        MouseLeftButtonDown += (_, e) => { CaptureMouse(); Pick(e.GetPosition(this)); e.Handled = true; };
        MouseMove += (_, e) => { if (IsMouseCaptured && e.LeftButton == MouseButtonState.Pressed) Pick(e.GetPosition(this)); };
        MouseLeftButtonUp += (_, _) => ReleaseMouseCapture();
    }
    protected override AutomationPeer OnCreateAutomationPeer() => new WheelPeer(this);
    sealed class WheelPeer(ColorWheel owner) : FrameworkElementAutomationPeer(owner)
    { protected override string GetClassNameCore() => "ColorWheel"; protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Image; }
    void Pick(Point point)
    {
        double x = (point.X - ActualWidth / 2) / (ActualWidth / 2), y = (ActualHeight / 2 - point.Y) / (ActualHeight / 2);
        Hue = (Math.Atan2(y, x) * 180 / Math.PI + 360) % 360; Saturation = Math.Clamp(Math.Sqrt(x * x + y * y), 0, 1);
        InvalidateVisual(); ColorSelected?.Invoke(Hue, Saturation);
    }
    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawImage(WheelImage, new Rect(RenderSize));
        dc.DrawEllipse(new SolidColorBrush(Color.FromArgb((byte)(255 * (1 - Brightness)), 0, 0, 0)), null, new Point(ActualWidth / 2, ActualHeight / 2), ActualWidth / 2, ActualHeight / 2);
        var marker = new Point(ActualWidth / 2 + Math.Cos(Hue * Math.PI / 180) * Saturation * (ActualWidth / 2 - 4), ActualHeight / 2 - Math.Sin(Hue * Math.PI / 180) * Saturation * (ActualHeight / 2 - 4));
        dc.DrawEllipse(null, new Pen(Brushes.Black, 4), marker, 6, 6); dc.DrawEllipse(null, new Pen(Brushes.White, 2), marker, 6, 6);
    }
    static BitmapSource CreateWheel()
    {
        const int n = 256; byte[] pixels = new byte[n * n * 4];
        for (int y = 0; y < n; y++) for (int x = 0; x < n; x++)
        {
            double dx = (x - (n - 1) / 2d) / ((n - 1) / 2d), dy = ((n - 1) / 2d - y) / ((n - 1) / 2d), s = Math.Sqrt(dx * dx + dy * dy);
            if (s > 1) continue; var c = FromHsv((Math.Atan2(dy, dx) * 180 / Math.PI + 360) % 360, s, 1); int i = (y * n + x) * 4;
            pixels[i] = c.B; pixels[i + 1] = c.G; pixels[i + 2] = c.R; pixels[i + 3] = 255;
        }
        var bitmap = BitmapSource.Create(n, n, 96, 96, PixelFormats.Bgra32, null, pixels, n * 4); bitmap.Freeze(); return bitmap;
    }
    public static Color FromHsv(double h, double s, double v)
    {
        h = (h % 360 + 360) % 360; s = Math.Clamp(s, 0, 1); v = Math.Clamp(v, 0, 1);
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        (double r, double g, double b) = h switch { < 60 => (c, x, 0d), < 120 => (x, c, 0d), < 180 => (0d, c, x), < 240 => (0d, x, c), < 300 => (x, 0d, c), _ => (c, 0d, x) };
        return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }
    public static (double H, double S, double V) ToHsv(Color c)
    {
        double r = c.R / 255d, g = c.G / 255d, b = c.B / 255d, max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        double h = d == 0 ? 0 : max == r ? 60 * ((g - b) / d % 6) : max == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
        return ((h + 360) % 360, max == 0 ? 0 : d / max, max);
    }
}

public sealed class ColorPickerDialog : Window
{
    readonly ColorWheel wheel = new();
    readonly Slider hue = new() { Minimum = 0, Maximum = 360, SmallChange = 1, LargeChange = 15 };
    readonly Slider saturation = new() { Minimum = 0, Maximum = 100, SmallChange = 1, LargeChange = 10 };
    readonly Slider brightness = new() { Minimum = 0, Maximum = 100, SmallChange = 1, LargeChange = 10 };
    readonly TextBox hex = new() { MaxLength = 7, MinWidth = 125 };
    readonly Border swatch = new() { Height = 40, CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 10, 0, 12) };
    readonly TextBlock error = new();
    readonly Action<Color>? preview;
    bool updating;
    public Color SelectedColor { get; private set; }
    public ColorPickerDialog(Window owner, string role, Color color, Action<Color>? preview = null)
    {
        this.preview = preview; DialogLayout.Configure(this, owner, L.Text("Choose color: {0}", role), 560);
        MinWidth = 420; MinHeight = 480; MaxHeight = Math.Min(800, SystemParameters.WorkArea.Height - 40); Height = 720; SizeToContent = SizeToContent.Manual;
        var root = new DockPanel { Margin = new Thickness(20) };
        var buttons = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
        var footer = new StackPanel(); DockPanel.SetDock(footer, Dock.Bottom);
        error.SetResourceReference(TextBlock.ForegroundProperty, "Error"); AutomationProperties.SetLiveSetting(error, AutomationLiveSetting.Polite);
        footer.Children.Add(error); footer.Children.Add(buttons); root.Children.Add(footer);
        var apply = new Button { Content = L.Text("Use color"), IsDefault = true };
        apply.Click += (_, _) => { if (!ThemePalette.TryColor(hex.Text, out var c)) { error.Text = L.Text("Enter a color as #RRGGBB, for example #2584D7."); AutomationProperties.SetHelpText(hex, error.Text); hex.Focus(); UIElementAutomationPeer.FromElement(error)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged); return; } SelectedColor = c; DialogResult = true; };
        buttons.Children.Add(apply); buttons.Children.Add(new Button { Content = L.Text("Cancel"), IsCancel = true });
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = role, FontSize = ThemeResources.Font(21), Margin = new Thickness(0, 0, 0, 10) });
        panel.Children.Add(wheel);
        AddSlider(panel, L.Text("Hue"), hue); AddSlider(panel, L.Text("Saturation"), saturation); AddSlider(panel, L.Text("Brightness"), brightness);
        panel.Children.Add(new TextBlock { Text = L.Text("Hex color (#RRGGBB)") }); AutomationProperties.SetName(hex, L.Text("Hex color (#RRGGBB)"));
        panel.Children.Add(hex); panel.Children.Add(swatch);
        panel.Children.Add(new TextBlock { Text = L.Text("Use the wheel, the arrow keys on a slider, or an exact hex color. Escape cancels this color change.") });
        root.Children.Add(new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }); Content = root;
        wheel.ColorSelected += (h, s) => { updating = true; hue.Value = h; saturation.Value = s * 100; updating = false; FromSliders(); };
        foreach (var slider in new[] { hue, saturation, brightness }) slider.ValueChanged += (_, _) => { if (!updating) FromSliders(); };
        hex.TextChanged += (_, _) => { if (!updating && ThemePalette.TryColor(hex.Text, out var c)) SetColor(c); };
        SetColor(color);
    }
    static void AddSlider(Panel panel, string name, Slider slider)
    {
        panel.Children.Add(new TextBlock { Text = name, Margin = new Thickness(0, 10, 0, 4) });
        AutomationProperties.SetName(slider, name); slider.MinHeight = 30; panel.Children.Add(slider);
    }
    void FromSliders() => SetColor(ColorWheel.FromHsv(hue.Value, saturation.Value / 100, brightness.Value / 100), true);
    void SetColor(Color color, bool fromSliders = false)
    {
        updating = true; SelectedColor = color;
        if (!fromSliders) { var hsv = ColorWheel.ToHsv(color); hue.Value = hsv.H; saturation.Value = hsv.S * 100; brightness.Value = hsv.V * 100; }
        wheel.Hue = hue.Value; wheel.Saturation = saturation.Value / 100; wheel.Brightness = brightness.Value / 100; wheel.InvalidateVisual();
        hex.Text = ThemePalette.Hex(color); swatch.Background = new SolidColorBrush(color); error.Text = ""; AutomationProperties.SetHelpText(hex, ""); updating = false; preview?.Invoke(color);
    }
}
