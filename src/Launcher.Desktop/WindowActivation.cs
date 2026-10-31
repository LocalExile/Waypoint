using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Launcher.Desktop;

public static class WindowActivation
{
    public static void Allow(int processId) => AllowSetForegroundWindow((uint)processId);
    public static void Focus(Window window) => Focus(new WindowInteropHelper(window).Handle);
    public static void FocusProcess(int processId)
    {
        try { using var process = Process.GetProcessById(processId); Allow(processId); Focus(process.MainWindowHandle); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }
    static void Focus(nint hwnd)
    {
        if (hwnd == 0) return;
        // SW_RESTORE preserves a maximized window's previous state when bringing it back from minimization.
        if (IsIconic(hwnd)) ShowWindow(hwnd, 9);
        nint popup = GetLastActivePopup(hwnd);
        if (popup != 0 && IsWindowVisible(popup) && IsWindowEnabled(popup)) hwnd = popup;
        if (!SetForegroundWindow(hwnd))
        {
            var info = new FlashInfo { Size = (uint)Marshal.SizeOf<FlashInfo>(), Window = hwnd, Flags = 2, Count = 3 };
            FlashWindowEx(ref info); // Respect Windows foreground restrictions; do not toggle Topmost or steal input.
        }
    }
    [StructLayout(LayoutKind.Sequential)] struct FlashInfo { public uint Size; public nint Window; public uint Flags, Count, Timeout; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool AllowSetForegroundWindow(uint processId);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool ShowWindow(nint hwnd, int command);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] static extern nint GetLastActivePopup(nint hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool IsWindowEnabled(nint hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool FlashWindowEx(ref FlashInfo info);
}
