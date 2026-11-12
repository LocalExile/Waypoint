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
        Assert.Equal(["3.29.3d", "3.29.3c"], ClientLocator.Current);
        Assert.Equal("3.29.3d", ClientLocator.Build);
        Assert.True(ClientLocator.IsOlder("3.29.3b", "3.29.3c"));
        Assert.True(ClientLocator.IsOlder("3.29.3c", "3.29.3d"));
        Assert.True(ClientLocator.IsOlder("3.29.3d", "3.30.0"));
        Assert.False(ClientLocator.IsOlder("3.29.3c", "3.29.3b"));
        Assert.False(ClientLocator.IsOlder("3.29.3c", "3.29.3c"));
        Assert.False(ClientLocator.IsOlder("not a version", "3.29.3c"));
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
        int asked = 0;
        var (outcome, plan) = await ClientUpdate.EnsureAsync(Folder, false, _ => ++asked > 0, _ => throw new InvalidOperationException("not this question"),
            message => throw new InvalidOperationException(message), Hold);
        Assert.Equal((ClientUpdate.Outcome.Replaced, 1), (outcome, asked));
        Assert.False(plan!.Link);
        Assert.False(Directory.Exists(Client));
        Assert.False(Directory.Exists(Path.Combine(Folder, ClientLocator.RemovedClientFolder)));
        Assert.Equal("saves", File.ReadAllText(Path.Combine(Folder, "data", "saves.db")));
    }

    [Fact]
    public async Task With_only_an_older_client_a_no_to_the_older_version_starts_nothing()
    {
        Release("3.29.3c", "3.29.3b");
        int asked = 0;
        var (outcome, _) = await ClientUpdate.EnsureAsync(Folder, false, _ => throw new InvalidOperationException("not this question"),
            _ => { asked++; return false; }, _ => { }, Hold);
        Assert.Equal((ClientUpdate.Outcome.Stopped, 1), (outcome, asked));
        Assert.True(Directory.Exists(Client));
    }

    [Fact]
    public async Task Not_now_to_a_move_that_must_happen_changes_nothing_and_starts_nothing()
    {
        Release("3.29.3c", "3.29.3b");
        _found["3.29.3c"] = @"E:\Path of Exile";
        var (outcome, plan) = await ClientUpdate.EnsureAsync(Folder, false, _ => false, _ => true, _ => { }, Hold);
        Assert.Equal(ClientUpdate.Outcome.Stopped, outcome);
        Assert.False(ClientUpdate.WorksAnyway(plan!));
        Assert.True(File.Exists(Path.Combine(Client, "Content.ggpk")));
    }

    [Fact]
    public async Task Not_now_to_a_move_from_a_working_older_setup_plays_on_and_is_not_asked_again()
    {
        Release("3.29.3b", "3.29.3b");
        _found["3.29.3c"] = @"E:\Path of Exile";
        int asked = 0;
        var first = await ClientUpdate.EnsureAsync(Folder, true, _ => { asked++; return false; }, _ => true, _ => { }, Hold);
        Assert.Equal(ClientUpdate.Outcome.Fits, first.Outcome);
        Assert.True(ClientUpdate.WorksAnyway(first.Plan!));
        var again = await ClientUpdate.EnsureAsync(Folder, true, _ => { asked++; return false; }, _ => true, _ => { }, Hold);
        Assert.Equal((ClientUpdate.Outcome.Fits, 1), (again.Outcome, asked));
        Assert.True(File.Exists(Path.Combine(Client, "Content.ggpk")));
    }

    [Fact]
    public async Task Continue_on_a_working_older_setup_hands_LocalExile_s_update_to_the_caller_and_the_next_round_asks_nothing()
    {
        Release("3.29.3b", "3.29.3b");
        string newer = Retail("3.29.3c");
        _found["3.29.3c"] = newer;
        var (outcome, _) = await ClientUpdate.EnsureAsync(Folder, true, _ => true, _ => true, _ => { }, Hold);
        Assert.Equal(ClientUpdate.Outcome.UpdateLocalExile, outcome);
        // LocalExile updated: the folder now needs 3.29.3c, and the move was already agreed to, with that client: no second
        // question and no second search, which could miss it.
        Release("3.29.3c", "3.29.3b");
        _found.Clear();
        var after = await ClientUpdate.EnsureAsync(Folder, true, _ => throw new InvalidOperationException("asked twice"), _ => true, _ => { }, Hold);
        Assert.Equal(ClientUpdate.Outcome.Replaced, after.Outcome);
        Assert.Equal(["--client", newer, "--client-mode", "copy"], ClientUpdate.ClientArguments(after.Plan!));
    }

    /// <summary>A retail client folder of <paramref name="build"/> elsewhere on the "PC".</summary>
    string Retail(string build, bool prepared = false)
    {
        string folder = Path.Combine(_top, "retail " + build + (prepared ? " prepared" : ""));
        Directory.CreateDirectory(folder);
        foreach (string file in prepared ? new[] { "Content.ggpk", "PathOfExile.exe", "Game.patched.exe" } : ["Content.ggpk", "PathOfExile.exe"])
            File.WriteAllText(Path.Combine(folder, file), "stand-in");
        _builds[folder] = build;
        return folder;
    }

    [Fact]
    public void Before_a_daily_update_a_client_that_will_not_fit_is_settled_first()
    {
        Directory.CreateDirectory(Path.Combine(Folder, "scripts", "release"));
        File.WriteAllText(Path.Combine(Folder, "scripts", "release", "build-release.ps1"), "[ordered]@{ client = '3.29.3b' }");
        Directory.CreateDirectory(Client);
        File.WriteAllText(Path.Combine(Client, "PathOfExile.exe"), "stand-in");
        _builds[Client] = "3.29.3b";

        // Nothing changes for the client, or the update stays on the same build: go.
        Assert.True(ClientUpdate.BeforeUpdate(Folder, "3.29.3b").Go);
        Assert.True(ClientUpdate.BeforeUpdate(Folder, null).Go);
        // Only the older client: the copy stays where it works, and says why.
        var only = ClientUpdate.BeforeUpdate(Folder, "3.29.3c");
        Assert.False(only.Go);
        Assert.Null(only.Ask);
        Assert.Contains("3.29.3b", only.Staying);
        // A 3.29.3c client elsewhere: asked first; Not now stays, and is not asked again.
        _found["3.29.3c"] = Retail("3.29.3c");
        var ask = ClientUpdate.BeforeUpdate(Folder, "3.29.3c");
        Assert.False(ask.Go);
        Assert.True(ClientUpdate.WorksAnyway(ask.Ask!));
        Assert.Contains("play on 3.29.3b", ClientUpdate.MoveText(ask.Ask!).Message);
        Assert.NotNull(ClientUpdate.Answer(ask.Ask!, false));
        var again = ClientUpdate.BeforeUpdate(Folder, "3.29.3c");
        Assert.False(again.Go);
        Assert.Null(again.Ask);
    }

    [Fact]
    public void Before_a_daily_update_Continue_lets_the_copy_move()
    {
        Directory.CreateDirectory(Path.Combine(Folder, "scripts", "release"));
        File.WriteAllText(Path.Combine(Folder, "scripts", "release", "build-release.ps1"), "[ordered]@{ client = '3.29.3b' }");
        Directory.CreateDirectory(Client);
        File.WriteAllText(Path.Combine(Client, "PathOfExile.exe"), "stand-in");
        _builds[Client] = "3.29.3b";
        _found["3.29.3c"] = Retail("3.29.3c");
        var ask = ClientUpdate.BeforeUpdate(Folder, "3.29.3c").Ask!;
        Assert.Null(ClientUpdate.Answer(ask, true));
        Assert.True(ClientUpdate.BeforeUpdate(Folder, "3.29.3c").Go);
    }

    [Fact]
    public void A_copy_with_no_client_and_only_an_older_one_found_is_offered_the_older_version()
    {
        Directory.CreateDirectory(Path.Combine(Folder, "scripts", "release"));
        File.WriteAllText(Path.Combine(Folder, "scripts", "release", "build-release.ps1"), "[ordered]@{ client = '3.29.3c' }");
        Assert.Null(ClientUpdate.Check(Folder));
        string older = Retail("3.29.3b");
        _found["3.29.3b"] = older;
        var plan = ClientUpdate.Check(Folder)!;
        Assert.Equal((ClientUpdate.Kind.UseOlder, "3.29.3b", older), (plan.Kind, plan.Have, plan.Found));
        _found["3.29.3c"] = Retail("3.29.3c");
        Assert.Null(ClientUpdate.Check(Folder));
    }

    [Fact]
    public void The_old_client_s_game_settings_come_back_into_the_new_one()
    {
        Release("3.29.3c", "3.29.3b");
        File.WriteAllText(Path.Combine(Client, "config.json"), """{"settings":"LocalExile"}""");
        ClientLocator.RemoveClient(Folder);
        Assert.False(Directory.Exists(Client));
        Assert.False(ClientLocator.RestoreSettings(Folder));
        Directory.CreateDirectory(Client);
        File.WriteAllText(Path.Combine(Client, "PathOfExile.exe"), "new client");
        Assert.True(ClientLocator.RestoreSettings(Folder));
        Assert.Equal("""{"settings":"LocalExile"}""", File.ReadAllText(Path.Combine(Client, "config.json")));
        Assert.False(File.Exists(ClientLocator.SavedSettings(Folder)));
    }

    [Fact]
    public void Settings_the_new_client_got_meanwhile_are_kept()
    {
        Release("3.29.3c", "3.29.3b");
        File.WriteAllText(Path.Combine(Client, "config.json"), "old");
        ClientLocator.RemoveClient(Folder);
        Directory.CreateDirectory(Client);
        File.WriteAllText(Path.Combine(Client, "PathOfExile.exe"), "new client");
        File.WriteAllText(Path.Combine(Client, "config.json"), "the guide's");
        Assert.False(ClientLocator.RestoreSettings(Folder));
        Assert.Equal("the guide's", File.ReadAllText(Path.Combine(Client, "config.json")));
        Assert.False(File.Exists(ClientLocator.SavedSettings(Folder)));
    }

    [Fact]
    public void A_client_another_LocalExile_prepared_loses_its_patched_executable_after_the_copy()
    {
        Release("3.29.3c", "3.29.3c");
        File.WriteAllText(Path.Combine(Client, "Game.patched.exe"), "patched for another folder's keys");
        ClientUpdate.AfterNewClient(Folder, Retail("3.29.3c", prepared: true));
        Assert.False(File.Exists(Path.Combine(Client, "Game.patched.exe")));
        File.WriteAllText(Path.Combine(Client, "Game.patched.exe"), "patched for this folder");
        ClientUpdate.AfterNewClient(Folder, Retail("3.29.3c"));
        Assert.True(File.Exists(Path.Combine(Client, "Game.patched.exe")));
    }

    [Fact]
    public async Task After_Not_now_the_working_older_setup_is_not_searched_again()
    {
        Release("3.29.3b", "3.29.3b");
        int searches = 0;
        ClientUpdate.FindClient = (build, _) => { searches++; return _found.TryGetValue(build, out var f) ? f : null; };
        _found["3.29.3c"] = Retail("3.29.3c");
        await ClientUpdate.EnsureAsync(Folder, true, _ => false, _ => true, _ => { }, Hold);
        int first = searches;
        var (outcome, _) = await ClientUpdate.EnsureAsync(Folder, true, _ => throw new InvalidOperationException("asked again"), _ => true, _ => { }, Hold);
        Assert.Equal((ClientUpdate.Outcome.Fits, first), (outcome, searches));
    }

    [Fact]
    public void The_move_dialog_names_both_folders_and_opening_one_asks_again()
    {
        Release("3.29.3c", "3.29.3b");
        _found["3.29.3c"] = @"E:\Path of Exile";
        var plan = ClientUpdate.Check(Folder)!;
        var (message, details) = ClientUpdate.MoveText(plan);
        string text = message + " " + details;
        Assert.Contains(@"E:\Path of Exile", text);
        Assert.Contains(Client, text);
        Assert.Contains("(replaced)", details);
        Assert.Contains("can't start yet", message);
        Assert.DoesNotContain(Client, message);
        Assert.Equal([@"E:\Path of Exile", Client], ClientUpdate.MoveFolders(plan).Select(f => f.Path));
        var opened = new List<string>();
        var answers = new Queue<int>([1, 2, 0]);
        string[]? shown = null;
        Assert.True(ClientUpdate.AskMove(plan, (_, _, _, buttons) => { shown = buttons; return answers.Dequeue(); }, opened.Add));
        Assert.Equal([@"E:\Path of Exile", Client], opened);
        Assert.Equal(4, shown!.Length);
        Assert.False(ClientUpdate.AskMove(plan, (_, _, _, buttons) => buttons.Length - 1, opened.Add));
        Assert.False(ClientUpdate.AskMove(plan, (_, _, _, _) => -1, opened.Add));
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
        Assert.Equal("copy", ClientUpdate.ClientArguments(plan)[3]);
        Assert.Contains("only the link is replaced", ClientUpdate.MoveText(plan).Details);
        Assert.Single(ClientUpdate.MoveFolders(plan));
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
