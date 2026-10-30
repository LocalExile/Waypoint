namespace GameServer.Launcher;

/// <summary>
/// The checkout the launcher drives. Like every tool here it works from the repository root (AGENTS.md): it accepts its
/// working directory or its own folder (the published LocalExileLauncher.exe sits in the root), never a parent folder.
/// </summary>
static class Repo
{
    public static bool IsRoot(string dir) =>
        File.Exists(Path.Combine(dir, "GameServer.slnx")) && File.Exists(Path.Combine(dir, LiveScript));

    /// <summary>The root among the working directory and the executable's folder, in that order; null for neither.</summary>
    public static string? Find(string workingDirectory, string exeDirectory) =>
        IsRoot(workingDirectory) ? workingDirectory : IsRoot(exeDirectory) ? exeDirectory : null;

    public static string LiveScript { get; } = Path.Combine("scripts", "dev", "live.ps1");
    public static string ExtractScript { get; } = Path.Combine("scripts", "dev", "extract-data.ps1");
    public static string ClientControl { get; } = Path.Combine("scripts", "dev", "client_control.py");
    public static string Logs { get; } = "logs";

    /// <summary>A server's build output, as live.ps1 runs it (Debug, net10.0).</summary>
    public static string ServerDll(string project) =>
        Path.Combine("src", $"GameServer.{project}", "bin", "Debug", "net10.0", $"GameServer.{project}.dll");

    /// <summary>The repository's virtual environment wins over the python on PATH, as in live.ps1.</summary>
    public static string Python => File.Exists(Path.Combine(".venv", "Scripts", "python.exe"))
        ? Path.GetFullPath(Path.Combine(".venv", "Scripts", "python.exe")) : "python";

    /// <summary>The client's own settings; GettingStarted step 10 needs networking_type=predictive there.</summary>
    public static string ClientConfig => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games", "Path of Exile", "production_Config.ini");
}
