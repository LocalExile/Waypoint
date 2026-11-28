using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LocalExile.Client.Patcher;

public enum BuildMatchKind
{
    Unknown = 0,
    ExactStockSha256 = 1,
    LocalExileKeyPatched329 = 2,
    SyntheticAllowed = 3,
}

public sealed record CheckResult(
    bool IsValidPe32Plus,
    BuildMatchKind MatchKind,
    string? GameVersion,
    string? BuildLabel,
    string Sha256Hex,
    long FileSize,
    int SectionCount,
    int HeaderSlackBytes,
    int ExistingImportCount,
    bool HasNativeModsImport,
    bool HasAuthenticodeSignature,
    uint AuthenticodeOffset,
    uint AuthenticodeSize,
    string Message);

public sealed record PatchOptions(
    string DllName = ClientPatcher.DefaultHostDllName,
    string ImportAnchorSymbol = ClientPatcher.DefaultImportAnchorSymbol,
    bool AllowSynthetic = false,
    bool CreateBackup = false);

public sealed record PatchResult(
    bool Patched,
    bool AlreadyPatched,
    BuildMatchKind MatchKind,
    string? GameVersion,
    string InputSha256,
    string OutputSha256,
    uint NewSectionRva,
    uint NewSectionRawOffset,
    uint NewSectionRawSize,
    int TotalImportCount,
    bool PreservedAuthenticodeForUnpatch,
    string? BackupPath,
    string Message);

public sealed record VerifyResult(
    bool IsValid,
    bool HasNativeModsImport,
    bool HasUnpatchMetadata,
    int ImportCount,
    string Sha256Hex,
    string? RecordedOriginalSha256,
    string Message);

public sealed record UnpatchResult(
    bool Unpatched,
    string PatchedSha256,
    string RestoredSha256,
    string ExpectedOriginalSha256,
    bool ExactByteRestoreVerified,
    string Message);

public sealed record ProxyDeploymentManifest(
    int SchemaVersion,
    string ProxyDllName,
    string ProxyDllSha256,
    string? HostDllName,
    string? HostDllSha256,
    string DeployedAtUtc,
    string WineDllOverridesHint);

/// <summary>
/// Universal PE32+ import-table patcher and reversible unpatcher for Path of Exile 3.29.3c / 3.29.3d,
/// plus proxy-DLL (<c>version.dll</c>) fallback deployment support.
/// </summary>
public sealed class ClientPatcher
{
    public const string DefaultHostDllName = "LocalExile.ClientHost.dll";
    public const string DefaultImportAnchorSymbol = "LocalExileClientHost_ImportAnchor";
    public const string DefaultProxyDllName = "version.dll";
    public const string PatchSectionName = ".nmimp";
    public const string ProxyManifestFileName = ".nativemods-proxy.json";

    private static readonly byte[] UnpatchMagic = "NMUNPAT1"u8.ToArray();
    private static readonly JsonSerializerOptions PrettyJsonOptions = new() { WriteIndented = true };
    private const uint UnpatchFormatVersion = 1;
    private const uint PatchSectionCharacteristics = 0xC0000040u; // CNT_INITIALIZED_DATA | MEM_READ | MEM_WRITE

    private readonly AddressLibraryCatalog? _catalog;

    public ClientPatcher(AddressLibraryCatalog? catalog = null)
    {
        _catalog = catalog;
    }

    public CheckResult CheckBytes(byte[] bytes, bool allowSynthetic = false, string dllName = DefaultHostDllName)
    {
        var image = Pe32PlusImage.Parse(bytes);
        var (matchKind, gameVersion, buildLabel) = IdentifyBuild(image, allowSynthetic);
        var imports = image.ReadImportDescriptors();
        bool hasImport = imports.Any(d => string.Equals(d.DllName, dllName, StringComparison.OrdinalIgnoreCase));
        var secDir = image.SecurityDirectory;
        bool hasAuth = secDir.VirtualAddress != 0 && secDir.Size != 0;

        string msg = matchKind switch
        {
            BuildMatchKind.ExactStockSha256 => $"Verified stock Path of Exile {gameVersion} ({buildLabel}).",
            BuildMatchKind.LocalExileKeyPatched329 => $"Verified LocalExile-patched 3.29 client ({gameVersion} code signatures matched).",
            BuildMatchKind.SyntheticAllowed => "Synthetic/unlisted PE32+ executable accepted via --allow-synthetic.",
            _ => $"Unsupported executable (SHA-256 {image.Sha256Hex[..16]}...). Expected Path of Exile 3.29.3c or 3.29.3d (pass --allow-synthetic for tests).",
        };

        return new CheckResult(
            IsValidPe32Plus: true,
            MatchKind: matchKind,
            GameVersion: gameVersion,
            BuildLabel: buildLabel,
            Sha256Hex: image.Sha256Hex,
            FileSize: bytes.LongLength,
            SectionCount: image.NumberOfSections,
            HeaderSlackBytes: image.HeaderSlackBytes,
            ExistingImportCount: imports.Count,
            HasNativeModsImport: hasImport,
            HasAuthenticodeSignature: hasAuth,
            AuthenticodeOffset: secDir.VirtualAddress,
            AuthenticodeSize: secDir.Size,
            Message: msg);
    }

    public (byte[] OutputBytes, PatchResult Result) PatchBytes(byte[] inputBytes, PatchOptions? options = null)
    {
        options ??= new PatchOptions();
        var image = Pe32PlusImage.Parse(inputBytes);
        var (matchKind, gameVersion, _) = IdentifyBuild(image, options.AllowSynthetic);

        if (matchKind == BuildMatchKind.Unknown)
        {
            throw new InvalidOperationException(
                $"Refusing to patch unsupported build (SHA-256 {image.Sha256Hex}). " +
                "Only verified 3.29.3c / 3.29.3d clients are allowed without --allow-synthetic.");
        }

        var existingImports = image.ReadImportDescriptors();
        if (existingImports.Any(d => string.Equals(d.DllName, options.DllName, StringComparison.OrdinalIgnoreCase)))
        {
            var existingSection = image.Sections.FirstOrDefault(s => s.Name == PatchSectionName);
            return ((byte[])inputBytes.Clone(), new PatchResult(
                Patched: false,
                AlreadyPatched: true,
                MatchKind: matchKind,
                GameVersion: gameVersion,
                InputSha256: image.Sha256Hex,
                OutputSha256: image.Sha256Hex,
                NewSectionRva: existingSection.VirtualAddress,
                NewSectionRawOffset: existingSection.PointerToRawData,
                NewSectionRawSize: existingSection.SizeOfRawData,
                TotalImportCount: existingImports.Count,
                PreservedAuthenticodeForUnpatch: false,
                BackupPath: null,
                Message: $"Executable already imports {options.DllName}; no changes made (idempotent)."));
        }

        if (image.HeaderSlackBytes < Pe32PlusImage.SectionHeaderSize)
        {
            throw new InvalidOperationException(
                $"Insufficient PE header slack: need {Pe32PlusImage.SectionHeaderSize} bytes, have {image.HeaderSlackBytes} bytes.");
        }

        if (image.NumberOfSections == 0)
            throw new InvalidOperationException("PE image contains zero sections.");

        uint maxVirtualEnd = image.SizeOfImage;
        uint maxRawEnd = 0;
        foreach (var sec in image.Sections)
        {
            if (sec.VirtualEnd > maxVirtualEnd)
                maxVirtualEnd = sec.VirtualEnd;
            if (sec.RawEnd > maxRawEnd)
                maxRawEnd = sec.RawEnd;
        }

        if (maxRawEnd > (uint)inputBytes.Length)
            throw new InvalidDataException($"Last section raw end 0x{maxRawEnd:X} exceeds file length 0x{inputBytes.Length:X}.");

        // Any trailing bytes beyond maxRawEnd (such as the EOF Authenticode SecurityDir on Game.exe)
        // are captured verbatim into the reversible NMUNPAT1 metadata record inside .nmimp so unpatch
        // restores the original file byte-for-byte with identical SHA-256.
        int trailingLen = inputBytes.Length - (int)maxRawEnd;
        byte[] trailingBytes = trailingLen > 0 ? inputBytes[(int)maxRawEnd..] : [];
        bool hadAuthenticode = image.SecurityDirectory.VirtualAddress != 0 && image.SecurityDirectory.Size != 0;

        uint newSecRva = Pe32PlusImage.AlignUp(maxVirtualEnd, image.SectionAlignment);
        uint newSecRawOffset = Pe32PlusImage.AlignUp(maxRawEnd, image.FileAlignment);

        byte[] origSlackBytes = inputBytes.AsSpan(image.SectionTableEnd, Pe32PlusImage.SectionHeaderSize).ToArray();
        byte[] origSha256Raw = SHA256.HashData(inputBytes);

        // Build .nmimp section payload.
        byte[] sectionPayload = BuildImportSectionPayload(
            image,
            existingImports,
            newSecRva,
            options.DllName,
            options.ImportAnchorSymbol,
            origSha256Raw,
            origSlackBytes,
            trailingBytes);

        uint newSecVirtualSize = (uint)sectionPayload.Length;
        uint newSecRawSize = Pe32PlusImage.AlignUp(newSecVirtualSize, image.FileAlignment);
        uint newSizeOfImage = Pe32PlusImage.AlignUp(newSecRva + newSecVirtualSize, image.SectionAlignment);

        byte[] output = new byte[newSecRawOffset + newSecRawSize];
        // Copy everything up to maxRawEnd (stripping any EOF Authenticode / overlay which is safely
        // preserved inside NMUNPAT1).
        Buffer.BlockCopy(inputBytes, 0, output, 0, (int)maxRawEnd);
        Buffer.BlockCopy(sectionPayload, 0, output, (int)newSecRawOffset, sectionPayload.Length);

        // Write new section header in the verified PE header slack.
        WriteSectionHeader(
            output.AsSpan(image.SectionTableEnd, Pe32PlusImage.SectionHeaderSize),
            PatchSectionName,
            newSecVirtualSize,
            newSecRva,
            newSecRawSize,
            newSecRawOffset,
            PatchSectionCharacteristics);

        // Update FileHeader.NumberOfSections.
        BinaryPrimitives.WriteUInt16LittleEndian(
            output.AsSpan(image.FileHeaderOffset + 2, 2),
            (ushort)(image.NumberOfSections + 1));

        // Update OptionalHeader.SizeOfImage.
        BinaryPrimitives.WriteUInt32LittleEndian(
            output.AsSpan(image.OptionalHeaderOffset + 56, 4),
            newSizeOfImage);

        // Update DataDirectory[1] (Import Table) to point to the relocated+extended table in .nmimp.
        uint newImportDirSize = (uint)((existingImports.Count + 2) * Pe32PlusImage.ImportDescriptorSize);
        WriteDataDirectory(output, image.DataDirectoriesOffset, Pe32PlusImage.DirIndexImport, newSecRva, newImportDirSize);

        // Clear DataDirectory[4] (Security/Authenticode) since the binary was modified and trailing cert moved to NMUNPAT1.
        if (image.NumberOfRvaAndSizes > Pe32PlusImage.DirIndexSecurity)
            WriteDataDirectory(output, image.DataDirectoriesOffset, Pe32PlusImage.DirIndexSecurity, 0, 0);

        // Clear DataDirectory[11] (Bound Import) if present so Windows loader doesn't use stale bound timestamps.
        if (image.NumberOfRvaAndSizes > Pe32PlusImage.DirIndexBoundImport)
            WriteDataDirectory(output, image.DataDirectoriesOffset, Pe32PlusImage.DirIndexBoundImport, 0, 0);

        // Recompute PE CheckSum.
        int checksumOffset = image.OptionalHeaderOffset + 64;
        uint newChecksum = Pe32PlusImage.ComputePeChecksum(output, checksumOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(checksumOffset, 4), newChecksum);

        // Sanity re-parse the patched image before returning!
        var verified = VerifyBytes(output, options.DllName);
        if (!verified.IsValid)
            throw new InvalidOperationException($"Internal error: patched PE failed self-verification: {verified.Message}");

        return (output, new PatchResult(
            Patched: true,
            AlreadyPatched: false,
            MatchKind: matchKind,
            GameVersion: gameVersion,
            InputSha256: image.Sha256Hex,
            OutputSha256: verified.Sha256Hex,
            NewSectionRva: newSecRva,
            NewSectionRawOffset: newSecRawOffset,
            NewSectionRawSize: newSecRawSize,
            TotalImportCount: existingImports.Count + 1,
            PreservedAuthenticodeForUnpatch: hadAuthenticode,
            BackupPath: null,
            Message: $"Injected import '{options.DllName}' ({options.ImportAnchorSymbol}) into section {PatchSectionName} at RVA 0x{newSecRva:X}."));
    }

    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Part of the uniform ClientPatcher instance service surface.")]
    public VerifyResult VerifyBytes(byte[] bytes, string dllName = DefaultHostDllName)
    {
        try
        {
            var image = Pe32PlusImage.Parse(bytes);
            var imports = image.ReadImportDescriptors();
            bool hasImport = imports.Any(d => string.Equals(d.DllName, dllName, StringComparison.OrdinalIgnoreCase));

            bool hasUnpatch = TryReadUnpatchRecord(image, out var unpatchRec);
            string? origSha = hasUnpatch ? Convert.ToHexStringLower(unpatchRec!.OriginalSha256) : null;

            // Validate that our injected descriptor resolves its ILT, IAT, and Hint/Name cleanly.
            if (hasImport)
            {
                var desc = imports.First(d => string.Equals(d.DllName, dllName, StringComparison.OrdinalIgnoreCase));
                if (desc.OriginalFirstThunk == 0 || desc.FirstThunk == 0)
                {
                    return new VerifyResult(false, true, hasUnpatch, imports.Count, image.Sha256Hex, origSha,
                        $"Import descriptor for {dllName} has zero ILT or IAT RVA.");
                }

                int iltOff = image.RvaToFileOffset(desc.OriginalFirstThunk, 16);
                int iatOff = image.RvaToFileOffset(desc.FirstThunk, 16);
                ulong iltEntry = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(iltOff, 8));
                ulong iatEntry = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(iatOff, 8));
                ulong iltNull = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(iltOff + 8, 8));
                ulong iatNull = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(iatOff + 8, 8));

                if (iltEntry == 0 || iltEntry != iatEntry || iltNull != 0 || iatNull != 0)
                {
                    return new VerifyResult(false, true, hasUnpatch, imports.Count, image.Sha256Hex, origSha,
                        $"Import lookup/address tables for {dllName} are malformed.");
                }

                // Confirm the Hint/Name string is readable.
                string anchorName = image.ReadNullTerminatedAsciiAtRva((uint)iltEntry + 2);
                if (string.IsNullOrWhiteSpace(anchorName))
                {
                    return new VerifyResult(false, true, hasUnpatch, imports.Count, image.Sha256Hex, origSha,
                        $"Import hint/name entry for {dllName} is empty.");
                }
            }

            // Confirm checksum matches.
            uint expectedChecksum = Pe32PlusImage.ComputePeChecksum(bytes, image.OptionalHeaderOffset + 64);
            bool checksumOk = image.CheckSum == 0 || image.CheckSum == expectedChecksum;

            bool ok = hasImport && checksumOk;
            string msg = !hasImport
                ? $"Executable does NOT import {dllName} (imports: {imports.Count})."
                : !checksumOk
                    ? $"PE checksum mismatch (header 0x{image.CheckSum:X8}, computed 0x{expectedChecksum:X8})."
                    : $"Valid PE32+ with {dllName} import ({imports.Count} total DLLs, reversible unpatch metadata: {(hasUnpatch ? "present" : "absent")}).";

            return new VerifyResult(ok, hasImport, hasUnpatch, imports.Count, image.Sha256Hex, origSha, msg);
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException)
        {
            return new VerifyResult(false, false, false, 0, Convert.ToHexStringLower(SHA256.HashData(bytes)), null,
                $"Invalid PE32+ image: {ex.Message}");
        }
    }

    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Part of the uniform ClientPatcher instance service surface.")]
    public (byte[] RestoredBytes, UnpatchResult Result) UnpatchBytes(byte[] patchedBytes)
    {
        var image = Pe32PlusImage.Parse(patchedBytes);
        if (!TryReadUnpatchRecord(image, out var rec) || rec is null)
        {
            throw new InvalidOperationException(
                $"No reversible NativeMods unpatch metadata ({PatchSectionName} / NMUNPAT1) found in executable.");
        }

        if (image.NumberOfSections != rec.OriginalSectionCount + 1)
        {
            throw new InvalidOperationException(
                $"Section count mismatch during unpatch: expected {rec.OriginalSectionCount + 1}, found {image.NumberOfSections}.");
        }

        var lastSection = image.Sections[^1];
        if (!string.Equals(lastSection.Name, PatchSectionName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Expected final section to be '{PatchSectionName}', found '{lastSection.Name}'.");
        }

        uint origRawEnd = (uint)(rec.OriginalFileLength - (ulong)rec.TrailingData.Length);
        byte[] restored = new byte[rec.OriginalFileLength];
        Buffer.BlockCopy(patchedBytes, 0, restored, 0, (int)origRawEnd);
        if (rec.TrailingData.Length > 0)
            Buffer.BlockCopy(rec.TrailingData, 0, restored, (int)origRawEnd, rec.TrailingData.Length);

        // Restore original section table slack (40 bytes where .nmimp header lived).
        int removedHeaderOffset = image.SectionTableOffset + rec.OriginalSectionCount * Pe32PlusImage.SectionHeaderSize;
        Buffer.BlockCopy(rec.OriginalHeaderSlackBytes, 0, restored, removedHeaderOffset, Pe32PlusImage.SectionHeaderSize);

        // Restore FileHeader.NumberOfSections.
        BinaryPrimitives.WriteUInt16LittleEndian(
            restored.AsSpan(image.FileHeaderOffset + 2, 2),
            rec.OriginalSectionCount);

        // Restore OptionalHeader.SizeOfImage and CheckSum.
        BinaryPrimitives.WriteUInt32LittleEndian(
            restored.AsSpan(image.OptionalHeaderOffset + 56, 4),
            rec.OriginalSizeOfImage);
        BinaryPrimitives.WriteUInt32LittleEndian(
            restored.AsSpan(image.OptionalHeaderOffset + 64, 4),
            rec.OriginalCheckSum);

        // Restore DataDirectories #1 (Import), #4 (Security), #11 (Bound Import).
        WriteDataDirectory(restored, image.DataDirectoriesOffset, Pe32PlusImage.DirIndexImport,
            rec.OriginalImportRva, rec.OriginalImportSize);
        if (image.NumberOfRvaAndSizes > Pe32PlusImage.DirIndexSecurity)
            WriteDataDirectory(restored, image.DataDirectoriesOffset, Pe32PlusImage.DirIndexSecurity,
                rec.OriginalSecurityOffset, rec.OriginalSecuritySize);
        if (image.NumberOfRvaAndSizes > Pe32PlusImage.DirIndexBoundImport)
            WriteDataDirectory(restored, image.DataDirectoriesOffset, Pe32PlusImage.DirIndexBoundImport,
                rec.OriginalBoundImportRva, rec.OriginalBoundImportSize);

        string restoredSha = Convert.ToHexStringLower(SHA256.HashData(restored));
        string expectedSha = Convert.ToHexStringLower(rec.OriginalSha256);
        bool exactMatch = string.Equals(restoredSha, expectedSha, StringComparison.OrdinalIgnoreCase);

        if (!exactMatch)
        {
            throw new InvalidDataException(
                $"Unpatch integrity check failed: restored SHA-256 {restoredSha} != expected original {expectedSha}.");
        }

        return (restored, new UnpatchResult(
            Unpatched: true,
            PatchedSha256: image.Sha256Hex,
            RestoredSha256: restoredSha,
            ExpectedOriginalSha256: expectedSha,
            ExactByteRestoreVerified: true,
            Message: $"Restored executable byte-for-byte (SHA-256 {restoredSha})."));
    }

    public PatchResult PatchFile(string inputPath, string outputPath, PatchOptions? options = null)
    {
        options ??= new PatchOptions();
        byte[] input = File.ReadAllBytes(inputPath);
        string? backupPath = null;

        if (options.CreateBackup && Path.GetFullPath(inputPath).Equals(Path.GetFullPath(outputPath), StringComparison.OrdinalIgnoreCase))
        {
            backupPath = inputPath + ".bak";
            if (!File.Exists(backupPath))
                File.WriteAllBytes(backupPath, input);
        }

        var (output, result) = PatchBytes(input, options);
        var outDir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(outDir))
            Directory.CreateDirectory(outDir);

        File.WriteAllBytes(outputPath, output);
        return result with { BackupPath = backupPath };
    }

    public UnpatchResult UnpatchFile(string inputPath, string outputPath)
    {
        byte[] patched = File.ReadAllBytes(inputPath);
        var (restored, result) = UnpatchBytes(patched);
        var outDir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(outDir))
            Directory.CreateDirectory(outDir);

        File.WriteAllBytes(outputPath, restored);
        return result;
    }

    // ---- Proxy-DLL (version.dll) Fallback Deployment ----

    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Part of the uniform ClientPatcher instance service surface.")]
    public ProxyDeploymentManifest DeployProxyDll(
        string clientDirectory,
        string proxyDllSourcePath,
        string? hostDllSourcePath = null,
        string proxyDllName = DefaultProxyDllName)
    {
        Directory.CreateDirectory(clientDirectory);

        byte[] proxyBytes = File.ReadAllBytes(proxyDllSourcePath);
        string proxyDest = Path.Combine(clientDirectory, proxyDllName);
        File.WriteAllBytes(proxyDest, proxyBytes);
        string proxySha = Convert.ToHexStringLower(SHA256.HashData(proxyBytes));

        string? hostName = null;
        string? hostSha = null;
        if (!string.IsNullOrWhiteSpace(hostDllSourcePath))
        {
            byte[] hostBytes = File.ReadAllBytes(hostDllSourcePath);
            hostName = DefaultHostDllName;
            File.WriteAllBytes(Path.Combine(clientDirectory, hostName), hostBytes);
            hostSha = Convert.ToHexStringLower(SHA256.HashData(hostBytes));
        }

        var manifest = new ProxyDeploymentManifest(
            SchemaVersion: 1,
            ProxyDllName: proxyDllName,
            ProxyDllSha256: proxySha,
            HostDllName: hostName,
            HostDllSha256: hostSha,
            DeployedAtUtc: DateTimeOffset.UtcNow.ToString("O"),
            WineDllOverridesHint: $"WINEDLLOVERRIDES=\"{Path.GetFileNameWithoutExtension(proxyDllName)}=n,b\"");

        string manifestPath = Path.Combine(clientDirectory, ProxyManifestFileName);
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, PrettyJsonOptions));
        return manifest;
    }

    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Part of the uniform ClientPatcher instance service surface.")]
    public (bool IsValid, string Message, ProxyDeploymentManifest? Manifest) VerifyProxyDll(string clientDirectory)
    {
        string manifestPath = Path.Combine(clientDirectory, ProxyManifestFileName);
        if (!File.Exists(manifestPath))
            return (false, $"Proxy deployment manifest not found at {manifestPath}.", null);

        var manifest = JsonSerializer.Deserialize<ProxyDeploymentManifest>(File.ReadAllText(manifestPath));
        if (manifest is null)
            return (false, "Proxy deployment manifest is invalid JSON.", null);

        string proxyPath = Path.Combine(clientDirectory, manifest.ProxyDllName);
        if (!File.Exists(proxyPath))
            return (false, $"Proxy DLL missing: {proxyPath}", manifest);

        string actualProxySha = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(proxyPath)));
        if (!string.Equals(actualProxySha, manifest.ProxyDllSha256, StringComparison.OrdinalIgnoreCase))
            return (false, $"Proxy DLL SHA-256 mismatch for {manifest.ProxyDllName}.", manifest);

        if (!string.IsNullOrWhiteSpace(manifest.HostDllName))
        {
            string hostPath = Path.Combine(clientDirectory, manifest.HostDllName);
            if (!File.Exists(hostPath))
                return (false, $"Host DLL missing: {hostPath}", manifest);

            string actualHostSha = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(hostPath)));
            if (!string.Equals(actualHostSha, manifest.HostDllSha256, StringComparison.OrdinalIgnoreCase))
                return (false, $"Host DLL SHA-256 mismatch for {manifest.HostDllName}.", manifest);
        }

        return (true, $"Proxy-DLL deployment verified ({manifest.ProxyDllName}; Proton hint: {manifest.WineDllOverridesHint}).", manifest);
    }

    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Part of the uniform ClientPatcher instance service surface.")]
    public bool RemoveProxyDll(string clientDirectory)
    {
        string manifestPath = Path.Combine(clientDirectory, ProxyManifestFileName);
        if (!File.Exists(manifestPath))
            return false;

        var manifest = JsonSerializer.Deserialize<ProxyDeploymentManifest>(File.ReadAllText(manifestPath));
        if (manifest is not null)
        {
            string proxyPath = Path.Combine(clientDirectory, manifest.ProxyDllName);
            if (File.Exists(proxyPath))
                File.Delete(proxyPath);

            if (!string.IsNullOrWhiteSpace(manifest.HostDllName))
            {
                string hostPath = Path.Combine(clientDirectory, manifest.HostDllName);
                if (File.Exists(hostPath))
                    File.Delete(hostPath);
            }
        }

        File.Delete(manifestPath);
        return true;
    }

    // ---- Internal helpers ----

    private (BuildMatchKind MatchKind, string? GameVersion, string? BuildLabel) IdentifyBuild(Pe32PlusImage image, bool allowSynthetic)
    {
        if (_catalog is not null)
        {
            if (_catalog.MatchExecutableSha256(image.Sha256Hex) is { } exact)
                return (BuildMatchKind.ExactStockSha256, exact.Document.GameVersion, exact.Build.Label);

            if (_catalog.MatchByVerifiedCodeSignatures(image) is { } codeMatched)
                return (BuildMatchKind.LocalExileKeyPatched329, codeMatched.GameVersion, "LocalExile-patched 3.29 client");
        }

        if (allowSynthetic)
            return (BuildMatchKind.SyntheticAllowed, "synthetic", "synthetic PE32+ (--allow-synthetic)");

        return (BuildMatchKind.Unknown, null, null);
    }

    private static byte[] BuildImportSectionPayload(
        Pe32PlusImage image,
        IReadOnlyList<PeImportDescriptor> existingImports,
        uint sectionRva,
        string dllName,
        string anchorSymbol,
        byte[] originalSha256Raw,
        byte[] originalSlackBytes,
        byte[] trailingBytes)
    {
        // Layout inside .nmimp:
        //   [0 .. (N+2)*20)         Import descriptors: N existing + 1 new + 1 null terminator
        //   [iltOff .. +16)         64-bit ILT: [hintNameRva, 0]
        //   [iatOff .. +16)         64-bit IAT: [hintNameRva, 0]
        //   [hintNameOff ..)        uint16 Hint(0) + ASCII anchorSymbol + NUL (2-byte aligned)
        //   [dllNameOff ..)         ASCII dllName + NUL
        //   [unpatchOff ..)         NMUNPAT1 reversible unpatch record + trailing bytes
        int descriptorTableBytes = (existingImports.Count + 2) * Pe32PlusImage.ImportDescriptorSize;
        int iltOffset = AlignUpInt(descriptorTableBytes, 8);
        int iatOffset = iltOffset + 16;
        int hintNameOffset = iatOffset + 16;

        byte[] anchorBytes = Encoding.ASCII.GetBytes(anchorSymbol);
        int hintNameSize = AlignUpInt(2 + anchorBytes.Length + 1, 2);

        int dllNameOffset = hintNameOffset + hintNameSize;
        byte[] dllNameBytes = Encoding.ASCII.GetBytes(dllName);
        int dllNameSize = dllNameBytes.Length + 1;

        int unpatchOffset = AlignUpInt(dllNameOffset + dllNameSize, 8);
        int unpatchHeaderSize = 8 + 4 + 8 + 2 + 2 + 4 * 8 + 32 + 40 + 4; // 132 bytes fixed header
        int totalSize = unpatchOffset + unpatchHeaderSize + trailingBytes.Length;

        byte[] buf = new byte[totalSize];

        // 1. Copy existing descriptors verbatim.
        for (int i = 0; i < existingImports.Count; i++)
            Buffer.BlockCopy(existingImports[i].RawBytes, 0, buf, i * Pe32PlusImage.ImportDescriptorSize, Pe32PlusImage.ImportDescriptorSize);

        // 2. Write new descriptor for LocalExile.ClientHost.dll.
        int newDescOff = existingImports.Count * Pe32PlusImage.ImportDescriptorSize;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(newDescOff + 0, 4), sectionRva + (uint)iltOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(newDescOff + 4, 4), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(newDescOff + 8, 4), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(newDescOff + 12, 4), sectionRva + (uint)dllNameOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(newDescOff + 16, 4), sectionRva + (uint)iatOffset);

        // 3. Terminator descriptor at (N+1)*20 is already all-zero.

        // 4. Write 64-bit ILT & IAT pointing at Hint/Name entry.
        ulong hintNameRva = sectionRva + (ulong)hintNameOffset;
        BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(iltOffset, 8), hintNameRva);
        BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(iatOffset, 8), hintNameRva);

        // 5. Write Hint (0) + anchor symbol name.
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(hintNameOffset, 2), 0);
        Buffer.BlockCopy(anchorBytes, 0, buf, hintNameOffset + 2, anchorBytes.Length);

        // 6. Write DLL name.
        Buffer.BlockCopy(dllNameBytes, 0, buf, dllNameOffset, dllNameBytes.Length);

        // 7. Write NMUNPAT1 reversible unpatch record.
        int p = unpatchOffset;
        Buffer.BlockCopy(UnpatchMagic, 0, buf, p, 8); p += 8;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(p, 4), UnpatchFormatVersion); p += 4;
        BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(p, 8), (ulong)image.RawBytes.Length); p += 8;
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(p, 2), image.NumberOfSections); p += 2;
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(p, 2), 0); p += 2;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(p, 4), image.SizeOfImage); p += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(p, 4), image.CheckSum); p += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(p, 4), image.ImportDirectory.VirtualAddress); p += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(p, 4), image.ImportDirectory.Size); p += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(p, 4), image.BoundImportDirectory.VirtualAddress); p += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(p, 4), image.BoundImportDirectory.Size); p += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(p, 4), image.SecurityDirectory.VirtualAddress); p += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(p, 4), image.SecurityDirectory.Size); p += 4;
        Buffer.BlockCopy(originalSha256Raw, 0, buf, p, 32); p += 32;
        Buffer.BlockCopy(originalSlackBytes, 0, buf, p, Pe32PlusImage.SectionHeaderSize); p += Pe32PlusImage.SectionHeaderSize;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(p, 4), (uint)trailingBytes.Length); p += 4;
        if (trailingBytes.Length > 0)
            Buffer.BlockCopy(trailingBytes, 0, buf, p, trailingBytes.Length);

        return buf;
    }

    private sealed record UnpatchRecord(
        ulong OriginalFileLength,
        ushort OriginalSectionCount,
        uint OriginalSizeOfImage,
        uint OriginalCheckSum,
        uint OriginalImportRva,
        uint OriginalImportSize,
        uint OriginalBoundImportRva,
        uint OriginalBoundImportSize,
        uint OriginalSecurityOffset,
        uint OriginalSecuritySize,
        byte[] OriginalSha256,
        byte[] OriginalHeaderSlackBytes,
        byte[] TrailingData);

    private static bool TryReadUnpatchRecord(Pe32PlusImage image, out UnpatchRecord? record)
    {
        record = null;
        var sec = image.Sections.FirstOrDefault(s => string.Equals(s.Name, PatchSectionName, StringComparison.Ordinal));
        if (string.IsNullOrEmpty(sec.Name) || sec.SizeOfRawData == 0)
            return false;

        int start = (int)sec.PointerToRawData;
        int len = (int)Math.Min(sec.SizeOfRawData, (uint)(image.RawBytes.Length - start));
        if (len < 132)
            return false;

        ReadOnlySpan<byte> span = image.RawBytes.AsSpan(start, len);
        int idx = span.IndexOf(UnpatchMagic);
        if (idx < 0 || idx + 132 > span.Length)
            return false;

        int p = idx + 8;
        uint version = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(p, 4)); p += 4;
        if (version != UnpatchFormatVersion)
            return false;

        ulong origLen = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(p, 8)); p += 8;
        ushort origSecCount = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(p, 2)); p += 2;
        p += 2; // reserved
        uint origSizeOfImage = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(p, 4)); p += 4;
        uint origChecksum = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(p, 4)); p += 4;
        uint origImpRva = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(p, 4)); p += 4;
        uint origImpSize = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(p, 4)); p += 4;
        uint origBoundRva = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(p, 4)); p += 4;
        uint origBoundSize = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(p, 4)); p += 4;
        uint origSecOff = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(p, 4)); p += 4;
        uint origSecSize = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(p, 4)); p += 4;
        byte[] origSha = span.Slice(p, 32).ToArray(); p += 32;
        byte[] origSlack = span.Slice(p, Pe32PlusImage.SectionHeaderSize).ToArray(); p += Pe32PlusImage.SectionHeaderSize;
        uint trailingLen = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(p, 4)); p += 4;

        if (p + (long)trailingLen > span.Length)
            return false;

        byte[] trailing = trailingLen > 0 ? span.Slice(p, (int)trailingLen).ToArray() : [];
        record = new UnpatchRecord(
            origLen,
            origSecCount,
            origSizeOfImage,
            origChecksum,
            origImpRva,
            origImpSize,
            origBoundRva,
            origBoundSize,
            origSecOff,
            origSecSize,
            origSha,
            origSlack,
            trailing);
        return true;
    }

    private static void WriteSectionHeader(
        Span<byte> dest,
        string name,
        uint virtualSize,
        uint virtualAddress,
        uint sizeOfRawData,
        uint pointerToRawData,
        uint characteristics)
    {
        dest.Clear();
        byte[] nameBytes = Encoding.ASCII.GetBytes(name);
        nameBytes.AsSpan(0, Math.Min(8, nameBytes.Length)).CopyTo(dest);
        BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(8, 4), virtualSize);
        BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(12, 4), virtualAddress);
        BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(16, 4), sizeOfRawData);
        BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(20, 4), pointerToRawData);
        BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(36, 4), characteristics);
    }

    private static void WriteDataDirectory(byte[] image, int dirsOffset, int index, uint virtualAddress, uint size)
    {
        int off = dirsOffset + index * 8;
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(off, 4), virtualAddress);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(off + 4, 4), size);
    }

    private static int AlignUpInt(int value, int alignment)
    {
        int rem = value % alignment;
        return rem == 0 ? value : value + (alignment - rem);
    }
}
