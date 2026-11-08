using System.Net;

namespace Waypoint.Tests;

/// <summary>GitHub's release download links failed with 504 while its API served the same files (2026-11-08).</summary>
public class GitHubDownloadTests
{
    const string Link = "https://github.com/LocalExile/LocalExile/releases/download/v0.2.0-beta/LocalExile-v0.2.0-beta-win-x64.zip.sha256";

    sealed class FakeGitHub(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<string> Asked { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken stop)
        {
            Asked.Add(request.RequestUri!.ToString() + " " + string.Join(",", request.Headers.Accept));
            return Task.FromResult(answer(request));
        }
    }

    static HttpResponseMessage Text(HttpStatusCode code, string body = "") => new(code) { Content = new StringContent(body) };

    static HttpResponseMessage Api(HttpRequestMessage request) => request.RequestUri!.ToString() switch
    {
        "https://api.github.com/repos/LocalExile/LocalExile/releases/tags/v0.2.0-beta" => Text(HttpStatusCode.OK,
            """{"assets":[{"name":"LocalExile-v0.2.0-beta-win-x64.zip","url":"https://api.github.com/assets/1"},{"name":"LocalExile-v0.2.0-beta-win-x64.zip.sha256","url":"https://api.github.com/assets/2"}]}"""),
        "https://api.github.com/assets/2" => Text(HttpStatusCode.OK, "abc  LocalExile-v0.2.0-beta-win-x64.zip"),
        _ => Text(HttpStatusCode.NotFound),
    };

    [Fact]
    public async Task A_download_link_that_answers_504_is_fetched_through_the_API()
    {
        var github = new FakeGitHub(r => r.RequestUri!.Host == "github.com" ? Text(HttpStatusCode.GatewayTimeout) : Api(r));
        using var client = new HttpClient(github);
        Assert.Equal("abc  LocalExile-v0.2.0-beta-win-x64.zip", await GitHubDownload.GetStringAsync(client, Link));
        Assert.Equal(3, github.Asked.Count);
        Assert.Contains("application/octet-stream", github.Asked[2]);
    }

    [Fact]
    public async Task A_working_link_and_a_missing_file_are_not_retried()
    {
        var working = new FakeGitHub(_ => Text(HttpStatusCode.OK, "direct"));
        using (var client = new HttpClient(working))
            Assert.Equal("direct", await GitHubDownload.GetStringAsync(client, Link));
        Assert.Single(working.Asked);

        var missing = new FakeGitHub(_ => Text(HttpStatusCode.NotFound));
        using (var client = new HttpClient(missing))
            await Assert.ThrowsAsync<HttpRequestException>(() => GitHubDownload.GetStringAsync(client, Link));
        Assert.Single(missing.Asked);
    }

    [Fact]
    public async Task Other_addresses_are_never_retried()
    {
        var failing = new FakeGitHub(_ => Text(HttpStatusCode.BadGateway));
        using var client = new HttpClient(failing);
        using var response = await GitHubDownload.GetAsync(client, "https://codeload.github.com/LocalExile/Mods/zip/refs/heads/main");
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Single(failing.Asked);
    }
}
