using System.Collections.ObjectModel;
using System.Reflection;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shell;
using Launcher.Core;
using Launcher.Desktop;

static class Program
{
    static int passed;
    static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        passed++;
    }
    static T Element<T>(MainWindow window, string name) where T : FrameworkElement => (T)window.FindName(name);
    static void Render(MainWindow window, RuntimeSnapshot state)
    {
        typeof(MainWindow).GetField("snapshot", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, state);
        typeof(MainWindow).GetMethod("Render", BindingFlags.Instance | BindingFlags.NonPublic, null, [typeof(RuntimeSnapshot)], null)!.Invoke(window, [state]);
    }
    static void Layout(MainWindow window, double width, double height)
    {
        // Exercise the real XAML without showing a window or starting timers, lookup, Git or runtime actions.
        var root = (FrameworkElement)window.Content;
        root.Measure(new Size(width, height));
        root.Arrange(new Rect(0, 0, width, height));
        root.UpdateLayout();
    }
    static double Luminance(Color c)
    {
        static double Linear(byte b) { double v = b / 255d; return v <= .04045 ? v / 12.92 : Math.Pow((v + .055) / 1.055, 2.4); }
        return .2126 * Linear(c.R) + .7152 * Linear(c.G) + .0722 * Linear(c.B);
    }

    static void Language(MainWindow window, string language)
    {
        var field = typeof(MainWindow).GetField("settings", BindingFlags.Instance | BindingFlags.NonPublic)!;
        field.SetValue(window, ((LauncherSettings)field.GetValue(window)!) with { Language = language });
        typeof(MainWindow).GetMethod("ApplyLanguage", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
        window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.DataBind);
    }
    static double Contrast(Brush text, Brush background)
    {
        double a = Luminance(((SolidColorBrush)text).Color), b = Luminance(((SolidColorBrush)background).Color);
        return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
    }

    [STAThread]
    static void Main()
    {
        var app = new App();
        app.InitializeComponent();
        var window = new MainWindow();
        Language(window, "en");
        var pages = Element<PageHost>(window, "Pages");
        var nav = Element<ListBox>(window, "Navigation");
        static nint Packed(short x, short y) => unchecked((nint)((ushort)x | ((uint)(ushort)y << 16)));
        Check(SnapCaption.ScreenPoint(Packed(-1920, -200)) == new Point(-1920, -200), "Native caption coordinates must preserve negative monitor origins.");
        Check(SnapCaption.ScreenPoint(Packed(1800, 500)) == new Point(1800, 500), "Native caption coordinates must decode positive screen positions.");
        var captionBounds = new Rect(-100, -40, 46, 32);
        Check(SnapCaption.Contains(captionBounds, new Point(-99, -39)), "Native maximize hit must work above/left of the primary monitor.");
        Check(!SnapCaption.Contains(captionBounds, new Point(-54, -20)), "Right edge belongs to the neighboring close control.");
        Check(!SnapCaption.Contains(captionBounds, new Point(-80, -8)), "Bottom edge must remain ordinary client input.");
        Check(!SnapCaption.Contains(Rect.Empty, new Point()), "Unarranged caption must not claim native hits.");
        Check(SnapCaption.Contains(new Rect(1500, 9, 69, 39), new Point(1520, 25)), "Physical caption bounds must accept scaled monitor coordinates.");
        var chrome = WindowChrome.GetWindowChrome(window);
        Check(window.WindowStyle == WindowStyle.None && chrome is not null && chrome.CaptionHeight == 32 && chrome.ResizeBorderThickness.Left > 0, "Custom frame must retain native caption and resize regions.");
        Check(chrome!.GlassFrameThickness == new Thickness(0) && !chrome.UseAeroCaptionButtons, "Native caption visuals must be replaced.");
        foreach (string name in new[] { "MinimizeWindowButton", "MaximizeWindowButton", "CloseWindowButton" })
        {
            var button = Element<Button>(window, name);
            Check(button.Command is not null && AutomationProperties.GetName(button).Contains("Waypoint"), "Caption controls need routed commands and accessible names.");
        }
        Check(pages.SelectedIndex == 0 && nav.SelectedIndex == 0, "Startup must select Play, not an empty page.");
        Check(nav.Items.Count == 8 && pages.Items.Count == 8, "Every page must be reachable from navigation.");
        Check(UIElementAutomationPeer.CreatePeerForElement(pages)!.GetAutomationControlType() == AutomationControlType.Pane, "Content-only page host must expose a pane, not hidden tab headers.");
        for (int i = 0; i < 8; i++)
        {
            nav.SetCurrentValue(System.Windows.Controls.Primitives.Selector.SelectedIndexProperty, i);
            window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.DataBind);
            Check(pages.SelectedIndex == i, "Navigation must select the matching page.");
            pages.SelectedIndex = (i + 1) % 8;
            window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.DataBind);
            Check(nav.SelectedIndex == pages.SelectedIndex, "Programmatic navigation must update the sidebar.");
        }
        Check(window.InputBindings.Count == 12, "Page, search, refresh and cycle shortcuts must be registered.");
        Check(Element<CheckBox>(window, "ModsAutoBackupBox").IsChecked == SettingsStore.Load().AutomaticBackups, "Backup preference must remain the stored choice.");
        var empty = new RuntimeSnapshot(Path.Combine(Path.GetTempPath(), "Waypoint-UI-Checks-no-install"), "UI fixture", "", "", "main", "",
            [new("Prepared client", false, "Missing")], [new("Gateway", "Stopped", 0, "", "Stopped", [6112], 0, 0, "")], []);
        Render(window, empty);
        Check(Element<TextBlock>(window, "ReadinessTitle").Text == "Finish setup to play", $"Readiness must describe the actual state: {Element<TextBlock>(window, "ReadinessTitle").Text} ({L.Current.Language}, {app.Windows.Count} windows).");
        Check(!Element<Button>(window, "PlayButton").IsEnabled && !Element<Button>(window, "StopServersButton").IsEnabled, "Unavailable runtime actions must be disabled.");
        Check(!Element<Button>(window, "ReloadModButton").IsEnabled, "Unsupported/empty mod inventory must not offer reload.");
        var ready = empty with { Requirements = [new("Prepared client", true, "Ready")] };
        Render(window, ready);
        Check(Element<Button>(window, "PlayButton").IsEnabled && Element<Button>(window, "StartServersButton").IsEnabled, "Prepared stopped workspace must offer startup.");
        var running = ready with { Services = [new("Gateway", "Ready", 123, "", "Ready", [6112], 12, 1, "1m"), new("Game", "Running", 456, "", "In game", [], 12, 1, "1m")] };
        Render(window, running);
        Check(!Element<Button>(window, "PlayButton").IsEnabled && !Element<Button>(window, "StopServersButton").IsEnabled && Element<Button>(window, "CloseGameButton").IsEnabled, "Running game must offer close and explain unavailable stop.");
        Render(window, ready);
        var services = Element<ItemsControl>(window, "ServiceList");
        object service = services.Items[0];
        Render(window, ready with { Services = [ready.Services[0] with { MemoryMb = 25 }] });
        Check(ReferenceEquals(service, services.Items[0]), "Polling metric changes must retain service containers.");
        var modSearch = Element<TextBox>(window, "ModSearch");
        modSearch.Text = "does-not-exist";
        Check(Element<TextBlock>(window, "ModsEmpty").Text.Contains("Clear"), "No-match mod search must provide a recovery action.");
        Element<TextBox>(window, "PrNumberBox").Text = "not-a-number";
        typeof(MainWindow).GetMethod("SwitchPrNumber_Click", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [window, new RoutedEventArgs()]);
        Check(Element<TextBlock>(window, "PrValidation").Visibility == Visibility.Visible && Element<TextBlock>(window, "PrValidation").Text.Contains("401"), "Invalid PR input must show an inline error with a valid example.");
        var items = new ObservableCollection<NavigationPage> { new("A", ""), new("B", "") };
        int changes = 0; items.CollectionChanged += (_, _) => changes++;
        var first = items[0];
        UiCollection.Reconcile(items, new[] { new NavigationPage("A", ""), new("B", "") }, p => p.Label);
        Check(changes == 0 && ReferenceEquals(first, items[0]), "Unchanged polling results must emit no collection resets or replacements.");
        UiCollection.Reconcile(items, new[] { new NavigationPage("B", ""), new("C", "") }, p => p.Label);
        Check(items.Select(p => p.Label).SequenceEqual(["B", "C"]), "Reconciliation must handle move, add and delete.");
        foreach (double scale in new[] { 1d, 1.25, 1.5, 2 })
        foreach (var size in new[] { new Size(960, 730), new Size(1006, 691), new Size(1226, 861) })
        {
            // DPI equivalent DIPs: more physical pixels, same usable logical workspace. Actual monitor changes are a separate manual check.
            ((FrameworkElement)window.Content).LayoutTransform = new ScaleTransform(scale, scale);
            foreach (int page in new[] { 0, 3, 4, 5, 6, 7 })
            {
                pages.SelectedIndex = page;
                Layout(window, size.Width * scale, size.Height * scale);
                Check(pages.ActualWidth > 650 && pages.ActualHeight > 400, $"Page {page} must retain usable content at {scale:P0}.");
                if (page == 3)
                {
                    Check(Element<ListBox>(window, "ModsList").ActualHeight >= 150, "Mod list must retain usable height.");
                    var action = Element<Button>(window, "ToggleModButton");
                    Check(action.ActualWidth + action.Margin.Left + action.Margin.Right >= action.DesiredSize.Width - .5, "Mod action must not be clipped.");
                }
                if (page == 4) Check(Element<ListBox>(window, "BackupsList").ActualHeight >= 200, "Backup list must use available height.");
            }
        }
        ((FrameworkElement)window.Content).LayoutTransform = Transform.Identity;
        pages.SelectedIndex = 0;
        Layout(window, 1006, 691);
        Check(Contrast(Element<TextBlock>(window, "ReadinessTitle").Foreground, (Brush)app.Resources["Surface"]) >= 4.5, "Page text must retain readable contrast through the tab content host.");
        foreach (string text in new[] { "Text", "Muted", "BadgeText" })
            Check(Contrast((Brush)app.Resources[text], (Brush)app.Resources[text == "BadgeText" ? "BadgeSurface" : "Surface"]) >= 4.5, $"{text} contrast must be at least 4.5:1.");
        app.ApplyContrast(true);
        Check(ReferenceEquals(app.Resources["Text"], SystemColors.WindowTextBrush) && ReferenceEquals(app.Resources["Background"], SystemColors.WindowBrush), "High Contrast must use system colors.");
        app.ApplyContrast(false);
        Check(((SolidColorBrush)app.Resources["Background"]).Color == Color.FromRgb(16, 21, 30), "Leaving High Contrast must restore the dark palette.");
        Check(AutomationProperties.GetName(modSearch) == "Search mods", "Search must have an accessible name.");
        Language(window, "ru");
        Render(window, ready);
        Check(Element<TextBlock>(window, "ReadinessTitle").Text == "Готово к игре", "Russian readiness must reflect current runtime state.");
        Check(AutomationProperties.GetName(modSearch) == "Поиск модов", "Accessible names must switch language live.");
        Check(((NavigationPage)nav.Items[7]).DisplayLabel == "Настройки", "Navigation must switch language without changing canonical page identities.");
        var translatedRow = (ServicePresentation)services.Items[0];
        Check(translatedRow.Value.Name == "Gateway" && translatedRow.Value.DisplayStatus == "Остановлен" && translatedRow.Value.DisplayAction == "Запустить", "Russian service presentation must preserve canonical action routing.");
        Check(Element<ListBox>(window, "LanguageBox").SelectedIndex == 1, "Selected language must remain discoverable in Settings.");
        foreach (double scale in new[] { 1d, 1.25, 1.5, 2d })
        foreach (var size in new[] { new Size(960, 730), new Size(1226, 861) })
        {
            ((FrameworkElement)window.Content).LayoutTransform = new ScaleTransform(scale, scale);
            for (int page = 0; page < 8; page++)
            {
                pages.SelectedIndex = page;
                Layout(window, size.Width * scale, size.Height * scale);
                Check(pages.ActualWidth > 650 && pages.ActualHeight > 400, $"Russian page {page} must retain usable space at {scale:P0}.");
            }
        }
        ((FrameworkElement)window.Content).LayoutTransform = Transform.Identity;
        new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();
        var dialog = new ConfirmationDialog(window, L.Text("Delete only the backup from {0:g}?\n\nCurrent saves are unchanged. This backup will no longer be available to restore.", DateTime.Now), L.Text("Delete selected backup"));
        var buttons = ((System.Windows.Controls.Panel)dialog.Content).Children.OfType<WrapPanel>().Single().Children.OfType<Button>().ToArray();
        Check(buttons.Any(b => b.IsCancel && b.IsDefault && (string)b.Content == "Отмена") && buttons.Any(b => !b.IsDefault && (string)b.Content == "Продолжить"), "Localized destructive confirmations must default to Cancel.");
        dialog.Close();
        Language(window, "en");
        Render(window, ready);
        Check(AutomationProperties.GetName(modSearch) == "Search mods" && translatedRow.Value.DisplayAction == "Start", "Switching back must restore English without changing service identity.");
        window.Close();
        Console.WriteLine($"PASS: {passed} desktop checks (no runtime actions or saved settings changed).");
    }
}
