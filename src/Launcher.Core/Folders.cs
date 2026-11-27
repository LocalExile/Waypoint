namespace Launcher.Core;

/// <summary>
/// The folders the player chose for Waypoint's data (Settings → Folders and tools): whether one can be used, and moving what
/// is in one to another. A move never leaves the player with less than they had: everything is copied and checked before the
/// old files go, and a failure removes the copies and leaves the old folder as it was.
/// </summary>
public static class Folders
{
    /// <summary>Why Waypoint cannot keep files in <paramref name="folder"/> (it cannot be made, or nothing can be written there);
    /// null when it can.</summary>
    public static string? Unusable(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            string probe = Path.Combine(folder, ".waypoint-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return null;
        }
        catch (IOException) when (File.Exists(folder))
        {
            return L.Text("The path points to a file.");
        }
        catch (UnauthorizedAccessException)
        {
            return L.Text("Access to this folder was denied.");
        }
        catch (Exception e) when (e is IOException or ArgumentException or NotSupportedException)
        {
            return e.Message;
        }
    }

    /// <summary>Whether a folder holds nothing (or is not there yet).</summary>
    public static bool Empty(string folder) => !Directory.Exists(folder) || !Directory.EnumerateFileSystemEntries(folder).Any();

    /// <summary>Everything in <paramref name="from"/> into <paramref name="to"/>, which must be empty or new, with neither inside
    /// the other. Throws <see cref="IOException"/> with the reason; nothing is lost then. Old files that cannot be removed
    /// after the copy stay behind (the new folder has them all) and are returned.</summary>
    public static IReadOnlyList<string> Move(string from, string to)
    {
        from = Path.TrimEndingDirectorySeparator(Path.GetFullPath(from));
        to = Path.TrimEndingDirectorySeparator(Path.GetFullPath(to));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(from, to, comparison)) return [];
        if (to.StartsWith(from + Path.DirectorySeparatorChar, comparison) || from.StartsWith(to + Path.DirectorySeparatorChar, comparison))
            throw new IOException(L.Text("One of these folders is inside the other: {0}, {1}.", from, to));
        if (!Empty(to)) throw new IOException(L.Text("{0} is not empty. Choose an empty folder or a new one.", to));
        if (Unusable(to) is { } why) throw new IOException(L.Text("Waypoint cannot write to {0}: {1}", to, why));
        if (!Directory.Exists(from)) return [];

        var copied = new List<string>();
        try
        {
            foreach (string folder in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, folder)));
            foreach (string file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            {
                string target = Path.Combine(to, Path.GetRelativePath(from, file));
                File.Copy(file, target);
                copied.Add(target);
                if (new FileInfo(target).Length != new FileInfo(file).Length) throw new IOException(L.Text("{0} was not copied whole.", file));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The old folder is untouched; the copies go. The new folder was empty, so all that is in it now is this move's.
            try { foreach (string folder in Directory.EnumerateDirectories(to)) Directory.Delete(folder, true); foreach (string file in copied) File.Delete(file); }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
            throw new IOException(L.Text("The files could not be moved, and nothing was changed: {0}", e.Message), e);
        }

        var left = new List<string>();
        foreach (string file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            try { File.Delete(file); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { left.Add(file); }
        try { if (left.Count == 0) Directory.Delete(from, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return left;
    }
}
