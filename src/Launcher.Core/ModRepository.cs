using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Launcher.Core;

/// <summary>A GitHub repository of mods laid out as LocalExile-Mods is: <c>mods/&lt;id&gt;/</c> folders and a <c>mods.json</c>
/// index at its root, and each mod's releases tagged <c>&lt;id&gt;-v&lt;version&gt;</c> with a <c>&lt;id&gt;-&lt;version&gt;.zip</c>.</summary>
public sealed record ModSource(string Owner, string Name, string Branch = "main")
{
    public static ModSource Official { get; } = new("LocalExile", "LocalExile-Mods");
    public string Key => $"{Owner}/{Name}";
    public string Page => $"https://github.com/{Owner}/{Name}";
    public bool IsOfficial => Key.Equals(Official.Key, StringComparison.OrdinalIgnoreCase);
    internal string IndexUrl => $"https://raw.githubusercontent.com/{Owner}/{Name}/{Branch}/{ModRepository.IndexName}";
    internal string ReleasesUrl => $"https://api.github.com/repos/{Owner}/{Name}/releases?per_page=100";
    internal string SourceZipUrl => $"https://codeload.github.com/{Owner}/{Name}/zip/refs/heads/{Branch}";
    internal string FileUrl(string path) => $"https://raw.githubusercontent.com/{Owner}/{Name}/{Branch}/{path}";
    /// <summary>A mod's folder on GitHub.</summary>
    public string ModPage(ModListing mod) => $"{Page}/tree/{Branch}/{mod.Path}";

    /// <summary>"owner/name", or a github.com URL of the repository (a trailing ".git", "/tree/&lt;branch&gt;" or slash is fine);
    /// null for anything else.</summary>
    public static ModSource? Parse(string? text)
    {
        var match = Regex.Match(text?.Trim() ?? "",
            @"\A(?:(?:https?://)?(?:www\.)?github\.com/)?([A-Za-z0-9](?:[A-Za-z0-9-]{0,38}))/([A-Za-z0-9._-]{1,100}?)(?:\.git)?(?:/tree/([A-Za-z0-9._/-]{1,100}))?/?\z",
            RegexOptions.CultureInvariant);
        if (!match.Success || match.Groups[2].Value is "." or "..") return null;
        string branch = match.Groups[3].Success ? match.Groups[3].Value.TrimEnd('/') : "main";
        return branch.Split('/').Any(p => p is "" or "." or "..") ? null : new(match.Groups[1].Value, match.Groups[2].Value, branch);
    }
}

/// <summary>One mod of a repository's index. <paramref name="ClientVersions"/> are the LocalExile versions it declares it
/// works with (its mod.json's <c>compatibleClientVersions</c>); empty when it declares none.</summary>
public sealed record ModListing(string Id, string Name, string Description, string Author, string Version, string Kind,
    bool IsProject, string Path, string[] Depends, bool HasSettings, IReadOnlyList<string>? ClientVersions = null)
{
    public ReleaseVersion? ParsedVersion => ReleaseVersion.Parse(Version);
}

/// <summary>Which LocalExile versions a mod says it works with: <c>compatibleClientVersions</c> in its mod.json (and the
/// index), such as <c>["0.2.0-beta"]</c>. A mod that declares none, or a LocalExile whose version is unknown, is not judged.</summary>
public static class ClientCompatibility
{
    public const string Field = "compatibleClientVersions";

    /// <summary>The field's versions; anything that is not a list of text is read as none.</summary>
    public static string[] Read(JsonElement field) => field.ValueKind == JsonValueKind.Array
        ? field.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!.Trim()).Where(v => v.Length > 0).Take(64).ToArray()
        : [];

    /// <summary>False only when the mod names its versions and <paramref name="client"/> is none of them.</summary>
    public static bool Fits(IReadOnlyList<string>? declared, ReleaseVersion? client) =>
        declared is not { Count: > 0 } || client is null || declared.Any(v => ReleaseVersion.Parse(v) is { } version && version.CompareTo(client) == 0);

    /// <summary>The versions as a mod's row shows them: "0.2.0-beta, 0.3.0"; "" for none.</summary>
    public static string Text(IReadOnlyList<string>? declared) => declared is { Count: > 0 } ? string.Join(", ", declared) : "";
}

/// <summary>A mod's published release: its zip and the SHA-256 GitHub records for it (or a ".sha256" file beside it).</summary>
public sealed record ModRelease(string Id, ReleaseVersion Version, string Tag, string Page, string ZipName, string ZipUrl, long ZipBytes,
    string? Sha256, string? HashUrl, DateTimeOffset? Published = null, string Notes = "");

/// <summary>A repository's mods as Browse shows them: the index, and each mod's releases, newest first.</summary>
public sealed record ModRepositoryView(ModSource Source, IReadOnlyList<ModListing> Mods, IReadOnlyDictionary<string, IReadOnlyList<ModRelease>> History,
    DateTimeOffset Read, string? ReleasesError = null)
{
    /// <summary>The newest release of each mod that has one.</summary>
    public IReadOnlyDictionary<string, ModRelease> Releases { get; } = History.Where(h => h.Value.Count > 0).ToDictionary(h => h.Key, h => h.Value[0], StringComparer.Ordinal);
}

/// <summary>A downloaded mod, checked and waiting in Waypoint's own folder for the adapter to put it in the checkout:
/// <c>Mods/Binaries/&lt;id&gt;/</c> for a release or a script mod, <c>Mods/Source/&lt;id&gt;/</c> for a project to build.</summary>
public sealed record StagedMod(string Id, string Version, string Folder, string Target);

/// <summary>Reads a mod repository from GitHub and downloads its mods. Nothing here writes into a checkout: a download is
/// checked into <see cref="DownloadsFolder"/>, and the adapter's InstallMod moves it into place.</summary>
public static class ModRepository
{
    public const string IndexName = "mods.json";
    const long MaxIndexBytes = 1024 * 1024;
    const long MaxZipBytes = 64L * 1024 * 1024;
    const long MaxModBytes = 64L * 1024 * 1024;
    const int MaxFiles = 2000;

    /// <summary>Where downloads are checked before the adapter takes them; the adapter takes only folders under it.</summary>
    public static string DownloadsFolder => Path.Combine(SettingsStore.DataDirectory, "mod-downloads");

    /// <summary>The index file: a version-1 object with a "mods" list. Throws for anything else.</summary>
    public static IReadOnlyList<ModListing> ParseIndex(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number || version.GetInt32() != 1)
            throw new InvalidDataException(L.Text("{0} is not a version 1 mod index", IndexName));
        if (!root.TryGetProperty("mods", out var mods) || mods.ValueKind != JsonValueKind.Array) throw new InvalidDataException(L.Text("{0} has no list of mods", IndexName));
        var result = new List<ModListing>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in mods.EnumerateArray().Take(1000))
        {
            if (mod.ValueKind != JsonValueKind.Object) continue;
            string Text(string name) => mod.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
            string id = Text("id");
            string path = Text("path");
            // A listing that could name a folder outside mods/ or a command-unsafe id is left out, not trusted.
            if (!ModCatalog.ValidId(id) || id != id.ToLowerInvariant() || path != "mods/" + id || !seen.Add(id)) continue;
            string[] depends = mod.TryGetProperty("depends", out var d) && d.ValueKind == JsonValueKind.Array
                ? d.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).Where(ModCatalog.ValidId).ToArray() : [];
            result.Add(new(id, Text("name") is { Length: > 0 } name ? name : id, Text("description"), Text("author"), Text("version"),
                Text("kind") is { Length: > 0 } kind ? kind : "feature", Text("type") == "project", path, depends,
                mod.TryGetProperty("settings", out var s) && s.ValueKind == JsonValueKind.True,
                ClientCompatibility.Read(mod.TryGetProperty(ClientCompatibility.Field, out var c) ? c : default)));
        }
        return result;
    }

    /// <summary>The newest release of each mod.</summary>
    public static IReadOnlyDictionary<string, ModRelease> ParseReleases(string json) =>
        ParseHistory(json).ToDictionary(h => h.Key, h => h.Value[0], StringComparer.Ordinal);

    /// <summary>Each mod's releases, newest first: tags <c>&lt;id&gt;-v&lt;version&gt;</c> with a <c>&lt;id&gt;-&lt;version&gt;.zip</c>;
    /// drafts, pre-releases and releases without their zip are left out.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<ModRelease>> ParseHistory(string json)
    {
        using var document = JsonDocument.Parse(json);
        var all = new Dictionary<string, List<ModRelease>>(StringComparer.Ordinal);
        if (document.RootElement.ValueKind != JsonValueKind.Array) return new Dictionary<string, IReadOnlyList<ModRelease>>();
        foreach (var release in document.RootElement.EnumerateArray())
        {
            if (release.ValueKind != JsonValueKind.Object) continue;
            if (release.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) continue;
            if (release.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True) continue;
            string tag = release.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
            var match = Regex.Match(tag, @"\A([a-z0-9][a-z0-9_.-]{0,127})-v(\d+\.\d+\.\d+)\z", RegexOptions.CultureInvariant);
            if (!match.Success || ReleaseVersion.Parse(match.Groups[2].Value) is not { } version) continue;
            string id = match.Groups[1].Value, zipName = $"{id}-{match.Groups[2].Value}.zip";
            JsonElement[] assets = release.TryGetProperty("assets", out var list) && list.ValueKind == JsonValueKind.Array ? list.EnumerateArray().ToArray() : [];
            string Name(JsonElement a) => a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            string? Url(JsonElement a) => a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
            var zip = assets.FirstOrDefault(a => Name(a) == zipName);
            if (zip.ValueKind != JsonValueKind.Object || Url(zip) is not { } zipUrl) continue;
            string? digest = zip.TryGetProperty("digest", out var dg) && dg.GetString() is { } text && text.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                && text.Length == 71 && text[7..].All(Uri.IsHexDigit) ? text[7..].ToLowerInvariant() : null;
            var hash = assets.FirstOrDefault(a => Name(a) == zipName + ".sha256");
            var candidate = new ModRelease(id, version, tag, release.TryGetProperty("html_url", out var page) ? page.GetString() ?? "" : "",
                zipName, zipUrl, zip.TryGetProperty("size", out var size) && size.TryGetInt64(out long bytes) ? bytes : 0,
                digest, hash.ValueKind == JsonValueKind.Object ? Url(hash) : null,
                release.TryGetProperty("published_at", out var at) && at.ValueKind == JsonValueKind.String && at.TryGetDateTimeOffset(out var when) ? when : null,
                release.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.String ? body.GetString() ?? "" : "");
            if (!all.TryGetValue(id, out var list2)) all[id] = list2 = [];
            if (!list2.Any(r => !r.Version.IsNewerThan(version) && !version.IsNewerThan(r.Version))) list2.Add(candidate);
        }
        return all.ToDictionary(a => a.Key, a => (IReadOnlyList<ModRelease>)[.. a.Value.OrderByDescending(r => r.Version, Comparer<ReleaseVersion>.Create((x, y) => x.IsNewerThan(y) ? 1 : y.IsNewerThan(x) ? -1 : 0))],
            StringComparer.Ordinal);
    }

    /// <summary>A mod's README.md from its repository, or "" when it has none.</summary>
    public static async Task<string> ReadmeAsync(HttpClient client, ModSource source, ModListing mod, CancellationToken stop = default)
    {
        using var response = await client.GetAsync(source.FileUrl(mod.Path + "/README.md"), HttpCompletionOption.ResponseHeadersRead, stop);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return "";
        response.EnsureSuccessStatusCode();
        string text = await response.Content.ReadAsStringAsync(stop);
        return text.Length > 256 * 1024 ? text[..(256 * 1024)] : text;
    }

    /// <summary>The repository's index and releases. The index comes from raw.githubusercontent.com; the releases from
    /// GitHub's API, whose hourly limit without an account is low, so a failure there leaves Browse with the index alone.</summary>
    public static async Task<ModRepositoryView> ReadAsync(HttpClient client, ModSource source, CancellationToken stop = default)
    {
        using var response = await client.GetAsync(source.IndexUrl, HttpCompletionOption.ResponseHeadersRead, stop);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) throw new InvalidDataException(L.Text("{0} has no {1} on its {2} branch", source.Key, IndexName, source.Branch));
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxIndexBytes) throw new InvalidDataException(L.Text("{0} is larger than 1 MB", IndexName));
        var mods = ParseIndex(await response.Content.ReadAsStringAsync(stop));
        IReadOnlyDictionary<string, IReadOnlyList<ModRelease>> releases = new Dictionary<string, IReadOnlyList<ModRelease>>();
        string? releasesError = null;
        try
        {
            using var list = await client.GetAsync(source.ReleasesUrl, stop);
            list.EnsureSuccessStatusCode();
            releases = ParseHistory(await list.Content.ReadAsStringAsync(stop));
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or TaskCanceledException && !stop.IsCancellationRequested)
        {
            releasesError = e.Message;
        }
        return new(source, mods, releases, DateTimeOffset.Now, releasesError);
    }

    /// <summary>Downloads a release, checks its SHA-256 and the folder it holds, and leaves it in <see cref="DownloadsFolder"/>
    /// for <c>Mods/Binaries/&lt;id&gt;/</c>.</summary>
    public static async Task<StagedMod> DownloadReleaseAsync(HttpClient client, ModRelease release, Action<long, long>? progress, CancellationToken stop)
    {
        string? expected = release.Sha256;
        if (expected is null && release.HashUrl is not null) expected = LocalExileReleases.HashOf(await GitHubDownload.GetStringAsync(client, release.HashUrl, stop));
        if (expected is null) throw new InvalidDataException(L.Text("release {0} has no SHA-256 for {1}; nothing was installed", release.Tag, release.ZipName));
        string work = NewWorkFolder();
        try
        {
            string zipPath = Path.Combine(work, release.ZipName);
            await DownloadAsync(client, release.ZipUrl, zipPath, progress, stop);
            await using (var stream = File.OpenRead(zipPath))
            {
                string actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, stop));
                if (actual != expected) throw new InvalidDataException(L.Text("{0} does not match its SHA-256; nothing was installed", release.ZipName));
            }
            string folder = Path.Combine(work, release.Id);
            using (var zip = ZipFile.OpenRead(zipPath)) Extract(zip, prefix: null, release.Id, folder);
            File.Delete(zipPath);
            Check(folder, release.Id, needsAssembly: true);
            return new(release.Id, release.Version.ToString(), folder, "Binaries");
        }
        catch { TryDelete(work); throw; }
    }

    /// <summary>Downloads the repository's branch and keeps only <c>mods/&lt;id&gt;/</c>: a script mod for
    /// <c>Mods/Binaries/&lt;id&gt;/</c>, a project for <c>Mods/Source/&lt;id&gt;/</c> (which the adapter then builds).</summary>
    public static async Task<StagedMod> DownloadSourceAsync(HttpClient client, ModSource source, ModListing listing, Action<long, long>? progress, CancellationToken stop)
    {
        string work = NewWorkFolder();
        try
        {
            string zipPath = Path.Combine(work, "source.zip");
            await DownloadAsync(client, source.SourceZipUrl, zipPath, progress, stop);
            string folder = Path.Combine(work, listing.Id);
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                // GitHub's archive holds one top folder, "<name>-<branch>/".
                string top = zip.Entries.Select(e => e.FullName.Replace('\\', '/')).FirstOrDefault(n => n.Contains('/'))?.Split('/')[0] ?? "";
                Extract(zip, $"{top}/{listing.Path}/", listing.Id, folder);
            }
            File.Delete(zipPath);
            bool project = Directory.EnumerateFiles(folder, "*.csproj").Any();
            Check(folder, listing.Id, needsAssembly: false);
            return new(listing.Id, listing.Version, folder, project ? "Source" : "Binaries");
        }
        catch { TryDelete(work); throw; }
    }

    /// <summary>A mod folder on the player's disk, copied and checked like a download: a project goes to
    /// <c>Mods/Source/&lt;id&gt;/</c>, a built mod or scripts to <c>Mods/Binaries/&lt;id&gt;/</c>.</summary>
    public static StagedMod StageFolder(string from)
    {
        if (!Directory.Exists(from) || !ModCatalog.Physical(from)) throw new InvalidDataException(L.Text("{0} is not a folder Waypoint can read", from));
        string manifest = Path.Combine(from, "mod.json");
        string id = Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(from)));
        if (File.Exists(manifest))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifest), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("id", out var named) && named.GetString() is { } text) id = text;
        }
        if (!ModCatalog.ValidId(id)) throw new InvalidDataException(L.Text("{0} is not a mod id Waypoint can use", id));
        string work = NewWorkFolder();
        try
        {
            string folder = Path.Combine(work, id);
            long total = 0;
            int count = 0;
            foreach (string file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(from, file);
                string[] parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (parts.Any(p => p is "bin" or "obj" or ".git" or ".vs")) continue;
                if (!ModCatalog.Physical(file)) throw new InvalidDataException(L.Text("{0} is a link; nothing was installed", relative));
                if (++count > MaxFiles || (total += new FileInfo(file).Length) > MaxModBytes) throw new InvalidDataException(L.Text("the mod is larger than {0} files or 64 MB; nothing was installed", MaxFiles));
                string target = Path.Combine(folder, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
            }
            bool project = Directory.Exists(folder) && Directory.EnumerateFiles(folder, "*.csproj").Any();
            Check(folder, id, needsAssembly: !project);
            return new(id, ReadVersion(folder), folder, project ? "Source" : "Binaries");
        }
        catch { TryDelete(work); throw; }
    }

    /// <summary>Removes what is left of a staged download (the adapter moved its folder; this removes the work folder).</summary>
    public static void Discard(StagedMod staged)
    {
        string? work = Path.GetDirectoryName(staged.Folder);
        if (work is not null && Path.GetFullPath(work).StartsWith(Path.GetFullPath(DownloadsFolder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) TryDelete(work);
    }

    static string NewWorkFolder()
    {
        string work = Path.Combine(DownloadsFolder, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        return work;
    }

    static async Task DownloadAsync(HttpClient client, string url, string path, Action<long, long>? progress, CancellationToken stop)
    {
        using var response = await GitHubDownload.GetAsync(client, url, stop);
        response.EnsureSuccessStatusCode();
        long total = response.Content.Headers.ContentLength ?? 0;
        if (total > MaxZipBytes) throw new InvalidDataException(L.Text("the download is larger than 64 MB; nothing was installed"));
        await using var input = await response.Content.ReadAsStreamAsync(stop);
        await using var output = File.Create(path);
        var buffer = new byte[81920];
        long done = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, stop)) > 0)
        {
            if ((done += read) > MaxZipBytes) throw new InvalidDataException(L.Text("the download is larger than 64 MB; nothing was installed"));
            await output.WriteAsync(buffer.AsMemory(0, read), stop);
            progress?.Invoke(done, total);
        }
    }

    /// <summary>Extracts the files under <paramref name="prefix"/> (or, for a release, the whole zip, whose files may sit in
    /// one "&lt;id&gt;/" folder) into <paramref name="folder"/>. Throws, before writing, for an unsafe or oversized zip.</summary>
    internal static void Extract(ZipArchive zip, string? prefix, string id, string folder)
    {
        var files = zip.Entries.Select(e => (Entry: e, Name: e.FullName.Replace('\\', '/'))).Where(f => !f.Name.EndsWith('/')).ToList();
        if (prefix is not null) files = files.Where(f => f.Name.StartsWith(prefix, StringComparison.Ordinal)).Select(f => (f.Entry, f.Name[prefix.Length..])).ToList();
        else if (files.Count > 0 && files.All(f => f.Name.StartsWith(id + "/", StringComparison.Ordinal))) files = files.Select(f => (f.Entry, f.Name[(id.Length + 1)..])).ToList();
        if (files.Count == 0) throw new InvalidDataException(L.Text("the download holds no files for {0}; nothing was installed", id));
        if (files.Count > MaxFiles || files.Sum(f => f.Entry.Length) > MaxModBytes) throw new InvalidDataException(L.Text("the mod is larger than {0} files or 64 MB; nothing was installed", MaxFiles));
        foreach (var (_, name) in files)
        {
            string[] parts = name.Split('/');
            if (parts.Any(p => p is "" or "." or "..") || name.Contains(':')) throw new InvalidDataException(L.Text("the download holds an unsafe path, {0}; nothing was installed", name));
        }
        foreach (var (entry, name) in files)
        {
            string target = Path.Combine(folder, Path.Combine(name.Split('/')));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target);
        }
    }

    /// <summary>A mod folder the Gateway can load: its manifest's id is the folder's, and it has its assembly or scripts
    /// (or, for a project, a .csproj).</summary>
    internal static void Check(string folder, string id, bool needsAssembly)
    {
        string manifest = Path.Combine(folder, "mod.json");
        bool scripts = Directory.Exists(folder) && Directory.EnumerateFiles(folder, "*.cs").Any();
        bool project = Directory.Exists(folder) && Directory.EnumerateFiles(folder, "*.csproj").Any();
        if (!File.Exists(manifest))
        {
            if (scripts && !project) return;
            throw new InvalidDataException(L.Text("{0} has no mod.json; nothing was installed", id));
        }
        string json = File.ReadAllText(manifest);
        _ = ModCatalog.ManifestDefaults(id, json);
        if (project && !needsAssembly) return;
        string assembly = ModCatalog.AssemblyName(id, json);
        if (!File.Exists(Path.Combine(folder, assembly)) && !scripts)
            throw new InvalidDataException(L.Text("{0} has neither {1} nor scripts; nothing was installed", id, assembly));
    }

    static string ReadVersion(string folder)
    {
        string manifest = Path.Combine(folder, "mod.json");
        if (!File.Exists(manifest)) return "0";
        try { return ModCatalog.ManifestDefaults(Path.GetFileName(folder), File.ReadAllText(manifest)).Version; }
        catch (Exception e) when (e is JsonException or InvalidDataException) { return "0"; }
    }

    static void TryDelete(string folder)
    {
        try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
