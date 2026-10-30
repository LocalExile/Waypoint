using System.Diagnostics;

namespace Launcher.Core;

public sealed record RepositorySearchRoot(string Path, int Depth);
public sealed record RepositorySearchResult(string[] Paths, int DirectoriesChecked, bool Limited);

public static class RepositoryDiscovery
{
    static readonly HashSet<string> Excluded = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".svn", ".hg", ".vs", ".venv", ".local", ".codex", ".agents", ".cache", ".npm",
        "Client", "data", "logs", "bin", "obj", "node_modules", "artifacts", "TestResults",
        "Windows", "Program Files", "Program Files (x86)", "ProgramData", "AppData", "Users",
        "$Recycle.Bin", "System Volume Information", "Recovery"
    };

    public static bool IsLocalExile(string path) =>
        File.Exists(System.IO.Path.Combine(path, "GameServer.slnx")) &&
        File.Exists(System.IO.Path.Combine(path, "scripts", "dev", "live.ps1")) &&
        File.Exists(System.IO.Path.Combine(path, "src", "GameServer.Core", "GameServer.Core.csproj")) &&
        File.Exists(System.IO.Path.Combine(path, "src", "GameServer.Gateway", "GameServer.Gateway.csproj"));

    public static RepositorySearchRoot[] DefaultRoots(string applicationDirectory, string currentDirectory, string? selectedPath = null)
    {
        var roots = new List<RepositorySearchRoot>();
        void Add(string path, int depth) { if (!string.IsNullOrWhiteSpace(path)) roots.Add(new(path, depth)); }
        if (!string.IsNullOrWhiteSpace(selectedPath))
        {
            Add(selectedPath, 1);
            try { Add(Directory.GetParent(selectedPath)?.FullName ?? "", 3); } catch (ArgumentException) { }
        }
        foreach (string location in new[] { applicationDirectory, currentDirectory })
        {
            Add(location, 2);
            try
            {
                var parent = new DirectoryInfo(location);
                for (int i = 0; i < 9 && parent is not null; i++, parent = parent.Parent)
                {
                    if (Directory.Exists(System.IO.Path.Combine(parent.FullName, ".git")) || File.Exists(System.IO.Path.Combine(parent.FullName, ".git")))
                    { Add(parent.Parent?.FullName ?? parent.FullName, 3); break; }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        }
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (string relative in new[] { "source/repos", "source", "repos", "Projects", "dev", "Developer", "GitHub", "Documents/GitHub", "Desktop", "Downloads" })
            Add(System.IO.Path.Combine(home, relative), 3);
        Add(home, 2);
        foreach (var drive in DriveInfo.GetDrives())
        {
            try { if (drive.DriveType == DriveType.Fixed && drive.IsReady) Add(drive.RootDirectory.FullName, 3); }
            catch (IOException) { }
        }
        return roots.GroupBy(r => r.Path, StringComparer.OrdinalIgnoreCase).Select(g => g.MaxBy(r => r.Depth)!).ToArray();
    }

    // Bounded directory-only search. Never follows junctions, scans game data or executes repository scripts.
    public static RepositorySearchResult Find(IEnumerable<RepositorySearchRoot> roots, CancellationToken cancellation = default,
        int maximumDirectories = 6000, int maximumResults = 50, TimeSpan? timeLimit = null)
    {
        var queue = new Queue<RepositorySearchRoot>(roots);
        var visited = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var clock = Stopwatch.StartNew();
        int checkedCount = 0;
        bool limited = false;
        while (queue.TryDequeue(out var next))
        {
            cancellation.ThrowIfCancellationRequested();
            if (checkedCount >= maximumDirectories || found.Count >= maximumResults || clock.Elapsed >= (timeLimit ?? TimeSpan.FromSeconds(6)))
            { limited = true; break; }
            try
            {
                string path = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(next.Path));
                if (path.StartsWith(@"\\", StringComparison.Ordinal) || next.Depth < 0) continue;
                if (visited.TryGetValue(path, out int priorDepth) && priorDepth >= next.Depth) continue;
                visited[path] = next.Depth;
                var directory = new DirectoryInfo(path);
                if (!directory.Exists || (directory.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                checkedCount++;
                if (IsLocalExile(path)) found.Add(path);
                if (next.Depth == 0) continue;
                foreach (var child in directory.EnumerateDirectories())
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (clock.Elapsed >= (timeLimit ?? TimeSpan.FromSeconds(6))) { limited = true; break; }
                    if (queue.Count >= maximumDirectories) { limited = true; break; }
                    if (!Excluded.Contains(child.Name) && (child.Attributes & FileAttributes.ReparsePoint) == 0)
                        queue.Enqueue(new(child.FullName, next.Depth - 1));
                }
                if (clock.Elapsed >= (timeLimit ?? TimeSpan.FromSeconds(6))) break;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException) { }
        }
        return new(found.Order(StringComparer.OrdinalIgnoreCase).ToArray(), checkedCount, limited);
    }
}
