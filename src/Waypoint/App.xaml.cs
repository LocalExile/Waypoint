using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Waypoint;

public partial class App : Application
{
    AppInstance? _instance;
    bool _createAccount;
    /// <summary>The one copy of the settings: the first start's questions and the window share it.</summary>
    internal Settings Settings { get; private set; } = new();
    public bool IsSessionEnding { get; private set; }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        IsSessionEnding = true;
        base.OnSessionEnding(e);
        if (e.Cancel) IsSessionEnding = false;
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // The first start's questions are windows too; closing one must not end the application.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Settings = Settings.Load();
        // Before anything shows, the first start's questions included.
        L.Current.SetLanguage(Settings.Language);
        Theme.Apply(Settings.Appearance);
        Repo.UseLocalProfile = Settings.UseLocalProfile;
        try
        {
            var options = AppStartupOptions.Parse(e.Args, Environment.CurrentDirectory);
            if (!options.AllowMultiple && !await BecomePrimary(options)) { Shutdown(0); return; }
            string? root = Repo.Find(options.RepositoryPath ?? Settings.ServerPath, Environment.CurrentDirectory);
            if (root is null)
            {
                var guide = new SetupWizard(Settings, null, null);
                guide.ShowDialog();
                root = guide.Checkout;
                _createAccount = guide.CreateAccountNext;
                if (root is null && guide.Skipped)
                {
                    var chooser = new CheckoutDialog(null, Settings.ServerPath);
                    if (chooser.ShowDialog() == true) root = chooser.Chosen;
                }
            }
            if (root is null) { Shutdown(0); return; }
            if (Settings.ServerPath != root && options.RepositoryPath is null)
            {
                Settings.ServerPath = root;
                Settings.Save();
            }
            // Every path of the server's tools is relative to its root (its AGENTS.md).
            Environment.CurrentDirectory = root;
            Repo.IsInstall = LocalExileReleases.IsInstall(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException
            or System.ComponentModel.Win32Exception or System.Security.SecurityException or InvalidOperationException)
        {
            Dialog.Tell(null, DialogKind.Error, L.Text("Could not open Waypoint"), L.Text(ex.Message));
            ReleaseInstance();
            Shutdown(1);
            return;
        }
        DispatcherUnhandledException += Unhandled;
        // Off the UI thread nothing can keep the process alive, but the reason is kept.
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Crashed(args.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, args) => args.SetObserved();
        var window = new MainWindow();
        MainWindow = window;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        window.Show();
        if (Settings.CreateAccountOnStart)
        {
            Settings.CreateAccountOnStart = false;
            Settings.Save();
            _createAccount = true;
        }
        if (_createAccount) window.CreateFirstAccount();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        ReleaseInstance();
        base.OnExit(e);
    }

    /// <summary>Waypoint runs once per Windows user and session: two copies would fight over one checkout's services. A
    /// second start brings the first window forward, and says why when it cannot simply do that. False: this copy ends.</summary>
    async Task<bool> BecomePrimary(AppStartupOptions options)
    {
        var identity = ReadIdentity();
        _instance = new(identity);
        if (_instance.IsPrimary) { Listen(identity); return true; }
        AppActivationReply reply;
        try { reply = await _instance.ActivateAsync(new(identity, options.RepositoryPath), WindowActivation.Allow); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException or UnauthorizedAccessException)
        {
            // The first one may have exited while this one connected: claim ownership once more, and never open a second
            // window beside an owner that is merely slow or out of reach.
            _instance.Dispose();
            _instance = new(identity);
            if (_instance.IsPrimary) { Listen(identity); return true; }
            throw new IOException(L.Text("The running Waypoint could not be contacted. Close it normally and try again. Use the same Windows permission level for both copies."), ex);
        }
        ReleaseInstance();
        if (reply.Status == AppActivationStatus.Focused) return false;
        string detail = reply.Status switch
        {
            AppActivationStatus.DifferentBuild => L.Text("Another Waypoint build is already open. Close that window normally before opening this build. Existing operations and services have not been stopped."),
            AppActivationStatus.DifferentCheckout => L.Text("Waypoint is already managing a checkout. Use Change location in the running window when you are ready. This launch has not changed its checkout or settings."),
            _ => L.Text("Waypoint is closing. Wait for it to finish, then open it again."),
        };
        if (reply.Busy) detail += "\n\n" + L.Text("The running window has an operation in progress. Let it finish before closing or changing checkout.");
        Dialog.Tell(null, DialogKind.Info, L.Text("Waypoint is already open"), detail, details: $"{reply.Running.Version}\n{reply.Running.ExecutablePath}\n{reply.RepositoryPath}");
        return false;
    }

    void Listen(AppInstanceIdentity identity) => _instance!.StartListening((request, token) => Dispatcher.InvokeAsync(() =>
    {
        if (MainWindow is not MainWindow workspace || !workspace.IsLoaded) return new AppActivationReply(AppActivationStatus.Starting, identity);
        return workspace.ActivateFromLaunch(identity, request);
    }, DispatcherPriority.Normal, token).Task);

    /// <summary>Ends this copy's claim, so a new one (a self-update, another checkout) can make its own.</summary>
    internal void ReleaseInstance()
    {
        _instance?.Dispose();
        _instance = null;
    }

    static AppInstanceIdentity ReadIdentity()
    {
        string executable = Environment.ProcessPath ?? throw new InvalidOperationException("Waypoint could not find its own executable.");
        var file = new FileInfo(executable);
        string build = (typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown")
            + " / " + file.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + " / " + file.LastWriteTimeUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return new(Environment.ProcessId, Updates.Current.ToString(), build, executable);
    }

    /// <summary>The folder picker until it gives a server checkout; null when the player cancels.</summary>
    internal static string? ChooseServer(Window? owner)
    {
        string start = Repo.Suggested(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory));
        while (true)
        {
            var dialog = new OpenFolderDialog
            {
                Title = L.Text("LocalExile server checkout (the folder with GameServer.slnx)"),
                InitialDirectory = start,
            };
            if (dialog.ShowDialog(owner) != true) return null;
            if (Repo.IsRoot(dialog.FolderName) || LocalExileReleases.IsInstall(dialog.FolderName)) return Path.GetFullPath(dialog.FolderName);
            Dialog.Tell(owner, DialogKind.Warning, L.Text("Not a server checkout"),
                L.Text("{0} has no GameServer.slnx and scripts\\dev\\live.ps1.", dialog.FolderName), ok: L.Text("Choose again"));
            start = dialog.FolderName;
        }
    }

    /// <summary>Another checkout: saved in the settings, then Waypoint starts again on it (every section reads from the old
    /// one). The new copy starts only once this one has exited: closing can still be cancelled.</summary>
    internal static void SwitchServer(Window owner, Settings settings, string? chosen = null)
    {
        if ((chosen ?? ChooseServer(owner)) is not { } root || root == settings.ServerPath) return;
        settings.ServerPath = root;
        settings.Save();
        if (Environment.ProcessPath is { } exe)
            Current.Exit += (_, _) =>
            {
                ((App)Current).ReleaseInstance();
                Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true })?.Dispose();
            };
        if (owner is MainWindow window) window.ExitNow();
        else owner.Close();
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
        if (Dialog.Show(Current.MainWindow, DialogKind.Error, L.Text("Unexpected error"),
                L.Text("Something failed; Waypoint keeps running. The details help a bug report."), e.Exception.ToString(),
                [L.Text("OK"), L.Text("Copy details")]) == 1)
            Clipboard.SetText(e.Exception.ToString());
        e.Handled = true;
    }
}
