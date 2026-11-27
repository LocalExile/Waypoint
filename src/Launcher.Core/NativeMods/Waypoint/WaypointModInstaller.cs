using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LocalExile.Client.AssetPipeline.Dat64;

namespace LocalExile.Client.Waypoint;

/// <summary>
/// Options controlling how <see cref="WaypointModInstaller"/> deploys a dual-side mod.
/// </summary>
public sealed record InstallOptions
{
    /// <summary>Current NativeMods Client SDK version used to check <c>nativeClient.sdkVersionRange</c>.</summary>
    public SemVer? CurrentSdkVersion { get; init; }

    /// <summary>Whether to also pack <c>client/</c> into a deterministic <c>.bundle.bin</c> + <c>manifest.json</c> under <c>Mods/Client/&lt;id&gt;/</c>.</summary>
    public bool PackClientBundle { get; init; } = true;

    /// <summary>Whether to also deploy loose UTF-16LE BOM CRLF text overlays under <c>Mods/Client/&lt;id&gt;/files/</c>.</summary>
    public bool DeployLooseOverlays { get; init; } = true;

    /// <summary>When false, cross-mod conflicts abort installation rather than warning.</summary>
    public bool AllowConflicts { get; init; }

    /// <summary>When false (the safe default), mods requesting <c>rawAccess: true</c> are blocked until explicitly approved by the player.</summary>
    public bool AllowRawAccess { get; init; }

    /// <summary>Whether to enable the mod in <c>Mods/Binaries/mod-state.txt</c> upon install (null uses manifest default).</summary>
    public bool? EnableOnInstall { get; init; }

    /// <summary>
    /// Optional stock PoE 3.29 client or <c>Data/</c> directory used by <see cref="DatPatchMerger"/>
    /// to build <c>Mods/Client/.merged-cache/</c> when installed mods declare <c>nativeClient.datPatches</c>.
    /// When omitted, <see cref="WaypointModInstaller"/> checks <c>&lt;localExileRoot&gt;/Client</c> automatically.
    /// </summary>
    public string? StockClientDirectory { get; init; }

    /// <summary>
    /// Optional explicit <see cref="IDat64Source"/> supplying stock <c>.datc64</c> tables for
    /// <see cref="DatPatchMerger"/> (takes precedence over <see cref="StockClientDirectory"/>).
    /// </summary>
    public IDat64Source? Dat64Source { get; init; }

    /// <summary>
    /// Whether to deterministically build or update <c>Mods/Client/.merged-cache/</c> whenever installed mods
    /// declare <c>nativeClient.datPatches</c> and a stock <c>.datc64</c> source is available (default <c>true</c>).
    /// </summary>
    public bool RebuildMergedCache { get; init; } = true;

    /// <summary>
    /// Whether to deploy server files into <c>Mods/Binaries/&lt;id&gt;/</c> (default <c>true</c>).
    /// Set to <c>false</c> when the caller already deployed the server half via <c>ModBridge.ps1</c>.
    /// </summary>
    public bool DeployServerFiles { get; init; } = true;

    /// <summary>
    /// Whether to write <c>Mods/Binaries/mod-state.txt</c> (default <c>true</c>).
    /// Set to <c>false</c> to preserve existing user enable/disable choices managed by Waypoint.
    /// </summary>
    public bool UpdateModState { get; init; } = true;
}

/// <summary>Result of rebuilding <c>Mods/Client/.merged-cache/</c> via <see cref="DatPatchMerger"/>.</summary>
public sealed record MergedCacheBuildResult(
    bool Built,
    string MergedCacheDirectory,
    string? Digest,
    IReadOnlyList<string> IncludedMods,
    IReadOnlyList<string> MergedTables,
    IReadOnlyList<string> GeneratedOverlays);

/// <summary>A single file recorded in the deployment ledger.</summary>
public sealed record DeployedFileRecord(
    [property: JsonPropertyName("relativePath")] string RelativePath,
    [property: JsonPropertyName("area")] string Area,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("sizeBytes")] long SizeBytes);

/// <summary>Deployment record for one installed dual-side mod in <c>Mods/Client/.nativemods-ledger.json</c>.</summary>
public sealed record InstalledDualModRecord(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("installedUtc")] DateTimeOffset InstalledUtc,
    [property: JsonPropertyName("serverDir")] string ServerDir,
    [property: JsonPropertyName("clientDir")] string? ClientDir,
    [property: JsonPropertyName("bundleFile")] string? BundleFile,
    [property: JsonPropertyName("rawAccess")] bool RawAccess,
    [property: JsonPropertyName("rawAccessJustification")] string? RawAccessJustification,
    [property: JsonPropertyName("replaces")] IReadOnlyList<string> Replaces,
    [property: JsonPropertyName("overlays")] IReadOnlyList<string> Overlays,
    [property: JsonPropertyName("customOpcodes")] IReadOnlyList<CustomOpcodeDeclaration> CustomOpcodes,
    [property: JsonPropertyName("gealBuiltins")] IReadOnlyList<string> GealBuiltins,
    [property: JsonPropertyName("customControls")] IReadOnlyList<string> CustomControls,
    [property: JsonPropertyName("contentRegistries")] IReadOnlyList<ContentRegistryDeclaration> ContentRegistries,
    [property: JsonPropertyName("manifest")] DualModManifest Manifest,
    [property: JsonPropertyName("files")] IReadOnlyList<DeployedFileRecord> Files,
    [property: JsonPropertyName("datPatches")] IReadOnlyList<string>? DatPatches = null);

/// <summary>Persistent deployment ledger stored at <c>&lt;serverRoot&gt;/Mods/Client/.nativemods-ledger.json</c>.</summary>
public sealed record DeploymentLedger(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("updatedUtc")] DateTimeOffset UpdatedUtc,
    [property: JsonPropertyName("mods")] IReadOnlyList<InstalledDualModRecord> Mods,
    [property: JsonPropertyName("mergedCacheDigest")] string? MergedCacheDigest = null,
    [property: JsonPropertyName("mergedCacheUpdatedUtc")] DateTimeOffset? MergedCacheUpdatedUtc = null);

/// <summary>
/// Human-readable and machine-readable audit report returned by <see cref="WaypointModInstaller"/>
/// before or after installing a dual-side mod.
/// </summary>
public sealed record InstallAuditReport(
    bool Succeeded,
    bool IsUpgrade,
    string? PreviousVersion,
    string ModId,
    string ModName,
    string Version,
    bool HasNativeClient,
    bool RawAccessRequested,
    string? RawAccessJustification,
    IReadOnlyList<string> ReplacedStockPaths,
    IReadOnlyList<string> AddedOverlayPaths,
    IReadOnlyList<CustomOpcodeDeclaration> CustomOpcodes,
    IReadOnlyList<string> GealBuiltins,
    IReadOnlyList<string> CustomControls,
    IReadOnlyList<ContentRegistryDeclaration> ContentRegistries,
    ValidationReport Validation,
    IReadOnlyList<CrossModConflict> Conflicts,
    IReadOnlyList<DeployedFileRecord> DeployedFiles,
    string? ServerTargetDir,
    string? ClientTargetDir,
    string? BundleFile,
    string? FailureReason = null,
    IReadOnlyList<string>? DatPatches = null,
    string? MergedCacheDigest = null)
{
    public string ToHumanReadableSummary()
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine(inv, $"=== NativeMods Install Audit: {ModName} ({ModId} v{Version}) ===");
        sb.AppendLine(inv, $"Outcome: {(Succeeded ? (IsUpgrade ? $"UPGRADED (from v{PreviousVersion})" : "INSTALLED") : $"BLOCKED - {FailureReason}")}");
        sb.AppendLine(inv, $"Native Client Half: {(HasNativeClient ? "Yes" : "No (server-only)")}");
        sb.AppendLine(inv, $"Raw Memory/Hook Access: {(RawAccessRequested ? $"YES — {RawAccessJustification}" : "No")}");

        if (ReplacedStockPaths.Count > 0)
        {
            sb.AppendLine(inv, $"Replaced Stock Client Paths ({ReplacedStockPaths.Count}):");
            foreach (string path in ReplacedStockPaths)
                sb.AppendLine(inv, $"  [OVERRIDE] {path}");
        }
        else
        {
            sb.AppendLine("Replaced Stock Client Paths: None");
        }

        if (AddedOverlayPaths.Count > 0)
            sb.AppendLine(inv, $"Added Namespaced Overlays: {AddedOverlayPaths.Count}");
        if (CustomOpcodes.Count > 0)
            sb.AppendLine(inv, $"Custom Opcodes: {string.Join(", ", CustomOpcodes.Select(o => $"{o.Name} (0x{o.Id:X4}, {o.Direction})"))}");
        if (GealBuiltins.Count > 0)
            sb.AppendLine(inv, $"GEAL Built-ins: {string.Join(", ", GealBuiltins)}");
        if (CustomControls.Count > 0)
            sb.AppendLine(inv, $"Custom UI Controls: {string.Join(", ", CustomControls)}");
        if (ContentRegistries.Count > 0)
            sb.AppendLine(inv, $"Content Registries: {string.Join(", ", ContentRegistries.Select(r => $"{r.Table}:{r.EntryId} ({r.Mode})"))}");
        if (DatPatches is { Count: > 0 })
            sb.AppendLine(inv, $"Declarative Dat Patches: {string.Join(", ", DatPatches)}");
        if (!string.IsNullOrWhiteSpace(MergedCacheDigest))
            sb.AppendLine(inv, $"Merged Cache Digest: {MergedCacheDigest}");

        if (Conflicts.Count > 0)
        {
            sb.AppendLine(inv, $"Cross-Mod Conflicts ({Conflicts.Count}):");
            foreach (var c in Conflicts)
                sb.AppendLine(inv, $"  [{c.Code}] {c.Message}");
        }
        else
        {
            sb.AppendLine("Cross-Mod Conflicts: None");
        }

        int errors = Validation.Errors.Count();
        int warnings = Validation.Warnings.Count();
        sb.AppendLine(inv, $"Validation: {errors} error(s), {warnings} warning(s)");
        foreach (var diag in Validation.Diagnostics.Where(d => d.Severity != DiagnosticSeverity.Info))
        {
            string loc = diag.FilePath is not null ? $" ({diag.FilePath}{(diag.Line is not null ? $":{diag.Line}" : "")})" : "";
            sb.AppendLine(inv, $"  [{diag.Severity}] {diag.Code}{loc}: {diag.Message}");
        }

        if (Succeeded)
        {
            sb.AppendLine(inv, $"Deployed Files: {DeployedFiles.Count}");
            if (ServerTargetDir is not null) sb.AppendLine(inv, $"  Server Dir: {ServerTargetDir}");
            if (ClientTargetDir is not null) sb.AppendLine(inv, $"  Client Dir: {ClientTargetDir}");
            if (BundleFile is not null) sb.AppendLine(inv, $"  Packed Bundle: {BundleFile}");
        }

        return sb.ToString();
    }
}

/// <summary>Result of uninstalling a dual-side mod.</summary>
public sealed record UninstallResult(
    bool Succeeded,
    string ModId,
    bool RemovedServerDir,
    bool RemovedClientDir,
    string Message);

/// <summary>
/// Installs, upgrades, audits, and cleanly uninstalls dual-side mods into a LocalExile checkout's
/// <c>Mods/Binaries/&lt;id&gt;/</c> and <c>Mods/Client/&lt;id&gt;/</c> directories.
/// Never touches stock client files (<c>Client/Game.exe</c>, <c>Client/Content.ggpk</c>, <c>Client/Bundles2/</c>).
/// </summary>
public sealed class WaypointModInstaller
{
    public const string LedgerFileName = ".nativemods-ledger.json";
    public const string ModStateFileName = "mod-state.txt";
    public const string MergedCacheDirectoryName = ".merged-cache";

    public static string GetServerBinariesRoot(string localExileRoot) =>
        Path.Combine(localExileRoot, "Mods", "Binaries");

    public static string GetClientModsRoot(string localExileRoot) =>
        Path.Combine(localExileRoot, "Mods", "Client");

    public static string GetMergedCacheDirectory(string localExileRoot) =>
        Path.Combine(GetClientModsRoot(localExileRoot), MergedCacheDirectoryName);

    public static string GetLedgerPath(string localExileRoot) =>
        Path.Combine(GetClientModsRoot(localExileRoot), LedgerFileName);

    /// <summary>Reads the deployment ledger, returning an empty ledger when none exists yet.</summary>
    public static DeploymentLedger ReadLedger(string localExileRoot)
    {
        string path = GetLedgerPath(localExileRoot);
        if (!File.Exists(path))
            return new DeploymentLedger(1, DateTimeOffset.UtcNow, []);

        string json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<DeploymentLedger>(json, DualModManifest.SerializerOptions)
            ?? new DeploymentLedger(1, DateTimeOffset.UtcNow, []);
    }

    /// <summary>
    /// Audits a candidate dual-side mod package directory against the target LocalExile checkout without modifying disk.
    /// </summary>
    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Uniform instance service API for Waypoint DI/composition.")]
    public InstallAuditReport AuditPackage(
        string packageDir,
        string localExileRoot,
        InstallOptions? options = null) =>
        AuditPackageCore(packageDir, localExileRoot, options, allowAutoNormalizedEncErrors: false);

    private static InstallAuditReport AuditPackageCore(
        string packageDir,
        string localExileRoot,
        InstallOptions? options,
        bool allowAutoNormalizedEncErrors)
    {
        options ??= new InstallOptions();
        var validation = DualModPackageValidator.ValidatePackageDirectory(packageDir, options.CurrentSdkVersion);

        string manifestPath = Path.Combine(packageDir, "mod.json");
        if (!File.Exists(manifestPath))
        {
            return new InstallAuditReport(
                false, false, null, Path.GetFileName(packageDir), Path.GetFileName(packageDir), "0.0.0",
                false, false, null, [], [], [], [], [], [],
                validation, [], [], null, null, null, "Missing mod.json.");
        }

        DualModManifest manifest;
        try
        {
            manifest = DualModManifest.LoadFromFile(manifestPath);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException)
        {
            return new InstallAuditReport(
                false, false, null, Path.GetFileName(packageDir), Path.GetFileName(packageDir), "0.0.0",
                false, false, null, [], [], [], [], [], [],
                validation, [], [], null, null, null, $"Invalid mod.json: {ex.Message}");
        }

        var ledger = ReadLedger(localExileRoot);
        var existingRecord = ledger.Mods.FirstOrDefault(m => m.Id.Equals(manifest.Id, StringComparison.OrdinalIgnoreCase));
        var otherManifests = ledger.Mods
            .Where(m => !m.Id.Equals(manifest.Id, StringComparison.OrdinalIgnoreCase))
            .Select(m => m.Manifest)
            .Append(manifest)
            .ToList();

        var conflicts = DualModPackageValidator.DetectConflicts(otherManifests).ToList();

        // Also detect cross-mod .datpatch.json entryKey / dead-stat slot collisions (two mods targeting the
        // SAME .datc64 table with distinct entryKeys produce ZERO conflicts!).
        var allDatPatches = new List<DatPatchDocument>();
        foreach (InstalledDualModRecord installed in ledger.Mods)
        {
            if (installed.Id.Equals(manifest.Id, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (TryLoadInstalledModDatPatch(localExileRoot, installed) is { } installedPatch)
            {
                allDatPatches.Add(installedPatch);
            }
        }

        if (TryLoadPackageDatPatch(packageDir, manifest) is { } candidatePatch)
        {
            allDatPatches.Add(candidatePatch);
        }

        if (allDatPatches.Count > 1)
        {
            conflicts.AddRange(DualModPackageValidator.DetectDatPatchConflicts(allDatPatches));
        }

        var nc = manifest.NativeClient;

        bool hasBlockingValidationErrors = allowAutoNormalizedEncErrors
            ? validation.Errors.Any(d => !string.Equals(d.Code, "ENC001", StringComparison.Ordinal))
            : !validation.IsValid;

        string? failureReason = null;
        if (hasBlockingValidationErrors)
            failureReason = "Package validation failed with errors.";
        else if (nc is { RawAccess: true } && !options.AllowRawAccess)
            failureReason = "Mod requests RawAccess, which is not approved in InstallOptions.";
        else if (conflicts.Count > 0 && !options.AllowConflicts)
            failureReason = $"Detected {conflicts.Count} cross-mod conflict(s).";

        return new InstallAuditReport(
            Succeeded: failureReason is null,
            IsUpgrade: existingRecord is not null,
            PreviousVersion: existingRecord?.Version,
            ModId: manifest.Id,
            ModName: manifest.DisplayName,
            Version: manifest.Version,
            HasNativeClient: nc is not null,
            RawAccessRequested: nc?.RawAccess ?? false,
            RawAccessJustification: nc?.RawAccessJustification,
            ReplacedStockPaths: nc?.Replaces ?? [],
            AddedOverlayPaths: nc?.Overlays ?? [],
            CustomOpcodes: nc?.CustomOpcodes ?? [],
            GealBuiltins: nc?.GealBuiltins ?? [],
            CustomControls: nc?.CustomControls ?? [],
            ContentRegistries: nc?.ContentRegistries ?? [],
            Validation: validation,
            Conflicts: conflicts,
            DeployedFiles: [],
            ServerTargetDir: Path.Combine(GetServerBinariesRoot(localExileRoot), manifest.Id),
            ClientTargetDir: nc is not null ? Path.Combine(GetClientModsRoot(localExileRoot), manifest.Id) : null,
            BundleFile: null,
            FailureReason: failureReason,
            DatPatches: nc?.DatPatches ?? []);
    }

    /// <summary>
    /// Installs or upgrades a dual-side mod from a `.zip` release archive into the LocalExile checkout.
    /// </summary>
    public InstallAuditReport InstallFromZip(
        string zipPath,
        string localExileRoot,
        InstallOptions? options = null)
    {
        if (!File.Exists(zipPath))
            throw new FileNotFoundException("Mod archive not found.", zipPath);

        long zipLen = new FileInfo(zipPath).Length;
        if (zipLen > DualModManifest.MaxPackageBytes)
            throw new InvalidDataException("Mod zip exceeds Waypoint's 64 MB limit.");

        string tempStage = Path.Combine(Path.GetTempPath(), "waypoint-nativemods-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempStage);
        try
        {
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                var entries = zip.Entries
                    .Select(e => (Entry: e, Name: e.FullName.Replace('\\', '/')))
                    .Where(x => !x.Name.EndsWith('/'))
                    .ToList();

                if (entries.Count == 0)
                    throw new InvalidDataException("Zip archive contains no files.");
                if (entries.Count > DualModManifest.MaxPackageFiles)
                    throw new InvalidDataException($"Zip archive exceeds {DualModManifest.MaxPackageFiles} files.");

                // Strip single top-level wrapper directory if present (matching Waypoint ModRepository.Extract)
                string? singlePrefix = null;
                if (entries.All(x => x.Name.Contains('/')))
                {
                    string candidate = entries[0].Name.Split('/')[0] + "/";
                    if (entries.All(x => x.Name.StartsWith(candidate, StringComparison.Ordinal)))
                        singlePrefix = candidate;
                }

                long totalUncompressed = 0;
                foreach (var (entry, rawName) in entries)
                {
                    string rel = singlePrefix is not null ? rawName[singlePrefix.Length..] : rawName;
                    if (!DualModPackageValidator.IsSafeRelativeVirtualPath(rel))
                        throw new InvalidDataException($"Zip contains unsafe entry path '{rawName}'.");

                    totalUncompressed += entry.Length;
                    if (totalUncompressed > DualModManifest.MaxPackageBytes)
                        throw new InvalidDataException("Extracted mod size exceeds Waypoint's 64 MB limit.");

                    string dest = Path.Combine(tempStage, Path.Combine(rel.Split('/')));
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    entry.ExtractToFile(dest, overwrite: true);
                }
            }

            return Install(tempStage, localExileRoot, options);
        }
        finally
        {
            try { Directory.Delete(tempStage, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// Installs or cleanly upgrades a dual-side mod from <paramref name="packageDir"/> into <paramref name="localExileRoot"/>.
    /// </summary>
    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Uniform instance service API for Waypoint DI/composition.")]
    public InstallAuditReport Install(
        string packageDir,
        string localExileRoot,
        InstallOptions? options = null)
    {
        options ??= new InstallOptions();
        if (!DualModPackageValidator.IsPhysicalPath(localExileRoot))
            throw new IOException("Redirected LocalExile root paths are not supported.");

        var audit = AuditPackageCore(packageDir, localExileRoot, options, allowAutoNormalizedEncErrors: true);
        if (!audit.Succeeded)
            return audit;

        var manifest = DualModManifest.LoadFromFile(Path.Combine(packageDir, "mod.json"));
        string serverRoot = GetServerBinariesRoot(localExileRoot);
        string clientRoot = GetClientModsRoot(localExileRoot);
        string serverTarget = Path.Combine(serverRoot, manifest.Id);
        string? clientTarget = manifest.NativeClient is not null ? Path.Combine(clientRoot, manifest.Id) : null;

        EnsureWithinDirectory(serverTarget, serverRoot);
        if (clientTarget is not null)
            EnsureWithinDirectory(clientTarget, clientRoot);

        var deployedFiles = new List<DeployedFileRecord>();

        // 1. Deploy server half cleanly into Mods/Binaries/<id>/ (skipped when ModBridge.ps1 already deployed it)
        if (options.DeployServerFiles)
        {
            // Preserve existing user settings.json values during an upgrade if present
            string? preservedSettings = null;
            string existingSettingsPath = Path.Combine(serverTarget, "settings.json");
            if (audit.IsUpgrade && File.Exists(existingSettingsPath))
                preservedSettings = File.ReadAllText(existingSettingsPath);

            Directory.CreateDirectory(serverRoot);
            string serverStaging = Path.Combine(serverRoot, $".staging-{manifest.Id}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(serverStaging);
            try
            {
                DeployServerFiles(packageDir, manifest, serverStaging);

                // Keep existing settings.json on upgrade if package didn't ship a fresh one
                if (preservedSettings is not null && !File.Exists(Path.Combine(serverStaging, "settings.json")))
                    File.WriteAllText(Path.Combine(serverStaging, "settings.json"), preservedSettings, new UTF8Encoding(false));

                SwapDirectoryAtomically(serverStaging, serverTarget);
            }
            finally
            {
                if (Directory.Exists(serverStaging))
                    try { Directory.Delete(serverStaging, recursive: true); } catch { }
            }
        }

        if (Directory.Exists(serverTarget))
        {
            foreach (string file in Directory.EnumerateFiles(serverTarget, "*", SearchOption.AllDirectories)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                string rel = DualModPackageValidator.NormalizeVirtualPath(Path.GetRelativePath(serverTarget, file));
                deployedFiles.Add(new(rel, "server", DualModPackageValidator.ComputeFileSha256(file), new FileInfo(file).Length));
            }
        }

        // 2. Deploy client half cleanly into Mods/Client/<id>/ (never touching Client/ stock files)
        string? deployedBundleFile = null;
        if (manifest.NativeClient is { } nc && clientTarget is not null)
        {
            Directory.CreateDirectory(clientRoot);
            string clientStaging = Path.Combine(clientRoot, $".staging-{manifest.Id}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(clientStaging);
            try
            {
                deployedBundleFile = DeployClientFiles(packageDir, manifest, nc, clientStaging, options);
                SwapDirectoryAtomically(clientStaging, clientTarget);

                foreach (string file in Directory.EnumerateFiles(clientTarget, "*", SearchOption.AllDirectories)
                    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                {
                    string rel = DualModPackageValidator.NormalizeVirtualPath(Path.GetRelativePath(clientTarget, file));
                    deployedFiles.Add(new(rel, "client", DualModPackageValidator.ComputeFileSha256(file), new FileInfo(file).Length));
                }
            }
            finally
            {
                if (Directory.Exists(clientStaging))
                    try { Directory.Delete(clientStaging, recursive: true); } catch { }
            }
        }
        else if (audit.IsUpgrade)
        {
            // Upgraded from dual-side to server-only: remove old client folder
            string oldClientDir = Path.Combine(clientRoot, manifest.Id);
            if (Directory.Exists(oldClientDir))
                Directory.Delete(oldClientDir, recursive: true);
        }

        // 3. Update Waypoint-compatible Mods/Binaries/mod-state.txt (skipped when Waypoint manages choices)
        if (options.UpdateModState)
        {
            bool enableState = options.EnableOnInstall ?? manifest.Enabled;
            WriteModStateChoice(serverRoot, manifest.Id, enableState);
        }

        // 4. Persist deployment ledger
        var ledger = ReadLedger(localExileRoot);
        var updatedMods = ledger.Mods
            .Where(m => !m.Id.Equals(manifest.Id, StringComparison.OrdinalIgnoreCase))
            .ToList();

        updatedMods.Add(new InstalledDualModRecord(
            manifest.Id,
            manifest.DisplayName,
            manifest.Version,
            DateTimeOffset.UtcNow,
            DualModPackageValidator.NormalizeVirtualPath(Path.GetRelativePath(localExileRoot, serverTarget)),
            clientTarget is not null ? DualModPackageValidator.NormalizeVirtualPath(Path.GetRelativePath(localExileRoot, clientTarget)) : null,
            deployedBundleFile,
            manifest.NativeClient?.RawAccess ?? false,
            manifest.NativeClient?.RawAccessJustification,
            manifest.NativeClient?.Replaces ?? [],
            manifest.NativeClient?.Overlays ?? [],
            manifest.NativeClient?.CustomOpcodes ?? [],
            manifest.NativeClient?.GealBuiltins ?? [],
            manifest.NativeClient?.CustomControls ?? [],
            manifest.NativeClient?.ContentRegistries ?? [],
            manifest,
            deployedFiles,
            manifest.NativeClient?.DatPatches ?? []));

        updatedMods = updatedMods.OrderBy(m => m.Id, StringComparer.OrdinalIgnoreCase).ToList();

        // 5. Deterministically build/update Mods/Client/.merged-cache/ when installed mods declare datPatches
        string? mergedCacheDigest = ledger.MergedCacheDigest;
        DateTimeOffset? mergedCacheUpdatedUtc = ledger.MergedCacheUpdatedUtc;
        if (options.RebuildMergedCache)
        {
            MergedCacheBuildResult? buildResult = TryRebuildMergedCacheForMods(localExileRoot, updatedMods, options);
            if (buildResult is not null)
            {
                mergedCacheDigest = buildResult.Digest;
                mergedCacheUpdatedUtc = buildResult.Built ? DateTimeOffset.UtcNow : null;
            }
        }

        WriteLedger(localExileRoot, new DeploymentLedger(1, DateTimeOffset.UtcNow, updatedMods, mergedCacheDigest, mergedCacheUpdatedUtc));

        return audit with
        {
            Succeeded = true,
            DeployedFiles = deployedFiles,
            BundleFile = deployedBundleFile,
            MergedCacheDigest = mergedCacheDigest,
        };
    }

    /// <summary>
    /// Deterministically rebuilds <c>Mods/Client/.merged-cache/</c> from all currently installed mods'
    /// declared <c>datPatches</c> using <paramref name="source"/> and updates the ledger's recorded digest.
    /// </summary>
    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Uniform instance service API for Waypoint DI/composition.")]
    public MergedCacheBuildResult RebuildMergedCache(string localExileRoot, IDat64Source source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localExileRoot);
        ArgumentNullException.ThrowIfNull(source);

        var ledger = ReadLedger(localExileRoot);
        MergedCacheBuildResult result = BuildMergedCacheCore(localExileRoot, ledger.Mods, source);
        WriteLedger(
            localExileRoot,
            new DeploymentLedger(
                ledger.SchemaVersion,
                DateTimeOffset.UtcNow,
                ledger.Mods,
                result.Digest,
                result.Built ? DateTimeOffset.UtcNow : null));
        return result;
    }

    /// <summary>
    /// Deterministically rebuilds <c>Mods/Client/.merged-cache/</c> from all currently installed mods'
    /// declared <c>datPatches</c> using stock tables in <paramref name="stockClientDirectory"/> and updates the ledger.
    /// </summary>
    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Uniform instance service API for Waypoint DI/composition.")]
    public MergedCacheBuildResult RebuildMergedCache(string localExileRoot, string stockClientDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stockClientDirectory);
        using var source = new DirectoryDat64Source(stockClientDirectory);
        return RebuildMergedCache(localExileRoot, source);
    }

    /// <summary>
    /// Cleanly uninstalls a dual-side mod from both <c>Mods/Binaries/&lt;id&gt;/</c> and <c>Mods/Client/&lt;id&gt;/</c>,
    /// removes it from <c>mod-state.txt</c> and the deployment ledger, updates or cleans <c>.merged-cache/</c>,
    /// and never touches stock client files.
    /// </summary>
    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Uniform instance service API for Waypoint DI/composition.")]
    public UninstallResult Uninstall(string modId, string localExileRoot) =>
        Uninstall(modId, localExileRoot, options: null);

    /// <summary>
    /// Cleanly uninstalls a dual-side mod and rebuilds (or removes) <c>Mods/Client/.merged-cache/</c> using
    /// optional stock source settings from <paramref name="options"/>.
    /// </summary>
    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Uniform instance service API for Waypoint DI/composition.")]
    public UninstallResult Uninstall(string modId, string localExileRoot, InstallOptions? options)
    {
        if (!DualModManifest.IsValidId(modId))
            return new(false, modId, false, false, $"Invalid mod id '{modId}'.");

        string serverRoot = GetServerBinariesRoot(localExileRoot);
        string clientRoot = GetClientModsRoot(localExileRoot);
        string serverDir = Path.Combine(serverRoot, modId);
        string clientDir = Path.Combine(clientRoot, modId);

        EnsureWithinDirectory(serverDir, serverRoot);
        EnsureWithinDirectory(clientDir, clientRoot);

        bool removedServer = false;
        bool removedClient = false;

        if (Directory.Exists(serverDir))
        {
            Directory.Delete(serverDir, recursive: true);
            removedServer = true;
        }

        if (Directory.Exists(clientDir))
        {
            Directory.Delete(clientDir, recursive: true);
            removedClient = true;
        }

        RemoveModStateChoice(serverRoot, modId);

        var ledger = ReadLedger(localExileRoot);
        bool wasInLedger = ledger.Mods.Any(m => m.Id.Equals(modId, StringComparison.OrdinalIgnoreCase));
        if (wasInLedger)
        {
            var remaining = ledger.Mods
                .Where(m => !m.Id.Equals(modId, StringComparison.OrdinalIgnoreCase))
                .ToList();

            string? mergedCacheDigest = ledger.MergedCacheDigest;
            DateTimeOffset? mergedCacheUpdatedUtc = ledger.MergedCacheUpdatedUtc;
            if (options?.RebuildMergedCache ?? true)
            {
                MergedCacheBuildResult? buildResult = TryRebuildMergedCacheForMods(localExileRoot, remaining, options ?? new InstallOptions());
                if (buildResult is not null)
                {
                    mergedCacheDigest = buildResult.Digest;
                    mergedCacheUpdatedUtc = buildResult.Built ? DateTimeOffset.UtcNow : null;
                }
            }

            WriteLedger(localExileRoot, new DeploymentLedger(1, DateTimeOffset.UtcNow, remaining, mergedCacheDigest, mergedCacheUpdatedUtc));
        }

        if (!removedServer && !removedClient && !wasInLedger)
            return new(false, modId, false, false, $"Mod '{modId}' is not installed.");

        return new(true, modId, removedServer, removedClient, $"Uninstalled '{modId}' cleanly.");
    }

    /// <summary>Reads <c>Mods/Binaries/mod-state.txt</c> using Waypoint's exact format.</summary>
    public static Dictionary<string, bool> ReadModStateChoices(string serverBinariesRoot)
    {
        var choices = new Dictionary<string, bool>(StringComparer.Ordinal);
        string path = Path.Combine(serverBinariesRoot, ModStateFileName);
        if (!File.Exists(path))
            return choices;

        foreach (string line in File.ReadAllLines(path))
        {
            string[] words = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words is [var id, "on" or "off"] && DualModManifest.IsValidId(id))
                choices[id] = words[1] == "on";
        }
        return choices;
    }

    private static void WriteModStateChoice(string serverBinariesRoot, string id, bool enabled)
    {
        Directory.CreateDirectory(serverBinariesRoot);
        string path = Path.Combine(serverBinariesRoot, ModStateFileName);
        var lines = new List<string>();
        if (File.Exists(path))
        {
            foreach (string raw in File.ReadAllLines(path))
            {
                string trimmed = raw.TrimEnd('\r');
                if (trimmed.Length == 0) continue;
                string[] words = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (words is [var other, "on" or "off"] && other == id) continue;
                lines.Add(trimmed);
            }
        }
        lines.Add(id + (enabled ? " on" : " off"));
        string tmp = Path.Combine(serverBinariesRoot, $".waypoint-choices-{Guid.NewGuid():N}.tmp");
        File.WriteAllLines(tmp, lines, new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }

    private static void RemoveModStateChoice(string serverBinariesRoot, string id)
    {
        string path = Path.Combine(serverBinariesRoot, ModStateFileName);
        if (!File.Exists(path)) return;

        var lines = File.ReadAllLines(path)
            .Select(l => l.TrimEnd('\r'))
            .Where(l => l.Length > 0 && (l.Split(' ', StringSplitOptions.RemoveEmptyEntries) is not [var other, "on" or "off"] || other != id))
            .ToList();

        string tmp = Path.Combine(serverBinariesRoot, $".waypoint-choices-{Guid.NewGuid():N}.tmp");
        File.WriteAllLines(tmp, lines, new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }

    private static void DeployServerFiles(string packageDir, DualModManifest manifest, string serverStaging)
    {
        string payloadDirName = string.IsNullOrWhiteSpace(manifest.NativeClient?.PayloadDir)
            ? "client"
            : manifest.NativeClient.PayloadDir.Replace('\\', '/').Split('/')[0];

        string? clientAsm = manifest.NativeClient?.Assembly;
        string? bundleFile = manifest.NativeClient?.BundleFile;

        foreach (string file in Directory.EnumerateFiles(packageDir, "*", SearchOption.AllDirectories))
        {
            string rel = DualModPackageValidator.NormalizeVirtualPath(Path.GetRelativePath(packageDir, file));
            string[] parts = rel.Split('/');

            // Exclude build artifacts, client-only payload/assembly/bundle, and README.md (per LocalExile-Mods build-release.ps1)
            if (parts.Any(p => p is "bin" or "obj" or ".git" or ".vs"))
                continue;
            if (parts[0].Equals(payloadDirName, StringComparison.OrdinalIgnoreCase))
                continue;
            if (parts[0].Equals("Client", StringComparison.OrdinalIgnoreCase) && parts.Length > 1)
                continue;
            if (rel.Equals("README.md", StringComparison.OrdinalIgnoreCase))
                continue;
            if (clientAsm is not null && rel.Equals(clientAsm, StringComparison.OrdinalIgnoreCase))
                continue;
            if (bundleFile is not null && rel.Equals(bundleFile, StringComparison.OrdinalIgnoreCase))
                continue;

            string dest = Path.Combine(serverStaging, Path.Combine(parts));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
        }
    }

    private static string? DeployClientFiles(
        string packageDir,
        DualModManifest manifest,
        NativeClientManifest nc,
        string clientStaging,
        InstallOptions options)
    {
        // Copy mod.json into Mods/Client/<id>/ so the managed client host can inspect manifest + RawAccess flags
        File.Copy(Path.Combine(packageDir, "mod.json"), Path.Combine(clientStaging, "mod.json"), overwrite: true);

        // Copy client assembly if shipped in package root (or Client/)
        if (!string.IsNullOrWhiteSpace(nc.Assembly))
        {
            string rootAsm = Path.Combine(packageDir, nc.Assembly);
            string nestedAsm = Path.Combine(packageDir, "Client", nc.Assembly);
            string? foundAsm = File.Exists(rootAsm) ? rootAsm : File.Exists(nestedAsm) ? nestedAsm : null;
            if (foundAsm is not null)
                File.Copy(foundAsm, Path.Combine(clientStaging, nc.Assembly), overwrite: true);
        }

        string payloadDirRel = string.IsNullOrWhiteSpace(nc.PayloadDir) ? "client" : nc.PayloadDir;
        string sourcePayloadDir = Path.Combine(packageDir, Path.Combine(payloadDirRel.Replace('\\', '/').Split('/')));

        string? bundleFileName = null;
        if (Directory.Exists(sourcePayloadDir))
        {
            if (options.DeployLooseOverlays || nc.DatPatches.Count > 0)
            {
                string filesDest = Path.Combine(clientStaging, "files");
                Directory.CreateDirectory(filesDest);
                var declaredDatPatches = new HashSet<string>(
                    nc.DatPatches.Select(DualModPackageValidator.NormalizeVirtualPath),
                    StringComparer.OrdinalIgnoreCase);

                foreach (string srcFile in Directory.EnumerateFiles(sourcePayloadDir, "*", SearchOption.AllDirectories))
                {
                    string rawRel = DualModPackageValidator.NormalizeVirtualPath(Path.GetRelativePath(sourcePayloadDir, srcFile));
                    if (!options.DeployLooseOverlays && !declaredDatPatches.Contains(rawRel))
                    {
                        continue;
                    }

                    string rel = rawRel.ToLowerInvariant();
                    string dest = Path.Combine(filesDest, Path.Combine(rel.Split('/')));
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);

                    // Encode text overlays (.ui, .ot, .it, etc. with BOM; .sm, .mat, .amd without BOM) to UTF-16LE CRLF as 3.29 expects!
                    if (ModBundlePacker.IsClientTextExtension(rel))
                    {
                        string text = DualModPackageValidator.ReadTextAutoEncoding(srcFile);
                        byte[] encoded = ModBundlePacker.IsClientTextNoBomExtension(rel)
                            ? ModBundlePacker.EncodeClientTextNoBom(text)
                            : ModBundlePacker.EncodeClientText(text);
                        File.WriteAllBytes(dest, encoded);
                    }
                    else
                    {
                        File.Copy(srcFile, dest, overwrite: true);
                    }
                }
            }

            if (options.PackClientBundle)
            {
                var packed = ModBundlePacker.Pack(manifest.Id, manifest.Version, sourcePayloadDir, clientStaging, encodeTextAsUtf16Le: true);
                bundleFileName = packed.Manifest.BundleFile;
            }
        }
        else if (!string.IsNullOrWhiteSpace(nc.BundleFile))
        {
            string prebuiltBundle = Path.Combine(packageDir, nc.BundleFile);
            if (File.Exists(prebuiltBundle))
            {
                File.Copy(prebuiltBundle, Path.Combine(clientStaging, Path.GetFileName(nc.BundleFile)), overwrite: true);
                bundleFileName = Path.GetFileName(nc.BundleFile);
                string prebuiltManifest = Path.Combine(packageDir, "manifest.json");
                if (File.Exists(prebuiltManifest))
                    File.Copy(prebuiltManifest, Path.Combine(clientStaging, "manifest.json"), overwrite: true);
            }
        }

        return bundleFileName;
    }

    private static void WriteLedger(string localExileRoot, DeploymentLedger ledger)
    {
        string clientRoot = GetClientModsRoot(localExileRoot);
        Directory.CreateDirectory(clientRoot);
        string path = GetLedgerPath(localExileRoot);
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

    private static MergedCacheBuildResult? TryRebuildMergedCacheForMods(
        string localExileRoot,
        IReadOnlyList<InstalledDualModRecord> installedMods,
        InstallOptions options)
    {
        var patches = CollectInstalledModDatPatches(localExileRoot, installedMods);
        string mergedCacheDir = GetMergedCacheDirectory(localExileRoot);

        if (patches.Count == 0)
        {
            if (Directory.Exists(mergedCacheDir))
            {
                Directory.Delete(mergedCacheDir, recursive: true);
            }

            return new MergedCacheBuildResult(false, mergedCacheDir, null, [], [], []);
        }

        if (options.Dat64Source is not null)
        {
            return BuildMergedCacheCore(localExileRoot, installedMods, options.Dat64Source, patches);
        }

        string? stockDir = ResolveStockClientDirectory(localExileRoot, options.StockClientDirectory);
        if (stockDir is null)
        {
            return null;
        }

        using var dirSource = new DirectoryDat64Source(stockDir);
        return BuildMergedCacheCore(localExileRoot, installedMods, dirSource, patches);
    }

    private static MergedCacheBuildResult BuildMergedCacheCore(
        string localExileRoot,
        IReadOnlyList<InstalledDualModRecord> installedMods,
        IDat64Source source,
        List<DatPatchDocument>? preloadedPatches = null)
    {
        List<DatPatchDocument> patches = preloadedPatches ?? CollectInstalledModDatPatches(localExileRoot, installedMods);
        string clientRoot = GetClientModsRoot(localExileRoot);
        string mergedCacheDir = GetMergedCacheDirectory(localExileRoot);
        EnsureWithinDirectory(mergedCacheDir, clientRoot);

        if (patches.Count == 0)
        {
            if (Directory.Exists(mergedCacheDir))
            {
                Directory.Delete(mergedCacheDir, recursive: true);
            }

            return new MergedCacheBuildResult(false, mergedCacheDir, null, [], [], []);
        }

        Directory.CreateDirectory(clientRoot);
        string stagingDir = Path.Combine(clientRoot, $".staging-merged-cache-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingDir);
        try
        {
            DatPatchMergeResult mergeResult = DatPatchMerger.Merge(source, patches, outputDirectory: stagingDir);
            SwapDirectoryAtomically(stagingDir, mergedCacheDir);

            return new MergedCacheBuildResult(
                Built: true,
                MergedCacheDirectory: mergedCacheDir,
                Digest: mergeResult.RowMap.Digest,
                IncludedMods: mergeResult.RowMap.Mods,
                MergedTables: mergeResult.MergedTables.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray(),
                GeneratedOverlays: mergeResult.GeneratedOverlays.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
        }
        finally
        {
            if (Directory.Exists(stagingDir))
                try { Directory.Delete(stagingDir, recursive: true); } catch { }
        }
    }

    private static string? ResolveStockClientDirectory(string localExileRoot, string? configuredStockDir)
    {
        if (!string.IsNullOrWhiteSpace(configuredStockDir) && Directory.Exists(configuredStockDir))
        {
            return configuredStockDir;
        }

        string defaultClientDir = Path.Combine(localExileRoot, "Client");
        if (Directory.Exists(defaultClientDir) &&
            (File.Exists(Path.Combine(defaultClientDir, "Bundles2", "_.index.bin")) ||
             Directory.Exists(Path.Combine(defaultClientDir, "Data"))))
        {
            return defaultClientDir;
        }

        return null;
    }

    private static List<DatPatchDocument> CollectInstalledModDatPatches(
        string localExileRoot,
        IReadOnlyList<InstalledDualModRecord> installedMods)
    {
        var patches = new List<DatPatchDocument>();
        foreach (InstalledDualModRecord mod in installedMods.OrderBy(m => m.Id, StringComparer.OrdinalIgnoreCase))
        {
            if (TryLoadInstalledModDatPatch(localExileRoot, mod) is { } doc)
            {
                patches.Add(doc);
            }
        }

        return patches;
    }

    private static DatPatchDocument? TryLoadInstalledModDatPatch(string localExileRoot, InstalledDualModRecord installed)
    {
        IReadOnlyList<string> patchPaths = installed.DatPatches is { Count: > 0 }
            ? installed.DatPatches
            : (installed.Manifest.NativeClient?.DatPatches ?? []);

        if (patchPaths.Count == 0)
        {
            return null;
        }

        string clientModDir = !string.IsNullOrWhiteSpace(installed.ClientDir)
            ? Path.Combine(localExileRoot, Path.Combine(installed.ClientDir.Replace('\\', '/').Split('/')))
            : Path.Combine(GetClientModsRoot(localExileRoot), installed.Id);

        string filesDir = Path.Combine(clientModDir, "files");
        var docs = new List<DatPatchDocument>();
        foreach (string rawRel in patchPaths)
        {
            string normalized = DualModPackageValidator.NormalizeVirtualPath(rawRel);
            string exactPath = Path.Combine(filesDir, Path.Combine(normalized.Split('/')));
            string lowerPath = Path.Combine(filesDir, Path.Combine(normalized.ToLowerInvariant().Split('/')));
            string? resolved = File.Exists(exactPath) ? exactPath : File.Exists(lowerPath) ? lowerPath : null;
            if (resolved is null)
            {
                continue;
            }

            try
            {
                docs.Add(DatPatchDocument.LoadFromFile(resolved));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException)
            {
                // Ignore corrupted/stale patch files during audit/rebuild discovery; validator surfaces errors.
            }
        }

        return ConsolidateModDatPatches(installed.Id, installed.Manifest.Depends, docs);
    }

    private static DatPatchDocument? TryLoadPackageDatPatch(string packageDir, DualModManifest manifest)
    {
        NativeClientManifest? nc = manifest.NativeClient;
        if (nc is null || nc.DatPatches.Count == 0)
        {
            return null;
        }

        string payloadDirRel = string.IsNullOrWhiteSpace(nc.PayloadDir) ? "client" : nc.PayloadDir;
        string payloadDir = Path.Combine(packageDir, Path.Combine(payloadDirRel.Replace('\\', '/').Split('/')));
        var docs = new List<DatPatchDocument>();

        foreach (string rawRel in nc.DatPatches)
        {
            string normalized = DualModPackageValidator.NormalizeVirtualPath(rawRel);
            string fullPath = Path.Combine(payloadDir, Path.Combine(normalized.Split('/')));
            if (!File.Exists(fullPath))
            {
                continue;
            }

            try
            {
                docs.Add(DatPatchDocument.LoadFromFile(fullPath));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException)
            {
                // Validation diagnostics already report syntax errors.
            }
        }

        return ConsolidateModDatPatches(manifest.Id, manifest.Depends, docs);
    }

    private static DatPatchDocument? ConsolidateModDatPatches(
        string modId,
        IReadOnlyList<string> manifestDepends,
        List<DatPatchDocument> docs)
    {
        if (docs.Count == 0)
        {
            return null;
        }

        var depends = new List<string>();
        var seenDepends = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void AddDep(string dep)
        {
            if (string.IsNullOrWhiteSpace(dep) ||
                dep.Equals("Game", StringComparison.OrdinalIgnoreCase) ||
                dep.Equals(modId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (seenDepends.Add(dep.Trim()))
            {
                depends.Add(dep.Trim());
            }
        }

        foreach (string dep in manifestDepends)
        {
            AddDep(dep);
        }

        var mergedTables = new Dictionary<string, (List<DatAppendEntry> Appends, List<DatModifyEntry> Modifies)>(StringComparer.OrdinalIgnoreCase);
        var mergedDeadStats = new List<DeadStatAllocationSpec>();

        foreach (DatPatchDocument doc in docs)
        {
            foreach (string dep in doc.Depends)
            {
                AddDep(dep);
            }

            foreach (var (tableName, tablePatch) in doc.Tables)
            {
                if (tablePatch is null)
                {
                    continue;
                }

                if (!mergedTables.TryGetValue(tableName, out var lists))
                {
                    lists = ([], []);
                    mergedTables[tableName] = lists;
                }

                lists.Appends.AddRange(tablePatch.Append);
                lists.Modifies.AddRange(tablePatch.Modify);
            }

            mergedDeadStats.AddRange(doc.AllocateDeadStat);
        }

        var finalTables = new Dictionary<string, DatTablePatch>(StringComparer.OrdinalIgnoreCase);
        foreach (var (tableName, (appends, modifies)) in mergedTables)
        {
            finalTables[tableName] = new DatTablePatch
            {
                Append = appends,
                Modify = modifies,
            };
        }

        return new DatPatchDocument
        {
            SchemaVersion = DatPatchDocument.CurrentSchemaVersion,
            ModId = modId,
            Depends = depends,
            Tables = finalTables,
            AllocateDeadStat = mergedDeadStats,
        };
    }

    private static void EnsureWithinDirectory(string candidatePath, string rootDir)
    {
        string fullRoot = Path.GetFullPath(rootDir);
        string rootWithSep = fullRoot.EndsWith(Path.DirectorySeparatorChar) ? fullRoot : fullRoot + Path.DirectorySeparatorChar;
        string fullCandidate = Path.GetFullPath(candidatePath);
        if (!fullCandidate.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Target path '{candidatePath}' escapes '{rootDir}'.");
    }
}
