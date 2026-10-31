using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Win32;

namespace Launcher.Core;

/// <summary>Ready: already patched by a LocalExile checkout. Standalone: a client to copy. Unsupported: a Steam or Epic
/// layout, which has no Content.ggpk and cannot be used, found only to say so.</summary>
public enum ClientKind { Ready, Standalone, Unsupported }

/// <param name="KnownBuild">Whether its executable is the 3.29.3b build; null when it has none to check.</param>
public sealed record FoundClient(string Folder, ClientKind Kind, long Bytes, bool? KnownBuild)
{
    public string Summary => L.Text(Kind switch
    {
        ClientKind.Ready => "Already prepared for LocalExile",
        ClientKind.Standalone => "Standalone client",
        _ => "Steam or Epic layout: it cannot be used",
    }) + (Bytes > 0 ? $" · {Bytes / 1e9:0.#} GB" : "") + (KnownBuild == false ? " · " + L.Text("not the 3.29.3b build") : "");
    public bool Usable => Kind != ClientKind.Unsupported;
}

/// <param name="Searched">Where it looked, for the report when nothing is found.</param>
public sealed record ClientSearchResult(FoundClient[] Clients, string[] Searched, bool Limited);

/// <summary>Finds the player's game clients. Bounded and read-only: it never opens the game or changes a file. The server
/// works only with client 3.29.3b, whose executable has no version resource, so the build is recognized by its hash; the
/// point is to warn before a copy of 70 GB, not to replace the server's own check when it makes the keys.</summary>
public static class ClientLocator
{
    /// <summary>SHA-256 of the 3.29.3b PathOfExile.exe.</summary>
    public const string KnownExecutable = "78a0e227189c263658a7f65a02f086428b859bfc114ae8e7ce8aab721ebd48d5";
    const long KnownExecutableLength = 79_345_496;

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

    /// <summary>Whether a client's executable is the 3.29.3b build; null when there is none, or it cannot be read.</summary>
    public static bool? IsKnownBuild(string folder)
    {
        if (Executable(folder) is not { } executable) return null;
        try
        {
            if (new FileInfo(executable).Length != KnownExecutableLength) return false;
            using var stream = new FileStream(executable, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return Convert.ToHexStringLower(SHA256.HashData(stream)) == KnownExecutable;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    public static FoundClient? Describe(string folder)
    {
        if (Classify(folder) is not { } kind) return null;
        long bytes = 0;
        try { bytes = new FileInfo(Path.Combine(folder, "Content.ggpk")).Length; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        if (!File.Exists(Path.Combine(folder, "Content.ggpk"))) bytes = 0;
        return new(Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)), kind, bytes, kind == ClientKind.Unsupported ? null : IsKnownBuild(folder));
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
        foreach (var folder in new[] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles })
        {
            named.Add(Path.Combine(Environment.GetFolderPath(folder), "Grinding Gear Games", "Path of Exile"));
            named.Add(Path.Combine(Environment.GetFolderPath(folder), "Epic Games", "PathOfExile"));
        }
        var drives = new List<string>();
        foreach (var drive in DriveInfo.GetDrives())
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

    static string RealPath(string folder)
    {
        try { return new DirectoryInfo(folder).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? folder; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return folder; }
    }
}
