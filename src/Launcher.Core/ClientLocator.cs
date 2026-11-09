using System.Text.RegularExpressions;
using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Win32;

namespace Launcher.Core;

/// <summary>Ready: already patched by a LocalExile checkout. Standalone: a client to copy. Unsupported: a Steam or Epic
/// layout, which has no Content.ggpk and cannot be used, found only to say so.</summary>
public enum ClientKind { Ready, Standalone, Unsupported }

/// <param name="KnownBuild">Whether its executable is the build LocalExile needs (<see cref="ClientLocator.Build"/>); null
/// when it has none to check.</param>
/// <param name="Build">The build's name when it is one Waypoint knows (<see cref="ClientLocator.BuildOf"/>).</param>
public sealed record FoundClient(string Folder, ClientKind Kind, long Bytes, bool? KnownBuild, string? Build = null)
{
    public string Summary => L.Text(Kind switch
    {
        ClientKind.Ready => "Already prepared for LocalExile",
        ClientKind.Standalone => "Standalone client",
        _ => "Steam or Epic layout: it cannot be used",
    }) + (Bytes > 0 ? $" · {Bytes / 1e9:0.#} GB" : "") + (Build is not null ? " · " + Build : KnownBuild == false ? " · " + L.Text("not the 3.29.3c build") : "");
    public bool Usable => Kind != ClientKind.Unsupported;
}

/// <param name="Searched">Where it looked, for the report when nothing is found.</param>
public sealed record ClientSearchResult(FoundClient[] Clients, string[] Searched, bool Limited);

/// <summary>Finds the player's game clients. Bounded and read-only: it never opens the game or changes a file. The server
/// works only with one client build at a time (<see cref="Build"/>), whose executable has no version resource, so a build is
/// recognized by its hash; the point is to warn before a copy of 70 GB, not to replace the server's own check when it makes the
/// keys.</summary>
public static class ClientLocator
{
    /// <summary>The client build current LocalExile needs. LocalExile v0.2.0-beta moved to it and dropped 3.29.3b.</summary>
    public const string Build = "3.29.3c";

    /// <summary>The last LocalExile made for an older client build: its release tag and the source commit (the head of the
    /// server's branch named after the build).</summary>
    public sealed record OlderVersion(string Release, string Commit);

    /// <summary>The builds Waypoint can name, newest first: the SHA-256 and length of their PathOfExile.exe, and for an older
    /// build the last LocalExile made for it, so that a player who has only that client can still play.</summary>
    static readonly (string Name, string Hash, long Length, OlderVersion? Older)[] Builds =
    [
        ("3.29.3c", "cc8aa38345be2a837575215d2ce142ebecf7b007345843265b335ebb8ac5964c", 79_394_136, null),
        ("3.29.3b", "78a0e227189c263658a7f65a02f086428b859bfc114ae8e7ce8aab721ebd48d5", 79_345_496,
            new("v0.1.0-beta", "7724004c8f13fce2c5e8a8c29ff40ef4b4ef10de")),
    ];

    /// <summary>SHA-256 of the <see cref="Build"/> PathOfExile.exe.</summary>
    public static string KnownExecutable => Builds.First(b => b.Name == Build).Hash;

    /// <summary>The original executable fingerprint for a supported build; unknown future builds are never repaired automatically.</summary>
    public static string? ExecutableHashFor(string? build) => Builds.FirstOrDefault(b => b.Name == build).Hash;

    /// <summary>The last LocalExile made for an older build; null for the current build or one Waypoint does not know.</summary>
    public static OlderVersion? LastVersionFor(string build) => Builds.FirstOrDefault(b => b.Name == build).Older;

    /// <summary>The older builds that still have a LocalExile of their own.</summary>
    public static IEnumerable<string> OlderBuilds => Builds.Where(b => b.Older is not null).Select(b => b.Name);

    static readonly HashSet<string> Excluded = new(StringComparer.OrdinalIgnoreCase)
    {
        "Windows", "ProgramData", "AppData", "$Recycle.Bin", "System Volume Information", "Recovery",
        ".git", ".vs", ".venv", "node_modules", "bin", "obj", "Bundles2", "logs"
    };

    /// <summary>What a folder is; null when it holds no client.</summary>
    public static ClientKind? Classify(string folder)
    {
        bool File(string name) => System.IO.File.Exists(Path.Combine(folder, name));
        if (File("Content.ggpk") && (File("PathOfExile.exe") || File("Game.exe")))
            return File("Game.patched.exe") ? ClientKind.Ready : ClientKind.Standalone;
        return Directory.Exists(Path.Combine(folder, "Bundles2")) && (File("PathOfExileSteam.exe") || File("PathOfExile.exe") || File("PathOfExile_x64Steam.exe") || File("PathOfExileEGS.exe"))
            ? ClientKind.Unsupported : null;
    }

    /// <summary>The original executable of a client folder.</summary>
    public static string? Executable(string folder) =>
        new[] { "PathOfExile.exe", "Game.exe" }.Select(n => Path.Combine(folder, n)).FirstOrDefault(File.Exists);

    /// <summary>Whether a client's executable is the <see cref="Build"/> build; null when there is none, or it cannot be read.</summary>
    public static bool? IsKnownBuild(string folder) => Identify(folder) is { } build ? build == Build : null;

    /// <summary>The named build of a client folder ("3.29.3b"); null when its executable is another one, missing or unreadable.</summary>
    public static string? BuildOf(string folder) => BuildOverride is { } read ? read(folder) : Identify(folder) is { Length: > 0 } build ? build : null;

    /// <summary>For tests only: what <see cref="BuildOf"/> answers instead of reading an executable (a test cannot make one of a
    /// real build's hash).</summary>
    public static Func<string, string?>? BuildOverride { get; set; }

    /// <summary>Whether build <paramref name="a"/> is older than <paramref name="b"/>; false when either is not one Waypoint knows.</summary>
    public static bool IsOlder(string a, string b)
    {
        int ia = Array.FindIndex(Builds, x => x.Name == a), ib = Array.FindIndex(Builds, x => x.Name == b);
        return ia >= 0 && ib >= 0 && ia > ib;
    }

    static readonly Dictionary<(string, long, DateTime), string> Identified = [];

    /// <summary>The build's name, "" for an executable of no known build, null when there is none or it cannot be read. A
    /// checkout's PathOfExile.exe can be the small launcher, so Game.exe is read too. Remembered by path, size and time.</summary>
    static string? Identify(string folder)
    {
        bool any = false;
        foreach (string name in new[] { "PathOfExile.exe", "Game.exe" })
        {
            string executable = Path.Combine(folder, name);
            try
            {
                var file = new FileInfo(executable);
                if (!file.Exists) continue;
                any = true;
                var key = (file.FullName, file.Length, file.LastWriteTimeUtc);
                string? build;
                lock (Identified) Identified.TryGetValue(key, out build);
                if (build is null)
                {
                    build = "";
                    if (Builds.Any(b => b.Length == file.Length))
                    {
                        using var stream = new FileStream(executable, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        string hash = Convert.ToHexStringLower(SHA256.HashData(stream));
                        build = Builds.FirstOrDefault(b => b.Hash == hash).Name ?? "";
                    }
                    lock (Identified) Identified[key] = build;
                }
                if (build.Length > 0) return build;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        }
        return any ? "" : null;
    }

    /// <summary>The client build a LocalExile folder needs: a release's release.json says; a checkout's release script names the
    /// one its release would be made for (a checkout pinned to an older version names its own). Null when neither says.</summary>
    public static string? Needed(string folder)
    {
        try
        {
            string manifest = Path.Combine(folder, "release.json");
            if (File.Exists(manifest))
            {
                using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(manifest));
                if (document.RootElement.TryGetProperty("client", out var client) && client.ValueKind == System.Text.Json.JsonValueKind.String
                    && client.GetString() is { Length: > 0 } name) return name;
            }
            string script = Path.Combine(folder, "scripts", "release", "build-release.ps1");
            if (File.Exists(script)) return NeededIn(File.ReadAllText(script));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException) { }
        return null;
    }

    /// <summary>The client build a checkout's release script names (<c>client = '3.29.3c'</c>); null when it names none.</summary>
    public static string? NeededIn(string? releaseScript) =>
        releaseScript is not null && Regex.Match(releaseScript, @"\bclient\s*=\s*'([^']+)'") is { Success: true } match ? match.Groups[1].Value : null;

    /// <summary>The folder's Client\ holds a build older than its LocalExile needs, so it cannot log in: (what it has, what it
    /// needs). Null when it fits, or when either side is not a build Waypoint knows (LocalExile's own check has the last word).</summary>
    public static (string Have, string Need)? Outdated(string folder)
    {
        if (Needed(folder) is not { } need) return null;
        string client = Path.Combine(folder, "Client");
        return Directory.Exists(client) && BuildOf(client) is { } have && IsOlder(have, need) ? (have, need) : null;
    }

    /// <summary>The other way round: the folder's Client\ is newer than its LocalExile knows (a v0.1.0-beta release whose linked
    /// client the official launcher updated), so LocalExile has to be updated, not the client: (what it has, what it needs).</summary>
    public static (string Have, string Need)? Newer(string folder)
    {
        if (Needed(folder) is not { } need) return null;
        string client = Path.Combine(folder, "Client");
        return Directory.Exists(client) && BuildOf(client) is { } have && IsOlder(need, have) ? (have, need) : null;
    }

    /// <summary>The folder a client removal leaves while it deletes; the next removal finishes it.</summary>
    public const string RemovedClientFolder = ".waypoint-old-client";

    /// <summary>Where the old client's game settings (Client\config.json, which names LocalExile's own settings folder) wait for
    /// the new client.</summary>
    public static string SavedSettings(string folder) => Path.Combine(folder, "data", "client-config.waypoint.json");

    /// <summary>Takes Client\ out of a LocalExile folder, so that the next setup asks for a new one. Its config.json is kept first
    /// (<see cref="SavedSettings"/>), so that the new client keeps LocalExile's own game settings. A link (a shared or linked
    /// client) loses only the link: the folder it points to is never touched. A copy is first renamed, which fails as a whole
    /// while the game holds one of its files, and only then deleted; a delete cut short leaves <see cref="RemovedClientFolder"/>,
    /// which <see cref="FinishRemoval"/> finishes. Saves and keys are in data\ and stay.</summary>
    public static void RemoveClient(string folder)
    {
        string client = Path.Combine(folder, "Client"), old = Path.Combine(folder, RemovedClientFolder);
        FinishRemoval(folder);
        if (!Directory.Exists(client)) return;
        string settings = Path.Combine(client, "config.json"), saved = SavedSettings(folder);
        if (File.Exists(settings) && !File.Exists(saved))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(saved)!);
            File.Copy(settings, saved);
        }
        if (new DirectoryInfo(client).Attributes.HasFlag(FileAttributes.ReparsePoint)) { Directory.Delete(client); return; }
        Directory.Move(client, old);
        DeleteTree(old);
    }

    /// <summary>Finishes a removal an earlier one left (<see cref="RemovedClientFolder"/>).</summary>
    public static void FinishRemoval(string folder)
    {
        string old = Path.Combine(folder, RemovedClientFolder);
        if (Directory.Exists(old)) DeleteTree(old);
    }

    /// <summary>The old client's game settings back into the new one (<see cref="RemoveClient"/>), once a client is in Client\.
    /// Settings the new client got meanwhile (the setup guide's) are kept, and the saved ones dropped. True when they came back.</summary>
    public static bool RestoreSettings(string folder)
    {
        string saved = SavedSettings(folder), client = Path.Combine(folder, "Client");
        if (!File.Exists(saved) || Executable(client) is null) return false;
        string settings = Path.Combine(client, "config.json");
        if (File.Exists(settings))
        {
            File.Delete(saved);
            return false;
        }
        File.Move(saved, settings);
        return true;
    }

    /// <summary>Deletes a folder without following a link inside it: a link is removed, never what it points to.</summary>
    static void DeleteTree(string folder)
    {
        foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos())
        {
            if (entry.Attributes.HasFlag(FileAttributes.ReadOnly)) entry.Attributes &= ~FileAttributes.ReadOnly;
            if (entry is DirectoryInfo directory)
            {
                if (directory.Attributes.HasFlag(FileAttributes.ReparsePoint)) directory.Delete();
                else DeleteTree(directory.FullName);
            }
            else entry.Delete();
        }
        Directory.Delete(folder);
    }

    public static FoundClient? Describe(string folder)
    {
        if (Classify(folder) is not { } kind) return null;
        long bytes = 0;
        try { bytes = new FileInfo(Path.Combine(folder, "Content.ggpk")).Length; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        if (!File.Exists(Path.Combine(folder, "Content.ggpk"))) bytes = 0;
        return new(Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)), kind, bytes, kind == ClientKind.Unsupported ? null : IsKnownBuild(folder),
            kind == ClientKind.Unsupported ? null : BuildOf(folder));
    }

    /// <summary>The usual places first (other checkouts' clients, what the installers recorded, the default folders), then a
    /// bounded walk of the fixed drives. A folder reached twice through a junction counts once.</summary>
    public static ClientSearchResult Find(IEnumerable<string> checkouts, CancellationToken cancellation = default, TimeSpan? timeLimit = null)
    {
        var searched = new List<string>();
        var named = new List<string>();
        foreach (string checkout in checkouts) named.Add(Path.Combine(checkout, "Client"));
        if (OperatingSystem.IsWindows())
        {
            searched.Add(L.Text("the Windows registry"));
            foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
                foreach (string key in new[] { @"Software\GrindingGearGames\Path of Exile", @"Software\WOW6432Node\GrindingGearGames\Path of Exile" })
                {
                    using var install = hive.OpenSubKey(key);
                    if (install?.GetValue("InstallLocation") is string location && location.Length > 0) named.Add(location);
                }
            using var steam = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (steam?.GetValue("SteamPath") is string steamPath && steamPath.Length > 0) named.Add(Path.Combine(steamPath, "steamapps", "common", "Path of Exile"));
        }
        if (OperatingSystem.IsLinux()) named.AddRange(WinePrefixClients());
        foreach (var folder in new[] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles })
        {
            named.Add(Path.Combine(Environment.GetFolderPath(folder), "Grinding Gear Games", "Path of Exile"));
            named.Add(Path.Combine(Environment.GetFolderPath(folder), "Epic Games", "PathOfExile"));
        }
        var drives = new List<string>();
        if (OperatingSystem.IsLinux()) drives.AddRange(HomeRoots());
        else foreach (var drive in DriveInfo.GetDrives())
        {
            try { if (drive.DriveType == DriveType.Fixed && drive.IsReady) drives.Add(drive.RootDirectory.FullName); }
            catch (IOException) { }
        }
        foreach (string drive in drives)
            foreach (string relative in new[] { "Path of Exile", @"Games\Path of Exile", @"Grinding Gear Games\Path of Exile", @"SteamLibrary\steamapps\common\Path of Exile" })
                named.Add(Path.Combine(drive, relative));
        searched.AddRange(drives);

        var found = new Dictionary<string, FoundClient>(StringComparer.OrdinalIgnoreCase);
        void Consider(string folder)
        {
            if (Describe(folder) is not { } client) return;
            found.TryAdd(RealPath(client.Folder), client);
        }
        foreach (string folder in named)
        {
            cancellation.ThrowIfCancellationRequested();
            try { Consider(folder); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { }
        }
        bool limited = Walk(drives, Consider, cancellation, timeLimit ?? TimeSpan.FromSeconds(8));
        return new(found.Values.OrderBy(c => c.Kind).ThenByDescending(c => c.KnownBuild == true).ThenBy(c => c.Folder, StringComparer.OrdinalIgnoreCase).ToArray(),
            searched.ToArray(), limited);
    }

    /// <summary>Where a Linux walk starts: a system's other mounts hold no game, and Games goes first, so that its Wine prefixes
    /// are reached within the walk's depth.</summary>
    static IEnumerable<string> HomeRoots()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new[] { Path.Combine(home, "Games"), home }.Where(Directory.Exists);
    }

    /// <summary>The installer's usual folders in each Wine prefix, and the folder it recorded there: the prefix's user.reg and
    /// system.reg are the Windows registry's files, with C:\ as drive_c.</summary>
    static IEnumerable<string> WinePrefixClients()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var prefixes = new List<string> { Path.Combine(home, ".wine") };
        if (Environment.GetEnvironmentVariable("WINEPREFIX") is { Length: > 0 } own) prefixes.Add(own);
        foreach (string games in new[] { Path.Combine(home, "Games"), Path.Combine(home, "Games", "umu") })
            if (Directory.Exists(games)) prefixes.AddRange(Directory.EnumerateDirectories(games));
        foreach (string prefix in prefixes)
        {
            string drive = Path.Combine(prefix, "drive_c");
            if (!Directory.Exists(drive)) continue;
            yield return Path.Combine(drive, "Path of Exile");
            yield return Path.Combine(drive, "Program Files (x86)", "Grinding Gear Games", "Path of Exile");
            foreach (string registry in new[] { "user.reg", "system.reg" })
                if (Path.Combine(prefix, registry) is var file && File.Exists(file)
                    && Regex.Match(File.ReadAllText(file), @"(?m)^""InstallLocation""=""[Cc]:\\\\(.+?)(?:\\\\)?""\s*$") is { Success: true } location)
                    yield return Path.Combine(drive, location.Groups[1].Value.Replace(@"\\", "/"));
        }
    }

    static bool Walk(IEnumerable<string> roots, Action<string> consider, CancellationToken cancellation, TimeSpan limit)
    {
        const int maximumDirectories = 20_000, depth = 3;
        var queue = new Queue<(string Path, int Depth)>(roots.Select(r => (r, depth)));
        var clock = Stopwatch.StartNew();
        int visited = 0;
        while (queue.TryDequeue(out var next))
        {
            cancellation.ThrowIfCancellationRequested();
            if (++visited > maximumDirectories || clock.Elapsed > limit) return true;
            try
            {
                consider(next.Path);
                if (next.Depth == 0) continue;
                foreach (var child in new DirectoryInfo(next.Path).EnumerateDirectories())
                    if (!Excluded.Contains(child.Name) && (child.Attributes & FileAttributes.ReparsePoint) == 0)
                        queue.Enqueue((child.FullName, next.Depth - 1));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException) { }
        }
        return false;
    }

    /// <summary>The folder a link leads to; a real folder is its own.</summary>
    public static string RealPath(string folder)
    {
        try { return new DirectoryInfo(folder).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? folder; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return folder; }
    }
}
