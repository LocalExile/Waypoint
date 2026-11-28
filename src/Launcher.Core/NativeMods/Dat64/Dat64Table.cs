using System.Buffers.Binary;
using System.Text;

namespace LocalExile.Client.AssetPipeline.Dat64;

/// <summary>
/// Binary representation of a Path of Exile 3.29 <c>.datc64</c> table:
/// <c>u32 rowCount</c>, <c>rowCount * rowStride</c> fixed row bytes,
/// an 8-byte <c>0xBBBBBBBBBBBBBBBB</c> sentinel, and the variable-length data section
/// (UTF-16LE strings terminated by 4 zero bytes and counted arrays).
/// </summary>
public sealed class Dat64Table
{
    public const ulong Sentinel = 0xBBBB_BBBB_BBBB_BBBBUL;
    public const ulong NullForeignRow = 0xFEFE_FEFE_FEFE_FEFEUL;
    public const uint NullEnumRow = 0xFEFE_FEFEu;
    public const int SentinelLength = 8;

    private readonly byte[] _fixedRows;
    private readonly byte[] _variableSection;

    public int RowCount { get; }
    public int RowStride { get; }
    public ReadOnlyMemory<byte> FixedRows => _fixedRows;
    public ReadOnlyMemory<byte> VariableSection => _variableSection;

    public Dat64Table(int rowCount, int rowStride, byte[] fixedRows, byte[] variableSection)
    {
        ArgumentNullException.ThrowIfNull(fixedRows);
        ArgumentNullException.ThrowIfNull(variableSection);

        if (rowCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(rowCount), "Row count cannot be negative.");
        }

        if (rowStride < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(rowStride), "Row stride cannot be negative.");
        }

        if (rowCount * rowStride != fixedRows.Length)
        {
            throw new ArgumentException($"Fixed section length ({fixedRows.Length}) does not equal RowCount ({rowCount}) * RowStride ({rowStride}).", nameof(fixedRows));
        }

        if (variableSection.Length < SentinelLength || BinaryPrimitives.ReadUInt64LittleEndian(variableSection) != Sentinel)
        {
            throw new ArgumentException("Variable section must begin with the 8-byte 0xBBBBBBBBBBBBBBBB sentinel.", nameof(variableSection));
        }

        RowCount = rowCount;
        RowStride = rowStride;
        _fixedRows = fixedRows;
        _variableSection = variableSection;
    }

    /// <summary>
    /// Parses a binary <c>.datc64</c> payload.
    /// </summary>
    public static Dat64Table Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4 + SentinelLength)
        {
            throw new InvalidDataException($"Invalid .datc64 payload: length {data.Length} is smaller than minimum 12 bytes.");
        }

        uint rawRowCount = BinaryPrimitives.ReadUInt32LittleEndian(data[..4]);
        if (rawRowCount > int.MaxValue)
        {
            throw new InvalidDataException($"Invalid .datc64 row count: {rawRowCount}.");
        }

        int rowCount = (int)rawRowCount;
        int fixedLength = FindAlignedSentinel(data, rowCount);
        if (fixedLength < 0)
        {
            throw new InvalidDataException($"Invalid .datc64 payload: aligned 0xBBBBBBBBBBBBBBBB sentinel not found for {rowCount} rows.");
        }

        int rowStride = rowCount == 0 ? 0 : fixedLength / rowCount;
        byte[] fixedRows = data.Slice(4, fixedLength).ToArray();
        byte[] variableSection = data[(4 + fixedLength)..].ToArray();
        return new Dat64Table(rowCount, rowStride, fixedRows, variableSection);
    }

    public static Dat64Table Load(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return Parse(File.ReadAllBytes(path));
    }

    public byte[] Serialize()
    {
        byte[] output = new byte[4 + _fixedRows.Length + _variableSection.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(0, 4), (uint)RowCount);
        Buffer.BlockCopy(_fixedRows, 0, output, 4, _fixedRows.Length);
        Buffer.BlockCopy(_variableSection, 0, output, 4 + _fixedRows.Length, _variableSection.Length);
        return output;
    }

    public ReadOnlySpan<byte> GetRowSpan(int rowIndex)
    {
        if ((uint)rowIndex >= (uint)RowCount)
        {
            throw new ArgumentOutOfRangeException(nameof(rowIndex), $"Row index {rowIndex} out of range (RowCount={RowCount}).");
        }

        return _fixedRows.AsSpan(rowIndex * RowStride, RowStride);
    }

    public byte ReadByte(int rowIndex, int columnOffset) => GetRowSpan(rowIndex)[columnOffset];

    public bool ReadBool(int rowIndex, int columnOffset) => GetRowSpan(rowIndex)[columnOffset] != 0;

    public short ReadInt16(int rowIndex, int columnOffset) =>
        BinaryPrimitives.ReadInt16LittleEndian(GetRowSpan(rowIndex).Slice(columnOffset, 2));

    public ushort ReadUInt16(int rowIndex, int columnOffset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(GetRowSpan(rowIndex).Slice(columnOffset, 2));

    public int ReadInt32(int rowIndex, int columnOffset) =>
        BinaryPrimitives.ReadInt32LittleEndian(GetRowSpan(rowIndex).Slice(columnOffset, 4));

    public uint ReadUInt32(int rowIndex, int columnOffset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(GetRowSpan(rowIndex).Slice(columnOffset, 4));

    public float ReadSingle(int rowIndex, int columnOffset) =>
        BitConverter.Int32BitsToSingle(ReadInt32(rowIndex, columnOffset));

    public ulong ReadUInt64(int rowIndex, int columnOffset) =>
        BinaryPrimitives.ReadUInt64LittleEndian(GetRowSpan(rowIndex).Slice(columnOffset, 8));

    public ulong? ReadRow(int rowIndex, int columnOffset)
    {
        ulong value = ReadUInt64(rowIndex, columnOffset);
        return value == NullForeignRow ? null : value;
    }

    public uint? ReadEnumRow(int rowIndex, int columnOffset)
    {
        uint value = ReadUInt32(rowIndex, columnOffset);
        return value == NullEnumRow ? null : value;
    }

    public ulong? ReadForeignRow(int rowIndex, int columnOffset)
    {
        ulong value = ReadUInt64(rowIndex, columnOffset);
        return value == NullForeignRow ? null : value;
    }

    /// <summary>
    /// Reads a UTF-16LE string referenced by the 8-byte variable-section offset stored at <paramref name="columnOffset"/>.
    /// Supports PoE's 4-byte double-NUL terminator (and stops cleanly on any UTF-16LE NUL code unit).
    /// </summary>
    public string ReadString(int rowIndex, int columnOffset)
    {
        ulong varOffset = ReadUInt64(rowIndex, columnOffset);
        return ReadStringAtOffset(varOffset);
    }

    public string ReadStringAtOffset(ulong varOffset)
    {
        if (varOffset > (ulong)_variableSection.Length)
        {
            throw new InvalidDataException($"String variable offset {varOffset} exceeds variable section length {_variableSection.Length}.");
        }

        int start = (int)varOffset;
        int end = start;
        while (end + 2 <= _variableSection.Length)
        {
            ushort unit = BinaryPrimitives.ReadUInt16LittleEndian(_variableSection.AsSpan(end, 2));
            if (unit == 0)
            {
                break;
            }

            end += 2;
        }

        return Encoding.Unicode.GetString(_variableSection, start, end - start);
    }

    public int[] ReadInt32Array(int rowIndex, int columnOffset)
    {
        ulong count = ReadUInt64(rowIndex, columnOffset);
        ulong varOffset = ReadUInt64(rowIndex, columnOffset + 8);
        if (count == 0)
        {
            return [];
        }

        int n = checked((int)count);
        int start = checked((int)varOffset);
        if (start < 0 || start + checked(n * 4) > _variableSection.Length)
        {
            throw new InvalidDataException($"Int32 array (count={n}, offset={start}) exceeds variable section length {_variableSection.Length}.");
        }

        int[] result = new int[n];
        for (int i = 0; i < n; i++)
        {
            result[i] = BinaryPrimitives.ReadInt32LittleEndian(_variableSection.AsSpan(start + (i * 4), 4));
        }

        return result;
    }

    public float[] ReadSingleArray(int rowIndex, int columnOffset)
    {
        ulong count = ReadUInt64(rowIndex, columnOffset);
        ulong varOffset = ReadUInt64(rowIndex, columnOffset + 8);
        if (count == 0)
        {
            return [];
        }

        int n = checked((int)count);
        int start = checked((int)varOffset);
        if (start < 0 || start + checked(n * 4) > _variableSection.Length)
        {
            throw new InvalidDataException($"Float array (count={n}, offset={start}) exceeds variable section length {_variableSection.Length}.");
        }

        float[] result = new float[n];
        for (int i = 0; i < n; i++)
        {
            int bits = BinaryPrimitives.ReadInt32LittleEndian(_variableSection.AsSpan(start + (i * 4), 4));
            result[i] = BitConverter.Int32BitsToSingle(bits);
        }

        return result;
    }

    public uint[] ReadEnumRowArray(int rowIndex, int columnOffset)
    {
        ulong count = ReadUInt64(rowIndex, columnOffset);
        ulong varOffset = ReadUInt64(rowIndex, columnOffset + 8);
        if (count == 0)
        {
            return [];
        }

        int n = checked((int)count);
        int start = checked((int)varOffset);
        if (start < 0 || start + checked(n * 4) > _variableSection.Length)
        {
            throw new InvalidDataException($"EnumRow array (count={n}, offset={start}) exceeds variable section length {_variableSection.Length}.");
        }

        uint[] result = new uint[n];
        for (int i = 0; i < n; i++)
        {
            result[i] = BinaryPrimitives.ReadUInt32LittleEndian(_variableSection.AsSpan(start + (i * 4), 4));
        }

        return result;
    }

    public ulong[] ReadRowArray(int rowIndex, int columnOffset)
    {
        ulong count = ReadUInt64(rowIndex, columnOffset);
        ulong varOffset = ReadUInt64(rowIndex, columnOffset + 8);
        if (count == 0)
        {
            return [];
        }

        int n = checked((int)count);
        int start = checked((int)varOffset);
        if (start < 0 || start + checked(n * 8) > _variableSection.Length)
        {
            throw new InvalidDataException($"Row array (count={n}, offset={start}) exceeds variable section length {_variableSection.Length}.");
        }

        ulong[] result = new ulong[n];
        for (int i = 0; i < n; i++)
        {
            result[i] = BinaryPrimitives.ReadUInt64LittleEndian(_variableSection.AsSpan(start + (i * 8), 8));
        }

        return result;
    }

    public string[] ReadStringArray(int rowIndex, int columnOffset)
    {
        ulong count = ReadUInt64(rowIndex, columnOffset);
        ulong varOffset = ReadUInt64(rowIndex, columnOffset + 8);
        if (count == 0)
        {
            return [];
        }

        int n = checked((int)count);
        int start = checked((int)varOffset);
        if (start < 0 || start + checked(n * 8) > _variableSection.Length)
        {
            throw new InvalidDataException($"String array (count={n}, offset={start}) exceeds variable section length {_variableSection.Length}.");
        }

        string[] result = new string[n];
        for (int i = 0; i < n; i++)
        {
            ulong strOffset = BinaryPrimitives.ReadUInt64LittleEndian(_variableSection.AsSpan(start + (i * 8), 8));
            result[i] = ReadStringAtOffset(strOffset);
        }

        return result;
    }

    public ulong[] ReadForeignRowArray(int rowIndex, int columnOffset)
    {
        ulong count = ReadUInt64(rowIndex, columnOffset);
        ulong varOffset = ReadUInt64(rowIndex, columnOffset + 8);
        if (count == 0)
        {
            return [];
        }

        int n = checked((int)count);
        int start = checked((int)varOffset);
        if (start < 0 || start + checked(n * 16) > _variableSection.Length)
        {
            throw new InvalidDataException($"ForeignRow array (count={n}, offset={start}) exceeds variable section length {_variableSection.Length}.");
        }

        ulong[] result = new ulong[n];
        for (int i = 0; i < n; i++)
        {
            result[i] = BinaryPrimitives.ReadUInt64LittleEndian(_variableSection.AsSpan(start + (i * 16), 8));
        }

        return result;
    }

    /// <summary>
    /// Finds the first row whose UTF-16LE string at <paramref name="stringColumnOffset"/> equals <paramref name="value"/>.
    /// </summary>
    public int FindRowByString(int stringColumnOffset, string value, StringComparison comparison = StringComparison.Ordinal)
    {
        ArgumentNullException.ThrowIfNull(value);
        for (int r = 0; r < RowCount; r++)
        {
            if (string.Equals(ReadString(r, stringColumnOffset), value, comparison))
            {
                return r;
            }
        }

        return -1;
    }

    private static int FindAlignedSentinel(ReadOnlySpan<byte> data, int rowCount)
    {
        for (int pos = 4; pos + SentinelLength <= data.Length; pos++)
        {
            if (BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(pos, SentinelLength)) != Sentinel)
            {
                continue;
            }

            int fixedLength = pos - 4;
            if (rowCount == 0)
            {
                if (fixedLength == 0)
                {
                    return 0;
                }

                continue;
            }

            if (fixedLength > 0 && fixedLength % rowCount == 0)
            {
                return fixedLength;
            }
        }

        return -1;
    }
}
