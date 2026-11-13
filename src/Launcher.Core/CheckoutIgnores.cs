namespace Launcher.Core;

/// <summary>
/// What Waypoint keeps in a LocalExile folder beside <c>Client</c> is its own: the clients set aside in <c>Clients\</c>
/// (<see cref="ClientVersions"/>) and the folders a download, a copy or a removal works in (<c>.waypoint-…</c>). A release
/// has no git. A source copy does, and there git would list those folders as new files, and a switch of the checkout, which
/// stashes what is not committed, would take a client apart. So before Waypoint makes one of them in a source copy, it
/// names them in the copy's own <c>info/exclude</c>: git's place for what one clone ignores, which no commit holds and
/// which therefore stays whichever revision the copy is on. Nothing tracked is changed.
/// </summary>
public static class CheckoutIgnores
{
    public static IReadOnlyList<string> Patterns { get; } = ["/" + ClientVersions.KeptFolder + "/", "/.waypoint-*"];

    const string Heading = "# Waypoint: game clients it keeps beside Client, and the folders it works in";

    /// <summary>Names Waypoint's folders in the checkout's <c>info/exclude</c>; nothing for a folder without git, and
    /// nothing when they are named already. True when the file holds them afterwards.</summary>
    public static bool Ensure(string folder)
    {
        try
        {
            if (Exclude(folder) is not { } file) return false;
            string[] lines = File.Exists(file) ? File.ReadAllLines(file) : [];
            var missing = Patterns.Where(pattern => !lines.Any(line => line.Trim() == pattern)).ToList();
            if (missing.Count == 0) return true;
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var text = new List<string>(lines);
            if (!lines.Any(line => line.Trim() == Heading)) text.Add(Heading);
            text.AddRange(missing);
            File.WriteAllLines(file, text);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    /// <summary>The checkout's <c>info/exclude</c>: in <c>.git</c>, or for a worktree (whose <c>.git</c> is a file that
    /// names its own folder) in the repository that folder's <c>commondir</c> names. Null for a folder without git.</summary>
    static string? Exclude(string folder)
    {
        string git = Path.Combine(folder, ".git");
        if (Directory.Exists(git)) return Path.Combine(git, "info", "exclude");
        if (!File.Exists(git)) return null;
        string? own = File.ReadLines(git).Select(line => line.Trim()).FirstOrDefault(line => line.StartsWith("gitdir:", StringComparison.Ordinal))?["gitdir:".Length..].Trim();
        if (own is not { Length: > 0 }) return null;
        own = Path.GetFullPath(own, folder);
        string shared = Path.Combine(own, "commondir");
        string common = File.Exists(shared) ? Path.GetFullPath(File.ReadAllText(shared).Trim(), own) : own;
        return Directory.Exists(common) ? Path.Combine(common, "info", "exclude") : null;
    }
}
