using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;

namespace Launcher.Core.LootFilter;

/// <summary>
/// The game's item filter sounds for the editor, from the client the checkout runs: they are FMOD samples in the loose file
/// FMOD/Desktop/ItemFilter.bank (FSB5, Vorbis without its setup headers, which no browser plays). vgmstream decodes them to
/// WAV once per bank into Waypoint's own data; it is downloaded from one pinned release (vgmstream/vgmstream, ISC-style
/// licence) and checked against its SHA-256. A standalone client keeps the bank inside Content.ggpk, which this does not read.
/// </summary>
public static class FilterSounds
{
    /// <summary>The ids PlayAlertSound takes: 1 to 16, and the named ones.</summary>
    public static readonly IReadOnlyList<string> Ids =
        [.. Enumerable.Range(1, 16).Select(n => n.ToString(System.Globalization.CultureInfo.InvariantCulture)),
         "ShAlchemy", "ShBlessed", "ShChaos", "ShDivine", "ShExalted", "ShFusing", "ShGeneral", "ShMirror", "ShRegal", "ShVaal"];

    const string Release = "r2117";
    static readonly (string Asset, string Sha256, string Exe) Decoder = OperatingSystem.IsWindows()
        ? ("vgmstream-win64.zip", "6c4a8a3813864fefed081bbd337dbc0ad93bf88e0b92f5db98d7ab258b22dc6c", "vgmstream-cli.exe")
        : ("vgmstream-linux.zip", "2f98c77f756079f63fbd119939067f1ed461d77e70993bc4cc372736d859c84a", "vgmstream-cli");

    /// <summary>The sample a PlayAlertSound id plays: 7 is AlertSound_07, ShDivine is SH22Divine.</summary>
    public static string? Sample(string id) =>
        int.TryParse(id, out int n) && n is >= 1 and <= 16 ? $"AlertSound_{n:00}"
        : id.StartsWith("Sh", StringComparison.OrdinalIgnoreCase) && Ids.Contains(id, StringComparer.OrdinalIgnoreCase) ? "SH22" + id[2..]
        : null;

    public static string Bank(string client) => Path.Combine(client, "FMOD", "Desktop", "ItemFilter.bank");

    /// <summary>The folder holding the client's filter sounds as WAV files, decoded first if they are not yet.</summary>
    public static async Task<string> FolderAsync(HttpClient http, string client, string cache, CancellationToken stop = default)
    {
        string bank = Bank(client);
        if (!File.Exists(bank)) throw new FileNotFoundException($"{bank} is missing (a standalone client keeps it inside Content.ggpk).", bank);
        string hash;
        await using (var stream = File.OpenRead(bank)) hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, stop))[..12];
        string folder = Path.Combine(cache, "sounds", hash), done = Path.Combine(folder, ".done");
        if (File.Exists(done)) return folder;

        string exe = await DecoderAsync(http, cache, stop);
        string temp = folder + ".tmp";
        if (Directory.Exists(temp)) Directory.Delete(temp, true);
        Directory.CreateDirectory(temp);
        var start = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = temp };
        foreach (var arg in new[] { "-S", "0", "-o", Path.Combine(temp, "?n.wav"), bank }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("vgmstream did not start");
        var output = process.StandardOutput.ReadToEndAsync(stop);
        string error = await process.StandardError.ReadToEndAsync(stop);
        await process.WaitForExitAsync(stop);
        await output;
        if (process.ExitCode != 0 || !File.Exists(Path.Combine(temp, "AlertSound_01.wav")))
            throw new InvalidOperationException($"vgmstream could not decode {bank}: {error.Trim()}");
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
        Directory.Move(temp, folder);
        await File.WriteAllTextAsync(done, bank, stop);
        return folder;
    }

    static async Task<string> DecoderAsync(HttpClient http, string cache, CancellationToken stop)
    {
        string folder = Path.Combine(cache, "vgmstream-" + Release), exe = Path.Combine(folder, Decoder.Exe);
        if (File.Exists(exe)) return exe;
        byte[] zip = await http.GetByteArrayAsync($"https://github.com/vgmstream/vgmstream/releases/download/{Release}/{Decoder.Asset}", stop);
        if (Convert.ToHexStringLower(SHA256.HashData(zip)) != Decoder.Sha256)
            throw new InvalidDataException($"{Decoder.Asset} of vgmstream {Release} does not match its pinned hash.");
        string temp = folder + ".tmp";
        if (Directory.Exists(temp)) Directory.Delete(temp, true);
        using (var archive = new ZipArchive(new MemoryStream(zip))) archive.ExtractToDirectory(temp);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(Path.Combine(temp, Decoder.Exe), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
        Directory.Move(temp, folder);
        return exe;
    }
}
