using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using Launcher.Core;

namespace Launcher.Desktop;

/// <summary>The existing workspace HWND stays alive while hidden. No second UI framework or background process.</summary>
public sealed class TrayIcon : IDisposable
{
    const int Callback = 0x8000 + 73;
    readonly Window owner;
    readonly HwndSource source;
    readonly Action open;
    readonly Action exit;
    readonly Action unavailable;
    readonly int taskbarCreated = RegisterWindowMessage("TaskbarCreated");
    readonly nint icon;
    readonly ContextMenu menu = new();
    bool visible, version4, disposed;

    public TrayIcon(Window owner, Action open, Action exit, Action unavailable)
    {
        this.owner = owner; this.open = open; this.exit = exit; this.unavailable = unavailable;
        source = HwndSource.FromHwnd(new WindowInteropHelper(owner).EnsureHandle())!;
        icon = LoadIcon();
        menu.SetResourceReference(Control.BackgroundProperty, "Surface");
        menu.SetResourceReference(Control.ForegroundProperty, "Text");
        menu.SetResourceReference(Control.BorderBrushProperty, "Outline");
        menu.SetResourceReference(Control.FontFamilyProperty, "AppFont");
        menu.SetResourceReference(Control.FontSizeProperty, "Font14");
        menu.BorderThickness = new Thickness(1);
        menu.Placement = PlacementMode.MousePoint;
        menu.PlacementTarget = owner;
        menu.Closed += (_, _) => { ShellNotifyIcon(3, Data()); PostMessage(source.Handle, 0, 0, 0); };
        source.AddHook(Hook);
    }

    public void Show()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (visible) return;
        if (!ShellNotifyIcon(0, Data())) throw new Win32Exception(Marshal.GetLastWin32Error(), L.Text("Windows could not show the notification area icon."));
        visible = true;
        var data = Data(); data.Version = 4;
        version4 = ShellNotifyIcon(4, data);
    }

    public void Hide()
    {
        menu.IsOpen = false;
        if (visible) ShellNotifyIcon(2, Data());
        visible = false;
    }

    public void RefreshLanguage()
    {
        if (visible) ShellNotifyIcon(1, Data());
    }

    nint Hook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == taskbarCreated && visible)
        {
            visible = false;
            try { Show(); }
            catch (Win32Exception) { owner.Dispatcher.BeginInvoke(unavailable); }
        }
        if (message != Callback || !visible) return 0;
        int notification = version4 ? (int)(lParam.ToInt64() & 0xffff) : (int)lParam;
        if (notification is 0x400 or 0x401 || !version4 && notification == 0x202)
            owner.Dispatcher.BeginInvoke(open);
        else if (notification == 0x7b || !version4 && notification == 0x205)
        {
            var point = version4 ? SnapCaption.ScreenPoint(wParam) : new Point(-1, -1);
            owner.Dispatcher.BeginInvoke(new Action(() => ShowMenu(point)));
        }
        handled = true;
        return 0;
    }

    void ShowMenu(Point anchor)
    {
        if (disposed || !visible) return;
        menu.Items.Clear();
        void Add(string label, Action action)
        {
            var item = new MenuItem { Header = L.Text(label) };
            item.SetResourceReference(FrameworkElement.StyleProperty, "TrayMenuItem");
            item.Click += (_, _) => { menu.IsOpen = false; owner.Dispatcher.BeginInvoke(action); };
            menu.Items.Add(item);
        }
        Add("Open Waypoint", open); Add("Exit Waypoint", exit);
        // Version 4 supplies the icon location even when the context menu is opened by keyboard.
        menu.Placement = PlacementMode.MousePoint;
        menu.HorizontalOffset = menu.VerticalOffset = 0;
        if (anchor.X != -1 || anchor.Y != -1)
        {
            var dpi = VisualTreeHelper.GetDpi(owner);
            menu.Placement = PlacementMode.AbsolutePoint;
            menu.HorizontalOffset = anchor.X / dpi.DpiScaleX;
            menu.VerticalOffset = anchor.Y / dpi.DpiScaleY;
        }
        // Give the popup its own foreground HWND: the workspace itself is hidden.
        menu.Opened += FocusMenu;
        menu.IsOpen = true;
    }

    void FocusMenu(object? sender, RoutedEventArgs e)
    {
        menu.Opened -= FocusMenu;
        owner.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(() =>
        {
            if (!menu.IsOpen) return;
            if (PresentationSource.FromVisual(menu) is HwndSource popup)
            {
                SetForegroundWindow(popup.Handle);
                SetFocus(popup.Handle);
            }
            ((MenuItem)menu.Items[0]).Focus();
        }));
    }

    NotifyData Data() => new()
    {
        Size = Marshal.SizeOf<NotifyData>(), Window = source.Handle, Id = 1,
        Flags = 1 | 2 | 4 | 0x80, Message = Callback, Icon = icon,
        Tip = L.Text("Waypoint — click to open"), Info = "", InfoTitle = ""
    };

    static nint LoadIcon()
    {
        using var stream = Application.GetResourceStream(new Uri($"pack://application:,,,/{typeof(TrayIcon).Assembly.GetName().Name};component/app.ico"))!.Stream;
        using var reader = new BinaryReader(stream);
        reader.ReadUInt16(); reader.ReadUInt16(); int count = reader.ReadUInt16();
        int desired = Math.Max(16, GetSystemMetrics(49));
        var entries = new List<(int Size, uint Length, uint Offset)>();
        for (int i = 0; i < count; i++)
        {
            int size = reader.ReadByte(); reader.ReadBytes(7);
            entries.Add((size == 0 ? 256 : size, reader.ReadUInt32(), reader.ReadUInt32()));
        }
        var selected = entries.OrderBy(entry => entry.Size < desired ? 1000 + desired - entry.Size : entry.Size - desired).First();
        stream.Position = selected.Offset;
        var bytes = reader.ReadBytes(checked((int)selected.Length));
        var result = CreateIconFromResourceEx(bytes, (uint)bytes.Length, true, 0x30000, desired, desired, 0);
        if (result == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not load the Waypoint icon.");
        return result;
    }

    public void Dispose()
    {
        if (disposed) return;
        Hide(); disposed = true;
        source.RemoveHook(Hook);
        DestroyIcon(icon);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct NotifyData
    {
        public int Size;
        public nint Window;
        public uint Id, Flags, Message;
        public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid Guid;
        public nint BalloonIcon;
    }
    static bool ShellNotifyIcon(uint message, NotifyData data) => NativeNotifyIcon(message, ref data);
    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] static extern bool NativeNotifyIcon(uint message, ref NotifyData data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int RegisterWindowMessage(string message);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll", SetLastError = true)] static extern nint CreateIconFromResourceEx(byte[] bits, uint size, [MarshalAs(UnmanagedType.Bool)] bool icon, uint version, int width, int height, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool DestroyIcon(nint icon);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] static extern nint SetFocus(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
}
