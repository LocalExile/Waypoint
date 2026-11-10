using System.Diagnostics;
using System.Reflection;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;

namespace Waypoint;

/// <summary>Waypoint on Linux. As on Windows it works from the chosen checkout or release install, whose path is in the
/// settings (Settings.ServerPath), and everything it does goes through the shared engine.</summary>
sealed class App : Avalonia.Application
{
    public static App Instance { get; private set; } = null!;
    public Settings Settings { get; private set; } = new();
    /// <summary>The folder the settings named when they were read: the guide and <see cref="Open"/> save their own.</summary>
    string? _saved;

    public override void Initialize()
    {
        Instance = this;
        RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
        AvaloniaXamlLoader.Load(this);
    }

    public void Prepare()
    {
        Settings = Settings.Load();
        _saved = Settings.ServerPath;
        // Waypoint's own log, from the first moment: the setup guide runs before there is a checkout.
        SessionLog.Open(SupportReport.LogFolder, $"Waypoint {Updates.Current} · {Environment.OSVersion} · .NET {Environment.Version}");
        // The player's folders and tools, before anything reads a folder or starts a tool.
        Settings.ApplyPaths();
        L.Current.SetLanguage(Settings.Language);
        Repo.UseLocalProfile = Settings.UseLocalProfile;
        string? root = Repo.Find(Program.Checkout ?? Settings.ServerPath, Environment.CurrentDirectory);
        if (root is not null) Open(root);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        Prepare();
        // As on Windows: a failed click is reported and Waypoint keeps running.
        Avalonia.Threading.Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            e.Handled = true;
            SessionLog.Current?.Write("Unexpected error: " + e.Exception, "ERROR");
            try
            {
                Directory.CreateDirectory(Repo.Logs);
                File.AppendAllText(Path.Combine(Repo.Logs, "launcher-crash.log"), $"{DateTime.Now:O}\n{e.Exception}\n\n");
            }
            catch (Exception io) when (io is IOException or UnauthorizedAccessException) { }
            Dialog.Tell(null, DialogKind.Error, L.Text("Unexpected error"), L.Text("Something failed; Waypoint keeps running. The details help a bug report."), e.Exception.ToString());
        };
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (string.IsNullOrEmpty(Repo.Find(Environment.CurrentDirectory, Environment.CurrentDirectory)))
            {
                // The first start's questions are windows too; closing one must not end the application. It runs once the
                // main loop does, which ending Waypoint needs.
                desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnExplicitShutdown;
                Dispatcher.UIThread.Post(() => FirstStart.Run(desktop));
            }
            else
            {
                desktop.MainWindow = new MainWindow();
                desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnMainWindowClose;
            }
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>The folder picker until it gives a server checkout; null when the player cancels.</summary>
    internal static string? ChooseServer(Window owner)
    {
        string start = Repo.Suggested(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory));
        while (true)
        {
            if (Ui.PickFolder(owner, L.Text("LocalExile server checkout (the folder with GameServer.slnx)"), start) is not { } folder) return null;
            if (Repo.IsRoot(folder) || LocalExileReleases.IsInstall(folder)) return Path.GetFullPath(folder);
            Dialog.Tell(owner, DialogKind.Warning, L.Text("Not a server checkout"),
                L.Text("{0} has no GameServer.slnx and scripts\\dev\\live.ps1.", folder), ok: L.Text("Choose again"));
            start = folder;
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
        // The folder in use, not the saved one: started with a folder as its argument, they differ.
        if ((chosen ?? ChooseServer(owner)) is not { } root || Editions.SameFolder(root, Environment.CurrentDirectory)) return false;
        try
        {
            if (!asked && !await Editions.ReadyToLeaveAsync(shared, root, (kind, title, message, buttons) => Dialog.Show(owner, kind, title, message, null, buttons))) return false;
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
        Restart(owner);
        return true;
    }

    /// <summary>Waypoint again, once this one has exited: after a change every section reads anew (its folders, say). The
    /// window is not asked what to do with what runs and leaves it as it is (<see cref="MainWindow.ExitNow"/>): a close
    /// that could be cancelled would leave this Waypoint open with the next one still to start.</summary>
    internal static void Restart(Window owner)
    {
        if (Instance.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.Exit += (_, _) => StartAgain();
        if (owner is MainWindow window) window.ExitNow();
        else owner.Close();
    }

    static void StartAgain()
    {
        if (Environment.ProcessPath is not { } exe) return;
        var start = new ProcessStartInfo(exe) { UseShellExecute = false };
        // Started as `dotnet Waypoint.dll`, the process is dotnet.
        if (Path.GetFileNameWithoutExtension(exe) == "dotnet") start.ArgumentList.Add(typeof(App).Assembly.Location);
        Process.Start(start)?.Dispose();
    }

    /// <summary>A colour of the theme by its name, as on Windows.</summary>
    public static IBrush Brush(string name) => Instance.TryGetResource(name, null, out var value) && value is IBrush brush ? brush : Brushes.Gray;

    /// <summary>Every path of the server's tools is relative to its root (its AGENTS.md).</summary>
    public void Open(string root)
    {
        // A source copy its user went to on purpose (the guide, the chooser, a folder as the argument, the folder Waypoint
        // was started in) is Advanced mode's, with nothing to choose there. A picture changes nothing.
        bool arrived = !Program.Rendering && Editions.Arrived(Settings, root, _saved);
        if (Settings.ServerPath != root || arrived) { Settings.ServerPath = root; Settings.Save(); }
        Environment.CurrentDirectory = root;
        Repo.IsInstall = LocalExileReleases.IsInstall(root);
        if (Repo.IsInstall) Repo.ClientVersion = LocalExileReleases.Installed(root);
        SessionLog.Current?.Write("Folder: " + root);
        foreach (string detail in SupportReport.Details(Editions.Advanced(Settings))) SessionLog.Current?.Write(detail);
        Editions.Remember(Settings, root);
        if (!Program.Rendering) GameSettings.Prepare(root);
    }
}

static class Program
{
    public static string? Checkout { get; private set; }
    /// <summary>Only a picture is being made: nothing may change, so nothing reaches out to git's remotes.</summary>
    public static bool Rendering { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        // Until the folder exists its path reads as empty, and the settings would land in the working directory: a machine
        // with no ~/.local/share would never remember its checkout.
        _ = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create);
        if (args.Length > 0 && Directory.Exists(args[0])) Checkout = Path.GetFullPath(args[0]);
        if (args.Length >= 3 && args[1] == "--render") return Render(args[2], args.Length > 3 ? args[3] : null, args.Length > 4 ? int.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture) : 3);
        return AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>The window, or one section by its type (`Waypoint &lt;checkout&gt; --render out.png PlayTab`), as a picture with no
    /// display: how the interface is checked without opening anything on the desktop. A window of its own renders the same way,
    /// and the guide opens on a page by name (`SetupWizard:Client`). A few seconds pass first, for what a section reads; a last
    /// argument sets how many.</summary>
    static int Render(string file, string? section, int seconds)
    {
        Rendering = true;
        var app = AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
        ((App)app.Instance!).Prepare();
        Avalonia.Controls.Window window = string.IsNullOrEmpty(section) ? new MainWindow() : Hosting(section);
        window.Show();
        for (int i = 0; i < seconds * 20; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Thread.Sleep(50);
        }
#pragma warning disable CS0618
        window.CaptureRenderedFrame()?.Save(file);
#pragma warning restore CS0618
        return 0;
    }

    static Avalonia.Controls.Window Hosting(string section)
    {
        string[] parts = section.Split(':');
        var type = typeof(Program).Assembly.GetType("Waypoint." + parts[0], throwOnError: true)!;
        if (!typeof(Avalonia.Controls.Window).IsAssignableFrom(type))
            return new Avalonia.Controls.Window { Width = 970, Height = 700, Content = Attached(type, new Shared()) };
        var window = (Avalonia.Controls.Window)Activator.CreateInstance(type)!;
        if (window is SetupWizard guide && parts.Length > 1) guide.ShowPage(parts[1]);
        return window;
    }

    /// <summary>A section attached as the window attaches it: what its Attach takes besides Shared is another section (Play's Control).</summary>
    static Avalonia.Controls.Control Attached(Type section, Shared shared)
    {
        var view = (Avalonia.Controls.Control)Activator.CreateInstance(section)!;
        var attach = section.GetMethod("Attach", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        attach?.Invoke(view, [.. attach.GetParameters().Select(p => p.ParameterType == typeof(Shared) ? shared : (object)Attached(p.ParameterType, shared))]);
        return view;
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}
