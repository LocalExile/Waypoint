using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocalExile.Client.AssetPipeline.Dat64;

/// <summary>
/// Declarative <c>.datpatch.json</c> document allowing a mod to compose append-only registry entries,
/// safe in-place stock row edits, and 30-slot dead-stat allocations with Route A <c>StatDescriptions</c> overlays.
/// </summary>
public sealed record DatPatchDocument
{
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Schema version of this <c>.datpatch.json</c> file (currently <c>1</c>).</summary>
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>Unique mod identifier owning this patch document.</summary>
    public required string ModId { get; init; }

    /// <summary>Optional mod IDs that should be merged before this mod (deterministic topological sort).</summary>
    public IReadOnlyList<string> Depends { get; init; } = [];

    /// <summary>Per-table patch operations keyed by <c>.datc64</c> table name (e.g. <c>BaseItemTypes</c>, <c>CurrencyItems</c>, <c>Mods</c>).</summary>
    public IReadOnlyDictionary<string, DatTablePatch> Tables { get; init; } =
        new Dictionary<string, DatTablePatch>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Custom tooltip stat allocations carved out of the 30 live-verified dead <c>Stats.datc64</c> stock rows
    /// (keeping stock <c>Stats[row].Id</c> intact for <c>LocalExile</c>'s <c>Stat.Checksum</c> and emitting Route A
    /// <c>Metadata/StatDescriptions/&lt;mod&gt;_custom_stats.txt</c> overlays).
    /// </summary>
    public IReadOnlyList<DeadStatAllocationSpec> AllocateDeadStat { get; init; } = [];

    public static DatPatchDocument Parse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        DatPatchDocument doc = JsonSerializer.Deserialize<DatPatchDocument>(json, ReadOptions)
            ?? throw new InvalidDataException("Failed to deserialize .datpatch.json: root JSON evaluated to null.");
        ValidateStructure(doc);
        return doc;
    }

    public static DatPatchDocument LoadFromFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Parse(File.ReadAllText(path));
    }

    public string ToJson() => JsonSerializer.Serialize(this, WriteOptions);

    private static void ValidateStructure(DatPatchDocument doc)
    {
        if (doc.SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Unsupported .datpatch.json schemaVersion {doc.SchemaVersion} in mod '{doc.ModId}' (expected {CurrentSchemaVersion}).");
        }

        if (string.IsNullOrWhiteSpace(doc.ModId))
        {
            throw new InvalidDataException(".datpatch.json must declare a non-empty 'modId'.");
        }
    }
}

/// <summary>
/// Declarative append and in-place modify operations for a single <c>.datc64</c> table.
/// </summary>
public sealed record DatTablePatch
{
    /// <summary>New rows to append to an appendable registry table (rejected on closed tables like <c>Stats</c>/<c>ItemClasses</c>).</summary>
    public IReadOnlyList<DatAppendEntry> Append { get; init; } = [];

    /// <summary>In-place field updates on existing stock rows (by <c>rowIndex</c> or primary <c>id</c>).</summary>
    public IReadOnlyList<DatModifyEntry> Modify { get; init; } = [];
}

/// <summary>
/// A single row to append to an appendable <c>.datc64</c> table.
/// </summary>
public sealed record DatAppendEntry
{
    /// <summary>
    /// Symbolic entry key unique per <c>(table, key)</c> across all merged mods.
    /// Referenced via <c>"$ref:TableName:EntryKey"</c> and exported in <c>content-row-map.json</c>.
    /// </summary>
    public required string Key { get; init; }

    /// <summary>Optional stock row index to deep-clone before applying <see cref="Fields"/> overrides.</summary>
    public int? CloneFromRow { get; init; }

    /// <summary>Optional stock row primary ID (e.g. <c>BaseItemTypes.Id</c>) to deep-clone before applying <see cref="Fields"/>.</summary>
    public string? CloneFromId { get; init; }

    /// <summary>
    /// Field overrides keyed by schema column name (e.g. <c>"Name"</c>, <c>"__col18"</c>) or byte offset (<c>"@137"</c> / <c>"offset:137"</c>).
    /// Values may use symbolic cross-table references <c>"$ref:TableName:EntryKey"</c> on row/foreignrow/array fields.
    /// </summary>
    public IReadOnlyDictionary<string, JsonElement> Fields { get; init; } =
        new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// An in-place column update on an existing stock row in a <c>.datc64</c> table.
/// </summary>
public sealed record DatModifyEntry
{
    /// <summary>Zero-based stock row index to modify (mutually exclusive with <see cref="Id"/>).</summary>
    public int? RowIndex { get; init; }

    /// <summary>Primary string ID (e.g. <c>BaseItemTypes.Id</c>, <c>Mods.Id</c>, or <c>CurrencyItems</c>' linked <c>BaseItemTypes.Id</c>) of the stock row to modify.</summary>
    public string? Id { get; init; }

    /// <summary>Field updates keyed by schema column name or byte offset (<c>"@offset"</c>).</summary>
    public IReadOnlyDictionary<string, JsonElement> Fields { get; init; } =
        new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Declarative request to allocate one of the 30 live-verified dead <c>Stats.datc64</c> stock rows
/// while preserving its stock <c>Stats[row].Id</c> (for <c>LocalExile</c> <c>Stat.Checksum</c> compatibility)
/// and emitting a Route A <c>Metadata/StatDescriptions/&lt;mod&gt;_custom_stats.txt</c> overlay.
/// </summary>
public sealed record DeadStatAllocationSpec
{
    /// <summary>Symbolic entry key referenced via <c>"$ref:Stats:&lt;key&gt;"</c> and recorded in <c>content-row-map.json</c>.</summary>
    public required string Key { get; init; }

    /// <summary>Optional specific dead slot index from <see cref="Dat64SchemaRegistry.DeadStatSlots"/> (never row 464).</summary>
    public int? PreferredSlot { get; init; }

    /// <summary>Optional override for <c>Stats.IsPublic</c>.</summary>
    public bool? IsPublic { get; init; }

    /// <summary>Optional override for <c>Stats.IsLocal</c>.</summary>
    public bool? IsLocal { get; init; }

    /// <summary>Optional override for <c>Stats.IsWeaponLocal</c>.</summary>
    public bool? IsWeaponLocal { get; init; }

    /// <summary>Optional override for <c>Stats.Semantics</c> (<c>StatSemantics</c> enum value).</summary>
    public int? Semantics { get; init; }

    /// <summary>Optional override for <c>Stats.IsScalable</c>.</summary>
    public bool? IsScalable { get; init; }

    /// <summary>Optional additional safe non-ID <c>Stats</c> column overrides (<c>Id</c> and <c>HASH32</c> are strictly forbidden).</summary>
    public IReadOnlyDictionary<string, JsonElement> Fields { get; init; } =
        new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Optional Route A <c>Metadata/StatDescriptions/*.txt</c> tooltip template specification.</summary>
    public DeadStatDescriptionSpec? Description { get; init; }

    /// <summary>Optional virtual path for the generated stat description file; defaults to <c>Metadata/StatDescriptions/&lt;modId&gt;_custom_stats.txt</c>.</summary>
    public string? OutputPath { get; init; }
}

/// <summary>
/// Tooltip description specification emitted into a Route A <c>Metadata/StatDescriptions/*.txt</c> overlay
/// targeting the allocated dead stat's stock <c>Stats[row].Id</c>.
/// </summary>
public sealed record DeadStatDescriptionSpec
{
    /// <summary>Shorthand single-variant display template (e.g. <c>"{0:+d}% increased Siphoned Power"</c>).</summary>
    public string? Template { get; init; }

    /// <summary>Shorthand condition when using <see cref="Template"/> (defaults to <c>"#"</c>).</summary>
    public string? Condition { get; init; }

    /// <summary>Optional shorthand handlers when using <see cref="Template"/> (e.g. <c>["negate 1"]</c>).</summary>
    public IReadOnlyList<string> Handlers { get; init; } = [];

    /// <summary>Explicit multi-variant stat description rules (takes precedence over <see cref="Template"/> when non-empty).</summary>
    public IReadOnlyList<DeadStatDescriptionVariant> Variants { get; init; } = [];
}

/// <summary>
/// A single condition/text variant line in a PoE <c>Metadata/StatDescriptions/*.txt</c> block.
/// </summary>
public sealed record DeadStatDescriptionVariant
{
    /// <summary>PoE stat condition expression (e.g. <c>"#"</c>, <c>"1|#"</c>, <c>"#|-1"</c>, <c>"!0"</c>).</summary>
    public string Condition { get; init; } = "#";

    /// <summary>PoE stat display string (e.g. <c>"{0:+d}% increased Siphoned Power"</c>).</summary>
    public required string Text { get; init; }

    /// <summary>Optional PoE trailing handlers (e.g. <c>["negate 1", "canonical_line"]</c>).</summary>
    public IReadOnlyList<string> Handlers { get; init; } = [];
}
