using System.Collections;
using System.Globalization;
using System.Text.RegularExpressions;

namespace System.Management;

// The engine asks Windows about processes and listening ports with three WMI queries (ServiceMonitor, PortCheck). Here the
// same queries are answered from /proc and ss, in the same shape, so that its matching rules stay in one place. Names follow
// Windows': a native program is "<file>.exe" (dotnet.exe, GameServer.Gateway.exe), and a process under Wine, which shows its
// Windows path as its first argument, has that file's name and the Linux path of that exe.

public sealed class ManagementException(string message) : Exception(message);
public sealed class ManagementScope(string path) { public string Path { get; } = path; }
public sealed class ObjectQuery(string query) { public string Query { get; } = query; }

public sealed class ManagementBaseObject(Dictionary<string, object?> values) : IDisposable
{
    public object? this[string name] => values.GetValueOrDefault(name);
    public void Dispose() { }
}

public sealed class ManagementObjectCollection(List<ManagementBaseObject> items) : IEnumerable<ManagementBaseObject>, IDisposable
{
    public IEnumerator<ManagementBaseObject> GetEnumerator() => items.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => items.GetEnumerator();
    public void Dispose() { }
}

public sealed partial class ManagementObjectSearcher(string query) : IDisposable
{
    public ManagementObjectSearcher(ManagementScope scope, ObjectQuery query) : this(query.Query) { _ = scope; }

    public ManagementObjectCollection Get()
    {
        if (query.Contains("MSFT_NetTCPConnection", StringComparison.Ordinal)) return new(LinuxPorts.Listeners());
        if (!query.Contains("Win32_Process", StringComparison.Ordinal)) throw new ManagementException($"not answered on Linux: {query}");
        var processes = LinuxProcesses.All();
        return new(ProcessId().Match(query) is { Success: true } id ? processes.Where(p => (int)p["ProcessId"]! == int.Parse(id.Groups[1].Value, CultureInfo.InvariantCulture)).ToList() : processes);
    }

    public void Dispose() { }

    [GeneratedRegex(@"WHERE ProcessId = (\d+)")] private static partial Regex ProcessId();
}

public static class ManagementDateTimeConverter
{
    public static DateTime ToDateTime(string value) => DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}

static class LinuxPorts
{
    public static List<ManagementBaseObject> Listeners()
    {
        var found = new List<ManagementBaseObject>();
        using var ss = Diagnostics.Process.Start(new Diagnostics.ProcessStartInfo("ss", "-Hltnp") { RedirectStandardOutput = true, UseShellExecute = false })
            ?? throw new ManagementException("ss did not start");
        foreach (string line in ss.StandardOutput.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] columns = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (columns.Length < 4 || !int.TryParse(columns[3][(columns[3].LastIndexOf(':') + 1)..], out int port)) continue;
            int at = line.IndexOf("pid=", StringComparison.Ordinal);
            int pid = at < 0 ? 0 : int.Parse(new string(line[(at + 4)..].TakeWhile(char.IsAsciiDigit).ToArray()), CultureInfo.InvariantCulture);
            found.Add(new(new() { ["LocalPort"] = port, ["OwningProcess"] = pid }));
        }
        ss.WaitForExit();
        return found;
    }
}

static class LinuxProcesses
{
    public static List<ManagementBaseObject> All()
    {
        var found = new List<ManagementBaseObject>();
        long boot = long.Parse(File.ReadLines("/proc/stat").First(l => l.StartsWith("btime ", StringComparison.Ordinal))[6..], CultureInfo.InvariantCulture);
        foreach (string folder in Directory.EnumerateDirectories("/proc"))
        {
            string id = Path.GetFileName(folder);
            if (!id.All(char.IsAsciiDigit)) continue;
            string stat; string[] arguments;
            // A process can end at any point here.
            try
            {
                stat = File.ReadAllText(folder + "/stat");
                arguments = File.ReadAllText(folder + "/cmdline").Split('\0', StringSplitOptions.RemoveEmptyEntries);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            if (arguments.Length == 0) continue; // a kernel thread, or one that has ended
            // After the name in parentheses, which may hold spaces: state, parent, ... and the start time in 100ths of a second.
            string[] fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
            string name, path;
            if (WindowsPath().IsMatch(arguments[0]))
            {
                name = arguments[0].Split('\\', '/')[^1];
                path = UnderWine(arguments[0], folder);
            }
            else
            {
                try { path = new FileInfo(folder + "/exe").ResolveLinkTarget(false)?.FullName ?? arguments[0]; }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { path = arguments[0]; }
                name = Path.GetFileName(path) + ".exe";
            }
            found.Add(new(new()
            {
                ["ProcessId"] = int.Parse(id, CultureInfo.InvariantCulture), ["ParentProcessId"] = int.Parse(fields[1], CultureInfo.InvariantCulture), ["Name"] = name,
                ["ExecutablePath"] = path, ["CommandLine"] = string.Join(' ', arguments),
                ["CreationDate"] = DateTimeOffset.FromUnixTimeMilliseconds(boot * 1000 + long.Parse(fields[19], CultureInfo.InvariantCulture) * 10).UtcDateTime.ToString("o", CultureInfo.InvariantCulture),
            }));
        }
        return found;
    }

    /// <summary>The Linux path of a Windows path. Proton picks the drive letters by where the game is, so the letter is read
    /// from that process's own prefix.</summary>
    static string UnderWine(string path, string folder)
    {
        if (DrivePath().Match(path) is not { Success: true } match) return path;
        try
        {
            string? prefix = File.ReadAllText(folder + "/environ").Split('\0').FirstOrDefault(v => v.StartsWith("WINEPREFIX=", StringComparison.Ordinal));
            if (prefix is null) return path;
            var drive = new DirectoryInfo($"{prefix[11..].TrimEnd('/')}/dosdevices/{char.ToLowerInvariant(match.Groups[1].Value[0])}:").ResolveLinkTarget(false);
            return drive is null ? path : drive.FullName.TrimEnd('/') + match.Groups[2].Value.Replace('\\', '/');
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return path; }
    }

    static Regex WindowsPath() => new(@"^[A-Za-z]:\\|\.exe$", RegexOptions.IgnoreCase);
    static Regex DrivePath() => new(@"^(?:\\\\\?\\)?([A-Za-z]):(\\.*)$");
}
