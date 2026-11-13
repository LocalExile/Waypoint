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
        // Before anything is started: a PowerShell 7 shell's module path would break the scripts Windows PowerShell runs.
        Platform.UseWindowsPowerShellModules();
        Settings = Settings.Load();
        // The folder the settings name before anything else writes one: the guide saves its own.
        string? saved = Settings.ServerPath;
        // Waypoint's own log, from the first moment: the setup guide runs before there is a checkout.
        SessionLog.Open(SupportReport.LogFolder, $"Waypoint {Updates.Current} · {Environment.OSVersion} · .NET {Environment.Version}");
        // The player's folders and tools, before anything reads a folder or starts a tool.
        Settings.ApplyPaths();
        // Before anything shows, the first start's questions included.
        L.Current.SetLanguage(Settings.Language);
        Theme.Apply(Settings.Appearance);
        Repo.UseLocalProfile = Settings.UseLocalProfile;
        try
        {
            var options = AppStartupOptions.Parse(e.Args, Environment.CurrentDirectory);
            if (!options.AllowMultiple && !await BecomePrimary(options)) { Shutdown(0); return; }
            string? root = Repo.Find(options.RepositoryPath ?? Settings.ServerPath, Environment.CurrentDirectory);
            // The saved folder is not there any more (moved, renamed, a drive not connected). That is not a first start:
            // Waypoint says so and offers the ways on, and only "Set up again…" leads to the guide below.
            if (root is null && options.RepositoryPath is null && Settings.ServerPath is { Length: > 0 } gone)
            {
                SessionLog.Current?.Write("The saved folder is not there: " + gone);
                var question = new MissingFolderDialog(gone, [.. FolderList.Read(Settings, "").Where(folder => folder.Kind != FolderKind.Missing)]);
                if (question.ShowDialog() != true) { Shutdown(0); return; }
                root = question.Chosen;
                SessionLog.Current?.Write(root is null ? "Answer: the setup guide" : "Answer: " + root);
            }
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
            // A source copy its user went to on purpose (the guide, the chooser, --repo, the folder Waypoint was started
            // in) is Advanced mode's, with nothing to choose there. Saved at once: the guide has saved its folder already.
            if (Editions.Arrived(Settings, root, saved)) Settings.Save();
            if (Settings.ServerPath != root && options.RepositoryPath is null)
            {
                Settings.ServerPath = root;
                Settings.Save();
            }
            // Every path of the server's tools is relative to its root (its AGENTS.md).
            Environment.CurrentDirectory = root;
            Repo.IsInstall = LocalExileReleases.IsInstall(root);
            if (Repo.IsInstall) Repo.ClientVersion = LocalExileReleases.Installed(root);
            SessionLog.Current?.Write("Folder: " + root);
            foreach (string detail in SupportReport.Details(Editions.Advanced(Settings))) SessionLog.Current?.Write(detail);
            if (options.RepositoryPath is null) Editions.Remember(Settings, root);
            // A Python installed without its PATH option counts, for Waypoint and the scripts it runs.
            await Prerequisites.RefreshToolsAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException
            or System.ComponentModel.Win32Exception or System.Security.SecurityException or InvalidOperationException)
        {
            SessionLog.Current?.Write("Could not open Waypoint: " + ex, "ERROR");
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

    /// <summary>Another LocalExile folder: Waypoint starts again on it (every section reads from the old one), and no file of
    /// either folder is moved. Every way of changing folder comes through here. What still runs is stopped first on the
    /// user's word (<see cref="Editions.ReadyToLeaveAsync"/>; <paramref name="asked"/>: the caller has seen to it), and the
    /// folder is saved only once nothing can refuse the close: an operation in progress is the one thing that does, and a
    /// folder saved before a refused close would open at the next start instead. <paramref name="createAccount"/>: the
    /// next start opens the Create account dialog, noted only with the folder. <paramref name="before"/>: a last question
    /// of the caller's, asked once what ran has been seen to; false gives the change up. True when Waypoint is starting
    /// again; false when everything is as it was.</summary>
    internal static async Task<bool> SwitchServerAsync(Window owner, Shared shared, string? chosen = null, bool asked = false, bool createAccount = false,
        Func<Task<bool>>? before = null)
    {
        // One change of folder at a time: the first is stopping what runs.
        if (shared.Leaving) return false;
        // The folder in use, not the saved one: started with --repo, they differ.
        if ((chosen ?? ChooseServer(owner)) is not { } root || Editions.SameFolder(root, Environment.CurrentDirectory)) return false;
        try
        {
            if (!asked && !await Editions.ReadyToLeaveAsync(shared, root, (kind, title, message, buttons) => Dialog.Show(owner, kind, title, message, null, buttons))) return false;
            // Windows is signing its user out, which closed the window during the wait: nothing is saved or started.
            if (Current is App { IsSessionEnding: true }) return false;
            if (shared.IsLiveRunning)
            {
                Dialog.Tell(owner, DialogKind.Info, L.Text("{0} is still running", shared.BusyWith), L.Text("Wait for the current operation before changing checkout."));
                return false;
            }
            if (before is not null && !await before()) return false;
            var settings = shared.Settings;
            shared.Logs.Write($"change of folder: from {Environment.CurrentDirectory} to {root}", Severity.Info);
            Editions.LeftFor(settings, root, Repo.IsInstall ? null : Environment.CurrentDirectory);
            FolderList.Add(settings, root);
            settings.ServerPath = root;
            if (createAccount) settings.CreateAccountOnStart = true;
            settings.Save();
        }
        // Up to here the window did not close once Waypoint had begun to stop what ran: now it may, or is usable again.
        finally { shared.Leaving = false; }
        StartAgain(owner);
        return true;
    }

    /// <summary>Waypoint again, once this one has exited: after a change every section reads anew (its folders, say). What
    /// runs is left as it is (<see cref="MainWindow.ExitNow"/>).</summary>
    internal static void StartAgain(Window owner)
    {
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
        SessionLog.Current?.Write("Waypoint crashed: " + exception, "ERROR");
        try
        {
            Directory.CreateDirectory(Repo.Logs);
            File.AppendAllText(Path.Combine(Repo.Logs, "launcher-crash.log"), $"{DateTime.Now:O}\n{exception}\n\n");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    static void Unhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        SessionLog.Current?.Write("Unexpected error: " + e.Exception, "ERROR");
        if (Dialog.Show(Current.MainWindow, DialogKind.Error, L.Text("Unexpected error"),
                L.Text("Something failed; Waypoint keeps running. The details help a bug report."), e.Exception.ToString(),
                [L.Text("OK"), L.Text("Copy details")]) == 1)
            Clipboard.SetText(e.Exception.ToString());
        e.Handled = true;
    }
}
