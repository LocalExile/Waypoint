using System.Text;
using System.Text.Json;
using LocalExile.Client.AssetPipeline.Dat64;
using LocalExile.Client.Waypoint;

namespace Launcher.Core.NativeMods;

/// <summary>Compact NativeMods metadata surfaced in Waypoint's <see cref="ModEntry"/> and <see cref="ModListing"/>.</summary>
public sealed record NativeClientSummary(
    bool Required,
    string SdkVersionRange = ">=0.1.0-poc <2.0.0",
    bool RawAccess = false,
    string? RawAccessJustification = null,
    int ReplacesCount = 0,
    bool HasClientAssembly = false,
    int DatPatchCount = 0,
    IReadOnlyList<string>? SupportedBuilds = null);

/// <summary>Result of rebuilding or cleaning <c>Mods/Client/.merged-cache/</c>.</summary>
public sealed record MergedCacheOutcome(
    bool Rebuilt,
    bool Removed,
    string? Digest,
    string? Diagnostic,
    IReadOnlyList<string> IncludedMods,
    IReadOnlyList<string> MergedTables);

/// <summary>Result of deploying the client half of a dual-side mod into <c>Mods/Client/&lt;id&gt;/</c>.</summary>
public sealed record ClientModDeployResult(
    bool Succeeded,
    string Message,
    InstallAuditReport? Audit,
    MergedCacheOutcome? MergedCache);

/// <summary>
/// Coordinates dual-side mod inspection, validation, split server/client deployment, ledger tracking,
/// and deterministic <c>Mods/Client/.merged-cache/</c> rebuilds for Waypoint.
/// </summary>
public static class NativeModsClientStore
{
    public const string ClientModStateFileName = ".client-mod-state.json";

    public static string GetClientModStatePath(string root) =>
        Path.Combine(WaypointModInstaller.GetClientModsRoot(root), ClientModStateFileName);

    /// <summary>Parses a compact <see cref="NativeClientSummary"/> from a <c>mod.json</c> or <c>mods.json</c> <c>nativeClient</c> object.</summary>
    public static NativeClientSummary? TryReadSummary(JsonElement field)
    {
        if (field.ValueKind != JsonValueKind.Object) return null;

        bool Bool(string name) => field.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
        string Text(string name, string fallback = "") =>
            field.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? fallback : fallback;
        int ArrayCount(string name) =>
            field.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.GetArrayLength() : 0;
        int Int(string name) =>
            field.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n) ? n : 0;

        string? justification = field.TryGetProperty("rawAccessJustification", out var j) && j.ValueKind == JsonValueKind.String
            ? j.GetString()
            : null;
        int replacesCount = field.TryGetProperty("replacesCount", out _) ? Int("replacesCount") : ArrayCount("replaces");
        int datPatchCount = field.TryGetProperty("datPatchesCount", out _) ? Int("datPatchesCount") : ArrayCount("datPatches");
        bool hasAsm = field.TryGetProperty("hasClientAssembly", out _)
            ? Bool("hasClientAssembly")
            : !string.IsNullOrWhiteSpace(Text("assembly"));
        string[] builds = field.TryGetProperty("supportedBuilds", out var b) && b.ValueKind == JsonValueKind.Array
            ? b.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).Where(s => s.Length > 0).ToArray()
            : ["3.29.3d", "3.29.3c"];

        return new NativeClientSummary(
            Required: Bool("required"),
            SdkVersionRange: Text("sdkVersionRange", ">=0.1.0-poc <2.0.0"),
            RawAccess: Bool("rawAccess"),
            RawAccessJustification: justification,
            ReplacesCount: replacesCount,
            HasClientAssembly: hasAsm,
            DatPatchCount: datPatchCount,
            SupportedBuilds: builds);
    }

    /// <summary>Returns true if <paramref name="folder"/> contains a <c>mod.json</c> declaring a <c>nativeClient</c> block.</summary>
    public static bool HasNativeClientManifest(string folder)
    {
        string path = Path.Combine(folder, "mod.json");
        if (!File.Exists(path) || !ModCatalog.Physical(path)) return false;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.EnumerateObject().Any(p => p.Name.Equals("nativeClient", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.Object);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    /// <summary>Runs full manifest, payload, <c>.ui</c> linter, <c>.datpatch.json</c>, closed-table, and cross-mod conflict auditing without modifying disk.</summary>
    public static InstallAuditReport AuditPackage(
        string packageDir,
        string root,
        bool allowRawAccess = false,
        bool allowConflicts = false)
    {
        var installer = new WaypointModInstaller();
        return installer.AuditPackage(packageDir, root, new InstallOptions
        {
            AllowRawAccess = allowRawAccess,
            AllowConflicts = allowConflicts,
            RebuildMergedCache = false,
        });
    }

    /// <summary>
    /// Creates a clean server-only staging folder under <see cref="ModRepository.DownloadsFolder"/> (excluding client-only
    /// <c>payloadDir/</c>, <c>Client/</c>, <c>nativeClient.assembly</c>, and <c>bundleFile</c>) so Waypoint's adapter
    /// <c>ModBridge.ps1 InstallMod</c> deploys only server files into <c>Mods/Binaries/&lt;id&gt;/</c>.
    /// </summary>
    public static string PrepareServerStaging(string fullStagedDir, string id)
    {
        var manifest = DualModManifest.LoadFromFile(Path.Combine(fullStagedDir, "mod.json"));
        string payloadRoot = string.IsNullOrWhiteSpace(manifest.NativeClient?.PayloadDir)
            ? "client"
            : manifest.NativeClient.PayloadDir.Replace('\\', '/').Split('/')[0];
        string? clientAsm = manifest.NativeClient?.Assembly;
        string? bundleFile = manifest.NativeClient?.BundleFile;

        string work = Path.Combine(ModRepository.DownloadsFolder, Guid.NewGuid().ToString("N"));
        string serverFolder = Path.Combine(work, id);
        Directory.CreateDirectory(serverFolder);

        foreach (string file in Directory.EnumerateFiles(fullStagedDir, "*", SearchOption.AllDirectories))
        {
            string rel = DualModPackageValidator.NormalizeVirtualPath(Path.GetRelativePath(fullStagedDir, file));
            string[] parts = rel.Split('/');
            if (parts.Any(p => p is "bin" or "obj" or ".git" or ".vs")) continue;
            if (parts[0].Equals(payloadRoot, StringComparison.OrdinalIgnoreCase)) continue;
            if (parts[0].Equals("Client", StringComparison.OrdinalIgnoreCase) && parts.Length > 1) continue;
            if (clientAsm is not null && rel.Equals(clientAsm, StringComparison.OrdinalIgnoreCase)) continue;
            if (bundleFile is not null && rel.Equals(bundleFile, StringComparison.OrdinalIgnoreCase)) continue;

            string dest = Path.Combine(serverFolder, Path.Combine(parts));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
        }

        return serverFolder;
    }

    /// <summary>
    /// Deploys the client payload of a validated dual-side mod into <c>Mods/Client/&lt;id&gt;/</c>, updates
    /// <c>Mods/Client/.nativemods-ledger.json</c> and <c>.client-mod-state.json</c>, and deterministically rebuilds
    /// <c>Mods/Client/.merged-cache/</c> for all currently enabled <c>datPatches</c> mods.
    /// </summary>
    public static ClientModDeployResult DeployClientHalf(
        string packageDir,
        string root,
        bool? enableOverride = null,
        bool allowRawAccess = false,
        bool allowConflicts = false,
        IDat64Source? dat64Source = null)
    {
        CheckoutIgnores.Ensure(root);
        var installer = new WaypointModInstaller();

        // Install client files + ledger via WaypointModInstaller; rebuild .merged-cache via our enabled-aware path below.
        var audit = installer.Install(packageDir, root, new InstallOptions
        {
            AllowRawAccess = allowRawAccess,
            AllowConflicts = allowConflicts,
            EnableOnInstall = enableOverride,
            RebuildMergedCache = false,
        });

        if (!audit.Succeeded)
            return new(false, audit.FailureReason ?? L.Text("Client mod installation was blocked by validation."), audit, null);

        var choices = ModCatalog.ReadChoices(Path.Combine(root, "Mods", "Binaries"));
        bool isEnabled = enableOverride ?? choices.GetValueOrDefault(audit.ModId, true);
        WriteClientModState(root, audit.ModId, isEnabled);

        MergedCacheOutcome? cache = null;
        if (audit.DatPatches is { Count: > 0 } || Directory.Exists(WaypointModInstaller.GetMergedCacheDirectory(root)))
            cache = RebuildMergedCacheForEnabledMods(root, dat64Source);

        return new(true, L.Text("Deployed NativeMods client payload for {0}.", audit.ModName), audit, cache);
    }

    /// <summary>Syncs enable state into <c>Mods/Client/.client-mod-state.json</c> and rebuilds <c>.merged-cache/</c> when a <c>datPatches</c> mod toggles.</summary>
    public static MergedCacheOutcome? OnModEnabledChanged(string root, string modId, bool enabled, IDat64Source? dat64Source = null)
    {
        var ledger = WaypointModInstaller.ReadLedger(root);
        var record = ledger.Mods.FirstOrDefault(m => m.Id.Equals(modId, StringComparison.OrdinalIgnoreCase));
        if (record is null) return null;

        WriteClientModState(root, record.Id, enabled);

        bool hasDatPatches = (record.DatPatches is { Count: > 0 }) || (record.Manifest.NativeClient?.DatPatches.Count > 0);
        if (!hasDatPatches) return null;

        return RebuildMergedCacheForEnabledMods(root, dat64Source);
    }

    /// <summary>Updates the ledger, removes client enable state, and rebuilds or cleans <c>Mods/Client/.merged-cache/</c> after uninstall.</summary>
    public static MergedCacheOutcome? OnModUninstalled(string root, string modId, IDat64Source? dat64Source = null)
    {
        RemoveClientModState(root, modId);
        var ledger = WaypointModInstaller.ReadLedger(root);
        var removed = ledger.Mods.FirstOrDefault(m => m.Id.Equals(modId, StringComparison.OrdinalIgnoreCase));
        if (removed is null) return null;

        var remaining = ledger.Mods.Where(m => !m.Id.Equals(modId, StringComparison.OrdinalIgnoreCase)).ToList();
        WriteLedger(root, new DeploymentLedger(ledger.SchemaVersion, DateTimeOffset.UtcNow, remaining, ledger.MergedCacheDigest, ledger.MergedCacheUpdatedUtc));

        bool hadDatPatches = (removed.DatPatches is { Count: > 0 }) || (removed.Manifest.NativeClient?.DatPatches.Count > 0);
        if (!hadDatPatches && !Directory.Exists(WaypointModInstaller.GetMergedCacheDirectory(root)))
            return null;

        return RebuildMergedCacheForEnabledMods(root, dat64Source);
    }

    /// <summary>
    /// Rebuilds <c>Mods/Client/.merged-cache/</c> + <c>content-row-map.json</c> deterministically from all currently
    /// <b>enabled</b> installed mods that declare <c>nativeClient.datPatches</c>.
    /// </summary>
    public static MergedCacheOutcome RebuildMergedCacheForEnabledMods(string root, IDat64Source? explicitSource = null)
    {
        var ledger = WaypointModInstaller.ReadLedger(root);
        var choices = ModCatalog.ReadChoices(Path.Combine(root, "Mods", "Binaries"));

        var enabledRecords = ledger.Mods
            .Where(m => choices.GetValueOrDefault(m.Id, m.Manifest.Enabled))
            .OrderBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var patches = CollectDatPatches(root, enabledRecords);
        string mergedDir = WaypointModInstaller.GetMergedCacheDirectory(root);

        if (patches.Count == 0)
        {
            bool existed = Directory.Exists(mergedDir);
            if (existed) Directory.Delete(mergedDir, recursive: true);
            WriteLedger(root, new DeploymentLedger(ledger.SchemaVersion, DateTimeOffset.UtcNow, ledger.Mods, null, null));
            return new(Rebuilt: false, Removed: existed, Digest: null, Diagnostic: null, IncludedMods: [], MergedTables: []);
        }

        IDat64Source? ownedSource = null;
        IDat64Source? source = explicitSource;
        if (source is null)
        {
            string clientDir = WaypointExtenderManager.GetClientDir(root);
            string kind = NativeModsExtender.DetectStockDatSourceKind(clientDir);
            if (kind is "Bundles2" or "Data")
            {
                ownedSource = new DirectoryDat64Source(clientDir);
                source = ownedSource;
            }
            else if (kind == "StandaloneGgpk")
            {
                return new(
                    Rebuilt: false,
                    Removed: false,
                    Digest: ledger.MergedCacheDigest,
                    Diagnostic: L.Text("Standalone Content.ggpk client detected: automatic .merged-cache rebuild requires Steam Bundles2/ or extracted Client/Data/*.datc64 tables."),
                    IncludedMods: [],
                    MergedTables: []);
            }
            else
            {
                return new(
                    Rebuilt: false,
                    Removed: false,
                    Digest: ledger.MergedCacheDigest,
                    Diagnostic: L.Text("No stock Client/Bundles2 or Client/Data tables found to build Mods/Client/.merged-cache/."),
                    IncludedMods: [],
                    MergedTables: []);
            }
        }

        try
        {
            string clientRoot = WaypointModInstaller.GetClientModsRoot(root);
            Directory.CreateDirectory(clientRoot);
            string staging = Path.Combine(clientRoot, ".staging-merged-cache-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            try
            {
                DatPatchMergeResult merged = DatPatchMerger.Merge(source, patches, outputDirectory: staging);
                SwapDirectoryAtomically(staging, mergedDir);
                WriteLedger(root, new DeploymentLedger(ledger.SchemaVersion, DateTimeOffset.UtcNow, ledger.Mods, merged.RowMap.Digest, DateTimeOffset.UtcNow));
                return new(
                    Rebuilt: true,
                    Removed: false,
                    Digest: merged.RowMap.Digest,
                    Diagnostic: null,
                    IncludedMods: merged.RowMap.Mods,
                    MergedTables: merged.MergedTables.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
            }
            finally
            {
                if (Directory.Exists(staging))
                    try { Directory.Delete(staging, recursive: true); } catch { }
            }
        }
        finally
        {
            if (ownedSource is IDisposable disposable)
                disposable.Dispose();
        }
    }

    public static Dictionary<string, bool> ReadClientModStates(string root)
    {
        string path = GetClientModStatePath(root);
        if (!File.Exists(path)) return new(StringComparer.OrdinalIgnoreCase);
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, bool>>(File.ReadAllText(path))
                ?? new(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static void WriteClientModState(string root, string modId, bool enabled)
    {
        var map = ReadClientModStates(root);
        map[modId] = enabled;
        SaveClientModStates(root, map);
    }

    private static void RemoveClientModState(string root, string modId)
    {
        string path = GetClientModStatePath(root);
        if (!File.Exists(path)) return;
        var map = ReadClientModStates(root);
        if (map.Remove(modId)) SaveClientModStates(root, map);
    }

    private static void SaveClientModStates(string root, Dictionary<string, bool> map)
    {
        string path = GetClientModStatePath(root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(map, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }

    private static List<DatPatchDocument> CollectDatPatches(string root, IReadOnlyList<InstalledDualModRecord> mods)
    {
        var result = new List<DatPatchDocument>();
        foreach (var mod in mods)
        {
            IReadOnlyList<string> paths = mod.DatPatches is { Count: > 0 } ? mod.DatPatches : (mod.Manifest.NativeClient?.DatPatches ?? []);
            if (paths.Count == 0) continue;

            string clientModDir = !string.IsNullOrWhiteSpace(mod.ClientDir)
                ? Path.Combine(root, Path.Combine(mod.ClientDir.Replace('\\', '/').Split('/')))
                : Path.Combine(WaypointModInstaller.GetClientModsRoot(root), mod.Id);
            string filesDir = Path.Combine(clientModDir, "files");

            var docs = new List<DatPatchDocument>();
            foreach (string raw in paths)
            {
                string norm = DualModPackageValidator.NormalizeVirtualPath(raw);
                string exact = Path.Combine(filesDir, Path.Combine(norm.Split('/')));
                string lower = Path.Combine(filesDir, Path.Combine(norm.ToLowerInvariant().Split('/')));
                string? found = File.Exists(exact) ? exact : File.Exists(lower) ? lower : null;
                if (found is null) continue;
                try { docs.Add(DatPatchDocument.LoadFromFile(found)); }
                catch (Exception e) when (e is IOException or InvalidDataException or JsonException) { }
            }

            if (docs.Count == 0) continue;
            if (docs.Count == 1)
            {
                result.Add(docs[0]);
                continue;
            }

            var depends = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string d in mod.Manifest.Depends.Concat(docs.SelectMany(x => x.Depends)))
                if (!string.IsNullOrWhiteSpace(d) && !d.Equals("Game", StringComparison.OrdinalIgnoreCase) && !d.Equals(mod.Id, StringComparison.OrdinalIgnoreCase))
                    depends.Add(d.Trim());

            var tables = new Dictionary<string, (List<DatAppendEntry> Appends, List<DatModifyEntry> Modifies)>(StringComparer.OrdinalIgnoreCase);
            var deadStats = new List<DeadStatAllocationSpec>();
            foreach (var doc in docs)
            {
                foreach (var (name, tp) in doc.Tables)
                {
                    if (tp is null) continue;
                    if (!tables.TryGetValue(name, out var lists)) tables[name] = lists = ([], []);
                    lists.Appends.AddRange(tp.Append);
                    lists.Modifies.AddRange(tp.Modify);
                }
                deadStats.AddRange(doc.AllocateDeadStat);
            }

            var finalTables = tables.ToDictionary(
                kv => kv.Key,
                kv => new DatTablePatch { Append = kv.Value.Appends, Modify = kv.Value.Modifies },
                StringComparer.OrdinalIgnoreCase);

            result.Add(new DatPatchDocument
            {
                SchemaVersion = DatPatchDocument.CurrentSchemaVersion,
                ModId = mod.Id,
                Depends = depends.ToList(),
                Tables = finalTables,
                AllocateDeadStat = deadStats,
            });
        }
        return result;
    }

    private static void WriteLedger(string root, DeploymentLedger ledger)
    {
        string path = WaypointModInstaller.GetLedgerPath(root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(ledger, DualModManifest.SerializerOptions), new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }

    private static void SwapDirectoryAtomically(string stagingDir, string targetDir)
    {
        string? backupDir = null;
        if (Directory.Exists(targetDir))
        {
            backupDir = targetDir + ".old-" + Guid.NewGuid().ToString("N");
            Directory.Move(targetDir, backupDir);
        }
        try
        {
            Directory.Move(stagingDir, targetDir);
        }
        catch
        {
            if (backupDir is not null && Directory.Exists(backupDir) && !Directory.Exists(targetDir))
                Directory.Move(backupDir, targetDir);
            throw;
        }
        if (backupDir is not null && Directory.Exists(backupDir))
            Directory.Delete(backupDir, recursive: true);
    }
}
