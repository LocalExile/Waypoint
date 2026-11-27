namespace Waypoint.Tests;

/// <summary>DepotDownloader's own release for each system Waypoint ships on, each held to its SHA-256 (<see cref="DepotTool"/>).</summary>
public class DepotToolTests
{
    [Fact]
    public void WindowsRunsTheWindowsReleaseAsItAlwaysHas()
    {
        var release = DepotTool.For(linux: false);
        Assert.EndsWith("/DepotDownloader_3.4.0/DepotDownloader-windows-x64.zip", release.Url, StringComparison.Ordinal);
        Assert.Equal("41c9e9f0df54b3ad02e67a11726756e5c73283bd7c2e1b04acfa5ae4c2ed3767", release.Sha256);
        Assert.Equal("DepotDownloader.exe", release.Program);
    }

    [Fact]
    public void LinuxRunsTheLinuxReleaseOfTheSameVersion()
    {
        var release = DepotTool.For(linux: true);
        Assert.EndsWith("/DepotDownloader_3.4.0/DepotDownloader-linux-x64.zip", release.Url, StringComparison.Ordinal);
        Assert.Equal("a999dec66b4850fc961bd50366696d23c2d0fad7b18790e6a5647b2f19097a53", release.Sha256);
        Assert.Equal("DepotDownloader", release.Program);
        Assert.Equal(33_442_357, release.Bytes);
    }

    [Fact]
    public void EachHashIsAWholeLowercaseSha256()
    {
        foreach (var release in new[] { DepotTool.For(false), DepotTool.For(true) })
            Assert.Matches("^[0-9a-f]{64}$", release.Sha256);
    }
}
