using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Launcher.Core;

/// <summary>
/// A file of a GitHub release has two addresses: its download link (github.com/&lt;owner&gt;/&lt;repo&gt;/releases/download/&lt;tag&gt;/&lt;name&gt;),
/// which is what a browser uses, and its asset in GitHub's API (api.github.com/repos/…/releases/assets/&lt;id&gt;, asked for as
/// application/octet-stream). They are served by different parts of GitHub: on 2026-11-08 the download links answered 504
/// Gateway Time-out for minutes while the API served the same files. So a download link that fails with a server error or a
/// time-out is tried once more through the API. Anything else (404, a stop) is the caller's, as before.
/// </summary>
public static partial class GitHubDownload
{
    [GeneratedRegex(@"\Ahttps://github\.com/([^/]+)/([^/]+)/releases/download/([^/]+)/([^/?#]+)\z")]
    private static partial Regex DownloadLink();

    /// <summary>The response for <paramref name="url"/>, headers read; through the API when the download link fails as above.
    /// The caller checks its status and disposes it.</summary>
    public static async Task<HttpResponseMessage> GetAsync(HttpClient client, string url, CancellationToken stop = default)
    {
        bool fallback = DownloadLink().IsMatch(url);
        try
        {
            var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, stop);
            if (!fallback || (int)response.StatusCode < 500) return response;
            response.Dispose();
        }
        catch (HttpRequestException) when (fallback) { }
        catch (TaskCanceledException) when (fallback && !stop.IsCancellationRequested) { }
        return await ThroughApiAsync(client, url, stop);
    }

    /// <summary>A small file (a .sha256) as text, as <see cref="GetAsync"/> fetches it.</summary>
    public static async Task<string> GetStringAsync(HttpClient client, string url, CancellationToken stop = default)
    {
        using var response = await GetAsync(client, url, stop);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(stop);
    }

    /// <summary>The same file through the API: the release by its tag, the asset by its name, then the asset itself.</summary>
    static async Task<HttpResponseMessage> ThroughApiAsync(HttpClient client, string url, CancellationToken stop)
    {
        var link = DownloadLink().Match(url);
        string owner = link.Groups[1].Value, repo = link.Groups[2].Value, tag = Uri.UnescapeDataString(link.Groups[3].Value), name = Uri.UnescapeDataString(link.Groups[4].Value);
        string? asset = null;
        using (var release = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{owner}/{repo}/releases/tags/{Uri.EscapeDataString(tag)}"))
        {
            release.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            using var answer = await client.SendAsync(release, stop);
            answer.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await answer.Content.ReadAsStringAsync(stop));
            if (document.RootElement.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                foreach (var a in assets.EnumerateArray())
                    if (a.TryGetProperty("name", out var n) && n.GetString() == name && a.TryGetProperty("url", out var u)) asset = u.GetString();
        }
        if (asset is null) throw new HttpRequestException(L.Text("{0} could not be downloaded from GitHub; try again later.", name));
        // The response refers to its request: it stays with it.
        var request = new HttpRequestMessage(HttpMethod.Get, asset);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stop);
    }
}
