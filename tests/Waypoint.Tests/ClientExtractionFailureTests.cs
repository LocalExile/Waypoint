using System.Security.Cryptography;

namespace Waypoint.Tests;

public sealed class ClientExtractionFailureTests : IDisposable
{
    readonly string folder = Path.Combine(Path.GetTempPath(), "Waypoint-extraction-" + Guid.NewGuid().ToString("N"));

    public ClientExtractionFailureTests()
    {
        Directory.CreateDirectory(Path.Combine(folder, "Client"));
        Directory.CreateDirectory(Path.Combine(folder, "scripts", "dev"));
        File.WriteAllText(Path.Combine(folder, "Client", "Game.exe"), "old executable fixture");
        File.WriteAllText(Path.Combine(folder, "Client", "PathOfExile.exe"), "new executable fixture");
    }

    [Fact]
    public async Task Hnc_failure_names_the_consumed_file_both_fingerprints_and_the_log()
    {
        File.WriteAllText(Path.Combine(folder, "scripts", "dev", "extract-data.ps1"),
            "Write-Output 'HNC mask not found: the client is not the expected 3.29.3c build.'\nWrite-Output 'extract-data: client-constants failed'\nexit 1\n");
        var error = await Assert.ThrowsAsync<IOException>(() => SetupSequence.ExtractAsync(folder, _ => { }, CancellationToken.None));
        Assert.Contains(Path.Combine(folder, "Client", "Game.exe"), error.Message);
        Assert.Contains(Path.Combine(folder, "Client", "PathOfExile.exe"), error.Message);
        Assert.Contains(Path.Combine(folder, "logs", "extract-client-constants.log"), error.Message);
        Assert.Contains(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(folder, "Client", "Game.exe")))), error.Message);
        Assert.Contains("extract-data: client-constants failed", error.Message);
        Assert.Equal("old executable fixture", File.ReadAllText(Path.Combine(folder, "Client", "Game.exe")));
    }

    [Fact]
    public async Task Other_extraction_failures_keep_their_own_error()
    {
        File.WriteAllText(Path.Combine(folder, "scripts", "dev", "extract-data.ps1"), "Write-Output 'Node is missing'\nexit 1\n");
        var error = await Assert.ThrowsAsync<IOException>(() => SetupSequence.ExtractAsync(folder, _ => { }, CancellationToken.None));
        Assert.Equal("Node is missing", error.Message);
        Assert.Equal(ClientLocator.KnownExecutable, ClientLocator.ExecutableHashFor("3.29.3c"));
        Assert.NotNull(ClientLocator.ExecutableHashFor("3.29.3b"));
        Assert.Null(ClientLocator.ExecutableHashFor("future build"));
    }

    public void Dispose() => Directory.Delete(folder, recursive: true);
}
