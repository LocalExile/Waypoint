using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Launcher.Core;
using Launcher.Core.NativeMods;
using LocalExile.Client.AssetPipeline.Dat64;
using LocalExile.Client.Patcher;
using LocalExile.Client.Waypoint;

static class NativeModsChecks
{
    public static async Task RunAsync(string fixture, Action<bool, string> check)
    {
        // 1. CheckoutIgnores includes /Mods/Client/
        check(CheckoutIgnores.Patterns.Contains("/Mods/Client/"),
            "CheckoutIgnores excludes /Mods/Client/ so dual-side client mods never dirty a git checkout");

        // 2. GitHub release parser & local zip staging / zip-slip rejection
        const string releaseJson = """
        {
          "tag_name": "v1.2.0",
          "html_url": "https://github.com/mrnamwen/LocalExile-NativeMods/releases/tag/v1.2.0",
          "body": "Release notes",
          "assets": [
            {
              "name": "NativeMods-win-x64-v1.2.0.zip",
              "browser_download_url": "https://github.com/mrnamwen/LocalExile-NativeMods/releases/download/v1.2.0/NativeMods-win-x64-v1.2.0.zip",
              "size": 123456,
              "digest": "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
            },
            {
              "name": "NativeMods-win-x64-v1.2.0.zip.sha256",
              "browser_download_url": "https://github.com/mrnamwen/LocalExile-NativeMods/releases/download/v1.2.0/NativeMods-win-x64-v1.2.0.zip.sha256"
            }
          ]
        }
        """;
        var parsedRelease = NativeModsReleases.ParseRelease(releaseJson);
        check(parsedRelease is { Version: "1.2.0", ZipName: "NativeMods-win-x64-v1.2.0.zip" }
            && parsedRelease.Sha256 == "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
            && parsedRelease.HashUrl is not null,
            "NativeMods GitHub release JSON parses win-x64 zip, digest, and .sha256 sidecar");

        string nmRoot = Path.Combine(fixture, "nativemods-checks");
        Directory.CreateDirectory(nmRoot);

        // Zip-slip rejection
        string badZip = Path.Combine(nmRoot, "evil.zip");
        using (var archive = ZipFile.Open(badZip, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("../escape.dll");
            using var w = new StreamWriter(entry.Open());
            w.Write("bad");
        }
        bool zipSlipRejected = false;
        try { NativeModsReleases.StageLocalZip(badZip); }
        catch (InvalidDataException) { zipSlipRejected = true; }
        check(zipSlipRejected, "NativeMods zip staging rejects zip-slip entries");

        // Build a valid local dist bundle + zip
        string distDir = Path.Combine(nmRoot, "dist-win-x64");
        Directory.CreateDirectory(Path.Combine(distDir, "addresses"));
        File.WriteAllText(Path.Combine(distDir, "LocalExile.ClientHost.dll"), "shim-fixture");
        File.WriteAllText(Path.Combine(distDir, "version.dll"), "proxy-fixture");
        File.WriteAllText(Path.Combine(distDir, "hostfxr.dll"), "hostfxr-fixture");
        File.WriteAllText(Path.Combine(distDir, "coreclr.dll"), "coreclr-fixture");
        File.WriteAllText(Path.Combine(distDir, "LocalExile.Client.Host.dll"), "managed-host-fixture");
        File.WriteAllText(Path.Combine(distDir, "LocalExile.Client.Host.runtimeconfig.json"), "{}");
        File.WriteAllText(Path.Combine(distDir, "addresses", "3.29.3d.json"), "{}");
        File.WriteAllText(Path.Combine(distDir, "version.txt"), "1.1.0");

        string goodZip = Path.Combine(nmRoot, "NativeMods-win-x64-1.1.0.zip");
        ZipFile.CreateFromDirectory(distDir, goodZip);
        var stagedFromZip = NativeModsReleases.StageLocalZip(goodZip);
        check(stagedFromZip.Version == "1.1.0" && File.Exists(stagedFromZip.NativeShimPath) && stagedFromZip.ProxyVersionDllPath is not null,
            "NativeMods local zip stages and validates required native/runtime files");
        NativeModsReleases.Discard(stagedFromZip);

        // Missing addresses/3.29.3d.json must be rejected
        string missingAddrDir = Path.Combine(nmRoot, "dist-no-addr");
        Directory.CreateDirectory(missingAddrDir);
        File.WriteAllText(Path.Combine(missingAddrDir, "LocalExile.ClientHost.dll"), "shim");
        File.WriteAllText(Path.Combine(missingAddrDir, "hostfxr.dll"), "hostfxr");
        File.WriteAllText(Path.Combine(missingAddrDir, "coreclr.dll"), "coreclr");
        File.WriteAllText(Path.Combine(missingAddrDir, "LocalExile.Client.Host.dll"), "host");
        File.WriteAllText(Path.Combine(missingAddrDir, "LocalExile.Client.Host.runtimeconfig.json"), "{}");
        bool missingAddrRejected = false;
        try { NativeModsReleases.StageLocalDirectory(missingAddrDir); }
        catch (InvalidDataException) { missingAddrRejected = true; }
        check(missingAddrRejected, "NativeMods bundle staging rejects bundles missing addresses/3.29.3d.json");

        // 3. Launch environment builder (Windows & Linux/Proton, opt-in & opt-out)
        var optOutLinux = NativeModsExtender.BuildLaunchEnvironment("/games/LocalExile", optedIn: false, ExtenderInstallMode.ProxyVersionDll, isLinux: true, "d3d11=n;version=n,b");
        check(optOutLinux["NATIVEMODS_HOST_DIR"] is null && optOutLinux["NATIVEMODS_MODS_DIR"] is null && optOutLinux["WINEDLLOVERRIDES"] == "d3d11=n",
            "Opted-out Linux launch clears NATIVEMODS_* and strips only version=n,b from WINEDLLOVERRIDES");

        var optInLinuxProxy = NativeModsExtender.BuildLaunchEnvironment("/games/LocalExile", optedIn: true, ExtenderInstallMode.ProxyVersionDll, isLinux: true, "d3d11=n");
        check(optInLinuxProxy["NATIVEMODS_HOST_DIR"] == @"Z:\games\LocalExile\Client\nativemods-host"
            && optInLinuxProxy["NATIVEMODS_ADDRESSES_DIR"] == @"Z:\games\LocalExile\Client\nativemods-host\addresses"
            && optInLinuxProxy["NATIVEMODS_MODS_DIR"] == @"Z:\games\LocalExile\Mods\Client"
            && optInLinuxProxy["NATIVEMODS_LOG_PATH"] == @"Z:\games\LocalExile\logs\nativemods-shim.log"
            && optInLinuxProxy["WINEDLLOVERRIDES"] == "d3d11=n;version=n,b",
            "Opted-in Linux proxy launch sets Z:\\ Wine paths and appends version=n,b without clobbering existing WINEDLLOVERRIDES");

        var optInLinuxProxyDup = NativeModsExtender.BuildLaunchEnvironment("/games/LocalExile", optedIn: true, ExtenderInstallMode.ProxyVersionDll, isLinux: true, "version=n,b");
        check(optInLinuxProxyDup["WINEDLLOVERRIDES"] == "version=n,b",
            "WINEDLLOVERRIDES does not duplicate version=n,b when already present");

        var optInWin = NativeModsExtender.BuildLaunchEnvironment(nmRoot, optedIn: true, ExtenderInstallMode.ProxyVersionDll, isLinux: false);
        check(!optInWin.ContainsKey("WINEDLLOVERRIDES") && optInWin["NATIVEMODS_MODS_DIR"] == Path.Combine(nmRoot, "Mods", "Client"),
            "Opted-in Windows launch sets native paths without WINEDLLOVERRIDES");

        // 4. Extender lifecycle: Proxy mode + ImportTable (.nmimp) mode + Release install guard + Stock Game.exe invariant
        string checkout = Path.Combine(nmRoot, "checkout");
        string clientDir = Path.Combine(checkout, "Client");
        Directory.CreateDirectory(clientDir);
        byte[] syntheticPe = BuildSyntheticPe32Plus();
        string stockExe = Path.Combine(clientDir, "Game.exe");
        string patchedExe = Path.Combine(clientDir, "Game.patched.exe");
        File.WriteAllBytes(stockExe, syntheticPe);
        File.WriteAllBytes(patchedExe, syntheticPe);
        string stockShaBefore = Convert.ToHexStringLower(SHA256.HashData(syntheticPe));

        var prevOverride = ClientLocator.BuildOverride;
        ClientLocator.BuildOverride = _ => "3.29.3d";
        try
        {
            var syntheticPatcher = new ClientPatcherBridge(allowSynthetic: true);
            var stagedDir = NativeModsReleases.StageLocalDirectory(distDir);

            // Release install refuses ImportTable (.nmimp) mode
            var releaseBlocked = await NativeModsExtender.InstallAsync(checkout, stagedDir, ExtenderInstallMode.ImportTable, optedIn: true, isReleaseInstall: true, syntheticPatcher);
            check(!releaseBlocked.Succeeded && releaseBlocked.Message.Contains("Proxy", StringComparison.OrdinalIgnoreCase),
                "Release installs refuse .nmimp import-patch mode and direct users to Proxy (version.dll)");

            // Install Proxy mode while opted-out -> version.dll is parked as version.dll.disabled
            var proxyInstall = await NativeModsExtender.InstallAsync(checkout, stagedDir, ExtenderInstallMode.ProxyVersionDll, optedIn: false, isReleaseInstall: false, syntheticPatcher);
            check(proxyInstall.Succeeded && !File.Exists(Path.Combine(clientDir, "version.dll")) && File.Exists(Path.Combine(clientDir, NativeModsExtender.ProxyDisabledFileName)),
                "Installing NativeMods while opted-out parks Client/version.dll as version.dll.disabled");

            // Opt-in ON -> restores version.dll cleanly
            var proxyOn = await NativeModsExtender.SetEnabledAsync(checkout, enabled: true, ExtenderInstallMode.ProxyVersionDll, isReleaseInstall: false, syntheticPatcher);
            check(proxyOn.Succeeded && proxyOn.Health.IsHealthy && File.Exists(Path.Combine(clientDir, "version.dll")),
                "Enabling Proxy mode restores Client/version.dll and reports healthy");

            // Switch to ImportTable (.nmimp) on source checkout -> parks version.dll and patches Game.patched.exe
            var importOn = await NativeModsExtender.SetEnabledAsync(checkout, enabled: true, ExtenderInstallMode.ImportTable, isReleaseInstall: false, syntheticPatcher);
            check(importOn.Succeeded && importOn.Health.IsHealthy && importOn.Health.PeImportVerified && !File.Exists(Path.Combine(clientDir, "version.dll")),
                "Enabling ImportTable (.nmimp) on source checkout patches Game.patched.exe and parks version.dll");

            // Disable ImportTable -> exact byte restore of Game.patched.exe
            var importOff = await NativeModsExtender.SetEnabledAsync(checkout, enabled: false, ExtenderInstallMode.ImportTable, isReleaseInstall: false, syntheticPatcher);
            string patchedShaRestored = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(patchedExe)));
            check(importOff.Succeeded && !importOff.Health.PeImportVerified && patchedShaRestored == stockShaBefore,
                "Disabling ImportTable (.nmimp) restores Game.patched.exe byte-for-byte");

            // Uninstall removes extender files cleanly while preserving stock Game.exe
            var removed = await NativeModsExtender.UninstallAsync(checkout, removeClientMods: false, isReleaseInstall: false, syntheticPatcher);
            string stockShaAfter = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(stockExe)));
            check(removed.Succeeded && !removed.Health.IsInstalled && !File.Exists(Path.Combine(clientDir, "LocalExile.ClientHost.dll"))
                && !Directory.Exists(Path.Combine(clientDir, "nativemods-host")) && stockShaAfter == stockShaBefore,
                "Uninstalling NativeMods removes all extender files and leaves stock Game.exe byte-identical");
        }
        finally
        {
            ClientLocator.BuildOverride = prevOverride;
        }

        // 5. Dual-side mod catalog, mods.json index, validation (.ui / closed table / RawAccess), split deploy & .merged-cache
        var catalogListings = ModRepository.ParseIndex("""
        {
          "version": 1,
          "mods": [
            {
              "id": "garden",
              "name": "Garden",
              "path": "mods/garden",
              "version": "1.0.0",
              "nativeClient": { "required": true, "rawAccess": false, "datPatchesCount": 1, "hasClientAssembly": true }
            },
            {
              "id": "plain",
              "name": "Plain",
              "path": "mods/plain",
              "version": "1.0.0"
            }
          ]
        }
        """);
        check(catalogListings.Count == 2 && catalogListings[0].HasNativeClient && catalogListings[0].NativeClient!.Required
            && catalogListings[0].NativeClient!.DatPatchCount == 1 && !catalogListings[1].HasNativeClient,
            "mods.json catalog parser reads nativeClient summaries while keeping server-only listings unchanged");

        // Build a valid dual-side mod package with a .datpatch.json targeting ClientStrings (verified appendable)
        string pkgDir = Path.Combine(nmRoot, "pkg-garden");
        Directory.CreateDirectory(Path.Combine(pkgDir, "client", "Metadata", "UI", "Mods", "garden"));
        File.WriteAllText(Path.Combine(pkgDir, "garden.dll"), "server-dll-fixture");
        File.WriteAllText(Path.Combine(pkgDir, "garden.Client.dll"), "client-dll-fixture");
        File.WriteAllText(Path.Combine(pkgDir, "client", "Metadata", "UI", "Mods", "garden", "Panel.ui"),
            "\n<DEFINITIONS>\nbegin Frame\n    id = \"garden_root\";\nend\n");
        File.WriteAllText(Path.Combine(pkgDir, "client", "garden.datpatch.json"), """
        {
          "schemaVersion": 1,
          "modId": "garden",
          "tables": {
            "ClientStrings": {
              "append": [
                {
                  "key": "GardenTitle",
                  "fields": { "Id": "Mods/Garden/Title", "Text": "Garden" }
                }
              ]
            }
          }
        }
        """);
        File.WriteAllText(Path.Combine(pkgDir, "mod.json"), """
        {
          "id": "garden",
          "name": "Garden",
          "version": "1.0.0",
          "kind": "feature",
          "compatibleClientVersions": ["0.5.2-beta"],
          "assembly": "garden.dll",
          "nativeClient": {
            "required": true,
            "sdkVersionRange": ">=1.0.0 <2.0.0",
            "supportedBuilds": ["3.29.3d", "3.29.3c"],
            "assembly": "garden.Client.dll",
            "payloadDir": "client",
            "overlays": ["Metadata/UI/Mods/garden/Panel.ui"],
            "datPatches": ["garden.datpatch.json"]
          }
        }
        """);

        // Prepare minimal stock Client/Data/ClientStrings.datc64 fixture for .merged-cache rebuild
        string stockDataDir = Path.Combine(clientDir, "Data");
        Directory.CreateDirectory(stockDataDir);
        Dat64SchemaRegistry schemaReg = Dat64SchemaRegistry.LoadDefault329();
        check(Dat64SchemaRegistry.AppendableRegistryTables.Count == 22
            && DualModPackageValidator.AppendableRegistryTables.Count == 22
            && Dat64SchemaRegistry.AppendableRegistryTables.SetEquals(DualModPackageValidator.AppendableRegistryTables)
            && Dat64SchemaRegistry.AppendableRegistryTables.All(t => schemaReg.TryGetTable(t, out _)),
            "All 22 appendable NativeMods 1.2.0 tables match across validator and embedded Dat64SchemaRegistry");
        Dat64TableSchema csSchema = schemaReg.Tables["ClientStrings"];
        File.WriteAllBytes(Path.Combine(stockDataDir, "ClientStrings.datc64"), BuildSingleRowDatc64(csSchema));

        var audit = NativeModsClientStore.AuditPackage(pkgDir, checkout);
        check(audit.Succeeded && audit.HasNativeClient && audit.DatPatches is { Count: 1 },
            "Valid dual-side mod passes .ui linting, namespace, and .datpatch.json validation"
            + (audit.Succeeded ? "" : $" ({audit.FailureReason}; {string.Join("; ", audit.Validation.Errors.Select(e => $"{e.Code}:{e.Message}"))})"));

        // Closed Stats table must be rejected (DAT004)
        var closedReport = DualModPackageValidator.ValidateDatPatchJson("""
        {
          "schemaVersion": 1,
          "modId": "bad",
          "tables": { "Stats": { "append": [{ "key": "BadStat", "fields": { "Id": "bad" } }] } }
        }
        """, "bad");
        check(!closedReport.IsValid && closedReport.Errors.Any(e => e.Code == "DAT004"),
            "Closed 3.29 Stats table append is rejected with DAT004");

        // Split staging excludes client payload from server directory
        string serverOnly = NativeModsClientStore.PrepareServerStaging(pkgDir, "garden");
        string serverTarget = Path.Combine(checkout, "Mods", "Binaries", "garden");
        try
        {
            check(File.Exists(Path.Combine(serverOnly, "garden.dll")) && !File.Exists(Path.Combine(serverOnly, "garden.Client.dll"))
                && !Directory.Exists(Path.Combine(serverOnly, "client")),
                "Split staging strips client assembly and payloadDir from server staging folder");

            // Simulate ModBridge.ps1 deploying the server half + user having turned the mod off before an update.
            Directory.CreateDirectory(serverTarget);
            foreach (string file in Directory.EnumerateFiles(serverOnly, "*", SearchOption.AllDirectories))
            {
                string dest = Path.Combine(serverTarget, Path.GetRelativePath(serverOnly, file));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(file, dest, overwrite: true);
            }
            File.WriteAllText(Path.Combine(serverTarget, "README.md"), "keep-me");
            ModCatalog.WriteChoice(Path.Combine(checkout, "Mods", "Binaries"), "garden", enabled: false);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(serverOnly)!, recursive: true);
        }

        // Deploy client half while mod is disabled -> preserves Mods/Binaries/garden and mod-state.txt choice
        var deployedOff = NativeModsClientStore.DeployClientHalf(pkgDir, checkout, enableOverride: null);
        string rowMapPath = Path.Combine(WaypointModInstaller.GetMergedCacheDirectory(checkout), "content-row-map.json");
        var choicesAfterDeploy = ModCatalog.ReadChoices(Path.Combine(checkout, "Mods", "Binaries"));
        check(deployedOff.Succeeded && File.Exists(Path.Combine(serverTarget, "README.md"))
            && choicesAfterDeploy.TryGetValue("garden", out bool stayedOff) && !stayedOff && !File.Exists(rowMapPath),
            "Deploying dual-side client half does not overwrite Mods/Binaries/<id>/ or clobber an existing disabled choice in mod-state.txt");

        // Enable mod -> builds Mods/Client/.merged-cache/content-row-map.json
        ModCatalog.WriteChoice(Path.Combine(checkout, "Mods", "Binaries"), "garden", enabled: true);
        var deployed = NativeModsClientStore.DeployClientHalf(pkgDir, checkout, enableOverride: true);
        check(deployed.Succeeded && Directory.Exists(Path.Combine(checkout, "Mods", "Client", "garden"))
            && deployed.MergedCache is { Rebuilt: true, Digest.Length: > 0 } && File.Exists(rowMapPath),
            "Deploying dual-side client half writes Mods/Client/<id>/ and builds Mods/Client/.merged-cache/content-row-map.json");

        // Disabling the mod in mod-state.txt clears .merged-cache
        ModCatalog.WriteChoice(Path.Combine(checkout, "Mods", "Binaries"), "garden", enabled: false);
        var disabledCache = NativeModsClientStore.OnModEnabledChanged(checkout, "garden", enabled: false);
        check(disabledCache is { Removed: true } && !File.Exists(rowMapPath),
            "Disabling a datPatches mod clears Mods/Client/.merged-cache/");

        // Re-enabling rebuilds .merged-cache
        ModCatalog.WriteChoice(Path.Combine(checkout, "Mods", "Binaries"), "garden", enabled: true);
        var reenabledCache = NativeModsClientStore.OnModEnabledChanged(checkout, "garden", enabled: true);
        check(reenabledCache is { Rebuilt: true } && File.Exists(rowMapPath),
            "Re-enabling a datPatches mod rebuilds Mods/Client/.merged-cache/");

        // Launch rebuilds only a stale cache, so a cache that no longer matches the enabled mods must read as stale.
        check(NativeModsClientStore.IsMergedCacheCurrent(checkout), "A cache just built for the enabled mods is current");
        ModCatalog.WriteChoice(Path.Combine(checkout, "Mods", "Binaries"), "garden", enabled: false);
        check(!NativeModsClientStore.IsMergedCacheCurrent(checkout),
            "Turning a datPatches mod off without a rebuild leaves the cache stale, so the next launch rebuilds it");
        ModCatalog.WriteChoice(Path.Combine(checkout, "Mods", "Binaries"), "garden", enabled: true);
        bool rebuildThrew = false;
        try { NativeModsClientStore.RebuildMergedCacheForEnabledMods(checkout, new InMemoryDat64Source(new Dictionary<string, byte[]>())); }
        catch (Exception) { rebuildThrew = true; }
        check(rebuildThrew && !File.Exists(rowMapPath) && !NativeModsClientStore.IsMergedCacheCurrent(checkout),
            "A failed rebuild removes the old cache instead of leaving it for the client to load");
        check(NativeModsClientStore.RebuildMergedCacheForEnabledMods(checkout) is { Rebuilt: true } && NativeModsClientStore.IsMergedCacheCurrent(checkout)
            && NativeModsClientStore.RebuildMergedCacheForEnabledMods(checkout) is { Rebuilt: false, Diagnostic: null },
            "The cache rebuilds after the failure, and an unchanged cache is not rebuilt again");

        // ModCatalog reads nativeClient metadata and RestartToApply
        var snap = new RuntimeSnapshot(checkout, "test", "", "", "", "", [], [], [],
            ModEnvironment: new(true, Path.Combine(checkout, "Mods", "Binaries"), true));
        var binEntry = ModCatalog.Read(snap).Single(m => m.Id == "garden");
        check(binEntry.HasNativeClient && binEntry.NativeClient!.Required && binEntry.ClientPayloadInstalled && binEntry.RestartToApply,
            "ModCatalog surfaces nativeClient metadata, ClientPayloadInstalled, and RestartToApply");

        // Uninstall updates ledger and removes .merged-cache
        var uninstalledCache = NativeModsClientStore.OnModUninstalled(checkout, "garden");
        check(uninstalledCache is { Removed: true } && !File.Exists(rowMapPath) && WaypointModInstaller.ReadLedger(checkout).Mods.Count == 0,
            "Uninstalling dual-side mod clears ledger entry and removes .merged-cache");

        // Standalone Content.ggpk client produces clean diagnostic instead of throwing
        Directory.Delete(stockDataDir, recursive: true);
        File.WriteAllText(Path.Combine(clientDir, "Content.ggpk"), "ggpk-fixture");
        var ggpkDeploy = NativeModsClientStore.DeployClientHalf(pkgDir, checkout, enableOverride: true);
        check(ggpkDeploy.Succeeded && ggpkDeploy.MergedCache is { Rebuilt: false } && ggpkDeploy.MergedCache.Diagnostic is { Length: > 0 },
            "Standalone Content.ggpk client reports an actionable diagnostic for .merged-cache instead of crashing");

        // A new MtxTypeGameSpecific row takes HASH32 from its MtxTypes row (true of every stock row in 3.29.3c and 3.29.3d),
        // both for a row appended in the same patch and for a stock row of an untouched MtxTypes.
        Dat64TableSchema mtxSchema = schemaReg.Tables["MtxTypes"], gsSchema = schemaReg.Tables["MtxTypeGameSpecific"];
        byte[] stockMtx = BuildSingleRowDatc64(mtxSchema);
        BinaryPrimitives.WriteUInt32LittleEndian(stockMtx.AsSpan(4 + mtxSchema.Hash32Column!.Offset), 0x1234ABCDu);
        byte[] stockGs = BuildSingleRowDatc64(gsSchema);
        stockGs.AsSpan(4, 16).Fill(0xFE); // stock row points at no MtxTypes row
        var mtxSource = new InMemoryDat64Source(new Dictionary<string, byte[]> { ["MtxTypes"] = stockMtx, ["MtxTypeGameSpecific"] = stockGs });
        uint AppendedGsHash(string json)
        {
            var merged = DatPatchMerger.Merge(mtxSource, [DatPatchDocument.Parse(json)], schemaReg);
            var gs = Dat64Table.Parse(merged.MergedTables.Single(t => t.Key.Contains("MtxTypeGameSpecific", StringComparison.OrdinalIgnoreCase)).Value);
            return gs.ReadUInt32(gs.RowCount - 1, gsSchema.Hash32Column!.Offset);
        }
        check(AppendedGsHash("""
            { "modId": "fx", "tables": {
              "MtxTypes": { "append": [{ "key": "fx", "fields": { "Id": "Metadata/Items/Test/Fx", "__col18": "TestFx" } }] },
              "MtxTypeGameSpecific": { "append": [{ "key": "fxgs", "fields": { "Type": "$ref:MtxTypes:fx" } }] } } }
            """) == PoeHashing.Hash32("Metadata/Items/Test/Fx"),
            "An appended MtxTypeGameSpecific row gets the HASH32 of the MtxTypes row appended beside it");
        check(AppendedGsHash("""
            { "modId": "gs", "tables": { "MtxTypeGameSpecific": { "append": [{ "key": "gs", "fields": { "Type": 0 } }] } } }
            """) == 0x1234ABCDu,
            "An appended MtxTypeGameSpecific row gets the HASH32 of the stock MtxTypes row it points at");
    }

    private static byte[] BuildSingleRowDatc64(Dat64TableSchema schema)
    {
        // Minimal valid PoE .datc64: 1 row of schema.RowStride bytes + 8-byte 0xBB sentinel + 4-byte empty UTF-16LE string at offset 8.
        byte[] fixedRows = new byte[schema.RowStride];
        foreach (Dat64ColumnSchema col in schema.Columns)
        {
            if (col.Type == Dat64ColumnType.String && !col.IsArray)
                BinaryPrimitives.WriteUInt64LittleEndian(fixedRows.AsSpan(col.Offset, 8), 8UL);
        }
        byte[] varSec = new byte[12];
        for (int i = 0; i < 8; i++) varSec[i] = 0xBB;
        return new Dat64Table(1, schema.RowStride, fixedRows, varSec).Serialize();
    }

    private static byte[] BuildSyntheticPe32Plus()
    {
        // Minimal valid PE32+ executable with 1 section (.rdata) and 1 import descriptor (KERNEL32.dll)
        // matching LocalExile.Client.Patcher's synthetic test fixture layout.
        const int fileAlign = 0x200;
        const int sectAlign = 0x1000;
        byte[] buf = new byte[0x400];

        // DOS header
        buf[0] = (byte)'M';
        buf[1] = (byte)'Z';
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(0x3C, 4), 0x80);

        // PE signature + COFF header at 0x80
        buf[0x80] = (byte)'P';
        buf[0x81] = (byte)'E';
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(0x84, 2), 0x8664); // AMD64
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(0x86, 2), 1);      // NumberOfSections
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(0x94, 2), 0xF0);   // SizeOfOptionalHeader
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(0x96, 2), 0x0022); // EXECUTABLE_IMAGE | LARGE_ADDRESS_AWARE

        // Optional header PE32+ at 0x98
        int opt = 0x98;
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(opt, 2), 0x20B);          // PE32+
        BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(opt + 0x18, 8), 0x140000000UL);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(opt + 0x20, 4), sectAlign);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(opt + 0x24, 4), fileAlign);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(opt + 0x38, 4), 0x2000);  // SizeOfImage
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(opt + 0x3C, 4), 0x200);   // SizeOfHeaders
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(opt + 0x6C, 4), 16);      // NumberOfRvaAndSizes

        // DataDirectory[1] = Import Table at RVA 0x1000, size 40 (1 descriptor + null terminator)
        int dd = opt + 0x70;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(dd + 8, 4), 0x1000);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(dd + 12, 4), 40);

        // Section header #0 (.rdata) at 0x98 + 0xF0 = 0x188
        int sh = opt + 0xF0;
        Encoding.ASCII.GetBytes(".rdata").CopyTo(buf, sh);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(sh + 8, 4), 0x200);       // VirtualSize
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(sh + 12, 4), 0x1000);     // VirtualAddress
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(sh + 16, 4), 0x200);      // SizeOfRawData
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(sh + 20, 4), 0x200);      // PointerToRawData
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(sh + 36, 4), 0x40000040); // INITIALIZED_DATA | MEM_READ

        // Section raw data at 0x200 (RVA 0x1000): 1 Import Descriptor + null terminator + name + thunk
        int raw = 0x200;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(raw + 0, 4), 0x1060);     // OriginalFirstThunk
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(raw + 12, 4), 0x1040);    // Name ("KERNEL32.dll")
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(raw + 16, 4), 0x1070);    // FirstThunk
        Encoding.ASCII.GetBytes("KERNEL32.dll\0").CopyTo(buf, raw + 0x40);
        BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(raw + 0x60, 8), 0x1080);  // Hint/Name RVA
        BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(raw + 0x70, 8), 0x1080);
        Encoding.ASCII.GetBytes("ExitProcess\0").CopyTo(buf, raw + 0x82);

        return buf;
    }
}
