using System.Diagnostics;
using System.Text.Json;

namespace GameServer.Launcher;

sealed record ChatCommand(string Name, string Usage)
{
    /// <summary>The usage without its " (explanation)", as /commands lists it.</summary>
    public string Short => Usage.IndexOf(" (", StringComparison.Ordinal) is int i and >= 0 ? Usage[..i] : Usage;
    public string Explanation => Usage.IndexOf(" (", StringComparison.Ordinal) is int i and >= 0 ? Usage[(i + 2)..].TrimEnd(')') : "";
}

/// <summary>The Gateway's chat commands, from the Gateway itself (<c>GameServer.Gateway commands</c>), so the console's
/// suggestions are the commands of the build that runs.</summary>
sealed class CommandCatalog(IReadOnlyList<ChatCommand> commands)
{
    public IReadOnlyList<ChatCommand> All { get; } = commands;

    public static async Task<CommandCatalog> LoadAsync()
    {
        string dll = Repo.ServerDll("Gateway");
        if (!File.Exists(dll)) throw new FileNotFoundException($"{dll} is not built yet: start the servers once (live.ps1 builds it)");
        var info = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        info.ArgumentList.Add(dll);
        info.ArgumentList.Add("commands");
        using var process = Process.Start(info)!;
        string output = await process.StandardOutput.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        if (process.ExitCode != 0) throw new InvalidOperationException($"GameServer.Gateway commands exited with {process.ExitCode}");
        return new(Parse(output));
    }

    /// <summary>One {"name", "usage"} object per line.</summary>
    internal static List<ChatCommand> Parse(string jsonLines) =>
        [.. jsonLines.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => JsonSerializer.Deserialize<JsonElement>(l))
            .Select(e => new ChatCommand(e.GetProperty("name").GetString()!, e.GetProperty("usage").GetString()!))];

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
