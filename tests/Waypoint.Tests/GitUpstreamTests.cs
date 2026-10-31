using System.Diagnostics;
using Waypoint;

namespace Waypoint.Tests;

/// <summary><see cref="Git"/> works in the process's working directory, so these run alone.</summary>
[CollectionDefinition(nameof(GitUpstreamTests), DisableParallelization = true)]
public sealed class GitUpstreamCollection;

/// <summary>Real git on throwaway repositories: LocalExile, a contributor's fork of it, and a checkout whose origin is
/// that fork.</summary>
[Collection(nameof(GitUpstreamTests))]
public sealed class GitUpstreamTests : IDisposable
{
    readonly string _top = Path.Combine(Path.GetTempPath(), "waypoint-git-" + Guid.NewGuid().ToString("N"));
    readonly string _upstream, _fork, _checkout, _writer, _startedIn, _oldUrl;

    public GitUpstreamTests()
    {
        _upstream = Path.Combine(_top, "upstream.git");
        _fork = Path.Combine(_top, "fork.git");
        _checkout = Path.Combine(_top, "checkout");
        _writer = Path.Combine(_top, "writer");
        _startedIn = Environment.CurrentDirectory;
        _oldUrl = Git.UpstreamUrl;
        Directory.CreateDirectory(_top);
        Run(_top, "init", "--bare", "-b", "main", _upstream);
        Run(_top, "clone", "-q", _upstream, _writer);
        Commit(_writer, "first");
        Run(_writer, "push", "-q", "origin", "HEAD:main");
        Run(_top, "clone", "-q", "--bare", _upstream, _fork);
        Run(_top, "clone", "-q", _fork, _checkout);
        Configure(_checkout);
        Git.UpstreamUrl = _upstream;
        Environment.CurrentDirectory = _checkout;
    }

    static string Run(string directory, params string[] args)
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
        Run(directory, "config", "user.name", "Test");
        Run(directory, "config", "user.email", "test@example.test");
        Run(directory, "config", "commit.gpgsign", "false");
    }

    static void Commit(string directory, string name)
    {
        if (!File.Exists(Path.Combine(directory, ".git", "config.done")))
        {
            Configure(directory);
            File.WriteAllText(Path.Combine(directory, ".git", "config.done"), "");
        }
        File.WriteAllText(Path.Combine(directory, name + ".txt"), name);
        Run(directory, "add", ".");
        Run(directory, "commit", "-q", "-m", name);
    }

    /// <summary>LocalExile moves on, on <paramref name="branch"/>.</summary>
    void Land(string name, string branch = "main")
    {
        Run(_writer, "switch", "-q", "-C", branch, branch == "main" ? "origin/main" : "HEAD");
        Commit(_writer, name);
        Run(_writer, "push", "-q", "origin", $"HEAD:{branch}");
        Run(_writer, "fetch", "-q", "origin");
    }

    static async Task Fetch() => Assert.True((await Git.FetchAsync(_ => { })).Ok);

    [Fact]
    public async Task A_fork_as_origin_is_compared_with_LocalExile_not_with_itself()
    {
        Land("second");
        Land("third");
        await Fetch();
        Assert.Equal((0, 2), await Git.CompareAsync("main"));
        // origin says up to date: that was the bug.
        Assert.Equal("0", Run(_checkout, "rev-list", "--count", "HEAD..origin/main"));
    }

    [Fact]
    public async Task No_remote_is_added_to_the_players_checkout()
    {
        await Fetch();
        Assert.Equal("origin", Run(_checkout, "remote"));
    }

    [Fact]
    public async Task Each_branch_says_where_it_comes_from()
    {
        Land("feature", "feature/upstream");
        Run(_checkout, "switch", "-q", "-c", "feature/fork");
        Commit(_checkout, "fork work");
        Run(_checkout, "push", "-q", "origin", "HEAD:feature/fork");
        Run(_checkout, "switch", "-q", "-c", "feature/local");
        Commit(_checkout, "local work");
        await Fetch();

        var branches = (await Git.BranchesAsync()).ToDictionary(b => b.Name, b => b.Source);
        Assert.Equal(BranchSource.LocalExile, branches["main"]);
        Assert.Equal(BranchSource.LocalExile, branches["feature/upstream"]);
        Assert.Equal(BranchSource.Fork, branches["feature/fork"]);
        Assert.Equal(BranchSource.Local, branches["feature/local"]);
    }

    [Fact]
    public async Task Update_brings_LocalExiles_commits_to_a_fork_user()
    {
        Land("second");
        var result = await Git.UpdateAsync("main", _ => { });
        Assert.True(result.Ok, result.Output);
        Assert.True(File.Exists(Path.Combine(_checkout, "second.txt")));
        Assert.Equal((0, 0), await Git.CompareAsync("main"));
    }

    [Fact]
    public async Task Update_refuses_to_lose_commits_of_the_players_own()
    {
        Commit(_checkout, "mine");
        string head = Run(_checkout, "rev-parse", "HEAD");
        Land("second");
        Assert.False((await Git.UpdateAsync("main", _ => { })).Ok);
        Assert.Equal(head, Run(_checkout, "rev-parse", "HEAD"));
        Assert.Equal((1, 1), await Git.CompareAsync("main"));
    }

    [Fact]
    public async Task A_branch_only_LocalExile_has_can_be_switched_to_beside_a_fork()
    {
        Land("feature", "feature/upstream");
        await Fetch();
        var result = await Git.SwitchToBranchAsync("feature/upstream", _ => { });
        Assert.True(result.Ok, result.Output);
        Assert.Equal("feature/upstream", Run(_checkout, "branch", "--show-current"));
        Assert.True(File.Exists(Path.Combine(_checkout, "feature.txt")));
    }

    [Fact]
    public async Task A_pull_request_comes_from_LocalExile_not_from_origin()
    {
        Land("proposal", "proposal");
        Run(_upstream, "update-ref", "refs/pull/446/head", Run(_upstream, "rev-parse", "refs/heads/proposal"));
        var result = await Git.SwitchToPullRequestAsync(446, _ => { });
        Assert.True(result.Ok, result.Output);
        Assert.Equal("pr/446", Run(_checkout, "branch", "--show-current"));
        Assert.True(File.Exists(Path.Combine(_checkout, "proposal.txt")));
    }

    [Fact]
    public async Task Going_back_returns_to_the_commit_before_an_update()
    {
        string before = (await Git.HeadAsync())!;
        Land("second");
        await Fetch();
        Assert.True((await Git.MoveForwardAsync("main", _ => { })).Ok);
        Assert.True(File.Exists(Path.Combine(_checkout, "second.txt")));

        Assert.True((await Git.ResetToAsync(before, _ => { })).Ok);
        Assert.Equal(before, await Git.HeadAsync());
        Assert.False(File.Exists(Path.Combine(_checkout, "second.txt")));
    }

    [Fact]
    public async Task Going_back_keeps_an_uncommitted_change_from_being_overwritten()
    {
        string before = (await Git.HeadAsync())!;
        Land("second");
        await Fetch();
        await Git.MoveForwardAsync("main", _ => { });
        File.WriteAllText(Path.Combine(_checkout, "second.txt"), "changed by hand");
        Assert.False((await Git.ResetToAsync(before, _ => { })).Ok);
        Assert.Equal("changed by hand", File.ReadAllText(Path.Combine(_checkout, "second.txt")));
    }

    public void Dispose()
    {
        Environment.CurrentDirectory = _startedIn;
        Git.UpstreamUrl = _oldUrl;
        foreach (string file in Directory.EnumerateFiles(_top, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(_top, recursive: true);
    }
}
