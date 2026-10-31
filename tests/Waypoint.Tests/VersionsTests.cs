using Waypoint;

namespace Waypoint.Tests;

public class VersionsTests
{
    [Fact]
    public void Origins_branches_come_newest_first_without_its_HEAD_pointer()
    {
        var branches = Git.ParseBranches(
            "origin/HEAD\t2026-10-30T10:00:00+00:00\tA\tpointer\n" +
            "origin/main\t2026-10-30T09:00:00+00:00\tAtakan\tMerge pull request #401\n" +
            "origin/feat/x\t2026-10-30T11:30:00+02:00\tWrathen\tDocs: tabs\tand more\n");
        // 11:30+02:00 is 09:30 UTC: after main's 09:00.
        Assert.Equal(["feat/x", "main"], branches.Select(b => b.Name));
        Assert.Equal("Docs: tabs\tand more", branches[0].Subject);
        Assert.Equal("Wrathen", branches[0].Author);
    }

    [Fact]
    public void Local_branches_keep_their_whole_name_and_are_marked_local()
    {
        var branches = Git.ParseBranches(
            "feat/launcher-support\t2026-10-30T18:00:00+00:00\tBohdan\tAdd hooks for outside tools\n" +
            "main\t2026-10-30T09:00:00+00:00\tAtakan\tMerge pull request #401\n", remote: null);
        Assert.Equal(["feat/launcher-support", "main"], branches.Select(b => b.Name));
        Assert.All(branches, b => Assert.True(b.LocalOnly));
        Assert.All(Git.ParseBranches("origin/main\t2026-10-30T09:00:00+00:00\tAtakan\tMerge\n"), b => Assert.False(b.LocalOnly));
    }

    [Fact]
    public void LocalExiles_branches_beside_a_fork_lose_the_launchers_prefix()
    {
        var branches = Git.ParseBranches("launcher/upstream/main\t2026-10-30T09:00:00+00:00\tAtakan\tMerge\n", "launcher/upstream");
        Assert.Equal("main", Assert.Single(branches).Name);
        Assert.Equal(BranchSource.LocalExile, branches[0].Source);
    }

    [Theory]
    [InlineData("LocalExile/LocalExile", true)]
    [InlineData("localexile/LOCALEXILE", true)]
    [InlineData("Wrathen/LocalExile", true)]
    [InlineData("evrydayjunglist/evryPath", false)]
    [InlineData(null, false)]
    public void LocalExile_is_known_by_its_old_name_too(string? repository, bool expected) =>
        Assert.Equal(expected, Git.IsUpstream(repository));

    [Theory]
    [InlineData("0\t195", 0, 195)]
    [InlineData("3\t0\n", 3, 0)]
    public void The_distance_from_LocalExile_reads_both_counts(string output, int ahead, int behind) =>
        Assert.Equal((ahead, behind), Git.ParseCounts(output));

    [Fact]
    public void A_distance_git_did_not_give_is_no_distance() => Assert.Null(Git.ParseCounts("fatal: bad revision"));

    [Theory]
    [InlineData("https://github.com/LocalExile/LocalExile", "LocalExile/LocalExile")]
    [InlineData("https://github.com/LocalExile/LocalExile.git\n", "LocalExile/LocalExile")]
    [InlineData("git@github.com:someone/LocalExile.git", "someone/LocalExile")]
    [InlineData("ssh://git@github.com/someone/LocalExile/", "someone/LocalExile")]
    [InlineData("https://gitlab.com/someone/LocalExile", null)]
    [InlineData("https://github.com/someone", null)]
    public void A_GitHub_remote_names_its_repository(string url, string? expected) =>
        Assert.Equal(expected, Git.GitHubRepository(url));

    [Fact]
    public void Only_the_launchers_own_stashes_are_offered_back_by_the_branch_they_left()
    {
        var stashes = Git.ParseStashes(
            "stash@{0}\tOn pr/403: launcher: pr/403 before switching to main\n" +
            "stash@{1}\tOn main: work in progress\n" +
            "stash@{2}\tOn feat/a: launcher: feat/a before switching to pr/405\n");
        Assert.Equal([("stash@{0}", "pr/403"), ("stash@{2}", "feat/a")], stashes.Select(s => (s.Ref, s.Branch)));
    }

    [Theory]
    [InlineData("pr/403", 403)]
    [InlineData("pr/x", null)]
    [InlineData("pr/-1", null)]
    [InlineData("feat/pr/3", null)]
    [InlineData(null, null)]
    public void A_pull_requests_branch_names_its_number(string? branch, int? expected) =>
        Assert.Equal(expected, Git.PullRequestOf(branch));

    [Fact]
    public void Pull_requests_from_another_repository_or_a_deleted_fork_are_forks()
    {
        const string json = """
            [
              {"number": 403, "title": "Mods", "user": {"login": "Wrathen"}, "draft": false, "updated_at": "2026-10-30T10:00:00Z",
               "html_url": "https://github.com/LocalExile/LocalExile/pull/403", "head": {"repo": {"full_name": "LocalExile/LocalExile"}}},
              {"number": 405, "title": "Atlas", "user": {"login": "riqqq"}, "draft": true, "updated_at": "2026-10-30T12:00:00Z",
               "html_url": "https://github.com/LocalExile/LocalExile/pull/405", "head": {"repo": {"full_name": "riqqq/LocalExile"}}},
              {"number": 300, "title": "Old", "user": {"login": "gone"}, "updated_at": "2026-09-01T12:00:00Z",
               "html_url": "", "head": {"repo": null}}
            ]
            """;
        var pulls = PullRequests.Parse(json, "LocalExile/LocalExile");
        Assert.Equal([405, 403, 300], pulls.Select(p => p.Number));
        Assert.Equal([true, false, true], pulls.Select(p => p.FromFork));
        Assert.True(pulls[0].Draft);
        Assert.Equal("(deleted fork)", pulls[2].Source);
    }

    [Fact]
    public void Ages_read_in_minutes_hours_days_then_as_a_date()
    {
        var now = new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal("just now", VersionsTab.Ago(now.AddSeconds(-20), now));
        Assert.Equal("10 min ago", VersionsTab.Ago(now.AddMinutes(-10), now));
        Assert.Equal("30 h ago", VersionsTab.Ago(now.AddHours(-30), now));
        Assert.Equal("12 d ago", VersionsTab.Ago(now.AddDays(-12), now));
        Assert.Equal("", VersionsTab.Ago(default, now));
    }
}
