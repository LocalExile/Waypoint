namespace Launcher.Core;

/// <summary>Waypoint's map adapter, built against the selected checkout's generator in a separate process.
/// Source and build outputs stay in Waypoint's data folder; the checkout supplies its existing libraries and game data.</summary>
public sealed class MapViewerHelper : IDisposable
{
    readonly string _folder;
    MapViewerHelper(string folder) => _folder = folder;

    public LocalRepositoryHead? CheckoutHead { get; private set; }

    public string AssemblyPath => Path.Combine(_folder, "host", "Waypoint.MapViewer.dll");

    public static bool IsAvailable(string checkout) =>
        File.Exists(Path.Combine(checkout, "src", "GameServer.Data", "GameServer.Data.csproj"))
        && File.Exists(Path.Combine(checkout, "src", "GameServer.Data", "Terrain", "Generation", "LevelGenerator.cs"))
        && File.Exists(Path.Combine(checkout, "src", "GameServer.Data", "Terrain", "Generation", "AreaExits.cs"))
        && File.Exists(Path.Combine(checkout, "data", "game-files.zip"))
        && File.Exists(Path.Combine(checkout, "data", "tables", "WorldAreas.json"));

    /// <summary>Call while holding the checkout's operation lock. Builds only the data library, never a gameplay server.</summary>
    public static async Task<MapViewerHelper> BuildAsync(string checkout, string dataDirectory, CancellationToken stop = default)
    {
        stop.ThrowIfCancellationRequested();
        checkout = Path.GetFullPath(checkout);
        if (!IsAvailable(checkout)) throw new NotSupportedException(L.Text("The map viewer needs a source checkout with the terrain generator and extracted game data."));
        string folder = Path.Combine(Path.GetFullPath(dataDirectory), "map-viewer", Guid.NewGuid().ToString("N"));
        var helper = new MapViewerHelper(folder);
        try
        {
            var head = await GitRepositoryProbe.ReadLocalAsync(checkout, stop);
            string source = Path.GetDirectoryName(BridgeScripts.Extract(dataDirectory))!;
            string generator = Path.Combine(folder, "generator");
            await Build(Path.Combine(checkout, "src", "GameServer.Data", "GameServer.Data.csproj"),
                generator, Path.Combine(folder, "generator-artifacts"), []);
            await Build(Path.Combine(source, "MapViewer.csproj"), Path.Combine(folder, "host"),
                Path.Combine(folder, "host-artifacts"), ["-p:MapGeneratorDirectory=" + generator]);
            var confirmed = await GitRepositoryProbe.ReadLocalAsync(checkout, stop);
            if (head == confirmed) helper.CheckoutHead = head;
            return helper;
        }
        catch { helper.Dispose(); throw; }

        async Task Build(string project, string output, string artifacts, string[] extra)
        {
            var lines = new List<string>();
            int code = await ToolProcess.RunAsync("dotnet",
                ["build", project, "-c", "Release", "--artifacts-path", artifacts, "-o", output, "--nologo", "-v", "quiet", "-p:UseSharedCompilation=false", .. extra],
                checkout, line => { lock (lines) lines.Add(line); }, stop);
            if (code != 0) throw new InvalidOperationException(L.Text("Could not prepare the map viewer.\n{0}", string.Join("\n", lines)));
        }
    }

    /// <summary>After the helper process exits, discard only this build's private cache directory.</summary>
    public void Dispose()
    {
        // Windows can briefly retain a mapped DLL after its process exits. Bound the wait on close to 250 ms.
        for (int attempt = 0; attempt < 6; attempt++)
        {
            try { if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true); return; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { if (attempt < 5) Thread.Sleep(50); }
        }
    }
}
