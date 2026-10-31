using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Shell;
using Launcher.Core;

namespace Launcher.Desktop;

/// <summary>Shared native-behaving frame for app-owned modal dialogs.</summary>
public class DialogWindow : Window
{
    FrameworkElement? caption;

    public DialogWindow()
    {
        SetResourceReference(StyleProperty, "DialogWindow");
        WindowChrome.SetWindowChrome(this, new WindowChrome { CaptionHeight = 32,
            ResizeBorderThickness = new Thickness(6), GlassFrameThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0), UseAeroCaptionButtons = false });
        CommandBindings.Add(new CommandBinding(SystemCommands.CloseWindowCommand,
            (_, _) => SystemCommands.CloseWindow(this)));
    }

    public override void OnApplyTemplate()
    {
        if (caption is not null) caption.SizeChanged -= CaptionSizeChanged;
        base.OnApplyTemplate();
        caption = GetTemplateChild("DialogCaption") as FrameworkElement;
        if (GetTemplateChild("DialogCloseButton") is Button close)
        {
            string label = L.Text("Close {0}", Title);
            AutomationProperties.SetName(close, label);
            close.ToolTip = label;
        }
        if (caption is not null) caption.SizeChanged += CaptionSizeChanged;
        UpdateCaption();
    }

    void CaptionSizeChanged(object sender, SizeChangedEventArgs e) => UpdateCaption();
    void UpdateCaption()
    {
        if (caption is { ActualHeight: > 0 } && WindowChrome.GetWindowChrome(this) is { } chrome)
            chrome.CaptionHeight = caption.ActualHeight + 1; // Include the outer frame's top border.
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Modal dialogs keep native move/resize/system-menu behavior, but do not minimize
        // their blocked owner or advertise maximize/Snap commands without caption buttons.
        nint hwnd = new WindowInteropHelper(this).Handle;
        const int styleIndex = -16;
        long style = GetWindowLongPtr(hwnd, styleIndex).ToInt64();
        SetWindowLongPtr(hwnd, styleIndex, (nint)(style & ~0x30000L)); // WS_MINIMIZEBOX | WS_MAXIMIZEBOX
        SetWindowPos(hwnd, 0, 0, 0, 0, 0, 0x37); // FRAMECHANGED, NOMOVE, NOSIZE, NOZORDER, NOACTIVATE
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int width, int height, uint flags);
}
