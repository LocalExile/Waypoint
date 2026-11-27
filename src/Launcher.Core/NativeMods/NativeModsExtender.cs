using System.Text;
using System.Text.Json;
using LocalExile.Client.Patcher;
using LocalExile.Client.Waypoint;

namespace Launcher.Core.NativeMods;

/// <summary>Per-checkout NativeMods opt-in preference stored in Waypoint's <c>settings.json</c>. Always OFF by default.</summary>
public sealed record NativeModsPreference(bool Enabled = false, string Mode = nameof(ExtenderInstallMode.ProxyVersionDll));

/// <summary>Combined health &amp; integrity report for the NativeMods script extender in a LocalExile folder.</summary>
public sealed record NativeModsHealth(
    bool OptedIn,
    bool IsInstalled,
    bool IsHealthy,
    bool IsActiveOnDisk,
    string? ExtenderVersion,
    ExtenderInstallMode Mode,
    string? ClientBuild,
    bool BuildSupported,
    bool ClientIsLink,
    bool IsReleaseInstall,
    bool StockClientPresent,
    bool PatchedClientPresent,
    bool NativeShimPresent,
    bool ProxyDllPresent,
    bool ProxyDllParked,
    bool ManagedHostPresent,
    bool RuntimeIntegrityOk,
    bool PeImportVerified,
    bool MergedCachePresent,
    bool MergedCacheStale,
    string StockDatSourceKind,
    int InstalledClientModCount,
    IReadOnlyList<string> Diagnostics);

/// <summary>Outcome of a NativeMods extender install, enable/disable, or uninstall operation.</summary>
public sealed record NativeModsOperationResult(bool Succeeded, string Message, NativeModsHealth Health);

/// <summary>
/// Waypoint-facing lifecycle manager for the NativeMods client extender.
/// Enforces opt-in default OFF, linked-client safety, release-install <c>.nmimp</c> protection,
/// exact stock <c>Game.exe</c> preservation, and clean disable/re-enable without uninstalling runtime files.
/// </summary>
public static class NativeModsExtender
{
    public const string ProxyDisabledFileName = "version.dll.disabled";

    public static readonly IReadOnlyList<string> SupportedClientBuilds = ["3.29.3d", "3.29.3c"];

    public static bool IsBuildSupported(string? build) =>
        !string.IsNullOrWhiteSpace(build) && SupportedClientBuilds.Contains(build, StringComparer.OrdinalIgnoreCase);

    public static ExtenderInstallMode ParseMode(string? mode) =>
        string.Equals(mode, nameof(ExtenderInstallMode.ImportTable), StringComparison.OrdinalIgnoreCase)
            ? ExtenderInstallMode.ImportTable
            : ExtenderInstallMode.ProxyVersionDll;

    public static string GetParkedProxyPath(string root) =>
        Path.Combine(WaypointExtenderManager.GetClientDir(root), ProxyDisabledFileName);

    public static string GetShimLogPath(string root) =>
        Path.Combine(root, "logs", "nativemods-shim.log");

    /// <summary>Inspects the LocalExile folder without mutating any files.</summary>
    public static async Task<NativeModsHealth> GetHealthAsync(
        string root,
        bool optedIn,
        bool isReleaseInstall,
        ExtenderInstallMode? preferredMode = null,
        IPePatcherBridge? patcher = null,
        CancellationToken stop = default)
    {
        var diagnostics = new List<string>();
        string clientDir = WaypointExtenderManager.GetClientDir(root);
        bool isLink = ClientVersions.Layout(root) == ClientLayout.Link;
        string? detectedBuild = Directory.Exists(clientDir) ? ClientLocator.BuildOf(clientDir) : null;
        bool buildSupported = IsBuildSupported(detectedBuild);

        patcher ??= CreateDefaultPatcher(root);
        var manager = new WaypointExtenderManager(patcher);
        var status = await manager.GetStatusAsync(root, stop);
        var state = WaypointExtenderManager.ReadState(root);

        ExtenderInstallMode effectiveMode = preferredMode ?? state?.Mode ?? status.Mode ?? ExtenderInstallMode.ProxyVersionDll;
        bool proxyParked = File.Exists(GetParkedProxyPath(root));

        // Verify managed runtime core files and address library for the detected build.
        string hostDir = WaypointExtenderManager.GetManagedHostDir(root);
        bool runtimeOk = status.ManagedHostPresent;
        if (status.IsInstalled)
        {
            foreach (string required in NativeModsReleases.RequiredHostFiles)
            {
                if (!File.Exists(Path.Combine(hostDir, required)))
                {
                    runtimeOk = false;
                    diagnostics.Add(L.Text("Missing managed runtime file: Client/nativemods-host/{0}.", required));
                }
            }

            string addrDir = Path.Combine(hostDir, WaypointExtenderManager.AddressLibraryFolderName);
            string expectedAddr = detectedBuild is not null ? $"{detectedBuild}.json" : "3.29.3d.json";
            if (!File.Exists(Path.Combine(addrDir, expectedAddr)) && !File.Exists(Path.Combine(addrDir, "3.29.3d.json")))
            {
                runtimeOk = false;
                diagnostics.Add(L.Text("Missing address library file in Client/nativemods-host/addresses/."));
            }
        }

        if (isLink)
            diagnostics.Add(L.Text("This folder's Client is a link to another folder; NativeMods requires a local copy of Client."));

        if (detectedBuild is not null && !buildSupported)
            diagnostics.Add(L.Text("Client build {0} is not supported by NativeMods (supported: 3.29.3c, 3.29.3d).", detectedBuild));

        if (isReleaseInstall && effectiveMode == ExtenderInstallMode.ImportTable)
            diagnostics.Add(L.Text("Release installs check Game.patched.exe file size on launch and overwrite .nmimp import patches. Use Proxy (version.dll) mode for release installs."));

        // Include non-duplicate diagnostics from WaypointExtenderManager, skipping proxy-missing when intentionally opted out & parked.
        foreach (string diag in status.Diagnostics)
        {
            if (!optedIn && proxyParked && diag.Contains("version.dll is missing", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!optedIn && diag.Contains("import check failed", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!diagnostics.Contains(diag, StringComparer.Ordinal))
                diagnostics.Add(diag);
        }

        bool activeOnDisk = status.ProxyDllPresent || status.PeImportVerified;
        if (optedIn && status.IsInstalled)
        {
            if (effectiveMode == ExtenderInstallMode.ProxyVersionDll && !status.ProxyDllPresent)
                diagnostics.Add(L.Text("NativeMods is enabled in Proxy mode, but Client/version.dll is not active."));
            if (effectiveMode == ExtenderInstallMode.ImportTable && !status.PeImportVerified)
                diagnostics.Add(L.Text("NativeMods is enabled in Import patch mode, but Game.patched.exe is not patched with .nmimp."));
        }

        // Check .merged-cache status for enabled datPatches mods.
        string stockKind = DetectStockDatSourceKind(clientDir);
        string mergedCacheDir = WaypointModInstaller.GetMergedCacheDirectory(root);
        bool mergedCachePresent = File.Exists(Path.Combine(mergedCacheDir, "content-row-map.json"));
        bool hasEnabledDatPatches = HasEnabledDatPatchMods(root);
        bool mergedCacheStale = hasEnabledDatPatches && !mergedCachePresent;
        if (mergedCacheStale && stockKind == "StandaloneGgpk")
            diagnostics.Add(L.Text("Installed mods declare datPatches, but automatic .merged-cache building requires Steam Bundles2/ or extracted Client/Data/*.datc64 tables."));

        bool healthy = status.IsInstalled
            && !isLink
            && (detectedBuild is null || buildSupported)
            && status.NativeShimPresent
            && runtimeOk
            && (!isReleaseInstall || effectiveMode != ExtenderInstallMode.ImportTable)
            && (!optedIn || (effectiveMode == ExtenderInstallMode.ProxyVersionDll ? status.ProxyDllPresent : status.PeImportVerified))
            && diagnostics.Count == 0;

        return new NativeModsHealth(
            OptedIn: optedIn,
            IsInstalled: status.IsInstalled,
            IsHealthy: healthy,
            IsActiveOnDisk: activeOnDisk,
            ExtenderVersion: state?.ExtenderVersion ?? status.ExtenderVersion,
            Mode: effectiveMode,
            ClientBuild: detectedBuild ?? status.DetectedBuild,
            BuildSupported: buildSupported,
            ClientIsLink: isLink,
            IsReleaseInstall: isReleaseInstall,
            StockClientPresent: status.StockClientPresent,
            PatchedClientPresent: status.PatchedClientPresent,
            NativeShimPresent: status.NativeShimPresent,
            ProxyDllPresent: status.ProxyDllPresent,
            ProxyDllParked: proxyParked,
            ManagedHostPresent: status.ManagedHostPresent,
            RuntimeIntegrityOk: runtimeOk,
            PeImportVerified: status.PeImportVerified,
            MergedCachePresent: mergedCachePresent,
            MergedCacheStale: mergedCacheStale,
            StockDatSourceKind: stockKind,
            InstalledClientModCount: status.InstalledClientModCount,
            Diagnostics: diagnostics);
    }

    /// <summary>Installs or updates the NativeMods extender from a verified <see cref="StagedExtenderBundle"/>.</summary>
    public static async Task<NativeModsOperationResult> InstallAsync(
        string root,
        StagedExtenderBundle bundle,
        ExtenderInstallMode mode,
        bool optedIn,
        bool isReleaseInstall,
        IPePatcherBridge? patcher = null,
        CancellationToken stop = default)
    {
        if (ClientVersions.Layout(root) == ClientLayout.Link)
        {
            var health = await GetHealthAsync(root, optedIn, isReleaseInstall, mode, patcher, stop);
            return new(false, L.Text("Cannot install NativeMods into a linked Client folder. Make a copy of the game client first."), health);
        }

        if (isReleaseInstall && mode == ExtenderInstallMode.ImportTable)
        {
            var health = await GetHealthAsync(root, optedIn, isReleaseInstall, mode, patcher, stop);
            return new(false, L.Text("Import patch (.nmimp) mode is not supported on release installs because LocalExile.exe checks Game.patched.exe file size and resets it. Use Proxy (version.dll) mode instead."), health);
        }

        if (mode == ExtenderInstallMode.ProxyVersionDll && string.IsNullOrWhiteSpace(bundle.ProxyVersionDllPath))
        {
            var health = await GetHealthAsync(root, optedIn, isReleaseInstall, mode, patcher, stop);
            return new(false, L.Text("The selected NativeMods bundle does not include version.dll required for Proxy mode."), health);
        }

        string clientDir = WaypointExtenderManager.GetClientDir(root);
        if (Directory.Exists(clientDir))
        {
            string? build = ClientLocator.BuildOf(clientDir);
            if (build is not null && !IsBuildSupported(build))
            {
                var health = await GetHealthAsync(root, optedIn, isReleaseInstall, mode, patcher, stop);
                return new(false, L.Text("Client build {0} is not supported by NativeMods (supported: 3.29.3c, 3.29.3d).", build), health);
            }
        }

        CheckoutIgnores.Ensure(root);
        patcher ??= CreateDefaultPatcher(root);

        // If switching from ImportTable to ProxyVersionDll, unpatch any existing .nmimp first.
        string patchedExe = WaypointExtenderManager.GetPatchedExePath(root);
        if (mode == ExtenderInstallMode.ProxyVersionDll && File.Exists(patchedExe))
        {
            var verify = await patcher.VerifyAsync(patchedExe, stop);
            if (verify.Succeeded && verify.IsPatched)
            {
                var unpatch = await patcher.UnpatchAsync(patchedExe, stop);
                if (!unpatch.Succeeded)
                {
                    var health = await GetHealthAsync(root, optedIn, isReleaseInstall, mode, patcher, stop);
                    return new(false, L.Text("Failed to unpatch Game.patched.exe before switching to Proxy mode: {0}", unpatch.Message), health);
                }
            }
        }

        var manager = new WaypointExtenderManager(patcher);
        var installOptions = new ExtenderInstallOptions(
            ExtenderVersion: bundle.Version,
            NativeShimDllSourcePath: bundle.NativeShimPath,
            ManagedHostBundleDir: bundle.ManagedHostDir,
            Mode: mode,
            AddressLibraryDir: bundle.AddressLibraryDir,
            ProxyVersionDllSourcePath: bundle.ProxyVersionDllPath);

        // Clean any previously parked proxy DLL before copying the new one.
        string parkedProxy = GetParkedProxyPath(root);
        if (File.Exists(parkedProxy)) File.Delete(parkedProxy);

        var result = await manager.InstallAsync(root, installOptions, stop);
        if (!result.Succeeded)
        {
            var health = await GetHealthAsync(root, optedIn, isReleaseInstall, mode, patcher, stop);
            return new(false, result.Message, health);
        }

        // Preserve a backup copy of version.dll inside nativemods-host/ so switching between modes works offline.
        if (!string.IsNullOrWhiteSpace(bundle.ProxyVersionDllPath) && File.Exists(bundle.ProxyVersionDllPath))
        {
            string hostProxyBackup = Path.Combine(WaypointExtenderManager.GetManagedHostDir(root), WaypointExtenderManager.ProxyDllFileName);
            if (!File.Exists(hostProxyBackup))
                File.Copy(bundle.ProxyVersionDllPath, hostProxyBackup, overwrite: true);
        }

        // If user installed the extender while Opt-In is OFF, immediately park the entry point so launches stay 100% stock.
        if (!optedIn)
        {
            var deactivate = await SetEnabledAsync(root, enabled: false, mode, isReleaseInstall, patcher, stop);
            if (!deactivate.Succeeded) return deactivate;
        }

        var finalHealth = await GetHealthAsync(root, optedIn, isReleaseInstall, mode, patcher, stop);
        return new(true, L.Text("Installed NativeMods extender {0} ({1}).", bundle.Version, ModeLabel(mode)), finalHealth);
    }

    /// <summary>
    /// Enables or disables an already-installed extender without deleting <c>Client/nativemods-host/</c> or <c>Mods/Client/</c>.
    /// Disabling parks <c>Client/version.dll</c> to <c>version.dll.disabled</c> and unpatches <c>.nmimp</c> so stock launches never load the shim.
    /// </summary>
    public static async Task<NativeModsOperationResult> SetEnabledAsync(
        string root,
        bool enabled,
        ExtenderInstallMode mode,
        bool isReleaseInstall,
        IPePatcherBridge? patcher = null,
        CancellationToken stop = default)
    {
        if (ClientVersions.Layout(root) == ClientLayout.Link)
        {
            var health = await GetHealthAsync(root, enabled, isReleaseInstall, mode, patcher, stop);
            return new(false, L.Text("Cannot modify NativeMods in a linked Client folder."), health);
        }

        patcher ??= CreateDefaultPatcher(root);
        string proxyPath = WaypointExtenderManager.GetProxyDllPath(root);
        string parkedPath = GetParkedProxyPath(root);
        string hostProxyBackup = Path.Combine(WaypointExtenderManager.GetManagedHostDir(root), WaypointExtenderManager.ProxyDllFileName);
        string patchedExe = WaypointExtenderManager.GetPatchedExePath(root);

        if (!enabled)
        {
            // Deactivate proxy DLL by parking it out of Windows/Wine DLL search order.
            if (File.Exists(proxyPath))
                File.Move(proxyPath, parkedPath, overwrite: true);

            // Deactivate .nmimp import-table patch if present (verifies exact byte restore).
            if (File.Exists(patchedExe))
            {
                var verify = await patcher.VerifyAsync(patchedExe, stop);
                if (verify.Succeeded && verify.IsPatched)
                {
                    var unpatch = await patcher.UnpatchAsync(patchedExe, stop);
                    if (!unpatch.Succeeded)
                    {
                        var failedHealth = await GetHealthAsync(root, false, isReleaseInstall, mode, patcher, stop);
                        return new(false, L.Text("Could not restore Game.patched.exe while disabling NativeMods: {0}", unpatch.Message), failedHealth);
                    }
                }
            }

            var offHealth = await GetHealthAsync(root, false, isReleaseInstall, mode, patcher, stop);
            return new(true, L.Text("NativeMods disabled. The game will launch without the extender."), offHealth);
        }

        // Enabling (opt-in ON)
        if (isReleaseInstall && mode == ExtenderInstallMode.ImportTable)
        {
            var health = await GetHealthAsync(root, false, isReleaseInstall, mode, patcher, stop);
            return new(false, L.Text("Import patch (.nmimp) mode is not supported on release installs because LocalExile.exe checks Game.patched.exe file size and resets it. Switch to Proxy (version.dll) mode."), health);
        }

        string clientDir = WaypointExtenderManager.GetClientDir(root);
        string? build = Directory.Exists(clientDir) ? ClientLocator.BuildOf(clientDir) : null;
        if (build is not null && !IsBuildSupported(build))
        {
            var health = await GetHealthAsync(root, false, isReleaseInstall, mode, patcher, stop);
            return new(false, L.Text("Client build {0} is not supported by NativeMods (supported: 3.29.3c, 3.29.3d).", build), health);
        }

        if (!File.Exists(WaypointExtenderManager.GetNativeShimPath(root)) || !Directory.Exists(WaypointExtenderManager.GetManagedHostDir(root)))
        {
            var health = await GetHealthAsync(root, false, isReleaseInstall, mode, patcher, stop);
            return new(false, L.Text("Install the NativeMods extender first before enabling it."), health);
        }

        if (mode == ExtenderInstallMode.ProxyVersionDll)
        {
            // Ensure any previous .nmimp patch is removed cleanly when switching to Proxy mode.
            if (File.Exists(patchedExe))
            {
                var verify = await patcher.VerifyAsync(patchedExe, stop);
                if (verify.Succeeded && verify.IsPatched)
                {
                    var unpatch = await patcher.UnpatchAsync(patchedExe, stop);
                    if (!unpatch.Succeeded)
                    {
                        var failedHealth = await GetHealthAsync(root, false, isReleaseInstall, mode, patcher, stop);
                        return new(false, L.Text("Could not unpatch Game.patched.exe before activating Proxy mode: {0}", unpatch.Message), failedHealth);
                    }
                }
            }

            if (!File.Exists(proxyPath))
            {
                if (File.Exists(parkedPath))
                    File.Move(parkedPath, proxyPath, overwrite: true);
                else if (File.Exists(hostProxyBackup))
                    File.Copy(hostProxyBackup, proxyPath, overwrite: true);
                else
                {
                    var failedHealth = await GetHealthAsync(root, false, isReleaseInstall, mode, patcher, stop);
                    return new(false, L.Text("Client/version.dll is missing. Reinstall the NativeMods extender."), failedHealth);
                }
            }
        }
        else
        {
            // ImportTable mode: park/remove proxy DLL and apply .nmimp patch.
            if (File.Exists(proxyPath))
                File.Move(proxyPath, parkedPath, overwrite: true);

            if (!File.Exists(patchedExe))
            {
                var failedHealth = await GetHealthAsync(root, false, isReleaseInstall, mode, patcher, stop);
                return new(false, L.Text("Client/Game.patched.exe is missing. Prepare the game client first."), failedHealth);
            }

            var check = await patcher.CheckAsync(patchedExe, stop);
            if (!check.Succeeded)
            {
                var failedHealth = await GetHealthAsync(root, false, isReleaseInstall, mode, patcher, stop);
                return new(false, L.Text("Game.patched.exe check failed: {0}", check.Message), failedHealth);
            }

            var patch = await patcher.PatchAsync(patchedExe, WaypointExtenderManager.NativeShimFileName, stop);
            if (!patch.Succeeded)
            {
                var failedHealth = await GetHealthAsync(root, false, isReleaseInstall, mode, patcher, stop);
                return new(false, L.Text("Failed to apply .nmimp patch to Game.patched.exe: {0}", patch.Message), failedHealth);
            }
        }

        // Update persisted extender mode in .extender-state.json so status stays consistent across restarts.
        if (WaypointExtenderManager.ReadState(root) is { } existing)
        {
            string? proxySha = File.Exists(proxyPath) ? DualModPackageValidator.ComputeFileSha256(proxyPath) : existing.ProxyDllSha256;
            UpdateStateRecord(root, existing with { Mode = mode, ProxyDllSha256 = proxySha });
        }

        var onHealth = await GetHealthAsync(root, true, isReleaseInstall, mode, patcher, stop);
        return new(true, L.Text("NativeMods enabled ({0}).", ModeLabel(mode)), onHealth);
    }

    /// <summary>Cleanly removes the NativeMods extender, restoring <c>Game.patched.exe</c> and never modifying <c>Game.exe</c>.</summary>
    public static async Task<NativeModsOperationResult> UninstallAsync(
        string root,
        bool removeClientMods,
        bool isReleaseInstall,
        IPePatcherBridge? patcher = null,
        CancellationToken stop = default)
    {
        patcher ??= CreateDefaultPatcher(root);
        var manager = new WaypointExtenderManager(patcher);

        // Move installed client mod folders to Mods/.replaced/ instead of hard-deleting when requested.
        if (removeClientMods)
        {
            string clientModsRoot = WaypointModInstaller.GetClientModsRoot(root);
            if (Directory.Exists(clientModsRoot))
            {
                string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8];
                foreach (string dir in Directory.EnumerateDirectories(clientModsRoot))
                {
                    string name = Path.GetFileName(dir);
                    if (name.StartsWith('.')) continue;
                    string aside = Path.Combine(root, "Mods", ".replaced", stamp, "Client", name);
                    Directory.CreateDirectory(Path.GetDirectoryName(aside)!);
                    Directory.Move(dir, aside);
                }

                string mergedCache = WaypointModInstaller.GetMergedCacheDirectory(root);
                if (Directory.Exists(mergedCache)) Directory.Delete(mergedCache, recursive: true);
                string ledger = WaypointModInstaller.GetLedgerPath(root);
                if (File.Exists(ledger)) File.Delete(ledger);
                string clientState = NativeModsClientStore.GetClientModStatePath(root);
                if (File.Exists(clientState)) File.Delete(clientState);
            }
        }

        var result = await manager.RemoveAsync(root, removeInstalledClientMods: false, stop);
        string parked = GetParkedProxyPath(root);
        if (File.Exists(parked)) File.Delete(parked);

        var finalHealth = await GetHealthAsync(root, optedIn: false, isReleaseInstall, null, patcher, stop);
        return new(result.Succeeded, result.Succeeded ? L.Text("NativeMods extender uninstalled cleanly.") : result.Message, finalHealth);
    }

    /// <summary>
    /// Builds the process environment overrides for launching the game client via <c>live.ps1</c> or <c>LocalExile.exe</c>.
    /// When <paramref name="optedIn"/> is true, explicitly injects <c>NATIVEMODS_*</c> paths (converted to <c>Z:\...</c> on Linux)
    /// and appends <c>version=n,b</c> to <c>WINEDLLOVERRIDES</c> on Linux in <see cref="ExtenderInstallMode.ProxyVersionDll"/> mode.
    /// When <paramref name="optedIn"/> is false, explicitly strips all <c>NATIVEMODS_*</c> variables and any <c>version=n,b</c> override.
    /// </summary>
    public static Dictionary<string, string?> BuildLaunchEnvironment(
        string root,
        bool optedIn,
        ExtenderInstallMode mode,
        bool isLinux,
        string? existingWineDllOverrides = null)
    {
        var env = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (!optedIn)
        {
            env["NATIVEMODS_HOST_DIR"] = null;
            env["NATIVEMODS_ADDRESSES_DIR"] = null;
            env["NATIVEMODS_MODS_DIR"] = null;
            env["NATIVEMODS_LOG_PATH"] = null;
            env["NATIVEMODS_MERGED_CACHE_DIR"] = null;
            if (isLinux)
            {
                string? stripped = StripWineDllOverride(existingWineDllOverrides, "version=n,b");
                if (stripped != existingWineDllOverrides)
                    env["WINEDLLOVERRIDES"] = stripped;
            }
            return env;
        }

        string fullRoot = isLinux && root.StartsWith('/') ? root : Path.GetFullPath(root);
        string FormatPath(string path) => isLinux ? ToWinePath(path) : path;

        string hostDir = WaypointExtenderManager.GetManagedHostDir(fullRoot);
        string addrDir = Path.Combine(hostDir, WaypointExtenderManager.AddressLibraryFolderName);
        string modsDir = WaypointModInstaller.GetClientModsRoot(fullRoot);
        string logPath = GetShimLogPath(fullRoot);

        env["NATIVEMODS_HOST_DIR"] = FormatPath(hostDir);
        env["NATIVEMODS_ADDRESSES_DIR"] = FormatPath(addrDir);
        env["NATIVEMODS_MODS_DIR"] = FormatPath(modsDir);
        env["NATIVEMODS_LOG_PATH"] = FormatPath(logPath);

        if (isLinux)
        {
            if (mode == ExtenderInstallMode.ProxyVersionDll)
                env["WINEDLLOVERRIDES"] = AppendWineDllOverride(existingWineDllOverrides, "version=n,b");
            else
            {
                string? stripped = StripWineDllOverride(existingWineDllOverrides, "version=n,b");
                if (stripped != existingWineDllOverrides)
                    env["WINEDLLOVERRIDES"] = stripped;
            }
        }

        return env;
    }

    public static string ToWinePath(string linuxPath)
    {
        // On Windows Path.GetFullPath treats a leading '/' as drive-relative and prepends the current drive letter.
        if (!OperatingSystem.IsWindows() || !linuxPath.StartsWith('/'))
            return "Z:" + Path.GetFullPath(linuxPath).Replace('/', '\\');
        var segments = new List<string>();
        foreach (string part in linuxPath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            if (part == "..") { if (segments.Count > 0) segments.RemoveAt(segments.Count - 1); }
            else segments.Add(part);
        }
        return @"Z:\" + string.Join('\\', segments);
    }

    public static string AppendWineDllOverride(string? existing, string token)
    {
        var parts = (existing ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        if (!parts.Any(p => p.Equals(token, StringComparison.OrdinalIgnoreCase)))
            parts.Add(token);
        return string.Join(';', parts);
    }

    public static string? StripWineDllOverride(string? existing, string token)
    {
        if (string.IsNullOrWhiteSpace(existing)) return existing;
        var kept = existing
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(p => !p.Equals(token, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return kept.Length == 0 ? null : string.Join(';', kept);
    }

    public static string ModeLabel(ExtenderInstallMode mode) => mode == ExtenderInstallMode.ImportTable
        ? L.Text("Import patch (.nmimp)")
        : L.Text("Proxy (version.dll)");

    internal static IPePatcherBridge CreateDefaultPatcher(string root)
    {
        string addrDir = Path.Combine(WaypointExtenderManager.GetManagedHostDir(root), WaypointExtenderManager.AddressLibraryFolderName);
        AddressLibraryCatalog? catalog = Directory.Exists(addrDir) ? AddressLibraryCatalog.LoadFromDirectory(addrDir) : null;
        return new ClientPatcherBridge(catalog);
    }

    internal static string DetectStockDatSourceKind(string clientDir)
    {
        if (!Directory.Exists(clientDir)) return "None";
        if (File.Exists(Path.Combine(clientDir, "Bundles2", "_.index.bin"))) return "Bundles2";
        if (Directory.Exists(Path.Combine(clientDir, "Data"))) return "Data";
        if (File.Exists(Path.Combine(clientDir, "Content.ggpk"))) return "StandaloneGgpk";
        return "None";
    }

    private static bool HasEnabledDatPatchMods(string root)
    {
        var ledger = WaypointModInstaller.ReadLedger(root);
        if (ledger.Mods.Count == 0) return false;
        var choices = ModCatalog.ReadChoices(Path.Combine(root, "Mods", "Binaries"));
        return ledger.Mods.Any(m =>
            choices.GetValueOrDefault(m.Id, m.Manifest.Enabled) &&
            ((m.DatPatches is { Count: > 0 }) || (m.Manifest.NativeClient?.DatPatches.Count > 0)));
    }

    private static void UpdateStateRecord(string root, ExtenderStateRecord state)
    {
        string path = WaypointExtenderManager.GetStateFilePath(root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(state, DualModManifest.SerializerOptions), new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }
}
