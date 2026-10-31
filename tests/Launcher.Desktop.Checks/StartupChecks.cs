using System.IO;
using System.Windows;
using System.Windows.Controls;
using Launcher.Core;
using Launcher.Desktop;

static class StartupChecks
{
    public static void Run(App app, MainWindow owner, Action<bool, string> check)
    {
        string root = Path.Combine(Path.GetTempPath(), "Waypoint startup checks " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            check(LegacyAppInstances.InspectExecutable(Path.Combine(root, "Waypoint.exe"), 1) is null,
                "A process name alone is never sufficient evidence of a legacy Waypoint.");
            File.WriteAllText(Path.Combine(root, "brand.json"), "{\"executableName\":\"Unrelated\",\"version\":\"1.0.0\"}");
            File.Copy(typeof(SettingsStore).Assembly.Location, Path.Combine(root, "Launcher.Core.dll"));
            File.Copy(typeof(SettingsStore).Assembly.Location, Path.Combine(root, "Unrelated.dll"));
            File.Copy(typeof(SettingsStore).Assembly.Location, Path.Combine(root, "Unrelated.exe"));
            check(LegacyAppInstances.InspectExecutable(Path.Combine(root, "Unrelated.exe"), 1) is null,
                "Unrelated binaries with a branding file and Core DLL are not adopted as running Waypoint.");
            string executable = Path.Combine(AppContext.BaseDirectory, "Launcher.Desktop.exe");
            if (!File.Exists(executable)) executable = Environment.ProcessPath!;
            check(LegacyAppInstances.InspectExecutable(executable, Environment.ProcessId) is null,
                "A protocol-aware build is not mistaken for a legacy instance.");
            var dialog = new StartupNoticeDialog(owner, L.Text("Waypoint is already open"));
            check(dialog.Content is DockPanel && dialog.Owner == owner, "Startup notices use the existing owned, scrollable dialog frame.");
            dialog.Close();
            var standalone = new StartupNoticeDialog(null, L.Text("Waypoint is already open"));
            check(standalone.ShowInTaskbar && standalone.Owner is null && standalone.WindowStartupLocation == WindowStartupLocation.CenterScreen,
                "A secondary process without a workspace exposes its notice as a reachable standalone window.");
            var actions = (WrapPanel)((DockPanel)standalone.Content).Children[0];
            check(actions.Children.OfType<Button>().Single() is { IsCancel: true, IsDefault: true },
                "A startup conflict defaults to closing the notice rather than terminating or changing the running app.");
            standalone.Close();
        }
        finally { Directory.Delete(root, true); }
    }
}
