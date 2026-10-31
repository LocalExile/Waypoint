using Waypoint;

namespace Waypoint.Tests;

// The launcher's own releases: versions from tags, GitHub's answer, the checksum file.
public class UpdatesTests
{
    [Theory]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("V0.10.0", "0.10.0")]
    [InlineData("1.2", "1.2.0")]
    [InlineData("0.0.0-dev+5ca9daa9", "0.0.0")]
    [InlineData("1.4.0-beta.2", "1.4.0")]
    public void Tags_and_build_versions_compare_by_their_numbers(string text, string expected) =>
        Assert.Equal(Version.Parse(expected), Updates.Parse(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("vnext")]
    public void Anything_else_is_no_version(string? text) => Assert.Null(Updates.Parse(text));

    [Fact]
    public void Newer_means_by_number_not_by_text() =>
        Assert.True(Updates.Parse("v0.10.0") > Updates.Parse("v0.9.3"));

    [Fact]
    public void A_build_from_source_is_no_release() => Assert.False(Updates.IsReleaseBuild);

    [Fact]
    public void The_latest_release_gives_its_version_page_notes_and_files()
    {
        var release = Updates.ParseRelease("""
            {"tag_name":"v0.3.0","html_url":"https://github.com/LocalExile/Waypoint/releases/tag/v0.3.0",
             "body":"Faster logs.","assets":[
               {"name":"Waypoint.exe.sha256","browser_download_url":"https://example.test/a.sha256"},
               {"name":"Waypoint.exe","browser_download_url":"https://example.test/a.exe"}]}
            """)!;

        Assert.Equal(new Version(0, 3, 0), release.Version);
        Assert.Equal("v0.3.0", release.Tag);
        Assert.EndsWith("/releases/tag/v0.3.0", release.Page);
        Assert.Equal("Faster logs.", release.Notes);
        Assert.Equal("https://example.test/a.exe", release.ExeUrl);
        Assert.Equal("https://example.test/a.sha256", release.HashUrl);
    }

    [Fact]
    public void A_release_without_its_files_or_version_tag_cannot_update()
    {
        var bare = Updates.ParseRelease("""{"tag_name":"v1.0.0","html_url":"x","body":null,"assets":[]}""")!;
        Assert.Null(bare.ExeUrl);
        Assert.Null(bare.HashUrl);
        Assert.Null(Updates.ParseRelease("""{"tag_name":"nightly","assets":[]}"""));
    }

    [Fact]
    public void The_checksum_is_the_first_word_of_the_sha256_file()
    {
        string hash = new('A', 64);
        Assert.Equal(new string('a', 64), Updates.HashOf($"{hash}  Waypoint.exe\n"));
        Assert.Equal(new string('a', 64), Updates.HashOf(hash));
        Assert.Null(Updates.HashOf("not a hash"));
        Assert.Null(Updates.HashOf(""));
    }
}
