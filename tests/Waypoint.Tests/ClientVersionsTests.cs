using System.Security.Cryptography;

namespace Waypoint.Tests;

/// <summary>The versions of the game a LocalExile folder holds (<see cref="ClientVersions"/>), and what DepotDownloader's
/// output says (<see cref="SteamDownload"/>). The lines are the tool's own, as version 3.4.0 printed them.</summary>
[Collection(nameof(ClientUpdateTests))]
public class ClientVersionsTests : IDisposable
{
    readonly string _top = Path.Combine(Path.GetTempPath(), "Waypoint-versions-" + Guid.NewGuid().ToString("N"));
    readonly Dictionary<string, string> _builds = new(StringComparer.OrdinalIgnoreCase);
    string Folder => Path.Combine(_top, "LocalExile");
    string Client => Path.Combine(Folder, "Client");

    public ClientVersionsTests()
    {
        Directory.CreateDirectory(Folder);
        ClientLocator.BuildOverride = folder => _builds.TryGetValue(Path.GetFullPath(folder), out var build) ? build : null;
    }

    public void Dispose()
    {
        ClientLocator.BuildOverride = null;
        if (Directory.Exists(_top)) Directory.Delete(_top, recursive: true);
    }

    /// <summary>A client as Steam lays it out, of <paramref name="version"/>, with what LocalExile adds beside it.</summary>
    string Steam(string folder, string version, string mark = "")
    {
        Directory.CreateDirectory(Path.Combine(folder, "Bundles2"));
        File.WriteAllText(Path.Combine(folder, "Bundles2", "_.index.bin"), "index of " + version);
        File.WriteAllText(Path.Combine(folder, "PathOfExileSteam.exe"), "game " + version + mark);
        File.WriteAllText(Path.Combine(folder, "Game.patched.exe"), "patched " + version);
        _builds[folder] = version;
        return folder;
    }

    [Fact]
    public void A_folders_client_is_told_as_LocalExile_tells_it()
    {
        Assert.Equal(ClientLayout.None, ClientVersions.Layout(Folder));
        Steam(Client, "3.29.3d");
        Assert.Equal(ClientLayout.Steam, ClientVersions.Layout(Folder));
        // A Content.ggpk wins, as in LocalExile's own check.
        File.WriteAllText(Path.Combine(Client, "Content.ggpk"), "archive");
        Assert.Equal(ClientLayout.Standalone, ClientVersions.Layout(Folder));
    }

    [Fact]
    public void A_kept_client_comes_back_by_two_renames_and_the_one_in_use_takes_its_place()
    {
        Steam(Client, "3.29.3d");
        string kept = Steam(Path.Combine(Folder, ClientVersions.KeptFolder, "3.29.3c"), "3.29.3c");
        var listed = Assert.Single(ClientVersions.Kept(Folder));
        Assert.Equal(("3.29.3c", "3.29.3c", kept), (listed.Name, listed.Version, listed.Folder));
        Assert.True(listed.Bytes > 0);

        ClientVersions.Use(Folder, "3.29.3c");
        Assert.Equal("game 3.29.3c", File.ReadAllText(Path.Combine(Client, "PathOfExileSteam.exe")));
        // What LocalExile prepared for it is still there: nothing is downloaded or patched again.
        Assert.Equal("patched 3.29.3c", File.ReadAllText(Path.Combine(Client, "Game.patched.exe")));
        string aside = Path.Combine(Folder, ClientVersions.KeptFolder, "3.29.3d");
        Assert.Equal("game 3.29.3d", File.ReadAllText(Path.Combine(aside, "PathOfExileSteam.exe")));

        ClientVersions.Delete(Folder, "3.29.3d");
        Assert.False(Directory.Exists(aside));
        Assert.Empty(ClientVersions.Kept(Folder));
        Assert.True(File.Exists(Path.Combine(Client, "PathOfExileSteam.exe")));
    }

    [Fact]
    public void Two_kept_clients_of_one_version_keep_apart_and_an_unfinished_client_is_not_set_aside()
    {
        Steam(Client, "3.29.3c", " in use");
        Steam(Path.Combine(Folder, ClientVersions.KeptFolder, "3.29.3c"), "3.29.3c", " kept");
        ClientVersions.Use(Folder, "3.29.3c");
        Assert.Equal("game 3.29.3c kept", File.ReadAllText(Path.Combine(Client, "PathOfExileSteam.exe")));
        // The name was taken while the two changed places: the one set aside gets a number.
        Assert.Equal("game 3.29.3c in use", File.ReadAllText(Path.Combine(Folder, ClientVersions.KeptFolder, "3.29.3c (2)", "PathOfExileSteam.exe")));

        File.WriteAllText(Path.Combine(Client, ClientVersions.Marker), "3.29.3d");
        Assert.Equal("3.29.3d", ClientVersions.Unfinished(Folder));
        Assert.Throws<IOException>(() => ClientVersions.Use(Folder, "3.29.3c (2)"));
        Assert.True(Directory.Exists(Path.Combine(Folder, ClientVersions.KeptFolder, "3.29.3c (2)")));
        Assert.Equal("game 3.29.3c kept", File.ReadAllText(Path.Combine(Client, "PathOfExileSteam.exe")));
    }

    [Fact]
    public void Every_build_Steam_can_deliver_is_offered_but_those_the_folder_holds()
    {
        static ListedBuild Build(string version, string name, string hash, bool steam = true) =>
            new(version, name, hash, null, steam ? "238960" : null, steam ? [new("238961", "1" + hash.Length)] : null);
        ListedBuild release = Build("3.29.3c", "release", "aa"), plain = Build("3.29.3c", "not on Steam", "ab", steam: false),
            hotfix = Build("3.29.3c", "hotfix", "bb"), d = Build("3.29.3d", "release", "cc");
        ListedBuild[] listed = [release, plain, hotfix, d];

        // Newest first, a version's two builds each on a line of its own, and never one Steam cannot deliver.
        Assert.Equal([d, hotfix, release], ClientVersions.Offered(listed, null, whole: true, []));
        Assert.True(ClientVersions.Several(listed, "3.29.3c"));
        Assert.False(ClientVersions.Several(listed, "3.29.3d"));
        // The hotfix in use still leaves the release of the same version to get, and a kept build is not offered again.
        Assert.Equal([d, release], ClientVersions.Offered(listed, hotfix, whole: true, []));
        Assert.Equal([release], ClientVersions.Offered(listed, hotfix, whole: true, [d, null]));
        // A client half-way to another build may also go back to the one it was.
        Assert.Equal([d, hotfix, release], ClientVersions.Offered(listed, hotfix, whole: false, []));
    }

    [Fact]
    public void The_newest_build_on_offer_is_the_highest_version_whatever_the_order_and_a_hotfix_before_its_release()
    {
        static ListedBuild Build(string version, string name) => new(version, name, name, null, "238960", [new("238961", "1")]);
        ListedBuild release = Build("3.29.3c", "release"), hotfix = Build("3.29.3c", "hotfix"), d = Build("3.29.3d", "d");
        // As Offered gives them (newest first), and in an order no list has today: the version decides, not the place.
        Assert.Same(d, ClientVersions.Newest([d, hotfix, release]));
        Assert.Same(d, ClientVersions.Newest([hotfix, release, d]));
        // Of one version's builds, the first offered: LocalExile's latest of them.
        Assert.Same(hotfix, ClientVersions.Newest([hotfix, release]));
        Assert.Null(ClientVersions.Newest([]));
    }

    [Fact]
    public void A_version_of_two_listed_builds_is_several_whether_Steam_delivers_both_or_not()
    {
        // LocalExile v0.5.0-beta's own list: the 3.29.3c release has no Steam data, its hotfix has.
        ListedBuild[] listed = [new("3.29.3c", "release", "aa"), new("3.29.3c", "hotfix", "bb", null, "238960", [new("238961", "1")]),
            new("3.29.3d", "release", "cc", null, "238960", [new("238961", "2")])];
        Assert.True(ClientVersions.Several(listed, "3.29.3c"));
        Assert.False(ClientVersions.Several(listed, "3.29.3d"));
    }

    [Fact]
    public void A_clients_files_say_standalone_or_Steam_and_a_copy_being_deleted_is_not_listed()
    {
        Steam(Client, "3.29.3d");
        Assert.Equal(ClientLayout.Steam, ClientVersions.FilesOf(Client));
        File.WriteAllText(Path.Combine(Client, "Content.ggpk"), "archive");
        Assert.Equal(ClientLayout.Standalone, ClientVersions.FilesOf(Client));
        Assert.Equal(ClientLayout.None, ClientVersions.FilesOf(Path.Combine(Folder, "nothing")));

        Steam(Path.Combine(Folder, ClientVersions.KeptFolder, "3.29.3c"), "3.29.3c");
        Steam(Path.Combine(Folder, ClientVersions.KeptFolder, ".deleting-3.29.3b"), "3.29.3b");
        Assert.Equal("3.29.3c", Assert.Single(ClientVersions.Kept(Folder)).Name);
    }

    [Fact]
    public async Task A_client_half_way_to_another_version_is_never_set_aside_or_put_in_use()
    {
        var d = new ListedBuild("3.29.3d", "", "dd", null, "238960", [new("238962", "1")]);
        Steam(Client, "3.29.3c");
        File.WriteAllText(Path.Combine(Client, ClientVersions.Marker), "3.29.3d\ndd");
        // Keeping both would put a client that is not whole in Clients\ under a whole one's name: refused before anything runs.
        var result = await ClientVersions.SwitchAsync(Folder, d, SwitchWay.KeepBoth, "no tool is started", "nobody", null, _ => { }, CancellationToken.None);
        Assert.Equal(SteamOutcome.Failed, result.Outcome);
        Assert.False(Directory.Exists(Path.Combine(Folder, ClientVersions.KeptFolder)));
        Assert.False(Directory.Exists(Path.Combine(Folder, ClientVersions.NextFolder)));
        Assert.Equal("3.29.3d", ClientVersions.Unfinished(Folder));

        // One that was set aside half-way by an earlier Waypoint says so, and does not come back into use.
        File.Delete(Path.Combine(Client, ClientVersions.Marker));
        string kept = Steam(Path.Combine(Folder, ClientVersions.KeptFolder, "3.29.3b"), "3.29.3b");
        File.WriteAllText(Path.Combine(kept, ClientVersions.Marker), "3.29.3c");
        Assert.Equal("3.29.3c", Assert.Single(ClientVersions.Kept(Folder)).HalfWayTo);
        Assert.Throws<IOException>(() => ClientVersions.Use(Folder, "3.29.3b"));
        Assert.Equal("game 3.29.3c", File.ReadAllText(Path.Combine(Client, "PathOfExileSteam.exe")));
    }

    [Fact]
    public void A_deletion_of_a_kept_client_that_was_cut_short_is_finished_and_nothing_else_goes()
    {
        Steam(Client, "3.29.3d");
        string kept = Steam(Path.Combine(Folder, ClientVersions.KeptFolder, "3.29.3c"), "3.29.3c");
        Assert.False(ClientVersions.HasUnfinishedDeletes(Folder));
        string remains = Steam(Path.Combine(Folder, ClientVersions.KeptFolder, ".deleting-3.29.3b"), "3.29.3b");
        Assert.True(ClientVersions.HasUnfinishedDeletes(Folder));

        ClientVersions.FinishDeletes(Folder);
        Assert.False(Directory.Exists(remains));
        Assert.False(ClientVersions.HasUnfinishedDeletes(Folder));
        Assert.True(File.Exists(Path.Combine(kept, "PathOfExileSteam.exe")));
        Assert.True(File.Exists(Path.Combine(Client, "PathOfExileSteam.exe")));
    }

    [Fact]
    public async Task Only_a_kept_client_from_Steam_that_is_there_can_be_finished()
    {
        var d = new ListedBuild("3.29.3d", "", "dd", null, "238960", [new("238962", "1")]);
        Steam(Client, "3.29.3d");
        // The mark names the build it was on its way to, for the window to find it in LocalExile's list.
        string kept = Steam(Path.Combine(Folder, ClientVersions.KeptFolder, "3.29.3c"), "3.29.3c");
        File.WriteAllText(Path.Combine(kept, ClientVersions.Marker), "3.29.3d\ndd");
        var half = Assert.Single(ClientVersions.Kept(Folder));
        Assert.Equal(("3.29.3d", "dd"), (half.HalfWayTo, half.HalfWayToBuild));

        // Nothing is started for a copy that is gone, or for one that is not a client from Steam: both are refused first.
        var gone = await ClientVersions.FinishKeptAsync(Folder, "3.29.3b", d, "no tool is started", "nobody", null, _ => { }, CancellationToken.None);
        Assert.Equal(SteamOutcome.Failed, gone.Outcome);
        File.WriteAllText(Path.Combine(kept, "Content.ggpk"), "archive");
        var standalone = await ClientVersions.FinishKeptAsync(Folder, "3.29.3c", d, "no tool is started", "nobody", null, _ => { }, CancellationToken.None);
        Assert.Equal(SteamOutcome.Failed, standalone.Outcome);
        Assert.True(File.Exists(Path.Combine(kept, ClientVersions.Marker)));
        Assert.Equal("game 3.29.3d", File.ReadAllText(Path.Combine(Client, "PathOfExileSteam.exe")));
    }

    [Fact]
    public void An_unfinished_switch_names_its_build_when_the_mark_does()
    {
        Steam(Client, "3.29.3c");
        Assert.Null(ClientVersions.UnfinishedBuild(Folder));
        File.WriteAllText(Path.Combine(Client, ClientVersions.Marker), "3.29.3c\nbb\n");
        Assert.Equal(("3.29.3c", "bb"), ClientVersions.UnfinishedBuild(Folder));
        Assert.Equal("3.29.3c", ClientVersions.Unfinished(Folder));
        // A mark with the version alone, as it was first written.
        File.WriteAllText(Path.Combine(Client, ClientVersions.Marker), "3.29.3d");
        Assert.Equal(("3.29.3d", null), ClientVersions.UnfinishedBuild(Folder));
    }

    [Fact]
    public void A_switch_needs_room_by_how_it_is_done()
    {
        const long build = 71_000_000_000, current = 70_000_000_000;
        // Where it is: only what differs.
        Assert.Equal(ClientVersions.Margin, ClientVersions.Needed(ClientLayout.Steam, SwitchWay.UpdateInPlace, build, current));
        // Both kept: a copy of the client in use, then what differs.
        Assert.Equal(current + ClientVersions.Margin, ClientVersions.Needed(ClientLayout.Steam, SwitchWay.KeepBoth, build, current));
        // A standalone client shares nothing with Steam's files, and no client at all needs the whole build.
        Assert.Equal(build + ClientVersions.Margin, ClientVersions.Needed(ClientLayout.Standalone, SwitchWay.UpdateInPlace, build, current));
        Assert.Equal(build + ClientVersions.Margin, ClientVersions.Needed(ClientLayout.None, SwitchWay.KeepBoth, build, 0));
    }

    [Fact]
    public void A_client_holds_a_build_when_its_executable_and_its_index_are_that_builds()
    {
        Steam(Client, "3.29.3d");
        string index = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(Client, "Bundles2", "_.index.bin"))));
        Assert.True(ClientVersions.Holds(Client, new("3.29.3d", "", new string('a', 64), index)));
        Assert.False(ClientVersions.Holds(Client, new("3.29.3d", "", new string('a', 64), new string('b', 64))));
        Assert.False(ClientVersions.Holds(Client, new("3.29.3c", "", new string('a', 64), index)));
    }

    [Fact]
    public void The_tools_lines_are_read_for_what_they_say()
    {
        var progress = SteamDownload.Read(@" 03.86% F:\LocalExile\Client\D3D12\d3d12SDKLayers.dll");
        Assert.Equal((SteamLineKind.Progress, 3.86, @"F:\LocalExile\Client\D3D12\d3d12SDKLayers.dll"), (progress.Kind, progress.Percent, progress.File));
        Assert.Equal(100, SteamDownload.Read(@"100.00% F:\LocalExile\Client\PathOfExileSteam.exe").Percent);
        Assert.Equal(SteamLineKind.NotAvailable, SteamDownload.Read("App 238960 (Path of Exile) is not available from this account.").Kind);
        Assert.Equal(SteamLineKind.SignInNeeded, SteamDownload.Read("Access token was rejected (InvalidPassword).").Kind);
        Assert.Equal(SteamLineKind.CodeStart, SteamDownload.Read("Use the Steam Mobile App to sign in with this QR code:").Kind);
        var signedIn = SteamDownload.Read("Success! Next time you can login with -username some_name -remember-password instead of -qr.");
        Assert.Equal((SteamLineKind.SignedIn, "some_name"), (signedIn.Kind, signedIn.Account));
        Assert.Equal(SteamLineKind.Other, SteamDownload.Read("Got depot key for 238962 result: OK").Kind);
        Assert.Equal(SteamLineKind.Other, SteamDownload.Read("").Kind);

        // A row of the sign-in code: two characters a square.
        string row = "        " + string.Concat(Enumerable.Repeat("\u2588\u2588  ", 10)) + "        ";
        var code = SteamDownload.Read(row);
        Assert.Equal(SteamLineKind.CodeRow, code.Kind);
        Assert.Equal(row.Length / 2, code.Cells!.Length);
        Assert.Equal([false, false, false, false, true, false, true, false], code.Cells.Take(8));
    }

    [Fact]
    public void A_log_never_gets_the_sign_in_code_or_the_accounts_name()
    {
        Assert.Null(SteamDownload.ForLog("        " + new string('\u2588', 40), null));
        Assert.Null(SteamDownload.ForLog(" 50.00% file.dll", null));
        Assert.Equal("Success! Next time you can login with -username <account> -remember-password instead of -qr.",
            SteamDownload.ForLog("Success! Next time you can login with -username some_name -remember-password instead of -qr.", null));
        Assert.Equal("Logging '<account>' into Steam3...", SteamDownload.ForLog("Logging 'some_name' into Steam3...", "some_name"));
        Assert.Equal("Got 12 licenses for <account>!", SteamDownload.ForLog("Got 12 licenses for some_name!", "some_name"));
    }

    /// <summary>The real tool against Steam, on the small depot of executables only: opt in by naming a folder that holds
    /// DepotDownloader.exe and an account whose sign-in it remembers (WAYPOINT_STEAM_TOOL, WAYPOINT_STEAM_ACCOUNT). It
    /// downloads about 130 MB.</summary>
    [Fact]
    public async Task With_the_real_tool_a_client_moves_between_versions_both_ways()
    {
        string? tool = Environment.GetEnvironmentVariable("WAYPOINT_STEAM_TOOL"), account = Environment.GetEnvironmentVariable("WAYPOINT_STEAM_ACCOUNT");
        if (string.IsNullOrEmpty(tool) || string.IsNullOrEmpty(account)) return;
        ClientLocator.BuildOverride = null;
        // LocalExile's two builds, with only the depot of executables: the index is a stand-in, so no hash is asked of it.
        var d = new ListedBuild("3.29.3d", "", "e9ba3d31c4d922e1826ef01d02fd22178a001b29e17264b68fc2446efd41f53d", null, "238960", [new("238962", "8687196073893351989")]);
        var c = new ListedBuild("3.29.3c", "", "01a22facf36886b39bca74793c0d8c7295b1495f1625b4ebc7b23c2de32a70cf", null, "238960", [new("238962", "1925736551784903498")]);
        ClientLocator.Learn([d, c]);
        var log = new List<string>();
        void Index(string client)
        {
            Directory.CreateDirectory(Path.Combine(client, "Bundles2"));
            File.WriteAllText(Path.Combine(client, "Bundles2", "_.index.bin"), "stand-in");
        }

        // No client yet: the build is made ready beside it and moved in. (The stand-in index is put where the fetch goes.)
        Index(Path.Combine(Folder, ClientVersions.NextFolder));
        var first = await ClientVersions.SwitchAsync(Folder, d, SwitchWay.KeepBoth, tool, account, null, log.Add, default);
        Assert.True(first.Outcome == SteamOutcome.Done, string.Join("\n", log.TakeLast(8)));
        Assert.Equal("3.29.3d", ClientLocator.BuildOf(Client));
        Assert.DoesNotContain(log, line => line.Contains(account, StringComparison.OrdinalIgnoreCase));

        // Both kept: the client in use goes to Clients\, the other version takes its place.
        File.WriteAllText(Path.Combine(Client, "Game.patched.exe"), "patched for 3.29.3d");
        File.WriteAllText(Path.Combine(Client, "config.json"), "{}");
        var both = await ClientVersions.SwitchAsync(Folder, c, SwitchWay.KeepBoth, tool, account, null, log.Add, default);
        Assert.True(both.Outcome == SteamOutcome.Done, string.Join("\n", log.TakeLast(8)));
        Assert.Equal("3.29.3c", ClientLocator.BuildOf(Client));
        Assert.False(File.Exists(Path.Combine(Client, "Game.patched.exe")));
        Assert.True(File.Exists(Path.Combine(Client, "config.json")));
        var kept = Assert.Single(ClientVersions.Kept(Folder));
        Assert.Equal("3.29.3d", kept.Version);
        Assert.True(File.Exists(Path.Combine(kept.Folder, "Game.patched.exe")));

        // Where it is: the one client becomes the other version, and nothing more is kept.
        var inPlace = await ClientVersions.SwitchAsync(Folder, d, SwitchWay.UpdateInPlace, tool, account, null, log.Add, default);
        Assert.True(inPlace.Outcome == SteamOutcome.Done, string.Join("\n", log.TakeLast(8)));
        Assert.Equal("3.29.3d", ClientLocator.BuildOf(Client));
        Assert.Null(ClientVersions.Unfinished(Folder));
        Assert.Single(ClientVersions.Kept(Folder));

        // And back, without a download.
        ClientVersions.Use(Folder, kept.Name);
        Assert.Equal("3.29.3d", ClientLocator.BuildOf(Client));
        Assert.True(File.Exists(Path.Combine(Client, "Game.patched.exe")));
    }
}
