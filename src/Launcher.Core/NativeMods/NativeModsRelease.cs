using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using LocalExile.Client.Waypoint;

namespace Launcher.Core.NativeMods;

/// <summary>A published NativeMods script extender release from GitHub (<c>mrnamwen/LocalExile-NativeMods</c>).</summary>
public sealed record NativeModsReleaseInfo(
    string Version,
    string Tag,
    string Page,
    string? ZipName,
    string? ZipUrl,
    string? Sha256,
    string? HashUrl,
    long ZipBytes,
    string Notes = "",
    DateTimeOffset? Published = null);

/// <summary>A verified extender bundle staged in Waypoint's data folder (or pointing at an imported local dist directory),
/// ready for <see cref="NativeModsExtender.InstallAsync"/>.</summary>
public sealed record StagedExtenderBundle(
    string Version,
    string NativeShimPath,
    string ManagedHostDir,
    string? ProxyVersionDllPath,
    string? AddressLibraryDir,
    string? StagingWorkDir);

/// <summary>
/// Fetches and verifies NativeMods extender releases from GitHub (<see cref="Repository"/>) or stages a local
/// <c>.zip</c> / <c>dist/win-x64/</c> folder. Nothing here writes into a LocalExile folder.
/// </summary>
public static partial class NativeModsReleases
{
    public const string Repository = "mrnamwen/LocalExile-NativeMods";
    public const string LatestUrl = $"https://api.github.com/repos/{Repository}/releases/latest";
    public const string ReleasesPage = $"https://github.com/{Repository}/releases";

    // Self-contained win-x64 .NET 10 runtime + native shims + address tables is ~90-120 MB uncompressed.
    public const long MaxZipBytes = 256L * 1024 * 1024;
    public const long MaxExtractedBytes = 512L * 1024 * 1024;
    public const int MaxExtractedFiles = 4000;

    public static string DownloadsFolder => Path.Combine(SettingsStore.DataDirectory, "nativemods-downloads");

    public static readonly IReadOnlyList<string> RequiredHostFiles =
    [
        "hostfxr.dll",
        "coreclr.dll",
        "LocalExile.Client.Host.dll",
        "LocalExile.Client.Host.runtimeconfig.json",
    ];

    [GeneratedRegex(@"\A[0-9a-fA-F]{64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Hex64();

    /// <summary>Parses GitHub's <c>/releases/latest</c> JSON into a <see cref="NativeModsReleaseInfo"/>; null if no valid tag.</summary>
    public static NativeModsReleaseInfo? ParseRelease(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;
        string tag = root.TryGetProperty("tag_name", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(tag)) return null;
        string version = tag.TrimStart('v', 'V');

        JsonElement[] assets = root.TryGetProperty("assets", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().ToArray()
            : [];
        static string Name(JsonElement a) => a.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? "" : "";
        static string? Url(JsonElement a) => a.ValueKind == JsonValueKind.Object && a.TryGetProperty("browser_download_url", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;

        var zip = assets.FirstOrDefault(a =>
            Name(a).EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
            Name(a).Contains("win-x64", StringComparison.OrdinalIgnoreCase));
        if (zip.ValueKind != JsonValueKind.Object)
            zip = assets.FirstOrDefault(a => Name(a).EndsWith(".zip", StringComparison.OrdinalIgnoreCase));

        string? zipName = zip.ValueKind == JsonValueKind.Object ? Name(zip) : null;
        string? digest = zip.ValueKind == JsonValueKind.Object
            && zip.TryGetProperty("digest", out var dg)
            && dg.ValueKind == JsonValueKind.String
            && dg.GetString() is { } text
            && text.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
            && Hex64().IsMatch(text[7..])
                ? text[7..].ToLowerInvariant()
                : null;

        var hashAsset = zipName is null
            ? default
            : assets.FirstOrDefault(a => Name(a).Equals(zipName + ".sha256", StringComparison.OrdinalIgnoreCase));

        long bytes = zip.ValueKind == JsonValueKind.Object && zip.TryGetProperty("size", out var sz) && sz.TryGetInt64(out long b) ? b : 0;
        string page = root.TryGetProperty("html_url", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? ReleasesPage : ReleasesPage;
        string notes = root.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.String ? body.GetString() ?? "" : "";
        DateTimeOffset? published = root.TryGetProperty("published_at", out var pub) && pub.ValueKind == JsonValueKind.String && pub.TryGetDateTimeOffset(out var at) ? at : null;

        return new NativeModsReleaseInfo(version, tag, page, zipName, Url(zip), digest, Url(hashAsset), bytes, notes, published);
    }

    /// <summary>Queries GitHub for the latest published NativeMods release; null on 404 or missing tag.</summary>
    public static async Task<NativeModsReleaseInfo?> LatestAsync(HttpClient client, CancellationToken stop = default)
    {
        using var response = await client.GetAsync(LatestUrl, stop);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return ParseRelease(await response.Content.ReadAsStringAsync(stop));
    }

    /// <summary>Downloads a NativeMods GitHub release zip, verifies its SHA-256, extracts it safely, and validates its contents.</summary>
    public static async Task<StagedExtenderBundle> DownloadAndStageAsync(
        HttpClient client,
        NativeModsReleaseInfo release,
        Action<long, long>? progress,
        CancellationToken stop)
    {
        if (string.IsNullOrWhiteSpace(release.ZipUrl) || string.IsNullOrWhiteSpace(release.ZipName))
            throw new InvalidOperationException(L.Text("Release {0} does not contain a win-x64 zip archive.", release.Tag));

        string? expected = release.Sha256;
        if (expected is null && !string.IsNullOrWhiteSpace(release.HashUrl))
            expected = LocalExileReleases.HashOf(await GitHubDownload.GetStringAsync(client, release.HashUrl, stop));
        if (expected is null)
            throw new InvalidDataException(L.Text("Release {0} has no SHA-256 for {1}; nothing was installed.", release.Tag, release.ZipName));

        string work = NewWorkFolder();
        try
        {
            string zipPath = Path.Combine(work, "nativemods.zip");
            using (var response = await GitHubDownload.GetAsync(client, release.ZipUrl, stop))
            {
                response.EnsureSuccessStatusCode();
                long total = response.Content.Headers.ContentLength ?? release.ZipBytes;
                if (total > MaxZipBytes)
                    throw new InvalidDataException(L.Text("The NativeMods download exceeds the 256 MB size limit."));

                await using var input = await response.Content.ReadAsStreamAsync(stop);
                await using var output = File.Create(zipPath);
                var buffer = new byte[81920];
                long done = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, stop)) > 0)
                {
                    if ((done += read) > MaxZipBytes)
                        throw new InvalidDataException(L.Text("The NativeMods download exceeds the 256 MB size limit."));
                    await output.WriteAsync(buffer.AsMemory(0, read), stop);
                    progress?.Invoke(done, total);
                }
            }

            string actual = await LocalExileReleases.FileHashAsync(zipPath, stop);
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(L.Text("{0} does not match its SHA-256; nothing was installed.", release.ZipName));

            string extracted = Path.Combine(work, "bundle");
            ExtractZipSafely(zipPath, extracted);
            File.Delete(zipPath);
            return InspectBundleDirectory(extracted, release.Version, work);
        }
        catch
        {
            TryDelete(work);
            throw;
        }
    }

    /// <summary>Stages a local NativeMods release <c>.zip</c> after verifying zip-slip, size, file-count, and bundle integrity.</summary>
    public static StagedExtenderBundle StageLocalZip(string zipPath, string? versionOverride = null)
    {
        if (!File.Exists(zipPath) || !ModCatalog.Physical(zipPath))
            throw new InvalidDataException(L.Text("{0} is not a file Waypoint can read.", zipPath));
        long length = new FileInfo(zipPath).Length;
        if (length > MaxZipBytes)
            throw new InvalidDataException(L.Text("The NativeMods archive exceeds the 256 MB size limit."));

        string work = NewWorkFolder();
        try
        {
            string extracted = Path.Combine(work, "bundle");
            ExtractZipSafely(zipPath, extracted);
            string version = versionOverride ?? GuessVersionFromName(Path.GetFileNameWithoutExtension(zipPath));
            return InspectBundleDirectory(extracted, version, work);
        }
        catch
        {
            TryDelete(work);
            throw;
        }
    }

    /// <summary>Validates and stages a local NativeMods <c>dist/win-x64/</c> folder without modifying the source folder.</summary>
    public static StagedExtenderBundle StageLocalDirectory(string directoryPath, string? versionOverride = null)
    {
        if (!Directory.Exists(directoryPath) || !ModCatalog.Physical(directoryPath))
            throw new InvalidDataException(L.Text("{0} is not a folder Waypoint can read.", directoryPath));
        string version = versionOverride ?? ReadBundleVersion(directoryPath) ?? "local";
        return InspectBundleDirectory(Path.GetFullPath(directoryPath), version, stagingWorkDir: null);
    }

    /// <summary>Cleans up a temporary download/extraction folder created by <see cref="DownloadAndStageAsync"/> or <see cref="StageLocalZip"/>.</summary>
    public static void Discard(StagedExtenderBundle bundle)
    {
        if (string.IsNullOrWhiteSpace(bundle.StagingWorkDir)) return;
        string full = Path.GetFullPath(bundle.StagingWorkDir);
        string root = Path.GetFullPath(DownloadsFolder) + Path.DirectorySeparatorChar;
        if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            TryDelete(full);
    }

    /// <summary>Extracts a zip into <paramref name="destination"/>, stripping a single wrapper folder when present and rejecting unsafe paths or oversized archives.</summary>
    internal static void ExtractZipSafely(string zipPath, string destination)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var entries = zip.Entries
            .Select(e => (Entry: e, Name: e.FullName.Replace('\\', '/')))
            .Where(x => !x.Name.EndsWith('/'))
            .ToList();

        if (entries.Count == 0)
            throw new InvalidDataException(L.Text("The NativeMods archive contains no files."));
        if (entries.Count > MaxExtractedFiles)
            throw new InvalidDataException(L.Text("The NativeMods archive contains more than {0} files.", MaxExtractedFiles));

        string? wrapper = null;
        if (entries.All(x => x.Name.Contains('/')))
        {
            string first = entries[0].Name.Split('/')[0] + "/";
            if (entries.All(x => x.Name.StartsWith(first, StringComparison.Ordinal)))
                wrapper = first;
        }

        long totalBytes = 0;
        foreach (var (entry, raw) in entries)
        {
            string rel = wrapper is not null ? raw[wrapper.Length..] : raw;
            string[] parts = rel.Split('/');
            if (parts.Any(p => p is "" or "." or "..") || rel.Contains(':'))
                throw new InvalidDataException(L.Text("The NativeMods archive holds an unsafe path, {0}.", raw));
            totalBytes += entry.Length;
            if (totalBytes > MaxExtractedBytes)
                throw new InvalidDataException(L.Text("The extracted NativeMods bundle exceeds the 512 MB size limit."));
        }

        Directory.CreateDirectory(destination);
        foreach (var (entry, raw) in entries)
        {
            string rel = wrapper is not null ? raw[wrapper.Length..] : raw;
            string target = Path.Combine(destination, Path.Combine(rel.Split('/')));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }
    }

    internal static StagedExtenderBundle InspectBundleDirectory(string bundleDir, string version, string? stagingWorkDir)
    {
        string shimPath = Path.Combine(bundleDir, WaypointExtenderManager.NativeShimFileName);
        if (!File.Exists(shimPath))
            throw new InvalidDataException(L.Text("The NativeMods bundle is missing {0}.", WaypointExtenderManager.NativeShimFileName));

        // Support both flat dist/win-x64/ layout (build-win64.sh) and split nativemods-host/ subdirectory layout.
        string hostDir = Directory.Exists(Path.Combine(bundleDir, WaypointExtenderManager.ManagedHostFolderName))
            ? Path.Combine(bundleDir, WaypointExtenderManager.ManagedHostFolderName)
            : bundleDir;

        foreach (string required in RequiredHostFiles)
        {
            if (!File.Exists(Path.Combine(hostDir, required)))
                throw new InvalidDataException(L.Text("The NativeMods bundle is missing required runtime file {0}.", required));
        }

        string proxyPath = Path.Combine(bundleDir, WaypointExtenderManager.ProxyDllFileName);
        string? resolvedProxy = File.Exists(proxyPath) ? proxyPath : null;

        string addrInBundle = Path.Combine(bundleDir, WaypointExtenderManager.AddressLibraryFolderName);
        string addrInHost = Path.Combine(hostDir, WaypointExtenderManager.AddressLibraryFolderName);
        bool separateHostDir = !string.Equals(bundleDir, hostDir, StringComparison.OrdinalIgnoreCase);
        bool hasAddrInHost = File.Exists(Path.Combine(addrInHost, "3.29.3d.json"));
        bool hasAddrInBundle = separateHostDir && File.Exists(Path.Combine(addrInBundle, "3.29.3d.json"));
        if (!hasAddrInHost && !hasAddrInBundle)
            throw new InvalidDataException(L.Text("The NativeMods bundle is missing {0}.", $"{WaypointExtenderManager.AddressLibraryFolderName}/3.29.3d.json"));

        string? resolvedAddresses = hasAddrInBundle && !hasAddrInHost ? addrInBundle : null;

        string resolvedVersion = ReadBundleVersion(bundleDir) ?? version;
        return new StagedExtenderBundle(resolvedVersion, shimPath, hostDir, resolvedProxy, resolvedAddresses, stagingWorkDir);
    }

    private static string? ReadBundleVersion(string bundleDir)
    {
        string meta = Path.Combine(bundleDir, "version.txt");
        if (File.Exists(meta))
        {
            string text = File.ReadAllText(meta).Trim();
            if (text.Length is > 0 and <= 64) return text.TrimStart('v', 'V');
        }
        return null;
    }

    private static string GuessVersionFromName(string name)
    {
        var match = Regex.Match(name, @"(\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?)", RegexOptions.CultureInvariant);
        return match.Success ? match.Groups[1].Value : "local";
    }

    private static string NewWorkFolder()
    {
        string work = Path.Combine(DownloadsFolder, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        return work;
    }

    private static void TryDelete(string folder)
    {
        try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
