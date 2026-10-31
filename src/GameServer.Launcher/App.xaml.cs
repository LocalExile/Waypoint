using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;

namespace GameServer.Launcher;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var settings = Settings.Load();
        string? root = Repo.Find(settings.ServerPath, Environment.CurrentDirectory);
        if (root is null)
        {
            MessageBox.Show("Choose the folder of your LocalExile server checkout: the one with GameServer.slnx and " +
                "scripts\\dev\\live.ps1. The launcher remembers it; Change… on the Control section picks another one.",
                "LocalExile Launcher: first start", MessageBoxButton.OK, MessageBoxImage.Information);
            root = ChooseServer(null);
        }
        if (root is null)
        {
            Shutdown(0);
            return;
        }
        if (settings.ServerPath != root)
        {
            settings.ServerPath = root;
            settings.Save();
        }
        // Every path of the server's tools is relative to its root (its AGENTS.md).
        Environment.CurrentDirectory = root;
        Theme.Apply(settings.Theme);
        DispatcherUnhandledException += Unhandled;
        // Off the UI thread nothing can keep the process alive, but the reason is kept.
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Crashed(args.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, args) => args.SetObserved();
        new MainWindow().Show();
    }

    /// <summary>The folder picker until it gives a server checkout; null when the player cancels.</summary>
    internal static string? ChooseServer(Window? owner)
    {
        string start = Repo.Suggested(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory));
        while (true)
        {
            var dialog = new OpenFolderDialog
            {
                Title = "LocalExile server checkout (the folder with GameServer.slnx)",
                InitialDirectory = start,
            };
            if (dialog.ShowDialog(owner) != true) return null;
            if (Repo.IsRoot(dialog.FolderName)) return Path.GetFullPath(dialog.FolderName);
            MessageBox.Show($"{dialog.FolderName} is not a LocalExile server checkout: it has no GameServer.slnx and " +
                "scripts\\dev\\live.ps1.", "LocalExile Launcher", MessageBoxButton.OK, MessageBoxImage.Warning);
            start = dialog.FolderName;
        }
    }

    /// <summary>Another checkout: saved in the launcher's settings, then the launcher starts again on it (every tab reads
    /// from the old one). The new one starts only once this one has exited: closing can still be cancelled.</summary>
    internal static void SwitchServer(Window owner, Settings settings)
    {
        if (ChooseServer(owner) is not { } root || root == settings.ServerPath) return;
        settings.ServerPath = root;
        settings.Save();
        if (Environment.ProcessPath is { } exe)
            Current.Exit += (_, _) => Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true })?.Dispose();
        owner.Close();
    }

    static void Crashed(object exception)
    {
        try
        {
            Directory.CreateDirectory(Repo.Logs);
            File.AppendAllText(Path.Combine(Repo.Logs, "launcher-crash.log"), $"{DateTime.Now:O}\n{exception}\n\n");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    static void Unhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.ToString(), "LocalExile Launcher: unexpected error", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
