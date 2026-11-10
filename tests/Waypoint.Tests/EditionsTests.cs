using System.Diagnostics;
using Launcher.Core;
using Waypoint;

namespace Waypoint.Tests;

// These tests set Repo.IsInstall, which every test shares: they run apart from the others, as the release's mod tests do.
[Collection("Release mod compatibility")]
public sealed class EditionsTests : IDisposable
{
    readonly string _folder = Path.Combine(Path.GetTempPath(), "waypoint-editions-" + Guid.NewGuid().ToString("N"));
    readonly bool _install = Repo.IsInstall;
    readonly string _upstreamUrl = Waypoint.Git.UpstreamUrl;

    // No test here reaches LocalExile on the network: its address is a folder of this test's, a repository only where a
    // test makes one.
    public EditionsTests() => Waypoint.Git.UpstreamUrl = Path.Combine(_folder, "localexile");

    public void Dispose()
    {
        Repo.IsInstall = _install;
        Waypoint.Git.UpstreamUrl = _upstreamUrl;
        if (!Directory.Exists(_folder)) return;
        // git marks its object files read-only.
        foreach (string file in Directory.GetFiles(_folder, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(_folder, recursive: true);
    }

    string Release(string name, string? commit)
    {
        string folder = Path.Combine(_folder, name);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "LocalExile.exe"), "");
        File.WriteAllText(Path.Combine(folder, "release.json"),
            commit is null ? """{"version":"0.3.0-beta"}""" : $$"""{"version":"0.3.0-beta","commit":"{{commit}}","client":"3.29.3c"}""");
        return folder;
    }

    string Source(string name)
    {
        string folder = Path.Combine(_folder, name);
        Directory.CreateDirectory(Path.Combine(folder, "scripts", "dev"));
        File.WriteAllText(Path.Combine(folder, "GameServer.slnx"), "");
        File.WriteAllText(Path.Combine(folder, "scripts", "dev", "live.ps1"), "");
        return folder;
    }

    static string Git(string directory, params string[] args)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (string a in args) info.ArgumentList.Add(a);
        using var p = Process.Start(info)!;
        string output = p.StandardOutput.ReadToEnd();
        string error = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"git {string.Join(' ', args)}: {error}");
        return output.Trim();
    }

    static void Configure(string directory)
    {
        Git(directory, "config", "user.name", "Test");
        Git(directory, "config", "user.email", "test@example.test");
        Git(directory, "config", "commit.gpgsign", "false");
    }

    static string Commit(string directory, string text)
    {
        File.WriteAllText(Path.Combine(directory, "code.txt"), text);
        Git(directory, "add", "-A");
        Git(directory, "commit", "-q", "-m", text);
        return Git(directory, "rev-parse", "HEAD");
    }

    [Fact]
    public void The_mode_follows_the_folder_and_only_a_former_Simple_user_on_a_source_copy_is_asked()
    {
        // Settings saved by v0.15.0 in Simple mode, opened on a source copy: the one page that asks.
        var simple = Settings.Saved("""{"AdvancedMode":false,"DeveloperMode":false}""");
        Repo.IsInstall = false;
        Assert.True(Editions.MustChoose(simple));
        Assert.False(Editions.Advanced(simple));
        // Developer Mode alone was still Simple mode.
        Assert.True(Editions.MustChoose(Settings.Saved("""{"AdvancedMode":false,"DeveloperMode":true}""")));

        // "Use Advanced mode": this copy is Advanced from now on, and nothing chosen on a release is written.
        simple.SourceChoiceMade = true;
        Assert.False(Editions.MustChoose(simple));
        Assert.True(Editions.Advanced(simple));
        Assert.False(simple.Advanced);
        Repo.IsInstall = true;
        Assert.False(Editions.Advanced(simple));
        Assert.False(Editions.MustChoose(simple));

        // Someone who had Advanced mode is never asked, and keeps it on a release too.
        var advanced = Settings.Saved("""{"AdvancedMode":true,"DeveloperMode":true}""");
        Assert.True(Editions.Advanced(advanced));
        Repo.IsInstall = false;
        Assert.False(Editions.MustChoose(advanced));
        Assert.True(Editions.Advanced(advanced));

        // Settings from before the modes existed load as Advanced.
        Assert.False(Editions.MustChoose(Settings.Saved("{}")));
    }

    [Fact]
    public void Going_to_a_source_copy_on_purpose_leaves_nothing_to_choose_there()
    {
        string release = Release("release", null), source = Source("source");
        var settings = Settings.Saved("""{"AdvancedMode":false,"DeveloperMode":false}""");

        Editions.LeftFor(settings, release);
        Assert.Null(settings.SourceChoiceMade);
        Editions.LeftFor(settings, source);
        Assert.True(settings.SourceChoiceMade);

        Repo.IsInstall = false;
        Assert.True(Editions.Advanced(settings));
        Repo.IsInstall = true;
        Assert.False(Editions.Advanced(settings));
    }

    [Fact]
    public void Starting_on_any_folder_but_the_saved_one_is_going_there_on_purpose()
    {
        string release = Release("release", null), source = Source("source"), other = Source("other");
        static Settings Simple() => Settings.Saved("""{"AdvancedMode":false,"DeveloperMode":false}""");

        // Started on the saved source copy, however it is spelled: the one user who is asked.
        var settings = Simple();
        Assert.False(Editions.Arrived(settings, source, source));
        Assert.False(Editions.Arrived(settings, source, source + Path.DirectorySeparatorChar));
        Assert.Null(settings.SourceChoiceMade);

        // --repo, the first start's guide or its chooser, the folder Waypoint was started in: noted, and to be saved.
        Assert.True(Editions.Arrived(settings, other, source));
        Assert.True(settings.SourceChoiceMade);
        // Nothing more to save the next time.
        Assert.False(Editions.Arrived(settings, other, source));

        // No folder saved yet, or one that cannot be read as a path.
        settings = Simple();
        Assert.True(Editions.Arrived(settings, source, null));
        settings = Simple();
        Assert.True(Editions.Arrived(settings, source, ""));
        settings = Simple();
        Assert.True(Editions.Arrived(settings, source, "\0"));

        // A release has nothing to choose.
        settings = Simple();
        Assert.False(Editions.Arrived(settings, release, source));
        Assert.Null(settings.SourceChoiceMade);
    }

    [Fact]
    public void Switch_without_stopping_is_offered_only_to_someone_who_stays_in_Advanced_mode()
    {
        string release = Release("release", null), source = Source("source");
        // On a source copy past the choice page, with Developer Mode off: someone new who took LocalExile's source, or
        // someone who pressed "Use Advanced mode".
        var settings = Settings.Saved("""{"AdvancedMode":false,"DeveloperMode":false,"SourceChoiceMade":true}""");
        Repo.IsInstall = false;
        Assert.True(Editions.MayLeaveRunning(settings, source));
        // On the release they would be in Simple mode, with this copy's game and servers shown as the release's own.
        Assert.False(Editions.MayLeaveRunning(settings, release));
        // The choice page is still Simple mode.
        Assert.False(Editions.MayLeaveRunning(Settings.Saved("""{"AdvancedMode":false,"DeveloperMode":false}"""), source));

        // On a release in Simple mode, to either kind of folder.
        Repo.IsInstall = true;
        Assert.False(Editions.MayLeaveRunning(settings, source));
        Assert.False(Editions.MayLeaveRunning(settings, release));

        // Developer Mode: whoever works on LocalExile may leave it running, from and to either kind.
        var developer = Settings.Saved("""{"AdvancedMode":false,"DeveloperMode":true}""");
        Assert.True(Editions.MayLeaveRunning(developer, release));
        Assert.True(Editions.MayLeaveRunning(developer, source));
        Repo.IsInstall = false;
        Assert.True(Editions.MayLeaveRunning(developer, release));
    }

    [Fact]
    public void A_release_shows_the_way_to_Latest_only_with_Developer_Mode()
    {
        // A release install: someone who came to play sees neither the switch nor the copy from source,
        // whether or not a Latest copy was set up before.
        Repo.IsInstall = true;
        Assert.False(Editions.Offered(new Settings()));
        Assert.False(Editions.Offered(new Settings { LatestFolder = Path.GetTempPath() }));
        Assert.True(Editions.Offered(new Settings { DeveloperMode = true }));

        // On a source copy there is no switch: Advanced mode has Change folder, and the choice page its own button.
        Repo.IsInstall = false;
        Assert.False(Editions.Offered(new Settings()));
        Assert.False(Editions.Offered(new Settings { DeveloperMode = true }));
    }

    [Fact]
    public void Two_spellings_of_one_folder_are_the_same_folder()
    {
        string folder = Source("copy");
        Assert.True(Editions.SameFolder(folder, folder + Path.DirectorySeparatorChar));
        Assert.True(Editions.SameFolder(folder, Path.Combine(folder, "scripts", "..")));
        Assert.False(Editions.SameFolder(folder, Path.Combine(folder, "scripts")));
        if (OperatingSystem.IsWindows()) Assert.True(Editions.SameFolder(folder, folder.ToUpperInvariant()));
    }

    [Fact]
    public void Characters_wait_only_on_a_release_and_only_while_their_source_copy_is_there()
    {
        string source = Source("source");
        var settings = new Settings { BringFrom = source };
        Repo.IsInstall = true;
        // A copy nobody played on has no characters to offer, until it has: the adapter would find no saves there.
        Assert.Null(Editions.BringFrom(settings));
        Directory.CreateDirectory(Path.Combine(source, "data"));
        File.WriteAllText(Path.Combine(source, "data", "saves.db"), "");
        Assert.Equal(source, Editions.BringFrom(settings));
        // From LocalExile's database split on.
        File.Move(Path.Combine(source, "data", "saves.db"), Path.Combine(source, "data", "characters.db"));
        Assert.Equal(source, Editions.BringFrom(settings));
        Assert.Null(Editions.BringFrom(new Settings()));
        Assert.Null(Editions.BringFrom(new Settings { BringFrom = Path.Combine(_folder, "gone") }));
        Repo.IsInstall = false;
        Assert.Null(Editions.BringFrom(settings));
    }

    [Fact]
    public async Task A_release_takes_the_characters_of_a_copy_that_is_the_same_or_older_and_no_other()
    {
        string source = Source("source");
        Git(source, "init", "-q", "-b", "main");
        Configure(source);
        Directory.CreateDirectory(Path.Combine(source, "Leagues", "Stock", "Source"));
        File.WriteAllText(Path.Combine(source, "Leagues", "Stock", "league.json"), "{}");
        File.WriteAllText(Path.Combine(source, "Leagues", "Stock", "Source", "Stock.cs"), "");
        string first = Commit(source, "first");
        string released = Commit(source, "released");

        // The copy is on the release's own commit.
        string release = Release("release", released);
        Assert.True((await Editions.ReleaseTakesAsync(source, release)).Ok);

        // The copy is older than the release: the step every update of a release makes.
        Git(source, "checkout", "-q", first);
        Assert.True((await Editions.ReleaseTakesAsync(source, release)).Ok);

        // Changes of its user's own to the code.
        File.WriteAllText(Path.Combine(source, "code.txt"), "changed by hand");
        var changed = await Editions.ReleaseTakesAsync(source, release);
        Assert.False(changed.Ok);
        Assert.Contains(source, changed.Why);
        Git(source, "checkout", "-q", "--", "code.txt");

        // New files count where they are code: a database change waiting to be applied, source the servers compile.
        foreach (string added in new[] { Path.Combine("sql", "updates", "pending_characters", "mine.sql"), Path.Combine("src", "Mine.cs"), Path.Combine("Modules", "Mine", "Mine.cs") })
        {
            string file = Path.Combine(source, added);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, "");
            var untracked = await Editions.ReleaseTakesAsync(source, release);
            Assert.False(untracked.Ok, added);
            Assert.Equal(changed.Why, untracked.Why);
            Directory.Delete(Path.Combine(source, added.Split(Path.DirectorySeparatorChar)[0]), recursive: true);
        }

        // What Waypoint's own Leagues section does is no change to LocalExile's code: a league made there, a league
        // shown or hidden there, any other new file. A league's source changed by hand is one.
        Directory.CreateDirectory(Path.Combine(source, "Leagues", "Mine", "Source"));
        File.WriteAllText(Path.Combine(source, "Leagues", "Mine", "league.json"), "{}");
        File.WriteAllText(Path.Combine(source, "Leagues", "Mine", "Source", "WaypointLeague.cs"), "");
        File.WriteAllText(Path.Combine(source, "Leagues", "Stock", "league.json"), """{"creatable":false}""");
        File.WriteAllText(Path.Combine(source, "notes.txt"), "");
        Assert.True((await Editions.ReleaseTakesAsync(source, release)).Ok);
        File.WriteAllText(Path.Combine(source, "Leagues", "Stock", "Source", "Stock.cs"), "changed by hand");
        Assert.False((await Editions.ReleaseTakesAsync(source, release)).Ok);
        Git(source, "checkout", "-q", "--", "Leagues");
        Directory.Delete(Path.Combine(source, "Leagues", "Mine"), recursive: true);
        File.Delete(Path.Combine(source, "notes.txt"));

        // The copy is ahead of the release.
        Git(source, "checkout", "-q", "main");
        Commit(source, "newer than the release");
        var ahead = await Editions.ReleaseTakesAsync(source, release);
        Assert.False(ahead.Ok);
        Assert.NotEqual("", ahead.Why);

        // A branch the release does not hold, even though it started from an older commit.
        Git(source, "checkout", "-q", "-b", "mine", first);
        Commit(source, "my own work");
        Assert.False((await Editions.ReleaseTakesAsync(source, release)).Ok);

        // A release that names no commit, or one this copy has never seen: Waypoint cannot tell, so it does not offer.
        Git(source, "checkout", "-q", first);
        Assert.False((await Editions.ReleaseTakesAsync(source, Release("no-commit", null))).Ok);
        Assert.False((await Editions.ReleaseTakesAsync(source, Release("unknown", new string('a', 40)))).Ok);
        Assert.Equal(released, LocalExileReleases.InstalledCommit(release));
        Assert.Null(LocalExileReleases.InstalledCommit(Path.Combine(_folder, "no-commit")));
    }

    [Fact]
    public async Task A_release_newer_than_anything_the_copy_has_seen_is_looked_up_at_LocalExile_whatever_origin_is()
    {
        // LocalExile, a fork of it made before the release, and a copy whose origin is that fork.
        string upstream = Path.Combine(_folder, "localexile"), fork = Path.Combine(_folder, "fork.git"), source = Path.Combine(_folder, "source");
        Directory.CreateDirectory(upstream);
        Git(upstream, "init", "-q", "-b", "main");
        Configure(upstream);
        string first = Commit(upstream, "first");
        Git(_folder, "clone", "-q", "--bare", upstream, fork);
        Git(_folder, "clone", "-q", fork, source);
        string released = Commit(upstream, "released");
        string release = Release("release", released);

        // LocalExile can't be reached: Waypoint cannot tell, and says what to do about it.
        Waypoint.Git.UpstreamUrl = Path.Combine(_folder, "gone");
        var unreachable = await Editions.ReleaseTakesAsync(source, release);
        Assert.False(unreachable.Ok);

        // It can: the release's commit comes from LocalExile itself, into Waypoint's own place in that copy. The copy's
        // branches and what it holds of its origin stay as they were.
        Waypoint.Git.UpstreamUrl = upstream;
        Assert.True((await Editions.ReleaseTakesAsync(source, release)).Ok);
        Assert.Equal(released, Git(source, "rev-parse", "refs/launcher/upstream/main"));
        Assert.Equal(first, Git(source, "rev-parse", "refs/remotes/origin/main"));
        Assert.Equal(first, Git(source, "rev-parse", "HEAD"));

        // A commit LocalExile does not have either: fetched, compared, and still not known. Another reason than no answer.
        var unknown = await Editions.ReleaseTakesAsync(source, Release("unknown", new string('a', 40)));
        Assert.False(unknown.Ok);
        Assert.NotEqual(unreachable.Why, unknown.Why);
    }

    [Fact]
    public async Task Git_work_with_a_time_limit_is_ended_when_it_takes_longer()
    {
        Directory.CreateDirectory(_folder);
        var clock = Stopwatch.StartNew();
        // git's own shell waits far longer than the limit.
        var late = await Waypoint.Git.RunAsync(null, TimeSpan.FromSeconds(1), ["-C", _folder, "-c", "alias.wait=!sleep 20", "wait"]);
        Assert.Equal(-1, late.Code);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(15), clock.Elapsed.ToString());

        // Within its limit, and with none, a command answers as ever.
        Assert.True((await Waypoint.Git.RunAsync(null, TimeSpan.FromSeconds(30), ["--version"])).Ok);
        Assert.True((await Waypoint.Git.RunAsync(null, "--version")).Ok);
    }

    [Fact]
    public void A_remembered_folder_counts_only_while_it_is_still_that_kind()
    {
        string release = Path.Combine(_folder, "release"), latest = Source("latest");
        Directory.CreateDirectory(release);
        File.WriteAllText(Path.Combine(release, "LocalExile.exe"), "");
        File.WriteAllText(Path.Combine(release, "release.json"), """{"version":"0.1.0","commit":"cc0f9c3","client":"3.29.3b"}""");
        var settings = new Settings { ReleaseFolder = release, LatestFolder = latest };

        Assert.Equal(release, Editions.Folder(settings, release: true));
        Assert.Equal(latest, Editions.Folder(settings, release: false));

        // Swapped, or gone: neither side is offered as the other.
        Assert.Null(Editions.Folder(new Settings { ReleaseFolder = latest, LatestFolder = release }, release: true));
        Assert.Null(Editions.Folder(new Settings { ReleaseFolder = latest, LatestFolder = release }, release: false));
        Directory.Delete(release, recursive: true);
        Assert.Null(Editions.Folder(settings, release: true));
        Assert.Null(Editions.Folder(new Settings(), release: false));
    }

    [Fact]
    public void The_old_release_is_asked_about_only_after_a_Play_on_the_copy_that_replaces_it()
    {
        string old = Path.Combine(_folder, "release"), copy = Path.Combine(_folder, "copy"), other = Path.Combine(_folder, "other");
        var settings = new Settings { RemoveRelease = new(old, copy) };

        // "Remove the old one later": nothing to ask before the game has started on the copy.
        Assert.Null(Editions.Replaced(settings, copy));
        // A Play in another folder is not one on the copy.
        Assert.False(Editions.PlayedOn(settings, other));
        Assert.Null(Editions.Replaced(settings, copy));

        Assert.True(Editions.PlayedOn(settings, copy));
        Assert.Equal(new ReleaseToRemove(old, copy, Played: true), Editions.Replaced(settings, copy));
        // Asked on the copy only, and noted once.
        Assert.Null(Editions.Replaced(settings, other));
        Assert.False(Editions.PlayedOn(settings, copy));

        // Someone who chose it under an earlier Waypoint and has played already is asked at once.
        Assert.Equal(old, Editions.Replaced(new Settings { RemoveRelease = new(old, copy, Played: true) }, copy.ToUpperInvariant())?.Old);
        // "Keep both" stores nothing.
        Assert.False(Editions.PlayedOn(new Settings(), copy));
        Assert.Null(Editions.Replaced(new Settings(), copy));
    }
}
