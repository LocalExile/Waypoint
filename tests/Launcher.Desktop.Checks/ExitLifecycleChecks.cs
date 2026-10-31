using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Launcher.Core;
using Launcher.Desktop;

static class ExitLifecycleChecks
{
    static readonly BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
    static void Until(Func<bool> done)
    {
        var watch = Stopwatch.StartNew();
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(25) };
        timer.Tick += (_, _) => { if (done() || watch.Elapsed > TimeSpan.FromSeconds(25)) frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame); timer.Stop();
        if (!done()) throw new TimeoutException("Disposable exit lifecycle did not complete.");
    }
    public static void Run(Action<bool, string> check)
    {
        string originalLanguage = L.Current.Language;
        var originalAppearance = ((App)Application.Current).Appearance;
        string root = Path.Combine(Path.GetTempPath(), "Waypoint exit fixture " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, ".waypoint-test-fixture"), "Synthetic inventories only; never signal real processes.");
        foreach (string file in new[] { "GameServer.slnx", "scripts/dev/live.ps1", "src/GameServer.Core/GameServer.Core.csproj", "src/GameServer.Gateway/GameServer.Gateway.csproj" })
        {
            string path = Path.Combine(root, file); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "fixture");
        }
        try
        {
            foreach (var scenario in new[] { "disabled", "stopped", "failed", "game-cancel", "game-leave", "busy", "hide-busy", "always-ask" })
            {
                File.WriteAllText(Path.Combine(root, "state.json"), JsonSerializer.Serialize(new { Running = true, Game = scenario.StartsWith("game-"), FailStop = scenario == "failed" }));
                File.WriteAllText(Path.Combine(root, "calls.txt"), "");
                var window = new MainWindow();
                var settings = new LauncherSettings(root, false, CloseToTray: scenario == "hide-busy", CloseBehaviorConfirmed: true, StopServicesOnExit: scenario != "disabled", AlwaysAskOnClose: scenario == "always-ask");
                typeof(MainWindow).GetField("settings", fields)!.SetValue(window, settings);
                typeof(MainWindow).GetField("runtime", fields)!.SetValue(window, new RuntimeClient(Path.Combine(AppContext.BaseDirectory, "ExitFixtureBridge.ps1")));
                bool closed = false; window.Closed += (_, _) => closed = true;
                int closePrompts = 0;
                var answer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(25) };
                answer.Tick += (_, _) =>
                {
                    if (scenario == "always-ask" && Application.Current.Windows.OfType<CloseBehaviorDialog>().FirstOrDefault() is { } closeDialog)
                    { closePrompts++; closeDialog.DialogResult = false; }
                    if (Application.Current.Windows.OfType<ExitWithGameDialog>().FirstOrDefault() is { } dialog)
                        dialog.DialogResult = scenario == "game-leave";
                };
                try
                {
                    window.Show();
                    Until(() => typeof(MainWindow).GetField("snapshot", fields)!.GetValue(window) is RuntimeSnapshot &&
                        ((SemaphoreSlim)typeof(MainWindow).GetField("gate", fields)!.GetValue(window)!).CurrentCount == 1);
                    File.WriteAllText(Path.Combine(root, "calls.txt"), "");
                    if (scenario is "busy" or "hide-busy") typeof(MainWindow).GetField("busy", fields)!.SetValue(window, true);
                    answer.Start(); window.Close();
                    if (scenario == "always-ask")
                    {
                        Until(() => !(bool)typeof(MainWindow).GetField("decidingClose", fields)!.GetValue(window)!);
                        window.Close();
                        Until(() => !(bool)typeof(MainWindow).GetField("decidingClose", fields)!.GetValue(window)!);
                        check(closePrompts == 2, "Always ask presents a fresh prompt on repeated close attempts without changing preferences.");
                    }
                    if (scenario == "hide-busy") Until(() => !window.IsVisible);
                    if (scenario != "disabled") Until(() => closed || !(bool)typeof(MainWindow).GetField("preparingExit", fields)!.GetValue(window)!);
                    var actions = File.ReadAllLines(Path.Combine(root, "calls.txt"));
                    check(closed == (scenario is "disabled" or "stopped" or "game-leave"), $"Real WPF close lifecycle handles {scenario} without bypassing the exit decision.");
                    check(actions.Contains("Stop") == (scenario is "stopped" or "failed"), $"Exit scenario {scenario} sends only the authorized synthetic stop action.");
                    if (scenario == "hide-busy")
                    {
                        check(!window.IsVisible && (bool)typeof(MainWindow).GetField("busy", fields)!.GetValue(window)!, "Hiding preserves an active operation and does not trigger service shutdown.");
                        window.RestoreFromTray();
                        check(window.IsVisible, "The same hidden workspace can be restored while its operation remains active.");
                    }
                    else if (scenario == "busy") check(window.IsVisible, "Busy exit remains visible without starting service shutdown.");
                    else if (!closed)
                        check(window.IsVisible && !(bool)typeof(MainWindow).GetField("busy", fields)!.GetValue(window)!, $"Exit scenario {scenario} returns to a usable visible workspace.");
                }
                finally
                {
                    answer.Stop();
                    typeof(MainWindow).GetField("busy", fields)!.SetValue(window, false);
                    typeof(MainWindow).GetField("settings", fields)!.SetValue(window, settings with { StopServicesOnExit = false, CloseToTray = false, AlwaysAskOnClose = false });
                    if (!closed) window.Close();
                }
            }
        }
        finally
        {
            L.Current.SetLanguage(originalLanguage); ((App)Application.Current).ApplyAppearance(originalAppearance);
            Directory.Delete(root, true);
        }
    }
}
