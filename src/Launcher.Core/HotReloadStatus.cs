using System.Text.RegularExpressions;

namespace Launcher.Core;

public sealed record HotReloadStatus(string State, string Detail)
{
    internal static IEnumerable<string> Events(string output) => output.Split('\n').Select(s => s.TrimEnd('\r'))
        .Where(s => Regex.IsMatch(s, @"\A(?:\d{2}:\d{2}:\d{2}\.\d{3} )?\[gateway\] (?:hot reload|listening on )", RegexOptions.CultureInvariant));
    // Only Gateway's explicit confirmation is called Applied. Watch output or a successful build alone isn't proof.
    public static HotReloadStatus Read(string output, bool watched, bool running)
    {
        if (!running) return new("Stopped", "Start Gateway with hot reload enabled to apply compatible saved code edits in game.");
        if (!watched) return new("Disabled", "Gateway is running without a watcher. Stop servers to change launch mode.");
        var lines = Events(output).ToArray();
        string? line = lines.LastOrDefault();
        if (line is null) return new("Watching", "Waiting for saved source edits. Auth and Patch require a restart.");
        if (line.Contains("[gateway] listening on ", StringComparison.Ordinal))
            return lines.Length > 1 ? new("Gateway restarted", "Gateway began listening again after the previous reload event. Sign in again if the game returned to login.")
                : new("Watching", "Waiting for saved source edits. Auth and Patch require a restart.");
        string detail = line[(line.IndexOf("[gateway]", StringComparison.Ordinal) + 10)..].Trim();
        if (detail.Contains(" failed:", StringComparison.Ordinal)) return new("Compile failed", detail + " · Old code keeps running.");
        if (detail.Contains("needs a restart", StringComparison.Ordinal)) return new("Restart required", detail);
        if (detail.Contains(" applied", StringComparison.Ordinal)) return new("Applied", detail);
        if (detail.Contains("no code changes to apply", StringComparison.Ordinal)) return new("Unchanged", detail);
        if (detail.EndsWith(" saved", StringComparison.Ordinal)) return new("Pending", detail);
        return new("Watching", detail);
    }
}

public sealed class HotReloadTracker
{
    int watcher, child;
    string started = "", eventAtRestart = "";
    bool restarted;

    public HotReloadStatus Update(ServiceStatus? gateway, string output)
    {
        if (gateway is null || gateway.WatchProcessId == 0)
        {
            watcher = child = 0; started = eventAtRestart = ""; restarted = false;
            return HotReloadStatus.Read(output, false, gateway?.IsRunning == true);
        }
        string latest = HotReloadStatus.Events(output).LastOrDefault(line => line.Contains("[gateway] hot reload", StringComparison.Ordinal)) ?? "";
        if (watcher != gateway.WatchProcessId)
        {
            watcher = gateway.WatchProcessId; child = gateway.ProcessId; started = gateway.StartedUtc; restarted = false;
        }
        else if (gateway.ProcessId > 0 && child > 0 && (child != gateway.ProcessId || started != gateway.StartedUtc))
        {
            restarted = true; eventAtRestart = latest;
            child = gateway.ProcessId; started = gateway.StartedUtc;
        }
        else if (gateway.ProcessId > 0 && child == 0) { child = gateway.ProcessId; started = gateway.StartedUtc; }
        if (restarted && latest == eventAtRestart)
            return new("Gateway restarted", "Gateway started a new process under the same watcher. Sign in again if the game returned to login.");
        restarted = false;
        return HotReloadStatus.Read(output, true, true);
    }
}
