using System.Security.Cryptography;

namespace Waypoint.Tests;

/// <summary>Which client versions a LocalExile runs is that LocalExile's to say (<see cref="LocalExileBuilds"/>), and a
/// client of any of them fits its folder (<see cref="ClientLocator.Supported"/>, <see cref="ClientUpdate.Check"/>).</summary>
[Collection(nameof(ClientUpdateTests))]
public class ClientBuildListTests : IDisposable
{
    readonly string _top = Path.Combine(Path.GetTempPath(), "Waypoint-builds-" + Guid.NewGuid().ToString("N"));
    readonly Dictionary<string, string> _builds = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, string> _found = [];
    readonly Dictionary<string, IReadOnlyList<ListedBuild>> _lists = new(StringComparer.OrdinalIgnoreCase);
    string Folder => Path.Combine(_top, "LocalExile");
    string Client => Path.Combine(Folder, "Client");

    // LocalExile v0.5.0-beta's answer to "builds --json", as its launcher prints it.
    static readonly string[] Answer =
    [
        """{"schema":1,"event":"build","time":"2026-11-12T03:00:00.0000000Z","version":"3.29.3c","name":"release, build 327984","executableSha256":"cc8aa38345be2a837575215d2ce142ebecf7b007345843265b335ebb8ac5964c","steamIndexSha256":null,"steamApp":null,"steamManifests":null}""",
        """{"schema":1,"event":"build","time":"2026-11-12T03:00:00.0000000Z","version":"3.29.3c","name":"3.29.3.5 hotfix of 2026-11-09, build 328126","executableSha256":"01a22facf36886b39bca74793c0d8c7295b1495f1625b4ebc7b23c2de32a70cf","steamIndexSha256":"d23c78997206716586041a3e47a89dde0eb516f1488df4a1ccf39b0d51be54d0","steamApp":"238960","steamManifests":[{"depot":"238961","manifest":"2467965126630699088"},{"depot":"238962","manifest":"1925736551784903498"}]}""",
        """{"schema":1,"event":"build","time":"2026-11-12T03:00:00.0000000Z","version":"3.29.3d","name":"release of 2026-11-10, build 328228","executableSha256":"e9ba3d31c4d922e1826ef01d02fd22178a001b29e17264b68fc2446efd41f53d","steamIndexSha256":"53560cc6391d2b11276f480ce5ebe86eb75a26935064c846aa99499f376bac29","steamApp":"238960","steamManifests":[{"depot":"238961","manifest":"5379264335337066375"},{"depot":"238962","manifest":"8687196073893351989"}]}""",
    ];

    public ClientBuildListTests()
    {
        Directory.CreateDirectory(Path.Combine(Folder, "data"));
        ClientLocator.BuildOverride = folder => _builds.TryGetValue(Path.GetFullPath(folder), out var build) ? build : null;
        ClientUpdate.FindClient = (build, _) => _found.TryGetValue(build, out var folder) ? folder : null;
        LocalExileBuilds.Override = folder => _lists.TryGetValue(Path.GetFullPath(folder), out var list) ? list : null;
    }

    public void Dispose()
    {
        ClientLocator.BuildOverride = null;
        LocalExileBuilds.Override = null;
        ClientUpdate.FindClient = (build, stop) =>
            ClientLocator.Find([], stop, TimeSpan.FromSeconds(2)).Clients.FirstOrDefault(c => c.Usable && c.Build == build)?.Folder;
        if (Directory.Exists(_top)) Directory.Delete(_top, recursive: true);
    }

    /// <summary>A release whose release.json names 3.29.3c, as every release does, with a client of <paramref name="has"/>.</summary>
    void Release(string? has, bool withList)
    {
        File.WriteAllText(Path.Combine(Folder, "release.json"), """{"version":"0.5.0-beta","commit":"b0929b2","client":"3.29.3c"}""");
        if (withList) _lists[Folder] = LocalExileBuilds.Parse(Answer);
        if (has is null) return;
        Directory.CreateDirectory(Client);
        File.WriteAllText(Path.Combine(Client, "Content.ggpk"), new string('x', 1000));
        _builds[Client] = has;
    }

    [Fact]
    public void The_launchers_answer_names_each_build_with_its_Steam_manifests_as_text()
    {
        var builds = LocalExileBuilds.Parse([.. Answer, "not an event", """{"schema":1,"event":"info","message":"x"}""", """{"event":"build","version":"9.9.9","executableSha256":"short"}"""]);
        Assert.Equal(["3.29.3c", "3.29.3c", "3.29.3d"], builds.Select(b => b.Version));
        Assert.False(builds[0].FromSteam);
        Assert.Null(builds[0].SteamIndexSha256);
        var d = builds[2];
        Assert.True(d.FromSteam);
        Assert.Equal("238960", d.SteamApp);
        Assert.Equal([new SteamDepot("238961", "5379264335337066375"), new SteamDepot("238962", "8687196073893351989")], d.Depots);
        Assert.Equal("release of 2026-11-10, build 328228", d.Name);
    }

    [Fact]
    public void A_source_copys_list_is_read_from_its_ClientBuilds_file()
    {
        const string source = """
            public static class ClientBuilds
            {
                public const uint SteamApp = 238960;

                public static IReadOnlyList<ClientBuild> Allowed { get; } =
                [
                    // Its executables are depot 238962 manifest 6963407386864245615; its Steam game files were never checked.
                    new("3.29.3c", "release, build 327984",
                        "cc8aa38345be2a837575215d2ce142ebecf7b007345843265b335ebb8ac5964c", 0x142feaed0),
                    new("3.29.3d", "release of 2026-11-10, build 328228",
                        "e9ba3d31c4d922e1826ef01d02fd22178a001b29e17264b68fc2446efd41f53d", 0x142feaeb0,
                        SteamIndexSha256: "53560cc6391d2b11276f480ce5ebe86eb75a26935064c846aa99499f376bac29",
                        SteamManifests: [new(238961, 5379264335337066375), new(238962, 8687196073893351989)]),
                ];
            }
            """;
        var builds = LocalExileBuilds.ParseSource(source);
        Assert.Equal(["3.29.3c", "3.29.3d"], builds.Select(b => b.Version));
        Assert.False(builds[0].FromSteam);
        Assert.Empty(builds[0].Depots);
        Assert.Equal("53560cc6391d2b11276f480ce5ebe86eb75a26935064c846aa99499f376bac29", builds[1].SteamIndexSha256);
        Assert.Equal("238960", builds[1].SteamApp);
        Assert.Equal([new SteamDepot("238961", "5379264335337066375"), new SteamDepot("238962", "8687196073893351989")], builds[1].Depots);
        Assert.Empty(LocalExileBuilds.ParseSource("nothing like a list"));
    }

    [Fact]
    public void A_LocalExile_that_lists_its_builds_runs_every_version_listed()
    {
        Release("3.29.3c", withList: true);
        Assert.Equal(["3.29.3d", "3.29.3c"], ClientLocator.Supported(Folder));
        // Its 3.29.3c client fits, although a newer version is listed and a client of it is on the PC.
        Assert.Equal("3.29.3c", ClientLocator.Needed(Folder));
        _found["3.29.3d"] = @"E:\Path of Exile";
        Assert.Null(ClientUpdate.Check(Folder, lookForNewer: true));
        Assert.Null(ClientLocator.Outdated(Folder));
        Assert.Null(ClientUpdate.OlderVersionOf(Folder));
        _builds[Client] = "3.29.3d";
        Assert.Equal("3.29.3d", ClientLocator.Needed(Folder));
        Assert.Null(ClientUpdate.Check(Folder, lookForNewer: true));
        Assert.Null(ClientLocator.Newer(Folder));
    }

    [Fact]
    public void A_release_that_cannot_list_them_runs_the_one_version_it_names()
    {
        Release("3.29.3d", withList: false);
        Assert.Equal(["3.29.3c"], ClientLocator.Supported(Folder));
        // v0.4.3-beta with a client the official launcher took to 3.29.3d: LocalExile needs the update, not the client.
        var plan = ClientUpdate.Check(Folder)!;
        Assert.Equal(ClientUpdate.Kind.UpdateLocalExile, plan.Kind);
        Assert.Equal(("3.29.3d", "3.29.3c"), (plan.Have, plan.Need));
        // It is on a current LocalExile all the same: no note that its version is going away.
        Assert.Null(ClientUpdate.OlderVersionOf(Folder));
    }

    [Fact]
    public void An_older_client_moves_to_whichever_listed_version_is_on_the_PC()
    {
        Release("3.29.3b", withList: true);
        _found["3.29.3c"] = @"D:\Path of Exile 3.29.3c";
        var plan = ClientUpdate.Check(Folder)!;
        Assert.Equal(ClientUpdate.Kind.Replace, plan.Kind);
        Assert.Equal(("3.29.3b", "3.29.3c", @"D:\Path of Exile 3.29.3c"), (plan.Have, plan.Need, plan.Found));
        // The newest listed version first, when both are there.
        _found["3.29.3d"] = @"E:\Path of Exile";
        plan = ClientUpdate.Check(Folder)!;
        Assert.Equal(("3.29.3d", @"E:\Path of Exile"), (plan.Need, plan.Found));
        Assert.Equal(@"E:\Path of Exile", ClientUpdate.FindCurrent());
    }

    [Fact]
    public void A_build_a_LocalExile_lists_is_recognized_by_its_executable()
    {
        // A build Waypoint has no row for: the list names its executable's hash and its version.
        ClientLocator.BuildOverride = null;
        string client = Path.Combine(_top, "A newer client");
        Directory.CreateDirectory(client);
        byte[] executable = new byte[17 << 20];
        executable[0] = 0x4d;
        executable[^1] = 0x5a;
        File.WriteAllBytes(Path.Combine(client, "PathOfExileSteam.exe"), executable);
        Assert.Null(ClientLocator.BuildOf(client));
        ClientLocator.Learn([new("3.29.3z", "a later patch", Convert.ToHexStringLower(SHA256.HashData(executable)))]);
        Assert.Equal("3.29.3z", ClientLocator.BuildOf(client));
        Assert.True(ClientLocator.IsOlder("3.29.3d", "3.29.3z"));
        Assert.Single(ClientLocator.ExecutableHashesFor("3.29.3z"));
    }
}
