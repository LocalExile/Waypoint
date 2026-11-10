using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;

namespace Waypoint;

/// <summary>What opens with no checkout chosen, as on Windows: the setup guide, the chooser when the player skipped it, then the
/// window. Closing either without a folder ends Waypoint.</summary>
static class FirstStart
{
    public static async void Run(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var app = App.Instance;
        try
        {
            var guide = new SetupWizard();
            await Ui.ShowAsync(guide, null);
            string? root = guide.Checkout;
            if (root is null && guide.Skipped)
            {
                var chooser = new CheckoutDialog(null, app.Settings.ServerPath);
                await Ui.ShowAsync(chooser, null);
                root = chooser.Chosen;
            }
            if (root is null) { desktop.Shutdown(0); return; }
            if (guide.CreateAccountNext) app.Settings.CreateAccountOnStart = true;
            app.Open(root);
            var window = new MainWindow();
            desktop.MainWindow = window;
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            window.Show();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException
            or System.ComponentModel.Win32Exception or System.Security.SecurityException or InvalidOperationException)
        {
            Dialog.Tell(null, DialogKind.Error, L.Text("Could not open Waypoint"), L.Text(ex.Message));
            desktop.Shutdown(1);
        }
    }
}
