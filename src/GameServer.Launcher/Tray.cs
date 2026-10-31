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

    /// <summary>The launcher's own icon at the tray's size (Assets\LocalExile.ico); the generic one if it cannot be read.</summary>
    static System.Drawing.Icon LoadIcon()
    {
        try
        {
            var resource = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/LocalExile.ico"));
            if (resource is not null)
                using (var stream = resource.Stream)
                    return new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
        }
        catch (Exception e) when (e is IOException or ArgumentException) { }
        return System.Drawing.SystemIcons.Application;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
