using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace LocalExile.Client.Patcher;

/// <summary>
/// Parsed PE32+ (x86-64) executable image providing safe RVA/file-offset translation,
/// header slack inspection, data directory inspection, and import-table enumeration.
/// </summary>
public sealed class Pe32PlusImage
{
    public const ushort DosMagic = 0x5A4D;        // "MZ"
    public const uint PeSignature = 0x00004550;   // "PE\0\0"
    public const ushort MachineAmd64 = 0x8664;
    public const ushort Pe32PlusMagic = 0x020B;
    public const int SectionHeaderSize = 40;
    public const int ImportDescriptorSize = 20;

    public const int DirIndexImport = 1;
    public const int DirIndexSecurity = 4;
    public const int DirIndexBoundImport = 11;

    public byte[] RawBytes { get; }
    public string Sha256Hex { get; }
    public int PeHeaderOffset { get; }
    public int FileHeaderOffset => PeHeaderOffset + 4;
    public int OptionalHeaderOffset => PeHeaderOffset + 24;
    public ushort Machine { get; }
    public ushort NumberOfSections { get; }
    public ushort SizeOfOptionalHeader { get; }
    public ulong ImageBase { get; }
    public uint SectionAlignment { get; }
    public uint FileAlignment { get; }
    public uint SizeOfImage { get; }
    public uint SizeOfHeaders { get; }
    public uint CheckSum { get; }
    public ushort DllCharacteristics { get; }
    public uint NumberOfRvaAndSizes { get; }
    public int DataDirectoriesOffset => OptionalHeaderOffset + 112;
    public int SectionTableOffset => OptionalHeaderOffset + SizeOfOptionalHeader;
    public int SectionTableEnd => SectionTableOffset + NumberOfSections * SectionHeaderSize;
    public int HeaderSlackBytes => (int)SizeOfHeaders - SectionTableEnd;

    public IReadOnlyList<PeDataDirectory> DataDirectories { get; }
    public IReadOnlyList<PeSectionHeader> Sections { get; }

    public PeDataDirectory ImportDirectory => GetDirectory(DirIndexImport);
    public PeDataDirectory SecurityDirectory => GetDirectory(DirIndexSecurity);
    public PeDataDirectory BoundImportDirectory => GetDirectory(DirIndexBoundImport);

    private Pe32PlusImage(
        byte[] rawBytes,
        string sha256Hex,
        int peHeaderOffset,
        ushort machine,
        ushort numberOfSections,
        ushort sizeOfOptionalHeader,
        ulong imageBase,
        uint sectionAlignment,
        uint fileAlignment,
        uint sizeOfImage,
        uint sizeOfHeaders,
        uint checkSum,
        ushort dllCharacteristics,
        uint numberOfRvaAndSizes,
        List<PeDataDirectory> dataDirectories,
        List<PeSectionHeader> sections)
    {
        RawBytes = rawBytes;
        Sha256Hex = sha256Hex;
        PeHeaderOffset = peHeaderOffset;
        Machine = machine;
        NumberOfSections = numberOfSections;
        SizeOfOptionalHeader = sizeOfOptionalHeader;
        ImageBase = imageBase;
        SectionAlignment = sectionAlignment;
        FileAlignment = fileAlignment;
        SizeOfImage = sizeOfImage;
        SizeOfHeaders = sizeOfHeaders;
        CheckSum = checkSum;
        DllCharacteristics = dllCharacteristics;
        NumberOfRvaAndSizes = numberOfRvaAndSizes;
        DataDirectories = dataDirectories;
        Sections = sections;
    }

    public static Pe32PlusImage Parse(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        if (bytes.Length < 0x40)
            throw new InvalidDataException("File is too small to be a PE32+ executable.");

        if (BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(0, 2)) != DosMagic)
            throw new InvalidDataException("Missing DOS 'MZ' signature.");

        int peOffset = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(0x3C, 4));
        if (peOffset < 0x40 || peOffset + 24 + 112 > bytes.Length)
            throw new InvalidDataException($"Invalid e_lfanew PE header offset: 0x{peOffset:X}.");

        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(peOffset, 4)) != PeSignature)
            throw new InvalidDataException("Missing 'PE\\0\\0' signature.");

        int fileHdr = peOffset + 4;
        ushort machine = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(fileHdr + 0, 2));
        if (machine != MachineAmd64)
            throw new InvalidDataException($"Unsupported PE machine 0x{machine:X4}; expected AMD64 (0x8664).");

        ushort numberOfSections = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(fileHdr + 2, 2));
        ushort sizeOfOptionalHeader = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(fileHdr + 16, 2));

        int optHdr = peOffset + 24;
        if (sizeOfOptionalHeader < 112 || optHdr + sizeOfOptionalHeader > bytes.Length)
            throw new InvalidDataException($"Invalid OptionalHeader size: {sizeOfOptionalHeader}.");

        ushort magic = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(optHdr, 2));
        if (magic != Pe32PlusMagic)
            throw new InvalidDataException($"Unsupported PE optional header magic 0x{magic:X4}; expected PE32+ (0x020B).");

        ulong imageBase = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(optHdr + 24, 8));
        uint sectionAlignment = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(optHdr + 32, 4));
        uint fileAlignment = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(optHdr + 36, 4));
        uint sizeOfImage = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(optHdr + 56, 4));
        uint sizeOfHeaders = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(optHdr + 60, 4));
        uint checkSum = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(optHdr + 64, 4));
        ushort dllCharacteristics = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(optHdr + 70, 2));
        uint numberOfRvaAndSizes = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(optHdr + 108, 4));

        if (sectionAlignment == 0 || (sectionAlignment & (sectionAlignment - 1)) != 0 ||
            fileAlignment == 0 || (fileAlignment & (fileAlignment - 1)) != 0)
            throw new InvalidDataException($"Invalid PE alignments: SectionAlignment=0x{sectionAlignment:X}, FileAlignment=0x{fileAlignment:X}.");

        if (numberOfRvaAndSizes > 32 || optHdr + 112 + numberOfRvaAndSizes * 8 > optHdr + sizeOfOptionalHeader)
            throw new InvalidDataException($"Invalid NumberOfRvaAndSizes: {numberOfRvaAndSizes}.");

        var dirs = new List<PeDataDirectory>((int)numberOfRvaAndSizes);
        for (int i = 0; i < numberOfRvaAndSizes; i++)
        {
            int dirOff = optHdr + 112 + i * 8;
            uint rva = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(dirOff, 4));
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(dirOff + 4, 4));
            dirs.Add(new PeDataDirectory(rva, size));
        }

        int secTable = optHdr + sizeOfOptionalHeader;
        if (secTable + numberOfSections * SectionHeaderSize > bytes.Length)
            throw new InvalidDataException("Section table extends beyond end of file.");

        var sections = new List<PeSectionHeader>(numberOfSections);
        for (int i = 0; i < numberOfSections; i++)
        {
            int s = secTable + i * SectionHeaderSize;
            string name = Encoding.ASCII.GetString(bytes, s, 8).TrimEnd('\0');
            uint virtualSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(s + 8, 4));
            uint virtualAddress = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(s + 12, 4));
            uint sizeOfRawData = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(s + 16, 4));
            uint pointerToRawData = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(s + 20, 4));
            uint characteristics = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(s + 36, 4));
            sections.Add(new PeSectionHeader(name, virtualSize, virtualAddress, sizeOfRawData, pointerToRawData, characteristics));
        }

        string sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
        return new Pe32PlusImage(
            bytes,
            sha256,
            peOffset,
            machine,
            numberOfSections,
            sizeOfOptionalHeader,
            imageBase,
            sectionAlignment,
            fileAlignment,
            sizeOfImage,
            sizeOfHeaders,
            checkSum,
            dllCharacteristics,
            numberOfRvaAndSizes,
            dirs,
            sections);
    }

    public PeDataDirectory GetDirectory(int index) =>
        index >= 0 && index < DataDirectories.Count ? DataDirectories[index] : new PeDataDirectory(0, 0);

    public bool TryRvaToFileOffset(uint rva, int length, out int fileOffset)
    {
        foreach (var sec in Sections)
        {
            uint mappedSize = Math.Max(sec.VirtualSize, sec.SizeOfRawData);
            if (rva >= sec.VirtualAddress && (ulong)rva + (ulong)length <= (ulong)sec.VirtualAddress + mappedSize)
            {
                ulong offsetInSec = rva - sec.VirtualAddress;
                if (offsetInSec + (ulong)length <= sec.SizeOfRawData)
                {
                    ulong candidate = sec.PointerToRawData + offsetInSec;
                    if (candidate + (ulong)length <= (ulong)RawBytes.Length)
                    {
                        fileOffset = (int)candidate;
                        return true;
                    }
                }
            }
        }

        fileOffset = 0;
        return false;
    }

    public int RvaToFileOffset(uint rva, int length = 1)
    {
        if (!TryRvaToFileOffset(rva, length, out int offset))
            throw new InvalidDataException($"RVA 0x{rva:X} (len {length}) does not map into any PE section raw data.");
        return offset;
    }

    public string ReadNullTerminatedAsciiAtRva(uint rva, int maxLen = 512)
    {
        int offset = RvaToFileOffset(rva, 1);
        int end = offset;
        int limit = Math.Min(RawBytes.Length, offset + maxLen);
        while (end < limit && RawBytes[end] != 0)
            end++;
        return Encoding.ASCII.GetString(RawBytes, offset, end - offset);
    }

    public IReadOnlyList<PeImportDescriptor> ReadImportDescriptors()
    {
        var result = new List<PeImportDescriptor>();
        var imp = ImportDirectory;
        if (imp.VirtualAddress == 0)
            return result;

        int offset = RvaToFileOffset(imp.VirtualAddress, ImportDescriptorSize);
        while (offset + ImportDescriptorSize <= RawBytes.Length)
        {
            uint originalFirstThunk = BinaryPrimitives.ReadUInt32LittleEndian(RawBytes.AsSpan(offset + 0, 4));
            uint timeDateStamp = BinaryPrimitives.ReadUInt32LittleEndian(RawBytes.AsSpan(offset + 4, 4));
            uint forwarderChain = BinaryPrimitives.ReadUInt32LittleEndian(RawBytes.AsSpan(offset + 8, 4));
            uint nameRva = BinaryPrimitives.ReadUInt32LittleEndian(RawBytes.AsSpan(offset + 12, 4));
            uint firstThunk = BinaryPrimitives.ReadUInt32LittleEndian(RawBytes.AsSpan(offset + 16, 4));

            if (originalFirstThunk == 0 && timeDateStamp == 0 && forwarderChain == 0 && nameRva == 0 && firstThunk == 0)
                break;

            string dllName = nameRva != 0 ? ReadNullTerminatedAsciiAtRva(nameRva) : "";
            var rawDescriptor = RawBytes.AsSpan(offset, ImportDescriptorSize).ToArray();
            result.Add(new PeImportDescriptor(originalFirstThunk, timeDateStamp, forwarderChain, nameRva, firstThunk, dllName, rawDescriptor));
            offset += ImportDescriptorSize;
        }

        return result;
    }

    public bool HasImportedDll(string dllName) =>
        ReadImportDescriptors().Any(d => string.Equals(d.DllName, dllName, StringComparison.OrdinalIgnoreCase));

    public static uint AlignUp(uint value, uint alignment)
    {
        uint rem = value % alignment;
        return rem == 0 ? value : value + (alignment - rem);
    }

    /// <summary>
    /// Standard Windows PE checksum algorithm (equivalent to <c>CheckSumMappedFile</c>).
    /// </summary>
    public static uint ComputePeChecksum(ReadOnlySpan<byte> image, int checksumFieldOffset)
    {
        ulong sum = 0;
        int len = image.Length;
        for (int i = 0; i < len; i += 2)
        {
            if (i == checksumFieldOffset || i == checksumFieldOffset + 2)
                continue;

            uint word = i + 1 < len
                ? (uint)(image[i] | (image[i + 1] << 8))
                : image[i];

            sum += word;
            if (sum > 0xFFFF_FFFFUL)
                sum = (sum & 0xFFFF_FFFFUL) + (sum >> 32);
        }

        while ((sum >> 16) != 0)
            sum = (sum & 0xFFFFUL) + (sum >> 16);

        return (uint)(sum + (ulong)len);
    }
}

public readonly record struct PeDataDirectory(uint VirtualAddress, uint Size);

public readonly record struct PeSectionHeader(
    string Name,
    uint VirtualSize,
    uint VirtualAddress,
    uint SizeOfRawData,
    uint PointerToRawData,
    uint Characteristics)
{
    public uint RawEnd => PointerToRawData + SizeOfRawData;
    public uint VirtualEnd => VirtualAddress + Math.Max(VirtualSize, SizeOfRawData);
}

public sealed record PeImportDescriptor(
    uint OriginalFirstThunk,
    uint TimeDateStamp,
    uint ForwarderChain,
    uint NameRva,
    uint FirstThunk,
    string DllName,
    byte[] RawBytes);
