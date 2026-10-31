using System.Reflection;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Launcher.Core;
using Launcher.Desktop;

static class ServiceIndicatorChecks
{
    static ServiceStatus Service(string name, string state, int pid = 0, int watcher = 0) => new(name, state, pid, "", "Fixture state", [], 0, 0, "", watcher);
    static SolidColorBrush Brush(ServiceStatusIndicator indicator) => (SolidColorBrush)((Ellipse)((Grid)((DockPanel)indicator.Content).Children[0]).Children[0]).Stroke;
    public static void Run(App app, MainWindow owner, Action<bool, string> check)
    {
        var original = app.Appearance;
        string language = L.Current.Language;
        var strip = (System.Windows.Controls.Primitives.UniformGrid)owner.FindName("ServiceIndicatorStrip");
        var indicators = strip.Children.OfType<ServiceStatusIndicator>().ToArray();
        var page = (PageHost)owner.FindName("Pages"); int selected = page.SelectedIndex;
        try
        {
            check(indicators.Select(i => i.ServiceName).SequenceEqual(new[] { "Patch", "Gateway", "Auth", "Game" }), "The persistent status strip names all four runtime components in a stable order.");
            check(ServiceLightState.From(null) == ServiceLight.Unknown && ServiceLightState.From(Service("Patch", "Stopped")) == ServiceLight.Off, "Unverified inventory stays neutral; only a verified stopped service is red/off.");
            check(ServiceLightState.From(Service("Patch", "Ready", 1)) == ServiceLight.On && ServiceLightState.From(Service("Game", "Running", 2)) == ServiceLight.On, "Owned ready servers and the running game are green/on.");
            check(ServiceLightState.From(Service("Gateway", "Starting", watcher: 3)) == ServiceLight.Starting, "A surviving Gateway watcher stays amber during a child restart gap.");
            check(ServiceLightState.From(Service("Gateway", "Ready")) != ServiceLight.On && ServiceLightState.From(Service("Game", "Running")) != ServiceLight.On, "A state string without a verified process cannot produce a green light.");
            check(ServiceLightState.From(Service("Auth", "Failed")) == ServiceLight.Attention && ServiceLightState.From(Service("Game", "future state")) == ServiceLight.Unknown, "Errors request attention and unfamiliar states never masquerade as stopped or ready.");
            indicators[0].Update(Service("Patch", "Stopped")); indicators[1].Update(Service("Gateway", "Ready", 1));
            indicators[2].Update(Service("Auth", "Starting", 2)); indicators[3].Update(null);
            foreach (var scheme in ThemePalette.Schemes)
            foreach (string mode in new[] { "Dark", "Light", "System" })
            foreach (string vision in new[] { "Standard", "RedGreen", "BlueYellow", "Grayscale" })
            {
                app.ApplyAppearance(new(Mode: mode, Scheme: scheme.Id, Vision: vision));
                string[] roles = ["Error", "Success", "Warning", "Muted"];
                bool correct = true;
                for (int i = 0; i < 4; i++)
                    correct &= Brush(indicators[i]).Color == ((SolidColorBrush)app.Resources[roles[i]]).Color &&
                        ThemePalette.Contrast(Brush(indicators[i]).Color, ((SolidColorBrush)app.Resources["Background"]).Color) >= 3;
                check(correct, $"Service lights update live for {scheme.Id}/{mode}/{vision}, using the actual semantic brushes with visible contrast.");
            }
            app.ApplyAppearance(new(Scheme: "custom", ProtectContrast: false, DarkColors: new() { ["Success"] = "#12AB34", ["Error"] = "#EF1234" }));
            check(Brush(indicators[1]).Color == ThemePalette.Parse("#12AB34") && Brush(indicators[0]).Color == ThemePalette.Parse("#EF1234"), "Existing success/error color-wheel roles also customize the persistent lights.");
            app.ApplyAppearance(new(ReduceMotion: true));
            check(indicators.Take(3).All(i => i.HasGlow) && !indicators[3].HasGlow, "The restrained glow is static, supports reduced motion and does not imply unknown status is active.");
            app.ApplyAppearance(new(IncreaseContrast: true));
            check(indicators.All(i => !i.HasGlow), "Increased contrast uses crisp status symbols without a glow halo.");
            app.ApplyContrast(true);
            check(indicators.All(i => !i.HasGlow && Brush(i).Color == SystemColors.WindowTextColor), "High Contrast gives every light a system-colored outline and disables glow.");
            app.ApplyContrast(false);
            foreach (string locale in new[] { "en", "ru" })
            foreach (var reading in new[] { new AppearanceSettings(), new AppearanceSettings(Mode: "Light", Font: "OpenDyslexic", TextScale: 150, StrongFocus: true) })
            {
                L.Current.SetLanguage(locale); app.ApplyAppearance(reading);
                foreach (var light in indicators) light.RefreshLanguage();
                foreach (int index in Enumerable.Range(0, page.Items.Count))
                {
                    page.SelectedIndex = index;
                    var root = (FrameworkElement)owner.Content;
                    root.Measure(new Size(960, 730)); root.Arrange(new Rect(0, 0, 960, 730)); root.UpdateLayout();
                    bool fits = indicators.All(light => light.Visibility == Visibility.Visible && light.ActualWidth > 100 && light.ActualHeight > 30 &&
                        ((FrameworkElement)light.Content).DesiredSize.Width + light.Padding.Left + light.Padding.Right + 8 <= light.ActualWidth + 1);
                    check(fits && strip.TranslatePoint(new Point(0, strip.ActualHeight), root).Y <= root.ActualHeight,
                        $"All four status labels remain visible on page {index}, {locale}, {reading.Font}/{reading.TextScale}% at minimum window size.");
                }
            }
            check(new ButtonAutomationPeer(indicators[0]).GetName().Contains(L.Text("Stopped")) && indicators[0].StatusText.Length > 0, "The current state is available as both visible text and an accessible button name.");
            indicators[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check(page.SelectedIndex == 0, "A service indicator opens the existing Play/details page without starting or stopping anything.");
            typeof(MainWindow).GetMethod("UpdateServiceIndicators", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(owner, [null]);
            check(indicators.All(i => i.Light == ServiceLight.Unknown && !i.HasGlow), "Failed checks or checkout changes clear old green lights instead of leaving stale success visible.");
        }
        finally { page.SelectedIndex = selected; L.Current.SetLanguage(language); app.ApplyContrast(false); app.ApplyAppearance(original); }
    }
}
