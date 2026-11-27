namespace Launcher.Core;

/// <summary>
/// The one file a Waypoint release is, which its self-update replaces: <c>Waypoint.exe</c> on Windows, and on Linux the
/// AppImage, <c>Waypoint-x86_64.AppImage</c>. A release publishes each beside its SHA-256 (<c>.sha256</c>).
/// </summary>
public static class LauncherFile
{
    public const string ExeName = "Waypoint.exe";
    public const string AppImageName = "Waypoint-x86_64.AppImage";

    /// <summary>
    /// The file an update replaces, or null when this Waypoint is not one a release published (a build from source,
    /// <c>dotnet run</c>). On Windows the running single-file <c>Waypoint.exe</c>. On Linux the AppImage the AppImage runtime
    /// names in <c>APPIMAGE</c>, and only while this process runs from inside the image that runtime mounted
    /// (<c>APPDIR</c>): the two are inherited by whatever an AppImage starts, so a Waypoint built from source and started
    /// from one must not take that other AppImage for its own. The AppImage may have been renamed by its user; it is
    /// replaced under its own name.
    /// </summary>
    public static string? Running(bool linux, string? processPath, string? appImage, string? appDir, Func<string, bool> exists)
    {
        if (processPath is not { Length: > 0 }) return null;
        if (!linux) return Path.GetFileName(processPath).Equals(ExeName, StringComparison.OrdinalIgnoreCase) ? processPath : null;
        if (appImage is not { Length: > 0 } || appDir is not { Length: > 0 } || !appImage.StartsWith('/') || !appDir.StartsWith('/')) return null;
        string mount = appDir.TrimEnd('/') + "/";
        if (!processPath.StartsWith(mount, StringComparison.Ordinal) || appImage.StartsWith(mount, StringComparison.Ordinal)) return null;
        return exists(appImage) ? appImage : null;
    }

    /// <summary><see cref="Running(bool, string?, string?, string?, Func{string, bool})"/> for this process.</summary>
    public static string? Running() => Running(OperatingSystem.IsLinux(), Environment.ProcessPath,
        Environment.GetEnvironmentVariable("APPIMAGE"), Environment.GetEnvironmentVariable("APPDIR"), File.Exists);
}
