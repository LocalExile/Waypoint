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
public sealed record KeptClient(string Name, string? Version, string Folder, long Bytes);

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
    /// <summary>In a client that is being updated where it is: the version it is on its way to.</summary>
    public const string Marker = ".waypoint-switching";
    /// <summary>In <see cref="NextFolder"/> once the copy it starts from is whole.</summary>
    const string Seeded = ".waypoint-seeded";
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

    /// <summary>The version a client being updated where it is was on its way to, when that was cut short; null for a
    /// client that is whole.</summary>
    public static string? Unfinished(string folder)
    {
        try
        {
            string marker = Path.Combine(Client(folder), Marker);
            return File.Exists(marker) ? File.ReadAllText(marker).Trim() : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
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
                if (entry.LinkTarget is null && LayoutOf(entry.FullName) is ClientLayout.Standalone or ClientLayout.Steam)
                    kept.Add(new(entry.Name, ClientLocator.BuildOf(entry.FullName), entry.FullName, SizeOf(entry.FullName)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return kept;
    }

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
    /// the first undone when the second fails. Nothing is downloaded or copied.</summary>
    public static void Use(string folder, string name)
    {
        string client = Client(folder), kept = Path.Combine(folder, KeptFolder, name);
        if (!Directory.Exists(kept)) throw new IOException(L.Text("{0} is no longer there.", kept));
        var layout = Layout(folder);
        if (layout == ClientLayout.Link) throw new IOException(L.Text("This folder's game client is a link to another folder. Make a copy of it first."));
        if (Unfinished(folder) is not null) throw new IOException(L.Text("The game client in use is half-way to another version. Finish that switch first."));
        string? aside = null;
        if (Directory.Exists(client))
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
    }

    /// <summary>Deletes a client that was set aside: renamed first, which fails as a whole while anything holds one of its
    /// files, then deleted without following a link inside it.</summary>
    public static void Delete(string folder, string name)
    {
        string kept = Path.Combine(folder, KeptFolder, name), going = Path.Combine(folder, KeptFolder, ".deleting-" + name);
        if (!Directory.Exists(kept)) return;
        if (Directory.Exists(going)) ClientLocator.DeleteTree(going);
        Directory.Move(kept, going);
        ClientLocator.DeleteTree(going);
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
        foreach (string name in new[] { "Game.exe", "Game.patched.exe", Seeded })
            if (File.Exists(Path.Combine(client, name))) File.Delete(Path.Combine(client, name));
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
        void Downloading(SteamProgress p) =>
            progress?.Invoke(new("download", (p.Depot - 1 + p.Percent / 100) / p.Depots, L.Text("Part {0} of {1}: {2:0.0}% · {3}", p.Depot, p.Depots, p.Percent, p.File)));
        try
        {
            if (layout == ClientLayout.Steam && way == SwitchWay.UpdateInPlace)
            {
                if (Directory.Exists(next)) ClientLocator.DeleteTree(next);
                await File.WriteAllTextAsync(Path.Combine(client, Marker), build.Version, stop);
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
