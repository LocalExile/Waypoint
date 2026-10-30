using System.Diagnostics;
using System.Text.Json;

namespace Launcher.Core;

public sealed class RuntimeClient
{
    readonly string bridge;
    public RuntimeClient(string? bridgePath = null) => bridge = bridgePath ?? Path.Combine(AppContext.BaseDirectory, "RuntimeBridge.ps1");

    public async Task<BridgeResult> RunAsync(LauncherSettings settings, string action, string service = "All",
        Action<string>? log = null, CancellationToken cancellation = default)
    {
        if (!new[] { "Inspect", "Start", "Play", "Stop", "CloseGame", "Restart", "Build" }.Contains(action))
            throw new ArgumentException("Unknown runtime operation.", nameof(action));
        if (!new[] { "All", "Auth", "Patch", "Gateway" }.Contains(service))
            throw new ArgumentException("Unknown service.", nameof(service));
        string directory = Path.Combine(SettingsStore.DataDirectory, "operations", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string resultFile = Path.Combine(directory, "result.json");
        string logFile = Path.Combine(directory, "operation.log");
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true,
            // No redirected streams: live.ps1 is explicitly required to run without pipes.
            WorkingDirectory = Directory.Exists(settings.RepositoryPath) ? settings.RepositoryPath : AppContext.BaseDirectory
        };
        foreach (string argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", bridge,
                     "-Action", action, "-RepoRoot", settings.RepositoryPath, "-Service", service, "-ResultPath", resultFile })
            start.ArgumentList.Add(argument);
        if (settings.UseLocalProfile) start.ArgumentList.Add("-UseLocalProfile");
        // Never inherit a developer shell's Game_* overrides into an unrelated checkout.
        foreach (string key in start.Environment.Keys.Where(k => k.StartsWith("Game_", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(key);
        if (action != "Inspect") log?.Invoke($"{action} {service} · operation log: {logFile}");
        using var process = Process.Start(start) ?? throw new IOException("Could not start the LocalExile adapter.");
        long position = 0;
        while (!process.HasExited)
        {
            if (File.Exists(logFile) && log is not null) position = await ReadNewTextAsync(logFile, position, log);
            // Cancellation stops waiting only for read-only inspection. Runtime work must finish or report failure;
            // killing the script could leave a build or a half-started environment behind.
            if (action == "Inspect") cancellation.ThrowIfCancellationRequested();
            await Task.Delay(300, action == "Inspect" ? cancellation : CancellationToken.None);
        }
        if (File.Exists(logFile) && log is not null) await ReadNewTextAsync(logFile, position, log);
        if (!File.Exists(resultFile)) throw new IOException($"The adapter exited ({process.ExitCode}) without a result. Read {logFile}.");
        var result = JsonSerializer.Deserialize<BridgeResult>(await File.ReadAllTextAsync(resultFile), SettingsStore.JsonOptions)
            ?? throw new IOException("The runtime adapter returned an empty result.");
        if (action == "Inspect") Directory.Delete(directory, true); // Only this call's new, private result directory.
        return result;
    }

    static async Task<long> ReadNewTextAsync(string path, long offset, Action<string> append)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            stream.Seek(offset, SeekOrigin.Begin);
            using var reader = new StreamReader(stream);
            string text = await reader.ReadToEndAsync();
            if (text.Length != 0) append(text.TrimEnd());
            return stream.Position;
        }
        catch (IOException) { return offset; }
    }
}
