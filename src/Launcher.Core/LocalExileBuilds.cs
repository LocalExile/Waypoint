using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Launcher.Core;

/// <summary>One depot of the game on Steam at one build. Steam's ids are text: a manifest id is larger than a JSON reader's
/// exact numbers.</summary>
public sealed record SteamDepot(string Depot, string Manifest);

/// <summary>One client build a LocalExile runs, as that LocalExile says it (<c>LocalExile.exe builds</c>).</summary>
/// <param name="Version">The version players know it by ("3.29.3d").</param>
/// <param name="Name">Which build of that version ("release of 2026-11-10, build 328228").</param>
/// <param name="ExecutableSha256">SHA-256 of its unpatched executable.</param>
/// <param name="SteamIndexSha256">SHA-256 of <c>Bundles2/_.index.bin</c> in its Steam install; null when that LocalExile does
/// not accept the build's Steam files.</param>
/// <param name="SteamApp">The game's app id on Steam; null when the build cannot be fetched from Steam.</param>
/// <param name="SteamManifests">Steam's manifest of each depot that holds this build; empty when they are not known.</param>
public sealed record ListedBuild(string Version, string Name, string ExecutableSha256, string? SteamIndexSha256 = null, string? SteamApp = null,
    IReadOnlyList<SteamDepot>? SteamManifests = null)
{
    public IReadOnlyList<SteamDepot> Depots => SteamManifests ?? [];
    /// <summary>Whether Steam can deliver this build: the app and at least one depot's manifest are known.</summary>
    public bool FromSteam => SteamApp is { Length: > 0 } && Depots.Count > 0;
}

/// <summary>
/// The client builds a LocalExile folder runs, asked of that LocalExile and never kept in a list of Waypoint's own: a release
/// answers <c>LocalExile.exe builds --json</c> (from v0.5.0-beta), and a source copy's list is its
/// <c>src/GameServer.Core/Protocol/ClientBuilds.cs</c>. So a client build LocalExile comes to allow, or stops allowing, is
/// known here without a new Waypoint. A LocalExile that cannot say (an older release, an older checkout) answers null, and
/// what it needs is read as before (<see cref="ClientLocator.Supported"/>).
/// </summary>
public static class LocalExileBuilds
{
    /// <summary>The first release whose launcher has the <c>builds</c> command. An older one is never asked: it has nothing to say.</summary>
    static readonly ReleaseVersion FirstWithList = new(0, 5, 0, "beta");

    const string SourceList = "src/GameServer.Core/Protocol/ClientBuilds.cs";

    /// <summary>Whether the folder is a release from before LocalExile could list its builds. Such a folder has no list
    /// for good; any other that answers none may only have failed to answer.</summary>
    public static bool TooOld(string folder)
    {
        try { return LocalExileReleases.IsInstall(folder) && (LocalExileReleases.Installed(folder) is not { } version || version.CompareTo(FirstWithList) < 0); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    /// <summary>For tests only: what <see cref="Of"/> answers instead of asking a LocalExile.</summary>
    public static Func<string, IReadOnlyList<ListedBuild>?>? Override { get; set; }

    static readonly Dictionary<(string, long, DateTime), IReadOnlyList<ListedBuild>?> Asked = [];
    /// <summary>When a launcher last failed to answer (it did not start, ran out of time, or ended with an error).</summary>
    static readonly Dictionary<(string, long, DateTime), long> Failed = [];
    /// <summary>How long a launcher that failed to answer is left alone before it may be asked again, in milliseconds.</summary>
    const long AskAgainAfter = 30_000;

    /// <summary>The builds this folder's LocalExile runs; null when it cannot say. An answer is remembered until the file
    /// that gave it changes (an update, a pull). A launcher that failed to answer (it did not start, ran out of time, ended
    /// with an error) is not asked again by itself, because that can take ten seconds and many callers are on the UI
    /// thread; <paramref name="again"/> asks it once more, at most every half minute, for a caller that is in the
    /// background and whose user is looking at the list.</summary>
    public static IReadOnlyList<ListedBuild>? Of(string folder, bool again = false)
    {
        if (Override is { } told) return told(folder);
        try
        {
            bool release = LocalExileReleases.IsInstall(folder);
            if (release && (LocalExileReleases.Installed(folder) is not { } version || version.CompareTo(FirstWithList) < 0)) return null;
            var file = new FileInfo(Path.Combine(folder, release ? LocalExileReleases.Launcher : SourceList));
            if (!file.Exists) return null;
            var key = (file.FullName, file.Length, file.LastWriteTimeUtc);
            lock (Asked)
            {
                if (Asked.TryGetValue(key, out var known)) return known;
                if (Failed.TryGetValue(key, out long at) && (!again || Environment.TickCount64 - at < AskAgainAfter)) return null;
            }
            var builds = release ? Ask(folder, file.FullName) : ParseSource(File.ReadAllText(file.FullName));
            lock (Asked)
            {
                if (builds is null) Failed[key] = Environment.TickCount64;
                else Asked[key] = builds.Count == 0 ? null : builds;
            }
            return builds is { Count: > 0 } ? builds : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException) { return null; }
    }

    /// <summary><c>LocalExile.exe builds --json</c>: it reads nothing but its own list, changes nothing and starts nothing.</summary>
    static IReadOnlyList<ListedBuild>? Ask(string folder, string launcher)
    {
        var start = new ProcessStartInfo(launcher)
        {
            WorkingDirectory = folder, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8,
        };
        start.ArgumentList.Add("builds");
        start.ArgumentList.Add("--json");
        using var process = Process.Start(start);
        if (process is null) return null;
        var output = process.StandardOutput.ReadToEndAsync();
        _ = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(10_000))
        {
            try { process.Kill(entireProcessTree: true); }
            // A child that cannot be ended makes Kill throw an AggregateException: still a launcher that did not answer.
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or AggregateException) { }
            return null;
        }
        return process.ExitCode == 0 ? Parse(output.Result.Split('\n')) : null;
    }

    /// <summary>The <c>build</c> events among a launcher's lines (the server's Agents/docs/dev/releases.md); anything else,
    /// and a line that is no event, is passed over.</summary>
    public static IReadOnlyList<ListedBuild> Parse(IEnumerable<string> lines)
    {
        var builds = new List<ListedBuild>();
        foreach (string line in lines)
        {
            if (line.TrimStart() is not ['{', ..]) continue;
            try
            {
                using var document = JsonDocument.Parse(line);
                var item = document.RootElement;
                if (Text(item, "event") != "build" || Text(item, "version") is not { Length: > 0 } version
                    || Text(item, "executableSha256") is not { } hash || !Regex.IsMatch(hash, @"\A[0-9a-fA-F]{64}\z")) continue;
                var depots = new List<SteamDepot>();
                if (item.TryGetProperty("steamManifests", out var manifests) && manifests.ValueKind == JsonValueKind.Array)
                    foreach (var manifest in manifests.EnumerateArray())
                        if (Text(manifest, "depot") is { Length: > 0 } depot && Text(manifest, "manifest") is { Length: > 0 } id && Digits(depot) && Digits(id))
                            depots.Add(new(depot, id));
                string? app = Text(item, "steamApp");
                builds.Add(new(version, Text(item, "name") ?? "", hash.ToLowerInvariant(), Text(item, "steamIndexSha256")?.ToLowerInvariant(),
                    app is not null && Digits(app) ? app : null, depots));
            }
            catch (JsonException) { }
        }
        return builds;
    }

    static string? Text(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    static bool Digits(string text) => text.Length is > 0 and <= 20 && text.All(char.IsAsciiDigit);

    /// <summary>A source copy's list, read from its <c>ClientBuilds.cs</c>: the rows of <c>Allowed</c>, each a version, a name
    /// and the executable's hash, with its Steam index and manifests when it has them.</summary>
    public static IReadOnlyList<ListedBuild> ParseSource(string source)
    {
        string? app = Regex.Match(source, @"\bSteamApp\s*=\s*(\d{1,10})\s*;") is { Success: true } steam ? steam.Groups[1].Value : null;
        var rows = Regex.Matches(source, @"new\(\s*""(?<version>[^""\r\n]+)""\s*,\s*""(?<name>[^""\r\n]*)""\s*,\s*""(?<hash>[0-9a-fA-F]{64})""");
        var builds = new List<ListedBuild>();
        for (int i = 0; i < rows.Count; i++)
        {
            int from = rows[i].Index + rows[i].Length, to = i + 1 < rows.Count ? rows[i + 1].Index : source.Length;
            string rest = source[from..to];
            string? index = Regex.Match(rest, @"SteamIndexSha256\s*:\s*""([0-9a-fA-F]{64})""") is { Success: true } found ? found.Groups[1].Value.ToLowerInvariant() : null;
            var depots = Regex.Match(rest, @"SteamManifests\s*:\s*\[(?<list>[^\]]*)\]") is { Success: true } list
                ? Regex.Matches(list.Groups["list"].Value, @"new\(\s*(\d{1,10})\s*,\s*(\d{1,20})\s*\)").Select(m => new SteamDepot(m.Groups[1].Value, m.Groups[2].Value)).ToList()
                : [];
            builds.Add(new(rows[i].Groups["version"].Value, rows[i].Groups["name"].Value, rows[i].Groups["hash"].Value.ToLowerInvariant(), index,
                depots.Count > 0 ? app : null, depots));
        }
        return builds;
    }
}
