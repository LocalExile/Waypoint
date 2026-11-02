using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless;
using Avalonia.Markup.Xaml;
using Avalonia.Media;

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
            desktop.MainWindow = string.IsNullOrEmpty(Repo.Find(Environment.CurrentDirectory, Environment.CurrentDirectory)) ? new FirstStartWindow() : new MainWindow();
            desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnMainWindowClose;
        }
        base.OnFrameworkInitializationCompleted();
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

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length > 0 && Directory.Exists(args[0])) Checkout = Path.GetFullPath(args[0]);
        if (args.Length >= 3 && args[1] == "--render") return Render(args[2], args.Length > 3 ? args[3] : null);
        return AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>The window, or one section by its type (`Waypoint &lt;checkout&gt; --render out.png PlayTab`), as a picture with no
    /// display: how the interface is checked without opening anything on the desktop.</summary>
    static int Render(string file, string? section)
    {
        var app = AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
        ((App)app.Instance!).Prepare();
        Avalonia.Controls.Window window = section is null ? new MainWindow() : Hosting(section);
        window.Show();
        for (int i = 0; i < 20; i++) { Avalonia.Threading.Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }
#pragma warning disable CS0618
        window.CaptureRenderedFrame()?.Save(file);
#pragma warning restore CS0618
        return 0;
    }

    static Avalonia.Controls.Window Hosting(string section)
    {
        var view = (Avalonia.Controls.Control)Activator.CreateInstance(typeof(Program).Assembly.GetType("Waypoint." + section, throwOnError: true)!)!;
        view.GetType().GetMethod("Attach", [typeof(Shared)])?.Invoke(view, [new Shared()]);
        return new Avalonia.Controls.Window { Width = 970, Height = 700, Content = view };
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}
