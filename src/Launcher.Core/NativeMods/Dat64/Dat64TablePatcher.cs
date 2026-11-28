using System.Buffers.Binary;
using System.Text;

namespace LocalExile.Client.AssetPipeline.Dat64;

/// <summary>
/// Non-destructive <c>.datc64</c> patcher: preserves every existing row and variable-section offset byte-for-byte
/// while appending brand-new rows, UTF-16LE strings (with PoE's 4-byte double-NUL terminator), and typed arrays.
/// </summary>
public sealed class Dat64TablePatcher
{
    private readonly int _originalRowCount;
    private readonly int _rowStride;
    private readonly List<byte> _fixedRows;
    private readonly List<byte> _variableSection;
    private int _rowCount;
    private ulong? _cachedEmptyStringOffset;

    public int RowCount => _rowCount;
    public int RowStride => _rowStride;
    public int OriginalRowCount => _originalRowCount;

    public Dat64TablePatcher(Dat64Table source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.RowStride <= 0)
        {
            throw new InvalidOperationException("Cannot append rows to a .datc64 table with RowStride == 0.");
        }

        _originalRowCount = source.RowCount;
        _rowCount = source.RowCount;
        _rowStride = source.RowStride;

        _fixedRows = new List<byte>(source.FixedRows.Length + (_rowStride * 4));
        _fixedRows.AddRange(source.FixedRows.Span);

        _variableSection = new List<byte>(source.VariableSection.Length + 1024);
        _variableSection.AddRange(source.VariableSection.Span);
    }

    /// <summary>
    /// Creates a mutable byte buffer initialized from an existing row so unknown/default columns stay valid.
    /// </summary>
    public byte[] CloneRow(int templateRowIndex)
    {
        if ((uint)templateRowIndex >= (uint)_rowCount)
        {
            throw new ArgumentOutOfRangeException(nameof(templateRowIndex), $"Template row {templateRowIndex} out of range (RowCount={_rowCount}).");
        }

        byte[] copy = new byte[_rowStride];
        _fixedRows.CopyTo(templateRowIndex * _rowStride, copy, 0, _rowStride);
        return copy;
    }

    /// <summary>
    /// Creates a mutable byte buffer initialized from an existing row AND deep-copies every schema-known
    /// variable-section slice (<c>foreignrow[]</c>, <c>row[]</c>, <c>string[]</c>, <c>i32[]</c>, <c>enumrow[]</c>,
    /// <c>f32[]</c>, <c>string</c>, and opaque <c>array</c>) into freshly allocated offsets so PoE 3.29's in-place
    /// loader resolution can NEVER alias across two rows.
    /// </summary>
    public byte[] CloneRowDeep(int templateRowIndex, Dat64TableSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        if (schema.RowStride != _rowStride)
        {
            throw new InvalidOperationException(
                $"Schema stride {schema.RowStride} for '{schema.Name}' does not match table stride {_rowStride}.");
        }

        byte[] copy = CloneRow(templateRowIndex);

        foreach (Dat64ColumnSchema col in schema.VariableSectionColumns)
        {
            if (col.IsOpaqueArray)
            {
                SetEmptyArrayHeader(copy, col.Offset);
            }
            else if (col.IsArray)
            {
                switch (col.Type)
                {
                    case Dat64ColumnType.ForeignRow:
                        SetForeignRowArray(copy, col.Offset, ReadForeignRowArray(templateRowIndex, col.Offset));
                        break;
                    case Dat64ColumnType.Row:
                        SetRowArray(copy, col.Offset, ReadRowArray(templateRowIndex, col.Offset));
                        break;
                    case Dat64ColumnType.String:
                        SetStringArray(copy, col.Offset, ReadStringArray(templateRowIndex, col.Offset));
                        break;
                    case Dat64ColumnType.I32:
                        SetInt32Array(copy, col.Offset, ReadInt32Array(templateRowIndex, col.Offset));
                        break;
                    case Dat64ColumnType.EnumRow:
                        SetEnumRowArray(copy, col.Offset, ReadEnumRowArray(templateRowIndex, col.Offset));
                        break;
                    case Dat64ColumnType.F32:
                        SetSingleArray(copy, col.Offset, ReadSingleArray(templateRowIndex, col.Offset));
                        break;
                    default:
                        SetEmptyArrayHeader(copy, col.Offset);
                        break;
                }
            }
            else if (col.Type == Dat64ColumnType.String)
            {
                string val = ReadString(templateRowIndex, col.Offset);
                SetStringFresh(copy, col.Offset, val);
            }
        }

        return copy;
    }

    public string ReadStringAtOffset(ulong varOffset)
    {
        if (varOffset > (ulong)_variableSection.Count)
        {
            throw new InvalidDataException($"String variable offset {varOffset} exceeds variable section length {_variableSection.Count}.");
        }

        ReadOnlySpan<byte> varSpan = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_variableSection);
        int start = (int)varOffset;
        int end = start;
        while (end + 2 <= varSpan.Length)
        {
            if (BinaryPrimitives.ReadUInt16LittleEndian(varSpan.Slice(end, 2)) == 0)
            {
                break;
            }

            end += 2;
        }

        return Encoding.Unicode.GetString(varSpan.Slice(start, end - start));
    }

    public string ReadString(int rowIndex, int columnOffset)
    {
        ReadOnlySpan<byte> row = GetFixedRowSpan(rowIndex);
        return ReadStringAtOffset(BinaryPrimitives.ReadUInt64LittleEndian(row.Slice(columnOffset, 8)));
    }

    private ReadOnlySpan<byte> GetFixedRowSpan(int rowIndex)
    {
        if ((uint)rowIndex >= (uint)_rowCount)
        {
            throw new ArgumentOutOfRangeException(nameof(rowIndex), $"Row index {rowIndex} out of range (RowCount={_rowCount}).");
        }

        return System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_fixedRows).Slice(rowIndex * _rowStride, _rowStride);
    }

    private ulong[] ReadForeignRowArray(int rowIndex, int columnOffset)
    {
        ReadOnlySpan<byte> row = GetFixedRowSpan(rowIndex);
        ulong count = BinaryPrimitives.ReadUInt64LittleEndian(row.Slice(columnOffset, 8));
        ulong varOffset = BinaryPrimitives.ReadUInt64LittleEndian(row.Slice(columnOffset + 8, 8));
        if (count == 0) return [];

        ReadOnlySpan<byte> varSpan = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_variableSection);
        int n = checked((int)count);
        int start = checked((int)varOffset);
        ulong[] result = new ulong[n];
        for (int i = 0; i < n; i++)
        {
            result[i] = BinaryPrimitives.ReadUInt64LittleEndian(varSpan.Slice(start + (i * 16), 8));
        }

        return result;
    }

    private ulong[] ReadRowArray(int rowIndex, int columnOffset)
    {
        ReadOnlySpan<byte> row = GetFixedRowSpan(rowIndex);
        ulong count = BinaryPrimitives.ReadUInt64LittleEndian(row.Slice(columnOffset, 8));
        ulong varOffset = BinaryPrimitives.ReadUInt64LittleEndian(row.Slice(columnOffset + 8, 8));
        if (count == 0) return [];

        ReadOnlySpan<byte> varSpan = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_variableSection);
        int n = checked((int)count);
        int start = checked((int)varOffset);
        ulong[] result = new ulong[n];
        for (int i = 0; i < n; i++)
        {
            result[i] = BinaryPrimitives.ReadUInt64LittleEndian(varSpan.Slice(start + (i * 8), 8));
        }

        return result;
    }

    private string[] ReadStringArray(int rowIndex, int columnOffset)
    {
        ReadOnlySpan<byte> row = GetFixedRowSpan(rowIndex);
        ulong count = BinaryPrimitives.ReadUInt64LittleEndian(row.Slice(columnOffset, 8));
        ulong varOffset = BinaryPrimitives.ReadUInt64LittleEndian(row.Slice(columnOffset + 8, 8));
        if (count == 0) return [];

        ReadOnlySpan<byte> varSpan = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_variableSection);
        int n = checked((int)count);
        int start = checked((int)varOffset);
        ulong[] offsets = new ulong[n];
        for (int i = 0; i < n; i++)
        {
            offsets[i] = BinaryPrimitives.ReadUInt64LittleEndian(varSpan.Slice(start + (i * 8), 8));
        }

        string[] result = new string[n];
        for (int i = 0; i < n; i++)
        {
            result[i] = ReadStringAtOffset(offsets[i]);
        }

        return result;
    }

    private int[] ReadInt32Array(int rowIndex, int columnOffset)
    {
        ReadOnlySpan<byte> row = GetFixedRowSpan(rowIndex);
        ulong count = BinaryPrimitives.ReadUInt64LittleEndian(row.Slice(columnOffset, 8));
        ulong varOffset = BinaryPrimitives.ReadUInt64LittleEndian(row.Slice(columnOffset + 8, 8));
        if (count == 0) return [];

        ReadOnlySpan<byte> varSpan = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_variableSection);
        int n = checked((int)count);
        int start = checked((int)varOffset);
        int[] result = new int[n];
        for (int i = 0; i < n; i++)
        {
            result[i] = BinaryPrimitives.ReadInt32LittleEndian(varSpan.Slice(start + (i * 4), 4));
        }

        return result;
    }

    private uint[] ReadEnumRowArray(int rowIndex, int columnOffset)
    {
        ReadOnlySpan<byte> row = GetFixedRowSpan(rowIndex);
        ulong count = BinaryPrimitives.ReadUInt64LittleEndian(row.Slice(columnOffset, 8));
        ulong varOffset = BinaryPrimitives.ReadUInt64LittleEndian(row.Slice(columnOffset + 8, 8));
        if (count == 0) return [];

        ReadOnlySpan<byte> varSpan = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_variableSection);
        int n = checked((int)count);
        int start = checked((int)varOffset);
        uint[] result = new uint[n];
        for (int i = 0; i < n; i++)
        {
            result[i] = BinaryPrimitives.ReadUInt32LittleEndian(varSpan.Slice(start + (i * 4), 4));
        }

        return result;
    }

    private float[] ReadSingleArray(int rowIndex, int columnOffset)
    {
        ReadOnlySpan<byte> row = GetFixedRowSpan(rowIndex);
        ulong count = BinaryPrimitives.ReadUInt64LittleEndian(row.Slice(columnOffset, 8));
        ulong varOffset = BinaryPrimitives.ReadUInt64LittleEndian(row.Slice(columnOffset + 8, 8));
        if (count == 0) return [];

        ReadOnlySpan<byte> varSpan = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_variableSection);
        int n = checked((int)count);
        int start = checked((int)varOffset);
        float[] result = new float[n];
        for (int i = 0; i < n; i++)
        {
            result[i] = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(varSpan.Slice(start + (i * 4), 4)));
        }

        return result;
    }

    /// <summary>
    /// Creates a blank schema-initialized row with null row/foreignrow/enumrow sentinels, fresh empty strings,
    /// and fresh empty array headers so no column references invalid or aliased variable-section memory.
    /// </summary>
    public byte[] CreateBlankRow(Dat64TableSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        if (schema.RowStride != _rowStride)
        {
            throw new InvalidOperationException(
                $"Schema stride {schema.RowStride} for '{schema.Name}' does not match table stride {_rowStride}.");
        }

        byte[] row = new byte[_rowStride];
        foreach (Dat64ColumnSchema col in schema.Columns)
        {
            if (col.IsArray || col.IsOpaqueArray)
            {
                SetEmptyArrayHeader(row, col.Offset);
                continue;
            }

            switch (col.Type)
            {
                case Dat64ColumnType.String:
                    SetStringFresh(row, col.Offset, string.Empty);
                    break;
                case Dat64ColumnType.ForeignRow:
                    WriteForeignRow(row, col.Offset, null);
                    break;
                case Dat64ColumnType.Row:
                    WriteRow(row, col.Offset, null);
                    break;
                case Dat64ColumnType.EnumRow:
                    WriteEnumRow(row, col.Offset, null);
                    break;
            }
        }

        return row;
    }

    /// <summary>
    /// Overwrites an existing row's fixed bytes in place (used for safe <c>modify</c> operations).
    /// </summary>
    public void UpdateRow(int rowIndex, ReadOnlySpan<byte> rowBytes)
    {
        if ((uint)rowIndex >= (uint)_rowCount)
        {
            throw new ArgumentOutOfRangeException(nameof(rowIndex), $"Row index {rowIndex} out of range (RowCount={_rowCount}).");
        }

        if (rowBytes.Length != _rowStride)
        {
            throw new ArgumentException($"Row length {rowBytes.Length} does not match table RowStride {_rowStride}.", nameof(rowBytes));
        }

        int baseOffset = rowIndex * _rowStride;
        for (int i = 0; i < _rowStride; i++)
        {
            _fixedRows[baseOffset + i] = rowBytes[i];
        }
    }

    /// <summary>
    /// Appends a UTF-16LE string terminated by 4 zero bytes (<c>0x0000 0x0000</c>), matching real 3.29.3d <c>.datc64</c> tables.
    /// Returns its byte offset relative to the start of the variable-data sentinel.
    /// </summary>
    public ulong AppendString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (value.Length == 0 && _cachedEmptyStringOffset.HasValue)
        {
            return _cachedEmptyStringOffset.Value;
        }

        ulong offset = AppendStringFresh(value);

        if (value.Length == 0)
        {
            _cachedEmptyStringOffset = offset;
        }

        return offset;
    }

    /// <summary>
    /// Always appends a brand-new UTF-16LE string terminated by 4 zero bytes at a fresh variable-section offset
    /// without reusing any cached offset.
    /// </summary>
    public ulong AppendStringFresh(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        // Align string offsets to 2 bytes for clean UTF-16LE reads.
        if ((_variableSection.Count & 1) != 0)
        {
            _variableSection.Add(0);
        }

        ulong offset = (ulong)_variableSection.Count;
        if (value.Length > 0)
        {
            _variableSection.AddRange(Encoding.Unicode.GetBytes(value));
        }

        // PoE .datc64 strings are terminated by 4 zero bytes (double UTF-16LE NUL).
        _variableSection.Add(0);
        _variableSection.Add(0);
        _variableSection.Add(0);
        _variableSection.Add(0);

        return offset;
    }

    /// <summary>
    /// Appends a little-endian <c>i32[]</c> array to the variable section and returns <c>(count, offset)</c>.
    /// </summary>
    public (ulong Count, ulong Offset) AppendInt32Array(ReadOnlySpan<int> values)
    {
        if (values.IsEmpty)
        {
            return (0UL, (ulong)_variableSection.Count);
        }

        ulong offset = (ulong)_variableSection.Count;
        Span<byte> buf = stackalloc byte[4];
        foreach (int v in values)
        {
            BinaryPrimitives.WriteInt32LittleEndian(buf, v);
            _variableSection.AddRange(buf);
        }

        return ((ulong)values.Length, offset);
    }

    /// <summary>
    /// Appends a <c>foreignrow[]</c> array (16 bytes per entry: <c>(rowIndex, 0)</c>) to the variable section.
    /// </summary>
    public (ulong Count, ulong Offset) AppendForeignRowArray(ReadOnlySpan<ulong> targetRowIndices)
    {
        if (targetRowIndices.IsEmpty)
        {
            return (0UL, (ulong)_variableSection.Count);
        }

        ulong offset = (ulong)_variableSection.Count;
        Span<byte> buf = stackalloc byte[16];
        foreach (ulong idx in targetRowIndices)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(buf[..8], idx);
            BinaryPrimitives.WriteUInt64LittleEndian(buf[8..16], idx == Dat64Table.NullForeignRow ? Dat64Table.NullForeignRow : 0UL);
            _variableSection.AddRange(buf);
        }

        return ((ulong)targetRowIndices.Length, offset);
    }

    public (ulong Count, ulong Offset) AppendRowArray(ReadOnlySpan<ulong> targetRowIndices)
    {
        if (targetRowIndices.IsEmpty)
        {
            return (0UL, (ulong)_variableSection.Count);
        }

        ulong offset = (ulong)_variableSection.Count;
        Span<byte> buf = stackalloc byte[8];
        foreach (ulong idx in targetRowIndices)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(buf, idx);
            _variableSection.AddRange(buf);
        }

        return ((ulong)targetRowIndices.Length, offset);
    }

    public (ulong Count, ulong Offset) AppendEnumRowArray(ReadOnlySpan<uint> values)
    {
        if (values.IsEmpty)
        {
            return (0UL, (ulong)_variableSection.Count);
        }

        ulong offset = (ulong)_variableSection.Count;
        Span<byte> buf = stackalloc byte[4];
        foreach (uint v in values)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(buf, v);
            _variableSection.AddRange(buf);
        }

        return ((ulong)values.Length, offset);
    }

    public (ulong Count, ulong Offset) AppendSingleArray(ReadOnlySpan<float> values)
    {
        if (values.IsEmpty)
        {
            return (0UL, (ulong)_variableSection.Count);
        }

        ulong offset = (ulong)_variableSection.Count;
        Span<byte> buf = stackalloc byte[4];
        foreach (float v in values)
        {
            BinaryPrimitives.WriteInt32LittleEndian(buf, BitConverter.SingleToInt32Bits(v));
            _variableSection.AddRange(buf);
        }

        return ((ulong)values.Length, offset);
    }

    public (ulong Count, ulong Offset) AppendStringArray(IReadOnlyList<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0)
        {
            return (0UL, (ulong)_variableSection.Count);
        }

        ulong[] offsets = new ulong[values.Count];
        for (int i = 0; i < values.Count; i++)
        {
            offsets[i] = AppendStringFresh(values[i] ?? string.Empty);
        }

        ulong arrayOffset = (ulong)_variableSection.Count;
        Span<byte> buf = stackalloc byte[8];
        foreach (ulong off in offsets)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(buf, off);
            _variableSection.AddRange(buf);
        }

        return ((ulong)values.Count, arrayOffset);
    }

    /// <summary>
    /// Appends a complete fixed-size row and returns its new 0-based row index.
    /// </summary>
    public int AppendRow(ReadOnlySpan<byte> rowBytes)
    {
        if (rowBytes.Length != _rowStride)
        {
            throw new ArgumentException($"Row length {rowBytes.Length} does not match table RowStride {_rowStride}.", nameof(rowBytes));
        }

        int newIndex = _rowCount;
        _fixedRows.AddRange(rowBytes);
        _rowCount++;
        return newIndex;
    }

    public void SetString(Span<byte> row, int columnOffset, string value)
    {
        ulong offset = AppendString(value);
        BinaryPrimitives.WriteUInt64LittleEndian(row.Slice(columnOffset, 8), offset);
    }

    public void SetStringFresh(Span<byte> row, int columnOffset, string value)
    {
        ulong offset = AppendStringFresh(value);
        BinaryPrimitives.WriteUInt64LittleEndian(row.Slice(columnOffset, 8), offset);
    }

    public void SetInt32Array(Span<byte> row, int columnOffset, ReadOnlySpan<int> values)
    {
        var (count, offset) = AppendInt32Array(values);
        WriteArrayHeader(row, columnOffset, count, offset);
    }

    public void SetEnumRowArray(Span<byte> row, int columnOffset, ReadOnlySpan<uint> values)
    {
        var (count, offset) = AppendEnumRowArray(values);
        WriteArrayHeader(row, columnOffset, count, offset);
    }

    public void SetSingleArray(Span<byte> row, int columnOffset, ReadOnlySpan<float> values)
    {
        var (count, offset) = AppendSingleArray(values);
        WriteArrayHeader(row, columnOffset, count, offset);
    }

    public void SetForeignRowArray(Span<byte> row, int columnOffset, ReadOnlySpan<ulong> rowIndices)
    {
        var (count, offset) = AppendForeignRowArray(rowIndices);
        WriteArrayHeader(row, columnOffset, count, offset);
    }

    public void SetRowArray(Span<byte> row, int columnOffset, ReadOnlySpan<ulong> rowIndices)
    {
        var (count, offset) = AppendRowArray(rowIndices);
        WriteArrayHeader(row, columnOffset, count, offset);
    }

    public void SetStringArray(Span<byte> row, int columnOffset, IReadOnlyList<string> values)
    {
        var (count, offset) = AppendStringArray(values);
        WriteArrayHeader(row, columnOffset, count, offset);
    }

    public void SetEmptyArrayHeader(Span<byte> row, int columnOffset) =>
        WriteArrayHeader(row, columnOffset, 0UL, (ulong)_variableSection.Count);

    public static void WriteBool(Span<byte> row, int columnOffset, bool value) =>
        row[columnOffset] = value ? (byte)1 : (byte)0;

    public static void WriteInt16(Span<byte> row, int columnOffset, short value) =>
        BinaryPrimitives.WriteInt16LittleEndian(row.Slice(columnOffset, 2), value);

    public static void WriteUInt16(Span<byte> row, int columnOffset, ushort value) =>
        BinaryPrimitives.WriteUInt16LittleEndian(row.Slice(columnOffset, 2), value);

    public static void WriteInt32(Span<byte> row, int columnOffset, int value) =>
        BinaryPrimitives.WriteInt32LittleEndian(row.Slice(columnOffset, 4), value);

    public static void WriteUInt32(Span<byte> row, int columnOffset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(row.Slice(columnOffset, 4), value);

    public static void WriteSingle(Span<byte> row, int columnOffset, float value) =>
        BinaryPrimitives.WriteInt32LittleEndian(row.Slice(columnOffset, 4), BitConverter.SingleToInt32Bits(value));

    public static void WriteUInt64(Span<byte> row, int columnOffset, ulong value) =>
        BinaryPrimitives.WriteUInt64LittleEndian(row.Slice(columnOffset, 8), value);

    public static void WriteRow(Span<byte> row, int columnOffset, ulong? targetRowIndex) =>
        BinaryPrimitives.WriteUInt64LittleEndian(row.Slice(columnOffset, 8), targetRowIndex ?? Dat64Table.NullForeignRow);

    public static void WriteEnumRow(Span<byte> row, int columnOffset, uint? enumValue) =>
        BinaryPrimitives.WriteUInt32LittleEndian(row.Slice(columnOffset, 4), enumValue ?? Dat64Table.NullEnumRow);

    public static void WriteForeignRow(Span<byte> row, int columnOffset, ulong? targetRowIndex)
    {
        ulong first = targetRowIndex ?? Dat64Table.NullForeignRow;
        ulong second = targetRowIndex.HasValue ? 0UL : Dat64Table.NullForeignRow;
        BinaryPrimitives.WriteUInt64LittleEndian(row.Slice(columnOffset, 8), first);
        BinaryPrimitives.WriteUInt64LittleEndian(row.Slice(columnOffset + 8, 8), second);
    }

    public static void WriteArrayHeader(Span<byte> row, int columnOffset, ulong count, ulong varOffset)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(row.Slice(columnOffset, 8), count);
        BinaryPrimitives.WriteUInt64LittleEndian(row.Slice(columnOffset + 8, 8), varOffset);
    }

    /// <summary>
    /// Finalizes the patched table and performs a full binary round-trip verification.
    /// </summary>
    public Dat64Table BuildAndVerify()
    {
        var candidate = new Dat64Table(_rowCount, _rowStride, _fixedRows.ToArray(), _variableSection.ToArray());
        byte[] serialized = candidate.Serialize();
        Dat64Table roundTripped = Dat64Table.Parse(serialized);

        if (roundTripped.RowCount != _rowCount || roundTripped.RowStride != _rowStride)
        {
            throw new InvalidOperationException(
                $"Dat64 round-trip mismatch: expected ({_rowCount} rows, stride {_rowStride}), got ({roundTripped.RowCount} rows, stride {roundTripped.RowStride}).");
        }

        if (!roundTripped.FixedRows.Span.SequenceEqual(candidate.FixedRows.Span) ||
            !roundTripped.VariableSection.Span.SequenceEqual(candidate.VariableSection.Span))
        {
            throw new InvalidOperationException("Dat64 round-trip byte comparison failed.");
        }

        return roundTripped;
    }
}
