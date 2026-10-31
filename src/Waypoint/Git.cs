using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Waypoint;

/// <summary>Where a branch comes from: LocalExile itself, the fork <c>origin</c> points at, or only this checkout.</summary>
enum BranchSource { LocalExile, Fork, Local }

/// <summary>A branch of the server repository with its last commit.</summary>
sealed record RemoteBranch(string Name, DateTimeOffset Updated, string Author, string Subject, BranchSource Source = BranchSource.LocalExile)
{
    /// <summary>Only in this checkout: not pushed, such as a feature being worked on.</summary>
    public bool LocalOnly => Source == BranchSource.Local;
}

/// <summary>Changes the launcher stashed when it switched away from <see cref="Branch"/>; <see cref="Ref"/> is
/// <c>stash@{n}</c>.</summary>
sealed record LauncherStash(string Ref, string Branch, string Message);

/// <summary>
/// git in the server checkout (the working directory): what the Versions section reads and the switches it makes. Never
/// asks for credentials (GIT_TERMINAL_PROMPT=0): the server repository is public, and a prompt would hang a hidden
/// process. LocalExile is what every checkout is compared with, whatever <c>origin</c> is: a contributor's origin is their
/// fork. Such a checkout keeps LocalExile's branches under <c>refs/launcher/upstream</c>, fetched by address, so that no
/// remote of the player's changes. The parsing of git's output is separate, for the tests.
/// </summary>
static class Git
{
    /// <summary>The local branch a pull request is fetched into.</summary>
    public const string PullRequestPrefix = "pr/";
    /// <summary>How the launcher's stashes begin, so it finds its own and leaves the player's alone.</summary>
    public const string StashPrefix = "launcher: ";
    /// <summary>The server's own repository (owner/name): its branches and its open pull requests are the ones Versions shows.</summary>
    public const string UpstreamRepository = "LocalExile/LocalExile";
    /// <summary>Where the branches of <see cref="UpstreamRepository"/> are kept beside an origin that is a fork.</summary>
    const string UpstreamRefs = "launcher/upstream";

    /// <summary>LocalExile's address; the tests point it at a repository of their own.</summary>
    internal static string UpstreamUrl { get; set; } = GitRepositoryProbe.UpstreamUrl;

    public sealed record Result(int Code, string Output)
    {
        public bool Ok => Code == 0;
    }

    /// <summary>Runs git with <paramref name="args"/>; each line of its output also goes to <paramref name="log"/>.</summary>
    public static async Task<Result> RunAsync(Action<string>? log, params string[] args)
    {
        var info = new ProcessStartInfo("git")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Environment.CurrentDirectory,
        };
        info.Environment["GIT_TERMINAL_PROMPT"] = "0";
        // A stalled connection ends the command instead of holding the checkout: Play waits for it.
        info.Environment["GIT_HTTP_LOW_SPEED_LIMIT"] = "1000";
        info.Environment["GIT_HTTP_LOW_SPEED_TIME"] = "30";
        foreach (string a in args) info.ArgumentList.Add(a);
        log?.Invoke($"> git {string.Join(' ', args)}");
        using var p = Process.Start(info)!;
        var output = new StringBuilder();
        void Line(string? line)
        {
            if (line is null) return;
            lock (output) output.AppendLine(line);
            log?.Invoke(line);
        }
        p.OutputDataReceived += (_, e) => Line(e.Data);
        p.ErrorDataReceived += (_, e) => Line(e.Data);
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        await p.WaitForExitAsync();
        lock (output) return new(p.ExitCode, output.ToString().TrimEnd());
    }

    /// <summary>The changed and untracked files (ignored ones, such as data\ and Client\, are not), one per line.</summary>
    public static async Task<List<string>> ChangesAsync() =>
        Lines((await RunAsync(null, "status", "--porcelain")).Output);

    /// <summary>origin, then LocalExile when origin is a fork.</summary>
    public static async Task<Result> FetchAsync(Action<string> log)
    {
        var origin = await RunAsync(log, "fetch", "--prune", "origin");
        if (await OriginIsUpstreamAsync()) return origin;
        var upstream = await RunAsync(log, "fetch", "--prune", UpstreamUrl, $"+refs/heads/*:refs/{UpstreamRefs}/*");
        return upstream.Ok ? origin : upstream;
    }

    /// <summary>The repository's names, the one it had before it moved to the organization included.</summary>
    internal static bool IsUpstream(string? repository) =>
        string.Equals(repository, UpstreamRepository, StringComparison.OrdinalIgnoreCase)
        || string.Equals(repository, "Wrathen/LocalExile", StringComparison.OrdinalIgnoreCase);

    public static async Task<bool> OriginIsUpstreamAsync() => IsUpstream(await GitHubRepositoryAsync());

    /// <summary>LocalExile's branches, then origin's where origin is a fork, then this checkout's own; each name once, with
    /// the first of those that has it (pull requests' pr/&lt;n&gt; aside: the Pull requests list has them).</summary>
    public static async Task<List<RemoteBranch>> BranchesAsync()
    {
        var origin = ParseBranches((await RunAsync(null, "for-each-ref", $"--format={BranchFormat}", "refs/remotes/origin")).Output);
        bool fork = !await OriginIsUpstreamAsync();
        var upstream = fork ? ParseBranches((await RunAsync(null, "for-each-ref", $"--format={BranchFormat}", $"refs/{UpstreamRefs}")).Output, UpstreamRefs) : origin;
        var names = upstream.Select(b => b.Name).ToHashSet();
        var forked = fork ? origin.Where(b => !names.Contains(b.Name)).Select(b => b with { Source = BranchSource.Fork }).ToList() : [];
        names.UnionWith(forked.Select(b => b.Name));
        var local = ParseBranches((await RunAsync(null, "for-each-ref", $"--format={BranchFormat}", "refs/heads")).Output, remote: null)
            .Where(b => !names.Contains(b.Name) && PullRequestOf(b.Name) is null);
        return [.. upstream, .. forked, .. local];
    }

    /// <summary>The ref of LocalExile's <paramref name="branch"/> here; null when it has no such branch.</summary>
    static async Task<string?> UpstreamRefAsync(string branch)
    {
        string name = await OriginIsUpstreamAsync() ? $"refs/remotes/origin/{branch}" : $"refs/{UpstreamRefs}/{branch}";
        return (await RunAsync(null, "rev-parse", "--verify", "--quiet", name)).Ok ? name : null;
    }

    /// <summary>What <paramref name="branch"/> follows: LocalExile's, else origin's; null when neither has it.</summary>
    static async Task<string?> SourceRefAsync(string branch)
    {
        if (await UpstreamRefAsync(branch) is { } upstream) return upstream;
        string origin = $"refs/remotes/origin/{branch}";
        return (await RunAsync(null, "rev-parse", "--verify", "--quiet", origin)).Ok ? origin : null;
    }

    /// <summary>How far the checkout is from LocalExile's <paramref name="branch"/>; null when LocalExile has no such branch.</summary>
    public static async Task<(int Ahead, int Behind)?> CompareAsync(string branch)
    {
        if (await UpstreamRefAsync(branch) is not { } upstream) return null;
        var r = await RunAsync(null, "rev-list", "--left-right", "--count", $"HEAD...{upstream}");
        return r.Ok ? ParseCounts(r.Output) : null;
    }

    /// <summary>`rev-list --left-right --count`: the two numbers it prints.</summary>
    internal static (int Ahead, int Behind)? ParseCounts(string output) =>
        output.Split((char[])['\t', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) is [var ahead, var behind]
        && int.TryParse(ahead, out int a) && int.TryParse(behind, out int b) ? (a, b) : null;

    /// <summary>The commit the checkout is on.</summary>
    public static async Task<string?> HeadAsync()
    {
        var r = await RunAsync(null, "rev-parse", "HEAD");
        return r.Ok ? r.Output.Trim() : null;
    }

    /// <summary>The checkout back on <paramref name="commit"/>; git refuses when that would touch an uncommitted change.</summary>
    public static Task<Result> ResetToAsync(string commit, Action<string> log) => RunAsync(log, "reset", "--keep", commit);

    public static async Task<HashSet<string>> LocalBranchesAsync() =>
        [.. Lines((await RunAsync(null, "for-each-ref", "--format=%(refname:short)", "refs/heads")).Output)];

    /// <summary>origin's default branch (origin/HEAD), main when the clone does not name one.</summary>
    public static async Task<string> DefaultBranchAsync()
    {
        var r = await RunAsync(null, "symbolic-ref", "--short", "refs/remotes/origin/HEAD");
        return r.Ok && r.Output.StartsWith("origin/", StringComparison.Ordinal) ? r.Output["origin/".Length..] : "main";
    }

    /// <summary>The GitHub repository origin points at (owner/name), null for another host.</summary>
    public static async Task<string?> GitHubRepositoryAsync() =>
        GitHubRepository((await RunAsync(null, "remote", "get-url", "origin")).Output);

    public static async Task<List<LauncherStash>> StashesAsync() =>
        ParseStashes((await RunAsync(null, "stash", "list", "--format=%gd%x09%gs")).Output);

    public static Task<Result> StashAsync(string from, string to, Action<string> log) =>
        RunAsync(log, "stash", "push", "--include-untracked", "-m", $"{StashPrefix}{from} before switching to {to}");

    public static Task<Result> RestoreAsync(LauncherStash stash, Action<string> log) => RunAsync(log, "stash", "pop", stash.Ref);

    /// <summary>To <paramref name="branch"/>: a local branch of that name is switched to and moved forward to what it
    /// follows (<see cref="SourceRefAsync"/>) when that loses none of its own commits (a branch only in this checkout is just
    /// switched to); else one is made from origin's, which it tracks, or from LocalExile's beside a fork.</summary>
    public static async Task<Result> SwitchToBranchAsync(string branch, Action<string> log)
    {
        string? source = await SourceRefAsync(branch);
        if (!(await LocalBranchesAsync()).Contains(branch))
            return source is null || source.StartsWith("refs/remotes/", StringComparison.Ordinal)
                ? await RunAsync(log, "switch", "--track", "-c", branch, $"origin/{branch}")
                : await RunAsync(log, "switch", "--no-track", "-c", branch, source);
        var switched = await RunAsync(log, "switch", branch);
        if (!switched.Ok || source is null) return switched;
        var forward = await RunAsync(log, "merge", "--ff-only", source);
        return forward.Ok ? forward
            : new(0, $"{branch} has commits of its own: switched, but left as it is ({forward.Output})");
    }

    /// <summary>To pull request <paramref name="number"/>'s head, fetched into <c>pr/&lt;number&gt;</c> (again when it
    /// moved or was rewritten). Refused when that branch has commits of the player's own, which would be lost: commits in
    /// neither the pull request's head now nor the one fetched last time (<see cref="FetchedRef"/>; a force-push leaves
    /// the old ones behind, and they are not the player's).</summary>
    public static async Task<Result> SwitchToPullRequestAsync(int number, Action<string> log)
    {
        string branch = PullRequestBranch(number), seen = FetchedRef(number);
        var before = await RunAsync(null, "rev-parse", "--verify", "--quiet", seen);
        var fetched = await RunAsync(log, "fetch", UpstreamUrl, $"pull/{number}/head");
        if (!fetched.Ok) return fetched;
        if ((await LocalBranchesAsync()).Contains(branch))
        {
            var own = before.Ok
                ? await RunAsync(null, "rev-list", "--count", branch, "^FETCH_HEAD", $"^{before.Output}")
                : await RunAsync(null, "rev-list", "--count", branch, "^FETCH_HEAD");
            if (own.Ok && int.TryParse(own.Output, out int count) && count > 0)
                return new(1, L.Text("{0} has {1} commit(s) of its own that the pull request has not; push or move them first", branch, count));
        }
        var switched = await RunAsync(log, "switch", "-C", branch, "FETCH_HEAD");
        if (switched.Ok) await RunAsync(null, "update-ref", seen, "FETCH_HEAD");
        return switched;
    }

    /// <summary>A pull request's local branch and what the launcher remembers of it.</summary>
    public static async Task<Result> DeletePullRequestAsync(int number, Action<string> log)
    {
        var deleted = await RunAsync(log, "branch", "-D", PullRequestBranch(number));
        await RunAsync(null, "update-ref", "-d", FetchedRef(number));
        return deleted;
    }

    /// <summary>The pull request's head the launcher fetched last, outside refs/remotes so `fetch --prune` keeps it.</summary>
    static string FetchedRef(int number) => $"refs/launcher/pr/{number}";

    /// <summary>The current branch brought up to what it follows, fast-forward only.</summary>
    public static async Task<Result> UpdateAsync(string branch, Action<string> log)
    {
        var fetched = await FetchAsync(log);
        return fetched.Ok ? await MoveForwardAsync(branch, log) : fetched;
    }

    /// <summary>The current branch moved forward to what was fetched last, fast-forward only.</summary>
    public static async Task<Result> MoveForwardAsync(string branch, Action<string> log) =>
        await SourceRefAsync(branch) is { } source ? await RunAsync(log, "merge", "--ff-only", source) : new(0, "");

    public static string PullRequestBranch(int number) => $"{PullRequestPrefix}{number}";

    /// <summary>The pull request a branch was fetched for (<c>pr/123</c>), else null.</summary>
    public static int? PullRequestOf(string? branch) =>
        branch is not null && branch.StartsWith(PullRequestPrefix, StringComparison.Ordinal)
        && int.TryParse(branch[PullRequestPrefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n : null;

    const string BranchFormat = "%(refname:short)%09%(committerdate:iso-strict)%09%(authorname)%09%(subject)";

    /// <summary>for-each-ref's lines, newest first: those under <paramref name="remote"/>, its prefix taken off, or, with
    /// null, local branches, which are then <see cref="RemoteBranch.LocalOnly"/>.</summary>
    internal static List<RemoteBranch> ParseBranches(string output, string? remote = "origin")
    {
        var branches = new List<RemoteBranch>();
        string prefix = remote is null ? "" : remote + "/";
        foreach (string line in Lines(output))
        {
            var f = line.Split('\t', 4);
            // origin/HEAD is a pointer, and plain "origin" the same pointer by its short name.
            if (f.Length < 4 || !f[0].StartsWith(prefix, StringComparison.Ordinal) || f[0] == prefix + "HEAD" || f[0].Length == prefix.Length) continue;
            DateTimeOffset.TryParse(f[1], CultureInfo.InvariantCulture, DateTimeStyles.None, out var at);
            branches.Add(new(f[0][prefix.Length..], at, f[2], f[3], remote is null ? BranchSource.Local : BranchSource.LocalExile));
        }
        return [.. branches.OrderByDescending(b => b.Updated)];
    }

    /// <summary>owner/name of a GitHub remote URL (https or ssh, with or without .git), else null.</summary>
    internal static string? GitHubRepository(string url)
    {
        url = url.Trim();
        foreach (string prefix in (string[])["https://github.com/", "http://github.com/", "git@github.com:", "ssh://git@github.com/"])
            if (url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                string path = url[prefix.Length..].TrimEnd('/');
                if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) path = path[..^4];
                return path.Count(c => c == '/') == 1 ? path : null;
            }
        return null;
    }

    internal static List<LauncherStash> ParseStashes(string output)
    {
        var stashes = new List<LauncherStash>();
        foreach (string line in Lines(output))
        {
            var f = line.Split('\t', 2);
            if (f.Length < 2) continue;
            // git prefixes the message with "On <branch>: ".
            int at = f[1].IndexOf(StashPrefix, StringComparison.Ordinal);
            if (at < 0) continue;
            string message = f[1][at..];
            int before = message.IndexOf(" before switching to ", StringComparison.Ordinal);
            if (before < 0) continue;
            stashes.Add(new(f[0], message[StashPrefix.Length..before], message));
        }
        return stashes;
    }

    static List<string> Lines(string text) =>
        [.. text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
}
