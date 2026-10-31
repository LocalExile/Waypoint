using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using Launcher.Core;
using Launcher.Desktop;

static class DialogFrameChecks
{
    public static void Run(App app, MainWindow owner, Action<bool, string> check)
    {
        var appearance = app.Appearance;
        string language = L.Current.Language;
        var ownerLanguage = owner.Language;
        Func<DialogWindow>[] create = [
            () => new AppearanceDialog(owner, app.Appearance, _ => throw new Exception("Closing must never apply preferences")),
            () => new ColorPickerDialog(owner, L.Text("Accent"), Colors.CornflowerBlue),
            () => new ConfirmationDialog(owner, new string('x', 2000), L.Text("Delete selected backup")),
            () => new SetupInstructionsDialog(owner, new SetupStep("Git", false, "", "Guide", L.Text("Open setup guide"), new string('x', 2000)), "test checkout"),
            () => new CreateAccountDialog(owner, "test database; no account creation is invoked"),
            () => new DiagnosticPreviewDialog(owner, _ => "test report; clipboard is not used"),
            () => new StartupNoticeDialog(owner, L.Text("Waypoint is already open"), new(42, "0.6.2", "fixture", @"C:\Fixture\Waypoint.exe")),
            .. new[] { "Create", "Duplicate", "Rename", "Password", "Delete" }.Select(action => (Func<DialogWindow>)(() =>
                new AccountActionDialog(owner, action, action == "Create" ? null : new("fixture", "Example", "", "revision", [], 0), [], true)))
        ];
        try
        {
            foreach (var scenario in new[] { ("en", new AppearanceSettings(), false),
                ("ru", new AppearanceSettings(Mode: "Light", Font: "OpenDyslexic", TextScale: 150), false),
                ("en", new AppearanceSettings(TextScale: 125, StrongFocus: true), true) })
            {
                L.Current.SetLanguage(scenario.Item1);
                owner.Language = System.Windows.Markup.XmlLanguage.GetLanguage(scenario.Item1);
                app.ApplyContrast(scenario.Item3); app.ApplyAppearance(scenario.Item2);
                foreach (var factory in create)
                {
                    var dialog = factory();
                    bool closed = false;
                    dialog.Closed += (_, _) => closed = true;
                    dialog.ApplyTemplate();
                    nint hwnd = new WindowInteropHelper(dialog).EnsureHandle();
                    var root = (FrameworkElement)VisualTreeHelper.GetChild(dialog, 0);
                    double width = dialog.MinWidth, height = Math.Max(dialog.MinHeight, 540);
                    root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout();
                    var caption = (FrameworkElement)dialog.Template.FindName("DialogCaption", dialog);
                    var title = (TextBlock)dialog.Template.FindName("DialogTitle", dialog);
                    var close = (Button)dialog.Template.FindName("DialogCloseButton", dialog);
                    var chrome = WindowChrome.GetWindowChrome(dialog);
                    bool actionsVisible = true;
                    if (dialog is ConfirmationDialog or SetupInstructionsDialog or CreateAccountDialog or AccountActionDialog or StartupNoticeDialog)
                    {
                        var actions = (FrameworkElement)((DockPanel)dialog.Content).Children[0];
                        double bottom = actions.TranslatePoint(new Point(0, actions.ActualHeight), root).Y;
                        actionsVisible = actions.ActualHeight > 0 && bottom <= height && bottom > caption.ActualHeight;
                    }
                    string label = dialog.GetType().Name + "/" + scenario.Item1 + (scenario.Item3 ? "/HighContrast" : "");
                    check(dialog.WindowStyle == WindowStyle.None && !dialog.AllowsTransparency && !dialog.ShowInTaskbar && dialog.Owner == owner,
                        label + " uses a themed owned dialog without another taskbar entry or layered-window rendering.");
                    check((GetWindowLongPtr(hwnd, -16).ToInt64() & 0x30000L) == 0 && dialog.ResizeMode == ResizeMode.CanResize,
                        label + " retains resizing without advertising modal minimize/maximize commands.");
                    check(chrome is { UseAeroCaptionButtons: false } && chrome.ResizeBorderThickness.Left == 6 &&
                        Math.Abs(chrome.CaptionHeight - caption.ActualHeight - 1) < .1,
                        label + " native drag region follows the actual scaled caption height.");
                    check(title.Text == dialog.Title && title.TextTrimming == TextTrimming.CharacterEllipsis && close.ActualWidth >= 46 && close.ActualHeight >= 32 && actionsVisible,
                        label + " retains its full accessible title, usable close target and visible actions at minimum width.");
                    check(AutomationProperties.GetName(close) == L.Text("Close {0}", dialog.Title) && close.Focusable && WindowChrome.GetIsHitTestVisibleInChrome(close)
                        && ((RoutedCommand)close.Command).CanExecute(null, close),
                        label + " exposes a localized keyboard-accessible close button in the caption.");
                    check(((SolidColorBrush)root.GetValue(Control.BackgroundProperty)).Color == ((SolidColorBrush)app.FindResource("Background")).Color,
                        label + " frame follows the selected palette.");
                    ((RoutedCommand)close.Command).Execute(null, close);
                    dialog.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    check(closed && dialog.DialogResult != true, label + " title-bar Close cancels without confirming or saving.");
                }
            }
        }
        finally { L.Current.SetLanguage(language); owner.Language = ownerLanguage; app.ApplyContrast(false); app.ApplyAppearance(appearance); }
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    static extern nint GetWindowLongPtr(nint hwnd, int index);
}
