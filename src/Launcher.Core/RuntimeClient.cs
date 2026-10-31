using System.Diagnostics;
using System.Text.Json;

namespace Launcher.Core;

public sealed class RuntimeClient
{
    readonly string bridge;
    public RuntimeClient(string? bridgePath = null) => bridge = bridgePath ?? Path.Combine(AppContext.BaseDirectory, "RuntimeBridge.ps1");

    public async Task<BridgeResult> RunAsync(LauncherSettings settings, string action, string service = "All",
        Action<string>? log = null, CancellationToken cancellation = default,
        Action<OperationProgress>? progress = null, AccountRequest? account = null,
        SourceSwitchRequest? source = null, string? backupId = null, ModRequest? mod = null)
    {
        if (!new[] { "Inspect", "Start", "Play", "Stop", "CloseGame", "Restart", "Build", "PrepareClient", "Dependencies", "Register", "Switch", "Fetch", "ReloadData", "Backup", "RestoreBackup", "DeleteBackup", "BuildMod", "BuildMods", "EnableMod", "DisableMod", "ReloadMod", "QueryMods", "RestoreModChoices" }.Contains(action))
            throw new ArgumentException("Unknown runtime operation.", nameof(action));
        if (!new[] { "All", "Auth", "Patch", "Gateway" }.Contains(service))
            throw new ArgumentException("Unknown service.", nameof(service));
        if (action == "Register" && account is null)
            throw new ArgumentException("Local account details are required.", nameof(account));
        if (action == "Switch" && source is null) throw new ArgumentException("Select a source first.", nameof(source));
        string directory = Path.Combine(SettingsStore.DataDirectory, "operations", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string resultFile = Path.Combine(directory, "result.json");
        string logFile = Path.Combine(directory, "operation.log");
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = action == "Register",
            // No redirected streams: live.ps1 is explicitly required to run without pipes.
            WorkingDirectory = Directory.Exists(settings.RepositoryPath) ? settings.RepositoryPath : AppContext.BaseDirectory
        };
        foreach (string argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", bridge,
                     "-Action", action, "-RepoRoot", settings.RepositoryPath, "-Service", service, "-ResultPath", resultFile })
            start.ArgumentList.Add(argument);
        if (settings.UseLocalProfile) start.ArgumentList.Add("-UseLocalProfile");
        if (!settings.HotReload) start.ArgumentList.Add("-NoHotReload");
        if (!settings.AutomaticBackups) start.ArgumentList.Add("-NoBackup");
        if (source is not null)
        {
            foreach (string argument in new[] { "-SourceKind", source.Kind, "-SourceValue", source.Value }) start.ArgumentList.Add(argument);
            if (source.StartFresh) start.ArgumentList.Add("-StartFresh");
        }
        if (backupId is not null) { start.ArgumentList.Add("-BackupId"); start.ArgumentList.Add(backupId); }
        if (mod is not null)
        {
            if (!string.IsNullOrEmpty(mod.Id) && !ModCatalog.ValidId(mod.Id)) throw new ArgumentException("Unsupported mod ID.", nameof(mod));
            if (mod.Id.Length > 0) { start.ArgumentList.Add("-ModId"); start.ArgumentList.Add(mod.Id); }
            if (mod.Project.Length > 0) { start.ArgumentList.Add("-ModProject"); start.ArgumentList.Add(mod.Project); }
        }
        // Never inherit a developer shell's Game_* overrides into an unrelated checkout.
        foreach (string key in start.Environment.Keys.Where(k => k.StartsWith("Game_", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(key);
        foreach (string key in start.Environment.Keys.Where(k => k.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray()) start.Environment.Remove(key);
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GCM_INTERACTIVE"] = "never";
        if (action != "Inspect") log?.Invoke($"{action} {service} · operation log: {Path.Combine(directory, action == "Register" ? "account-service.log" : "operation.log")}");
        using var process = Process.Start(start) ?? throw new IOException("Could not start the LocalExile adapter.");
        if (action == "Register")
        {
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(account));
            process.StandardInput.Close();
        }
        long position = 0;
        string progressFile = Path.Combine(directory, "progress.json");
        string lastProgress = "";
        var reader = new OperationProgressReader();
        void Output(string text)
        {
            log?.Invoke(text);
            foreach (string detail in reader.Append(text))
            {
                string extractionLog = Path.Combine(settings.RepositoryPath, "logs", "extract-" + detail["Extracting ".Length..].Replace(' ', '-') + ".log");
                progress?.Invoke(new("Extracting data", detail, extractionLog));
            }
        }
        async Task ReadProgress()
        {
            if (File.Exists(progressFile) && progress is not null)
            {
                try
                {
                    // Allow the adapter to atomically replace this file while we read the previous stage.
                    await using var stream = new FileStream(progressFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var stageReader = new StreamReader(stream);
                    string value = await stageReader.ReadToEndAsync();
                    if (value != lastProgress)
                    {
                        var update = JsonSerializer.Deserialize<OperationProgress>(value, SettingsStore.JsonOptions);
                        if (update is not null) progress(update);
                        lastProgress = value;
                    }
                }
                catch (IOException) { }
                catch (JsonException) { }
            }
        }
        while (!process.HasExited)
        {
            await ReadProgress();
            if (File.Exists(logFile)) position = await ReadNewTextAsync(logFile, position, Output);
            // Cancellation stops waiting only for read-only inspection. Runtime work must finish or report failure;
            // killing the script could leave a build or a half-started environment behind.
            if (action == "Inspect") cancellation.ThrowIfCancellationRequested();
            await Task.Delay(300, action == "Inspect" ? cancellation : CancellationToken.None);
        }
        if (File.Exists(logFile)) await ReadNewTextAsync(logFile, position, Output);
        await ReadProgress();
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
            if (text.Length != 0) append(text);
            return stream.Position;
        }
        catch (IOException) { return offset; }
    }
}

public sealed record AccountRequest(string Name, string Password);
