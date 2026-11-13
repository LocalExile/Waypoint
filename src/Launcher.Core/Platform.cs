namespace Launcher.Core;

/// <summary>What differs between the systems Waypoint runs on. On Windows each answer is what the code used before Linux:
/// Linux has its own front end (src/Waypoint.Linux) over the same engine.</summary>
public static class Platform
{
    /// <summary>Windows PowerShell on Windows, which live.ps1 and the adapter are written for; PowerShell 7 elsewhere, found on
    /// PATH or where `dotnet tool install --global PowerShell` puts it, which PATH often lacks. The player's choice first
    /// (Settings → Folders and tools), while it exists.</summary>
    public static string PowerShell => ToolPaths.Usable("powershell") ?? (OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe")
        : OnPath("pwsh") ?? DotnetTool("pwsh") ?? "pwsh");

    /// <summary>Where a user's data goes. Outside Windows the folder may not exist yet, and .NET then answers with nothing, which
    /// would put settings in the working directory: the checkout.</summary>
    public static string LocalData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData,
        OperatingSystem.IsWindows() ? Environment.SpecialFolderOption.None : Environment.SpecialFolderOption.Create);

    /// <summary>The prefix a Linux client runs in: the shell's, else the one Waypoint keeps for every install it runs, which no other
    /// Path of Exile uses. Waypoint names it to what it starts with WINEPREFIX (src/Waypoint.Linux/GameSettings.cs).</summary>
    public static string GamePrefix => ChosenGamePrefix is { Length: > 0 } chosen ? chosen
        : Environment.GetEnvironmentVariable("WINEPREFIX") is { Length: > 0 } own ? own : Path.Combine(LocalData, "LocalExile", "wine-prefix");

    /// <summary>The player's own prefix (Settings → Folders and tools), over the shell's and the default.</summary>
    public static string? ChosenGamePrefix { get; set; }

    /// <summary>The Python on PATH. Some Linux systems only have python3.</summary>
    public static string Python => OperatingSystem.IsWindows() || OnPath("python") is not null ? "python" : "python3";

    /// <summary>A virtual environment's Python.</summary>
    public static string VenvPython(string root) => OperatingSystem.IsWindows()
        ? Path.Combine(root, ".venv", "Scripts", "python.exe") : Path.Combine(root, ".venv", "bin", "python");

    /// <summary>
    /// Gives everything Waypoint starts the module path Windows PowerShell expects. Started from a PowerShell 7 shell
    /// (a terminal, an editor's), Waypoint inherits that shell's <c>PSModulePath</c>, which names PowerShell 7's own module
    /// folders first; Windows PowerShell, which the adapter and live.ps1 run under, then finds PowerShell 7's version of
    /// its built-in modules, cannot load it, and a command as plain as Get-FileHash "is not recognized". PowerShell 7
    /// takes its own folders out when it starts Windows PowerShell itself; it cannot for a program in between, so
    /// Waypoint does the same for its own process, once, at its start. Nothing else is changed, and a PowerShell 7 that
    /// Waypoint starts adds its folders again by itself.
    /// </summary>
    public static void UseWindowsPowerShellModules()
    {
        if (!OperatingSystem.IsWindows()) return;
        string? inherited = Environment.GetEnvironmentVariable("PSModulePath");
        string? cleaned = WithoutPowerShell7(inherited, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PowerShell", "Modules"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "Modules"));
        if (cleaned != inherited) Environment.SetEnvironmentVariable("PSModulePath", cleaned);
    }

    /// <summary>A module path without the three folders PowerShell 7 adds for itself: its user folder, its shared folder,
    /// and the <c>Modules</c> folder of a PowerShell 7 install, known by the <c>pwsh</c> beside it wherever that install is.
    /// Every other entry stays, in its order (a product's own module folder may be named PowerShell too). Null when
    /// nothing is left: Windows PowerShell then builds its own.</summary>
    public static string? WithoutPowerShell7(string? modulePath, string userModules, string sharedModules)
    {
        if (string.IsNullOrEmpty(modulePath)) return modulePath;
        static string Clean(string folder) => Path.TrimEndingDirectorySeparator(folder.Trim().Trim('"'));
        bool Same(string a, string b) => string.Equals(Clean(a), Clean(b), StringComparison.OrdinalIgnoreCase);
        bool Home(string entry)
        {
            try
            {
                return Path.GetFileName(Clean(entry)).Equals("Modules", StringComparison.OrdinalIgnoreCase) && Path.GetDirectoryName(Clean(entry)) is { Length: > 0 } above
                    && (File.Exists(Path.Combine(above, "pwsh.exe")) || File.Exists(Path.Combine(above, "pwsh.dll")));
            }
            catch (ArgumentException) { return false; }
        }
        string[] entries = modulePath.Split(';', StringSplitOptions.RemoveEmptyEntries);
        var kept = entries.Where(entry => !Same(entry, userModules) && !Same(entry, sharedModules) && !Home(entry)).ToArray();
        return kept.Length == entries.Length ? modulePath : kept.Length == 0 ? null : string.Join(';', kept);
    }

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
