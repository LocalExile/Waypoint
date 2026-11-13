using System.Security.Cryptography;

namespace Launcher.Core;

/// <summary>What a LocalExile folder's <c>Client</c> is, as LocalExile tells the two layouts apart (<c>ClientFiles.Resolve</c>:
/// a <c>Content.ggpk</c> first, else <c>Bundles2/_.index.bin</c>).</summary>
public enum ClientLayout { None, Standalone, Steam, Link }

/// <summary>How a folder moves to another version of the game from Steam when it already has a client.</summary>
public enum SwitchWay
{
    /// <summary>The one client is brought to the other version where it is: only what differs is downloaded, and no more
    /// room is needed, but the version it was is gone.</summary>
    UpdateInPlace,
    /// <summary>The client in use is set aside in <see cref="ClientVersions.KeptFolder"/> and the other version made beside
    /// it: both stay, at the room of both, and going back is a rename.</summary>
    KeepBoth,
}

/// <summary>A client set aside in the folder's <c>Clients\</c>.</summary>
/// <param name="Name">Its folder's name there.</param>
/// <param name="Version">The version it is, when its executable is one a list or Waypoint names.</param>
/// <param name="HalfWayTo">For a client that was set aside while half-way to another version (it carries the mark): that
/// version. Such a copy is not whole and cannot be used until it is finished (<see cref="ClientVersions.FinishKeptAsync"/>).</param>
/// <param name="HalfWayToBuild">The executable hash of the build it was on its way to, when the mark names it.</param>
public sealed record KeptClient(string Name, string? Version, string Folder, long Bytes, string? HalfWayTo = null, string? HalfWayToBuild = null);

/// <param name="Stage">"copy" while the client in use is copied as a start, "download" while Steam's files come.</param>
/// <param name="Fraction">0 to 1 of the stage, when it can be told.</param>
public sealed record SwitchProgress(string Stage, double? Fraction, string Detail);

/// <summary>
/// The versions of the game a LocalExile folder holds: the one in <c>Client</c>, which is all LocalExile ever looks at, and
/// those set aside in <c>Clients\</c>, which are Waypoint's own. A version comes from Steam (<see cref="SteamDownload"/>) in
/// one of two ways, the user's to choose (<see cref="SwitchWay"/>). Throughout, a client that works is never taken away
/// before another is whole, except by <see cref="SwitchWay.UpdateInPlace"/>, whose point is to need no second copy: a
/// client being updated where it is carries a mark (<see cref="Unfinished"/>) until it is whole again, nothing starts on it
/// meanwhile, and the same switch run again finishes it.
/// </summary>
public static class ClientVersions
{
    public const string KeptFolder = "Clients";
    /// <summary>Beside <c>Client</c> while another version is made ready to take its place.</summary>
    public const string NextFolder = ".waypoint-next-client";
    /// <summary>In a client that is being updated where it is: the version it is on its way to and, on a second line, that
    /// build's executable hash (one version can be several builds).</summary>
    public const string Marker = ".waypoint-switching";
    /// <summary>In <see cref="NextFolder"/> once the copy it starts from is whole.</summary>
    const string Seeded = ".waypoint-seeded";
    /// <summary>Beside <c>Client</c> while a client that is on this PC is copied in (<see cref="CopyInAsync"/>).</summary>
    public const string CopiedFolder = ".waypoint-copied-client";
    /// <summary>In <see cref="CopiedFolder"/>: the folder the copy is of, so that a copy of another one starts over.</summary>
    const string CopyOf = ".waypoint-copy-of";
    /// <summary>Room left over on the drive, beyond what a switch is known to need.</summary>
    public const long Margin = 4_000_000_000;

    static string Client(string folder) => Path.Combine(folder, "Client");

    public static ClientLayout Layout(string folder) => LayoutOf(Client(folder));

    public static ClientLayout LayoutOf(string client)
    {
        try
        {
            if (ClientSettings.IsLink(client)) return ClientLayout.Link;
            if (File.Exists(Path.Combine(client, "Content.ggpk"))) return ClientLayout.Standalone;
            return File.Exists(Path.Combine(client, "Bundles2", "_.index.bin")) ? ClientLayout.Steam : ClientLayout.None;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return ClientLayout.None; }
    }

    /// <summary>Which of the two a client folder's files are, whether the folder is a link or not: a client that came as the
    /// standalone game (<c>Content.ggpk</c>) or one that came from Steam.</summary>
    public static ClientLayout FilesOf(string client)
    {
        try
        {
            if (File.Exists(Path.Combine(client, "Content.ggpk"))) return ClientLayout.Standalone;
            return File.Exists(Path.Combine(client, "Bundles2", "_.index.bin")) ? ClientLayout.Steam : ClientLayout.None;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return ClientLayout.None; }
    }

    /// <summary>The version a client being updated where it is was on its way to, when that was cut short; null for a
    /// client that is whole.</summary>
    public static string? Unfinished(string folder) => UnfinishedBuild(folder)?.Version;

    /// <summary>The same, with the executable hash of the build it was on its way to when the mark names it.</summary>
    public static (string Version, string? ExecutableSha256)? UnfinishedBuild(string folder) => MarkIn(Client(folder));

    /// <summary>The mark in a client folder, the one in use or one set aside.</summary>
    static (string Version, string? ExecutableSha256)? MarkIn(string client)
    {
        try
        {
            string marker = Path.Combine(client, Marker);
            if (!File.Exists(marker)) return null;
            string[] lines = [.. File.ReadAllLines(marker).Select(l => l.Trim()).Where(l => l.Length > 0)];
            return (lines.Length > 0 ? lines[0] : "", lines.Length > 1 ? lines[1] : null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The newest of the builds on offer: the highest version, and of a version's builds the first offered, which
    /// is the latest LocalExile lists (a hotfix before its release). Null when nothing is on offer.</summary>
    public static ListedBuild? Newest(IEnumerable<ListedBuild> offered)
    {
        ListedBuild? newest = null;
        foreach (var build in offered)
            if (newest is null || ClientLocator.IsOlder(newest.Version, build.Version)) newest = build;
        return newest;
    }

    /// <summary>For a LocalExile folder with no game client at all, in use or kept: the newest build its LocalExile lists
    /// that Steam can deliver. Null when the folder has a client, its LocalExile lists none (before v0.5.0-beta), or this
    /// Waypoint cannot run the download tool. It is what Finish setup offers, and the one test for whether the setup guide
    /// may say so. It can ask the folder's launcher, which takes a moment: never on the UI thread.</summary>
    public static ListedBuild? FirstDownload(string folder) =>
        DepotTool.Supported && Layout(folder) == ClientLayout.None && Kept(folder).Count == 0
            ? Newest(Offered(LocalExileBuilds.Of(folder) ?? [], null, whole: true, [])) : null;

    /// <summary>Whether a version is more than one build in the list (a release and its hotfix), whether Steam can deliver
    /// each or not: its builds are then told apart by LocalExile's own name for each.</summary>
    public static bool Several(IEnumerable<ListedBuild> builds, string? version) => builds.Count(b => b.Version == version) > 1;

    /// <summary>The builds to offer from Steam, newest first: every build Steam can deliver, less those the folder holds
    /// already (the one in use when it is whole, and those set aside).</summary>
    /// <param name="inUse">The build of the client in use, when its executable is a listed one.</param>
    /// <param name="whole">False for a client half-way to another build: every build is then offered, the one it was too.</param>
    /// <param name="kept">The builds of the clients set aside.</param>
    public static IReadOnlyList<ListedBuild> Offered(IEnumerable<ListedBuild> builds, ListedBuild? inUse, bool whole, IEnumerable<ListedBuild?> kept)
    {
        var here = kept.Append(whole ? inUse : null).Where(b => b is not null).Select(b => b!.ExecutableSha256).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return [.. builds.Where(b => b.FromSteam && !here.Contains(b.ExecutableSha256)).Reverse()];
    }

    /// <summary>The bytes of every file under a folder, links not followed; 0 when it cannot be read.</summary>
    public static long SizeOf(string folder)
    {
        try
        {
            return new DirectoryInfo(folder).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint })
                .Sum(file => file.Length);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return 0; }
    }

    /// <summary>The clients set aside in the folder, by name.</summary>
    public static IReadOnlyList<KeptClient> Kept(string folder)
    {
        var kept = new List<KeptClient>();
        try
        {
            string store = Path.Combine(folder, KeptFolder);
            if (!Directory.Exists(store)) return kept;
            foreach (var entry in new DirectoryInfo(store).EnumerateDirectories().OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
                // A copy whose deletion was cut short (".deleting-<name>") is not a client to list.
                if (entry.LinkTarget is null && !entry.Name.StartsWith(Deleting, StringComparison.Ordinal) && LayoutOf(entry.FullName) is ClientLayout.Standalone or ClientLayout.Steam)
                {
                    var mark = MarkIn(entry.FullName);
                    kept.Add(new(entry.Name, ClientLocator.BuildOf(entry.FullName), entry.FullName, SizeOf(entry.FullName), mark?.Version, mark?.ExecutableSha256));
                }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return kept;
    }

    /// <summary>The start of a kept client's name while it is being deleted.</summary>
    const string Deleting = ".deleting-";

    /// <summary>A name in <c>Clients\</c> that is free: the version, or the version with a number.</summary>
    static string FreeName(string folder, string? version)
    {
        string store = Path.Combine(folder, KeptFolder), name = version ?? "other";
        Directory.CreateDirectory(store);
        for (int i = 1; ; i++)
        {
            string candidate = Path.Combine(store, i == 1 ? name : $"{name} ({i})");
            if (!Directory.Exists(candidate) && !File.Exists(candidate)) return candidate;
        }
    }

    /// <summary>Puts a client that was set aside back in use, and the one in use in its place in <c>Clients\</c>: two renames,
    /// the first undone when the second fails. Nothing is downloaded or copied. With <paramref name="overLink"/> (the Game
    /// client window in Advanced mode) a <c>Client</c> that is a link gives way too: only the link goes, once the kept
    /// client is in its place, and the folder it led to is never touched.</summary>
    public static void Use(string folder, string name, bool overLink = false)
    {
        string client = Client(folder), kept = Path.Combine(folder, KeptFolder, name);
        if (!Directory.Exists(kept)) throw new IOException(L.Text("{0} is no longer there.", kept));
        CheckoutIgnores.Ensure(folder);
        var layout = Layout(folder);
        bool link = layout == ClientLayout.Link;
        if (link && !overLink) throw new IOException(L.Text("This folder's game client is a link to another folder. Make a copy of it first."));
        // The mark of a client that is half-way is in the folder a link leads to: that folder's to finish, not this one's.
        if (!link && Unfinished(folder) is not null) throw new IOException(L.Text("The game client in use is half-way to another version. Finish that switch first."));
        if (MarkIn(kept) is not null) throw new IOException(L.Text("That copy was set aside half-way to another version, so it is not whole and cannot be used."));
        string? aside = null;
        if (link) aside = SetLinkAside(folder);
        else if (Directory.Exists(client))
        {
            aside = FreeName(folder, ClientLocator.BuildOf(client));
            Directory.Move(client, aside);
        }
        try { Directory.Move(kept, client); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            if (aside is not null) Directory.Move(aside, client);
            throw;
        }
        if (link && aside is not null) RemoveLink(aside);
    }

    /// <summary>A <c>Client</c> that is a link, renamed out of the way (<see cref="OwnClient.OldLink"/>) so that it can be put
    /// back as it was: a rename moves the link, never what it leads to.</summary>
    static string SetLinkAside(string folder)
    {
        string aside = Path.Combine(folder, OwnClient.OldLink);
        if (ClientSettings.IsLink(aside)) RemoveLink(aside);
        Directory.Move(Client(folder), aside);
        return aside;
    }

    /// <summary>Removes a link and nothing it leads to.</summary>
    static void RemoveLink(string link)
    {
        if (!ClientSettings.IsLink(link)) return;
        if (OperatingSystem.IsWindows()) Directory.Delete(link); else File.Delete(link); // a Unix link is a file
    }

    /// <summary>
    /// Puts a link to another client folder in the place of the client in use. A client that is a copy is set aside in
    /// <c>Clients\</c> first, and a link is only renamed out of the way; either comes back if the new link cannot be
    /// made. Nothing is deleted but a link that was replaced, and <see cref="Use"/> undoes the rest. Only from the Game
    /// client window in Advanced mode, on its user's word. <paramref name="link"/> makes the link once <c>Client</c> is
    /// out of the way (Waypoint's own for a shared client, or LocalExile's own link mode) and throws when it cannot.
    /// </summary>
    public static async Task LinkAsync(string folder, Func<Task> link)
    {
        string client = Client(folder);
        bool linked = Layout(folder) == ClientLayout.Link;
        if (!linked && Unfinished(folder) is not null) throw new IOException(L.Text("The game client in use is half-way to another version. Finish that switch first."));
        CheckoutIgnores.Ensure(folder);
        string? aside = SetAside(folder, linked);
        try
        {
            await link();
            if (!ClientSettings.IsLink(client)) throw new IOException(L.Text("The link could not be made. The client in use was not changed."));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception or OperationCanceledException)
        {
            // Whatever a failed try left in Client's place is no client: what was set aside goes back.
            if (aside is not null)
            {
                if (ClientSettings.IsLink(client)) RemoveLink(client);
                if (!Directory.Exists(client)) Directory.Move(aside, client);
            }
            throw;
        }
        if (linked && aside is not null) RemoveLink(aside);
    }

    /// <summary>Takes what is in <c>Client</c>'s place out of the way, so that it can be put back: a link is renamed
    /// (<see cref="SetLinkAside"/>), a client goes to <c>Clients\</c> under its version, an empty folder is removed. Where
    /// it went, or null when there was nothing to keep.</summary>
    static string? SetAside(string folder, bool linked)
    {
        string client = Client(folder);
        if (linked) return SetLinkAside(folder);
        if (!Directory.Exists(client)) return null;
        if (!Directory.EnumerateFileSystemEntries(client).Any())
        {
            Directory.Delete(client);
            return null;
        }
        string aside = FreeName(folder, ClientLocator.BuildOf(client));
        Directory.Move(client, aside);
        return aside;
    }

    /// <summary>What a copy of <paramref name="source"/> into the folder takes (<see cref="CopyInAsync"/>): its plan, with
    /// what an earlier try of the same copy already made counted as done.</summary>
    public static ClientCopyPlan CopyInPlan(string folder, string source) => ClientCopy.Plan(source, folder, Path.Combine(folder, CopiedFolder));

    /// <summary>
    /// Brings a copy of a client that is on this PC (another LocalExile folder's, in use or kept) into the folder's
    /// <c>Client</c>. The copy is made beside it (<see cref="CopiedFolder"/>), with the copy the setup guide uses, so the
    /// client in use goes on working meanwhile, a stop leaves what was copied, and the same copy goes on from it. Only a
    /// whole copy is swapped in: a client that was in use is then kept in <c>Clients\</c>, and a link loses only the link.
    /// The folder copied from is only read. Its patched executable comes along only with the keys it was patched for
    /// (<see cref="ClientCopy.Plan"/>); the folder's own setup must run afterwards, which prepares the new client.
    /// </summary>
    public static async Task CopyInAsync(string folder, string source, Action<ClientCopyProgress>? progress, CancellationToken stop)
    {
        string client = Client(folder), side = Path.Combine(folder, CopiedFolder), of = Path.Combine(side, CopyOf);
        bool linked = Layout(folder) == ClientLayout.Link;
        if (!linked && Unfinished(folder) is not null) throw new IOException(L.Text("The game client in use is half-way to another version. Finish that switch first."));
        CheckoutIgnores.Ensure(folder);
        source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
        // What an earlier copy of another client left is of no use to this one.
        if (Directory.Exists(side) && (!File.Exists(of) || !string.Equals(File.ReadAllText(of).Trim(), source, StringComparison.OrdinalIgnoreCase)))
            ClientLocator.DeleteTree(side);
        Directory.CreateDirectory(side);
        await File.WriteAllTextAsync(of, source, stop);
        await ClientCopy.RunAsync(CopyInPlan(folder, source), progress, stop);
        stop.ThrowIfCancellationRequested();
        // Waypoint's own marks are not part of a client.
        foreach (string name in new[] { CopyOf, Seeded, Marker, OwnClient.Complete })
            if (File.Exists(Path.Combine(side, name))) File.Delete(Path.Combine(side, name));
        // LocalExile's own game settings are named by the client's config.json: they go with the folder. A linked
        // client's are the folder's it leads to.
        string settings = Path.Combine(client, "config.json");
        if (!linked && File.Exists(settings) && !File.Exists(Path.Combine(side, "config.json"))) File.Copy(settings, Path.Combine(side, "config.json"));

        string? aside = SetAside(folder, linked);
        try { Directory.Move(side, client); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            if (aside is not null) Directory.Move(aside, client);
            throw;
        }
        if (linked && aside is not null) RemoveLink(aside);
    }

    /// <summary>Deletes a client that was set aside: renamed first, which fails as a whole while anything holds one of its
    /// files, then deleted without following a link inside it.</summary>
    public static void Delete(string folder, string name)
    {
        string kept = Path.Combine(folder, KeptFolder, name), going = Path.Combine(folder, KeptFolder, Deleting + name);
        FinishDeletes(folder);
        if (!Directory.Exists(kept)) return;
        if (Directory.Exists(going)) ClientLocator.DeleteTree(going);
        Directory.Move(kept, going);
        ClientLocator.DeleteTree(going);
    }

    /// <summary>Whether a deletion of a kept client was cut short (Waypoint closed, the PC went off): its remains are
    /// still on the drive, under a name the list does not show.</summary>
    public static bool HasUnfinishedDeletes(string folder)
    {
        try
        {
            string store = Path.Combine(folder, KeptFolder);
            return Directory.Exists(store) && new DirectoryInfo(store).EnumerateDirectories(Deleting + "*").Any();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Finishes the deletions of kept clients that were cut short. Their user already said to delete them.</summary>
    public static void FinishDeletes(string folder)
    {
        string store = Path.Combine(folder, KeptFolder);
        if (!Directory.Exists(store)) return;
        foreach (var entry in new DirectoryInfo(store).EnumerateDirectories(Deleting + "*").ToList())
            if (entry.LinkTarget is null) ClientLocator.DeleteTree(entry.FullName);
    }

    /// <summary>The room a switch needs on the folder's drive, as far as it can be told before it starts.</summary>
    /// <param name="buildBytes">The build's size on disk (<see cref="SteamDownload.SizeAsync"/>).</param>
    /// <param name="currentBytes">The size of the client in use.</param>
    public static long Needed(ClientLayout layout, SwitchWay way, long buildBytes, long currentBytes) =>
        layout == ClientLayout.Steam && way == SwitchWay.UpdateInPlace ? Margin
        : layout == ClientLayout.Steam ? currentBytes + Margin
        : buildBytes + Margin;

    /// <summary>Whether a folder holds this build whole: its executable is the build's, and its game files' index is the
    /// one LocalExile lists for it.</summary>
    public static bool Holds(string client, ListedBuild build)
    {
        try
        {
            string index = Path.Combine(client, "Bundles2", "_.index.bin");
            if (!File.Exists(index) || ClientLocator.BuildOf(client) != build.Version) return false;
            // Two builds of one version differ by their executable (a test has no real executable to hash).
            if (ClientLocator.BuildOverride is null && ClientLocator.ListedBuildOf(client, [build]) is null) return false;
            if (build.SteamIndexSha256 is not { } expected) return true;
            using var stream = new FileStream(index, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return Convert.ToHexStringLower(SHA256.HashData(stream)) == expected;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>What LocalExile made beside the game's files for the build that was there (Game.exe and the copy patched for
    /// this folder's keys): gone, so that its next setup makes them again from the build that is there now.</summary>
    static void ForgetPrepared(string client)
    {
        // The mark too: a copy of a client that was half-way carries it, and what was just verified is whole.
        foreach (string name in new[] { "Game.exe", "Game.patched.exe", Seeded, Marker })
            if (File.Exists(Path.Combine(client, name))) File.Delete(Path.Combine(client, name));
    }

    /// <summary>
    /// Makes a client that was set aside half-way to another version whole where it is, as the build it was on its way to:
    /// Steam sends what is missing, the same check as for a client in use says it is whole, and only then does the mark go.
    /// It is then a kept copy of that build, under that version's name. The client in use is not touched.
    /// </summary>
    public static async Task<SteamResult> FinishKeptAsync(string folder, string name, ListedBuild build, string tool, string account,
        Action<SwitchProgress>? progress, Action<string> log, CancellationToken stop)
    {
        string kept = Path.Combine(folder, KeptFolder, name);
        try
        {
            if (!Directory.Exists(kept)) return new(SteamOutcome.Failed, L.Text("{0} is no longer there.", kept));
            CheckoutIgnores.Ensure(folder);
            if (new DirectoryInfo(kept).LinkTarget is not null || FilesOf(kept) != ClientLayout.Steam)
                return new(SteamOutcome.Failed, L.Text("That copy is not a client from Steam, so Steam cannot finish it."));
            var fetched = await SteamDownload.FetchAsync(tool, build, account, kept,
                p => progress?.Invoke(new("download", (p.Depot - 1 + p.Percent / 100) / p.Depots, L.Text("Part {0} of {1}: {2:0.0}% · {3}", p.Depot, p.Depots, p.Percent, p.File))), log, stop);
            if (fetched.Outcome != SteamOutcome.Done) return fetched;
            if (!Holds(kept, build)) return new(SteamOutcome.Failed, L.Text("The download ended, but the client is not whole. Run it again."));
            ForgetPrepared(kept);
            // Its folder is named after the version it was: it is the build's now.
            if (!string.Equals(name, build.Version, StringComparison.OrdinalIgnoreCase)) Directory.Move(kept, FreeName(folder, build.Version));
            return new(SteamOutcome.Done);
        }
        catch (OperationCanceledException) { return new(SteamOutcome.Stopped); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return new(SteamOutcome.Failed, e.Message); }
    }

    /// <summary>
    /// Brings the folder to a build from Steam. A client from Steam that is updated where it is carries <see cref="Marker"/>
    /// until it is whole. Every other case makes the build ready in <see cref="NextFolder"/>, beside a client that goes on
    /// working, and swaps only a whole one in: the client in use then goes to <c>Clients\</c>
    /// (<see cref="SwitchWay.KeepBoth"/>) or is deleted once the new one is in place. With a client from Steam in use, the
    /// new folder starts as a copy of it, so that only what differs is downloaded. A stop or a failure leaves what was
    /// fetched, and the same call goes on from it. LocalExile's setup must run afterwards: it prepares the new client.
    /// </summary>
    public static async Task<SteamResult> SwitchAsync(string folder, ListedBuild build, SwitchWay way, string tool, string account,
        Action<SwitchProgress>? progress, Action<string> log, CancellationToken stop)
    {
        var layout = Layout(folder);
        if (layout == ClientLayout.Link) return new(SteamOutcome.Failed, L.Text("This folder's game client is a link to another folder. Make a copy of it first."));
        string client = Client(folder), next = Path.Combine(folder, NextFolder);
        // A client half-way to another version is made whole where it is, whichever build is asked for: set aside or
        // copied, it would be a client that is not whole under a whole one's name.
        bool marked = Unfinished(folder) is not null;
        if (marked && layout == ClientLayout.Steam && way != SwitchWay.UpdateInPlace)
            return new(SteamOutcome.Failed, L.Text("The game client in use is half-way to another version. Finish that switch first."));
        // A marked client that is not from Steam cannot be finished where it is (a broken or hand-altered folder): it is
        // replaced by the build, and never set aside as if it were whole.
        if (marked && layout != ClientLayout.Steam) way = SwitchWay.UpdateInPlace;
        void Downloading(SteamProgress p) =>
            progress?.Invoke(new("download", (p.Depot - 1 + p.Percent / 100) / p.Depots, L.Text("Part {0} of {1}: {2:0.0}% · {3}", p.Depot, p.Depots, p.Percent, p.File)));
        try
        {
            // A source copy: git leaves alone what is made beside Client from here on.
            CheckoutIgnores.Ensure(folder);
            if (layout == ClientLayout.Steam && way == SwitchWay.UpdateInPlace)
            {
                if (Directory.Exists(next)) ClientLocator.DeleteTree(next);
                await File.WriteAllTextAsync(Path.Combine(client, Marker), build.Version + "\n" + build.ExecutableSha256, stop);
                var updated = await SteamDownload.FetchAsync(tool, build, account, client, Downloading, log, stop);
                if (updated.Outcome != SteamOutcome.Done) return updated;
                if (!Holds(client, build)) return new(SteamOutcome.Failed, L.Text("The download ended, but the client is not whole. Run it again."));
                ForgetPrepared(client);
                File.Delete(Path.Combine(client, Marker));
                return new(SteamOutcome.Done);
            }

            if (layout == ClientLayout.Steam && !File.Exists(Path.Combine(next, Seeded)))
            {
                // A copy of the client in use, as the start: Steam then sends only what differs.
                var copy = ClientCopy.Plan(client, folder, next);
                ClientCopy.RequireSpace(copy);
                await ClientCopy.RunAsync(copy, copied => progress?.Invoke(new("copy", copied.Total == 0 ? 0 : (double)copied.Done / copied.Total,
                    L.Text("{0:0.0} of {1:0.0} GB", copied.Done / 1e9, copied.Total / 1e9))), stop);
                await File.WriteAllTextAsync(Path.Combine(next, Seeded), "", stop);
            }
            var fetched = await SteamDownload.FetchAsync(tool, build, account, next, Downloading, log, stop);
            if (fetched.Outcome != SteamOutcome.Done) return fetched;
            if (!Holds(next, build)) return new(SteamOutcome.Failed, L.Text("The download ended, but the client is not whole. Run it again."));
            ForgetPrepared(next);
            // LocalExile's own game settings are named by the client's config.json: they go with the folder.
            string settings = Path.Combine(client, "config.json");
            if (File.Exists(settings) && !File.Exists(Path.Combine(next, "config.json"))) File.Copy(settings, Path.Combine(next, "config.json"));

            string? aside = null;
            if (Directory.Exists(client))
            {
                aside = way == SwitchWay.KeepBoth ? FreeName(folder, ClientLocator.BuildOf(client)) : Path.Combine(folder, ClientLocator.RemovedClientFolder);
                if (way != SwitchWay.KeepBoth) ClientLocator.FinishRemoval(folder);
                Directory.Move(client, aside);
            }
            try { Directory.Move(next, client); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (aside is not null) Directory.Move(aside, client);
                throw;
            }
            if (aside is not null && way != SwitchWay.KeepBoth) ClientLocator.FinishRemoval(folder);
            return new(SteamOutcome.Done);
        }
        catch (OperationCanceledException) { return new(SteamOutcome.Stopped); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return new(SteamOutcome.Failed, e.Message); }
    }
}
