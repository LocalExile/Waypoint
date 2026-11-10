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

    public void Dispose()
    {
        Repo.IsInstall = _install;
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
        Git(source, "config", "user.name", "Test");
        Git(source, "config", "user.email", "test@example.test");
        Git(source, "config", "commit.gpgsign", "false");
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
}
