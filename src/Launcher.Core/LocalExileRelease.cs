using System.ComponentModel;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Launcher.Core;

/// <summary>A LocalExile release's version: SemVer, so that 0.1.0 is newer than 0.1.0-beta. Build metadata ("+commit") does
/// not count.</summary>
public sealed record ReleaseVersion(int Major, int Minor, int Patch, string Prerelease) : IComparable<ReleaseVersion>
{
    public static ReleaseVersion? Parse(string? text)
    {
        var match = Regex.Match(text?.Trim() ?? "", @"\A[vV]?(\d{1,9})\.(\d{1,9})\.(\d{1,9})(?:-([0-9A-Za-z.-]+))?(?:\+[0-9A-Za-z.-]+)?\z", RegexOptions.CultureInvariant);
        return match.Success ? new(int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value), int.Parse(match.Groups[3].Value), match.Groups[4].Value) : null;
    }

    public int CompareTo(ReleaseVersion? other)
    {
        if (other is null) return 1;
        int numbers = (Major, Minor, Patch).CompareTo((other.Major, other.Minor, other.Patch));
        if (numbers != 0) return numbers;
        if (Prerelease.Length == 0 && other.Prerelease.Length == 0) return 0;
        if (Prerelease.Length == 0) return 1;
        if (other.Prerelease.Length == 0) return -1;
        string[] mine = Prerelease.Split('.'), theirs = other.Prerelease.Split('.');
        for (int i = 0; i < Math.Min(mine.Length, theirs.Length); i++)
        {
            bool a = long.TryParse(mine[i], out long x), b = long.TryParse(theirs[i], out long y);
            int order = a && b ? x.CompareTo(y) : a ? -1 : b ? 1 : string.CompareOrdinal(mine[i], theirs[i]);
            if (order != 0) return Math.Sign(order);
        }
        return mine.Length.CompareTo(theirs.Length);
    }

    public bool IsNewerThan(ReleaseVersion other) => CompareTo(other) > 0;

    public override string ToString() => $"{Major}.{Minor}.{Patch}" + (Prerelease.Length > 0 ? "-" + Prerelease : "");
}

/// <summary>A published LocalExile release: the player's zip and its SHA-256.</summary>
public sealed record LocalExileRelease(ReleaseVersion Version, string Tag, string Page, string? ZipName, string? ZipUrl, string? HashUrl, long ZipBytes,
    string Notes = "", DateTimeOffset? Published = null);

/// <summary>
/// A release install: what the release zip holds, with <c>LocalExile.exe</c> and <c>release.json</c> at its root
/// (the server's Agents/docs/dev/releases.md). An update extracts the new zip over the old one, as the player's README
/// says. <c>data\</c> and <c>Client\</c> are not in a zip; one that names them is refused, so nothing of the player's
/// can be overwritten.
/// </summary>
public static class LocalExileReleases
{
    public const string ExeName = "LocalExile.exe";
    /// <summary>A release's launcher on this system; a Linux release's servers and launcher are native programs.</summary>
    public static string Launcher => OperatingSystem.IsWindows() ? ExeName : "LocalExile";
    /// <summary>The end of the name of this system's zip among a release's files.</summary>
    public static string AssetSuffix => OperatingSystem.IsWindows() ? "-win-x64.zip" : "-linux-x64.zip";
    public const string ManifestName = "release.json";
    public const string LatestUrl = "https://api.github.com/repos/LocalExile/LocalExile/releases/latest";
    public const string ListUrl = "https://api.github.com/repos/LocalExile/LocalExile/releases?per_page=30";
    /// <summary>Beside the install's files while a download is checked and extracted; never left behind.</summary>
    const string StagingName = ".waypoint-update";
    const string ZipRoot = "LocalExile/";

    /// <summary>A release install rather than a source checkout.</summary>
    public static bool IsInstall(string? folder) =>
        !string.IsNullOrEmpty(folder) && File.Exists(Path.Combine(folder, Launcher)) && File.Exists(Path.Combine(folder, ManifestName))
        && !File.Exists(Path.Combine(folder, "GameServer.slnx"));

    /// <summary>The version in an install's release.json; null when it cannot be read.</summary>
    public static ReleaseVersion? Installed(string folder)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, ManifestName)));
            return document.RootElement.TryGetProperty("version", out var version) ? ReleaseVersion.Parse(version.GetString()) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { return null; }
    }

    /// <summary>Whether the install has gone through <c>LocalExile.exe setup</c>: its client is patched, with keys and saves.</summary>
    public static bool IsSetUp(string folder) =>
        File.Exists(Path.Combine(folder, "Client", "Game.patched.exe")) && File.Exists(Path.Combine(folder, "data", "login-keys.json")) && HasSaves(folder);

    public static bool HasSaves(string folder) => File.Exists(Path.Combine(folder, "data", "saves.db"));

    /// <summary>GitHub's "latest release" answer; null when its tag is no version.</summary>
    public static LocalExileRelease? ParseRelease(string json)
    {
        using var document = JsonDocument.Parse(json);
        return ParseRelease(document.RootElement);
    }

    /// <summary>GitHub's list of releases, newest first as it gives them; what has no version tag is left out.</summary>
    public static IReadOnlyList<LocalExileRelease> ParseReleases(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.ValueKind == JsonValueKind.Array ? document.RootElement.EnumerateArray().Select(ParseRelease).OfType<LocalExileRelease>().ToList() : [];
    }

    public static async Task<IReadOnlyList<LocalExileRelease>> ListAsync(HttpClient client, CancellationToken stop = default)
    {
        using var response = await client.GetAsync(ListUrl, stop);
        response.EnsureSuccessStatusCode();
        return ParseReleases(await response.Content.ReadAsStringAsync(stop));
    }

    static LocalExileRelease? ParseRelease(JsonElement root)
    {
        string tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
        if (ReleaseVersion.Parse(tag) is not { } version) return null;
        JsonElement[] assets = root.TryGetProperty("assets", out var list) && list.ValueKind == JsonValueKind.Array ? list.EnumerateArray().ToArray() : [];
        string Name(JsonElement a) => a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
        var zip = assets.FirstOrDefault(a => Name(a).StartsWith("LocalExile-", StringComparison.OrdinalIgnoreCase) && Name(a).EndsWith(AssetSuffix, StringComparison.OrdinalIgnoreCase));
        string? Url(JsonElement a) => a.ValueKind == JsonValueKind.Object && a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
        string? zipName = zip.ValueKind == JsonValueKind.Object ? Name(zip) : null;
        var hash = assets.FirstOrDefault(a => zipName is not null && Name(a).Equals(zipName + ".sha256", StringComparison.OrdinalIgnoreCase));
        return new(version, tag, root.TryGetProperty("html_url", out var page) ? page.GetString() ?? "" : "", zipName, Url(zip), Url(hash),
            zip.ValueKind == JsonValueKind.Object && zip.TryGetProperty("size", out var size) && size.TryGetInt64(out long bytes) ? bytes : 0,
            root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "",
            root.TryGetProperty("published_at", out var published) && published.TryGetDateTimeOffset(out var at) ? at : null);
    }

    /// <summary>The latest published release; null when there is none.</summary>
    public static async Task<LocalExileRelease?> LatestAsync(HttpClient client, CancellationToken stop = default)
    {
        using var response = await client.GetAsync(LatestUrl, stop);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return ParseRelease(await response.Content.ReadAsStringAsync(stop));
    }

    /// <summary>The SHA-256 a ".sha256" file names: its first word.</summary>
    public static string? HashOf(string sha256File) =>
        sha256File.Split((char[])[' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() is { Length: 64 } hex && hex.All(Uri.IsHexDigit)
            ? hex.ToLowerInvariant() : null;

    /// <summary>Where each of a zip's files goes in the install, in the order to put them there: the launcher and then
    /// release.json last, so a cut-short update still reads as the old version and is offered again. Throws, before anything
    /// is written, for a zip that is not a release's or that would touch what is the player's.</summary>
    public static IReadOnlyList<(ZipArchiveEntry Entry, string Relative)> Plan(ZipArchive zip)
    {
        var files = new List<(ZipArchiveEntry, string)>();
        foreach (var entry in zip.Entries)
        {
            string name = entry.FullName.Replace('\\', '/');
            if (name.EndsWith('/')) continue;
            if (!name.StartsWith(ZipRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException(L.Text("{0} is not inside the release's LocalExile folder; nothing was changed", name));
            string relative = name[ZipRoot.Length..];
            string[] parts = relative.Split('/');
            if (parts.Any(p => p is "" or "." or "..") || relative.Contains(':')) throw new InvalidDataException(L.Text("the release holds an unsafe path, {0}; nothing was changed", name));
            bool playerData = parts[0].Equals("Client", StringComparison.OrdinalIgnoreCase)
                || parts[0].Equals("data", StringComparison.OrdinalIgnoreCase) && !(parts.Length > 2 && parts[1].Equals("schema", StringComparison.OrdinalIgnoreCase));
            if (playerData) throw new InvalidDataException(L.Text("the release would overwrite {0}, which holds your own data; nothing was changed", name));
            files.Add((entry, Path.Combine(parts)));
        }
        if (!files.Any(f => f.Item2 == Launcher) || !files.Any(f => f.Item2 == ManifestName)) throw new InvalidDataException(L.Text("the release has no {0} or {1}; nothing was changed", ExeName, ManifestName));
        return files.OrderBy(f => f.Item2 == ManifestName ? 2 : f.Item2 == Launcher ? 1 : 0).ToList();
    }

    /// <summary>Extracts a checked zip into a staging folder, then moves its files into place. A stop or a bad zip during the
    /// extraction leaves the install as it was; once the files move, the few seconds left finish.</summary>
    public static async Task ExtractAsync(string zipPath, string folder, Action<long, long>? progress, CancellationToken stop)
    {
        string staging = Path.Combine(folder, StagingName, "new");
        Directory.CreateDirectory(staging);
        await Task.Run(() =>
        {
            using var zip = ZipFile.OpenRead(zipPath);
            var plan = Plan(zip);
            long total = plan.Sum(f => f.Entry.Length), done = 0;
            foreach (var (entry, relative) in plan)
            {
                stop.ThrowIfCancellationRequested();
                string target = Path.Combine(staging, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: true);
                done += entry.Length;
                progress?.Invoke(done, total);
            }
            foreach (var (_, relative) in plan)
            {
                string target = Path.Combine(folder, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Move(Path.Combine(staging, relative), target, overwrite: true);
            }
        }, CancellationToken.None);
    }

    /// <summary>Downloads a release, checks it against its SHA-256 and puts it in <paramref name="folder"/>.</summary>
    /// <param name="progress">Bytes so far and of how many; the download first, then, with <paramref name="extracting"/>, the extraction.</param>
    public static async Task InstallAsync(HttpClient client, LocalExileRelease release, string folder, Action<long, long> progress, Action extracting, CancellationToken stop)
    {
        if (release.ZipUrl is null || release.HashUrl is null) throw new InvalidOperationException(L.Text("release {0} has no {1} with its .sha256", release.Tag, "LocalExile-*" + AssetSuffix));
        string expected = HashOf(await client.GetStringAsync(release.HashUrl, stop)) ?? throw new InvalidOperationException(L.Text("the release's .sha256 holds no SHA-256"));
        Directory.CreateDirectory(folder);
        string staging = Path.Combine(folder, StagingName);
        DeleteStaging(staging);
        Directory.CreateDirectory(staging);
        try
        {
            string zip = Path.Combine(staging, "release.zip");
            using (var response = await client.GetAsync(release.ZipUrl, HttpCompletionOption.ResponseHeadersRead, stop))
            {
                response.EnsureSuccessStatusCode();
                long total = response.Content.Headers.ContentLength ?? release.ZipBytes;
                await using var source = await response.Content.ReadAsStreamAsync(stop);
                await using var file = File.Create(zip);
                byte[] buffer = new byte[1 << 16];
                long done = 0;
                for (int read; (read = await source.ReadAsync(buffer, stop)) > 0;)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), stop);
                    done += read;
                    progress(done, total);
                }
            }
            if (await FileHashAsync(zip, stop) != expected) throw new InvalidDataException(L.Text("the downloaded {0} does not match its SHA-256; nothing was changed", release.ZipName ?? "zip"));
            extracting();
            await ExtractAsync(zip, folder, progress, stop);
        }
        finally { DeleteStaging(staging); }
    }

    public static async Task<string> FileHashAsync(string path, CancellationToken stop)
    {
        await using var file = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(file, stop));
    }

    /// <summary>A copy of the save database, with its journal, while nothing runs: a newer server may change its format, and
    /// the zip brings no way back. Null when there are no saves yet.</summary>
    public static string? BackUpSaves(string folder, ReleaseVersion? from, DateTime now)
    {
        string[] files = ["saves.db", "saves.db-wal", "saves.db-shm"];
        if (!HasSaves(folder)) return null;
        string target = Path.Combine(folder, "backups", $"before-update-{from?.ToString() ?? "unknown"}-{now:yyyyMMdd-HHmmss}");
        Directory.CreateDirectory(target);
        foreach (string name in files)
            if (Path.Combine(folder, "data", name) is var source && File.Exists(source)) File.Copy(source, Path.Combine(target, name));
        return target;
    }

    static void DeleteStaging(string staging)
    {
        try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Why an account cannot be created; null when it can. The rules are Auth's. The password goes to LocalExile.exe
    /// on standard input, whose encoding is the console's, so anything but plain ASCII could arrive as another password.</summary>
    public static string? AccountProblem(string name, string password, string again)
    {
        if (!Regex.IsMatch(name, @"\A[A-Za-z0-9_-]{3,32}\z", RegexOptions.CultureInvariant)) return L.Text("The account name needs 3 to 32 letters, digits, _ or -.");
        if (password.Length < 4) return L.Text("The password needs at least 4 characters.");
        if (password.Any(c => c is < ' ' or > '~')) return L.Text("The password can only use English letters, digits and common symbols.");
        return password == again ? null : L.Text("The passwords differ.");
    }

    /// <summary>What a step of LocalExile.exe is doing, in the player's language.</summary>
    public static string Stage(string? id, string fallback) => id switch
    {
        "client-check" => L.Text("Checking the game client"),
        "client-copy" => L.Text("Copying the game client"),
        "client-link" => L.Text("Linking the game client"),
        { } extract when extract.StartsWith("extract", StringComparison.Ordinal) => L.Text("Extracting the game data"),
        "keys" => L.Text("Preparing the login keys"),
        "account" => L.Text("Checking your account"),
        "login-list" => L.Text("Pointing the client at your PC"),
        "client" => L.Text("Starting the game"),
        { } server when server.StartsWith("server", StringComparison.Ordinal) => L.Text("Starting the servers"),
        _ => fallback,
    };

    /// <summary>A step that writes the client or the keys: it finishes before a stop ends the run.</summary>
    public static bool MustFinish(string? stage) => stage is "client-link" or "keys" or "login-list";
}

/// <summary>A line of <c>LocalExile.exe --json</c> (the server's Agents/docs/dev/releases.md, schema 1).</summary>
public sealed record LocalExileEvent(int Schema, string Name, string? Id = null, string? Message = null, long? Current = null, long? Total = null,
    string? Option = null, string? Suggestion = null, string? ValueName = null, string? Value = null, int? Code = null, string? Kind = null)
{
    public const int KnownSchema = 1;

    /// <summary>Null for a line that is no event.</summary>
    public static LocalExileEvent? Parse(string line)
    {
        if (!line.TrimStart().StartsWith('{')) return null;
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            string? Text(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            long? Number(string name) => root.TryGetProperty(name, out var v) && v.TryGetInt64(out long n) ? n : null;
            if (Text("event") is not { } name || Number("schema") is not { } schema) return null;
            return new((int)schema, name, Text("id"), Text("message"), Number("current"), Number("total"), Text("option"), Text("suggestion"),
                Text("name"), Text("value"), (int?)Number("code"), Text("kind"));
        }
        catch (JsonException) { return null; }
    }
}

/// <summary>What <c>LocalExile.exe</c> answered: its exit code and, when it stopped to ask, the option it needs.</summary>
public sealed record LocalExileResult(int Code, string? Error, string? NeededOption, string? Suggestion)
{
    public const int NeedsInput = 7;
}

/// <summary>Runs an install's <c>LocalExile.exe</c> with <c>--json</c>. It is the only thing in a release that starts the
/// servers and the client. Ending it ends them too, so a run for playing stays alive as long as the game.</summary>
public static class LocalExileTool
{
    /// <param name="input">The new account's password: written to standard input, and never on a command line or in a log.</param>
    /// <param name="output">Every line that is no event (a crash's message).</param>
    public static async Task<LocalExileResult> RunAsync(string folder, IEnumerable<string> arguments, Action<LocalExileEvent> events, Action<string>? output = null, string? input = null, CancellationToken stop = default)
    {
        string? error = null, needed = null, suggestion = null;
        bool unknownSchema = false;
        using var refuse = CancellationTokenSource.CreateLinkedTokenSource(stop);
        void Line(string line)
        {
            if (LocalExileEvent.Parse(line) is not { } item) { if (line.Trim().Length > 0) output?.Invoke(line); return; }
            if (item.Schema > LocalExileEvent.KnownSchema)
            {
                // "A change that breaks a reader raises schema": reading on could misread a question or a failure.
                unknownSchema = true;
                refuse.Cancel();
                return;
            }
            if (item.Name == "error") error = item.Message;
            if (item.Name == "needs-input") { needed = item.Option; suggestion = item.Suggestion; }
            events(item);
        }
        try
        {
            int code = await ToolProcess.RunAsync(Path.Combine(folder, LocalExileReleases.Launcher), [.. arguments, "--json"], folder, Line, refuse.Token, input, utf8: true);
            return new(code, error, needed, suggestion);
        }
        catch (OperationCanceledException) when (unknownSchema)
        {
            throw new InvalidOperationException(L.Text("This LocalExile speaks a newer protocol than this Waypoint knows. Update Waypoint."));
        }
    }
}
