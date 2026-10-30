using System.Diagnostics;
using System.Text.Json;

namespace GameServer.Launcher;

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
        if (!File.Exists(dll)) throw new FileNotFoundException($"{dll} is not built yet: start the servers once (live.ps1 builds it)");
        DateTime built = File.GetLastWriteTimeUtc(dll);
        var info = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        info.ArgumentList.Add(dll);
        info.ArgumentList.Add("commands");
        using var process = Process.Start(info)!;
        string output = await process.StandardOutput.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        if (process.ExitCode != 0) throw new InvalidOperationException($"GameServer.Gateway commands exited with {process.ExitCode}");
        return new(Parse(output), built);
    }

    /// <summary>One {"name", "usage", "args"} object per line ("args" may be missing).</summary>
    internal static List<ChatCommand> Parse(string jsonLines) =>
        [.. JsonObjects(jsonLines)
            .Select(e => new ChatCommand(e.GetProperty("name").GetString()!, e.GetProperty("usage").GetString()!,
                e.TryGetProperty("args", out var a) ? [.. a.EnumerateArray().Select(k => k.GetString()!)] : []))];

    /// <summary>The values of an argument kind (<c>GameServer.Gateway values &lt;kind&gt;</c>).</summary>
    public static async Task<IReadOnlyList<ArgumentValue>> ValuesAsync(string kind)
    {
        var info = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        foreach (string a in (string[])[Repo.ServerDll("Gateway"), "values", kind]) info.ArgumentList.Add(a);
        using var process = Process.Start(info)!;
        string output = await process.StandardOutput.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(60));
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        if (process.ExitCode != 0) throw new InvalidOperationException($"GameServer.Gateway values {kind} exited with {process.ExitCode}");
        return ParseValues(output);
    }

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
