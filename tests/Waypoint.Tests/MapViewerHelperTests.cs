namespace Waypoint.Tests;

public class MapViewerHelperTests : IDisposable
{
    readonly string _folder = Path.Combine(Path.GetTempPath(), "waypoint-map-helper-" + Guid.NewGuid().ToString("N"));
    string Checkout => Path.Combine(_folder, "checkout with spaces");
    string Cache => Path.Combine(_folder, "waypoint cache");

    public MapViewerHelperTests()
    {
        Write("src/GameServer.Data/GameServer.Data.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
            </Project>
            """);
        Write("src/GameServer.Data/Terrain/Generation/LevelGenerator.cs", "#error MAP_VIEWER_FIXTURE_BUILD_FAILURE");
        Write("src/GameServer.Data/Terrain/Generation/AreaExits.cs", "");
        Write("data/game-files.zip", "");
        Write("data/tables/WorldAreas.json", "[]");
    }

    void Write(string relative, string text)
    {
        string path = Path.Combine(Checkout, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    [Fact]
    public void GeneratorAndExtractedDataEnableViewerWithoutLocalExileMapHook()
    {
        Assert.False(Directory.Exists(Path.Combine(Checkout, "scripts")));
        Assert.True(MapViewerHelper.IsAvailable(Checkout));
        File.Delete(Path.Combine(Checkout, "data", "game-files.zip"));
        Assert.False(MapViewerHelper.IsAvailable(Checkout));
        Assert.False(MapViewerHelper.IsAvailable(Path.Combine(_folder, "release")));
    }

    [Fact]
    public async Task FailedPreparationKeepsCheckoutUntouchedAndRemovesPrivateBuild()
    {
        var before = Directory.GetFiles(Checkout, "*", SearchOption.AllDirectories)
            .ToDictionary(p => p, File.ReadAllText);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => MapViewerHelper.BuildAsync(Checkout, Cache));
        Assert.Contains("MAP_VIEWER_FIXTURE_BUILD_FAILURE", error.Message);
        Assert.Equal(before.Keys.Order(), Directory.GetFiles(Checkout, "*", SearchOption.AllDirectories).Order());
        foreach (var (path, text) in before) Assert.Equal(text, File.ReadAllText(path));
        Assert.Empty(Directory.GetDirectories(Path.Combine(Cache, "map-viewer")));
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(Cache, "adapter"), "MapViewer.cs", SearchOption.AllDirectories));
    }

    public void Dispose() { if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true); }
}
