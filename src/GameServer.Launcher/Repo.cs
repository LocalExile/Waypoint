namespace GameServer.Launcher;

/// <summary>
/// The LocalExile server checkout the launcher drives. Its tools work from that checkout's root (its AGENTS.md), so the
/// launcher makes the root its working directory at startup and every path here is relative to it. Which checkout is
/// the player's choice, kept in the settings; the launcher never searches for one.
/// </summary>
static class Repo
{
    /// <summary>A server checkout: its solution and live.ps1 are there.</summary>
    public static bool IsRoot(string? dir) =>
        !string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, "GameServer.slnx")) && File.Exists(Path.Combine(dir, LiveScript));

    /// <summary>The checkout to drive: the saved one while it still is one, else the working directory when it is one
    /// (`dotnet run` from a server checkout); null when the player has to choose.</summary>
    public static string? Find(string? saved, string workingDirectory) =>
        IsRoot(saved) ? Path.GetFullPath(saved!) : IsRoot(workingDirectory) ? workingDirectory : null;

    /// <summary>Where the folder picker starts: a "LocalExile" folder beside the launcher's own repository when it is a
    /// checkout (the usual layout, LocalExile\ and LocalExile-Launcher\ side by side), else the launcher's folder.</summary>
    public static string Suggested(string launcherFolder)
    {
        for (var dir = new DirectoryInfo(launcherFolder); dir?.Parent is not null; dir = dir.Parent)
            if (Path.Combine(dir.Parent.FullName, "LocalExile") is var sibling && IsRoot(sibling)) return sibling;
        return launcherFolder;
    }

    /// <summary>The checkout's branch, from its HEAD file (no git process): a short commit when detached, null when the
    /// checkout has none (not a git checkout, or unreadable).</summary>
    public static string? Branch()
    {
        try
        {
            // A worktree's .git is a file naming its git directory.
            string git = ".git";
            if (File.Exists(git) && File.ReadAllText(git).Trim() is var link && link.StartsWith("gitdir:", StringComparison.Ordinal))
                git = link["gitdir:".Length..].Trim();
            string head = Path.Combine(git, "HEAD");
            return File.Exists(head) ? BranchOf(File.ReadAllText(head)) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    internal static string? BranchOf(string head)
    {
        head = head.Trim();
        const string Ref = "ref: refs/heads/";
        if (head.StartsWith(Ref, StringComparison.Ordinal)) return head[Ref.Length..];
        return head.Length >= 7 && head.All(Uri.IsHexDigit) ? head[..7] : null;
    }

    public static string LiveScript { get; } = Path.Combine("scripts", "dev", "live.ps1");
    public static string ExtractScript { get; } = Path.Combine("scripts", "dev", "extract-data.ps1");
    public static string ClientControl { get; } = Path.Combine("scripts", "dev", "client_control.py");
    public static string Logs { get; } = "logs";

    /// <summary>A server's build output, as live.ps1 runs it (Debug, net10.0).</summary>
    public static string ServerDll(string project) =>
        Path.Combine("src", $"GameServer.{project}", "bin", "Debug", "net10.0", $"GameServer.{project}.dll");

    /// <summary>The checkout's virtual environment wins over the python on PATH, as in live.ps1.</summary>
    public static string Python => File.Exists(Path.Combine(".venv", "Scripts", "python.exe"))
        ? Path.GetFullPath(Path.Combine(".venv", "Scripts", "python.exe")) : "python";

    /// <summary>The client's own settings; GettingStarted step 10 needs networking_type=predictive there.</summary>
    public static string ClientConfig => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games", "Path of Exile", "production_Config.ini");
}
