using System.Text.Json;

namespace Waypoint;

/// <summary>An open pull request of the server repository. <see cref="Source"/> is the repository its branch lives in
/// (owner/name); <see cref="FromFork"/> when that is not the server repository itself, or is gone.</summary>
sealed record PullRequest(int Number, string Title, string Author, bool Draft, DateTimeOffset Updated, string Source,
    bool FromFork, string Page);

/// <summary>The open pull requests, from GitHub's public API (no token: 60 requests an hour, so the list is kept for a
/// minute and read again only on Refresh).</summary>
static class PullRequests
{
    static (string Repository, DateTime At, List<PullRequest> List)? _cache;

    public static async Task<List<PullRequest>> OpenAsync(string repository, bool refresh)
    {
        if (!refresh && _cache is { } c && c.Repository == repository && DateTime.UtcNow - c.At < TimeSpan.FromMinutes(1)) return c.List;
        using var client = Updates.Client();
        string json = await client.GetStringAsync($"https://api.github.com/repos/{repository}/pulls?state=open&per_page=100");
        var list = Parse(json, repository);
        _cache = (repository, DateTime.UtcNow, list);
        return list;
    }

    internal static List<PullRequest> Parse(string json, string repository)
    {
        using var doc = JsonDocument.Parse(json);
        var list = new List<PullRequest>();
        foreach (var p in doc.RootElement.EnumerateArray())
        {
            string? source = p.GetProperty("head").TryGetProperty("repo", out var repo) && repo.ValueKind == JsonValueKind.Object
                ? repo.GetProperty("full_name").GetString() : null;
            DateTimeOffset.TryParse(p.GetProperty("updated_at").GetString(), out var updated);
            list.Add(new(
                p.GetProperty("number").GetInt32(),
                p.GetProperty("title").GetString() ?? "",
                p.GetProperty("user").GetProperty("login").GetString() ?? "",
                p.TryGetProperty("draft", out var draft) && draft.GetBoolean(),
                updated,
                source ?? "(deleted fork)",
                !string.Equals(source, repository, StringComparison.OrdinalIgnoreCase),
                p.GetProperty("html_url").GetString() ?? ""));
        }
        return [.. list.OrderByDescending(p => p.Updated)];
    }
}
