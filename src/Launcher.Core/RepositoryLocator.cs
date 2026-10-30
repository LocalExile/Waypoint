using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Launcher.Core;

public sealed record GitReadResult(int ExitCode, string Output);
public sealed record UpstreamHead(string Branch, string Commit, DateTimeOffset CheckedAt);
public sealed record LocalRepositoryHead(string Commit, string Branch, bool? HasTrackedChanges);
public sealed record DiscoveredRepository(string Root, LocalRepositoryHead Head, bool MatchesUpstream, bool ClientFilesFound)
{
    public string Summary => (MatchesUpstream ? "HEAD matches latest upstream" : Head.Commit.Length > 0 ? "Different local HEAD" : "Local HEAD unavailable") +
        " · " + (Head.Branch.Length > 0 ? Head.Branch : "No branch information") +
        (Head.HasTrackedChanges == true ? " · tracked files modified" : Head.HasTrackedChanges is null ? " · working files not checked" : " · tracked files unchanged") +
        (ClientFilesFound ? " · prepared client found" : " · client setup needed");
    public string CommitLabel => Head.Commit.Length > 0 ? "Local HEAD: " + Head.Commit : "Git is missing or this folder has no readable Git HEAD. Manual selection still works.";
}
public sealed record RepositoryLookupResult(UpstreamHead? Upstream, DiscoveredRepository[] Repositories,
    int DirectoriesChecked, bool Limited, string? UpstreamError)
{
    public DiscoveredRepository? SingleMatch => !Limited && Repositories.Count(r => r.MatchesUpstream) == 1 ? Repositories.Single(r => r.MatchesUpstream) : null;
}

public static class GitRepositoryProbe
{
    public const string UpstreamUrl = "https://github.com/LocalExile/LocalExile.git";

    public static UpstreamHead? ParseUpstream(string output)
    {
        var branch = Regex.Match(output, @"(?m)^ref: refs/heads/([^\r\n\t]+)\tHEAD\r?$", RegexOptions.CultureInvariant);
        var head = Regex.Match(output, @"(?m)^([a-fA-F0-9]{40}|[a-fA-F0-9]{64})\tHEAD\r?$", RegexOptions.CultureInvariant);
        return branch.Success && head.Success ? new(branch.Groups[1].Value, head.Groups[1].Value.ToLowerInvariant(), DateTimeOffset.UtcNow) : null;
    }

    public static async Task<UpstreamHead?> ReadUpstreamAsync(CancellationToken cancellation = default)
    {
        var result = await ReadAsync(null, ["-c", "credential.helper=", "ls-remote", "--symref", UpstreamUrl, "HEAD"], TimeSpan.FromSeconds(10), cancellation);
        return result.ExitCode == 0 ? ParseUpstream(result.Output) : null;
    }

    public static async Task<LocalRepositoryHead> ReadLocalAsync(string root, CancellationToken cancellation = default)
    {
        if (!Directory.Exists(Path.Combine(root, ".git")) && !File.Exists(Path.Combine(root, ".git"))) return new("", "", null);
        var head = await ReadAsync(root, ["rev-parse", "--verify", "HEAD^{commit}"], TimeSpan.FromSeconds(3), cancellation);
        string commit = head.Output.Trim();
        if (head.ExitCode != 0 || !Regex.IsMatch(commit, @"\A(?:[a-fA-F0-9]{40}|[a-fA-F0-9]{64})\z")) return new("", "", null);
        var branch = await ReadAsync(root, ["symbolic-ref", "--quiet", "--short", "HEAD"], TimeSpan.FromSeconds(2), cancellation);
        var changes = await ReadAsync(root, ["diff", "--quiet", "--no-ext-diff", "--no-textconv", "HEAD", "--"], TimeSpan.FromSeconds(3), cancellation);
        var confirmed = await ReadAsync(root, ["rev-parse", "--verify", "HEAD^{commit}"], TimeSpan.FromSeconds(3), cancellation);
        if (confirmed.ExitCode != 0 || !string.Equals(confirmed.Output.Trim(), commit, StringComparison.OrdinalIgnoreCase)) return new("", "", null);
        return new(commit.ToLowerInvariant(), branch.ExitCode == 0 ? branch.Output.Trim() : "Detached HEAD",
            changes.ExitCode is 0 or 1 ? changes.ExitCode == 1 : null);
    }

    public static async Task<GitReadResult> ReadAsync(string? root, IEnumerable<string> arguments, TimeSpan timeout, CancellationToken cancellation = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(timeout);
        var start = new ProcessStartInfo("git") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string key in start.Environment.Keys.Where(k => k.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray()) start.Environment.Remove(key);
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GCM_INTERACTIVE"] = "never";
        start.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        foreach (string arg in new[] { "--no-optional-locks", "-c", "core.fsmonitor=false" }) start.ArgumentList.Add(arg);
        if (root is not null) { start.ArgumentList.Add("-C"); start.ArgumentList.Add(root); }
        foreach (string arg in arguments) start.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start()) return new(-1, "");
            using var cancellationRegistration = deadline.Token.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } catch (Win32Exception) { }
            });
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync(deadline.Token); }
            catch (OperationCanceledException)
            {
                // Only our disposable read-only Git query is terminated; never an emulator or game process.
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } catch (Win32Exception) { }
                await process.WaitForExitAsync(CancellationToken.None);
                await Task.WhenAll(stdout, stderr);
                cancellation.ThrowIfCancellationRequested();
                return new(-1, "");
            }
            await stderr;
            cancellation.ThrowIfCancellationRequested();
            return new(process.ExitCode, await stdout);
        }
        catch (Exception ex) when (ex is Win32Exception or IOException) { return new(-1, ""); }
    }
}

public static class RepositoryLocator
{
    public static async Task<RepositoryLookupResult> FindAsync(IEnumerable<RepositorySearchRoot> roots, CancellationToken cancellation = default,
        Func<CancellationToken, Task<UpstreamHead?>>? upstreamReader = null)
    {
        var upstreamTask = (upstreamReader ?? GitRepositoryProbe.ReadUpstreamAsync)(cancellation);
        var searchTask = Task.Run(() => RepositoryDiscovery.Find(roots, cancellation), cancellation);
        await Task.WhenAll(upstreamTask, searchTask);
        var upstream = await upstreamTask;
        var search = await searchTask;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        var candidates = new System.Collections.Concurrent.ConcurrentBag<DiscoveredRepository>();
        bool limited = search.Limited;
        try
        {
            await Parallel.ForEachAsync(search.Paths, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = deadline.Token }, async (path, token) =>
            {
                var head = await GitRepositoryProbe.ReadLocalAsync(path, token);
                candidates.Add(new(path, head, upstream is not null && head.Commit == upstream.Commit,
                    File.Exists(Path.Combine(path, "Client", "Game.patched.exe")) && File.Exists(Path.Combine(path, "data", "login-keys.json"))));
            });
        }
        catch (OperationCanceledException) { cancellation.ThrowIfCancellationRequested(); limited = true; }
        // Retain folders even when a time-limited Git inspection could not complete.
        foreach (string path in search.Paths.Except(candidates.Select(c => c.Root), StringComparer.OrdinalIgnoreCase))
            candidates.Add(new(path, new("", "", null), false, false));
        return new(upstream, candidates.OrderByDescending(c => c.MatchesUpstream).ThenBy(c => c.Root, StringComparer.OrdinalIgnoreCase).ToArray(),
            search.DirectoriesChecked, limited, upstream is null ? "Could not check GitHub's current default-branch HEAD. Check your connection and Git installation, or choose a folder manually." : null);
    }
}
