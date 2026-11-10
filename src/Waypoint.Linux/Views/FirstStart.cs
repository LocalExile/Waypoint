using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;

namespace Waypoint;

/// <summary>What opens with no checkout chosen, as on Windows: the setup guide, the chooser when the player skipped it, then the
/// window. Closing either without a folder ends Waypoint. A saved folder that is not there any more is asked about first
/// (<see cref="MissingFolderDialog"/>): that is not a first start.</summary>
static class FirstStart
{
    public static async void Run(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var app = App.Instance;
        try
        {
            string? root = null;
            // The saved folder is not there any more (moved, renamed, a drive not connected). That is not a first start:
            // Waypoint says so and offers the ways on, and only "Set up again…" leads to the guide below.
            if (Program.Checkout is null && app.Settings.ServerPath is { Length: > 0 } gone)
            {
                SessionLog.Current?.Write("The saved folder is not there: " + gone);
                var question = new MissingFolderDialog(gone, [.. FolderList.Read(app.Settings, "").Where(folder => folder.Kind != FolderKind.Missing)]);
                await Ui.ShowAsync(question, null);
                if (question.Chosen is null && !question.SetUpAgain) { desktop.Shutdown(0); return; }
                root = question.Chosen;
                SessionLog.Current?.Write(root is null ? "Answer: the setup guide" : "Answer: " + root);
            }
            if (root is null)
            {
                var guide = new SetupWizard();
                await Ui.ShowAsync(guide, null);
                root = guide.Checkout;
                if (root is null && guide.Skipped)
                {
                    var chooser = new CheckoutDialog(null, app.Settings.ServerPath);
                    await Ui.ShowAsync(chooser, null);
                    root = chooser.Chosen;
                }
                if (root is not null && guide.CreateAccountNext) app.Settings.CreateAccountOnStart = true;
            }
            if (root is null) { desktop.Shutdown(0); return; }
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
