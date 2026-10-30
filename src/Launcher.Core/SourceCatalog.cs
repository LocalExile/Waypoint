using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Launcher.Core;

public sealed record SourceChoice(string Kind, string Value, string Label, string Commit, string Detail)
{
    public string SearchText => Label + " " + Detail + " " + Commit;
    public override string ToString() => Label;
}
public sealed record SourceSwitchRequest(string Kind, string Value, bool StartFresh = false);
public sealed record SourceCatalogResult(SourceChoice[] Sources, string? Warning);

public static class SourceCatalog
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    static readonly SemaphoreSlim CacheGate = new(1, 1);
    static SourceChoice[] cachedPrs = [];
    static DateTimeOffset cachedAt;
    static string? etag;

    public static SourceChoice[] ParseBranches(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(line => line.TrimEnd('\r').Split('\t')).Where(parts => parts.Length == 2 &&
            Regex.IsMatch(parts[1], "\\A[0-9a-fA-F]{40}(?:[0-9a-fA-F]{24})?\\z") &&
            (parts[0].StartsWith("refs/heads/", StringComparison.Ordinal) || parts[0].StartsWith("refs/remotes/", StringComparison.Ordinal)) &&
            !parts[0].EndsWith("/HEAD", StringComparison.Ordinal))
        .Select(parts => new SourceChoice(parts[0].StartsWith("refs/heads/", StringComparison.Ordinal) ? "Branch" : "Remote",
            parts[0], parts[0].Replace("refs/heads/", "").Replace("refs/remotes/", ""), parts[1],
            parts[0].StartsWith("refs/heads/", StringComparison.Ordinal) ? "Local branch" : "Fetched remote branch"))
        .OrderBy(s => s.Kind).ThenBy(s => s.Label, StringComparer.OrdinalIgnoreCase).ToArray();

    public static SourceChoice[] ParsePullRequests(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateArray().Select(pr => new SourceChoice("PR", pr.GetProperty("number").GetInt32().ToString(),
            $"PR #{pr.GetProperty("number").GetInt32()} · {pr.GetProperty("title").GetString()}",
            pr.GetProperty("head").GetProperty("sha").GetString() ?? "",
            pr.GetProperty("head").GetProperty("label").GetString() ?? ""))
            .Where(s => int.TryParse(s.Value, out int number) && number > 0 && Regex.IsMatch(s.Commit, "\\A[0-9a-fA-F]{40}\\z")).ToArray();
    }

    public static async Task<SourceCatalogResult> LoadAsync(string root, bool refresh, CancellationToken cancellation = default,
        Action<SourceChoice[]>? localReady = null)
    {
        // Git queries and GitHub metadata are independent. No fetch or checkout occurs when opening the picker.
        var branchesTask = GitRepositoryProbe.ReadAsync(root,
            ["for-each-ref", "--format=%(refname)%09%(objectname)", "refs/heads/", "refs/remotes/"], TimeSpan.FromSeconds(5), cancellation);
        var branches = await branchesTask;
        localReady?.Invoke(ParseBranches(branches.Output));
        string? warning = null;
        await CacheGate.WaitAsync(cancellation);
        SourceChoice[] prs;
        try
        {
            if (refresh || DateTimeOffset.UtcNow - cachedAt > TimeSpan.FromMinutes(2))
            {
                try
                {
                    var all = new List<SourceChoice>();
                    string? nextEtag = null;
                    for (int page = 1; page <= 10; page++)
                    {
                        using var request = new HttpRequestMessage(HttpMethod.Get,
                            $"https://api.github.com/repos/LocalExile/LocalExile/pulls?state=open&per_page=100&page={page}");
                        request.Headers.UserAgent.ParseAdd("LocalWorkspaceLauncher/0.2");
                        if (page == 1 && etag is not null) request.Headers.TryAddWithoutValidation("If-None-Match", etag);
                        using var response = await Http.SendAsync(request, cancellation);
                        if (response.StatusCode == System.Net.HttpStatusCode.NotModified) { all.AddRange(cachedPrs); break; }
                        response.EnsureSuccessStatusCode();
                        if (page == 1) nextEtag = response.Headers.ETag?.ToString();
                        var values = ParsePullRequests(await response.Content.ReadAsStringAsync(cancellation));
                        all.AddRange(values);
                        if (values.Length < 100) break;
                        if (page == 10) warning = "Showing the first 1,000 open PRs. Enter a PR number to fetch another.";
                    }
                    cachedPrs = all.ToArray(); cachedAt = DateTimeOffset.UtcNow; etag = nextEtag ?? etag;
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or KeyNotFoundException)
                { cancellation.ThrowIfCancellationRequested(); warning = "GitHub PR list unavailable. Local/fetched branches still work; enter a PR number to try it directly."; }
            }
            prs = cachedPrs;
        }
        finally { CacheGate.Release(); }
        if (branches.ExitCode != 0) warning = "Could not read local branches. Check the selected Git checkout. " + warning;
        return new(ParseBranches(branches.Output).Concat(prs).ToArray(), warning);
    }
}
