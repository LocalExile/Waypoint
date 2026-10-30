using System.Windows;
using System.Windows.Threading;

namespace GameServer.Launcher;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        string? root = Repo.Find(Environment.CurrentDirectory, Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory));
        if (root is null)
        {
            MessageBox.Show("Run the launcher from the LocalExile repository root: LocalExileLauncher.exe beside GameServer.slnx " +
                "(dotnet publish src/GameServer.Launcher -o .), or dotnet run --project src/GameServer.Launcher from the root.",
                "LocalExile Launcher", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }
        Environment.CurrentDirectory = root;
        DispatcherUnhandledException += Unhandled;
        // Off the UI thread nothing can keep the process alive, but the reason is kept.
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Crashed(args.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, args) => args.SetObserved();
        new MainWindow().Show();
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
