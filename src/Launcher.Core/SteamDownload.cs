using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Launcher.Core;

/// <summary>
/// DepotDownloader (github.com/SteamRE/DepotDownloader, GPL-2.0), the one outside program the Steam download needs. It is
/// never part of Waypoint: its official release is fetched the first time it is needed, held to the SHA-256 written here,
/// kept under Waypoint's own folder and run as a program of its own. It is self-contained, so nothing else is installed,
/// and neither Steam nor SteamCMD has to be on the PC.
/// </summary>
public static class DepotTool
{
    public const string Version = "3.4.0";
    public const string Page = "https://github.com/SteamRE/DepotDownloader";
    const string WindowsUrl = "https://github.com/SteamRE/DepotDownloader/releases/download/DepotDownloader_3.4.0/DepotDownloader-windows-x64.zip";
    const string WindowsSha256 = "41c9e9f0df54b3ad02e67a11726756e5c73283bd7c2e1b04acfa5ae4c2ed3767";
    /// <summary>The zip's size, for the progress before the server says.</summary>
    public const long DownloadBytes = 33_474_005;
    const string ExeName = "DepotDownloader.exe";

    /// <summary>Only the build that was fetched, hashed and run: Windows on x64.</summary>
    public static bool Supported => OperatingSystem.IsWindows() && RuntimeInformation.OSArchitecture == Architecture.X64;

    public static string Folder(string home) => Path.Combine(home, "tools", "DepotDownloader", Version);
    public static string Executable(string home) => Path.Combine(Folder(home), ExeName);
    public static bool Installed(string home) => File.Exists(Executable(home));

    /// <summary>Fetches and unpacks the release; throws <see cref="IOException"/> for a file that is not the one pinned. A
    /// folder is used only once it is whole: it is unpacked beside its place and renamed into it.</summary>
    public static async Task InstallAsync(string home, HttpClient http, Action<long, long>? progress, CancellationToken stop)
    {
        if (!Supported) throw new IOException(L.Text("The Steam download works on 64-bit Windows only for now."));
        if (Installed(home)) return;
        string folder = Folder(home), staging = folder + ".download", zip = Path.Combine(staging, "DepotDownloader.zip");
        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        Directory.CreateDirectory(staging);
        try
        {
            using (var response = await GitHubDownload.GetAsync(http, WindowsUrl, stop))
            {
                response.EnsureSuccessStatusCode();
                long total = response.Content.Headers.ContentLength ?? DownloadBytes, done = 0;
                await using var from = await response.Content.ReadAsStreamAsync(stop);
                await using var to = new FileStream(zip, FileMode.Create, FileAccess.Write, FileShare.None);
                byte[] buffer = new byte[1 << 16];
                int read;
                while ((read = await from.ReadAsync(buffer, stop)) > 0)
                {
                    await to.WriteAsync(buffer.AsMemory(0, read), stop);
                    progress?.Invoke(done += read, total);
                }
            }
            string hash;
            await using (var file = File.OpenRead(zip)) hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, stop));
            if (hash != WindowsSha256)
                throw new IOException(L.Text("The downloaded DepotDownloader is not the release Waypoint expects, so it was not used. Try again later."));
            string unpacked = Path.Combine(staging, "tool");
            Directory.CreateDirectory(unpacked);
            using (var archive = ZipFile.OpenRead(zip))
                foreach (var entry in archive.Entries)
                {
                    // Its files are at the zip's root; nothing with a folder in its name is wanted.
                    if (entry.Name.Length == 0 || entry.FullName != entry.Name) continue;
                    entry.ExtractToFile(Path.Combine(unpacked, entry.Name), overwrite: true);
                }
            if (!File.Exists(Path.Combine(unpacked, ExeName))) throw new IOException(L.Text("The downloaded DepotDownloader holds no program, so it was not used."));
            Directory.CreateDirectory(Path.GetDirectoryName(folder)!);
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            Directory.Move(unpacked, folder);
        }
        finally
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }
}

public enum SteamLineKind
{
    /// <summary>Nothing Waypoint acts on.</summary>
    Other,
    /// <summary>A file of the depot is whole: how far the depot is, and the file.</summary>
    Progress,
    /// <summary>A new sign-in code follows, row by row.</summary>
    CodeStart,
    /// <summary>One row of the sign-in code.</summary>
    CodeRow,
    /// <summary>The sign-in was approved; the tool names the account it now remembers.</summary>
    SignedIn,
    /// <summary>The account has no Path of Exile, or no account was used.</summary>
    NotAvailable,
    /// <summary>The remembered sign-in is no longer taken, or there is none: sign in again.</summary>
    SignInNeeded,
}

/// <param name="Cells">A row of the sign-in code: true for a dark square.</param>
public readonly record struct SteamLine(SteamLineKind Kind, double Percent = 0, string? File = null, string? Account = null, bool[]? Cells = null);

public enum SteamOutcome
{
    Done,
    /// <summary>Its user stopped it: what was fetched stays, and the next run goes on from it.</summary>
    Stopped,
    /// <summary>No sign-in the tool remembers is taken by Steam.</summary>
    SignInNeeded,
    /// <summary>The Steam account has no Path of Exile in its library.</summary>
    NotAvailable,
    Failed,
}

public sealed record SteamResult(SteamOutcome Outcome, string? Message = null);

/// <param name="Depot">Which of the build's depots is being fetched, from 1.</param>
/// <param name="Percent">How far that depot is.</param>
public sealed record SteamProgress(int Depot, int Depots, double Percent, string File);

/// <summary>
/// A client build from Steam, through <see cref="DepotTool"/>: which build, and the app, depots and manifests that hold it,
/// are LocalExile's to say (<see cref="ListedBuild"/>, from <c>LocalExile.exe builds</c>).
/// <list type="bullet">
/// <item>Steam's anonymous account cannot fetch the game, so a sign-in is needed once. It is the tool's own QR sign-in:
/// Waypoint shows the code the tool prints and the user approves it in the Steam mobile app. Waypoint sees no password and
/// no token. The tool keeps the sign-in (in the Windows user's isolated storage) and reports the account's name, which is
/// all Waypoint keeps, to name that sign-in on later runs.</item>
/// <item>A fetch into a folder that already holds a build downloads only what differs, and removes files the new build no
/// longer has; one that was stopped goes on from what it has.</item>
/// </list>
/// </summary>
public static partial class SteamDownload
{
    [GeneratedRegex(@"\A\s*(\d{1,3}\.\d{2})% (.+)\z")]
    private static partial Regex ProgressLine();

    [GeneratedRegex(@"login with -username (\S+) -remember-password")]
    private static partial Regex SignedInLine();

    [GeneratedRegex(@"Total bytes on disk\s*:\s*(\d+)")]
    private static partial Regex DiskBytes();

    /// <summary>What one line of the tool's output says.</summary>
    public static SteamLine Read(string line)
    {
        if (ProgressLine().Match(line) is { Success: true } progress
            && double.TryParse(progress.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double percent))
            return new(SteamLineKind.Progress, percent, progress.Groups[2].Value);
        if (line.Contains("sign in with this QR code", StringComparison.Ordinal)) return new(SteamLineKind.CodeStart);
        if (SignedInLine().Match(line) is { Success: true } signedIn) return new(SteamLineKind.SignedIn, Account: signedIn.Groups[1].Value);
        if (line.Contains("is not available from this account", StringComparison.Ordinal)) return new(SteamLineKind.NotAvailable);
        if (line.Contains("Access token was rejected", StringComparison.Ordinal) || line.Contains("Enter account password", StringComparison.Ordinal)
            || line.Contains("Unable to login to Steam3", StringComparison.Ordinal) || line.Contains("has stored credentials", StringComparison.Ordinal))
            return new(SteamLineKind.SignInNeeded);
        // A row of the code is two characters a square and nothing a word is made of.
        if (line.Length >= 42 && line.Length % 2 == 0 && !line.Any(char.IsAsciiLetterOrDigit))
        {
            bool[] cells = new bool[line.Length / 2];
            for (int i = 0; i < cells.Length; i++) cells[i] = line[i * 2] != ' ' || line[i * 2 + 1] != ' ';
            return new(SteamLineKind.CodeRow, Cells: cells);
        }
        return new(SteamLineKind.Other);
    }

    /// <summary>A line for a log: never a row of the sign-in code, and never the account's name.</summary>
    public static string? ForLog(string line, string? account)
    {
        var read = Read(line);
        if (read.Kind is SteamLineKind.CodeRow or SteamLineKind.Progress || line.Trim().Length == 0) return null;
        if (read.Account is { } named) line = line.Replace(named, "<account>");
        if (account is { Length: > 0 }) line = line.Replace(account, "<account>");
        return Regex.Replace(line, @"Logging '[^']*' into", "Logging '<account>' into");
    }

    static string[] Arguments(ListedBuild build, SteamDepot depot, string target) =>
        ["-app", build.SteamApp!, "-depot", depot.Depot, "-manifest", depot.Manifest, "-dir", target];

    /// <summary>The tool's QR sign-in. Each code it prints goes to <paramref name="code"/>, whole, and again when Steam
    /// changes it. The account's name once the user approved it in the Steam mobile app; null when it ended without one.
    /// Only the manifest of the build's smallest depot is fetched, into <paramref name="scratch"/>.</summary>
    public static async Task<string?> SignInAsync(string tool, ListedBuild build, string scratch, Action<bool[][]> code, Action<string> log, CancellationToken stop)
    {
        if (!build.FromSteam) return null;
        Directory.CreateDirectory(scratch);
        string? account = null;
        var rows = new List<bool[]>();
        void Line(string line)
        {
            var read = Read(line);
            if (ForLog(line, account) is { } text) log(text);
            switch (read.Kind)
            {
                case SteamLineKind.CodeStart: rows.Clear(); break;
                case SteamLineKind.CodeRow:
                    rows.Add(read.Cells!);
                    // A code is square: once it has as many rows as a row has squares it is whole.
                    if (rows.Count == read.Cells!.Length) code([.. rows]);
                    break;
                case SteamLineKind.SignedIn: account = read.Account; break;
            }
        }
        int exit = await ToolProcess.RunAsync(tool, [.. Arguments(build, build.Depots[^1], scratch), "-manifest-only", "-qr", "-remember-password"], scratch, Line, stop, input: "", utf8: true);
        return exit == 0 ? account : null;
    }

    /// <summary>How much room the build takes on disk, from its manifests (a few seconds, nothing else is fetched); null
    /// when it cannot be told, with why in <paramref name="result"/>.</summary>
    public static async Task<(long? Bytes, SteamResult Result)> SizeAsync(string tool, ListedBuild build, string account, string scratch, Action<string> log, CancellationToken stop)
    {
        Directory.CreateDirectory(scratch);
        long bytes = 0;
        foreach (var depot in build.Depots)
        {
            var result = await RunAsync(tool, [.. Arguments(build, depot, scratch), "-manifest-only", "-username", account, "-remember-password"], scratch, account, null, log, stop);
            if (result.Outcome != SteamOutcome.Done) return (null, result);
            string listing = Path.Combine(scratch, $"manifest_{depot.Depot}_{depot.Manifest}.txt");
            if (!File.Exists(listing) || DiskBytes().Match(await File.ReadAllTextAsync(listing, stop)) is not { Success: true } size) return (null, new(SteamOutcome.Failed));
            bytes += long.Parse(size.Groups[1].Value, CultureInfo.InvariantCulture);
        }
        return (bytes, new(SteamOutcome.Done));
    }

    /// <summary>Fetches the build into <paramref name="target"/>, depot after depot: a folder that holds another build of
    /// the game from Steam is brought to this one, and a stopped fetch is gone on with.</summary>
    public static async Task<SteamResult> FetchAsync(string tool, ListedBuild build, string account, string target, Action<SteamProgress>? progress, Action<string> log, CancellationToken stop)
    {
        if (!build.FromSteam) return new(SteamOutcome.Failed, L.Text("LocalExile names no Steam download for this version."));
        Directory.CreateDirectory(target);
        for (int i = 0; i < build.Depots.Count; i++)
        {
            int depot = i + 1;
            var result = await RunAsync(tool, [.. Arguments(build, build.Depots[i], target), "-username", account, "-remember-password"], target, account,
                progress is null ? null : (percent, file) => progress(new(depot, build.Depots.Count, percent, file)), log, stop);
            if (result.Outcome != SteamOutcome.Done) return result;
        }
        return new(SteamOutcome.Done);
    }

    static async Task<SteamResult> RunAsync(string tool, string[] arguments, string directory, string account, Action<double, string>? progress, Action<string> log, CancellationToken stop)
    {
        bool notAvailable = false, signIn = false;
        string? last = null;
        void Line(string line)
        {
            var read = Read(line);
            if (ForLog(line, account) is { } text)
            {
                log(text);
                last = text;
            }
            switch (read.Kind)
            {
                case SteamLineKind.Progress: progress?.Invoke(read.Percent, Path.GetFileName(read.File!)); break;
                case SteamLineKind.NotAvailable: notAvailable = true; break;
                case SteamLineKind.SignInNeeded: signIn = true; break;
            }
        }
        try
        {
            int exit = await ToolProcess.RunAsync(tool, arguments, directory, Line, stop, input: "", utf8: true);
            if (exit == 0) return new(SteamOutcome.Done);
        }
        catch (OperationCanceledException) { return new(SteamOutcome.Stopped); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException) { return new(SteamOutcome.Failed, e.Message); }
        return signIn ? new(SteamOutcome.SignInNeeded) : notAvailable ? new(SteamOutcome.NotAvailable) : new(SteamOutcome.Failed, last);
    }

    /// <summary>What an outcome that is not Done means, for its user.</summary>
    public static string Explain(SteamResult result) => result.Outcome switch
    {
        SteamOutcome.NotAvailable => L.Text("That Steam account has no Path of Exile. Add the game to its library in Steam (it is free), then try again."),
        SteamOutcome.SignInNeeded => L.Text("Steam asks for the sign-in again."),
        SteamOutcome.Stopped => L.Text("Stopped. What was downloaded is kept, and the next try goes on from it."),
        _ => result.Message is { Length: > 0 } message ? L.Text("The Steam download did not finish: {0}", message) : L.Text("The Steam download did not finish."),
    };
}
