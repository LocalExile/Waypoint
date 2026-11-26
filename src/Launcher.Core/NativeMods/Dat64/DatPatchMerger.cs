using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocalExile.Client.AssetPipeline.Dat64;

/// <summary>
/// Abstraction for loading stock baseline <c>.datc64</c> table bytes by logical table name (e.g. <c>"BaseItemTypes"</c>).
/// </summary>
public interface IDat64Source
{
    bool TryReadTable(string tableName, out byte[] bytes);
}

/// <summary>
/// In-memory <see cref="IDat64Source"/> backed by a table-name dictionary (ideal for tests and programmatic merging).
/// </summary>
public sealed class InMemoryDat64Source : IDat64Source
{
    private readonly Dictionary<string, byte[]> _tables;

    public InMemoryDat64Source(IReadOnlyDictionary<string, byte[]> tables)
    {
        ArgumentNullException.ThrowIfNull(tables);
        _tables = new Dictionary<string, byte[]>(tables, StringComparer.OrdinalIgnoreCase);
    }

    public bool TryReadTable(string tableName, out byte[] bytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        if (_tables.TryGetValue(tableName, out byte[]? found))
        {
            bytes = found;
            return true;
        }

        bytes = [];
        return false;
    }
}

/// <summary>
/// Directory-backed <see cref="IDat64Source"/> supporting loose <c>Data/&lt;Table&gt;.datc64</c>,
/// direct <c>&lt;Table&gt;.datc64</c>, or live read-only extraction from PoE 3.29 <c>Bundles2/_.index.bin</c>.
/// </summary>
public sealed class DirectoryDat64Source : IDat64Source, IDisposable
{
    private readonly string _rootDir;
    private Bundles2Reader? _bundles;

    public DirectoryDat64Source(string rootDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDir);
        _rootDir = rootDir;
    }

    public bool TryReadTable(string tableName, out byte[] bytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        string fileName = tableName.EndsWith(".datc64", StringComparison.OrdinalIgnoreCase)
            ? tableName
            : tableName + ".datc64";

        string looseDataPath = Path.Combine(_rootDir, "Data", fileName);
        if (File.Exists(looseDataPath))
        {
            bytes = File.ReadAllBytes(looseDataPath);
            return true;
        }

        string directPath = Path.Combine(_rootDir, fileName);
        if (File.Exists(directPath))
        {
            bytes = File.ReadAllBytes(directPath);
            return true;
        }

        string indexPath = Path.Combine(_rootDir, "Bundles2", "_.index.bin");
        if (File.Exists(indexPath))
        {
            _bundles ??= Bundles2Reader.Open(_rootDir);
            if (_bundles.TryReadFile($"Data/{fileName}", out bytes))
            {
                return true;
            }
        }

        bytes = [];
        return false;
    }

    public void Dispose()
    {
        _bundles?.Dispose();
        _bundles = null;
    }
}

/// <summary>
/// Recorded metadata for a single merged content entry in <c>content-row-map.json</c>.
/// </summary>
public sealed record ContentRowMapEntry
{
    public required string ModId { get; init; }
    public required int RowIndex { get; init; }
    public uint? Hash32 { get; init; }
    public ushort? Hash16 { get; init; }
    public string? StockId { get; init; }
}

/// <summary>
/// Per-table summary and symbolic entry mapping recorded in <c>content-row-map.json</c>.
/// </summary>
public sealed record ContentTableRowMap
{
    public required int StockRowCount { get; init; }
    public required int MergedRowCount { get; init; }
    public required string TableSha256 { get; init; }
    public required IReadOnlyDictionary<string, ContentRowMapEntry> Entries { get; init; }
}

/// <summary>
/// Metadata for a generated Route A text overlay (e.g. <c>Metadata/StatDescriptions/&lt;mod&gt;_custom_stats.txt</c>).
/// </summary>
public sealed record GeneratedOverlayEntry
{
    public required string VirtualPath { get; init; }
    public required string Sha256 { get; init; }
}

/// <summary>
/// Deterministic <c>content-row-map.json</c> manifest + SHA-256 handshake digest emitted by <see cref="DatPatchMerger"/>
/// so client and server can verify exact row-ID and hash parity at handshake.
/// </summary>
public sealed record ContentRowMap
{
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly JsonSerializerOptions ReadJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public required string Digest { get; init; }
    public required IReadOnlyList<string> Mods { get; init; }
    public required IReadOnlyDictionary<string, ContentTableRowMap> Tables { get; init; }
    public required IReadOnlyList<GeneratedOverlayEntry> GeneratedOverlays { get; init; }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static ContentRowMap Parse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        return JsonSerializer.Deserialize<ContentRowMap>(json, ReadJsonOptions)
            ?? throw new InvalidDataException("Failed to deserialize content-row-map.json.");
    }
}

/// <summary>
/// Complete output of a multi-mod <see cref="DatPatchMerger"/> run.
/// </summary>
public sealed record DatPatchMergeResult(
    IReadOnlyDictionary<string, byte[]> MergedTables,
    IReadOnlyDictionary<string, byte[]> GeneratedOverlays,
    ContentRowMap RowMap,
    string RowMapJson);

/// <summary>
/// Schema-driven multi-mod <c>.datc64</c> delta merger.
/// Deterministically merges append/modify/allocateDeadStat patches from multiple mods into a single
/// conflict-free set of <c>.datc64</c> binaries, Route A <c>Metadata/StatDescriptions/*.txt</c> overlays,
/// and a SHA-256-digested <c>content-row-map.json</c>.
/// </summary>
public static class DatPatchMerger
{
    private const string RefPrefix = "$ref:";

    private static readonly HashSet<string> UniqueHash32Tables = new(StringComparer.OrdinalIgnoreCase)
    {
        "BaseItemTypes",
        "Words",
        "MtxTypes",
        "Mods",
    };

    private static readonly HashSet<string> ForbiddenStatsModifyColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        "Id",
        "HASH32",
    };

    /// <summary>
    /// Merges an ordered/unordered set of <see cref="DatPatchDocument"/> patches against stock <c>.datc64</c>
    /// tables and optionally writes the <c>Mods/Client/.merged-cache/</c> output directory layout.
    /// </summary>
    public static DatPatchMergeResult Merge(
        IDat64Source source,
        IReadOnlyList<DatPatchDocument> patches,
        Dat64SchemaRegistry? schemaRegistry = null,
        string? outputDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(patches);

        Dat64SchemaRegistry registry = schemaRegistry ?? Dat64SchemaRegistry.LoadDefault329();

        // 1. Deterministic topological sort by Declared Depends + OrdinalIgnoreCase ModId tie-breaker.
        List<DatPatchDocument> orderedMods = SortModsDeterministically(patches);

        // 2. Policy & schema pre-validation across all mods before touching any bytes.
        var touchedTableNames = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenEntryKeys = new Dictionary<(string Table, string Key), string>(TupleKeyComparer.Instance);
        int totalDeadStatRequests = 0;

        foreach (DatPatchDocument mod in orderedMods)
        {
            if (mod.AllocateDeadStat.Count > 0)
            {
                touchedTableNames.Add("Stats");
                totalDeadStatRequests += mod.AllocateDeadStat.Count;

                foreach (DeadStatAllocationSpec deadSpec in mod.AllocateDeadStat)
                {
                    if (string.IsNullOrWhiteSpace(deadSpec.Key))
                    {
                        throw new InvalidOperationException($"Mod '{mod.ModId}' contains an allocateDeadStat entry with an empty key.");
                    }

                    var tupleKey = ("Stats", deadSpec.Key.Trim());
                    if (seenEntryKeys.TryGetValue(tupleKey, out string? existingMod))
                    {
                        throw new InvalidOperationException(
                            $"Duplicate (table, entryKey) conflict for ('Stats', '{deadSpec.Key}'): declared by both '{existingMod}' and '{mod.ModId}'.");
                    }

                    seenEntryKeys[tupleKey] = mod.ModId;
                }
            }

            foreach (var (rawTableName, tablePatch) in mod.Tables)
            {
                if (tablePatch is null)
                {
                    continue;
                }

                Dat64TableSchema tableSchema = registry.GetRequiredTable(rawTableName);
                string canonicalName = tableSchema.Name;

                if (tablePatch.Append.Count > 0)
                {
                    ValidateTableAppendable(canonicalName, mod.ModId);

                    foreach (DatAppendEntry entry in tablePatch.Append)
                    {
                        if (string.IsNullOrWhiteSpace(entry.Key))
                        {
                            throw new InvalidOperationException(
                                $"Mod '{mod.ModId}' contains an append entry on table '{canonicalName}' with an empty key.");
                        }

                        var tupleKey = (canonicalName, entry.Key.Trim());
                        if (seenEntryKeys.TryGetValue(tupleKey, out string? existingMod))
                        {
                            throw new InvalidOperationException(
                                $"Duplicate (table, entryKey) conflict for ('{canonicalName}', '{entry.Key}'): declared by both '{existingMod}' and '{mod.ModId}'.");
                        }

                        seenEntryKeys[tupleKey] = mod.ModId;
                    }
                }

                if (tablePatch.Append.Count > 0 || tablePatch.Modify.Count > 0)
                {
                    touchedTableNames.Add(canonicalName);
                }
            }
        }

        if (totalDeadStatRequests > Dat64SchemaRegistry.DeadStatSlots.Count)
        {
            throw new InvalidOperationException(
                $"Dead stat allocation limit exceeded: mods requested {totalDeadStatRequests} custom Stats slots, but only {Dat64SchemaRegistry.DeadStatSlots.Count} verified dead stock rows exist in Stats.datc64. " +
                "Use unbounded high-ID StatsComponent (>= 0x10000) for additional entity stats.");
        }

        // 3. Load stock tables & validate row strides.
        var tableStates = new Dictionary<string, TableMergeState>(StringComparer.OrdinalIgnoreCase);
        foreach (string tableName in touchedTableNames)
        {
            LoadTableState(tableName, source, registry, tableStates);
        }

        // Auxiliary read-only lookup cache for referenced tables (e.g., resolving CurrencyItems Id via BaseItemTypes).
        var auxReadTables = new Dictionary<string, Dat64Table>(StringComparer.OrdinalIgnoreCase);

        // 4. Pass 1 — Reserve row indices for all dead-stat allocations & all append entries (enables forward/mutual $ref).
        var reservedSymbols = new Dictionary<(string Table, string Key), ReservedRow>(TupleKeyComparer.Instance);
        var allocatedDeadSlots = new List<(DatPatchDocument Mod, DeadStatAllocationSpec Spec, int RowIndex, string StockId)>();

        if (totalDeadStatRequests > 0)
        {
            TableMergeState statsState = tableStates["Stats"];
            var usedDeadSlots = new HashSet<int>();

            // First validate and reserve any explicit PreferredSlot requests.
            var assignedSlots = new Dictionary<(string ModId, string Key), int>();
            foreach (DatPatchDocument mod in orderedMods)
            {
                foreach (DeadStatAllocationSpec spec in mod.AllocateDeadStat)
                {
                    if (!spec.PreferredSlot.HasValue)
                    {
                        continue;
                    }

                    int slot = spec.PreferredSlot.Value;
                    if (slot == Dat64SchemaRegistry.ForbiddenDummyStatRow)
                    {
                        throw new InvalidOperationException(
                            $"Mod '{mod.ModId}' requested forbidden Stats row {Dat64SchemaRegistry.ForbiddenDummyStatRow} ('dummy_stat_display_nothing', referenced by 375 stock mods) for dead stat '{spec.Key}'.");
                    }

                    if (!Dat64SchemaRegistry.DeadStatSlotSet.Contains(slot))
                    {
                        throw new InvalidOperationException(
                            $"Mod '{mod.ModId}' requested Stats row {slot} for dead stat '{spec.Key}', which is not in the 30 live-verified dead stock slots.");
                    }

                    if (!usedDeadSlots.Add(slot))
                    {
                        throw new InvalidOperationException(
                            $"Dead stat slot {slot} collision: requested by mod '{mod.ModId}' ('{spec.Key}') after already being allocated.");
                    }

                    assignedSlots[(mod.ModId, spec.Key.Trim())] = slot;
                }
            }

            // Deterministically assign remaining dead stat allocations from the 30-slot pool in ascending verified order.
            int poolCursor = 0;
            foreach (DatPatchDocument mod in orderedMods)
            {
                foreach (DeadStatAllocationSpec spec in mod.AllocateDeadStat)
                {
                    string key = spec.Key.Trim();
                    int slot;
                    if (!assignedSlots.TryGetValue((mod.ModId, key), out slot))
                    {
                        while (poolCursor < Dat64SchemaRegistry.DeadStatSlots.Count &&
                               usedDeadSlots.Contains(Dat64SchemaRegistry.DeadStatSlots[poolCursor]))
                        {
                            poolCursor++;
                        }

                        if (poolCursor >= Dat64SchemaRegistry.DeadStatSlots.Count)
                        {
                            throw new InvalidOperationException(
                                $"Dead stat slot pool exhausted while allocating '{key}' for mod '{mod.ModId}' (max {Dat64SchemaRegistry.DeadStatSlots.Count}).");
                        }

                        slot = Dat64SchemaRegistry.DeadStatSlots[poolCursor++];
                        usedDeadSlots.Add(slot);
                    }

                    if (slot < 0 || slot >= statsState.StockTable.RowCount)
                    {
                        throw new InvalidOperationException(
                            $"Allocated dead Stats row {slot} exceeds stock Stats.datc64 row count ({statsState.StockTable.RowCount}).");
                    }

                    string stockId = statsState.StockTable.ReadString(slot, statsState.Schema.PrimaryIdColumn!.Offset);
                    allocatedDeadSlots.Add((mod, spec, slot, stockId));
                    reservedSymbols[("Stats", key)] = new ReservedRow(mod.ModId, "Stats", key, slot, stockId);
                }
            }
        }

        // Reserve row indices for every appended entry across all mods (deterministic mod -> canonical table -> entry order).
        var stagedAppends = new List<(DatPatchDocument Mod, TableMergeState State, DatAppendEntry Entry, int ReservedRowIndex)>();
        foreach (DatPatchDocument mod in orderedMods)
        {
            foreach (string rawTableName in mod.Tables.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
            {
                DatTablePatch? tablePatch = mod.Tables[rawTableName];
                if (tablePatch is null || tablePatch.Append.Count == 0)
                {
                    continue;
                }

                Dat64TableSchema schema = registry.GetRequiredTable(rawTableName);
                TableMergeState state = tableStates[schema.Name];

                foreach (DatAppendEntry entry in tablePatch.Append)
                {
                    int newRowIndex = state.NextReservedRowIndex++;
                    if (state.NextReservedRowIndex > Dat64SchemaRegistry.MaxTotalRows)
                    {
                        throw new InvalidOperationException(
                            $"Table '{schema.Name}' exceeds PoE 3.29's u16 index ceiling of {Dat64SchemaRegistry.MaxTotalRows} total rows while appending '{entry.Key}' for mod '{mod.ModId}'.");
                    }

                    string key = entry.Key.Trim();
                    reservedSymbols[(schema.Name, key)] = new ReservedRow(mod.ModId, schema.Name, key, newRowIndex, StockId: null);
                    stagedAppends.Add((mod, state, entry, newRowIndex));
                }
            }
        }

        // 5. Pass 2a — Apply allocated dead stats on Stats.datc64 & stage Route A StatDescriptions overlays.
        var statDescriptionBlocksByPath = new SortedDictionary<string, List<(string StockId, DeadStatDescriptionSpec Spec)>>(StringComparer.OrdinalIgnoreCase);

        foreach (var (mod, spec, rowIndex, stockId) in allocatedDeadSlots)
        {
            TableMergeState statsState = tableStates["Stats"];
            byte[] rowBytes = statsState.Patcher.CloneRow(rowIndex);

            ApplyDeadStatNonIdOverrides(statsState, rowBytes, mod.ModId, spec, reservedSymbols);
            statsState.Patcher.UpdateRow(rowIndex, rowBytes);

            statsState.RowMapEntries[spec.Key.Trim()] = new ContentRowMapEntry
            {
                ModId = mod.ModId,
                RowIndex = rowIndex,
                StockId = stockId,
            };

            if (spec.Description is not null)
            {
                string virtualPath = ResolveStatDescriptionOutputPath(mod.ModId, spec.OutputPath);
                if (!statDescriptionBlocksByPath.TryGetValue(virtualPath, out var list))
                {
                    list = [];
                    statDescriptionBlocksByPath[virtualPath] = list;
                }

                list.Add((stockId, spec.Description));
            }
        }

        // 6. Pass 2b — Apply in-place modify entries on existing stock rows.
        foreach (DatPatchDocument mod in orderedMods)
        {
            foreach (string rawTableName in mod.Tables.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
            {
                DatTablePatch? tablePatch = mod.Tables[rawTableName];
                if (tablePatch is null || tablePatch.Modify.Count == 0)
                {
                    continue;
                }

                Dat64TableSchema schema = registry.GetRequiredTable(rawTableName);
                TableMergeState state = tableStates[schema.Name];

                foreach (DatModifyEntry modEntry in tablePatch.Modify)
                {
                    int targetRow = ResolveExistingStockRow(state, modEntry.RowIndex, modEntry.Id, mod.ModId, source, registry, tableStates, auxReadTables);
                    if (schema.Name.Equals("Stats", StringComparison.OrdinalIgnoreCase) &&
                        targetRow == Dat64SchemaRegistry.ForbiddenDummyStatRow)
                    {
                        throw new InvalidOperationException(
                            $"Mod '{mod.ModId}' attempted to modify forbidden Stats row {Dat64SchemaRegistry.ForbiddenDummyStatRow} ('dummy_stat_display_nothing').");
                    }

                    byte[] rowBytes = state.Patcher.CloneRow(targetRow);
                    ApplyFields(
                        state,
                        rowBytes,
                        modEntry.Fields,
                        mod.ModId,
                        entryContext: $"modify row {targetRow}",
                        reservedSymbols,
                        forbidStatsId: schema.Name.Equals("Stats", StringComparison.OrdinalIgnoreCase));

                    state.Patcher.UpdateRow(targetRow, rowBytes);
                    state.ModifiedRows.Add(targetRow);
                }
            }
        }

        // 7. Pass 2c — Apply append entries in exact reserved order, deep-copying variable slices & auto-allocating hashes.
        foreach (var (mod, state, entry, reservedRowIndex) in stagedAppends)
        {
            byte[] rowBytes;
            if (entry.CloneFromRow.HasValue || !string.IsNullOrWhiteSpace(entry.CloneFromId))
            {
                int templateRow = ResolveExistingStockRow(
                    state,
                    entry.CloneFromRow,
                    entry.CloneFromId,
                    mod.ModId,
                    source,
                    registry,
                    tableStates,
                    auxReadTables);
                rowBytes = state.Patcher.CloneRowDeep(templateRow, state.Schema);
            }
            else
            {
                rowBytes = state.Patcher.CreateBlankRow(state.Schema);
            }

            HashSet<int> explicitlyWrittenOffsets = ApplyFields(
                state,
                rowBytes,
                entry.Fields,
                mod.ModId,
                entryContext: $"append '{entry.Key}'",
                reservedSymbols,
                forbidStatsId: false);

            // Auto-compute HASH32 when present and not explicitly provided.
            uint? computedHash32 = null;
            if (state.Schema.Hash32Column is { } h32Col)
            {
                uint hash32;
                if (explicitlyWrittenOffsets.Contains(h32Col.Offset))
                {
                    hash32 = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(rowBytes.AsSpan(h32Col.Offset, 4));
                }
                else
                {
                    hash32 = ComputeAutoHash32(state, rowBytes, entry.Key, tableStates);
                    Dat64TablePatcher.WriteUInt32(rowBytes, h32Col.Offset, hash32);
                }

                if (UniqueHash32Tables.Contains(state.Schema.Name) && !state.UsedHash32s.Add(hash32))
                {
                    throw new InvalidOperationException(
                        $"{state.Schema.Name}.HASH32 collision (0x{hash32:X8}) while appending '{entry.Key}' for mod '{mod.ModId}'.");
                }

                computedHash32 = hash32;
            }

            // Auto-allocate collision-free non-zero HASH16 when present.
            ushort? computedHash16 = null;
            if (state.Schema.Hash16Column is { } h16Col)
            {
                ushort hash16;
                if (explicitlyWrittenOffsets.Contains(h16Col.Offset))
                {
                    hash16 = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(rowBytes.AsSpan(h16Col.Offset, 2));
                    if (hash16 == 0)
                    {
                        throw new InvalidOperationException(
                            $"{state.Schema.Name}.HASH16 cannot be 0 for '{entry.Key}' in mod '{mod.ModId}'.");
                    }

                    if (!state.UsedHash16s.Add(hash16))
                    {
                        throw new InvalidOperationException(
                            $"{state.Schema.Name}.HASH16 collision (0x{hash16:X4}) for '{entry.Key}' in mod '{mod.ModId}'.");
                    }
                }
                else
                {
                    string seed = ReadPrimaryStringOrFallback(state, rowBytes, $"{state.Schema.Name}:{entry.Key}");
                    hash16 = AllocateUniqueHash16(seed, state.UsedHash16s, state.Schema.Name);
                    Dat64TablePatcher.WriteUInt16(rowBytes, h16Col.Offset, hash16);
                }

                computedHash16 = hash16;
            }

            // Enforce unique MtxTypes.__col18 (offset 137) on MtxTypes appends.
            if (state.Schema.Name.Equals("MtxTypes", StringComparison.OrdinalIgnoreCase))
            {
                ulong shortIdOff = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(
                    rowBytes.AsSpan(Dat64SchemaRegistry.MtxTypesShortIdOffset, 8));
                string shortId = ReadStringFromPatcherSnapshot(state.Patcher, shortIdOff);
                if (!string.IsNullOrEmpty(shortId) && !state.UsedMtxShortIds.Add(shortId))
                {
                    throw new InvalidOperationException(
                        $"MtxTypes.__col18 ('{shortId}') collides with an existing row while appending '{entry.Key}' for mod '{mod.ModId}'. " +
                        "Provide a unique '__col18' string when appending to MtxTypes.");
                }
            }

            // Enforce primary unique column constraints (e.g. BaseItemTypes.Id, CurrencyItems.BaseItemTypesKey, ModType.Name).
            EnforceUniqueColumnsOnAppend(state, rowBytes, mod.ModId, entry.Key);

            int actualRowIndex = state.Patcher.AppendRow(rowBytes);
            if (actualRowIndex != reservedRowIndex)
            {
                throw new InvalidOperationException(
                    $"Internal row-reservation mismatch on '{state.Schema.Name}' for '{entry.Key}': reserved {reservedRowIndex}, appended {actualRowIndex}.");
            }

            state.RowMapEntries[entry.Key.Trim()] = new ContentRowMapEntry
            {
                ModId = mod.ModId,
                RowIndex = actualRowIndex,
                Hash32 = computedHash32,
                Hash16 = computedHash16,
                StockId = null,
            };
        }

        // 8. Finalize, round-trip verify, and run schema-wide zero-aliasing verification on every merged table.
        var mergedTableBytes = new SortedDictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var rowMapTables = new SortedDictionary<string, ContentTableRowMap>(StringComparer.Ordinal);

        foreach (string tableName in touchedTableNames.OrderBy(n => n, StringComparer.Ordinal))
        {
            TableMergeState state = tableStates[tableName];
            Dat64Table finalTable = state.Patcher.BuildAndVerify();

            if (state.Schema.Name.Equals("Stats", StringComparison.OrdinalIgnoreCase) &&
                finalTable.RowCount != state.StockTable.RowCount)
            {
                throw new InvalidOperationException(
                    $"Closed table invariant violation: Stats.datc64 row count changed from {state.StockTable.RowCount} to {finalTable.RowCount}.");
            }

            if (finalTable.RowCount > Dat64SchemaRegistry.MaxTotalRows)
            {
                throw new InvalidOperationException(
                    $"Table '{state.Schema.Name}' row count ({finalTable.RowCount}) exceeds u16 ceiling ({Dat64SchemaRegistry.MaxTotalRows}).");
            }

            VerifyNoPointerArrayAliasing(state.Schema, finalTable, state.StockTable.RowCount, state.ModifiedRows);

            byte[] serialized = finalTable.Serialize();
            string sha256 = Convert.ToHexStringLower(SHA256.HashData(serialized));
            mergedTableBytes[state.Schema.Name] = serialized;

            var sortedEntries = new SortedDictionary<string, ContentRowMapEntry>(state.RowMapEntries, StringComparer.Ordinal);
            rowMapTables[state.Schema.Name] = new ContentTableRowMap
            {
                StockRowCount = state.StockTable.RowCount,
                MergedRowCount = finalTable.RowCount,
                TableSha256 = sha256,
                Entries = sortedEntries,
            };
        }

        // 9. Build Route A UTF-16LE (with BOM + CRLF) StatDescriptions overlay files.
        var generatedOverlays = new SortedDictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var overlayEntries = new List<GeneratedOverlayEntry>(statDescriptionBlocksByPath.Count);

        foreach (var (virtualPath, blocks) in statDescriptionBlocksByPath)
        {
            byte[] overlayBytes = BuildStatDescriptionsOverlayBytes(blocks);
            generatedOverlays[virtualPath] = overlayBytes;
            overlayEntries.Add(new GeneratedOverlayEntry
            {
                VirtualPath = virtualPath,
                Sha256 = Convert.ToHexStringLower(SHA256.HashData(overlayBytes)),
            });
        }

        // 10. Compute deterministic overall SHA-256 handshake digest.
        string digest = ComputeHandshakeDigest(rowMapTables, overlayEntries);
        var rowMap = new ContentRowMap
        {
            Digest = digest,
            Mods = orderedMods.Select(m => m.ModId).ToList(),
            Tables = rowMapTables,
            GeneratedOverlays = overlayEntries,
        };
        string rowMapJson = rowMap.ToJson();

        // 11. Emit Mods/Client/.merged-cache/ directory layout if requested.
        if (!string.IsNullOrWhiteSpace(outputDirectory))
        {
            WriteMergedCache(outputDirectory, mergedTableBytes, generatedOverlays, rowMapJson);
        }

        return new DatPatchMergeResult(mergedTableBytes, generatedOverlays, rowMap, rowMapJson);
    }

    /// <summary>
    /// Generates UTF-16LE (with BOM <c>FF FE</c> and <c>CRLF</c> line endings) PoE 3.29 <c>Metadata/StatDescriptions/*.txt</c>
    /// overlay bytes mapping custom display templates onto stock dead-slot <c>Stats[row].Id</c> identifiers.
    /// </summary>
    public static byte[] BuildStatDescriptionsOverlayBytes(
        IReadOnlyList<(string StockId, DeadStatDescriptionSpec Spec)> blocks)
    {
        ArgumentNullException.ThrowIfNull(blocks);

        var sb = new StringBuilder();
        for (int b = 0; b < blocks.Count; b++)
        {
            var (stockId, spec) = blocks[b];
            IReadOnlyList<DeadStatDescriptionVariant> variants = ResolveDescriptionVariants(spec);
            if (variants.Count == 0)
            {
                continue;
            }

            if (sb.Length > 0)
            {
                sb.Append("\r\n");
            }

            sb.Append("description\r\n");
            sb.Append("\t1 ").Append(stockId).Append("\r\n");
            sb.Append('\t').Append(variants.Count.ToString(CultureInfo.InvariantCulture)).Append("\r\n");

            foreach (DeadStatDescriptionVariant variant in variants)
            {
                string cond = string.IsNullOrWhiteSpace(variant.Condition) ? "#" : variant.Condition.Trim();
                string escapedText = variant.Text.Replace("\"", "\\\"", StringComparison.Ordinal);
                sb.Append("\t\t").Append(cond).Append(" \"").Append(escapedText).Append('"');
                foreach (string handler in variant.Handlers)
                {
                    if (!string.IsNullOrWhiteSpace(handler))
                    {
                        sb.Append(' ').Append(handler.Trim());
                    }
                }

                sb.Append("\r\n");
            }
        }

        byte[] payload = Encoding.Unicode.GetBytes(sb.ToString());
        byte[] withBom = new byte[2 + payload.Length];
        withBom[0] = 0xFF;
        withBom[1] = 0xFE;
        Buffer.BlockCopy(payload, 0, withBom, 2, payload.Length);
        return withBom;
    }

    /// <summary>
    /// Verifies that no appended or modified row shares a non-empty variable-section offset on any
    /// <c>foreignrow[]</c> or <c>row[]</c> column with any other row in the table (preventing PoE 3.29's
    /// in-place pointer resolution crash at <c>Game.patched.exe + 0x129838</c>).
    /// </summary>
    public static void VerifyNoPointerArrayAliasing(
        Dat64TableSchema schema,
        Dat64Table table,
        int stockRowCount,
        IReadOnlySet<int>? modifiedRows = null)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(table);

        if (schema.PointerArrayColumns.Count == 0)
        {
            return;
        }

        var rowsToCheck = new HashSet<int>();
        for (int r = stockRowCount; r < table.RowCount; r++)
        {
            rowsToCheck.Add(r);
        }

        if (modifiedRows is not null)
        {
            foreach (int r in modifiedRows)
            {
                rowsToCheck.Add(r);
            }
        }

        if (rowsToCheck.Count == 0)
        {
            return;
        }

        foreach (Dat64ColumnSchema col in schema.PointerArrayColumns)
        {
            // Map every non-empty variable-section offset across ALL rows to detect any collision against checked rows.
            var offsetOwners = new Dictionary<ulong, int>();
            for (int r = 0; r < table.RowCount; r++)
            {
                ulong count = table.ReadUInt64(r, col.Offset);
                if (count == 0)
                {
                    continue;
                }

                ulong varOffset = table.ReadUInt64(r, col.Offset + 8);
                if (offsetOwners.TryGetValue(varOffset, out int priorRow))
                {
                    if (rowsToCheck.Contains(r) || rowsToCheck.Contains(priorRow))
                    {
                        throw new InvalidDataException(
                            $"{schema.Name}[{r}].{col.Name} (offset {col.Offset}) aliases row {priorRow} variable offset 0x{varOffset:X}; " +
                            "in-place pointer array resolution would crash PoE 3.29.");
                    }
                }
                else
                {
                    offsetOwners[varOffset] = r;
                }
            }
        }
    }

    private static List<DatPatchDocument> SortModsDeterministically(IReadOnlyList<DatPatchDocument> patches)
    {
        var byId = new Dictionary<string, DatPatchDocument>(StringComparer.OrdinalIgnoreCase);
        foreach (DatPatchDocument patch in patches)
        {
            ArgumentNullException.ThrowIfNull(patch);
            if (string.IsNullOrWhiteSpace(patch.ModId))
            {
                throw new InvalidOperationException("Every DatPatchDocument must declare a non-empty ModId.");
            }

            if (!byId.TryAdd(patch.ModId, patch))
            {
                throw new InvalidOperationException($"Duplicate DatPatchDocument ModId '{patch.ModId}'.");
            }
        }

        var inDegree = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var dependents = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (string id in byId.Keys)
        {
            inDegree[id] = 0;
            dependents[id] = [];
        }

        foreach (DatPatchDocument mod in byId.Values)
        {
            var seenDeps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string dep in mod.Depends)
            {
                if (string.IsNullOrWhiteSpace(dep))
                {
                    continue;
                }

                if (dep.Equals(mod.ModId, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"Mod '{mod.ModId}' cannot depend on itself.");
                }

                if (byId.ContainsKey(dep) && seenDeps.Add(dep))
                {
                    dependents[dep].Add(mod.ModId);
                    inDegree[mod.ModId]++;
                }
            }
        }

        var ready = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, deg) in inDegree)
        {
            if (deg == 0)
            {
                ready.Add(id);
            }
        }

        var ordered = new List<DatPatchDocument>(byId.Count);
        while (ready.Count > 0)
        {
            string next = ready.Min!;
            ready.Remove(next);
            ordered.Add(byId[next]);

            foreach (string depMod in dependents[next])
            {
                inDegree[depMod]--;
                if (inDegree[depMod] == 0)
                {
                    ready.Add(depMod);
                }
            }
        }

        if (ordered.Count != byId.Count)
        {
            string remaining = string.Join(", ", inDegree.Where(kv => kv.Value > 0).Select(kv => kv.Key).OrderBy(k => k, StringComparer.OrdinalIgnoreCase));
            throw new InvalidOperationException($"Dependency cycle detected among .datpatch.json mods: [{remaining}].");
        }

        return ordered;
    }

    private static void ValidateTableAppendable(string tableName, string modId)
    {
        if (Dat64SchemaRegistry.ClosedRegistryTables.Contains(tableName))
        {
            string guidance = tableName.Equals("Stats", StringComparison.OrdinalIgnoreCase)
                ? "Stats.datc64 is strictly closed at 23,362 (0x5b42) rows in PoE 3.29 (0x5b43 is compiled StatId::INVALID). " +
                  "Use 'allocateDeadStat' (30 verified dead stock rows + Route A StatDescriptions overlay), in-place non-ID 'modify', or high-ID StatsComponent (>= 0x10000) instead."
                : $"Table '{tableName}' is bound to a compiled enum in PoE 3.29 and cannot accept appended rows.";
            throw new InvalidOperationException($"Mod '{modId}' attempted to append to closed table '{tableName}'. {guidance}");
        }

        if (!Dat64SchemaRegistry.AppendableRegistryTables.Contains(tableName))
        {
            throw new InvalidOperationException(
                $"Mod '{modId}' attempted to append to non-appendable table '{tableName}'. " +
                $"Only the {Dat64SchemaRegistry.AppendableRegistryTables.Count} live-verified appendable registry tables accept 'append' entries.");
        }
    }

    private static void LoadTableState(
        string tableName,
        IDat64Source source,
        Dat64SchemaRegistry registry,
        Dictionary<string, TableMergeState> states)
    {
        if (states.ContainsKey(tableName))
        {
            return;
        }

        Dat64TableSchema schema = registry.GetRequiredTable(tableName);
        if (!source.TryReadTable(schema.Name, out byte[] rawBytes))
        {
            throw new FileNotFoundException(
                $"Stock .datc64 table 'Data/{schema.Name}.datc64' was not found in the supplied stock source.",
                $"Data/{schema.Name}.datc64");
        }

        Dat64Table stock = Dat64Table.Parse(rawBytes);
        if (stock.RowStride != schema.RowStride)
        {
            throw new InvalidDataException(
                $"Stock '{schema.Name}.datc64' stride mismatch: schema expects {schema.RowStride} bytes, binary has {stock.RowStride} bytes.");
        }

        states[schema.Name] = new TableMergeState(schema, stock);
    }

    private static int ResolveExistingStockRow(
        TableMergeState state,
        int? rowIndex,
        string? primaryId,
        string modId,
        IDat64Source source,
        Dat64SchemaRegistry registry,
        Dictionary<string, TableMergeState> loadedStates,
        Dictionary<string, Dat64Table> auxReadTables)
    {
        if (rowIndex.HasValue && !string.IsNullOrWhiteSpace(primaryId))
        {
            throw new InvalidOperationException(
                $"Mod '{modId}' on table '{state.Schema.Name}' specified both rowIndex ({rowIndex.Value}) and id ('{primaryId}'); specify only one.");
        }

        if (rowIndex.HasValue)
        {
            int idx = rowIndex.Value;
            if (idx < 0 || idx >= state.StockTable.RowCount)
            {
                throw new InvalidOperationException(
                    $"Mod '{modId}' referenced stock row {idx} on '{state.Schema.Name}', which is out of range (stock rows: {state.StockTable.RowCount}).");
            }

            return idx;
        }

        if (string.IsNullOrWhiteSpace(primaryId))
        {
            throw new InvalidOperationException(
                $"Mod '{modId}' on table '{state.Schema.Name}' must specify either 'rowIndex' or 'id'.");
        }

        if (state.StockRowByPrimaryId.TryGetValue(primaryId, out int directRow))
        {
            return directRow;
        }

        // Ergonomic fallback for join tables whose unique key is a foreignrow to a table with a string Id
        // (e.g. CurrencyItems.BaseItemTypesKey -> BaseItemTypes, MtxTypeGameSpecific.Type -> MtxTypes).
        Dat64ColumnSchema? uniqueFk = state.Schema.UniqueColumns.FirstOrDefault(c =>
            !c.IsArray && c.Type == Dat64ColumnType.ForeignRow && !string.IsNullOrWhiteSpace(c.ReferenceTable));
        if (uniqueFk is not null && registry.TryGetTable(uniqueFk.ReferenceTable!, out Dat64TableSchema? refSchema) &&
            refSchema?.PrimaryIdColumn is { } refIdCol)
        {
            Dat64Table? refTable = TryGetReadOnlyStockTable(refSchema.Name, source, loadedStates, auxReadTables);
            if (refTable is not null)
            {
                int refRow = refTable.FindRowByString(refIdCol.Offset, primaryId, StringComparison.OrdinalIgnoreCase);
                if (refRow >= 0)
                {
                    for (int r = 0; r < state.StockTable.RowCount; r++)
                    {
                        if (state.StockTable.ReadForeignRow(r, uniqueFk.Offset) == (ulong)refRow)
                        {
                            return r;
                        }
                    }
                }
            }
        }

        throw new InvalidOperationException(
            $"Mod '{modId}' could not find stock row with id '{primaryId}' in '{state.Schema.Name}'.");
    }

    private static Dat64Table? TryGetReadOnlyStockTable(
        string tableName,
        IDat64Source source,
        Dictionary<string, TableMergeState> loadedStates,
        Dictionary<string, Dat64Table> auxReadTables)
    {
        if (loadedStates.TryGetValue(tableName, out TableMergeState? state))
        {
            return state.StockTable;
        }

        if (auxReadTables.TryGetValue(tableName, out Dat64Table? cached))
        {
            return cached;
        }

        if (source.TryReadTable(tableName, out byte[] bytes))
        {
            Dat64Table parsed = Dat64Table.Parse(bytes);
            auxReadTables[tableName] = parsed;
            return parsed;
        }

        return null;
    }

    private static void ApplyDeadStatNonIdOverrides(
        TableMergeState statsState,
        Span<byte> rowBytes,
        string modId,
        DeadStatAllocationSpec spec,
        Dictionary<(string Table, string Key), ReservedRow> reservedSymbols)
    {
        if (spec.IsPublic.HasValue)
        {
            WriteNamedBool(statsState.Schema, rowBytes, "IsPublic", spec.IsPublic.Value);
        }

        if (spec.IsLocal.HasValue)
        {
            WriteNamedBool(statsState.Schema, rowBytes, "IsLocal", spec.IsLocal.Value);
        }

        if (spec.IsWeaponLocal.HasValue)
        {
            WriteNamedBool(statsState.Schema, rowBytes, "IsWeaponLocal", spec.IsWeaponLocal.Value);
        }

        if (spec.Semantics.HasValue)
        {
            if (statsState.Schema.TryResolveColumn("Semantics", out Dat64ColumnSchema? semCol) && semCol is not null)
            {
                Dat64TablePatcher.WriteEnumRow(rowBytes, semCol.Offset, unchecked((uint)spec.Semantics.Value));
            }
        }

        if (spec.IsScalable.HasValue)
        {
            WriteNamedBool(statsState.Schema, rowBytes, "IsScalable", spec.IsScalable.Value);
        }

        if (spec.Fields.Count > 0)
        {
            ApplyFields(
                statsState,
                rowBytes,
                spec.Fields,
                modId,
                entryContext: $"allocateDeadStat '{spec.Key}'",
                reservedSymbols,
                forbidStatsId: true);
        }
    }

    private static void WriteNamedBool(Dat64TableSchema schema, Span<byte> rowBytes, string colName, bool value)
    {
        if (schema.TryResolveColumn(colName, out Dat64ColumnSchema? col) && col is not null)
        {
            Dat64TablePatcher.WriteBool(rowBytes, col.Offset, value);
        }
    }

    private static HashSet<int> ApplyFields(
        TableMergeState state,
        Span<byte> rowBytes,
        IReadOnlyDictionary<string, JsonElement> fields,
        string modId,
        string entryContext,
        Dictionary<(string Table, string Key), ReservedRow> reservedSymbols,
        bool forbidStatsId)
    {
        var writtenOffsets = new HashSet<int>();

        foreach (var (fieldKey, value) in fields)
        {
            if (!state.Schema.TryResolveColumn(fieldKey, out Dat64ColumnSchema? col) || col is null)
            {
                throw new InvalidOperationException(
                    $"Unknown column '{fieldKey}' on table '{state.Schema.Name}' in mod '{modId}' ({entryContext}).");
            }

            if (forbidStatsId && (ForbiddenStatsModifyColumns.Contains(col.Name) || col.Offset == 0))
            {
                throw new InvalidOperationException(
                    $"Mod '{modId}' attempted to modify protected Stats.{col.Name} ({entryContext}). " +
                    "Stock Stats[row].Id and HASH32 MUST remain intact to preserve LocalExile's Stat.Checksum.");
            }

            WriteFieldValue(state, rowBytes, col, value, modId, entryContext, reservedSymbols);
            writtenOffsets.Add(col.Offset);
        }

        return writtenOffsets;
    }

    private static void WriteFieldValue(
        TableMergeState state,
        Span<byte> rowBytes,
        Dat64ColumnSchema col,
        JsonElement value,
        string modId,
        string entryContext,
        Dictionary<(string Table, string Key), ReservedRow> reservedSymbols)
    {
        if (col.IsOpaqueArray)
        {
            if (value.ValueKind == JsonValueKind.Null || (value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == 0))
            {
                state.Patcher.SetEmptyArrayHeader(rowBytes, col.Offset);
                return;
            }

            throw new InvalidOperationException(
                $"Opaque array column '{state.Schema.Name}.{col.Name}' in mod '{modId}' ({entryContext}) only accepts null or an empty array [].");
        }

        if (col.IsArray)
        {
            if (value.ValueKind == JsonValueKind.Null)
            {
                state.Patcher.SetEmptyArrayHeader(rowBytes, col.Offset);
                return;
            }

            if (value.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException(
                    $"Column '{state.Schema.Name}.{col.Name}' expects a JSON array in mod '{modId}' ({entryContext}), got {value.ValueKind}.");
            }

            switch (col.Type)
            {
                case Dat64ColumnType.ForeignRow:
                {
                    var items = new List<ulong>(value.GetArrayLength());
                    foreach (JsonElement el in value.EnumerateArray())
                    {
                        items.Add(ResolveRowReferenceOrNull(el, col, modId, entryContext, reservedSymbols) ?? Dat64Table.NullForeignRow);
                    }

                    state.Patcher.SetForeignRowArray(rowBytes, col.Offset, items.ToArray());
                    return;
                }
                case Dat64ColumnType.Row:
                {
                    var items = new List<ulong>(value.GetArrayLength());
                    foreach (JsonElement el in value.EnumerateArray())
                    {
                        items.Add(ResolveRowReferenceOrNull(el, col, modId, entryContext, reservedSymbols) ?? Dat64Table.NullForeignRow);
                    }

                    state.Patcher.SetRowArray(rowBytes, col.Offset, items.ToArray());
                    return;
                }
                case Dat64ColumnType.String:
                {
                    var items = new List<string>(value.GetArrayLength());
                    foreach (JsonElement el in value.EnumerateArray())
                    {
                        items.Add(el.ValueKind == JsonValueKind.Null ? string.Empty : RequireString(el, state.Schema.Name, col.Name, modId, entryContext));
                    }

                    state.Patcher.SetStringArray(rowBytes, col.Offset, items);
                    return;
                }
                case Dat64ColumnType.I32:
                {
                    var items = new List<int>(value.GetArrayLength());
                    foreach (JsonElement el in value.EnumerateArray())
                    {
                        items.Add(ResolveInt32(el, state.Schema.Name, col.Name, modId, entryContext, reservedSymbols));
                    }

                    state.Patcher.SetInt32Array(rowBytes, col.Offset, items.ToArray());
                    return;
                }
                case Dat64ColumnType.EnumRow:
                {
                    var items = new List<uint>(value.GetArrayLength());
                    foreach (JsonElement el in value.EnumerateArray())
                    {
                        items.Add(el.ValueKind == JsonValueKind.Null
                            ? Dat64Table.NullEnumRow
                            : unchecked((uint)ResolveInt32(el, state.Schema.Name, col.Name, modId, entryContext, reservedSymbols)));
                    }

                    state.Patcher.SetEnumRowArray(rowBytes, col.Offset, items.ToArray());
                    return;
                }
                case Dat64ColumnType.F32:
                {
                    var items = new List<float>(value.GetArrayLength());
                    foreach (JsonElement el in value.EnumerateArray())
                    {
                        items.Add(RequireSingle(el, state.Schema.Name, col.Name, modId, entryContext));
                    }

                    state.Patcher.SetSingleArray(rowBytes, col.Offset, items.ToArray());
                    return;
                }
                default:
                    throw new InvalidOperationException(
                        $"Unsupported array element type '{col.Type}' for '{state.Schema.Name}.{col.Name}'.");
            }
        }

        switch (col.Type)
        {
            case Dat64ColumnType.Bool:
                Dat64TablePatcher.WriteBool(rowBytes, col.Offset, RequireBool(value, state.Schema.Name, col.Name, modId, entryContext));
                break;
            case Dat64ColumnType.I16:
                Dat64TablePatcher.WriteInt16(rowBytes, col.Offset, checked((short)ResolveInt32(value, state.Schema.Name, col.Name, modId, entryContext, reservedSymbols)));
                break;
            case Dat64ColumnType.U16:
                Dat64TablePatcher.WriteUInt16(rowBytes, col.Offset, checked((ushort)ResolveUInt64(value, state.Schema.Name, col.Name, modId, entryContext, reservedSymbols)));
                break;
            case Dat64ColumnType.I32:
                Dat64TablePatcher.WriteInt32(rowBytes, col.Offset, ResolveInt32(value, state.Schema.Name, col.Name, modId, entryContext, reservedSymbols));
                break;
            case Dat64ColumnType.U32:
                Dat64TablePatcher.WriteUInt32(rowBytes, col.Offset, checked((uint)ResolveUInt64(value, state.Schema.Name, col.Name, modId, entryContext, reservedSymbols)));
                break;
            case Dat64ColumnType.F32:
                Dat64TablePatcher.WriteSingle(rowBytes, col.Offset, RequireSingle(value, state.Schema.Name, col.Name, modId, entryContext));
                break;
            case Dat64ColumnType.String:
            {
                string str = value.ValueKind == JsonValueKind.Null
                    ? string.Empty
                    : RequireString(value, state.Schema.Name, col.Name, modId, entryContext);
                state.Patcher.SetStringFresh(rowBytes, col.Offset, str);
                break;
            }
            case Dat64ColumnType.ForeignRow:
            {
                ulong? target = ResolveRowReferenceOrNull(value, col, modId, entryContext, reservedSymbols);
                Dat64TablePatcher.WriteForeignRow(rowBytes, col.Offset, target);
                break;
            }
            case Dat64ColumnType.Row:
            {
                ulong? target = ResolveRowReferenceOrNull(value, col, modId, entryContext, reservedSymbols);
                Dat64TablePatcher.WriteRow(rowBytes, col.Offset, target);
                break;
            }
            case Dat64ColumnType.EnumRow:
            {
                uint? enumVal = value.ValueKind == JsonValueKind.Null
                    ? null
                    : unchecked((uint)ResolveInt32(value, state.Schema.Name, col.Name, modId, entryContext, reservedSymbols));
                Dat64TablePatcher.WriteEnumRow(rowBytes, col.Offset, enumVal);
                break;
            }
            default:
                throw new InvalidOperationException($"Unsupported scalar type '{col.Type}' for '{state.Schema.Name}.{col.Name}'.");
        }
    }

    private static ulong? ResolveRowReferenceOrNull(
        JsonElement element,
        Dat64ColumnSchema col,
        string modId,
        string entryContext,
        Dictionary<(string Table, string Key), ReservedRow> reservedSymbols)
    {
        if (element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (element.ValueKind == JsonValueKind.Number)
        {
            long raw = element.GetInt64();
            return raw < 0 ? null : (ulong)raw;
        }

        if (element.ValueKind == JsonValueKind.String)
        {
            string s = element.GetString() ?? string.Empty;
            if (TryResolveSymbolicRef(s, modId, $"{col.Name} ({entryContext})", reservedSymbols, out ReservedRow resolved))
            {
                if (!string.IsNullOrWhiteSpace(col.ReferenceTable) &&
                    !col.ReferenceTable.Equals(resolved.Table, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Cross-table reference type mismatch in mod '{modId}' ({entryContext}): column '{col.Name}' references table '{col.ReferenceTable}', but '{s}' targets '{resolved.Table}'.");
                }

                return (ulong)resolved.RowIndex;
            }
        }

        throw new InvalidOperationException(
            $"Column '{col.Name}' expects null, a non-negative integer row index, or a '$ref:TableName:EntryKey' reference in mod '{modId}' ({entryContext}), got '{element}'.");
    }

    private static bool TryResolveSymbolicRef(
        string raw,
        string modId,
        string context,
        Dictionary<(string Table, string Key), ReservedRow> reservedSymbols,
        out ReservedRow resolved)
    {
        if (!raw.StartsWith(RefPrefix, StringComparison.OrdinalIgnoreCase))
        {
            resolved = default!;
            return false;
        }

        string remainder = raw[RefPrefix.Length..];
        int sep = remainder.IndexOf(':', StringComparison.Ordinal);
        if (sep <= 0 || sep + 1 >= remainder.Length)
        {
            throw new InvalidOperationException(
                $"Malformed symbolic reference '{raw}' in mod '{modId}' ({context}). Expected '$ref:TableName:EntryKey'.");
        }

        string targetTable = remainder[..sep].Trim();
        string targetKey = remainder[(sep + 1)..].Trim();

        if (!reservedSymbols.TryGetValue((targetTable, targetKey), out resolved!))
        {
            throw new InvalidOperationException(
                $"Unresolved symbolic reference '{raw}' in mod '{modId}' ({context}): no entry '{targetKey}' exists on table '{targetTable}'.");
        }

        return true;
    }

    private static int ResolveInt32(
        JsonElement el,
        string tableName,
        string colName,
        string modId,
        string ctx,
        Dictionary<(string Table, string Key), ReservedRow> reservedSymbols)
    {
        if (el.ValueKind == JsonValueKind.Number)
        {
            return el.GetInt32();
        }

        if (el.ValueKind == JsonValueKind.String)
        {
            string s = el.GetString() ?? string.Empty;
            if (TryResolveSymbolicRef(s, modId, $"{tableName}.{colName} ({ctx})", reservedSymbols, out ReservedRow resolved))
            {
                return resolved.RowIndex;
            }

            if (TryParseHexOrInt64(s, out long parsed))
            {
                return checked((int)parsed);
            }
        }

        throw new InvalidOperationException(
            $"Column '{tableName}.{colName}' expects an integer in mod '{modId}' ({ctx}), got '{el}'.");
    }

    private static ulong ResolveUInt64(
        JsonElement el,
        string tableName,
        string colName,
        string modId,
        string ctx,
        Dictionary<(string Table, string Key), ReservedRow> reservedSymbols)
    {
        if (el.ValueKind == JsonValueKind.Number)
        {
            return el.GetUInt64();
        }

        if (el.ValueKind == JsonValueKind.String)
        {
            string s = el.GetString() ?? string.Empty;
            if (TryResolveSymbolicRef(s, modId, $"{tableName}.{colName} ({ctx})", reservedSymbols, out ReservedRow resolved))
            {
                return (ulong)resolved.RowIndex;
            }

            if (TryParseHexOrUInt64(s, out ulong parsed))
            {
                return parsed;
            }
        }

        throw new InvalidOperationException(
            $"Column '{tableName}.{colName}' expects an unsigned integer in mod '{modId}' ({ctx}), got '{el}'.");
    }

    private static bool TryParseHexOrInt64(string text, out long value)
    {
        ReadOnlySpan<char> span = text.AsSpan().Trim();
        if (span.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return long.TryParse(span[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        }

        return long.TryParse(span, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryParseHexOrUInt64(string text, out ulong value)
    {
        ReadOnlySpan<char> span = text.AsSpan().Trim();
        if (span.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return ulong.TryParse(span[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        }

        return ulong.TryParse(span, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static bool RequireBool(JsonElement el, string tableName, string colName, string modId, string ctx)
    {
        if (el.ValueKind == JsonValueKind.True) return true;
        if (el.ValueKind == JsonValueKind.False) return false;
        if (el.ValueKind == JsonValueKind.Number) return el.GetInt32() != 0;
        throw new InvalidOperationException($"Column '{tableName}.{colName}' expects a boolean in mod '{modId}' ({ctx}), got '{el}'.");
    }

    private static float RequireSingle(JsonElement el, string tableName, string colName, string modId, string ctx)
    {
        if (el.ValueKind == JsonValueKind.Number) return el.GetSingle();
        throw new InvalidOperationException($"Column '{tableName}.{colName}' expects a float in mod '{modId}' ({ctx}), got '{el}'.");
    }

    private static string RequireString(JsonElement el, string tableName, string colName, string modId, string ctx)
    {
        if (el.ValueKind == JsonValueKind.String) return el.GetString() ?? string.Empty;
        throw new InvalidOperationException($"Column '{tableName}.{colName}' expects a string in mod '{modId}' ({ctx}), got '{el}'.");
    }

    private static uint ComputeAutoHash32(
        TableMergeState state,
        ReadOnlySpan<byte> rowBytes,
        string entryKey,
        Dictionary<string, TableMergeState> loadedStates)
    {
        if (state.Schema.PrimaryIdColumn is { } idCol)
        {
            ulong strOff = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(rowBytes.Slice(idCol.Offset, 8));
            string id = ReadStringFromPatcherSnapshot(state.Patcher, strOff);
            if (!string.IsNullOrEmpty(id))
            {
                return PoeHashing.Hash32(id);
            }
        }

        // Special case: MtxTypeGameSpecific inherits HASH32 from its referenced MtxTypes row when available.
        if (state.Schema.Name.Equals("MtxTypeGameSpecific", StringComparison.OrdinalIgnoreCase) &&
            loadedStates.TryGetValue("MtxTypes", out TableMergeState? mtxState) &&
            mtxState.Schema.Hash32Column is { } mtxH32Col)
        {
            ulong mtxRow = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(rowBytes.Slice(0, 8));
            if (mtxRow != Dat64Table.NullForeignRow && mtxRow < (ulong)mtxState.Patcher.RowCount)
            {
                byte[] mtxBytes = mtxState.Patcher.CloneRow((int)mtxRow);
                return System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(mtxBytes.AsSpan(mtxH32Col.Offset, 4));
            }
        }

        return PoeHashing.Hash32($"{state.Schema.Name}:{entryKey}");
    }

    private static string ReadPrimaryStringOrFallback(TableMergeState state, ReadOnlySpan<byte> rowBytes, string fallback)
    {
        if (state.Schema.PrimaryIdColumn is { } idCol)
        {
            ulong strOff = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(rowBytes.Slice(idCol.Offset, 8));
            string value = ReadStringFromPatcherSnapshot(state.Patcher, strOff);
            if (!string.IsNullOrEmpty(value))
            {
                return value;
            }
        }

        return fallback;
    }

    private static string ReadStringFromPatcherSnapshot(Dat64TablePatcher patcher, ulong varOffset) =>
        patcher.ReadStringAtOffset(varOffset);

    private static ushort AllocateUniqueHash16(string seedKey, HashSet<ushort> used, string tableName)
    {
        ushort candidate = PoeHashing.FoldHash16(PoeHashing.Hash32(seedKey));
        for (int attempt = 0; attempt < 65535; attempt++)
        {
            ushort h = (ushort)((candidate + attempt) & 0xFFFF);
            if (h != 0 && used.Add(h))
            {
                return h;
            }
        }

        throw new InvalidOperationException($"Exhausted all 16-bit HASH16 values on table '{tableName}'.");
    }

    private static void EnforceUniqueColumnsOnAppend(
        TableMergeState state,
        ReadOnlySpan<byte> rowBytes,
        string modId,
        string entryKey)
    {
        if (state.Schema.PrimaryIdColumn is { } idCol)
        {
            ulong strOff = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(rowBytes.Slice(idCol.Offset, 8));
            string id = ReadStringFromPatcherSnapshot(state.Patcher, strOff);
            if (!string.IsNullOrEmpty(id) && !state.UsedPrimaryIds.Add(id))
            {
                throw new InvalidOperationException(
                    $"Duplicate {state.Schema.Name}.{idCol.Name} '{id}' while appending '{entryKey}' for mod '{modId}'.");
            }
        }

        foreach (Dat64ColumnSchema uniqCol in state.Schema.UniqueColumns)
        {
            if (uniqCol.IsArray || uniqCol.Type != Dat64ColumnType.ForeignRow)
            {
                continue;
            }

            ulong fk = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(rowBytes.Slice(uniqCol.Offset, 8));
            if (fk != Dat64Table.NullForeignRow)
            {
                if (!state.UsedUniqueForeignKeys.TryGetValue(uniqCol.Offset, out HashSet<ulong>? set))
                {
                    set = [];
                    state.UsedUniqueForeignKeys[uniqCol.Offset] = set;
                }

                if (!set.Add(fk))
                {
                    throw new InvalidOperationException(
                        $"Duplicate unique foreign key {state.Schema.Name}.{uniqCol.Name} = {fk} while appending '{entryKey}' for mod '{modId}'.");
                }
            }
        }
    }

    private static IReadOnlyList<DeadStatDescriptionVariant> ResolveDescriptionVariants(DeadStatDescriptionSpec spec)
    {
        if (spec.Variants.Count > 0)
        {
            return spec.Variants;
        }

        if (!string.IsNullOrWhiteSpace(spec.Template))
        {
            return
            [
                new DeadStatDescriptionVariant
                {
                    Condition = string.IsNullOrWhiteSpace(spec.Condition) ? "#" : spec.Condition,
                    Text = spec.Template,
                    Handlers = spec.Handlers,
                },
            ];
        }

        return [];
    }

    private static string ResolveStatDescriptionOutputPath(string modId, string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            string normalized = explicitPath.Replace('\\', '/').Trim().TrimStart('/');
            if (normalized.Contains("..", StringComparison.Ordinal) || normalized.Contains(':', StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Unsafe StatDescriptions outputPath '{explicitPath}' in mod '{modId}'.");
            }

            return normalized;
        }

        var safeName = new StringBuilder(modId.Length);
        foreach (char c in modId)
        {
            safeName.Append(char.IsLetterOrDigit(c) || c is '_' or '-' ? char.ToLowerInvariant(c) : '_');
        }

        return $"Metadata/StatDescriptions/{safeName}_custom_stats.txt";
    }

    private static string ComputeHandshakeDigest(
        SortedDictionary<string, ContentTableRowMap> tables,
        List<GeneratedOverlayEntry> overlays)
    {
        var canonical = new StringBuilder();
        canonical.Append("content-row-map:v1\n");

        foreach (var (tableName, tbl) in tables)
        {
            canonical.Append("T:").Append(tableName)
                .Append('|').Append(tbl.StockRowCount.ToString(CultureInfo.InvariantCulture))
                .Append('|').Append(tbl.MergedRowCount.ToString(CultureInfo.InvariantCulture))
                .Append('|').Append(tbl.TableSha256).Append('\n');

            foreach (var (entryKey, entry) in tbl.Entries)
            {
                canonical.Append("E:").Append(tableName)
                    .Append(':').Append(entryKey)
                    .Append('|').Append(entry.ModId)
                    .Append('|').Append(entry.RowIndex.ToString(CultureInfo.InvariantCulture))
                    .Append('|').Append(entry.Hash32?.ToString("X8", CultureInfo.InvariantCulture) ?? "-")
                    .Append('|').Append(entry.Hash16?.ToString("X4", CultureInfo.InvariantCulture) ?? "-")
                    .Append('|').Append(entry.StockId ?? "-")
                    .Append('\n');
            }
        }

        foreach (GeneratedOverlayEntry ov in overlays.OrderBy(o => o.VirtualPath, StringComparer.Ordinal))
        {
            canonical.Append("O:").Append(ov.VirtualPath).Append('|').Append(ov.Sha256).Append('\n');
        }

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()));
        return Convert.ToHexStringLower(hash);
    }

    private static void WriteMergedCache(
        string outputDirectory,
        SortedDictionary<string, byte[]> mergedTables,
        SortedDictionary<string, byte[]> generatedOverlays,
        string rowMapJson)
    {
        Directory.CreateDirectory(outputDirectory);

        foreach (var (tableName, bytes) in mergedTables)
        {
            string fullPath = Path.Combine(outputDirectory, "Data", $"{tableName}.datc64");
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllBytes(fullPath, bytes);
        }

        foreach (var (virtualPath, bytes) in generatedOverlays)
        {
            string fullPath = Path.Combine(outputDirectory, virtualPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllBytes(fullPath, bytes);
        }

        File.WriteAllText(Path.Combine(outputDirectory, "content-row-map.json"), rowMapJson, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private sealed record ReservedRow(string ModId, string Table, string Key, int RowIndex, string? StockId);

    private sealed class TupleKeyComparer : IEqualityComparer<(string Table, string Key)>
    {
        public static readonly TupleKeyComparer Instance = new();

        public bool Equals((string Table, string Key) x, (string Table, string Key) y) =>
            string.Equals(x.Table, y.Table, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.Key, y.Key, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Table, string Key) obj) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Table),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Key));
    }

    private sealed class TableMergeState
    {
        public Dat64TableSchema Schema { get; }
        public Dat64Table StockTable { get; }
        public Dat64TablePatcher Patcher { get; }
        public int NextReservedRowIndex { get; set; }
        public Dictionary<string, int> StockRowByPrimaryId { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> UsedPrimaryIds { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<uint> UsedHash32s { get; } = [];
        public HashSet<ushort> UsedHash16s { get; } = [];
        public HashSet<string> UsedMtxShortIds { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<int, HashSet<ulong>> UsedUniqueForeignKeys { get; } = [];
        public HashSet<int> ModifiedRows { get; } = [];
        public Dictionary<string, ContentRowMapEntry> RowMapEntries { get; } = new(StringComparer.Ordinal);

        public TableMergeState(Dat64TableSchema schema, Dat64Table stockTable)
        {
            Schema = schema;
            StockTable = stockTable;
            Patcher = new Dat64TablePatcher(stockTable);
            NextReservedRowIndex = stockTable.RowCount;

            if (schema.PrimaryIdColumn is { } idCol)
            {
                for (int r = 0; r < stockTable.RowCount; r++)
                {
                    string id = stockTable.ReadString(r, idCol.Offset);
                    if (!string.IsNullOrEmpty(id))
                    {
                        StockRowByPrimaryId.TryAdd(id, r);
                        UsedPrimaryIds.Add(id);
                    }
                }
            }

            if (schema.Hash32Column is { } h32Col)
            {
                for (int r = 0; r < stockTable.RowCount; r++)
                {
                    UsedHash32s.Add(stockTable.ReadUInt32(r, h32Col.Offset));
                }
            }

            if (schema.Hash16Column is { } h16Col)
            {
                for (int r = 0; r < stockTable.RowCount; r++)
                {
                    ushort h16 = stockTable.ReadUInt16(r, h16Col.Offset);
                    if (h16 != 0)
                    {
                        UsedHash16s.Add(h16);
                    }
                }
            }

            if (schema.Name.Equals("MtxTypes", StringComparison.OrdinalIgnoreCase) &&
                stockTable.RowStride >= Dat64SchemaRegistry.MtxTypesShortIdOffset + 8)
            {
                for (int r = 0; r < stockTable.RowCount; r++)
                {
                    string shortId = stockTable.ReadString(r, Dat64SchemaRegistry.MtxTypesShortIdOffset);
                    if (!string.IsNullOrEmpty(shortId))
                    {
                        UsedMtxShortIds.Add(shortId);
                    }
                }
            }

            foreach (Dat64ColumnSchema uniqCol in schema.UniqueColumns)
            {
                if (uniqCol.IsArray || uniqCol.Type != Dat64ColumnType.ForeignRow)
                {
                    continue;
                }

                var set = new HashSet<ulong>();
                for (int r = 0; r < stockTable.RowCount; r++)
                {
                    if (stockTable.ReadForeignRow(r, uniqCol.Offset) is ulong fk)
                    {
                        set.Add(fk);
                    }
                }

                UsedUniqueForeignKeys[uniqCol.Offset] = set;
            }
        }
    }
}
