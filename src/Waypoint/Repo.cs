using System.Text.RegularExpressions;

namespace Waypoint;

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

    /// <summary>The checkout to drive: the saved one while it still is one, a saved release install, else the working
    /// directory when it is a checkout (`dotnet run` from a server checkout); null when the player has to choose.</summary>
    public static string? Find(string? saved, string workingDirectory) =>
        IsRoot(saved) || LocalExileReleases.IsInstall(saved) ? Path.GetFullPath(saved!) : IsRoot(workingDirectory) ? workingDirectory : null;

    /// <summary>Waypoint runs on a release install: everything goes through LocalExile.exe, and what needs a checkout's
    /// live.ps1, adapter or git is not offered. Set once at startup.</summary>
    public static bool IsInstall { get; set; }

    /// <summary>The LocalExile version the checkout runs, which a mod's <c>compatibleClientVersions</c> are checked against: a
    /// release's release.json, a source checkout's newest version tag (<c>git describe</c>). Null when unknown.</summary>
    public static ReleaseVersion? ClientVersion { get; set; }

    /// <summary>The first release made for downloadable mods: it ships an empty Mods\Binaries\ for Waypoint to fill.</summary>
    public static ReleaseVersion FirstDownloadableModsRelease { get; } = ReleaseVersion.Parse("0.2.0-beta")!;

    /// <summary>A release older than <see cref="FirstDownloadableModsRelease"/> (or of no readable version): it keeps the mods
    /// it shipped with, built for an older mod API, so Waypoint installs, updates and edits none there.</summary>
    public static bool LegacyReleaseMods => IsInstall && (ClientVersion is null || ClientVersion.CompareTo(FirstDownloadableModsRelease) < 0);

    /// <summary>The first release whose GameServer.Modding.dll loads the compiled mods of LocalExile-Mods (LocalExile #762).</summary>
    public static ReleaseVersion FirstReleaseLoadingMods { get; } = ReleaseVersion.Parse("0.2.2-beta")!;

    /// <summary>A release made for downloadable mods but older than <see cref="FirstReleaseLoadingMods"/> (v0.2.0-beta,
    /// v0.2.1-beta): its GameServer.Modding.dll carries the release's version (0.2.x), the mods are built for 1.0.0.0, and
    /// the server refuses them with "Could not load file or assembly 'GameServer.Modding'". Waypoint only says so: nothing
    /// is refused there, since a player must still be able to remove a mod, and a script mod is compiled by the server.</summary>
    public static bool ReleaseModsMayNotLoad => IsInstall && !LegacyReleaseMods && ClientVersion!.CompareTo(FirstReleaseLoadingMods) < 0;

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
    public static string? Branch() => BranchIn(".");

    /// <summary>As <see cref="Branch"/>, of the checkout in <paramref name="folder"/>: another folder of the list.</summary>
    public static string? BranchIn(string folder)
    {
        try
        {
            // A worktree's .git is a file naming its git directory, which may be written relative to the worktree.
            string git = Path.Combine(folder, ".git");
            if (File.Exists(git) && File.ReadAllText(git).Trim() is var link && link.StartsWith("gitdir:", StringComparison.Ordinal))
                git = Path.Combine(folder, link["gitdir:".Length..].Trim());
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
    public static string LocalLiveScript { get; } = Path.Combine("scripts", "dev", "live.local.ps1");
    /// <summary>The player's choice to use a checkout's local launch profile where it has one (Settings).</summary>
    public static bool UseLocalProfile { get; set; } = true;
    /// <summary>What a run starts: the checkout's local wrapper with its profile, else live.ps1.</summary>
    public static string LiveEntry => UseLocalProfile && File.Exists(LocalLiveScript) ? LocalLiveScript : LiveScript;

    /// <summary>Whether the checkout's Gateway serves the network mode the client picks (LocalExile #684); an older one serves
    /// only Predictive unless Game_LOCKSTEP asks (its <c>LockstepLink.cs</c>), and Waypoint no longer sets that.</summary>
    public static bool ServesAnyNetworkMode() =>
        File.Exists(Path.Combine("src", "GameServer.Gateway", "Connection", "LockstepLatency.cs"));

    /// <summary>Whether the checkout has what Waypoint drives it with: live.ps1 -Stop, the Gateway's <c>commands</c> and
    /// <c>values</c> modes and the dev host's accounts came together. An older Gateway does not know those modes and would
    /// start serving, so nothing asks it without this.</summary>
    public static bool HasLauncherHooks()
    {
        try { return File.Exists(LiveScript) && HasLauncherHooks(File.ReadAllText(LiveScript)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    internal static bool HasLauncherHooks(string script) => script.Contains("[switch]$Stop", StringComparison.Ordinal);

    /// <summary>Whether the checkout's live.ps1 starts the walk-map recorder with -ClientOnly -Walkmaps; an older one
    /// refuses the pair, and then starting the game with Walk maps on takes a full run.</summary>
    public static bool ClientOnlyTakesWalkmaps()
    {
        try
        {
            // Asked every second (the Play button's tooltip): read again only when the script changed (a switch, a pull).
            var written = File.GetLastWriteTimeUtc(LiveScript);
            if (_clientOnly is { } known && known.Written == written) return known.Takes;
            bool takes = File.Exists(LiveScript) && ClientOnlyTakesWalkmaps(File.ReadAllText(LiveScript));
            _clientOnly = (written, takes);
            return takes;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    static (DateTime Written, bool Takes)? _clientOnly;

    internal static bool ClientOnlyTakesWalkmaps(string script) =>
        script.Contains("-ClientOnly", StringComparison.Ordinal)
        && !script.Contains("-ClientOnly cannot be combined with -NoClient, -Only or -Walkmaps", StringComparison.Ordinal);

    public static string ExtractScript { get; } = Path.Combine("scripts", "dev", "extract-data.ps1");
    /// <summary>extract-data.ps1 -List, for only the entries the servers read when the script has -Servers (live.ps1 extracts those).</summary>
    public static string[] ExtractListArguments => Launcher.Core.SetupSequence.ExtractsForServers(".") ? ["-Servers", "-List"] : ["-List"];
    public static string ClientControl { get; } = Path.Combine("scripts", "dev", "client_control.py");
    public static string Logs { get; } = "logs";

    /// <summary>A server's build output, as live.ps1 runs it (Debug, net10.0).</summary>
#if WINDOWS
    public static string ServerDll(string project) => IsInstall ? Path.Combine("app", $"GameServer.{project}.exe") :
#else
    public static string ServerDll(string project) => IsInstall ? Path.Combine("app", $"GameServer.{project}") :
#endif
        Path.Combine("src", $"GameServer.{project}", "bin", "Debug", "net10.0", $"GameServer.{project}.dll");

    /// <summary>The checkout's virtual environment wins over the python on PATH, as in live.ps1.</summary>
#if WINDOWS
    public static string Python => File.Exists(Path.Combine(".venv", "Scripts", "python.exe"))
        ? Path.GetFullPath(Path.Combine(".venv", "Scripts", "python.exe")) : "python";
#else
    public static string Python => File.Exists(Platform.VenvPython(".")) ? Path.GetFullPath(Platform.VenvPython(".")) : Platform.Python;
#endif

    /// <summary>The settings file this client uses; GettingStarted step 10 needs networking_type=predictive there.</summary>
#if WINDOWS
    public static string ClientConfig => ClientSettings.Read("Client").ConfigFile;
#else
    public static string ClientConfig => Path.Combine(Platform.GamePrefix, "drive_c", "users", "steamuser", "Documents", "My Games", "Path of Exile", "production_Config.ini");
#endif
}
