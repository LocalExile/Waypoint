using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocalExile.Client.Waypoint;

/// <summary>Metadata for a single file stored inside a NativeMods prototype bundle.</summary>
public sealed record BundleFileEntry(
    [property: JsonPropertyName("virtualPath")] string VirtualPath,
    [property: JsonPropertyName("offset")] long Offset,
    [property: JsonPropertyName("uncompressedSize")] int UncompressedSize,
    [property: JsonPropertyName("storedSize")] int StoredSize,
    [property: JsonPropertyName("blockCount")] int BlockCount,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("encoding")] string Encoding);

/// <summary>Companion <c>manifest.json</c> written beside <c>&lt;id&gt;-&lt;hash&gt;.bundle.bin</c>.</summary>
public sealed record ModBundleManifest(
    [property: JsonPropertyName("formatVersion")] int FormatVersion,
    [property: JsonPropertyName("modId")] string ModId,
    [property: JsonPropertyName("modVersion")] string ModVersion,
    [property: JsonPropertyName("bundleFile")] string BundleFile,
    [property: JsonPropertyName("bundleSha256")] string BundleSha256,
    [property: JsonPropertyName("blockSize")] int BlockSize,
    [property: JsonPropertyName("files")] IReadOnlyList<BundleFileEntry> Files);

/// <summary>Result of packing a mod's <c>client/</c> directory into a deterministic bundle.</summary>
public sealed record PackedBundleResult(
    string BundlePath,
    string ManifestPath,
    ModBundleManifest Manifest);

/// <summary>Result of verifying a packed bundle against its companion <c>manifest.json</c>.</summary>
public sealed record BundleVerificationResult(
    bool IsValid,
    IReadOnlyList<string> Errors);

/// <summary>
/// Packs a mod's <c>client/</c> payload directory into a deterministic <c>&lt;id&gt;-&lt;hash&gt;.bundle.bin</c>
/// using Oodle-compatible uncompressed stored blocks (<c>0xCC 0x06</c> per 0x40000-byte chunk, as verified in
/// <c>10-verification.md</c> §2) plus a companion <c>manifest.json</c> with SHA-256 verification.
/// </summary>
public static class ModBundlePacker
{
    public const int FormatVersion = 1;
    public const int StoredBlockSize = 0x40000; // 256 KiB, matching PoE bundle granule size
    public static readonly byte[] Magic = "NMB1"u8.ToArray();
    private static readonly byte[] OodleStoredBlockHeader = [0xCC, 0x06];

    private static readonly Encoding Utf16LeNoBom = new UnicodeEncoding(bigEndian: false, byteOrderMark: false);

    private static readonly HashSet<string> ClientTextBomExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ui", ".ot", ".otc", ".ao", ".aoc", ".it", ".itc", ".act", ".epk", ".ddt", ".pet", ".trl", ".txt", ".xml"
    };

    private static readonly HashSet<string> ClientTextNoBomExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".sm", ".mat", ".amd"
    };

    /// <summary>
    /// Normalized virtual paths whose extension normally uses UTF-16LE BOM (e.g. <c>.txt</c>)
    /// but which Path of Exile 3.29 requires to be emitted without a BOM.
    /// </summary>
    private static readonly HashSet<string> ClientTextNoBomPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "art/uiimages1.txt"
    };

    /// <summary>
    /// Encodes a UTF-8 or UTF-16 text file to Path of Exile 3.29's expected BOM text format:
    /// UTF-16LE with BOM (<c>FF FE</c>) and CRLF line endings (matching <c>spikes/encode-overlay.py</c>).
    /// </summary>
    public static byte[] EncodeClientText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string stripped = text.TrimStart('\uFEFF');
        string crlf = stripped.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal);
        byte[] payload = Encoding.Unicode.GetBytes(crlf);
        byte[] result = new byte[2 + payload.Length];
        result[0] = 0xFF;
        result[1] = 0xFE;
        payload.CopyTo(result, 2);
        return result;
    }

    /// <summary>
    /// Encodes a UTF-8 or UTF-16 text file to Path of Exile 3.29's BOM-less text format (<c>.sm</c>, <c>.mat</c>, <c>.amd</c>):
    /// UTF-16LE without BOM and CRLF line endings.
    /// </summary>
    public static byte[] EncodeClientTextNoBom(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string stripped = text.TrimStart('\uFEFF');
        string crlf = stripped.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal);
        return Utf16LeNoBom.GetBytes(crlf);
    }

    /// <summary>Encodes text according to <paramref name="virtualPath"/> (UTF-16LE BOM, UTF-16LE no-BOM, or UTF-8).</summary>
    public static byte[] EncodeClientText(string text, string virtualPath)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(virtualPath);
        if (IsClientTextNoBomPath(virtualPath))
            return EncodeClientTextNoBom(text);
        string ext = Path.GetExtension(DualModPackageValidator.NormalizeVirtualPath(virtualPath));
        if (ClientTextBomExtensions.Contains(ext))
            return EncodeClientText(text);
        return Encoding.UTF8.GetBytes(text);
    }

    /// <summary>
    /// Returns true if <paramref name="virtualPath"/> must be encoded as BOM-less UTF-16LE CRLF
    /// (either by extension such as <c>.sm</c>/<c>.mat</c>/<c>.amd</c>, or by normalized path exception such as <c>art/uiimages1.txt</c>).
    /// </summary>
    public static bool IsClientTextNoBomPath(string virtualPath)
    {
        if (string.IsNullOrWhiteSpace(virtualPath))
            return false;

        string normalized = DualModPackageValidator.NormalizeVirtualPath(virtualPath);
        return ClientTextNoBomPaths.Contains(normalized)
            || ClientTextNoBomExtensions.Contains(Path.GetExtension(normalized));
    }

    public static bool IsClientTextNoBomExtension(string virtualPath) =>
        IsClientTextNoBomPath(virtualPath);

    public static bool IsClientTextExtension(string virtualPath)
    {
        if (string.IsNullOrWhiteSpace(virtualPath))
            return false;

        string normalized = DualModPackageValidator.NormalizeVirtualPath(virtualPath);
        if (ClientTextNoBomPaths.Contains(normalized))
            return true;

        string ext = Path.GetExtension(normalized);
        return ClientTextBomExtensions.Contains(ext) || ClientTextNoBomExtensions.Contains(ext);
    }

    /// <summary>
    /// Packs all files under <paramref name="sourceClientDir"/> into a deterministic
    /// <c>&lt;modId&gt;-&lt;shortHash&gt;.bundle.bin</c> and <c>manifest.json</c> in <paramref name="outputDir"/>.
    /// </summary>
    public static PackedBundleResult Pack(
        string modId,
        string modVersion,
        string sourceClientDir,
        string outputDir,
        bool encodeTextAsUtf16Le = true)
    {
        if (!DualModManifest.IsValidId(modId))
            throw new ArgumentException($"Invalid mod id '{modId}'.", nameof(modId));
        if (!Directory.Exists(sourceClientDir))
            throw new DirectoryNotFoundException($"Source client directory not found: '{sourceClientDir}'.");

        Directory.CreateDirectory(outputDir);

        // Deterministic ordering by lower-case ordinal virtual path
        var sourceFiles = Directory.EnumerateFiles(sourceClientDir, "*", SearchOption.AllDirectories)
            .Select(fullPath => (
                FullPath: fullPath,
                VirtualPath: DualModPackageValidator.NormalizeVirtualPath(Path.GetRelativePath(sourceClientDir, fullPath)).ToLowerInvariant()))
            .OrderBy(item => item.VirtualPath, StringComparer.Ordinal)
            .ToList();

        foreach (var (_, vpath) in sourceFiles)
        {
            if (!DualModPackageValidator.IsSafeRelativeVirtualPath(vpath))
                throw new InvalidDataException($"Unsafe virtual path '{vpath}'.");
        }

        // Build bundle bytes in memory (bounded by Waypoint's 64 MB mod limit) for deterministic content hash naming
        using var bundleStream = new MemoryStream();
        WriteHeader(bundleStream, sourceFiles.Count);

        var entries = new List<BundleFileEntry>(sourceFiles.Count);
        foreach (var (fullPath, virtualPath) in sourceFiles)
        {
            byte[] payloadBytes;
            string encoding;
            if (encodeTextAsUtf16Le && IsClientTextExtension(virtualPath))
            {
                string text = DualModPackageValidator.ReadTextAutoEncoding(fullPath);
                bool noBom = IsClientTextNoBomExtension(virtualPath);
                payloadBytes = noBom ? EncodeClientTextNoBom(text) : EncodeClientText(text);
                encoding = noBom ? "utf-16le-nobom-crlf" : "utf-16le-bom-crlf";
            }
            else
            {
                payloadBytes = File.ReadAllBytes(fullPath);
                encoding = "binary";
            }

            string payloadSha = Convert.ToHexStringLower(SHA256.HashData(payloadBytes));
            long entryOffset = bundleStream.Position;
            int blockCount = WriteStoredBlocks(bundleStream, payloadBytes);
            int storedSize = checked((int)(bundleStream.Position - entryOffset));

            entries.Add(new BundleFileEntry(
                virtualPath,
                entryOffset,
                payloadBytes.Length,
                storedSize,
                blockCount,
                payloadSha,
                encoding));
        }

        byte[] bundleBytes = bundleStream.ToArray();
        string bundleSha256 = Convert.ToHexStringLower(SHA256.HashData(bundleBytes));
        string shortHash = bundleSha256[..12];
        string bundleFileName = $"{modId}-{shortHash}.bundle.bin";
        string bundlePath = Path.Combine(outputDir, bundleFileName);
        string manifestPath = Path.Combine(outputDir, "manifest.json");

        File.WriteAllBytes(bundlePath, bundleBytes);

        var manifest = new ModBundleManifest(
            FormatVersion,
            modId,
            modVersion,
            bundleFileName,
            bundleSha256,
            StoredBlockSize,
            entries);

        File.WriteAllText(
            manifestPath,
            JsonSerializer.Serialize(manifest, DualModManifest.SerializerOptions),
            new UTF8Encoding(false));

        return new PackedBundleResult(bundlePath, manifestPath, manifest);
    }

    /// <summary>Loads a companion bundle <c>manifest.json</c>.</summary>
    public static ModBundleManifest LoadManifest(string manifestPath)
    {
        string json = File.ReadAllText(manifestPath);
        return JsonSerializer.Deserialize<ModBundleManifest>(json, DualModManifest.SerializerOptions)
            ?? throw new InvalidDataException("Bundle manifest is empty or invalid.");
    }

    /// <summary>
    /// Verifies the bundle binary header, overall SHA-256, and every entry's stored blocks and per-file SHA-256.
    /// </summary>
    public static BundleVerificationResult Verify(string bundlePath, string manifestPath)
    {
        var errors = new List<string>();
        if (!File.Exists(bundlePath))
        {
            errors.Add($"Bundle file not found: '{bundlePath}'.");
            return new(false, errors);
        }
        if (!File.Exists(manifestPath))
        {
            errors.Add($"Bundle manifest not found: '{manifestPath}'.");
            return new(false, errors);
        }

        ModBundleManifest manifest;
        try
        {
            manifest = LoadManifest(manifestPath);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException)
        {
            errors.Add($"Failed to parse bundle manifest: {ex.Message}");
            return new(false, errors);
        }

        if (manifest.FormatVersion != FormatVersion)
            errors.Add($"Unsupported bundle format version {manifest.FormatVersion}; expected {FormatVersion}.");

        byte[] bundleBytes = File.ReadAllBytes(bundlePath);
        string actualBundleSha = Convert.ToHexStringLower(SHA256.HashData(bundleBytes));
        if (!actualBundleSha.Equals(manifest.BundleSha256, StringComparison.OrdinalIgnoreCase))
            errors.Add($"Bundle SHA-256 mismatch: expected {manifest.BundleSha256}, actual {actualBundleSha}.");

        if (bundleBytes.Length < 16 || !bundleBytes.AsSpan(0, 4).SequenceEqual(Magic))
        {
            errors.Add("Invalid bundle magic header (expected 'NMB1').");
            return new(false, errors);
        }

        int headerVersion = BinaryPrimitives.ReadInt32LittleEndian(bundleBytes.AsSpan(4, 4));
        int headerEntryCount = BinaryPrimitives.ReadInt32LittleEndian(bundleBytes.AsSpan(8, 4));
        if (headerVersion != FormatVersion)
            errors.Add($"Header format version mismatch: {headerVersion}.");
        if (headerEntryCount != manifest.Files.Count)
            errors.Add($"Header file count ({headerEntryCount}) does not match manifest ({manifest.Files.Count}).");

        foreach (var entry in manifest.Files)
        {
            if (!DualModPackageValidator.IsSafeRelativeVirtualPath(entry.VirtualPath))
            {
                errors.Add($"Unsafe virtual path in manifest: '{entry.VirtualPath}'.");
                continue;
            }

            try
            {
                byte[] decoded = ReadEntryBytes(bundleBytes, entry);
                string actualEntrySha = Convert.ToHexStringLower(SHA256.HashData(decoded));
                if (!actualEntrySha.Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase))
                    errors.Add($"Entry '{entry.VirtualPath}' SHA-256 mismatch.");
            }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentOutOfRangeException)
            {
                errors.Add($"Entry '{entry.VirtualPath}' failed to decode: {ex.Message}");
            }
        }

        return new(errors.Count == 0, errors);
    }

    /// <summary>Reads a single virtual path directly from a packed bundle, verifying its SHA-256.</summary>
    public static byte[] ReadEntry(string bundlePath, string manifestPath, string virtualPath)
    {
        string normalized = DualModPackageValidator.NormalizeVirtualPath(virtualPath).ToLowerInvariant();
        var manifest = LoadManifest(manifestPath);
        var entry = manifest.Files.FirstOrDefault(f => f.VirtualPath.Equals(normalized, StringComparison.OrdinalIgnoreCase))
            ?? throw new FileNotFoundException($"Virtual path '{virtualPath}' not found in bundle '{manifest.BundleFile}'.");

        byte[] bundleBytes = File.ReadAllBytes(bundlePath);
        byte[] decoded = ReadEntryBytes(bundleBytes, entry);
        string actualSha = Convert.ToHexStringLower(SHA256.HashData(decoded));
        if (!actualSha.Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Entry '{entry.VirtualPath}' failed SHA-256 verification.");

        return decoded;
    }

    /// <summary>
    /// Verifies and unpacks all files from a packed bundle into <paramref name="targetDir"/>.
    /// </summary>
    public static IReadOnlyList<string> Unpack(string bundlePath, string manifestPath, string targetDir)
    {
        var verification = Verify(bundlePath, manifestPath);
        if (!verification.IsValid)
            throw new InvalidDataException("Bundle verification failed: " + string.Join("; ", verification.Errors));

        var manifest = LoadManifest(manifestPath);
        byte[] bundleBytes = File.ReadAllBytes(bundlePath);
        Directory.CreateDirectory(targetDir);
        string fullRoot = Path.GetFullPath(targetDir);
        string rootWithSep = fullRoot.EndsWith(Path.DirectorySeparatorChar) ? fullRoot : fullRoot + Path.DirectorySeparatorChar;

        var extracted = new List<string>(manifest.Files.Count);
        foreach (var entry in manifest.Files)
        {
            string dest = Path.GetFullPath(Path.Combine(fullRoot, Path.Combine(entry.VirtualPath.Split('/'))));
            if (!dest.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Bundle entry '{entry.VirtualPath}' escapes target directory.");

            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            byte[] data = ReadEntryBytes(bundleBytes, entry);
            File.WriteAllBytes(dest, data);
            extracted.Add(dest);
        }

        return extracted;
    }

    private static void WriteHeader(Stream stream, int entryCount)
    {
        Span<byte> header = stackalloc byte[16];
        Magic.CopyTo(header[..4]);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..8], FormatVersion);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..12], entryCount);
        BinaryPrimitives.WriteInt32LittleEndian(header[12..16], StoredBlockSize);
        stream.Write(header);
    }

    /// <summary>
    /// Writes payload as 0x40000-byte stored chunks: [u32 chunkRawLen][0xCC 0x06][raw bytes...].
    /// An empty 0-byte file writes a single 0-length stored block.
    /// </summary>
    private static int WriteStoredBlocks(Stream stream, byte[] payload)
    {
        Span<byte> lenBuf = stackalloc byte[4];
        if (payload.Length == 0)
        {
            BinaryPrimitives.WriteInt32LittleEndian(lenBuf, 0);
            stream.Write(lenBuf);
            stream.Write(OodleStoredBlockHeader);
            return 1;
        }

        int blocks = 0;
        int offset = 0;
        while (offset < payload.Length)
        {
            int chunkLen = Math.Min(StoredBlockSize, payload.Length - offset);
            BinaryPrimitives.WriteInt32LittleEndian(lenBuf, chunkLen);
            stream.Write(lenBuf);
            stream.Write(OodleStoredBlockHeader);
            stream.Write(payload, offset, chunkLen);
            offset += chunkLen;
            blocks++;
        }
        return blocks;
    }

    private static byte[] ReadEntryBytes(byte[] bundleBytes, BundleFileEntry entry)
    {
        if (entry.Offset < 16 || entry.StoredSize < 0 || entry.Offset > bundleBytes.Length - entry.StoredSize)
            throw new InvalidDataException("Entry offset/size falls outside bundle bounds.");

        if (entry.UncompressedSize < 0 || entry.UncompressedSize > entry.StoredSize)
            throw new InvalidDataException($"Invalid NMB1 uncompressedSize {entry.UncompressedSize} for stored size {entry.StoredSize}.");

        using var output = new MemoryStream(entry.UncompressedSize);
        long pos = entry.Offset;
        long end = entry.Offset + entry.StoredSize;
        int blocksRead = 0;

        while (pos < end)
        {
            if (pos + 6 > end)
                throw new InvalidDataException("Truncated stored block header.");

            int chunkLen = BinaryPrimitives.ReadInt32LittleEndian(bundleBytes.AsSpan((int)pos, 4));
            if (chunkLen < 0 || chunkLen > StoredBlockSize)
                throw new InvalidDataException($"Invalid stored block length {chunkLen}.");

            if (bundleBytes[pos + 4] != OodleStoredBlockHeader[0] || bundleBytes[pos + 5] != OodleStoredBlockHeader[1])
                throw new InvalidDataException("Invalid Oodle stored block marker (expected 0xCC 0x06).");

            pos += 6;
            if (chunkLen > end - pos)
                throw new InvalidDataException("Truncated stored block payload.");

            output.Write(bundleBytes, (int)pos, chunkLen);
            pos += chunkLen;
            blocksRead++;
        }

        if (blocksRead == 0)
            throw new InvalidDataException("NMB1 entry contains no stored blocks.");

        if (blocksRead != entry.BlockCount)
            throw new InvalidDataException($"Block count mismatch: expected {entry.BlockCount}, read {blocksRead}.");

        byte[] result = output.ToArray();
        if (result.Length != entry.UncompressedSize)
            throw new InvalidDataException($"Uncompressed size mismatch: expected {entry.UncompressedSize}, got {result.Length}.");

        return result;
    }
}
