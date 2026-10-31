using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Threading;
using Launcher.Core;

namespace Launcher.Desktop;

public partial class App
{
    AppInstance? desktopInstance;
    public string? StartupRepository { get; private set; }
    public bool AllowMultipleInstances { get; private set; }

    async Task StartDesktopAsync(string[] arguments)
    {
        try
        {
            LoadStartupPresentation();
            var options = AppStartupOptions.Parse(arguments, Environment.CurrentDirectory);
            StartupRepository = options.RepositoryPath; AllowMultipleInstances = options.AllowMultiple;
            if (options.AllowMultiple) { ShowWorkspace(); return; }
            var identity = ReadIdentity();
            desktopInstance = new(identity);
            if (!desktopInstance.IsPrimary)
            {
                AppActivationReply reply;
                try { reply = await desktopInstance.ActivateAsync(new(identity, options.RepositoryPath), WindowActivation.Allow); }
                catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException or UnauthorizedAccessException)
                {
                    // The first app may have exited while we connected. Claim ownership once more; never
                    // open another workspace while the existing owner is merely slow or inaccessible.
                    desktopInstance.Dispose(); desktopInstance = new(identity);
                    if (desktopInstance.IsPrimary) { StartPrimary(identity); return; }
                    throw new IOException("The running Waypoint could not be contacted. Close it normally and try again. Use the same Windows permission level for both copies.", ex);
                }
                desktopInstance.Dispose(); desktopInstance = null;
                if (reply.Status == AppActivationStatus.Focused) { Shutdown(); return; }
                string detail = reply.Status switch
                {
                    AppActivationStatus.DifferentBuild => L.Text("Another Waypoint build is already open. Close that window normally before opening this build. Existing operations and services have not been stopped."),
                    AppActivationStatus.DifferentCheckout => L.Text("Waypoint is already managing a checkout. Use Change location in the running window when you are ready. This launch has not changed its checkout or settings."),
                    _ => L.Text("Waypoint is closing. Wait for it to finish, then open it again.")
                };
                if (reply.Busy) detail += "\n\n" + L.Text("The running window has an operation in progress. Let it finish before closing or changing checkout.");
                ShowStartupNotice(detail, reply.Running, identity, reply.RepositoryPath, options.RepositoryPath);
                return;
            }
            StartPrimary(identity);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or Win32Exception or System.Security.SecurityException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            desktopInstance?.Dispose(); desktopInstance = null;
            ShowStartupNotice(L.Text(ex.Message), null, null, "", null, true);
        }
    }
    void StartPrimary(AppInstanceIdentity identity)
    {
        var legacy = LegacyAppInstances.Find().FirstOrDefault();
        if (legacy is not null)
        {
            desktopInstance?.Dispose(); desktopInstance = null;
            WindowActivation.FocusProcess(legacy.ProcessId);
            ShowStartupNotice(L.Text("An older Waypoint without single-instance support is already open. Close it normally before opening this build. No process has been stopped."), legacy, identity, "", StartupRepository);
            return;
        }
        desktopInstance!.StartListening((request, token) => Dispatcher.InvokeAsync(() =>
        {
            if (MainWindow is not MainWindow workspace || !workspace.IsLoaded)
                return new AppActivationReply(AppActivationStatus.Starting, identity);
            return workspace.ActivateFromLaunch(identity, request);
        }, DispatcherPriority.Normal, token).Task);
        ShowWorkspace();
    }
    void ShowWorkspace() { MainWindow = new MainWindow(); MainWindow.Show(); }
    static AppInstanceIdentity ReadIdentity()
    {
        using var brand = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "brand.json")));
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(File.ReadAllBytes(typeof(App).Assembly.Location));
        hash.AppendData(File.ReadAllBytes(typeof(AppInstance).Assembly.Location));
        return new(Environment.ProcessId, brand.RootElement.GetProperty("version").GetString() ?? "unknown",
            (typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown") +
                " / " + Convert.ToHexStringLower(hash.GetHashAndReset()),
            Environment.ProcessPath ?? typeof(App).Assembly.Location);
    }
    void LoadStartupPresentation()
    {
        try { var preferences = SettingsStore.Load(); L.Current.SetLanguage(preferences.Language); ApplyAppearance(preferences.Appearance); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
    }
    void ShowStartupNotice(string message, AppInstanceIdentity? running, AppInstanceIdentity? requested, string repository, string? requestedRepository, bool error = false)
    {
        var notice = new StartupNoticeDialog(null, message, running, requested, repository, requestedRepository, error);
        MainWindow = notice; notice.Show();
    }
}

public partial class MainWindow
{
    public AppActivationReply ActivateFromLaunch(AppInstanceIdentity identity, AppLaunchRequest request)
    {
        var reply = AppActivationPolicy.Evaluate(identity, request, settings.RepositoryPath, busy, closing);
        if (!closing) WindowActivation.Focus(this);
        return reply;
    }
}

/// <summary>A secondary launch may explain a conflict, but never constructs a workspace or changes settings.</summary>
public sealed class StartupNoticeDialog : DialogWindow
{
    public StartupNoticeDialog(Window? owner, string message, AppInstanceIdentity? running = null,
        AppInstanceIdentity? requested = null, string repository = "", string? requestedRepository = null, bool error = false)
    {
        string title = L.Text(error ? "Could not open Waypoint" : "Waypoint is already open");
        if (owner is not null) DialogLayout.Configure(this, owner, title);
        else
        {
            Title = title; Width = 680; MinWidth = 420; MinHeight = 220; MaxHeight = Math.Min(700, SystemParameters.WorkArea.Height - 40);
            SizeToContent = SizeToContent.Height; WindowStartupLocation = WindowStartupLocation.CenterScreen; ShowInTaskbar = true;
            Language = XmlLanguage.GetLanguage(L.Current.Culture.Name);
        }
        var body = new StackPanel { Margin = new Thickness(24) };
        body.Children.Add(new TextBlock { Text = title, FontSize = ThemeResources.Font(22), FontWeight = FontWeights.SemiBold });
        body.Children.Add(new TextBlock { Text = message, Margin = new Thickness(0, 14, 0, 12) });
        void Detail(string label, string value)
        {
            if (value.Length == 0) return;
            body.Children.Add(new TextBlock { Text = L.Text(label), Margin = new Thickness(0, 8, 0, 3), FontWeight = FontWeights.SemiBold });
            body.Children.Add(new CommitText { Value = value });
        }
        if (running is not null)
        {
            Detail("Running build", BuildLabel(running));
            Detail("Running executable", running.ExecutablePath);
        }
        if (requested is not null) Detail("Requested build", BuildLabel(requested));
        Detail("Current checkout", repository);
        if (requestedRepository is not null) Detail("Requested checkout", requestedRepository);
        var buttons = new WrapPanel { Margin = new Thickness(0, 18, 0, 0) };
        if (running is not null)
        {
            var show = new Button { Content = L.Text("Show running window") };
            show.Click += (_, _) => { WindowActivation.FocusProcess(running.ProcessId); Close(); }; buttons.Children.Add(show);
        }
        var close = new Button { Content = L.Text("Close"), IsCancel = true, IsDefault = true };
        close.Click += (_, _) => Close(); buttons.Children.Add(close); body.Children.Add(buttons);
        Content = DialogLayout.WithActions(body, buttons); Loaded += (_, _) => close.Focus();
    }
    static string BuildLabel(AppInstanceIdentity identity) => identity.Version + " · " +
        (identity.Build.StartsWith(identity.Version + "+", StringComparison.Ordinal) ? identity.Build[(identity.Version.Length + 1)..] : identity.Build);
}
