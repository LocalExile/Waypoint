using Launcher.Core;
using Waypoint;

namespace Waypoint.Tests;

/// <summary>The sidebar's folder list: what it holds, how a folder is read, and what a change of folder notes.</summary>
public sealed class FolderListTests : IDisposable
{
    readonly string _folder = Path.Combine(Path.GetTempPath(), "waypoint-folders-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    string Release(string name, string version = "0.3.0-beta")
    {
        string folder = Path.Combine(_folder, name);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, LocalExileReleases.Launcher), "");
        File.WriteAllText(Path.Combine(folder, "release.json"), $$"""{"version":"{{version}}"}""");
        return folder;
    }

    string Source(string name, string? head = "ref: refs/heads/main\n")
    {
        string folder = Path.Combine(_folder, name);
        Directory.CreateDirectory(Path.Combine(folder, "scripts", "dev"));
        File.WriteAllText(Path.Combine(folder, "GameServer.slnx"), "");
        File.WriteAllText(Path.Combine(folder, "scripts", "dev", "live.ps1"), "");
        if (head is not null)
        {
            Directory.CreateDirectory(Path.Combine(folder, ".git"));
            File.WriteAllText(Path.Combine(folder, ".git", "HEAD"), head);
        }
        return folder;
    }

    [Fact]
    public void Settings_from_before_the_list_name_their_three_folders_until_it_is_written()
    {
        string release = Release("release"), source = Source("source"), other = Source("other");
        // The folder in use is usually the release or the source copy as well: each folder shows once.
        var settings = new Settings { ServerPath = release, ReleaseFolder = release, LatestFolder = source };
        Assert.Equal([release, source], FolderList.Paths(settings, release));
        // Started with --repo on a folder the settings do not name: shown, not saved.
        Assert.Equal([release, source, other], FolderList.Paths(settings, other));
        Assert.Empty(settings.KnownFolders);

        // The first folder added writes the list, with what stood in for it; from then on the list alone counts.
        Assert.True(FolderList.Add(settings, other));
        Assert.Equal([release, source, other], settings.KnownFolders);
        settings.LatestFolder = Path.Combine(_folder, "elsewhere");
        Assert.Equal([release, source, other], FolderList.Paths(settings, null));
    }

    [Fact]
    public void A_folder_is_in_the_list_once_however_it_is_spelled()
    {
        string release = Release("release");
        var settings = new Settings();
        Assert.True(FolderList.Add(settings, release));
        Assert.False(FolderList.Add(settings, release));
        Assert.False(FolderList.Add(settings, release + Path.DirectorySeparatorChar));
        Assert.False(FolderList.Add(settings, Path.Combine(release, "..", "release")));
        if (OperatingSystem.IsWindows()) Assert.False(FolderList.Add(settings, release.ToUpperInvariant()));
        Assert.Single(settings.KnownFolders);
        // What can't be read as a path is no folder, and is not noted.
        Assert.False(FolderList.Add(settings, "bad\0path"));
        Assert.Single(settings.KnownFolders);
    }

    [Fact]
    public void Taking_a_folder_off_the_list_drops_what_else_named_it_and_touches_nothing_in_it()
    {
        string release = Release("release"), source = Source("source");
        var settings = new Settings { ServerPath = release, ReleaseFolder = release, LatestFolder = source, BringFrom = source };
        settings.NoBringFrom.Add(source);

        Assert.True(FolderList.Forget(settings, source + Path.DirectorySeparatorChar));
        Assert.Equal([release], settings.KnownFolders);
        Assert.Null(settings.LatestFolder);
        Assert.Null(settings.BringFrom);
        Assert.Empty(settings.NoBringFrom);
        Assert.Equal(release, settings.ReleaseFolder);
        Assert.True(File.Exists(Path.Combine(source, "GameServer.slnx")));
        // Not in the list: nothing to do, nothing to save.
        Assert.False(FolderList.Forget(settings, source));

        // The remembered source copy no longer stands in once the list is written, even when the list is left empty.
        var one = new Settings { LatestFolder = source };
        Assert.True(FolderList.Forget(one, source));
        Assert.Empty(FolderList.Paths(one, null));
    }

    [Fact]
    public void A_folder_is_read_as_it_is_now()
    {
        string release = Release("release"), source = Source("source"), detached = Source("detached", "0123456789abcdef0123456789abcdef01234567\n"),
            plain = Source("plain", head: null), gone = Path.Combine(_folder, "gone", "LocalExile-old");

        var read = FolderList.Read(release, current: true);
        Assert.Equal((FolderKind.Release, "0.3.0-beta", true), (read.Kind, read.Detail, read.Current));
        Assert.Equal(L.Text("Release {0}", "0.3.0-beta"), read.Title);

        read = FolderList.Read(source, current: false);
        Assert.Equal((FolderKind.Source, "main", false), (read.Kind, read.Detail, read.Current));
        Assert.Equal(L.Text("Source, {0}", "main"), read.Title);
        Assert.Equal("0123456", FolderList.Read(detached, false).Detail);
        // A source copy that is no git checkout (a downloaded zip): a source copy all the same.
        Assert.Equal(L.Text("Source"), FolderList.Read(plain, false).Title);

        // Not there: its own name, so that its user knows which one it was.
        read = FolderList.Read(gone, false);
        Assert.Equal(FolderKind.Missing, read.Kind);
        Assert.Equal("LocalExile-old", read.Title);
        Assert.Equal(FolderKind.Missing, FolderList.Read("bad\0path", false).Kind);

        // The release updated and the copy on another branch, outside Waypoint: read again, nothing was kept.
        File.WriteAllText(Path.Combine(release, "release.json"), """{"version":"0.4.0"}""");
        File.WriteAllText(Path.Combine(source, ".git", "HEAD"), "ref: refs/heads/my-skill-fix\n");
        Assert.Equal("0.4.0", FolderList.Read(release, true).Detail);
        Assert.Equal("my-skill-fix", FolderList.Read(source, false).Detail);
        // A release whose release.json can't be read is still a release.
        File.WriteAllText(Path.Combine(release, "release.json"), "{");
        Assert.Equal(L.Text("Release"), FolderList.Read(release, true).Title);
    }

    [Fact]
    public void A_worktree_names_its_branch_through_its_git_file()
    {
        string main = Source("main"), tree = Source("tree", head: null);
        string gitdir = Path.Combine(main, ".git", "worktrees", "tree");
        Directory.CreateDirectory(gitdir);
        File.WriteAllText(Path.Combine(gitdir, "HEAD"), "ref: refs/heads/feature/zoom\n");
        File.WriteAllText(Path.Combine(tree, ".git"), "gitdir: " + gitdir + "\n");
        Assert.Equal("feature/zoom", Repo.BranchIn(tree));
        // git may write the directory relative to the worktree.
        File.WriteAllText(Path.Combine(tree, ".git"), "gitdir: " + Path.Combine("..", "main", ".git", "worktrees", "tree") + "\n");
        Assert.Equal("feature/zoom", Repo.BranchIn(tree));
        Assert.Null(Repo.BranchIn(Path.Combine(_folder, "nowhere")));
    }

    [Fact]
    public void The_list_shows_releases_then_source_copies_then_what_is_missing_each_in_the_order_it_came()
    {
        string first = Source("first"), release = Release("release"), gone = Path.Combine(_folder, "gone"), second = Source("second"),
            older = Release("older", "0.2.2-beta");
        var settings = new Settings();
        foreach (string folder in new[] { first, release, gone, second, older }) FolderList.Add(settings, folder);

        var list = FolderList.Read(settings, second);
        Assert.Equal([release, older, first, second, gone], list.Select(folder => folder.Path));
        Assert.Equal(second, Assert.Single(list, folder => folder.Current).Path);
    }

    [Theory]
    [InlineData(@"D:\Games\LocalExile", @"D:\Games\LocalExile")]
    [InlineData(@"C:\Users\someone\Documents\Games\LocalExile-skills", @"…\Games\LocalExile-skills")]
    [InlineData(@"C:\AVeryLongFolderNameWithNoSeparatorNearTheEnd-LocalExile", "…ratorNearTheEnd-LocalExile")]
    [InlineData("/home/someone/games/LocalExile-my-skill-fix", "…/LocalExile-my-skill-fix")]
    public void A_long_path_keeps_its_end(string path, string shown)
    {
        Assert.Equal(shown, FolderList.Short(path));
        Assert.True(FolderList.Short(path).Length <= 27);
    }

    [Fact]
    public void Leaving_a_source_copy_for_a_release_offers_its_characters_there_unless_declined()
    {
        string release = Release("release"), source = Source("source"), other = Source("other");

        // Source copy to release, by whatever way: that copy's characters are the ones the release offers.
        var settings = new Settings();
        Editions.LeftFor(settings, release, from: source);
        Assert.Equal(source, settings.BringFrom);
        // Between two source copies, and from a release, nothing is offered: the note stays as it was.
        Editions.LeftFor(settings, other, from: source);
        Editions.LeftFor(settings, release, from: null);
        Editions.LeftFor(settings, release, from: Path.Combine(_folder, "gone"));
        Assert.Equal(source, settings.BringFrom);
        // The copy left last is the one offered.
        Editions.LeftFor(settings, release, from: other);
        Assert.Equal(other, settings.BringFrom);

        // "Don't offer again" for a copy holds for every later change of folder from it.
        var declined = new Settings();
        declined.NoBringFrom.Add(source);
        Editions.LeftFor(declined, release, from: source);
        Assert.Null(declined.BringFrom);
        Editions.LeftFor(declined, release, from: other);
        Assert.Equal(other, declined.BringFrom);
    }

    [Fact]
    public void The_list_is_saved_with_the_settings_and_older_settings_load_without_one()
    {
        var saved = Settings.Saved("""{"ServerPath":"D:\\Games\\LocalExile","KnownFolders":["D:\\Games\\LocalExile","F:\\dev\\LocalExile"],"NoBringFrom":["F:\\dev\\LocalExile"]}""");
        Assert.Equal([@"D:\Games\LocalExile", @"F:\dev\LocalExile"], saved.KnownFolders);
        Assert.Equal([@"F:\dev\LocalExile"], saved.NoBringFrom);

        var older = Settings.Saved("""{"ServerPath":"D:\\Games\\LocalExile","ReleaseFolder":"D:\\Games\\LocalExile","LatestFolder":"F:\\dev\\LocalExile"}""");
        Assert.Empty(older.KnownFolders);
        Assert.Equal([@"D:\Games\LocalExile", @"F:\dev\LocalExile"], FolderList.Paths(older, null));
    }
}
