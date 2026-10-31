using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace Waypoint;

/// <summary>A published release: its version, page, notes and the files the launcher replaces itself with.</summary>
sealed record Release(Version Version, string Tag, string Page, string Notes, string? ExeUrl, string? HashUrl);

/// <summary>
/// The launcher's own versions. A release build carries the version of its git tag (the release workflow passes
/// <c>-p:Version</c>); a build from source is 0.0.0 and never offers updates. The latest release comes from GitHub's
/// public API for <see cref="Repository"/>, and an update replaces the running exe: a Windows exe that runs can be
/// renamed, so it becomes <c>.old</c>, the downloaded one (checked against the release's SHA-256) takes its name, and
/// the new launcher starts; the next start deletes the <c>.old</c>.
/// </summary>
static class Updates
{
    /// <summary>Where the releases are published (owner/name).</summary>
    public const string Repository = "LocalExile/Waypoint";
    public const string ExeName = "Waypoint.exe";

    /// <summary>This build's version: the release's, 0.0.0 for a build from source.</summary>
    public static Version Current { get; } = Parse(
        typeof(Updates).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion) ?? new(0, 0, 0);

    public static bool IsReleaseBuild => Current > new Version(0, 0, 0);

    /// <summary>The running file when it is the published single-file exe (`dotnet run` runs a dll through dotnet).</summary>
    public static string? RunningExe =>
        Environment.ProcessPath is { } path && Path.GetFileName(path).Equals(ExeName, StringComparison.OrdinalIgnoreCase) ? path : null;

    /// <summary>"v1.2.3", "1.2.3", "1.2.3-dev+abc": the numeric part, three components; null for anything else.</summary>
    internal static Version? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string core = text.Trim().TrimStart('v', 'V').Split('+', '-')[0];
        return Version.TryParse(core, out var v) ? new Version(v.Major, v.Minor, Math.Max(v.Build, 0)) : null;
    }

    /// <summary>GitHub's "latest release" answer; null when it has no version tag.</summary>
    internal static Release? ParseRelease(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        string tag = r.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
        if (Parse(tag) is not { } version) return null;
        string? Asset(string name) => r.TryGetProperty("assets", out var assets)
            ? assets.EnumerateArray().FirstOrDefault(a => a.GetProperty("name").GetString() == name) is { ValueKind: JsonValueKind.Object } a
                ? a.GetProperty("browser_download_url").GetString() : null
            : null;
        return new(version, tag, r.TryGetProperty("html_url", out var page) ? page.GetString() ?? "" : "",
            r.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "", Asset(ExeName), Asset(ExeName + ".sha256"));
    }

    /// <summary>The SHA-256 a ".sha256" file names: its first word, as `sha256sum` and the release workflow write it.</summary>
    internal static string? HashOf(string sha256File) =>
        sha256File.Split((char[])[' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() is { Length: 64 } hex
            ? hex.ToLowerInvariant() : null;

    internal static HttpClient Client()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"Waypoint/{Current}");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    /// <summary>The latest release when it is newer than this build; null when there is none, or nothing newer.</summary>
    public static async Task<Release?> CheckAsync()
    {
        using var client = Client();
        using var response = await client.GetAsync($"https://api.github.com/repos/{Repository}/releases/latest");
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        var release = ParseRelease(await response.Content.ReadAsStringAsync());
        return release is not null && release.Version > Current ? release : null;
    }

    /// <summary>Downloads the release's exe beside the running one, checks it, swaps it in and starts it, after
    /// <paramref name="beforeStart"/> (this copy's single-instance claim ends there). The caller then closes this one.</summary>
    public static async Task ApplyAsync(Release release, IProgress<string> progress, Action beforeStart)
    {
        string exe = RunningExe ?? throw new InvalidOperationException(L.Text("only the published Waypoint.exe updates itself; download it from the release page"));
        if (release.ExeUrl is null || release.HashUrl is null)
            throw new InvalidOperationException(L.Text("release {0} has no {1} with its .sha256", release.Tag, ExeName));
        using var client = Client();
        progress.Report(L.Text("checking {0} …", release.Tag));
        string expected = HashOf(await client.GetStringAsync(release.HashUrl))
            ?? throw new InvalidOperationException(L.Text("the release's .sha256 holds no SHA-256"));
        string download = exe + ".new";
        progress.Report(L.Text("downloading {0} …", release.Tag));
        await using (var file = File.Create(download))
        await using (var stream = await client.GetStreamAsync(release.ExeUrl))
            await stream.CopyToAsync(file);
        string actual;
        await using (var file = File.OpenRead(download))
            actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(file));
        if (actual != expected)
        {
            File.Delete(download);
            throw new InvalidOperationException(L.Text("the downloaded {0} does not match its SHA-256; nothing was changed", ExeName));
        }
        progress.Report(L.Text("installing …"));
        // A running exe can be renamed but not replaced in place. Should the new one not take its name, the old one
        // gets it back, so there is always a launcher to start.
        File.Move(exe, exe + ".old", overwrite: true);
        try { File.Move(download, exe); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            File.Move(exe + ".old", exe);
            throw;
        }
        beforeStart();
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe)! })?.Dispose();
    }

    /// <summary>The exe an update replaced, once the new one runs.</summary>
    public static void CleanUp()
    {
        if (RunningExe is not { } exe) return;
        try { File.Delete(exe + ".old"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
