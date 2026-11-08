using System.Diagnostics;
using System.Text.Json;

namespace Launcher.Core;

/// <summary>Optional read-only client-art previews. Failure never blocks league inspection or configuration.</summary>
public sealed record LeagueArtwork(string Normal, string Hardcore)
{
    public static async Task<LeagueArtwork?> LoadAsync(string root, CancellationToken cancellation = default)
    {
        if (!File.Exists(Path.Combine(root, "Client/Content.ggpk"))) return null;
        var start = new ProcessStartInfo(Platform.Python) { WorkingDirectory = root, CreateNoWindow = true, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { Path.Combine(Path.GetDirectoryName(BridgeScripts.Extract())!, "LeagueArt.py"), root,
                     Path.Combine(SettingsStore.DataDirectory, "league-art") }) start.ArgumentList.Add(argument);
        try
        {
            using var process = Process.Start(start)!;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(TimeSpan.FromSeconds(60));
            var output = process.StandardOutput.ReadToEndAsync(cancellation); var error = process.StandardError.ReadToEndAsync(cancellation);
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } return null; }
            await error;
            if (process.ExitCode != 0) return null;
            using var result = JsonDocument.Parse(await output);
            return new(result.RootElement.GetProperty("normal").GetString()!, result.RootElement.GetProperty("hardcore").GetString()!);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or JsonException or InvalidOperationException)
        { return null; }
    }
}
