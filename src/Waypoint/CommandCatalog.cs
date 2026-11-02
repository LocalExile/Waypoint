using System.Diagnostics;
using System.Text.Json;

namespace Waypoint;

/// <param name="Args">What the leading arguments name (area, monster, item …), for their suggestions.</param>
sealed record ChatCommand(string Name, string Usage, IReadOnlyList<string> Args)
{
    public ChatCommand(string name, string usage) : this(name, usage, []) { }

    /// <summary>The usage without its " (explanation)", as /commands lists it.</summary>
    public string Short => Usage.IndexOf(" (", StringComparison.Ordinal) is int i and >= 0 ? Usage[..i] : Usage;
    public string Explanation => Usage.IndexOf(" (", StringComparison.Ordinal) is int i and >= 0 ? Usage[(i + 2)..].TrimEnd(')') : "";
}

/// <summary>The Gateway's chat commands, from the Gateway itself (<c>GameServer.Gateway commands</c>), so the console's
/// suggestions are the commands of the build that runs.</summary>
sealed class CommandCatalog(IReadOnlyList<ChatCommand> commands, DateTime built = default)
{
    public IReadOnlyList<ChatCommand> All { get; } = commands;

    /// <summary>When the Gateway the list came from was built.</summary>
    public DateTime Built { get; } = built;

    /// <summary>The Gateway was built again since (live.ps1 rebuilds it on Play or Restart Gateway): its commands and
    /// argument values may have changed.</summary>
    public bool Stale => File.Exists(Repo.ServerDll("Gateway")) && File.GetLastWriteTimeUtc(Repo.ServerDll("Gateway")) != Built;

    public static async Task<CommandCatalog> LoadAsync()
    {
        string dll = Repo.ServerDll("Gateway");
        if (!File.Exists(dll)) throw new FileNotFoundException(L.Text("{0} is not built yet: start the servers once (live.ps1 builds it)", dll));
        DateTime built = File.GetLastWriteTimeUtc(dll);
        return new(Parse(await AskGateway(TimeSpan.FromSeconds(30), dll, "commands")), built);
    }

    /// <summary>Runs the built Gateway in one of its listing modes and returns what it printed. The process never outlives
    /// the question, whatever it was doing.</summary>
    internal static async Task<string> AskGateway(TimeSpan timeout, params string[] arguments)
    {
        if (!Repo.IsInstall && !Repo.HasLauncherHooks())
            throw new InvalidOperationException(L.Text("This checkout is older than the launcher hooks, so its Gateway cannot list commands: pull the server's main branch."));
        // A release's Gateway is its own executable.
#if WINDOWS
        bool program = arguments[0].EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
#else
        bool program = !arguments[0].EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
#endif
        var info = new ProcessStartInfo(program ? Path.GetFullPath(arguments[0]) : "dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        foreach (string a in program ? arguments.Skip(1) : arguments) info.ArgumentList.Add(a);
        using var process = Process.Start(info)!;
        try
        {
            string output = await process.StandardOutput.ReadToEndAsync().WaitAsync(timeout);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            if (process.ExitCode != 0) throw new InvalidOperationException(L.Text("GameServer.Gateway {0} exited with {1}", arguments[1], process.ExitCode));
            return output;
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }

    /// <summary>One {"name", "usage", "args"} object per line ("args" may be missing).</summary>
    internal static List<ChatCommand> Parse(string jsonLines) =>
        [.. JsonObjects(jsonLines)
            .Select(e => new ChatCommand(e.GetProperty("name").GetString()!, e.GetProperty("usage").GetString()!,
                e.TryGetProperty("args", out var a) ? [.. a.EnumerateArray().Select(k => k.GetString()!)] : []))];

    /// <summary>The values of an argument kind (<c>GameServer.Gateway values &lt;kind&gt;</c>).</summary>
    public static async Task<IReadOnlyList<ArgumentValue>> ValuesAsync(string kind) =>
        ParseValues(await AskGateway(TimeSpan.FromSeconds(60), Repo.ServerDll("Gateway"), "values", kind));

    /// <summary>One {"value", "detail"} object per line.</summary>
    internal static List<ArgumentValue> ParseValues(string jsonLines) =>
        [.. JsonObjects(jsonLines)
            .Select(e => new ArgumentValue(e.GetProperty("value").GetString()!, e.GetProperty("detail").GetString() ?? ""))];

    /// <summary>The output's JSON objects, one per line. The other lines are the Gateway's own log ("[gateway] atlas: …"),
    /// which the catalogs it loads write as they do in a running server.</summary>
    static IEnumerable<JsonElement> JsonObjects(string lines) =>
        lines.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => l.StartsWith('{')).Select(l => JsonSerializer.Deserialize<JsonElement>(l));

    /// <summary>While the first word is typed ("/it"): the commands that start with it, then those that contain it.</summary>
    public IReadOnlyList<ChatCommand> Suggest(string text)
    {
        if (!text.StartsWith('/') || text.Contains(' ')) return [];
        string typed = text[1..];
        return [.. All.Where(c => c.Name.StartsWith(typed, StringComparison.OrdinalIgnoreCase))
            .Concat(All.Where(c => !c.Name.StartsWith(typed, StringComparison.OrdinalIgnoreCase)
                && c.Name.Contains(typed, StringComparison.OrdinalIgnoreCase)))];
    }

    /// <summary>The command a line runs ("/item Iron Ring" → item), if it is one of these.</summary>
    public ChatCommand? Of(string text)
    {
        if (!text.StartsWith('/')) return null;
        string name = text[1..].Split(' ', 2)[0];
        if (name.Equals("com", StringComparison.OrdinalIgnoreCase)) name = "commands";
        return All.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }
}
