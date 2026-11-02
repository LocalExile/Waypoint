namespace Launcher.Core;

/// <summary>What differs between the systems Waypoint runs on. On Windows each answer is what the code used before Linux:
/// Linux has its own front end (src/Waypoint.Linux) over the same engine.</summary>
public static class Platform
{
    /// <summary>Windows PowerShell on Windows, which live.ps1 and the adapter are written for; PowerShell 7 elsewhere, found on
    /// PATH or where `dotnet tool install --global PowerShell` puts it, which PATH often lacks.</summary>
    public static string PowerShell => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe")
        : OnPath("pwsh") ?? DotnetTool("pwsh") ?? "pwsh";

    /// <summary>Where a user's data goes. Outside Windows the folder may not exist yet, and .NET then answers with nothing, which
    /// would put settings in the working directory: the checkout.</summary>
    public static string LocalData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData,
        OperatingSystem.IsWindows() ? Environment.SpecialFolderOption.None : Environment.SpecialFolderOption.Create);

    /// <summary>The Python on PATH. Some Linux systems only have python3.</summary>
    public static string Python => OperatingSystem.IsWindows() || OnPath("python") is not null ? "python" : "python3";

    /// <summary>A virtual environment's Python.</summary>
    public static string VenvPython(string root) => OperatingSystem.IsWindows()
        ? Path.Combine(root, ".venv", "Scripts", "python.exe") : Path.Combine(root, ".venv", "bin", "python");

    /// <summary>A file on PATH; null when there is none.</summary>
    public static string? OnPath(string file)
    {
        foreach (string folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try { if (Path.Combine(folder.Trim('"'), file) is var path && File.Exists(path)) return path; }
            catch (ArgumentException) { }
        }
        return null;
    }

    static string? DotnetTool(string file) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "tools", file) is var path && File.Exists(path) ? path : null;
}
