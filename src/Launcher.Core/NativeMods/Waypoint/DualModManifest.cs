using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace LocalExile.Client.Waypoint;

/// <summary>
/// Direction of a custom mod opcode declared in <c>mod.json</c>.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OpcodeDirection
{
    ClientToServer,
    ServerToClient,
    Bidirectional
}

/// <summary>
/// Declares a custom opcode used over the mod's typed network channel.
/// </summary>
public sealed record CustomOpcodeDeclaration(
    [property: JsonPropertyName("id")] ushort Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("direction")] OpcodeDirection Direction = OpcodeDirection.Bidirectional);

/// <summary>
/// Declares a content registry entry (table row patch) supplied by a dual-side mod.
/// </summary>
public sealed record ContentRegistryDeclaration(
    [property: JsonPropertyName("table")] string Table,
    [property: JsonPropertyName("entryId")] string EntryId,
    [property: JsonPropertyName("mode")] string Mode = "append");

/// <summary>
/// Metadata linking a client mod payload to its companion LocalExile server mod.
/// </summary>
public sealed record ServerHalfMetadata(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("minVersion")] string MinVersion = "1.0.0",
    [property: JsonPropertyName("supportsGracefulFallback")] bool SupportsGracefulFallback = true,
    [property: JsonPropertyName("channelName")] string? ChannelName = null);

/// <summary>
/// SHA-256 integrity records for a dual-side mod package.
/// </summary>
public sealed record PackageIntegrity(
    [property: JsonPropertyName("packageSha256")] string? PackageSha256 = null,
    [property: JsonPropertyName("files")] IReadOnlyDictionary<string, string>? Files = null);

/// <summary>
/// NativeMods extension section embedded as <c>nativeClient</c> inside a standard LocalExile <c>mod.json</c>.
/// A mod without this section remains a 100% standard LocalExile server mod.
/// </summary>
public sealed record NativeClientManifest
{
    [JsonPropertyName("required")]
    public bool Required { get; init; }

    [JsonPropertyName("sdkVersionRange")]
    public string SdkVersionRange { get; init; } = ">=0.1.0-poc <2.0.0";

    [JsonPropertyName("supportedBuilds")]
    public IReadOnlyList<string> SupportedBuilds { get; init; } = ["3.29.3d", "3.29.3c"];

    [JsonPropertyName("assembly")]
    public string? Assembly { get; init; }

    [JsonPropertyName("assemblySha256")]
    public string? AssemblySha256 { get; init; }

    [JsonPropertyName("payloadDir")]
    public string PayloadDir { get; init; } = "client";

    [JsonPropertyName("bundleFile")]
    public string? BundleFile { get; init; }

    [JsonPropertyName("bundleSha256")]
    public string? BundleSha256 { get; init; }

    [JsonPropertyName("overlays")]
    public IReadOnlyList<string> Overlays { get; init; } = [];

    [JsonPropertyName("replaces")]
    public IReadOnlyList<string> Replaces { get; init; } = [];

    /// <summary>
    /// Relative paths under <see cref="PayloadDir"/> to declarative <c>.datpatch.json</c> files
    /// merged conflict-free into <c>Mods/Client/.merged-cache/</c> by <c>DatPatchMerger</c>.
    /// </summary>
    [JsonPropertyName("datPatches")]
    public IReadOnlyList<string> DatPatches { get; init; } = [];

    [JsonPropertyName("rawAccess")]
    public bool RawAccess { get; init; }

    [JsonPropertyName("rawAccessJustification")]
    public string? RawAccessJustification { get; init; }

    [JsonPropertyName("customOpcodes")]
    public IReadOnlyList<CustomOpcodeDeclaration> CustomOpcodes { get; init; } = [];

    [JsonPropertyName("gealBuiltins")]
    public IReadOnlyList<string> GealBuiltins { get; init; } = [];

    [JsonPropertyName("customControls")]
    public IReadOnlyList<string> CustomControls { get; init; } = [];

    [JsonPropertyName("contentRegistries")]
    public IReadOnlyList<ContentRegistryDeclaration> ContentRegistries { get; init; } = [];

    [JsonPropertyName("serverHalf")]
    public ServerHalfMetadata? ServerHalf { get; init; }

    [JsonPropertyName("integrity")]
    public PackageIntegrity? Integrity { get; init; }
}

/// <summary>
/// Represents a LocalExile <c>mod.json</c> manifest extended with an optional <c>nativeClient</c> section
/// for dual-side (server + 3.29 client) mods. Fully compatible with Waypoint's <c>ModCatalog</c> and
/// <c>LocalExile-Mods/tools/build-index.ps1</c>.
/// </summary>
public sealed partial record DualModManifest
{
    public const int MaxManifestBytes = 128 * 1024;
    public const long MaxPackageBytes = 64L * 1024 * 1024;
    public const int MaxPackageFiles = 2000;

    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9_.-]{0,127}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ValidIdRegex();

    [GeneratedRegex(@"\A[a-z0-9][a-z0-9_.-]{0,127}\z", RegexOptions.CultureInvariant)]
    private static partial Regex LowercaseIdRegex();

    [GeneratedRegex(@"\A[0-9a-fA-F]{64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Regex();

    public static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    [JsonPropertyName("id")]
    public string Id { get; init; } = "";

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("description")]
    public string Description { get; init; } = "";

    [JsonPropertyName("author")]
    public string Author { get; init; } = "";

    [JsonPropertyName("version")]
    public string Version { get; init; } = "1.0.0";

    [JsonPropertyName("compatibleClientVersions")]
    public IReadOnlyList<string> CompatibleClientVersions { get; init; } = [];

    [JsonPropertyName("kind")]
    public string Kind { get; init; } = "feature";

    [JsonPropertyName("depends")]
    public IReadOnlyList<string> Depends { get; init; } = [];

    [JsonPropertyName("assembly")]
    public string? Assembly { get; init; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; } = true;

    [JsonPropertyName("nativeClient")]
    public NativeClientManifest? NativeClient { get; init; }

    [JsonIgnore]
    public string EffectiveAssembly => string.IsNullOrWhiteSpace(Assembly) ? $"{Id}.dll" : Assembly;

    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Id : Name;

    [JsonIgnore]
    public bool HasNativeClient => NativeClient is not null;

    /// <summary>Matches Waypoint's <c>ModCatalog.ValidId</c>.</summary>
    public static bool IsValidId(string? value) =>
        !string.IsNullOrEmpty(value) && value is not "." and not ".." && ValidIdRegex().IsMatch(value);

    /// <summary>Matches LocalExile-Mods catalog rule (lower-case folder/id).</summary>
    public static bool IsCanonicalCatalogId(string? value) =>
        !string.IsNullOrEmpty(value) && value is not "." and not ".." && LowercaseIdRegex().IsMatch(value);

    public static bool IsValidSha256(string? value) =>
        !string.IsNullOrEmpty(value) && Sha256Regex().IsMatch(value);

    public static bool IsSafeAssemblyFileName(string? fileName) =>
        !string.IsNullOrWhiteSpace(fileName) &&
        Path.GetFileName(fileName) == fileName &&
        fileName.IndexOfAny(['/', '\\', ':']) < 0 &&
        fileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);

    public static DualModManifest Parse(string json, string? fallbackFolderId = null)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaxManifestBytes)
            throw new InvalidDataException($"mod.json exceeds the {MaxManifestBytes / 1024} KB Waypoint limit.");

        var parsed = JsonSerializer.Deserialize<DualModManifest>(json, SerializerOptions)
            ?? throw new InvalidDataException("mod.json did not deserialize to a JSON object.");

        if (string.IsNullOrWhiteSpace(parsed.Id) && !string.IsNullOrWhiteSpace(fallbackFolderId))
            parsed = parsed with { Id = fallbackFolderId };

        return parsed;
    }

    public static DualModManifest LoadFromFile(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
            throw new FileNotFoundException("mod.json not found.", path);
        if (info.Length > MaxManifestBytes)
            throw new InvalidDataException($"mod.json exceeds the {MaxManifestBytes / 1024} KB Waypoint limit.");

        string? folderName = Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(path)));
        return Parse(File.ReadAllText(path), folderName);
    }

    public string ToJson() => JsonSerializer.Serialize(this, SerializerOptions);
}

/// <summary>
/// A single entry in a LocalExile-Mods <c>mods.json</c> (v1) repository index,
/// optionally annotated with NativeMods client summary metadata for Waypoint's Browse UI.
/// </summary>
public sealed record WaypointCatalogModEntry
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = "";

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("description")]
    public string Description { get; init; } = "";

    [JsonPropertyName("author")]
    public string Author { get; init; } = "";

    [JsonPropertyName("version")]
    public string Version { get; init; } = "";

    [JsonPropertyName("compatibleClientVersions")]
    public IReadOnlyList<string> CompatibleClientVersions { get; init; } = [];

    [JsonPropertyName("kind")]
    public string Kind { get; init; } = "feature";

    [JsonPropertyName("type")]
    public string Type { get; init; } = "project";

    [JsonPropertyName("path")]
    public string Path { get; init; } = "";

    [JsonPropertyName("depends")]
    public IReadOnlyList<string> Depends { get; init; } = [];

    [JsonPropertyName("settings")]
    public bool Settings { get; init; }

    [JsonPropertyName("nativeClient")]
    public WaypointCatalogClientSummary? NativeClient { get; init; }

    public static WaypointCatalogModEntry FromManifest(DualModManifest manifest, bool isProject, bool hasSettings)
    {
        WaypointCatalogClientSummary? clientSummary = manifest.NativeClient is { } nc
            ? new WaypointCatalogClientSummary(
                nc.Required,
                nc.SdkVersionRange,
                nc.RawAccess,
                nc.Replaces.Count,
                !string.IsNullOrWhiteSpace(nc.Assembly),
                nc.DatPatches.Count)
            : null;

        return new WaypointCatalogModEntry
        {
            Id = manifest.Id,
            Name = manifest.DisplayName,
            Description = manifest.Description,
            Author = manifest.Author,
            Version = manifest.Version,
            CompatibleClientVersions = manifest.CompatibleClientVersions,
            Kind = manifest.Kind.ToLowerInvariant(),
            Type = isProject ? "project" : "script",
            Path = $"mods/{manifest.Id}",
            Depends = manifest.Depends,
            Settings = hasSettings,
            NativeClient = clientSummary
        };
    }
}

public sealed record WaypointCatalogClientSummary(
    [property: JsonPropertyName("required")] bool Required,
    [property: JsonPropertyName("sdkVersionRange")] string SdkVersionRange,
    [property: JsonPropertyName("rawAccess")] bool RawAccess,
    [property: JsonPropertyName("replacesCount")] int ReplacesCount,
    [property: JsonPropertyName("hasClientAssembly")] bool HasClientAssembly,
    [property: JsonPropertyName("datPatchesCount")] int DatPatchesCount = 0);

/// <summary>
/// Version-1 repository catalog index matching <c>LocalExile-Mods/mods.json</c>.
/// </summary>
public sealed record WaypointCatalogIndex
{
    [JsonPropertyName("version")]
    public int Version { get; init; } = 1;

    [JsonPropertyName("mods")]
    public IReadOnlyList<WaypointCatalogModEntry> Mods { get; init; } = [];

    public static WaypointCatalogIndex Parse(string json)
    {
        var index = JsonSerializer.Deserialize<WaypointCatalogIndex>(json, DualModManifest.SerializerOptions)
            ?? throw new InvalidDataException("mods.json did not deserialize to an object.");
        if (index.Version != 1)
            throw new InvalidDataException($"Unsupported mods.json version {index.Version}; expected 1.");
        return index;
    }

    public string ToJson() => JsonSerializer.Serialize(this, DualModManifest.SerializerOptions);
}
