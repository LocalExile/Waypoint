using System.Diagnostics;

namespace Waypoint.Tests;

/// <summary>LocalExile v0.2.0-beta moved from client 3.29.3b to 3.29.3c (<see cref="ClientUpdate"/>). A test cannot make an
/// executable of a real build's hash, so the build of each client folder is told (<see cref="ClientLocator.BuildOverride"/>),
/// and so is what the quick search finds (<see cref="ClientUpdate.FindClient"/>).</summary>
[Collection(nameof(ClientUpdateTests))]
public class ClientUpdateTests : IDisposable
{
    readonly string _top = Path.Combine(Path.GetTempPath(), "Waypoint-client-" + Guid.NewGuid().ToString("N"));
    readonly Dictionary<string, string> _builds = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, string> _found = [];
    string Folder => Path.Combine(_top, "LocalExile");
    string Client => Path.Combine(Folder, "Client");

    public ClientUpdateTests()
    {
        Directory.CreateDirectory(Path.Combine(Folder, "data"));
        File.WriteAllText(Path.Combine(Folder, "data", "saves.db"), "saves");
        ClientLocator.BuildOverride = folder => _builds.TryGetValue(Path.GetFullPath(folder), out var build) ? build : null;
        ClientUpdate.FindClient = (build, _) => _found.TryGetValue(build, out var folder) ? folder : null;
    }

    public void Dispose()
    {
        ClientLocator.BuildOverride = null;
        ClientUpdate.FindClient = (build, stop) =>
            ClientLocator.Find([], stop, TimeSpan.FromSeconds(2)).Clients.FirstOrDefault(c => c.Usable && c.Build == build)?.Folder;
        foreach (var link in new DirectoryInfo(_top).EnumerateDirectories("*", SearchOption.AllDirectories).Where(d => d.LinkTarget is not null).ToList())
            if (link.Exists) link.Delete();
        if (Directory.Exists(_top)) Directory.Delete(_top, recursive: true);
    }

    /// <summary>A release whose release.json names <paramref name="needs"/>, with a client of <paramref name="has"/>.</summary>
    void Release(string needs, string? has)
    {
        File.WriteAllText(Path.Combine(Folder, "release.json"), $$"""{"version":"0.2.0-beta","commit":"e28909f","client":"{{needs}}"}""");
        if (has is null) return;
        Directory.CreateDirectory(Client);
        File.WriteAllText(Path.Combine(Client, "Content.ggpk"), new string('x', 1000));
        _builds[Client] = has;
    }

    [Fact]
    public void A_folder_says_which_client_it_needs()
    {
        Assert.Null(ClientLocator.Needed(Folder));
        Directory.CreateDirectory(Path.Combine(Folder, "scripts", "release"));
        File.WriteAllText(Path.Combine(Folder, "scripts", "release", "build-release.ps1"), "[ordered]@{ version = $Version; commit = $commit; client = '3.29.3b'; runtime = $runtime }");
        Assert.Equal("3.29.3b", ClientLocator.Needed(Folder));
        File.WriteAllText(Path.Combine(Folder, "release.json"), """{"version":"0.2.0-beta","client":"3.29.3c"}""");
        Assert.Equal("3.29.3c", ClientLocator.Needed(Folder));
    }

    [Fact]
    public void Builds_are_ordered_and_the_older_one_keeps_its_last_version()
    {
        Assert.Equal("3.29.3c", ClientLocator.Build);
        Assert.True(ClientLocator.IsOlder("3.29.3b", "3.29.3c"));
        Assert.False(ClientLocator.IsOlder("3.29.3c", "3.29.3b"));
        Assert.False(ClientLocator.IsOlder("3.29.3a", "3.29.3c"));
        Assert.Equal(new ClientLocator.OlderVersion("v0.1.0-beta", "7724004c8f13fce2c5e8a8c29ff40ef4b4ef10de"), ClientLocator.LastVersionFor("3.29.3b"));
        Assert.Null(ClientLocator.LastVersionFor("3.29.3c"));
    }

    [Fact]
    public void A_fitting_client_starts_as_usual()
    {
        Release("3.29.3c", "3.29.3c");
        _found["3.29.3c"] = @"E:\Path of Exile";
        Assert.Null(ClientUpdate.Check(Folder, lookForNewer: true));
    }

    [Fact]
    public void An_older_client_is_replaced_by_the_one_found()
    {
        Release("3.29.3c", "3.29.3b");
        _found["3.29.3c"] = @"E:\Path of Exile";
        var plan = ClientUpdate.Check(Folder)!;
        Assert.Equal(ClientUpdate.Kind.Replace, plan.Kind);
        Assert.Equal(("3.29.3b", "3.29.3c", @"E:\Path of Exile"), (plan.Have, plan.Need, plan.Found));
        Assert.Equal(["--client", @"E:\Path of Exile", "--client-mode", "copy"], ClientUpdate.ClientArguments(plan));
    }

    [Fact]
    public void With_only_an_older_client_the_older_version_is_offered_and_the_client_stays()
    {
        Release("3.29.3c", "3.29.3b");
        var plan = ClientUpdate.Check(Folder)!;
        Assert.Equal(ClientUpdate.Kind.UseOlder, plan.Kind);
        Assert.Equal("v0.1.0-beta", plan.Older!.Release);
        Assert.True(File.Exists(Path.Combine(Client, "Content.ggpk")));
    }

    [Fact]
    public void A_client_newer_than_LocalExile_asks_for_LocalExile_s_update()
    {
        Release("3.29.3b", "3.29.3c");
        var plan = ClientUpdate.Check(Folder)!;
        Assert.Equal(ClientUpdate.Kind.UpdateLocalExile, plan.Kind);
        Assert.Null(plan.Found);
    }

    [Fact]
    public void An_older_version_is_kept_only_while_the_older_client_is_all_there_is()
    {
        Release("3.29.3b", "3.29.3b");
        Assert.Null(ClientUpdate.Check(Folder, lookForNewer: true));
        _found["3.29.3c"] = @"E:\Path of Exile";
        Assert.Null(ClientUpdate.Check(Folder, lookForNewer: false));
        var plan = ClientUpdate.Check(Folder, lookForNewer: true)!;
        Assert.Equal((ClientUpdate.Kind.UpdateLocalExile, @"E:\Path of Exile"), (plan.Kind, plan.Found));
    }

    [Fact]
    public void A_client_of_no_known_build_is_left_to_LocalExile()
    {
        Release("3.29.3c", null);
        Directory.CreateDirectory(Client);
        _found["3.29.3c"] = @"E:\Path of Exile";
        Assert.Null(ClientUpdate.Check(Folder, lookForNewer: true));
    }

    [Fact]
    public void The_quiet_note_is_only_for_an_older_version()
    {
        Release("3.29.3c", "3.29.3c");
        Assert.Null(ClientUpdate.OlderNote(Folder));
        Release("3.29.3b", "3.29.3b");
        Assert.Contains("3.30", ClientUpdate.OlderNote(Folder));
    }

    [Fact]
    public async Task Replacing_removes_the_old_copy_and_keeps_the_saves()
    {
        Release("3.29.3c", "3.29.3b");
        _found["3.29.3c"] = @"E:\Path of Exile";
        Directory.CreateDirectory(Path.Combine(Client, "Bundles"));
        File.WriteAllText(Path.Combine(Client, "Bundles", "a.bin"), "bundle");
        File.SetAttributes(Path.Combine(Client, "Bundles", "a.bin"), FileAttributes.ReadOnly);
        var (outcome, plan) = await ClientUpdate.EnsureAsync(Folder, false, _ => throw new InvalidOperationException("not asked"), message => throw new InvalidOperationException(message), Hold);
        Assert.Equal(ClientUpdate.Outcome.Replaced, outcome);
        Assert.False(plan!.Link);
        Assert.False(Directory.Exists(Client));
        Assert.False(Directory.Exists(Path.Combine(Folder, ClientLocator.RemovedClientFolder)));
        Assert.Equal("saves", File.ReadAllText(Path.Combine(Folder, "data", "saves.db")));
    }

    [Fact]
    public async Task Only_the_older_version_is_asked_and_a_no_starts_nothing()
    {
        Release("3.29.3c", "3.29.3b");
        int asked = 0;
        var (outcome, _) = await ClientUpdate.EnsureAsync(Folder, false, _ => { asked++; return false; }, _ => { }, Hold);
        Assert.Equal((ClientUpdate.Outcome.Stopped, 1), (outcome, asked));
        Assert.True(Directory.Exists(Client));
    }

    [Fact]
    public void A_linked_client_loses_only_the_link()
    {
        string shared = Path.Combine(_top, "shared client");
        Directory.CreateDirectory(shared);
        File.WriteAllText(Path.Combine(shared, "Content.ggpk"), "the shared client");
        Junction(Client, shared);
        Release("3.29.3c", null);
        _builds[Client] = "3.29.3b";
        _found["3.29.3c"] = @"E:\Path of Exile";
        var plan = ClientUpdate.Check(Folder)!;
        Assert.True(plan.Link);
        Assert.Equal(Path.TrimEndingDirectorySeparator(shared), Path.TrimEndingDirectorySeparator(plan.Target), StringComparer.OrdinalIgnoreCase);
        Assert.Equal("link", ClientUpdate.ClientArguments(plan)[3]);
        ClientLocator.RemoveClient(Folder);
        Assert.False(Directory.Exists(Client));
        Assert.Equal("the shared client", File.ReadAllText(Path.Combine(shared, "Content.ggpk")));
    }

    [Fact]
    public void A_link_inside_a_copy_is_removed_without_reaching_its_target()
    {
        string outside = Path.Combine(_top, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "keep.txt"), "keep");
        Directory.CreateDirectory(Client);
        File.WriteAllText(Path.Combine(Client, "Content.ggpk"), "old");
        Junction(Path.Combine(Client, "logs"), outside);
        ClientLocator.RemoveClient(Folder);
        Assert.False(Directory.Exists(Client));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(outside, "keep.txt")));
    }

    [Fact]
    public void A_removal_cut_short_is_finished_next_time()
    {
        string old = Path.Combine(Folder, ClientLocator.RemovedClientFolder);
        Directory.CreateDirectory(old);
        File.WriteAllText(Path.Combine(old, "Content.ggpk"), "left over");
        ClientLocator.RemoveClient(Folder);
        Assert.False(Directory.Exists(old));
    }

    [Fact]
    public void A_release_names_its_client_in_its_notes()
    {
        var release = new LocalExileRelease(ReleaseVersion.Parse("v0.2.0-beta")!, "v0.2.0-beta", "", null, null, null, 0,
            "For the Path of Exile **3.29.3c** client. Download your system's zip below and extract it.");
        Assert.Equal("3.29.3c", ClientUpdate.ClientOf(release));
        Assert.Null(ClientUpdate.ClientOf(release with { Notes = "Bug fixes." }));
    }

    [Fact]
    public void A_copy_on_the_version_for_an_older_client_is_one_Play_never_moves()
    {
        Directory.CreateDirectory(Path.Combine(Folder, ".git"));
        File.WriteAllText(Path.Combine(Folder, ".git", "HEAD"), "ref: refs/heads/waypoint-3.29.3b\n");
        Assert.True(SwitchToSource.OnTestedVersion(Folder));
        File.WriteAllText(Path.Combine(Folder, ".git", "HEAD"), "ref: refs/heads/main\n");
        Assert.False(SwitchToSource.OnTestedVersion(Folder));
    }

    static async Task<bool> Hold(Func<Task> work)
    {
        await work();
        return true;
    }

    static void Junction(string link, string target)
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
        process.WaitForExit();
        Assert.True(new DirectoryInfo(link).LinkTarget is not null, "the junction was made");
    }
}
