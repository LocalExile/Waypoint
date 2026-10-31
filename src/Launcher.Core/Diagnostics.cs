using System.Text;
using System.Text.RegularExpressions;

namespace Launcher.Core;

public static class LogReader
{
    public static string Tail(string path, int maximumBytes = 65_536)
    {
        if (!File.Exists(path)) return "No log file yet.";
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            stream.Seek(Math.Max(0, stream.Length - maximumBytes), SeekOrigin.Begin);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return "Could not read log: " + ex.Message; }
    }

    public static string Filter(string text, string search, bool warningsOnly)
    {
        return string.Join(Environment.NewLine, text.Split('\n').Where(line =>
            (search.Length == 0 || line.Contains(search, StringComparison.OrdinalIgnoreCase)) &&
            (!warningsOnly || Regex.IsMatch(line, @"warn|error|exception|failed|fatal|\[!\]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))));
    }

    public static string GameLog(string root)
    {
        string directory = Path.Combine(root, "Client", "logs");
        // Fixed allowed names only: never read arbitrary file IDs or recursively collect the client directory.
        return new[] { "Client.txt", "LatestClient.txt" }.Select(name => Path.Combine(directory, name))
            .Where(File.Exists).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault() ?? Path.Combine(directory, "Client.txt");
    }
}

public static class DiagnosticReport
{
    static readonly RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    public static string Redact(string text, IEnumerable<string>? privateNames = null, IEnumerable<string>? privatePaths = null)
    {
        var detectedNames = Regex.Matches(text, @"[""']?\b(?:account|accountname|username|character|charactername|name)[""']?\s*[:=]\s*(?:[""'](?<name>[^""'\r\n]+)[""']|(?<name>[A-Za-z0-9_-]+))", Options)
            .Select(m => m.Groups["name"].Value).Where(n => n.Length >= 3).ToArray();
        foreach (string path in (privatePaths ?? []).Append(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))
                     .Where(p => !string.IsNullOrWhiteSpace(p)).Distinct().OrderByDescending(p => p.Length))
        {
            text = text.Replace(path, "<private-path>", StringComparison.OrdinalIgnoreCase)
                .Replace(path.Replace('\\', '/'), "<private-path>", StringComparison.OrdinalIgnoreCase)
                .Replace(path.Replace("\\", "\\\\"), "<private-path>", StringComparison.OrdinalIgnoreCase);
        }
        // Include Windows home paths belonging to other machines as well as this machine's current profile.
        text = Regex.Replace(text, @"\b[A-Z]:[\\/]+Users[\\/]+[^\\/\s""']+", "<user-home>", Options);
        text = Regex.Replace(text, @"(?<![\w.-])[\w.+-]+@[\w.-]+\.[A-Za-z]{2,}(?![\w.-])", "<email>", Options);
        // Entire labelled values, including quoted strings, whitespace-bearing credentials and JSON values.
        text = Regex.Replace(text,
            @"(?<key>[""']?\b(?:password|passwd|passworddigest|clientdigesthash|passwordhash|passwordsalt|token|ticket|secret|authorization|credential|credentials|key64|machineida|machineidb|account|accountname|username|runas\s*user|machine|character|charactername|name)[""']?\s*[:=]\s*)(?:""(?:\\.|[^""\\\r\n])*""|'(?:\\.|[^'\\\r\n])*'|[^,;\r\n}]+)",
            "${key}<redacted>", Options);
        text = Regex.Replace(text, @"\bBearer\s+[^\s,;""']+", "Bearer <redacted>", Options);
        text = Regex.Replace(text, @"(?<![A-Za-z0-9])[A-Fa-f0-9]{32,}(?![A-Za-z0-9])", "<redacted-hex>", RegexOptions.CultureInvariant);
        foreach (string name in (privateNames ?? []).Concat(detectedNames).Append(Environment.UserName).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().OrderByDescending(n => n.Length))
            text = Regex.Replace(text, @"(?<![\w])" + Regex.Escape(name) + @"(?![\w])", "<name>", Options);
        return text;
    }

    public static string Create(RuntimeSnapshot state, string launcherVersion, string operationText, string? failure,
        string sourceLabel, string selectedLog, IEnumerable<string>? privateNames = null)
    {
        // Only caller-selected log text and in-memory operation output are included. Never enumerate saves,
        // account JSON, generated keys, credentials, settings dumps or whole source/game directories.
        var text = new StringBuilder();
        text.AppendLine("LocalExile launcher diagnostic report").AppendLine($"Launcher: {launcherVersion}")
            .AppendLine($"OS: {Environment.OSVersion}; .NET: {Environment.Version}")
            .AppendLine($"Launch profile: {state.Profile}")
            .AppendLine($"Source: {state.SourceBranch} {state.SourceCommit}")
            .AppendLine($"Built: {state.BuildCommit}")
            .AppendLine($"Setup: {(state.SetupReady ? "ready" : "incomplete")}");
        foreach (var service in state.Services) text.AppendLine($"{service.Name}: {service.Status}; PID {service.ProcessId}");
        foreach (var requirement in state.Requirements.Where(r => !r.Ready)) text.AppendLine("Missing: " + requirement.Name);
        foreach (var conflict in state.Conflicts) text.AppendLine("Conflict: " + conflict);
        if (failure is not null) text.AppendLine("Last failure: " + failure);
        text.AppendLine().AppendLine("RECENT OPERATIONS").AppendLine(LastLines(operationText, 100))
            .AppendLine().AppendLine("SELECTED LOG: " + sourceLabel).AppendLine(LastLines(selectedLog, 200));
        string report = Redact(text.ToString(), privateNames, [state.Root, state.SaveDatabase]);
        // Commit hashes are useful public build identifiers, added from validated fields after log redaction.
        if (Regex.IsMatch(state.SourceCommit, @"\A[a-fA-F0-9]{40}\z")) report += "\nSource commit: " + state.SourceCommit + "\n";
        foreach (Match component in Regex.Matches(state.BuildCommit, @"\b(Auth|Patch|Gateway)=([0-9]+\.[0-9]+\.[0-9]+(?:\.[0-9]+)?\+[a-fA-F0-9]{40})(?=;|$)"))
            report += "Built " + component.Groups[1].Value + ": " + component.Groups[2].Value + "\n";
        return report;
    }

    static string LastLines(string text, int count) => string.Join(Environment.NewLine, text.Split('\n').TakeLast(count));
}
