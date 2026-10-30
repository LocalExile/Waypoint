using Forms = System.Windows.Forms;

namespace GameServer.Launcher;

/// <summary>The launcher's tray icon while it waits out a game session: double-click or "Show launcher" brings the
/// window back, and its menu can stop everything or close the launcher.</summary>
sealed class Tray : IDisposable
{
    readonly Forms.NotifyIcon _icon;

    public event Action? ShowRequested, StopAllRequested, ExitRequested;

    public Tray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Show launcher", null, (_, _) => ShowRequested?.Invoke());
        menu.Items.Add("Stop all", null, (_, _) => StopAllRequested?.Invoke());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitRequested?.Invoke());
        _icon = new Forms.NotifyIcon { Text = "LocalExile Launcher", Icon = LoadIcon(), ContextMenuStrip = menu };
        _icon.DoubleClick += (_, _) => ShowRequested?.Invoke();
    }

    public bool Visible
    {
        get => _icon.Visible;
        set => _icon.Visible = value;
    }

    public void Notify(string text) => _icon.ShowBalloonTip(3000, "LocalExile Launcher", text, Forms.ToolTipIcon.Info);

    /// <summary>The game's own icon, read from the local client copy; the generic one without it.</summary>
    static System.Drawing.Icon LoadIcon()
    {
        try
        {
            string exe = Path.GetFullPath(Path.Combine("Client", "Game.exe"));
            if (File.Exists(exe) && System.Drawing.Icon.ExtractAssociatedIcon(exe) is { } icon) return icon;
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException) { }
        return System.Drawing.SystemIcons.Application;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
