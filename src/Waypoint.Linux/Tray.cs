using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Waypoint;

/// <summary>
/// Waypoint's tray icon while the window is hidden, for a game session or by the close choice, as on Windows (src/Waypoint/
/// Tray.cs): a click or "Show Waypoint" brings the window back, and its menu can stop everything or exit. Avalonia's tray
/// icon is a StatusNotifierItem on Linux, which only a desktop with a tray host shows (KDE, and GNOME with its AppIndicator
/// extension): without one (<see cref="Available"/> false) Waypoint never hides its window, which would leave it out of reach.
/// </summary>
sealed class Tray : IDisposable
{
    readonly TrayIcon _icon;
    readonly NativeMenuItem _show = new(), _stop = new(), _exit = new();

    public event Action? ShowRequested, StopAllRequested, ExitRequested;

    /// <summary>The desktop shows tray icons: a StatusNotifierWatcher has its name on the session bus. Non-blocking: false
    /// until the background probe answers, and always false while rendering headless.</summary>
    public static bool Available => !Program.Rendering && _available;

    /// <summary>Raised once when the background session-bus probe finishes.</summary>
    public static event Action? Ready;

    static volatile bool _available;
    static int _asked;

    /// <summary>Starts the question early off the UI thread, so the window never waits for the session bus.</summary>
    public static void Ask()
    {
        if (Program.Rendering || Interlocked.Exchange(ref _asked, 1) != 0) return;
        _ = Task.Run(() =>
        {
            _available = WatcherRuns();
            Ready?.Invoke();
        });
    }

    public Tray()
    {
        _show.Click += (_, _) => ShowRequested?.Invoke();
        _stop.Click += (_, _) => StopAllRequested?.Invoke();
        _exit.Click += (_, _) => ExitRequested?.Invoke();
        var menu = new NativeMenu();
        menu.Items.Add(_show);
        menu.Items.Add(_stop);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(_exit);
        _icon = new TrayIcon { ToolTipText = "Waypoint", Icon = LoadIcon(), Menu = menu, IsVisible = false };
        _icon.Clicked += (_, _) => ShowRequested?.Invoke();
        if (Application.Current is { } app) TrayIcon.SetIcons(app, [_icon]);
        ShowLanguage();
    }

    public void ShowLanguage()
    {
        _show.Header = L.Text("Show Waypoint");
        _stop.Header = L.Text("Stop all");
        _exit.Header = L.Text("Exit Waypoint");
    }

    public bool Visible
    {
        get => _icon.IsVisible;
        set => _icon.IsVisible = value && Available;
    }

    /// <summary>A desktop notification, as the tray's balloon on Windows; nothing where the desktop has no notify-send.</summary>
    public static void Notify(string text)
    {
        try { Process.Start(new ProcessStartInfo("notify-send", ["--app-name=Waypoint", "--icon=waypoint", "Waypoint", text]) { UseShellExecute = false })?.Dispose(); }
        catch (System.ComponentModel.Win32Exception) { }
    }

    /// <summary>The emblem (branding/waypoint-192.png); none if it cannot be read.</summary>
    static WindowIcon? LoadIcon()
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://Waypoint/waypoint-192.png"));
            return new WindowIcon(new Bitmap(stream));
        }
        catch (Exception e) when (e is IOException or FileNotFoundException or ArgumentException) { return null; }
    }

    /// <summary>The freedesktop tray protocol's watcher, asked through the session bus's own tools (gdbus, else dbus-send). No
    /// answer counts as no tray.</summary>
    static bool WatcherRuns()
    {
        (string Program, string[] Arguments)[] asks =
        [
            ("gdbus", ["call", "--session", "--dest", "org.freedesktop.DBus", "--object-path", "/org/freedesktop/DBus",
                "--method", "org.freedesktop.DBus.NameHasOwner", "org.kde.StatusNotifierWatcher"]),
            ("dbus-send", ["--session", "--print-reply", "--dest=org.freedesktop.DBus", "/org/freedesktop/DBus",
                "org.freedesktop.DBus.NameHasOwner", "string:org.kde.StatusNotifierWatcher"]),
        ];
        foreach (var (program, arguments) in asks)
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo(program, arguments) { UseShellExecute = false, RedirectStandardOutput = true });
                if (process is null) continue;
                var output = process.StandardOutput.ReadToEndAsync();
                if (!process.WaitForExit(2000))
                {
                    process.Kill();
                    continue;
                }
                if (process.ExitCode == 0) return output.Result.Contains("true", StringComparison.Ordinal);
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { }
        }
        return false;
    }

    public void Dispose()
    {
        _icon.IsVisible = false;
        if (Application.Current is { } app) TrayIcon.SetIcons(app, []);
        _icon.Dispose();
    }
}
