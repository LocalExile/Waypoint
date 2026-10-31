using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Launcher.Desktop;

static class NativeTrayChecks
{
    static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
    public static void Run(MainWindow workspace, Action<bool, string> check)
    {
        var owner = new DialogWindow { Title = "Waypoint native tray fixture", Width = 460, Height = 240,
            Content = new TextBlock { Text = "Disposable tray callback checks. No runtime actions.", Margin = new Thickness(24) } };
        owner.Show(); Pump(); owner.Hide();
        int opens = 0, exits = 0, failures = 0;
        var tray = new TrayIcon(owner, () => opens++, () => exits++, () => failures++);
        try
        {
            tray.Show(); tray.Show(); tray.RefreshLanguage();
            var hwnd = new WindowInteropHelper(owner).Handle;
            const int callback = 0x8000 + 73;
            // Feed native callback messages through the real HWND hook and dispatcher. No simulated game/runtime work.
            PostMessage(hwnd, callback, 0, 0x400); Pump();
            check(opens == 1 && exits == 0, "Native mouse selection dispatches Open exactly once.");
            PostMessage(hwnd, callback, 0, 0x401); Pump();
            check(opens == 2, "Native keyboard selection restores through the same Open action.");
            PostMessage(hwnd, callback, unchecked((nint)0xffffffff), 0x7b); Pump();
            var menu = (ContextMenu)typeof(TrayIcon).GetField("menu", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(tray)!;
            check(menu.IsOpen && menu.Items.Count == 2 && ((MenuItem)menu.Items[0]).IsKeyboardFocused,
                $"Native context-menu callback opens the WPF menu with keyboard focus on Open (open={menu.IsOpen}, items={menu.Items.Count}, focus={((MenuItem)menu.Items[0]).IsKeyboardFocused}).");
            ((MenuItem)menu.Items[1]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Pump();
            check(exits == 1 && !menu.IsOpen, "Tray Exit dismisses the menu before dispatching an explicit exit.");
            tray.Hide(); PostMessage(hwnd, callback, 0, 0x400); Pump();
            check(opens == 2 && failures == 0, "A removed tray icon ignores delayed callbacks.");
            tray.Show(); tray.Dispose(); tray.Dispose();
            check(!menu.IsOpen, "Removing and disposing the native icon is safe and closes its popup.");
        }
        finally { tray.Dispose(); owner.Close(); }
    }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool PostMessage(nint hwnd, int message, nint wParam, nint lParam);
}
