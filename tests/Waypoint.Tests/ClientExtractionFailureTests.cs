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
        // The release and its 3.29.3.5 hotfix are both the 3.29.3c build. Waypoint's own rows come first and only they are
        // looked at: a hash a LocalExile's list named in another test stays learned for the whole run, and follows them.
        Assert.Equal(["cc8aa38345be2a837575215d2ce142ebecf7b007345843265b335ebb8ac5964c", "01a22facf36886b39bca74793c0d8c7295b1495f1625b4ebc7b23c2de32a70cf"],
            ClientLocator.ExecutableHashesFor("3.29.3c")[..2]);
        Assert.Equal(ClientLocator.KnownExecutable, ClientLocator.ExecutableHashesFor(ClientLocator.Build)[0]);
        Assert.Equal("78a0e227189c263658a7f65a02f086428b859bfc114ae8e7ce8aab721ebd48d5", ClientLocator.ExecutableHashesFor("3.29.3b")[0]);
        Assert.Empty(ClientLocator.ExecutableHashesFor("future build"));
    }

    [Theory]
    [InlineData("Unhandled exception. System.IO.InvalidDataException: HNC mask not found: the client is not the expected 3.29.3c build.", true)]
    [InlineData("hnc mask not found", true)]
    [InlineData("extract-data: client-constants failed (exit -532462766)", false)]
    [InlineData("HNC Protocol Violated", false)]
    [InlineData("Node is missing", false)]
    [InlineData(null, false)]
    public void Setup_recovery_is_recommended_only_for_the_recognized_Hnc_failure(string? message, bool expected)
    {
        Assert.Equal(expected, ClientExtractionFailure.IsHncFailure(message));
    }

    // LocalExile's own sentences: its allowed-builds check (live.ps1's extraction, the Auth tools) and a release's launcher.
    // The first three are one sentence as live.ps1's console wrapped it on its way to Waypoint.
    [Theory]
    [InlineData("Unhandled exception. GameServer.Core.Protocol.UnknownClientBuildException: this is not a Path of Exile executable ", true)]
    [InlineData("LocalExile runs (SHA-256 78a0e227189c2636...): it runs 3.29.3c (release, build 327984); 3.29.3c (3.29.3.5 hotfix of ", false)]
    [InlineData("2026-11-09, build 328126); 3.29.3d (release of 2026-11-10, build 328228), and refuses any other build", true)]
    [InlineData("this is not a Path of Exile executable LocalExile runs (SHA-256 78a0e227189c2636...): it runs 3.29.3c (release, build 327984), and refuses any other build", true)]
    [InlineData(@"C:\Game\PathOfExile.exe (SHA-256 78a0e227189c2636...) is another build of Path of Exile. LocalExile runs 3.29.3d (release of 2026-11-10, build 328228); other versions do not work", true)]
    [InlineData(@"C:\Steam\Path of Exile is a Steam install of another build; LocalExile runs Steam's 3.29.3d", true)]
    [InlineData(@"C:\Game mixes two builds: C:\Game\PathOfExileSteam.exe is 3.29.3d, and the game files in Bundles2 are another build's", true)]
    [InlineData("HNC mask not found: the client is not a 3.29.3c or 3.29.3d build LocalExile runs.", false)]
    [InlineData("extract-data: client-constants failed (exit -532462766)", false)]
    [InlineData("This client is Path of Exile 3.29.3d (release of 2026-11-10, build 328228). LocalExile supports 3.29.3c and 3.29.3d.", false)]
    [InlineData(null, false)]
    public void A_client_LocalExile_refuses_as_no_build_it_runs_is_recognized(string? message, bool expected)
    {
        Assert.Equal(expected, ClientExtractionFailure.IsBuildRefusal(message));
    }

    public void Dispose() => Directory.Delete(folder, recursive: true);
}
