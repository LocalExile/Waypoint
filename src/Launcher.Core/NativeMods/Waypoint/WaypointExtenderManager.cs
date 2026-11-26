using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocalExile.Client.Waypoint;

/// <summary>How the NativeMods extender DLL is loaded into the 3.29 client process.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ExtenderInstallMode
{
    /// <summary>Primary mode: adds an import table entry for <c>LocalExile.ClientHost.dll</c> in <c>Client/Game.patched.exe</c>.</summary>
    ImportTable,

    /// <summary>Fallback mode: deploys a <c>version.dll</c> proxy beside <c>Game.patched.exe</c> (requires <c>WINEDLLOVERRIDES=version=n,b</c> on Proton).</summary>
    ProxyVersionDll
}

/// <summary>Result returned by an <see cref="IPePatcherBridge"/> operation.</summary>
public sealed record PePatchResult(
    bool Succeeded,
    string Message,
    bool IsPatched = false,
    string? DetectedBuild = null,
    string? ExecutableSha256 = null);

/// <summary>
/// Abstraction decoupling Waypoint's extender manager from the concrete PE32+ patcher implementation
/// (<c>LocalExile.Client.Patcher</c> library or CLI).
/// </summary>
public interface IPePatcherBridge
{
    Task<PePatchResult> CheckAsync(string patchedExePath, CancellationToken cancellationToken = default);
    Task<PePatchResult> PatchAsync(string patchedExePath, string hostDllName, CancellationToken cancellationToken = default);
    Task<PePatchResult> UnpatchAsync(string patchedExePath, CancellationToken cancellationToken = default);
    Task<PePatchResult> VerifyAsync(string patchedExePath, CancellationToken cancellationToken = default);
}

/// <summary>
/// Source bundle paths and options for installing the NativeMods script extender via Waypoint.
/// </summary>
public sealed record ExtenderInstallOptions(
    string ExtenderVersion,
    string NativeShimDllSourcePath,
    string ManagedHostBundleDir,
    ExtenderInstallMode Mode = ExtenderInstallMode.ImportTable,
    string? AddressLibraryDir = null,
    string? ProxyVersionDllSourcePath = null);

/// <summary>Persistent state recorded at <c>Mods/Client/.extender-state.json</c>.</summary>
public sealed record ExtenderStateRecord(
    [property: JsonPropertyName("extenderVersion")] string ExtenderVersion,
    [property: JsonPropertyName("mode")] ExtenderInstallMode Mode,
    [property: JsonPropertyName("installedUtc")] DateTimeOffset InstalledUtc,
    [property: JsonPropertyName("nativeShimSha256")] string NativeShimSha256,
    [property: JsonPropertyName("proxyDllSha256")] string? ProxyDllSha256,
    [property: JsonPropertyName("managedHostFileCount")] int ManagedHostFileCount,
    [property: JsonPropertyName("patchedExePath")] string? PatchedExePath,
    [property: JsonPropertyName("detectedBuild")] string? DetectedBuild);

/// <summary>Live status snapshot presented in Waypoint's NativeMods extender UI panel.</summary>
public sealed record ExtenderStatus(
    bool IsInstalled,
    bool IsHealthy,
    string? ExtenderVersion,
    ExtenderInstallMode? Mode,
    bool StockClientPresent,
    bool PatchedClientPresent,
    bool NativeShimPresent,
    bool ProxyDllPresent,
    bool ManagedHostPresent,
    bool PeImportVerified,
    string? NativeShimSha256,
    string? DetectedBuild,
    int InstalledClientModCount,
    IReadOnlyList<string> Diagnostics);

/// <summary>Result of installing or removing the NativeMods script extender.</summary>
public sealed record ExtenderOperationResult(
    bool Succeeded,
    string Message,
    ExtenderStatus Status);

/// <summary>
/// High-level Waypoint API for inspecting, installing, verifying, and cleanly removing the NativeMods
/// script extender (<c>LocalExile.ClientHost.dll</c> + managed host bundle + PE import patch).
/// Never modifies <c>Client/Game.exe</c>, <c>Client/Content.ggpk</c>, or <c>Client/Bundles2/</c>.
/// </summary>
public sealed class WaypointExtenderManager
{
    public const string NativeShimFileName = "LocalExile.ClientHost.dll";
    public const string ProxyDllFileName = "version.dll";
    public const string ManagedHostFolderName = "nativemods-host";
    public const string AddressLibraryFolderName = "addresses";
    public const string StateFileName = ".extender-state.json";

    private readonly IPePatcherBridge? _patcher;

    public WaypointExtenderManager(IPePatcherBridge? patcher = null)
    {
        _patcher = patcher;
    }

    public static string GetClientDir(string localExileRoot) => Path.Combine(localExileRoot, "Client");
    public static string GetStockExePath(string localExileRoot) => Path.Combine(GetClientDir(localExileRoot), "Game.exe");
    public static string GetPatchedExePath(string localExileRoot) => Path.Combine(GetClientDir(localExileRoot), "Game.patched.exe");
    public static string GetNativeShimPath(string localExileRoot) => Path.Combine(GetClientDir(localExileRoot), NativeShimFileName);
    public static string GetProxyDllPath(string localExileRoot) => Path.Combine(GetClientDir(localExileRoot), ProxyDllFileName);
    public static string GetManagedHostDir(string localExileRoot) => Path.Combine(GetClientDir(localExileRoot), ManagedHostFolderName);
    public static string GetStateFilePath(string localExileRoot) => Path.Combine(WaypointModInstaller.GetClientModsRoot(localExileRoot), StateFileName);

    /// <summary>Inspects the LocalExile checkout and returns the current NativeMods extender status.</summary>
    public async Task<ExtenderStatus> GetStatusAsync(string localExileRoot, CancellationToken cancellationToken = default)
    {
        var diagnostics = new List<string>();
        bool stockPresent = File.Exists(GetStockExePath(localExileRoot));
        string patchedExe = GetPatchedExePath(localExileRoot);
        bool patchedPresent = File.Exists(patchedExe);
        string shimPath = GetNativeShimPath(localExileRoot);
        bool shimPresent = File.Exists(shimPath);
        string proxyPath = GetProxyDllPath(localExileRoot);
        bool proxyPresent = File.Exists(proxyPath);
        string hostDir = GetManagedHostDir(localExileRoot);
        bool hostPresent = Directory.Exists(hostDir) && Directory.EnumerateFiles(hostDir, "*", SearchOption.AllDirectories).Any();

        var state = ReadState(localExileRoot);
        int clientModCount = WaypointModInstaller.ReadLedger(localExileRoot).Mods.Count(m => m.ClientDir is not null);

        string? actualShimHash = shimPresent ? DualModPackageValidator.ComputeFileSha256(shimPath) : null;
        if (state is not null && actualShimHash is not null &&
            !actualShimHash.Equals(state.NativeShimSha256, StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add("LocalExile.ClientHost.dll SHA-256 differs from recorded install state.");
        }

        bool peVerified = false;
        string? detectedBuild = state?.DetectedBuild;

        if (patchedPresent && _patcher is not null)
        {
            var verify = await _patcher.VerifyAsync(patchedExe, cancellationToken);
            peVerified = verify.Succeeded && verify.IsPatched;
            detectedBuild ??= verify.DetectedBuild;
            if (state?.Mode == ExtenderInstallMode.ImportTable && !peVerified)
                diagnostics.Add($"Game.patched.exe import check failed: {verify.Message}");
        }
        else if (!patchedPresent)
        {
            diagnostics.Add("Client/Game.patched.exe not found (run LocalExile's GameServer.Auth patch step first).");
        }

        bool isInstalled = state is not null || shimPresent || hostPresent;
        ExtenderInstallMode? mode = state?.Mode ?? (proxyPresent ? ExtenderInstallMode.ProxyVersionDll : shimPresent ? ExtenderInstallMode.ImportTable : null);

        bool healthy;
        if (!isInstalled)
        {
            healthy = false;
        }
        else if (mode == ExtenderInstallMode.ProxyVersionDll)
        {
            healthy = shimPresent && proxyPresent && hostPresent && diagnostics.Count == 0;
            if (!proxyPresent) diagnostics.Add("Proxy mode active, but Client/version.dll is missing.");
        }
        else
        {
            bool patchRequirementMet = _patcher is null ? patchedPresent : peVerified;
            healthy = shimPresent && hostPresent && patchRequirementMet && diagnostics.Count == 0;
        }

        if (isInstalled && !shimPresent) diagnostics.Add($"Missing {NativeShimFileName} in Client/.");
        if (isInstalled && !hostPresent) diagnostics.Add($"Missing managed host bundle in Client/{ManagedHostFolderName}/.");

        return new ExtenderStatus(
            IsInstalled: isInstalled,
            IsHealthy: healthy,
            ExtenderVersion: state?.ExtenderVersion,
            Mode: mode,
            StockClientPresent: stockPresent,
            PatchedClientPresent: patchedPresent,
            NativeShimPresent: shimPresent,
            ProxyDllPresent: proxyPresent,
            ManagedHostPresent: hostPresent,
            PeImportVerified: peVerified,
            NativeShimSha256: actualShimHash,
            DetectedBuild: detectedBuild,
            InstalledClientModCount: clientModCount,
            Diagnostics: diagnostics);
    }

    /// <summary>
    /// Installs or updates the NativeMods script extender into <c>Client/</c> and coordinates PE patching
    /// of <c>Client/Game.patched.exe</c> (leaving <c>Client/Game.exe</c> untouched).
    /// </summary>
    public async Task<ExtenderOperationResult> InstallAsync(
        string localExileRoot,
        ExtenderInstallOptions options,
        CancellationToken cancellationToken = default)
    {
        if (!DualModPackageValidator.IsPhysicalPath(localExileRoot))
            throw new IOException("Redirected LocalExile root paths are not supported.");

        if (!File.Exists(options.NativeShimDllSourcePath))
        {
            var status = await GetStatusAsync(localExileRoot, cancellationToken);
            return new(false, $"Native shim DLL not found: '{options.NativeShimDllSourcePath}'.", status);
        }

        if (!Directory.Exists(options.ManagedHostBundleDir))
        {
            var status = await GetStatusAsync(localExileRoot, cancellationToken);
            return new(false, $"Managed host bundle directory not found: '{options.ManagedHostBundleDir}'.", status);
        }

        string clientDir = GetClientDir(localExileRoot);
        Directory.CreateDirectory(clientDir);

        string patchedExe = GetPatchedExePath(localExileRoot);
        string? detectedBuild = null;

        if (options.Mode == ExtenderInstallMode.ImportTable)
        {
            if (!File.Exists(patchedExe))
            {
                var status = await GetStatusAsync(localExileRoot, cancellationToken);
                return new(false, "Client/Game.patched.exe does not exist; run LocalExile patch step first or use ProxyVersionDll mode.", status);
            }

            if (_patcher is null)
            {
                var status = await GetStatusAsync(localExileRoot, cancellationToken);
                return new(false, "ImportTable mode requires an IPePatcherBridge instance.", status);
            }

            var check = await _patcher.CheckAsync(patchedExe, cancellationToken);
            if (!check.Succeeded)
            {
                var status = await GetStatusAsync(localExileRoot, cancellationToken);
                return new(false, $"PE pre-check failed: {check.Message}", status);
            }
            detectedBuild = check.DetectedBuild;
        }
        else if (options.Mode == ExtenderInstallMode.ProxyVersionDll)
        {
            if (string.IsNullOrWhiteSpace(options.ProxyVersionDllSourcePath) || !File.Exists(options.ProxyVersionDllSourcePath))
            {
                var status = await GetStatusAsync(localExileRoot, cancellationToken);
                return new(false, "ProxyVersionDll mode requires a valid ProxyVersionDllSourcePath.", status);
            }
        }

        // 1. Copy LocalExile.ClientHost.dll into Client/
        string destShim = GetNativeShimPath(localExileRoot);
        CopyFileAtomically(options.NativeShimDllSourcePath, destShim);
        string shimHash = DualModPackageValidator.ComputeFileSha256(destShim);

        // 2. Copy managed host bundle (+ optional address library) into Client/nativemods-host/
        string destHostDir = GetManagedHostDir(localExileRoot);
        string hostStaging = Path.Combine(clientDir, $".staging-host-{Guid.NewGuid():N}");
        Directory.CreateDirectory(hostStaging);
        int hostFileCount = 0;
        try
        {
            hostFileCount += CopyDirectoryFiles(options.ManagedHostBundleDir, hostStaging);
            if (!string.IsNullOrWhiteSpace(options.AddressLibraryDir) && Directory.Exists(options.AddressLibraryDir))
            {
                hostFileCount += CopyDirectoryFiles(options.AddressLibraryDir, Path.Combine(hostStaging, AddressLibraryFolderName));
            }

            if (Directory.Exists(destHostDir))
                Directory.Delete(destHostDir, recursive: true);
            Directory.Move(hostStaging, destHostDir);
        }
        finally
        {
            if (Directory.Exists(hostStaging))
                try { Directory.Delete(hostStaging, recursive: true); } catch { }
        }

        // 3. Apply either PE import patch or proxy version.dll
        string? proxyHash = null;
        if (options.Mode == ExtenderInstallMode.ImportTable && _patcher is not null)
        {
            var patchResult = await _patcher.PatchAsync(patchedExe, NativeShimFileName, cancellationToken);
            if (!patchResult.Succeeded)
            {
                var status = await GetStatusAsync(localExileRoot, cancellationToken);
                return new(false, $"PE patch failed: {patchResult.Message}", status);
            }
            detectedBuild ??= patchResult.DetectedBuild;

            // Remove proxy DLL if previously installed in proxy mode
            string oldProxy = GetProxyDllPath(localExileRoot);
            if (File.Exists(oldProxy))
                File.Delete(oldProxy);
        }
        else if (options.Mode == ExtenderInstallMode.ProxyVersionDll && options.ProxyVersionDllSourcePath is not null)
        {
            string destProxy = GetProxyDllPath(localExileRoot);
            CopyFileAtomically(options.ProxyVersionDllSourcePath, destProxy);
            proxyHash = DualModPackageValidator.ComputeFileSha256(destProxy);
        }

        // 4. Write extender state record under Mods/Client/.extender-state.json
        var state = new ExtenderStateRecord(
            options.ExtenderVersion,
            options.Mode,
            DateTimeOffset.UtcNow,
            shimHash,
            proxyHash,
            hostFileCount,
            File.Exists(patchedExe) ? DualModPackageValidator.NormalizeVirtualPath(Path.GetRelativePath(localExileRoot, patchedExe)) : null,
            detectedBuild);

        WriteState(localExileRoot, state);

        var finalStatus = await GetStatusAsync(localExileRoot, cancellationToken);
        return new(true, $"Installed NativeMods extender v{options.ExtenderVersion} ({options.Mode}).", finalStatus);
    }

    /// <summary>
    /// Removes the NativeMods script extender, unpatches <c>Game.patched.exe</c> if patched, and leaves
    /// <c>Game.exe</c> and stock game archives completely untouched.
    /// </summary>
    public async Task<ExtenderOperationResult> RemoveAsync(
        string localExileRoot,
        bool removeInstalledClientMods = false,
        CancellationToken cancellationToken = default)
    {
        var state = ReadState(localExileRoot);
        string patchedExe = GetPatchedExePath(localExileRoot);

        // 1. Unpatch Game.patched.exe if import-table patched
        if (_patcher is not null && File.Exists(patchedExe))
        {
            var verify = await _patcher.VerifyAsync(patchedExe, cancellationToken);
            if (verify.Succeeded && verify.IsPatched)
            {
                var unpatch = await _patcher.UnpatchAsync(patchedExe, cancellationToken);
                if (!unpatch.Succeeded)
                {
                    var failedStatus = await GetStatusAsync(localExileRoot, cancellationToken);
                    return new(false, $"Failed to unpatch Game.patched.exe: {unpatch.Message}", failedStatus);
                }
            }
        }

        // 2. Delete deployed extender files from Client/
        string shimPath = GetNativeShimPath(localExileRoot);
        if (File.Exists(shimPath))
            File.Delete(shimPath);

        string proxyPath = GetProxyDllPath(localExileRoot);
        if (File.Exists(proxyPath) && (state is null || state.Mode == ExtenderInstallMode.ProxyVersionDll))
            File.Delete(proxyPath);

        string hostDir = GetManagedHostDir(localExileRoot);
        if (Directory.Exists(hostDir))
            Directory.Delete(hostDir, recursive: true);

        string stateFile = GetStateFilePath(localExileRoot);
        if (File.Exists(stateFile))
            File.Delete(stateFile);

        if (removeInstalledClientMods)
        {
            string clientModsRoot = WaypointModInstaller.GetClientModsRoot(localExileRoot);
            if (Directory.Exists(clientModsRoot))
                Directory.Delete(clientModsRoot, recursive: true);
        }

        var finalStatus = await GetStatusAsync(localExileRoot, cancellationToken);
        return new(true, "Removed NativeMods extender cleanly.", finalStatus);
    }

    public static ExtenderStateRecord? ReadState(string localExileRoot)
    {
        string path = GetStateFilePath(localExileRoot);
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<ExtenderStateRecord>(File.ReadAllText(path), DualModManifest.SerializerOptions);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;
        }
    }

    private static void WriteState(string localExileRoot, ExtenderStateRecord state)
    {
        string path = GetStateFilePath(localExileRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(state, DualModManifest.SerializerOptions), new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }

    private static void CopyFileAtomically(string sourcePath, string destPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
        string tmp = destPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.Copy(sourcePath, tmp, overwrite: true);
        File.Move(tmp, destPath, overwrite: true);
    }

    private static int CopyDirectoryFiles(string sourceDir, string destDir)
    {
        int count = 0;
        Directory.CreateDirectory(destDir);
        foreach (string file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(sourceDir, file);
            string dest = Path.Combine(destDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
            count++;
        }
        return count;
    }
}
