using Launcher.Core;

namespace Waypoint;

/// <summary>What a folder of the list is now: read each time it is shown, never saved.</summary>
enum FolderKind { Release, Source, Missing }

/// <summary>One LocalExile folder of the sidebar's list, as it is now: a release with its version, a source copy with its
/// branch, or a folder that is not there (moved, renamed, a drive not connected).</summary>
/// <param name="Client">The version of the game client in the folder ("3.29.3d"), when it was asked for and the client is
/// a build its LocalExile or Waypoint knows; null otherwise, and for a folder with no client.</param>
sealed record KnownFolder(string Path, FolderKind Kind, string? Detail, bool Current, string? Client = null)
{
    /// <summary>The client's version as the list says it: "Path of Exile 3.29.3d".</summary>
    public string? ClientText => FolderList.ClientText(Client);

    /// <summary>What the list calls it: "Release 0.3.0-beta", "Source, main", or the folder's own name while it is missing.</summary>
    public string Title => Kind switch
    {
        FolderKind.Release => Detail is null ? L.Text("Release") : L.Text("Release {0}", Detail),
        FolderKind.Source => Detail is null ? L.Text("Source") : L.Text("Source, {0}", Detail),
        _ => System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(Path)) is { Length: > 0 } name ? name : Path,
    };
}

/// <summary>
/// The LocalExile folders Waypoint knows (<see cref="Settings.KnownFolders"/>): every one it has run on, and every one its user
/// added. The sidebar lists them, and choosing one is a change of folder like any other (<c>App.SwitchServerAsync</c>): no
/// file of either folder moves. The list holds paths only. What a folder is, a release or a source copy, its version or
/// branch, is read from the folder when the list is shown, so a folder updated, switched to another branch or replaced
/// outside Waypoint shows as it is. Taking a folder off the list touches nothing in it.
/// </summary>
static class FolderList
{
    static string Clean(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    /// <summary>Whether two paths name one folder (<see cref="Editions.SameFolder"/>); a path that can't be read as one
    /// names no folder.</summary>
    internal static bool Same(string a, string b)
    {
        try { return Editions.SameFolder(a, b); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or IOException) { return false; }
    }

    /// <summary>The folders of the list, in the order they came, with <paramref name="current"/> among them whether it was
    /// saved or not (Waypoint started with <c>--repo</c> saves nothing). Settings from before the list name up to three
    /// folders (the one in use, the last release and the last source copy): those stand in until the list is first written.</summary>
    public static IReadOnlyList<string> Paths(Settings settings, string? current)
    {
        string?[] saved = settings.KnownFolders.Count > 0 ? [.. settings.KnownFolders] : [settings.ServerPath, settings.ReleaseFolder, settings.LatestFolder];
        var paths = new List<string>();
        foreach (string? path in saved.Append(current))
            if (path is { Length: > 0 } && !paths.Any(p => Same(p, path))) paths.Add(path);
        return paths;
    }

    /// <summary>Notes <paramref name="folder"/>. True when the list changed, which the caller saves.</summary>
    public static bool Add(Settings settings, string folder)
    {
        var paths = Paths(settings, null).ToList();
        if (!paths.Any(p => Same(p, folder)))
        {
            try { paths.Add(Clean(folder)); }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or IOException) { return false; }
        }
        if (paths.SequenceEqual(settings.KnownFolders)) return false;
        settings.KnownFolders = paths;
        return true;
    }

    /// <summary>Takes <paramref name="folder"/> off the list, with what else named it: the release or source copy
    /// remembered for a switch, and the characters waiting to come from it. Nothing in the folder is touched. True when the
    /// list changed, which the caller saves.</summary>
    public static bool Forget(Settings settings, string folder)
    {
        var paths = Paths(settings, null).ToList();
        if (paths.RemoveAll(p => Same(p, folder)) == 0) return false;
        settings.KnownFolders = paths;
        if (settings.ReleaseFolder is { Length: > 0 } release && Same(release, folder)) settings.ReleaseFolder = null;
        if (settings.LatestFolder is { Length: > 0 } latest && Same(latest, folder)) settings.LatestFolder = null;
        if (settings.BringFrom is { Length: > 0 } from && Same(from, folder)) settings.BringFrom = null;
        settings.NoBringFrom.RemoveAll(no => Same(no, folder));
        return true;
    }

    /// <summary>The version of the game client in a LocalExile folder; null when it has none, or one of no build that its
    /// LocalExile lists or Waypoint knows. The folder's own list is asked first (<see cref="ClientLocator.Supported"/>), so
    /// a build LocalExile came to allow after this Waypoint is named too. It reads the client's executable the first time:
    /// never on the UI thread.</summary>
    public static string? ClientOf(string path)
    {
        try
        {
            string client = System.IO.Path.Combine(path, "Client");
            if (!Directory.Exists(client)) return null;
            ClientLocator.Supported(path);
            return ClientLocator.BuildOf(client);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException) { return null; }
    }

    public static string? ClientText(string? version) => version is null ? null : L.Text("Path of Exile {0}", version);

    /// <summary>What <paramref name="path"/> is now; with <paramref name="client"/>, also which version its game client is
    /// (<see cref="ClientOf"/>, off the UI thread).</summary>
    public static KnownFolder Read(string path, bool current, bool client = false)
    {
        try
        {
            if (LocalExileReleases.IsInstall(path)) return new(path, FolderKind.Release, LocalExileReleases.Installed(path)?.ToString(), current, client ? ClientOf(path) : null);
            if (Repo.IsRoot(path)) return new(path, FolderKind.Source, Repo.BranchIn(path), current, client ? ClientOf(path) : null);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException) { }
        return new(path, FolderKind.Missing, null, current);
    }

    /// <summary>The list as it is shown: the releases, then the source copies, then what is missing, each in the order it
    /// came, so that a row stays where its user last saw it.</summary>
    public static IReadOnlyList<KnownFolder> Read(Settings settings, string current) => Read(Paths(settings, current), current);

    /// <summary>As above, of paths taken from the settings beforehand: the reading may run off the UI thread.</summary>
    public static IReadOnlyList<KnownFolder> Read(IReadOnlyList<string> paths, string current, bool client = false) =>
        [.. paths.Select(path => Read(path, Same(path, current), client)).OrderBy(folder => folder.Kind)];

    /// <summary>A path for a narrow place, the sidebar: its end says which folder it is, so the start is what goes, at a
    /// separator where one is near. The whole path is in the tooltip.</summary>
    public static string Short(string path, int length = 27)
    {
        if (path.Length <= length) return path;
        string end = path[^(length - 1)..];
        int separator = end.IndexOfAny(['\\', '/']);
        return "…" + (separator > 0 && separator < end.Length / 2 ? end[separator..] : end);
    }
}
