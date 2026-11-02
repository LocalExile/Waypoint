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

    public override void Initialize()
    {
        Instance = this;
        RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
        AvaloniaXamlLoader.Load(this);
    }

    public void Prepare()
    {
        Settings = Settings.Load();
        L.Current.SetLanguage(Settings.Language);
        Repo.UseLocalProfile = Settings.UseLocalProfile;
        string? root = Repo.Find(Program.Checkout ?? Settings.ServerPath, Environment.CurrentDirectory);
        if (root is not null) Open(root);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        Prepare();
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

    /// <summary>Another checkout: saved in the settings, then Waypoint starts again on it (every section reads from the old
    /// one). The new copy starts only once this one has exited: closing can still be cancelled.</summary>
    internal static void SwitchServer(Window owner, Settings settings, string? chosen = null)
    {
        if ((chosen ?? ChooseServer(owner)) is not { } root || root == settings.ServerPath) return;
        settings.ServerPath = root;
        settings.Save();
        if (Instance.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.Exit += (_, _) => StartAgain();
        owner.Close();
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
        if (Settings.ServerPath != root) { Settings.ServerPath = root; Settings.Save(); }
        Environment.CurrentDirectory = root;
        Repo.IsInstall = LocalExileReleases.IsInstall(root);
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
        Avalonia.Controls.Window window = section is null ? new MainWindow() : Hosting(section);
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
        object made = Activator.CreateInstance(typeof(Program).Assembly.GetType("Waypoint." + parts[0], throwOnError: true)!)!;
        if (made is SetupWizard guide && parts.Length > 1) guide.ShowPage(parts[1]);
        if (made is Avalonia.Controls.Window own) return own;
        var view = (Avalonia.Controls.Control)made;
        view.GetType().GetMethod("Attach", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, [typeof(Shared)])?.Invoke(view, [new Shared()]);
        return new Avalonia.Controls.Window { Width = 970, Height = 700, Content = view };
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}
