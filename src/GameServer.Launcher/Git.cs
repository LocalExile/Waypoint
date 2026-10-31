using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace GameServer.Launcher;

/// <summary>A branch of the server repository (origin's), with its last commit.</summary>
sealed record RemoteBranch(string Name, DateTimeOffset Updated, string Author, string Subject);

/// <summary>Changes the launcher stashed when it switched away from <see cref="Branch"/>; <see cref="Ref"/> is
/// <c>stash@{n}</c>.</summary>
sealed record LauncherStash(string Ref, string Branch, string Message);

/// <summary>
/// git in the server checkout (the working directory): what the Versions section reads and the switches it makes. Never
/// asks for credentials (GIT_TERMINAL_PROMPT=0): the server repository is public, and a prompt would hang a hidden
/// process. The parsing of git's output is separate, for the tests.
/// </summary>
static class Git
{
    /// <summary>The local branch a pull request is fetched into.</summary>
    public const string PullRequestPrefix = "pr/";
    /// <summary>How the launcher's stashes begin, so it finds its own and leaves the player's alone.</summary>
    public const string StashPrefix = "launcher: ";

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

    public static Task<Result> FetchAsync(Action<string> log) => RunAsync(log, "fetch", "--prune", "origin");

    public static async Task<List<RemoteBranch>> BranchesAsync() =>
        ParseBranches((await RunAsync(null, "for-each-ref", $"--format={BranchFormat}", "refs/remotes/origin")).Output);

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

    /// <summary>To origin's <paramref name="branch"/>: a local branch of that name is switched to and moved forward to
    /// origin's when it can be without losing its own commits; else one is made that tracks origin's.</summary>
    public static async Task<Result> SwitchToBranchAsync(string branch, Action<string> log)
    {
        if (!(await LocalBranchesAsync()).Contains(branch))
            return await RunAsync(log, "switch", "--track", "-c", branch, $"origin/{branch}");
        var switched = await RunAsync(log, "switch", branch);
        if (!switched.Ok) return switched;
        var forward = await RunAsync(log, "merge", "--ff-only", $"origin/{branch}");
        return forward.Ok ? forward
            : new(0, $"{branch} has commits of its own that origin's has not: switched, but left as it is ({forward.Output})");
    }

    /// <summary>To pull request <paramref name="number"/>'s head, fetched into <c>pr/&lt;number&gt;</c> (again when it
    /// moved or was rewritten). Refused when that branch has commits of the player's own, which would be lost: commits in
    /// neither the pull request's head now nor the one fetched last time (<see cref="FetchedRef"/>; a force-push leaves
    /// the old ones behind, and they are not the player's).</summary>
    public static async Task<Result> SwitchToPullRequestAsync(int number, Action<string> log)
    {
        string branch = PullRequestBranch(number), seen = FetchedRef(number);
        var before = await RunAsync(null, "rev-parse", "--verify", "--quiet", seen);
        var fetched = await RunAsync(log, "fetch", "origin", $"pull/{number}/head");
        if (!fetched.Ok) return fetched;
        if ((await LocalBranchesAsync()).Contains(branch))
        {
            var own = before.Ok
                ? await RunAsync(null, "rev-list", "--count", branch, "^FETCH_HEAD", $"^{before.Output}")
                : await RunAsync(null, "rev-list", "--count", branch, "^FETCH_HEAD");
            if (own.Ok && int.TryParse(own.Output, out int count) && count > 0)
                return new(1, $"{branch} has {count} commit(s) of its own that the pull request has not; push or move them first");
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

    /// <summary>The current branch brought up to origin's, fast-forward only.</summary>
    public static Task<Result> PullAsync(Action<string> log) => RunAsync(log, "pull", "--ff-only");

    public static string PullRequestBranch(int number) => $"{PullRequestPrefix}{number}";

    /// <summary>The pull request a branch was fetched for (<c>pr/123</c>), else null.</summary>
    public static int? PullRequestOf(string? branch) =>
        branch is not null && branch.StartsWith(PullRequestPrefix, StringComparison.Ordinal)
        && int.TryParse(branch[PullRequestPrefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n : null;

    const string BranchFormat = "%(refname:short)%09%(committerdate:iso-strict)%09%(authorname)%09%(subject)";

    internal static List<RemoteBranch> ParseBranches(string output)
    {
        var branches = new List<RemoteBranch>();
        foreach (string line in Lines(output))
        {
            var f = line.Split('\t', 4);
            // origin/HEAD is a pointer, and plain "origin" the same pointer by its short name.
            if (f.Length < 4 || !f[0].StartsWith("origin/", StringComparison.Ordinal) || f[0] == "origin/HEAD") continue;
            DateTimeOffset.TryParse(f[1], CultureInfo.InvariantCulture, DateTimeStyles.None, out var at);
            branches.Add(new(f[0]["origin/".Length..], at, f[2], f[3]));
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
