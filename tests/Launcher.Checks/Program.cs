using System.Text.Json;
using Launcher.Core;

var client = new RuntimeClient();
if (args.Length >= 2 && args[0] == "--inspect")
{
    var result = await client.RunAsync(new(args[1]), "Inspect");
    Console.WriteLine(JsonSerializer.Serialize(result, SettingsStore.JsonOptions));
    return result.Ok ? 0 : 1;
}
if (args.Length >= 3 && args[0] == "--operation")
{
    var result = await client.RunAsync(new(args[2]), args[1], args.Length > 3 ? args[3] : "All", Console.WriteLine);
    Console.WriteLine(JsonSerializer.Serialize(result, SettingsStore.JsonOptions));
    return result.Ok ? 0 : 1;
}

int passed = 0;
void Check(bool condition, string description)
{
    if (!condition) throw new Exception("FAIL: " + description);
    passed++;
    Console.WriteLine("PASS: " + description);
}
string fixture = Path.Combine(Path.GetTempPath(), "Launcher checks " + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(fixture);
try
{
    string settingsPath = Path.Combine(fixture, "settings.json");
    Check(SettingsStore.Load(settingsPath).RepositoryPath == "", "first run has no machine-specific path");
    var original = new LauncherSettings(@"C:\A checkout with spaces\LocalExile", false);
    SettingsStore.Save(original, settingsPath);
    Check(SettingsStore.Load(settingsPath) == original, "settings round-trip paths with spaces and runtime selection");
    SettingsStore.Save(original with { UseLocalProfile = true }, settingsPath);
    Check(SettingsStore.Load(settingsPath).UseLocalProfile, "atomic settings replacement");
    File.WriteAllText(settingsPath, "{");
    bool rejected = false;
    try { SettingsStore.Load(settingsPath); } catch (JsonException) { rejected = true; }
    Check(rejected, "malformed settings do not silently reset the selected checkout");

    var missing = await client.RunAsync(new(fixture), "Inspect");
    Check(!missing.Ok && missing.Message.Contains("GameServer.slnx"), "arbitrary folders are refused before inspecting or changing runtime");
    Directory.CreateDirectory(Path.Combine(fixture, "scripts", "dev"));
    File.WriteAllText(Path.Combine(fixture, "GameServer.slnx"), "<Solution />");
    File.WriteAllText(Path.Combine(fixture, "scripts", "dev", "live.ps1"), "param([switch]$ClientOnly)");
    var inspected = await client.RunAsync(new(fixture), "Inspect");
    Check(inspected.Ok && inspected.Snapshot is not null, "inspection supports checkout paths with spaces: " + inspected.Message);
    var state = inspected.Snapshot!;
    Check(!state.SetupReady && !state.SafeToLaunch, "missing client and login keys block Play");
    Check(state.Services.Length == 4, "inspection distinguishes three server services and the game");
    Check(state.Services.All(s => s.ProcessId == 0), "unrelated running processes are never adopted by a new checkout");
    Check(!Directory.Exists(Path.Combine(fixture, "data")) && !Directory.Exists(Path.Combine(fixture, "logs")), "inspection does not create runtime data or logs in the checkout");
    var start = await client.RunAsync(new(fixture), "Start");
    Check(!start.Ok, "incomplete setup cannot start services");
    Check(!Directory.Exists(Path.Combine(fixture, "data")), "failed preflight does not initialize saves");

    Directory.CreateDirectory(Path.Combine(fixture, "config"));
    File.WriteAllText(Path.Combine(fixture, "scripts", "dev", "live.local.ps1"), "throw 'must never execute during inspection'");
    File.WriteAllText(Path.Combine(fixture, "config", "runtime.local.ps1"), "$env:Game_SAVES_DB = 'data/existing.db'");
    var local = await client.RunAsync(new(fixture), "Inspect");
    Check(local.Ok && local.Snapshot!.SaveDatabase == Path.Combine(fixture, "data", "existing.db"), "constant local runtime profile resolves save paths without executing the wrapper");
    Check(local.Snapshot!.Requirements.Any(r => r.Name == "Existing save database" && !r.Ready), "missing existing profile saves are never replaced with an empty database");
    File.WriteAllText(Path.Combine(fixture, "config", "runtime.local.ps1"), "Set-Content -LiteralPath './executed.txt' -Value 'bad'");
    var complex = await client.RunAsync(new(fixture), "Inspect");
    Check(!complex.Ok && !File.Exists(Path.Combine(fixture, "executed.txt")), "unsupported profile code is rejected without executing it");

    var healthy = state with { Requirements = [new("test", true, "")], Conflicts = [], Services = state.Services.Select(s => s with { Status = s.Name == "Game" ? "Stopped" : "Ready", ProcessId = s.Name == "Game" ? 0 : 1 }).ToArray() };
    Check(healthy.ServersReady && healthy.SafeToLaunch, "all owned listeners and setup allow Play");
    Check(!(healthy with { Services = healthy.Services.Select(s => s.Name == "Gateway" ? s with { Status = "Starting" } : s).ToArray() }).ServersReady, "running process without readiness is not treated as ready");
    Check(!(healthy with { Conflicts = ["foreign listener"] }).SafeToLaunch, "foreign listener blocks Play");
    Console.WriteLine($"{passed} checks passed.");
    return 0;
}
finally
{
    // Fixture is created above with a fresh GUID; never accept an external cleanup target.
    if (Directory.Exists(fixture)) Directory.Delete(fixture, true);
}
