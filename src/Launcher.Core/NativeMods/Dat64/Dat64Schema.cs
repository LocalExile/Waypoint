using System.Reflection;
using System.Text.Json;

namespace LocalExile.Client.AssetPipeline.Dat64;

/// <summary>
/// Logical column types supported by Path of Exile 3.29 <c>.datc64</c> tables (matching <c>poe-tool-dev/dat-schema</c>).
/// </summary>
public enum Dat64ColumnType
{
    Bool,
    I16,
    U16,
    I32,
    U32,
    F32,
    String,
    Row,
    ForeignRow,
    EnumRow,
    Array,
}

/// <summary>
/// Schema metadata for a single column inside a <c>.datc64</c> table.
/// </summary>
public sealed record Dat64ColumnSchema
{
    public required int Index { get; init; }
    public required string Name { get; init; }
    public required Dat64ColumnType Type { get; init; }
    public bool IsArray { get; init; }
    public bool IsInterval { get; init; }
    public bool IsUnique { get; init; }
    public bool IsLocalized { get; init; }
    public required int Offset { get; init; }
    public required int Size { get; init; }
    public required int ElementSize { get; init; }
    public string? ReferenceTable { get; init; }
    public string? ReferenceColumn { get; init; }

    public bool IsOpaqueArray => Type == Dat64ColumnType.Array;
    public bool IsForeignRowArray => IsArray && Type == Dat64ColumnType.ForeignRow;
    public bool IsRowArray => IsArray && Type == Dat64ColumnType.Row;
    public bool IsStringArray => IsArray && Type == Dat64ColumnType.String;
    public bool UsesVariableSection => IsArray || Type is Dat64ColumnType.String or Dat64ColumnType.Array;
}

/// <summary>
/// Schema layout for a single Path of Exile 3.29 <c>.datc64</c> table.
/// </summary>
public sealed class Dat64TableSchema
{
    private readonly Dictionary<string, Dat64ColumnSchema> _byName;
    private readonly Dictionary<int, Dat64ColumnSchema> _byOffset;

    public string Name { get; }
    public int ValidFor { get; }
    public int RowStride { get; }
    public IReadOnlyList<Dat64ColumnSchema> Columns { get; }

    public Dat64ColumnSchema? PrimaryIdColumn { get; }
    public Dat64ColumnSchema? Hash32Column { get; }
    public Dat64ColumnSchema? Hash16Column { get; }
    public IReadOnlyList<Dat64ColumnSchema> UniqueColumns { get; }
    public IReadOnlyList<Dat64ColumnSchema> VariableSectionColumns { get; }
    public IReadOnlyList<Dat64ColumnSchema> PointerArrayColumns { get; }

    public Dat64TableSchema(string name, int validFor, IReadOnlyList<Dat64ColumnSchema> columns)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(columns);

        Name = name;
        ValidFor = validFor;
        Columns = columns;

        _byName = new Dictionary<string, Dat64ColumnSchema>(columns.Count, StringComparer.OrdinalIgnoreCase);
        _byOffset = new Dictionary<int, Dat64ColumnSchema>(columns.Count);

        var uniqueCols = new List<Dat64ColumnSchema>();
        var varCols = new List<Dat64ColumnSchema>();
        var ptrArrCols = new List<Dat64ColumnSchema>();

        int stride = 0;
        Dat64ColumnSchema? primaryId = null;
        Dat64ColumnSchema? hash32 = null;
        Dat64ColumnSchema? hash16 = null;

        foreach (Dat64ColumnSchema col in columns)
        {
            _byName.TryAdd(col.Name, col);
            _byName.TryAdd($"__col{col.Index}", col);
            _byOffset.TryAdd(col.Offset, col);
            stride = Math.Max(stride, col.Offset + col.Size);

            if (col.IsUnique)
            {
                uniqueCols.Add(col);
                if (primaryId is null && !col.IsArray && col.Type == Dat64ColumnType.String)
                {
                    primaryId = col;
                }
            }

            if (col.UsesVariableSection)
            {
                varCols.Add(col);
            }

            if (col.IsForeignRowArray || col.IsRowArray)
            {
                ptrArrCols.Add(col);
            }

            if (hash32 is null && !col.IsArray && col.Type == Dat64ColumnType.U32 &&
                col.Name.Equals("HASH32", StringComparison.OrdinalIgnoreCase))
            {
                hash32 = col;
            }

            if (hash16 is null && !col.IsArray && col.Type == Dat64ColumnType.U16 &&
                col.Name.Equals("HASH16", StringComparison.OrdinalIgnoreCase))
            {
                hash16 = col;
            }
        }

        // Fallback for tables whose Id string column is not marked unique in community schema.
        primaryId ??= columns.FirstOrDefault(c =>
            !c.IsArray && c.Type == Dat64ColumnType.String &&
            (c.Name.Equals("Id", StringComparison.OrdinalIgnoreCase) ||
             c.Name.Equals("Name", StringComparison.OrdinalIgnoreCase) ||
             c.Name.Equals("Text", StringComparison.OrdinalIgnoreCase)));

        RowStride = stride;
        PrimaryIdColumn = primaryId;
        Hash32Column = hash32;
        Hash16Column = hash16;
        UniqueColumns = uniqueCols;
        VariableSectionColumns = varCols;
        PointerArrayColumns = ptrArrCols;
    }

    /// <summary>
    /// Resolves a column by logical name (case-insensitive), <c>__colN</c>, or explicit byte offset (<c>@137</c> / <c>offset:137</c>).
    /// </summary>
    public bool TryResolveColumn(string nameOrOffset, out Dat64ColumnSchema? column)
    {
        if (string.IsNullOrWhiteSpace(nameOrOffset))
        {
            column = null;
            return false;
        }

        string trimmed = nameOrOffset.Trim();
        if (_byName.TryGetValue(trimmed, out column))
        {
            return true;
        }

        if (TryParseByteOffset(trimmed, out int byteOffset) && _byOffset.TryGetValue(byteOffset, out column))
        {
            return true;
        }

        column = null;
        return false;
    }

    public static bool TryParseByteOffset(string token, out int byteOffset)
    {
        byteOffset = -1;
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        ReadOnlySpan<char> span = token.AsSpan().Trim();
        if (span.StartsWith("@", StringComparison.Ordinal))
        {
            span = span[1..];
        }
        else if (span.StartsWith("offset:", StringComparison.OrdinalIgnoreCase))
        {
            span = span["offset:".Length..];
        }
        else
        {
            return false;
        }

        return int.TryParse(span, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out byteOffset)
            && byteOffset >= 0;
    }
}

/// <summary>
/// Registry of Path of Exile 3.29 <c>.datc64</c> table schemas, appendability policy, and verified dead <c>Stats</c> slots.
/// </summary>
public sealed class Dat64SchemaRegistry
{
    public const int MaxTotalRows = 65_535;
    public const int StockStatsRowCount329 = 23_362;
    public const int ForbiddenDummyStatRow = 464;
    public const int MtxTypesShortIdOffset = 137;

    private const string EmbeddedSchemaResourceName = "LocalExile.Client.AssetPipeline.Schemas.poe-3.29-dat-schema.json";

    /// <summary>
    /// The 22 live-verified appendable registry tables in Path of Exile 3.29.3c/3.29.3d
    /// (mirrors <c>DualModPackageValidator.AppendableRegistryTables</c>).
    /// </summary>
    public static IReadOnlySet<string> AppendableRegistryTables { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
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
        "Words",
    };

    /// <summary>
    /// Closed tables that MUST NEVER accept appended rows in 3.29.3d
    /// (<c>ItemClasses</c> compiled 103-entry enum; <c>Stats</c> fixed 23,362 / 0x5b42 rows).
    /// </summary>
    public static IReadOnlySet<string> ClosedRegistryTables { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "ItemClasses",
        "Stats",
    };

    /// <summary>
    /// The 30 live-verified dead <c>Stats.datc64</c> stock rows (<c>old_do_not_use_*</c> / <c>currently_unused_*</c>)
    /// with zero references in <c>Mods.sql</c>, <c>LocalExile</c>, and <c>Game.exe</c>'s virtual stat graph.
    /// Row <c>464</c> (<c>dummy_stat_display_nothing</c>, 375 stock refs) is strictly excluded.
    /// </summary>
    public static IReadOnlyList<int> DeadStatSlots { get; } =
    [
        99, 101, 102, 103, 379, 380, 565, 804, 805, 826,
        827, 1155, 1156, 1657, 1767, 1913, 1914, 2139, 2174, 2175,
        2176, 2177, 2182, 2193, 2254, 2427, 2434, 2622, 2744, 14334,
    ];

    public static IReadOnlySet<int> DeadStatSlotSet { get; } = new HashSet<int>(DeadStatSlots);

    private static readonly Lazy<Dat64SchemaRegistry> Default329Instance = new(LoadEmbeddedDefault329);

    private readonly Dictionary<string, Dat64TableSchema> _tables;

    public IReadOnlyDictionary<string, Dat64TableSchema> Tables => _tables;

    public Dat64SchemaRegistry(IReadOnlyDictionary<string, Dat64TableSchema> tables)
    {
        ArgumentNullException.ThrowIfNull(tables);
        _tables = new Dictionary<string, Dat64TableSchema>(tables, StringComparer.OrdinalIgnoreCase);
    }

    public static Dat64SchemaRegistry LoadDefault329() => Default329Instance.Value;

    public static Dat64SchemaRegistry LoadFromFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return LoadFromJson(File.ReadAllText(path));
    }

    public static Dat64SchemaRegistry LoadFromJson(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        using var doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        var tables = new Dictionary<string, Dat64TableSchema>(StringComparer.OrdinalIgnoreCase);

        if (!root.TryGetProperty("tables", out JsonElement tablesEl) || tablesEl.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Invalid poe-dat-schema JSON: missing 'tables' array.");
        }

        foreach (JsonElement t in tablesEl.EnumerateArray())
        {
            if (!t.TryGetProperty("name", out JsonElement nameEl) || nameEl.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            string tableName = nameEl.GetString()!;
            int validFor = t.TryGetProperty("validFor", out JsonElement vfEl) && vfEl.ValueKind == JsonValueKind.Number
                ? vfEl.GetInt32()
                : 3;

            var cols = new List<Dat64ColumnSchema>();
            int offset = 0;

            if (t.TryGetProperty("columns", out JsonElement colsEl) && colsEl.ValueKind == JsonValueKind.Array)
            {
                int index = 0;
                foreach (JsonElement c in colsEl.EnumerateArray())
                {
                    Dat64ColumnSchema parsed = ParseColumn(c, index, offset);
                    cols.Add(parsed);
                    offset += parsed.Size;
                    index++;
                }
            }

            var schema = new Dat64TableSchema(tableName, validFor, cols);

            // Prefer PoE 1 (validFor & 1 != 0) when a table name appears for both PoE 1 and PoE 2.
            if (tables.TryGetValue(tableName, out Dat64TableSchema? existing))
            {
                bool newIsGame1 = (validFor & 1) != 0;
                bool existingIsGame1 = (existing.ValidFor & 1) != 0;
                if (newIsGame1 && !existingIsGame1)
                {
                    tables[tableName] = schema;
                }
            }
            else
            {
                tables[tableName] = schema;
            }
        }

        return new Dat64SchemaRegistry(tables);
    }

    public bool TryGetTable(string tableName, out Dat64TableSchema? schema)
    {
        if (string.IsNullOrWhiteSpace(tableName))
        {
            schema = null;
            return false;
        }

        return _tables.TryGetValue(tableName.Trim(), out schema);
    }

    public Dat64TableSchema GetRequiredTable(string tableName)
    {
        if (!TryGetTable(tableName, out Dat64TableSchema? schema) || schema is null)
        {
            throw new InvalidOperationException(
                $"No schema definition found for .datc64 table '{tableName}'. Supply a full poe-dat-schema JSON via --schema if targeting a custom table.");
        }

        return schema;
    }

    public static int GetAtomSize(Dat64ColumnType type) => type switch
    {
        Dat64ColumnType.Bool => 1,
        Dat64ColumnType.I16 => 2,
        Dat64ColumnType.U16 => 2,
        Dat64ColumnType.I32 => 4,
        Dat64ColumnType.U32 => 4,
        Dat64ColumnType.F32 => 4,
        Dat64ColumnType.EnumRow => 4,
        Dat64ColumnType.String => 8,
        Dat64ColumnType.Row => 8,
        Dat64ColumnType.ForeignRow => 16,
        Dat64ColumnType.Array => 16,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unsupported Dat64ColumnType."),
    };

    private static Dat64ColumnSchema ParseColumn(JsonElement c, int index, int offset)
    {
        string colName = c.TryGetProperty("name", out JsonElement nEl) && nEl.ValueKind == JsonValueKind.String
            ? nEl.GetString()!
            : string.Empty;
        if (string.IsNullOrWhiteSpace(colName))
        {
            colName = $"__col{index}";
        }

        string typeStr = c.TryGetProperty("type", out JsonElement tyEl) && tyEl.ValueKind == JsonValueKind.String
            ? tyEl.GetString()!
            : "array";

        Dat64ColumnType colType = MapType(typeStr);
        bool isArray = ReadBoolProp(c, "array") || colType == Dat64ColumnType.Array;
        bool isInterval = ReadBoolProp(c, "interval");
        bool isUnique = ReadBoolProp(c, "unique");
        bool isLocalized = ReadBoolProp(c, "localized");

        string? refTable = null;
        string? refColumn = null;
        if (c.TryGetProperty("references", out JsonElement rfEl) && rfEl.ValueKind == JsonValueKind.Object)
        {
            if (rfEl.TryGetProperty("table", out JsonElement rtEl) && rtEl.ValueKind == JsonValueKind.String)
            {
                refTable = rtEl.GetString();
            }

            if (rfEl.TryGetProperty("column", out JsonElement rcEl) && rcEl.ValueKind == JsonValueKind.String)
            {
                refColumn = rcEl.GetString();
            }
        }

        int elementSize = GetAtomSize(colType);
        int size = isArray ? 16 : elementSize * (isInterval ? 2 : 1);

        return new Dat64ColumnSchema
        {
            Index = index,
            Name = colName,
            Type = colType,
            IsArray = isArray,
            IsInterval = isInterval,
            IsUnique = isUnique,
            IsLocalized = isLocalized,
            Offset = offset,
            Size = size,
            ElementSize = elementSize,
            ReferenceTable = refTable,
            ReferenceColumn = refColumn,
        };
    }

    private static bool ReadBoolProp(JsonElement obj, string propName) =>
        obj.TryGetProperty(propName, out JsonElement el) && el.ValueKind == JsonValueKind.True;

    private static Dat64ColumnType MapType(string typeStr) => typeStr switch
    {
        "bool" => Dat64ColumnType.Bool,
        "i16" => Dat64ColumnType.I16,
        "u16" => Dat64ColumnType.U16,
        "i32" => Dat64ColumnType.I32,
        "u32" => Dat64ColumnType.U32,
        "f32" => Dat64ColumnType.F32,
        "string" => Dat64ColumnType.String,
        "row" => Dat64ColumnType.Row,
        "foreignrow" => Dat64ColumnType.ForeignRow,
        "enumrow" => Dat64ColumnType.EnumRow,
        "array" => Dat64ColumnType.Array,
        _ => Dat64ColumnType.Array,
    };

    private static Dat64SchemaRegistry LoadEmbeddedDefault329()
    {
        Assembly asm = typeof(Dat64SchemaRegistry).Assembly;
        using Stream? stream = asm.GetManifestResourceStream(EmbeddedSchemaResourceName);
        if (stream is null)
        {
            throw new InvalidOperationException(
                $"Embedded 3.29 .datc64 schema resource '{EmbeddedSchemaResourceName}' was not found in assembly '{asm.FullName}'.");
        }

        using var reader = new StreamReader(stream);
        return LoadFromJson(reader.ReadToEnd());
    }
}
