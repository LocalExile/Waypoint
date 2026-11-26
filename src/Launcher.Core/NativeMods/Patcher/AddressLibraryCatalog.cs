using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocalExile.Client.Patcher;

/// <summary>
/// Deserialized representation of a per-build NativeMods Address Library JSON file
/// (<c>data/addresses/3.29.3d.json</c>, <c>data/addresses/3.29.3c.json</c>).
/// </summary>
public sealed class AddressLibraryDocument
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; }

    [JsonPropertyName("gameVersion")]
    public string GameVersion { get; init; } = "";

    [JsonPropertyName("imageBase")]
    public string ImageBase { get; init; } = "0x140000000";

    [JsonPropertyName("builds")]
    public List<ClientBuildRecord> Builds { get; init; } = [];

    [JsonPropertyName("symbols")]
    public List<AddressSymbolRecord> Symbols { get; init; } = [];

    [JsonPropertyName("offsets")]
    public List<StructOffsetRecord> Offsets { get; init; } = [];

    [JsonPropertyName("constants")]
    public AddressConstants Constants { get; init; } = new();

    public ulong ImageBaseValue => HexParsers.ParseHexUInt64(ImageBase);
}

public sealed class ClientBuildRecord
{
    [JsonPropertyName("buildNumber")]
    public int BuildNumber { get; init; }

    [JsonPropertyName("label")]
    public string Label { get; init; } = "";

    [JsonPropertyName("executableSha256")]
    public string ExecutableSha256 { get; init; } = "";

    [JsonPropertyName("executableSize")]
    public long ExecutableSize { get; init; }

    [JsonPropertyName("steamIndexSha256")]
    public string? SteamIndexSha256 { get; init; }

    [JsonPropertyName("source")]
    public string Source { get; init; } = "";
}

public sealed class AddressSymbolRecord
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("category")]
    public string Category { get; init; } = "";

    [JsonPropertyName("kind")]
    public string Kind { get; init; } = "";

    [JsonPropertyName("rva")]
    public string? Rva { get; init; }

    [JsonPropertyName("va")]
    public string? Va { get; init; }

    [JsonPropertyName("expectedBytes")]
    public string? ExpectedBytes { get; init; }

    [JsonPropertyName("status")]
    public string Status { get; init; } = "";

    [JsonPropertyName("callingConvention")]
    public string? CallingConvention { get; init; }

    [JsonPropertyName("signature")]
    public string? Signature { get; init; }

    [JsonPropertyName("notes")]
    public string? Notes { get; init; }

    [JsonPropertyName("source")]
    public string Source { get; init; } = "";

    public ulong? RvaValue => string.IsNullOrWhiteSpace(Rva) ? null : HexParsers.ParseHexUInt64(Rva);
    public ulong? VaValue => string.IsNullOrWhiteSpace(Va) ? null : HexParsers.ParseHexUInt64(Va);
    public bool IsVerified => RvaValue.HasValue && !string.Equals(Status, "unverified_3_29_3c", StringComparison.Ordinal);
}

public sealed class StructOffsetRecord
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("struct")]
    public string StructName { get; init; } = "";

    [JsonPropertyName("offset")]
    public string Offset { get; init; } = "";

    [JsonPropertyName("type")]
    public string Type { get; init; } = "";

    [JsonPropertyName("status")]
    public string Status { get; init; } = "";

    [JsonPropertyName("notes")]
    public string? Notes { get; init; }

    [JsonPropertyName("source")]
    public string Source { get; init; } = "";

    public ulong OffsetValue => HexParsers.ParseHexUInt64(Offset);
}

public sealed class AddressConstants
{
    [JsonPropertyName("rulesetS2cOpcode")]
    public string RulesetS2cOpcode { get; init; } = "0x2c9";

    [JsonPropertyName("rulesetC2sOpcode")]
    public string RulesetC2sOpcode { get; init; } = "0x2ca";

    [JsonPropertyName("rulesetMaxPayload")]
    public int RulesetMaxPayload { get; init; } = 65535;

    [JsonPropertyName("rulesetSlotCount")]
    public int RulesetSlotCount { get; init; } = 175;

    [JsonPropertyName("rulesetFilledSlotCount")]
    public int RulesetFilledSlotCount { get; init; } = 68;

    [JsonPropertyName("rulesetVtableSlotCount")]
    public int RulesetVtableSlotCount { get; init; } = 9;

    [JsonPropertyName("rulesetBaseSize")]
    public int RulesetBaseSize { get; init; } = 24;

    [JsonPropertyName("reservedOpcodeMin")]
    public string ReservedOpcodeMin { get; init; } = "0x2fc";

    [JsonPropertyName("chatS2cOpcode")]
    public string ChatS2cOpcode { get; init; } = "0x17";

    [JsonPropertyName("chatC2sOpcode")]
    public string ChatC2sOpcode { get; init; } = "0x15";

    [JsonPropertyName("chatFallbackPrefix")]
    public string ChatFallbackPrefix { get; init; } = "##NM ";

    [JsonPropertyName("gealClientBuiltinCount")]
    public int GealClientBuiltinCount { get; init; } = 724;

    [JsonPropertyName("gealWhileIterationCap")]
    public int GealWhileIterationCap { get; init; } = 1000;

    [JsonPropertyName("bundleHashSeed")]
    public string BundleHashSeed { get; init; } = "0x1337b33f";

    [JsonPropertyName("bundleBlockSize")]
    public int BundleBlockSize { get; init; } = 262144;

    [JsonPropertyName("oodleStoredBlockMagic")]
    public string OodleStoredBlockMagic { get; init; } = "cc06";

    [JsonPropertyName("msvcCxxExceptionCode")]
    public string MsvcCxxExceptionCode { get; init; } = "0xe06d7363";

    [JsonPropertyName("peHeaderSlackBytes")]
    public int PeHeaderSlackBytes { get; init; } = 136;
}

/// <summary>
/// Loads and queries the 3.29.3c / 3.29.3d Address Library database files.
/// </summary>
public sealed class AddressLibraryCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private readonly Dictionary<string, AddressLibraryDocument> _byVersion = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<AddressLibraryDocument> Documents => _byVersion.Values;

    public static AddressLibraryCatalog LoadFromDirectory(string directoryPath)
    {
        if (!Directory.Exists(directoryPath))
            throw new DirectoryNotFoundException($"Address library directory not found: {directoryPath}");

        var catalog = new AddressLibraryCatalog();
        foreach (var file in Directory.EnumerateFiles(directoryPath, "*.json", SearchOption.TopDirectoryOnly))
        {
            if (file.EndsWith(".schema.json", StringComparison.OrdinalIgnoreCase))
                continue;

            var json = File.ReadAllText(file);
            var doc = JsonSerializer.Deserialize<AddressLibraryDocument>(json, JsonOptions)
                ?? throw new InvalidDataException($"Failed to parse address library file: {file}");

            if (doc.SchemaVersion != 1)
                throw new InvalidDataException($"Unsupported address library schemaVersion {doc.SchemaVersion} in {file}");

            catalog._byVersion[doc.GameVersion] = doc;
        }

        if (catalog._byVersion.Count == 0)
            throw new InvalidDataException($"No address library JSON files found in: {directoryPath}");

        return catalog;
    }

    /// <summary>
    /// Searches upward from <paramref name="startDirectory"/> (or the current directory / app base)
    /// for <c>data/addresses</c>.
    /// </summary>
    public static string? LocateDefaultDirectory(string? startDirectory = null)
    {
        var roots = new[]
        {
            startDirectory,
            Directory.GetCurrentDirectory(),
            AppContext.BaseDirectory,
        };

        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root))
                continue;

            var dir = new DirectoryInfo(root);
            while (dir is not null)
            {
                var candidate = Path.Combine(dir.FullName, "data", "addresses");
                if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "3.29.3d.json")))
                    return candidate;
                dir = dir.Parent;
            }
        }

        return null;
    }

    public AddressLibraryDocument? GetByGameVersion(string gameVersion) =>
        _byVersion.TryGetValue(gameVersion, out var doc) ? doc : null;

    public (AddressLibraryDocument Document, ClientBuildRecord Build)? MatchExecutableSha256(string sha256Hex)
    {
        foreach (var doc in _byVersion.Values)
        {
            foreach (var build in doc.Builds)
            {
                if (string.Equals(build.ExecutableSha256, sha256Hex, StringComparison.OrdinalIgnoreCase))
                    return (doc, build);
            }
        }

        return null;
    }

    /// <summary>
    /// Checks whether a PE32+ image's code bytes match the verified 3.29 anchor signatures
    /// (used to recognize LocalExile <c>Game.patched.exe</c> copies whose login keys or
    /// loose-files branches were already patched by <c>GameServer.Auth</c>).
    /// </summary>
    public AddressLibraryDocument? MatchByVerifiedCodeSignatures(Pe32PlusImage image)
    {
        if (image.ImageBase != 0x140000000UL)
            return null;

        // Require all core anchor signatures to match to avoid false positives.
        string[] anchors =
        [
            "frame.state_stack_pass_a_hook",
            "network.app_global_ref",
            "ui.control_load",
            "files.text_stream_open",
            "allocator.alloc",
            "runtime.wstring_assign",
        ];

        foreach (var version in new[] { "3.29.3d", "3.29.3c" })
        {
            if (!_byVersion.TryGetValue(version, out var doc))
                continue;

            int matched = 0;
            int required = 0;
            foreach (var anchorName in anchors)
            {
                var sym = doc.Symbols.FirstOrDefault(s => s.Name == anchorName);
                if (sym?.RvaValue is null || string.IsNullOrWhiteSpace(sym.ExpectedBytes))
                    continue;

                required++;
                int byteLen = sym.ExpectedBytes.Length / 2;
                if (!image.TryRvaToFileOffset((uint)sym.RvaValue.Value, byteLen, out int fileOffset))
                    break;

                if (HexParsers.MatchesPattern(image.RawBytes.AsSpan(fileOffset, byteLen), sym.ExpectedBytes))
                    matched++;
            }

            if (required >= 2 && matched == required)
                return doc;
        }

        return null;
    }
}

public static class HexParsers
{
    public static ulong ParseHexUInt64(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[2..];
        return ulong.Parse(trimmed, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }

    public static bool MatchesPattern(ReadOnlySpan<byte> actual, string hexPattern)
    {
        if (hexPattern.Length % 2 != 0 || actual.Length != hexPattern.Length / 2)
            return false;

        for (int i = 0; i < actual.Length; i++)
        {
            char hi = hexPattern[i * 2];
            char lo = hexPattern[i * 2 + 1];
            if (hi == '?' && lo == '?')
                continue;

            int hiVal = ParseNibble(hi);
            int loVal = ParseNibble(lo);
            if (hiVal < 0 || loVal < 0)
                return false;

            byte expected = (byte)((hiVal << 4) | loVal);
            if (actual[i] != expected)
                return false;
        }

        return true;
    }

    private static int ParseNibble(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1,
    };
}
