using System.Text;

namespace Waypoint;

/// <summary>
/// What a user gives when someone asks for their logs: one redacted text with Waypoint's own log (the file on disk, so
/// earlier sessions and the setup guide are in it), the recent operations and the newest lines of every server log and
/// the game's. It is previewed, then copied or saved as one file to send (<see cref="Save"/>); nothing is sent by
/// Waypoint. The header's button, a failure's dialog and the Logs section all open the same report.
/// </summary>
static class SupportReport
{
    /// <summary>Where saved reports go: a folder of Waypoint's own, shown to the user after a save.</summary>
    public static string Folder => Path.Combine(Settings.Home, "reports");

    /// <summary>Where Waypoint's own log is written.</summary>
    public static string LogFolder => Path.Combine(Settings.Home, "logs");

    /// <summary>The report's text, made again for the names the user adds in the preview.</summary>
    public static Func<string[], string> For(Shared shared) => additional => Create(shared, additional);

    public static string Create(Shared shared, IEnumerable<string> additionalNames)
    {
        var workspace = shared.Workspace;
        var state = workspace.Latest ?? new RuntimeSnapshot(Environment.CurrentDirectory, "Unavailable", "", "", "", "",
            [new("Installation inspection", false, "Select and check a valid checkout first.")], [], []);
        var logs = new List<(string Heading, string Text, int Lines)>();
        // The file holds the launcher's lines of this session and the ones before it, already redacted.
        if (SessionLog.Current is { } disk) logs.Add(($"WAYPOINT LOG ({SessionLog.Name})", LogReader.Tail(disk.File, 200_000), 400));
        foreach (var source in shared.Logs.Sources)
        {
            if (source == shared.Logs.Launcher && SessionLog.Current is not null) continue;
            var lines = shared.Logs.Snapshot(source);
            if (lines.Count > 0) logs.Add(("LOG: " + source.Name, string.Join('\n', lines.Select(l => l.Text)), 150));
        }
        return DiagnosticReport.Create(state, Updates.Current.ToString(), workspace.OperationText, workspace.LastFailure, logs,
            workspace.PrivateNames.Concat(additionalNames), Details(shared.Settings.Advanced));
    }

    /// <summary>The head's lines about this LocalExile: a release and its version or a source checkout, and Waypoint's mode.</summary>
    public static string[] Details(bool advanced) =>
    [
        "LocalExile: " + (Repo.IsInstall ? "release " + (Repo.ClientVersion?.ToString() ?? "of unknown version")
            : "source checkout" + (Repo.ClientVersion is { } version ? $" ({version})" : "")),
        "Mode: " + (advanced ? "Advanced" : "Simple"),
    ];

    /// <summary>A saved report's name: sortable, and unlike the one before it.</summary>
    public static string FileName(DateTime now) => $"waypoint-report-{now:yyyyMMdd-HHmmss}.txt";

    /// <summary>Writes the reviewed text to a file in <paramref name="folder"/> and returns its path.</summary>
    public static string Save(string folder, string text, DateTime now)
    {
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, FileName(now));
        File.WriteAllText(path, text, new UTF8Encoding(false));
        return path;
    }
}
