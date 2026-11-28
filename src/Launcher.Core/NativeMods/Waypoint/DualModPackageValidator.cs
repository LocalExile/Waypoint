using System.Security.Cryptography;
using System.Text.Json;
using LocalExile.Client.AssetPipeline.Dat64;

namespace LocalExile.Client.Waypoint;

/// <summary>Result of validating a dual-side mod manifest and/or package directory.</summary>
public sealed record ValidationReport(IReadOnlyList<ValidationDiagnostic> Diagnostics)
{
    public bool IsValid => !Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);
    public IEnumerable<ValidationDiagnostic> Errors => Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error);
    public IEnumerable<ValidationDiagnostic> Warnings => Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Warning);
    public IEnumerable<ValidationDiagnostic> Infos => Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Info);
}

/// <summary>A conflict detected between two installed or candidate dual-side mods.</summary>
public sealed record CrossModConflict(
    string Code,
    string Kind,
    string Resource,
    string ModA,
    string ModB,
    string Message);

/// <summary>
/// Validates dual-side mod manifests, payloads, `.ui` layouts, namespace rules, SDK SemVer compatibility,
/// SHA-256 digests, and cross-mod collisions.
/// </summary>
public static class DualModPackageValidator
{
    private static readonly HashSet<string> AllowedKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "feature", "league", "script"
    };

    private static readonly HashSet<string> SupportedClientBuilds = new(StringComparer.OrdinalIgnoreCase)
    {
        "3.29.3d", "3.29.3c"
    };

    /// <summary>The 22 tables verified appendable in 10-verification.md §5 and live 3.29.3c/3.29.3d testing.</summary>
    public static readonly IReadOnlySet<string> AppendableRegistryTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "BaseItemTypes",
        "ItemVisualIdentity",
        "CurrencyItems",
        "MtxTypes",
        "MtxTypeGameSpecific",
        "Pet",
        "MonsterVarieties",
        "NPCs",
        "Chests",
        "Projectiles",
        "MiscObjects",
        "MiscAnimated",
        "HideoutDoodads",
        "ExecuteGEAL",
        "ClientStrings",
        "Mods",
        "ModType",
        "BuffDefinitions",
        "MinimapIcons",
        "BuffVisuals",
        "NPCTalk",
        "Words"
    };

    /// <summary>
    /// Tables bound to compiled enums or fixed-size structures in 3.29 that cannot safely accept appended rows
    /// (<c>ItemClasses</c> compiled 103-entry enum; <c>Stats</c> fixed 23,362 / 0x5b42 rows where 0x5b43 is
    /// compiled <c>StatId::INVALID</c> across 35+ sites and 4 fixed-size tables OOB on <c>statId &gt;= 0x5b43</c>).
    /// </summary>
    public static readonly IReadOnlySet<string> ClosedRegistryTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "ItemClasses",
        "Stats"
    };

    /// <summary>
    /// Validates a <see cref="DualModManifest"/> without inspecting disk files.
    /// </summary>
    public static ValidationReport ValidateManifest(DualModManifest manifest, SemVer? currentSdkVersion = null)
    {
        var diagnostics = new List<ValidationDiagnostic>();

        if (!DualModManifest.IsValidId(manifest.Id))
        {
            diagnostics.Add(new(DiagnosticSeverity.Error, "MAN001",
                $"Invalid mod id '{manifest.Id}'. Must match ^[A-Za-z0-9][A-Za-z0-9_.-]{{0,127}}$ and not be '.' or '..'."));
        }
        else if (!DualModManifest.IsCanonicalCatalogId(manifest.Id))
        {
            diagnostics.Add(new(DiagnosticSeverity.Warning, "MAN002",
                $"Mod id '{manifest.Id}' contains uppercase characters; LocalExile-Mods catalog requires lower-case ids."));
        }

        if (!SemVer.TryParse(manifest.Version, out _))
        {
            diagnostics.Add(new(DiagnosticSeverity.Error, "MAN003",
                $"Invalid mod version '{manifest.Version}'. Expected <major>.<minor>.<patch>."));
        }

        if (!AllowedKinds.Contains(manifest.Kind))
        {
            diagnostics.Add(new(DiagnosticSeverity.Error, "MAN004",
                $"Unknown mod kind '{manifest.Kind}'. Expected one of: feature, league, script."));
        }

        if (!DualModManifest.IsSafeAssemblyFileName(manifest.EffectiveAssembly))
        {
            diagnostics.Add(new(DiagnosticSeverity.Error, "MAN005",
                $"Invalid server assembly '{manifest.EffectiveAssembly}'. Must be a single .dll filename with no path separators."));
        }

        if (manifest.CompatibleClientVersions.Count == 0)
        {
            diagnostics.Add(new(DiagnosticSeverity.Warning, "MAN006",
                "compatibleClientVersions is empty. LocalExile-Mods build-index.ps1 requires at least one LocalExile version (e.g. '0.5.2-beta')."));
        }
        else
        {
            foreach (string ver in manifest.CompatibleClientVersions)
            {
                if (!SemVer.TryParse(ver, out _))
                {
                    diagnostics.Add(new(DiagnosticSeverity.Error, "MAN007",
                        $"Invalid compatibleClientVersions entry '{ver}'. Expected SemVer such as '0.5.2-beta'."));
                }
            }
        }

        foreach (string dep in manifest.Depends)
        {
            if (!DualModManifest.IsValidId(dep))
            {
                diagnostics.Add(new(DiagnosticSeverity.Error, "MAN008", $"Invalid dependency id '{dep}'."));
            }
            else if (dep.Equals(manifest.Id, StringComparison.Ordinal))
            {
                diagnostics.Add(new(DiagnosticSeverity.Error, "MAN009", $"Mod '{manifest.Id}' cannot depend on itself."));
            }
        }

        if (manifest.NativeClient is { } nc)
        {
            ValidateNativeClientSection(manifest, nc, currentSdkVersion, diagnostics);
        }

        return new ValidationReport(diagnostics);
    }

    /// <summary>
    /// Validates a complete dual-side mod package directory on disk (manifest, payload namespace rules,
    /// declared replaces/overlays vs actual files, `.ui` linting, file limits, and SHA-256 digests).
    /// </summary>
    public static ValidationReport ValidatePackageDirectory(string packageDir, SemVer? currentSdkVersion = null)
    {
        var diagnostics = new List<ValidationDiagnostic>();

        if (!Directory.Exists(packageDir))
        {
            diagnostics.Add(new(DiagnosticSeverity.Error, "PKG001", $"Package directory not found: '{packageDir}'."));
            return new ValidationReport(diagnostics);
        }

        if (!IsPhysicalPath(packageDir))
        {
            diagnostics.Add(new(DiagnosticSeverity.Error, "PKG002", "Redirected/symlinked package paths are not supported by Waypoint."));
            return new ValidationReport(diagnostics);
        }

        string manifestPath = Path.Combine(packageDir, "mod.json");
        if (!File.Exists(manifestPath))
        {
            diagnostics.Add(new(DiagnosticSeverity.Error, "PKG003", "Missing required mod.json at package root.", manifestPath));
            return new ValidationReport(diagnostics);
        }

        DualModManifest manifest;
        try
        {
            manifest = DualModManifest.LoadFromFile(manifestPath);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException)
        {
            diagnostics.Add(new(DiagnosticSeverity.Error, "PKG004", $"Failed to parse mod.json: {ex.Message}", manifestPath));
            return new ValidationReport(diagnostics);
        }

        diagnostics.AddRange(ValidateManifest(manifest, currentSdkVersion).Diagnostics);

        // Enforce Waypoint package size/file/symlink safety limits across the entire package
        long totalBytes = 0;
        int fileCount = 0;
        foreach (string file in Directory.EnumerateFiles(packageDir, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(packageDir, file);
            string[] segments = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (segments.Any(s => s is "bin" or "obj" or ".git" or ".vs"))
                continue;

            if (!IsPhysicalPath(file))
            {
                diagnostics.Add(new(DiagnosticSeverity.Error, "PKG005", $"Symlinked/reparse file '{rel}' is forbidden.", rel));
                continue;
            }

            fileCount++;
            totalBytes += new FileInfo(file).Length;
        }

        if (fileCount > DualModManifest.MaxPackageFiles)
        {
            diagnostics.Add(new(DiagnosticSeverity.Error, "PKG006",
                $"Package contains {fileCount} files, exceeding Waypoint's limit of {DualModManifest.MaxPackageFiles}."));
        }

        if (totalBytes > DualModManifest.MaxPackageBytes)
        {
            diagnostics.Add(new(DiagnosticSeverity.Error, "PKG007",
                $"Package size ({totalBytes} bytes) exceeds Waypoint's 64 MB limit."));
        }

        if (manifest.NativeClient is not { } nc)
            return new ValidationReport(diagnostics);

        // Check client assembly & SHA-256 if declared
        if (!string.IsNullOrWhiteSpace(nc.AssemblySha256))
        {
            if (string.IsNullOrWhiteSpace(nc.Assembly))
            {
                diagnostics.Add(new(DiagnosticSeverity.Error, "INT001",
                    "nativeClient.assemblySha256 is declared, but nativeClient.assembly is not specified."));
            }
            else
            {
                string asmPath = Path.Combine(packageDir, nc.Assembly);
                if (!File.Exists(asmPath))
                {
                    diagnostics.Add(new(DiagnosticSeverity.Error, "INT001",
                        $"Declared client assembly '{nc.Assembly}' (with assemblySha256) is missing from package.", nc.Assembly));
                }
                else
                {
                    VerifyFileHash(asmPath, nc.AssemblySha256, nc.Assembly, "INT001", diagnostics);
                }
            }
        }

        // Check bundle SHA-256 if declared
        if (!string.IsNullOrWhiteSpace(nc.BundleSha256))
        {
            if (string.IsNullOrWhiteSpace(nc.BundleFile))
            {
                diagnostics.Add(new(DiagnosticSeverity.Error, "INT002",
                    "nativeClient.bundleSha256 is declared, but nativeClient.bundleFile is not specified."));
            }
            else
            {
                string bundlePath = Path.Combine(packageDir, nc.BundleFile);
                if (!File.Exists(bundlePath))
                {
                    diagnostics.Add(new(DiagnosticSeverity.Error, "INT002",
                        $"Declared client bundle '{nc.BundleFile}' (with bundleSha256) is missing from package.", nc.BundleFile));
                }
                else
                {
                    VerifyFileHash(bundlePath, nc.BundleSha256, nc.BundleFile, "INT002", diagnostics);
                }
            }
        }

        // Check integrity.files hashes if declared
        if (nc.Integrity?.Files is { Count: > 0 } declaredHashes)
        {
            foreach (var (relPath, expectedHash) in declaredHashes)
            {
                if (!IsSafeRelativeVirtualPath(relPath))
                {
                    diagnostics.Add(new(DiagnosticSeverity.Error, "INT003", $"Unsafe relative path in integrity.files: '{relPath}'.", relPath));
                    continue;
                }
                string fullPath = Path.Combine(packageDir, Path.Combine(relPath.Replace('\\', '/').Split('/')));
                if (!File.Exists(fullPath))
                {
                    diagnostics.Add(new(DiagnosticSeverity.Error, "INT004", $"Integrity entry '{relPath}' does not exist in package.", relPath));
                }
                else
                {
                    VerifyFileHash(fullPath, expectedHash, relPath, "INT005", diagnostics);
                }
            }
        }

        // Inspect payloadDir (default "client")
        string payloadDirRel = string.IsNullOrWhiteSpace(nc.PayloadDir) ? "client" : nc.PayloadDir;
        if (!IsSafeRelativeVirtualPath(payloadDirRel))
        {
            diagnostics.Add(new(DiagnosticSeverity.Error, "PAY001", $"Unsafe payloadDir '{payloadDirRel}'."));
            return new ValidationReport(diagnostics);
        }

        string payloadFullDir = Path.Combine(packageDir, Path.Combine(payloadDirRel.Replace('\\', '/').Split('/')));
        var actualPayloadFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (Directory.Exists(payloadFullDir))
        {
            foreach (string file in Directory.EnumerateFiles(payloadFullDir, "*", SearchOption.AllDirectories))
            {
                string virtualPath = NormalizeVirtualPath(Path.GetRelativePath(payloadFullDir, file));
                actualPayloadFiles[virtualPath] = file;
            }
        }
        else if (nc.Overlays.Count > 0 || nc.Replaces.Count > 0 || nc.DatPatches.Count > 0)
        {
            diagnostics.Add(new(DiagnosticSeverity.Error, "PAY002",
                $"Manifest declares overlays/replaces/datPatches but payload directory '{payloadDirRel}/' does not exist."));
        }

        var declaredReplaces = new HashSet<string>(
            nc.Replaces.Select(NormalizeVirtualPath),
            StringComparer.OrdinalIgnoreCase);

        var declaredOverlays = new HashSet<string>(
            nc.Overlays.Select(NormalizeVirtualPath),
            StringComparer.OrdinalIgnoreCase);

        var declaredDatPatches = new HashSet<string>(
            nc.DatPatches.Select(NormalizeVirtualPath),
            StringComparer.OrdinalIgnoreCase);

        // Enforce path namespace rule: every file in client/ MUST either be under a Mods/<namespace>/ segment,
        // be explicitly declared in nativeClient.replaces, or be a declared nativeClient.datPatches file!
        foreach (string virtualPath in actualPayloadFiles.Keys.Order(StringComparer.OrdinalIgnoreCase))
        {
            bool isNamespaced = IsModNamespacedPath(virtualPath, manifest.Id);
            bool isDeclaredReplace = declaredReplaces.Contains(virtualPath);
            bool isDeclaredDatPatch = declaredDatPatches.Contains(virtualPath);

            if (!isNamespaced && !isDeclaredReplace && !isDeclaredDatPatch)
            {
                diagnostics.Add(new(
                    DiagnosticSeverity.Error,
                    "PAY003",
                    $"Payload file '{virtualPath}' is outside the 'Mods/<namespace>/' namespace and is not declared in nativeClient.replaces. " +
                    "Either move it under a Mods/<id>/ segment or explicitly declare it in nativeClient.replaces.",
                    virtualPath));
            }
            else if (isNamespaced && isDeclaredReplace)
            {
                diagnostics.Add(new(
                    DiagnosticSeverity.Warning,
                    "PAY004",
                    $"File '{virtualPath}' is in a Mods/ namespace yet also listed in nativeClient.replaces (which is intended for stock game paths).",
                    virtualPath));
            }
        }

        // Check declared replaces exist in client/
        foreach (string rep in declaredReplaces)
        {
            if (!actualPayloadFiles.ContainsKey(rep))
            {
                diagnostics.Add(new(
                    DiagnosticSeverity.Error,
                    "PAY005",
                    $"Declared nativeClient.replaces path '{rep}' was not found under '{payloadDirRel}/'.",
                    rep));
            }
        }

        // Check declared overlays exist in client/
        foreach (string ov in declaredOverlays)
        {
            if (!actualPayloadFiles.ContainsKey(ov))
            {
                diagnostics.Add(new(
                    DiagnosticSeverity.Error,
                    "PAY006",
                    $"Declared nativeClient.overlays path '{ov}' was not found under '{payloadDirRel}/'.",
                    ov));
            }
        }

        // Check declared datPatches exist in client/ and validate their JSON syntax, schema tables, & dead-stat slots.
        var parsedModPatches = new List<DatPatchDocument>();
        foreach (string patchPath in declaredDatPatches.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            if (!actualPayloadFiles.TryGetValue(patchPath, out string? fullPatchPath))
            {
                diagnostics.Add(new(
                    DiagnosticSeverity.Error,
                    "PAY007",
                    $"Declared nativeClient.datPatches path '{patchPath}' was not found under '{payloadDirRel}/'.",
                    patchPath));
                continue;
            }

            try
            {
                string json = File.ReadAllText(fullPatchPath);
                DatPatchDocument? doc = ValidateDatPatchJsonCore(json, manifest.Id, patchPath, diagnostics);
                if (doc is not null)
                {
                    parsedModPatches.Add(doc);
                }
            }
            catch (IOException ex)
            {
                diagnostics.Add(new(
                    DiagnosticSeverity.Error,
                    "DAT001",
                    $"Failed to read .datpatch.json '{patchPath}': {ex.Message}",
                    patchPath));
            }
        }

        // Detect duplicate (table, entryKey) or dead-stat slot collisions across multiple .datpatch.json files within the same mod.
        if (parsedModPatches.Count > 1)
        {
            ValidateIntraModDatPatchCollisions(manifest.Id, parsedModPatches, diagnostics);
        }

        // Lint every .ui file in the payload
        var lowerAssetSet = new HashSet<string>(
            actualPayloadFiles.Keys.Select(k => k.ToLowerInvariant()),
            StringComparer.Ordinal);

        foreach (var (virtualPath, fullPath) in actualPayloadFiles.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (virtualPath.EndsWith(".sm", StringComparison.OrdinalIgnoreCase)
                || virtualPath.EndsWith(".mat", StringComparison.OrdinalIgnoreCase)
                || virtualPath.EndsWith(".amd", StringComparison.OrdinalIgnoreCase))
            {
                byte[] header = new byte[3];
                int read = 0;
                using (var fs = File.OpenRead(fullPath))
                {
                    read = fs.Read(header, 0, 3);
                }

                bool hasUtf16Bom = read >= 2 &&
                    ((header[0] == 0xFF && header[1] == 0xFE) || (header[0] == 0xFE && header[1] == 0xFF));
                bool hasUtf8Bom = read >= 3 && header[0] == 0xEF && header[1] == 0xBB && header[2] == 0xBF;
                if (hasUtf16Bom || hasUtf8Bom)
                {
                    string bomKind = hasUtf8Bom
                        ? "UTF-8 BOM (EF BB BF)"
                        : (header[0] == 0xFF ? "UTF-16LE BOM (FF FE)" : "UTF-16BE BOM (FE FF)");
                    diagnostics.Add(new(
                        DiagnosticSeverity.Error,
                        "ENC001",
                        $"Loose PoE asset '{virtualPath}' contains forbidden {bomKind}; .sm/.mat/.amd files must be BOM-less UTF-16LE CRLF.",
                        virtualPath));
                }
                continue;
            }

            if (!virtualPath.EndsWith(".ui", StringComparison.OrdinalIgnoreCase))
                continue;

            string uiContent = ReadTextAutoEncoding(fullPath);
            var uiDiagnostics = UiLayoutLinter.Lint(uiContent, virtualPath, nc.CustomControls, lowerAssetSet);
            diagnostics.AddRange(uiDiagnostics);
        }

        return new ValidationReport(diagnostics);
    }

    /// <summary>
    /// Detects collisions across a set of dual-side mods (duplicate IDs, overlapping replaced/overlay paths,
    /// custom opcodes, GEAL built-ins, custom UI controls, and content registry entries).
    /// </summary>
    public static IReadOnlyList<CrossModConflict> DetectConflicts(IEnumerable<DualModManifest> manifests)
    {
        var list = manifests.ToList();
        var conflicts = new List<CrossModConflict>();

        var modIds = new Dictionary<string, DualModManifest>(StringComparer.OrdinalIgnoreCase);
        var pathOwners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var opcodeOwners = new Dictionary<ushort, (string ModId, string Name)>();
        var gealOwners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var controlOwners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var registryOwners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var mod in list)
        {
            if (modIds.TryGetValue(mod.Id, out _))
            {
                conflicts.Add(new("CNF001", "ModId", mod.Id, mod.Id, mod.Id, $"Duplicate mod id '{mod.Id}'."));
                continue;
            }
            modIds[mod.Id] = mod;

            if (mod.NativeClient is not { } nc)
                continue;

            // Check both replaces and overlays for virtual path collisions
            var allPaths = nc.Replaces.Concat(nc.Overlays)
                .Select(NormalizeVirtualPath)
                .Distinct(StringComparer.OrdinalIgnoreCase);

            foreach (string path in allPaths)
            {
                if (pathOwners.TryGetValue(path, out string? existing))
                {
                    conflicts.Add(new("CNF002", "FilePath", path, existing, mod.Id,
                        $"Virtual path '{path}' is claimed by both '{existing}' and '{mod.Id}'."));
                }
                else
                {
                    pathOwners[path] = mod.Id;
                }
            }

            foreach (var op in nc.CustomOpcodes)
            {
                if (opcodeOwners.TryGetValue(op.Id, out var existing))
                {
                    conflicts.Add(new("CNF003", "CustomOpcode", $"0x{op.Id:X4} ({op.Name})", existing.ModId, mod.Id,
                        $"Custom opcode {op.Id} (0x{op.Id:X4}) collides between '{existing.ModId}' ({existing.Name}) and '{mod.Id}' ({op.Name})."));
                }
                else
                {
                    opcodeOwners[op.Id] = (mod.Id, op.Name);
                }
            }

            foreach (string builtin in nc.GealBuiltins)
            {
                if (string.IsNullOrWhiteSpace(builtin)) continue;
                if (gealOwners.TryGetValue(builtin, out string? existing))
                {
                    conflicts.Add(new("CNF004", "GealBuiltin", builtin, existing, mod.Id,
                        $"GEAL built-in '{builtin}' is registered by both '{existing}' and '{mod.Id}'."));
                }
                else
                {
                    gealOwners[builtin] = mod.Id;
                }
            }

            foreach (string control in nc.CustomControls)
            {
                if (string.IsNullOrWhiteSpace(control)) continue;
                if (controlOwners.TryGetValue(control, out string? existing))
                {
                    conflicts.Add(new("CNF005", "CustomControl", control, existing, mod.Id,
                        $"Custom UI control '{control}' is registered by both '{existing}' and '{mod.Id}'."));
                }
                else
                {
                    controlOwners[control] = mod.Id;
                }
            }

            foreach (var reg in nc.ContentRegistries)
            {
                string key = $"{reg.Table}:{reg.EntryId}";
                if (registryOwners.TryGetValue(key, out string? existing))
                {
                    conflicts.Add(new("CNF006", "ContentRegistry", key, existing, mod.Id,
                        $"Content registry entry '{key}' is declared by both '{existing}' and '{mod.Id}'."));
                }
                else
                {
                    registryOwners[key] = mod.Id;
                }
            }
        }

        return conflicts;
    }

    /// <summary>
    /// Validates a standalone <c>.datpatch.json</c> payload (JSON syntax, schema table membership,
    /// 22 verified <see cref="AppendableRegistryTables"/> for <c>append</c>, hard rejection of closed
    /// <c>Stats</c>/<c>ItemClasses</c> tables for <c>append</c>, and valid <c>allocateDeadStat</c> slots).
    /// </summary>
    public static ValidationReport ValidateDatPatchJson(
        string json,
        string? expectedModId = null,
        string? filePath = null)
    {
        var diagnostics = new List<ValidationDiagnostic>();
        ValidateDatPatchJsonCore(json, expectedModId, filePath, diagnostics);
        return new ValidationReport(diagnostics);
    }

    /// <summary>
    /// Detects cross-mod collisions across declarative <c>.datpatch.json</c> documents.
    /// Two mods appending distinct entry keys to the SAME <c>.datc64</c> table produce ZERO conflicts;
    /// conflicts are reported only for duplicate <c>(table, entryKey)</c> keys, colliding explicit
    /// <c>allocateDeadStat.preferredSlot</c> assignments, or exceeding the 30-slot dead-stat pool.
    /// </summary>
    public static IReadOnlyList<CrossModConflict> DetectDatPatchConflicts(IEnumerable<DatPatchDocument> patches)
    {
        ArgumentNullException.ThrowIfNull(patches);

        Dat64SchemaRegistry schemaRegistry = Dat64SchemaRegistry.LoadDefault329();
        var conflicts = new List<CrossModConflict>();
        var entryOwners = new Dictionary<(string Table, string Key), string>(TableKeyComparer.Instance);
        var deadSlotOwners = new Dictionary<int, (string ModId, string Key)>();
        int totalDeadStats = 0;
        string? firstDeadStatMod = null;

        foreach (DatPatchDocument patch in patches)
        {
            if (patch is null)
            {
                continue;
            }

            foreach (var (rawTableName, tablePatch) in patch.Tables)
            {
                if (tablePatch is null || tablePatch.Append.Count == 0)
                {
                    continue;
                }

                string canonicalTable = schemaRegistry.TryGetTable(rawTableName, out Dat64TableSchema? schema) && schema is not null
                    ? schema.Name
                    : rawTableName.Trim();

                foreach (DatAppendEntry entry in tablePatch.Append)
                {
                    if (string.IsNullOrWhiteSpace(entry.Key))
                    {
                        continue;
                    }

                    string trimmedKey = entry.Key.Trim();
                    var tuple = (canonicalTable, trimmedKey);
                    if (entryOwners.TryGetValue(tuple, out string? existingMod))
                    {
                        conflicts.Add(new CrossModConflict(
                            "CNF007",
                            "DatPatchEntry",
                            $"{canonicalTable}:{trimmedKey}",
                            existingMod,
                            patch.ModId,
                            $"Duplicate .datpatch.json entry ('{canonicalTable}', '{trimmedKey}') is declared by both '{existingMod}' and '{patch.ModId}'."));
                    }
                    else
                    {
                        entryOwners[tuple] = patch.ModId;
                    }
                }
            }

            foreach (DeadStatAllocationSpec deadSpec in patch.AllocateDeadStat)
            {
                totalDeadStats++;
                firstDeadStatMod ??= patch.ModId;

                if (!string.IsNullOrWhiteSpace(deadSpec.Key))
                {
                    string trimmedKey = deadSpec.Key.Trim();
                    var tuple = ("Stats", trimmedKey);
                    if (entryOwners.TryGetValue(tuple, out string? existingMod))
                    {
                        conflicts.Add(new CrossModConflict(
                            "CNF007",
                            "DatPatchEntry",
                            $"Stats:{trimmedKey}",
                            existingMod,
                            patch.ModId,
                            $"Duplicate dead-stat entry ('Stats', '{trimmedKey}') is declared by both '{existingMod}' and '{patch.ModId}'."));
                    }
                    else
                    {
                        entryOwners[tuple] = patch.ModId;
                    }
                }

                if (deadSpec.PreferredSlot is int slot)
                {
                    if (deadSlotOwners.TryGetValue(slot, out var existingSlot))
                    {
                        conflicts.Add(new CrossModConflict(
                            "CNF008",
                            "DeadStatSlot",
                            $"Stats[{slot}]",
                            existingSlot.ModId,
                            patch.ModId,
                            $"Dead-stat preferredSlot {slot} collides between '{existingSlot.ModId}' ('{existingSlot.Key}') and '{patch.ModId}' ('{deadSpec.Key}')."));
                    }
                    else
                    {
                        deadSlotOwners[slot] = (patch.ModId, deadSpec.Key);
                    }
                }
            }

            if (totalDeadStats > Dat64SchemaRegistry.DeadStatSlots.Count && firstDeadStatMod is not null)
            {
                conflicts.Add(new CrossModConflict(
                    "CNF009",
                    "DeadStatPool",
                    "Stats",
                    firstDeadStatMod,
                    patch.ModId,
                    $"Combined allocateDeadStat requests ({totalDeadStats}) exceed the {Dat64SchemaRegistry.DeadStatSlots.Count} verified dead Stats stock slots."));
                break;
            }
        }

        return conflicts;
    }

    private static DatPatchDocument? ValidateDatPatchJsonCore(
        string json,
        string? expectedModId,
        string? filePath,
        List<ValidationDiagnostic> diagnostics)
    {
        DatPatchDocument doc;
        try
        {
            doc = DatPatchDocument.Parse(json);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or ArgumentException)
        {
            diagnostics.Add(new(
                DiagnosticSeverity.Error,
                "DAT001",
                $"Invalid .datpatch.json syntax or schema: {ex.Message}",
                filePath));
            return null;
        }

        if (!string.IsNullOrWhiteSpace(expectedModId) &&
            !doc.ModId.Equals(expectedModId, StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(new(
                DiagnosticSeverity.Error,
                "DAT002",
                $".datpatch.json modId '{doc.ModId}' does not match manifest id '{expectedModId}'.",
                filePath));
        }

        Dat64SchemaRegistry schemaRegistry = Dat64SchemaRegistry.LoadDefault329();
        var seenEntries = new HashSet<(string Table, string Key)>(TableKeyComparer.Instance);
        var usedPreferredSlots = new HashSet<int>();

        foreach (var (rawTableName, tablePatch) in doc.Tables)
        {
            if (tablePatch is null)
            {
                continue;
            }

            if (!schemaRegistry.TryGetTable(rawTableName, out Dat64TableSchema? tableSchema) || tableSchema is null)
            {
                diagnostics.Add(new(
                    DiagnosticSeverity.Error,
                    "DAT003",
                    $".datpatch.json references unknown 3.29 .datc64 table '{rawTableName}'.",
                    filePath));
                continue;
            }

            string canonicalName = tableSchema.Name;

            if (tablePatch.Append.Count > 0)
            {
                if (ClosedRegistryTables.Contains(canonicalName))
                {
                    string closedMessage = canonicalName.Equals("Stats", StringComparison.OrdinalIgnoreCase)
                        ? "Table 'Stats' is closed in 3.29 (fixed 23,362 / 0x5b42 rows) and cannot accept 'append' entries. Use 'allocateDeadStat' (30 verified dead stock rows) or high-ID StatsComponent instead."
                        : $"Table '{canonicalName}' is closed (compiled enum in 3.29) and cannot accept 'append' entries.";
                    diagnostics.Add(new(DiagnosticSeverity.Error, "DAT004", closedMessage, filePath));
                }
                else if (!AppendableRegistryTables.Contains(canonicalName))
                {
                    diagnostics.Add(new(
                        DiagnosticSeverity.Error,
                        "DAT005",
                        $"Table '{canonicalName}' is not in the {AppendableRegistryTables.Count} verified AppendableRegistryTables and cannot accept 'append' entries.",
                        filePath));
                }

                foreach (DatAppendEntry entry in tablePatch.Append)
                {
                    if (string.IsNullOrWhiteSpace(entry.Key))
                    {
                        diagnostics.Add(new(
                            DiagnosticSeverity.Error,
                            "DAT006",
                            $"Append entry on table '{canonicalName}' must declare a non-empty 'key'.",
                            filePath));
                        continue;
                    }

                    if (!seenEntries.Add((canonicalName, entry.Key.Trim())))
                    {
                        diagnostics.Add(new(
                            DiagnosticSeverity.Error,
                            "DAT007",
                            $"Duplicate append entry key ('{canonicalName}', '{entry.Key}') in mod '{doc.ModId}'.",
                            filePath));
                    }
                }
            }

            if (tablePatch.Modify.Count > 0 && canonicalName.Equals("Stats", StringComparison.OrdinalIgnoreCase))
            {
                foreach (DatModifyEntry modEntry in tablePatch.Modify)
                {
                    if (modEntry.RowIndex == Dat64SchemaRegistry.ForbiddenDummyStatRow)
                    {
                        diagnostics.Add(new(
                            DiagnosticSeverity.Error,
                            "DAT008",
                            $"Modifying forbidden Stats row {Dat64SchemaRegistry.ForbiddenDummyStatRow} ('dummy_stat_display_nothing') is disallowed.",
                            filePath));
                    }

                    foreach (string colName in modEntry.Fields.Keys)
                    {
                        if (colName.Equals("Id", StringComparison.OrdinalIgnoreCase) ||
                            colName.Equals("HASH32", StringComparison.OrdinalIgnoreCase))
                        {
                            diagnostics.Add(new(
                                DiagnosticSeverity.Error,
                                "DAT008",
                                $"Modifying 'Stats.{colName}' is forbidden because stock Stats IDs/HASH32 must remain intact for Stat.Checksum compatibility.",
                                filePath));
                        }
                    }
                }
            }
        }

        if (doc.AllocateDeadStat.Count > Dat64SchemaRegistry.DeadStatSlots.Count)
        {
            diagnostics.Add(new(
                DiagnosticSeverity.Error,
                "DAT009",
                $"Mod '{doc.ModId}' requests {doc.AllocateDeadStat.Count} allocateDeadStat entries, exceeding the {Dat64SchemaRegistry.DeadStatSlots.Count} verified dead stock slots.",
                filePath));
        }

        foreach (DeadStatAllocationSpec deadSpec in doc.AllocateDeadStat)
        {
            if (string.IsNullOrWhiteSpace(deadSpec.Key))
            {
                diagnostics.Add(new(
                    DiagnosticSeverity.Error,
                    "DAT006",
                    "allocateDeadStat entry must declare a non-empty 'key'.",
                    filePath));
            }
            else if (!seenEntries.Add(("Stats", deadSpec.Key.Trim())))
            {
                diagnostics.Add(new(
                    DiagnosticSeverity.Error,
                    "DAT007",
                    $"Duplicate allocateDeadStat key ('Stats', '{deadSpec.Key}') in mod '{doc.ModId}'.",
                    filePath));
            }

            if (deadSpec.PreferredSlot is int slot)
            {
                if (slot == Dat64SchemaRegistry.ForbiddenDummyStatRow)
                {
                    diagnostics.Add(new(
                        DiagnosticSeverity.Error,
                        "DAT009",
                        $"allocateDeadStat '{deadSpec.Key}' requests forbidden Stats row {Dat64SchemaRegistry.ForbiddenDummyStatRow} ('dummy_stat_display_nothing').",
                        filePath));
                }
                else if (!Dat64SchemaRegistry.DeadStatSlotSet.Contains(slot))
                {
                    diagnostics.Add(new(
                        DiagnosticSeverity.Error,
                        "DAT009",
                        $"allocateDeadStat '{deadSpec.Key}' requests Stats row {slot}, which is not in the {Dat64SchemaRegistry.DeadStatSlots.Count} live-verified dead stock slots.",
                        filePath));
                }
                else if (!usedPreferredSlots.Add(slot))
                {
                    diagnostics.Add(new(
                        DiagnosticSeverity.Error,
                        "DAT009",
                        $"Duplicate allocateDeadStat preferredSlot {slot} within mod '{doc.ModId}'.",
                        filePath));
                }
            }

            foreach (string colName in deadSpec.Fields.Keys)
            {
                if (colName.Equals("Id", StringComparison.OrdinalIgnoreCase) ||
                    colName.Equals("HASH32", StringComparison.OrdinalIgnoreCase))
                {
                    diagnostics.Add(new(
                        DiagnosticSeverity.Error,
                        "DAT008",
                        $"allocateDeadStat '{deadSpec.Key}' cannot override 'Stats.{colName}'; stock Stats.Id must remain intact.",
                        filePath));
                }
            }
        }

        return doc;
    }

    private static void ValidateIntraModDatPatchCollisions(
        string modId,
        IReadOnlyList<DatPatchDocument> modPatches,
        List<ValidationDiagnostic> diagnostics)
    {
        IReadOnlyList<CrossModConflict> internalConflicts = DetectDatPatchConflicts(modPatches);
        foreach (CrossModConflict conflict in internalConflicts)
        {
            string code = conflict.Code == "CNF007" ? "DAT007" : "DAT009";
            diagnostics.Add(new(
                DiagnosticSeverity.Error,
                code,
                $"Intra-mod .datpatch.json collision in '{modId}': {conflict.Message}"));
        }
    }

    private sealed class TableKeyComparer : IEqualityComparer<(string Table, string Key)>
    {
        public static readonly TableKeyComparer Instance = new();

        public bool Equals((string Table, string Key) x, (string Table, string Key) y) =>
            string.Equals(x.Table, y.Table, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.Key, y.Key, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Table, string Key) obj) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Table),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Key));
    }

    /// <summary>
    /// Returns true if a virtual path is properly namespaced under a <c>Mods/&lt;segment&gt;/</c> path
    /// matching the mod id (or its final hyphen-separated component, e.g. <c>harvest-garden</c> -> <c>Mods/Garden/</c> or <c>Mods/harvest-garden/</c>).
    /// </summary>
    public static bool IsModNamespacedPath(string virtualPath, string modId)
    {
        string normalized = NormalizeVirtualPath(virtualPath);
        string[] parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);

        var allowedNamespaces = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { modId };
        // Also allow the short namespace suffix for hyphenated ids (e.g. "harvest-garden" -> "garden")
        int dash = modId.LastIndexOf('-');
        if (dash >= 0 && dash + 1 < modId.Length)
            allowedNamespaces.Add(modId[(dash + 1)..]);

        for (int i = 0; i < parts.Length - 2; i++)
        {
            if (parts[i].Equals("mods", StringComparison.OrdinalIgnoreCase) &&
                allowedNamespaces.Contains(parts[i + 1]))
            {
                return true;
            }
        }

        return false;
    }

    public static string NormalizeVirtualPath(string path) =>
        path.Replace('\\', '/').Trim().TrimStart('/');

    public static bool IsSafeRelativeVirtualPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains(':'))
            return false;

        string normalized = path.Replace('\\', '/');
        if (normalized.StartsWith('/'))
            return false;

        string[] parts = normalized.Split('/');
        return parts.Length > 0 && parts.All(p => p is not ("" or "." or ".."));
    }

    public static bool IsPhysicalPath(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                return false;
            }
        }
        return true;
    }

    public static string ComputeFileSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    /// <summary>Reads a text file from disk, auto-detecting UTF-16LE/BE (with or without BOM) or UTF-8 and stripping leading BOM characters.</summary>
    public static string ReadTextAutoEncoding(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        string text;
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            text = System.Text.Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        }
        else if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            text = System.Text.Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        }
        else if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            text = System.Text.Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }
        else if (bytes.Length >= 2 && (bytes.Length & 1) == 0 && bytes.AsSpan().IndexOf((byte)0) >= 0)
        {
            int evenZeros = 0;
            int oddZeros = 0;
            for (int i = 0; i < bytes.Length; i += 2)
            {
                if (bytes[i] == 0) evenZeros++;
                if (bytes[i + 1] == 0) oddZeros++;
            }

            if (evenZeros > oddZeros)
            {
                string beText = System.Text.Encoding.BigEndianUnicode.GetString(bytes);
                if (ContainsUnexpectedAsciiControlChar(beText))
                {
                    string leText = System.Text.Encoding.Unicode.GetString(bytes);
                    text = !ContainsUnexpectedAsciiControlChar(leText) ? leText : beText;
                }
                else
                {
                    text = beText;
                }
            }
            else
            {
                text = System.Text.Encoding.Unicode.GetString(bytes);
            }
        }
        else
        {
            text = System.Text.Encoding.UTF8.GetString(bytes);
        }

        return text.TrimStart('\uFEFF');
    }

    private static bool ContainsUnexpectedAsciiControlChar(string text)
    {
        foreach (char c in text)
        {
            if ((c >= '\x01' && c <= '\x08') || (c >= '\x0B' && c <= '\x0C') || (c >= '\x0E' && c <= '\x1F'))
            {
                return true;
            }
        }

        return false;
    }

    private static void ValidateNativeClientSection(
        DualModManifest manifest,
        NativeClientManifest nc,
        SemVer? currentSdkVersion,
        List<ValidationDiagnostic> diagnostics)
    {
        if (!SemVerRange.TryParse(nc.SdkVersionRange, out var range) || range is null)
        {
            diagnostics.Add(new(DiagnosticSeverity.Error, "NC001",
                $"Invalid nativeClient.sdkVersionRange '{nc.SdkVersionRange}'."));
        }
        else if (currentSdkVersion is { } sdkVer && !range.IsSatisfiedBy(sdkVer))
        {
            diagnostics.Add(new(DiagnosticSeverity.Error, "NC002",
                $"Mod '{manifest.Id}' requires SDK version '{nc.SdkVersionRange}', which is incompatible with installed SDK '{sdkVer}'."));
        }

        if (nc.SupportedBuilds.Count == 0)
        {
            diagnostics.Add(new(DiagnosticSeverity.Warning, "NC003",
                "nativeClient.supportedBuilds is empty; expected 3.29.3d and/or 3.29.3c."));
        }
        else
        {
            foreach (string build in nc.SupportedBuilds)
            {
                if (!SupportedClientBuilds.Contains(build))
                {
                    diagnostics.Add(new(DiagnosticSeverity.Error, "NC004",
                        $"Unsupported client build '{build}'. NativeMods targets 3.29.3c and 3.29.3d only."));
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(nc.Assembly) && !DualModManifest.IsSafeAssemblyFileName(nc.Assembly))
        {
            diagnostics.Add(new(DiagnosticSeverity.Error, "NC005",
                $"Invalid nativeClient.assembly '{nc.Assembly}'. Must be a single .dll filename with no path separators."));
        }

        if (!string.IsNullOrWhiteSpace(nc.AssemblySha256) && !DualModManifest.IsValidSha256(nc.AssemblySha256))
        {
            diagnostics.Add(new(DiagnosticSeverity.Error, "NC006",
                "nativeClient.assemblySha256 must be a 64-character hex SHA-256 string."));
        }

        if (!string.IsNullOrWhiteSpace(nc.BundleSha256) && !DualModManifest.IsValidSha256(nc.BundleSha256))
        {
            diagnostics.Add(new(DiagnosticSeverity.Error, "NC007",
                "nativeClient.bundleSha256 must be a 64-character hex SHA-256 string."));
        }

        if (nc.RawAccess)
        {
            if (string.IsNullOrWhiteSpace(nc.RawAccessJustification))
            {
                diagnostics.Add(new(DiagnosticSeverity.Error, "NC008",
                    "nativeClient.rawAccess is true, so nativeClient.rawAccessJustification must explain why Raw memory/hook access is needed."));
            }
            else
            {
                diagnostics.Add(new(DiagnosticSeverity.Warning, "NC009",
                    $"Mod '{manifest.Id}' requests Raw memory/hook access: {nc.RawAccessJustification}"));
            }
        }

        foreach (string path in nc.Overlays)
        {
            if (!IsSafeRelativeVirtualPath(path))
                diagnostics.Add(new(DiagnosticSeverity.Error, "NC010", $"Unsafe overlay virtual path '{path}'.", path));
        }

        foreach (string path in nc.Replaces)
        {
            if (!IsSafeRelativeVirtualPath(path))
                diagnostics.Add(new(DiagnosticSeverity.Error, "NC011", $"Unsafe replaces virtual path '{path}'.", path));
        }

        foreach (string patchPath in nc.DatPatches)
        {
            if (!IsSafeRelativeVirtualPath(patchPath))
            {
                diagnostics.Add(new(DiagnosticSeverity.Error, "NC022", $"Unsafe datPatches relative path '{patchPath}'.", patchPath));
            }
            else if (!patchPath.EndsWith(".datpatch.json", StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(new(DiagnosticSeverity.Error, "NC023",
                    $"Invalid datPatches entry '{patchPath}'. Declared dat patches must use the '.datpatch.json' extension.", patchPath));
            }
        }

        var seenOpcodes = new HashSet<ushort>();
        foreach (var op in nc.CustomOpcodes)
        {
            if (op.Id == 0)
            {
                diagnostics.Add(new(DiagnosticSeverity.Error, "NC012",
                    $"Custom opcode '{op.Name}' cannot use reserved id 0."));
            }
            else if (!seenOpcodes.Add(op.Id))
            {
                diagnostics.Add(new(DiagnosticSeverity.Error, "NC013",
                    $"Duplicate custom opcode id {op.Id} (0x{op.Id:X4}) within mod '{manifest.Id}'."));
            }

            if (string.IsNullOrWhiteSpace(op.Name))
            {
                diagnostics.Add(new(DiagnosticSeverity.Error, "NC014",
                    $"Custom opcode {op.Id} must declare a non-empty name."));
            }
        }

        foreach (var reg in nc.ContentRegistries)
        {
            if (string.IsNullOrWhiteSpace(reg.Table) || string.IsNullOrWhiteSpace(reg.EntryId))
            {
                diagnostics.Add(new(DiagnosticSeverity.Error, "NC015",
                    "Content registry entries must specify both 'table' and 'entryId'."));
                continue;
            }

            if (ClosedRegistryTables.Contains(reg.Table))
            {
                string closedMessage = reg.Table.Equals("Stats", StringComparison.OrdinalIgnoreCase)
                    ? "Content registry table 'Stats' is closed in 3.29 (row count must remain 23,362 / 0x5b42 because 0x5b43 is compiled StatId::INVALID across 35+ sites and 4 fixed-size tables OOB on statId >= 0x5b43). " +
                      "Use in-place non-ID column edits, Phase 3 dead-slot allocation (30 unused old_do_not_use_*/currently_unused_* stock rows keeping stock Id + Route A Metadata/StatDescriptions/*.txt overlays), or unbounded high-ID StatsComponent (0x1b3 varint) entity stats instead."
                    : $"Content registry table '{reg.Table}' is closed (compiled enum in 3.29 binary) and cannot accept appended rows.";
                diagnostics.Add(new(DiagnosticSeverity.Error, "NC016", closedMessage));
            }
            else if (!AppendableRegistryTables.Contains(reg.Table))
            {
                diagnostics.Add(new(DiagnosticSeverity.Warning, "NC018",
                    $"Content registry table '{reg.Table}' has not yet been verified appendable in 3.29."));
            }

            if (!reg.Mode.Equals("append", StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(new(DiagnosticSeverity.Error, "NC019",
                    $"Content registry mode '{reg.Mode}' is unsupported; 3.29 tables only allow 'append' (never in-place insertion)."));
            }
        }

        if (nc.ServerHalf is { } sh)
        {
            if (!DualModManifest.IsValidId(sh.Id))
                diagnostics.Add(new(DiagnosticSeverity.Error, "NC020", $"Invalid serverHalf.id '{sh.Id}'."));
            if (!SemVer.TryParse(sh.MinVersion, out _))
                diagnostics.Add(new(DiagnosticSeverity.Error, "NC021", $"Invalid serverHalf.minVersion '{sh.MinVersion}'."));
        }
    }

    private static void VerifyFileHash(string fullPath, string expectedSha256, string displayPath, string code, List<ValidationDiagnostic> diagnostics)
    {
        if (!DualModManifest.IsValidSha256(expectedSha256))
        {
            diagnostics.Add(new(DiagnosticSeverity.Error, code, $"Malformed expected SHA-256 for '{displayPath}'.", displayPath));
            return;
        }

        string actual = ComputeFileSha256(fullPath);
        if (!actual.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(new(DiagnosticSeverity.Error, code,
                $"SHA-256 mismatch for '{displayPath}': expected {expectedSha256.ToLowerInvariant()}, actual {actual}.", displayPath));
        }
    }
}
