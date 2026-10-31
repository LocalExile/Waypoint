using Forms = System.Windows.Forms;

namespace Waypoint;

/// <summary>Waypoint's tray icon while the window is hidden, for a game session or by the close choice: double-click or
/// "Show Waypoint" brings the window back, and its menu can stop everything or exit.</summary>
sealed class Tray : IDisposable
{
    readonly Forms.NotifyIcon _icon;
    readonly Forms.ContextMenuStrip _menu = new() { ShowImageMargin = false };
    readonly Forms.ToolStripMenuItem _show, _stop, _exit;

    public event Action? ShowRequested, StopAllRequested, ExitRequested;

    public Tray()
    {
        _show = new Forms.ToolStripMenuItem("", null, (_, _) => ShowRequested?.Invoke());
        _stop = new Forms.ToolStripMenuItem("", null, (_, _) => StopAllRequested?.Invoke());
        _exit = new Forms.ToolStripMenuItem("", null, (_, _) => ExitRequested?.Invoke());
        _menu.Items.Add(_show);
        _menu.Items.Add(_stop);
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add(_exit);
        _icon = new Forms.NotifyIcon { Text = "Waypoint", Icon = LoadIcon(), ContextMenuStrip = _menu };
        _icon.DoubleClick += (_, _) => ShowRequested?.Invoke();
        ShowLanguage();
        ShowTheme();
        Theme.Changed += ShowTheme;
    }

    /// <summary>The menu's colours follow the appearance (a Windows Forms menu cannot read the WPF resources).</summary>
    void ShowTheme()
    {
        _menu.Renderer = new Forms.ToolStripProfessionalRenderer(new MenuColors()) { RoundedEdges = false };
        _menu.BackColor = Color("Shell.Background");
        _menu.ForeColor = Color("Shell.Text");
        foreach (Forms.ToolStripItem item in _menu.Items) item.ForeColor = Color("Shell.Text");
    }

    static System.Drawing.Color Color(string key) =>
        Theme.Brush(key) is System.Windows.Media.SolidColorBrush { Color: var c } ? System.Drawing.Color.FromArgb(c.R, c.G, c.B) : System.Drawing.Color.Gray;

    sealed class MenuColors : Forms.ProfessionalColorTable
    {
        public override System.Drawing.Color MenuBorder => Color("Shell.Border");
        public override System.Drawing.Color MenuItemBorder => Color("Shell.RowHover");
        public override System.Drawing.Color MenuItemSelected => Color("Shell.RowHover");
        public override System.Drawing.Color ToolStripDropDownBackground => Color("Shell.Background");
        public override System.Drawing.Color SeparatorDark => Color("Shell.Border");
        public override System.Drawing.Color SeparatorLight => Color("Shell.Border");
    }

    public void ShowLanguage()
    {
        _show.Text = L.Text("Show Waypoint");
        _stop.Text = L.Text("Stop all");
        _exit.Text = L.Text("Exit Waypoint");
    }

    public bool Visible
    {
        get => _icon.Visible;
        set => _icon.Visible = value;
    }

    public void Notify(string text) => _icon.ShowBalloonTip(3000, "Waypoint", text, Forms.ToolTipIcon.Info);

    /// <summary>The emblem at the tray's size (branding\app.ico); the generic one if it cannot be read.</summary>
    static System.Drawing.Icon LoadIcon()
    {
        try
        {
            var resource = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/app.ico"));
            if (resource is not null)
                using (var stream = resource.Stream)
                    return new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
        }
        catch (Exception e) when (e is IOException or ArgumentException) { }
        return System.Drawing.SystemIcons.Application;
    }

    public void Dispose()
    {
        Theme.Changed -= ShowTheme;
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
    }
}
