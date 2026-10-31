using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;
using System.Runtime.InteropServices;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Shell;

namespace Waypoint;

/// <summary>The window draws its own title bar: the header is the caption, with the Windows behaviour a caption has (drag,
/// double-click, snapping, the system menu, Snap Layouts on Maximize on Windows 11).</summary>
public partial class MainWindow
{
    HwndSource? frameSource;
    bool snapHover;
    bool snapPressed;

    void InitializeWindowFrame()
    {
        CommandBindings.Add(new CommandBinding(SystemCommands.MinimizeWindowCommand, (_, _) => SystemCommands.MinimizeWindow(this)));
        CommandBindings.Add(new CommandBinding(SystemCommands.MaximizeWindowCommand, (_, _) => SystemCommands.MaximizeWindow(this)));
        CommandBindings.Add(new CommandBinding(SystemCommands.RestoreWindowCommand, (_, _) => SystemCommands.RestoreWindow(this)));
        // Goes through the existing Closing guard, including while a runtime operation is busy.
        CommandBindings.Add(new CommandBinding(SystemCommands.CloseWindowCommand, (_, _) => SystemCommands.CloseWindow(this)));
        StateChanged += (_, _) => UpdateWindowFrame();
        Theme.Changed += UpdateWindowFrame;
        DpiChanged += (_, _) => UpdateWindowFrame();
        SourceInitialized += (_, _) =>
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return;
            frameSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            frameSource?.AddHook(SnapLayoutHook);
            ToolTipService.SetIsEnabled(MaximizeWindowButton, false);
        };
        Deactivated += (_, _) => { CancelSnapPress(); SetSnapHover(false); };
        Closed += (_, _) => { CancelSnapPress(); frameSource?.RemoveHook(SnapLayoutHook); frameSource = null; };
        UpdateWindowFrame();
    }

    void UpdateWindowFrame()
    {
        SetSnapHover(false);
        CancelSnapPress();
        // The caption is the header's height, at the current text size.
        WindowChrome.GetWindowChrome(this).CaptionHeight = 56 * Theme.Zoom;
        // The text size zooms the whole window: the smallest usable window grows with it, within the screen.
        MinWidth = Math.Min(900 * Theme.Zoom, SystemParameters.WorkArea.Width);
        MinHeight = Math.Min(560 * Theme.Zoom, SystemParameters.WorkArea.Height);
        bool maximized = WindowState == WindowState.Maximized;
        MaximizeWindowButton.Content = maximized ? "\uE923" : "\uE922";
        MaximizeWindowButton.Command = maximized ? SystemCommands.RestoreWindowCommand : SystemCommands.MaximizeWindowCommand;
        MaximizeWindowButton.ToolTip = maximized ? L.Text("Restore down") : L.Text("Maximize");
        AutomationProperties.SetName(MaximizeWindowButton, maximized ? L.Text("Restore Waypoint") : L.Text("Maximize Waypoint"));
        // Maximized HWNDs extend the invisible resize frame beyond the work area. Keep the
        // client controls inside that frame, using this monitor's actual DPI rather than 8px.
        if (maximized)
        {
            const int sizeFrameX = 32, sizeFrameY = 33, paddedBorder = 92;
            var dpi = VisualTreeHelper.GetDpi(this);
            uint dpiX = (uint)Math.Round(dpi.PixelsPerInchX), dpiY = (uint)Math.Round(dpi.PixelsPerInchY);
            double x = (GetSystemMetricsForDpi(sizeFrameX, dpiX) + GetSystemMetricsForDpi(paddedBorder, dpiX)) / dpi.DpiScaleX;
            double y = (GetSystemMetricsForDpi(sizeFrameY, dpiY) + GetSystemMetricsForDpi(paddedBorder, dpiY)) / dpi.DpiScaleY;
            WindowLayout.Margin = new Thickness(x, y, x, y);
        }
        else WindowLayout.Margin = new Thickness(0);
    }

    nint SnapLayoutHook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        const int hitTest = 0x84, ncMouseMove = 0xA0, ncMouseLeave = 0x2A2, clientMouseMove = 0x200, maxButton = 9;
        const int ncButtonDown = 0xA1, ncDoubleClick = 0xA3, ncButtonUp = 0xA2, clientButtonUp = 0x202;
        if (message == hitTest && IsMaximizePoint(SnapCaption.ScreenPoint(lParam)))
        {
            handled = true;
            return maxButton;
        }
        if ((message is ncButtonDown or ncDoubleClick) && wParam == maxButton)
        {
            // Windows owns hover/Snap Layouts. Our rectangle owns click tracking: DefWindowProc
            // would track the hidden system button rectangle and paint a duplicate caption glyph.
            snapPressed = true;
            SnapCaption.SetIsPressed(MaximizeWindowButton, true);
            MaximizeWindowButton.Focus();
            SetCapture(hwnd);
            handled = true;
            return 0;
        }
        if ((message is ncButtonUp or clientButtonUp) && snapPressed)
        {
            bool activate = GetCursorPos(out var cursor) && IsMaximizePoint(new Point(cursor.X, cursor.Y));
            CancelSnapPress();
            SetSnapHover(false);
            handled = true;
            if (activate && MaximizeWindowButton.Command is { } command && command.CanExecute(null)) command.Execute(null);
            return 0;
        }
        if (message is 0x215 or 0x1F) CancelSnapPress(); // capture changed / cancel mode
        if (message == clientMouseMove && snapPressed)
        {
            bool inside = GetCursorPos(out var cursor) && IsMaximizePoint(new Point(cursor.X, cursor.Y));
            SnapCaption.SetIsPressed(MaximizeWindowButton, inside);
            SetSnapHover(inside);
            return 0;
        }
        if (message == ncMouseMove) SetSnapHover(wParam == maxButton);
        else if (message is ncMouseLeave or clientMouseMove) SetSnapHover(false);
        // Leave hover messages unhandled so Windows can show its native layout picker.
        return 0;
    }

    bool IsMaximizePoint(Point screen)
    {
        if (ResizeMode is not (ResizeMode.CanResize or ResizeMode.CanResizeWithGrip) || WindowState == WindowState.Minimized
            || !MaximizeWindowButton.IsVisible || !MaximizeWindowButton.IsEnabled || MaximizeWindowButton.ActualWidth <= 0
            || MaximizeWindowButton.ActualHeight <= 0 || PresentationSource.FromVisual(MaximizeWindowButton) is null) return false;
        // Recompute through WPF for this monitor's DPI and the current maximized/restore position.
        double top = WindowState == WindowState.Maximized ? 0 : WindowChrome.GetWindowChrome(this).ResizeBorderThickness.Top;
        var bounds = new Rect(MaximizeWindowButton.PointToScreen(new Point(0, top)),
            MaximizeWindowButton.PointToScreen(new Point(MaximizeWindowButton.ActualWidth, MaximizeWindowButton.ActualHeight)));
        return SnapCaption.Contains(bounds, screen);
    }

    void CancelSnapPress()
    {
        if (!snapPressed) return;
        snapPressed = false;
        SnapCaption.SetIsPressed(MaximizeWindowButton, false);
        if (frameSource is not null && GetCapture() == frameSource.Handle) ReleaseCapture();
    }

    void SetSnapHover(bool hovered)
    {
        if (snapHover == hovered) return;
        snapHover = hovered;
        SnapCaption.SetIsHovered(MaximizeWindowButton, hovered);
        if (hovered && frameSource is not null)
        {
            var tracking = new MouseTracking { Size = (uint)Marshal.SizeOf<MouseTracking>(), Flags = 0x12, Window = frameSource.Handle };
            TrackMouseEvent(ref tracking); // TME_LEAVE | TME_NONCLIENT
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MouseTracking { public uint Size, Flags; public nint Window; public uint HoverTime; }

    [StructLayout(LayoutKind.Sequential)]
    struct ScreenCursor { public int X, Y; }

    [DllImport("user32.dll")]
    static extern nint SetCapture(nint hwnd);
    [DllImport("user32.dll")]
    static extern nint GetCapture();
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool ReleaseCapture();
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetCursorPos(out ScreenCursor point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool TrackMouseEvent(ref MouseTracking tracking);

    [DllImport("user32.dll")]
    static extern int GetSystemMetricsForDpi(int index, uint dpi);
}
