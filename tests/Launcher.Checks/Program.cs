using System.Diagnostics;
using System.Text.Json;
using Launcher.Core;

var client = new RuntimeClient();
if (OperatingSystem.IsWindows() && args.Length == 2 && args[0] == "--instance-host")
{
    InstanceChecks.Host(args[1]);
    return 0;
}
if (args.Length == 2 && args[0] == "--accounts")
{
    var result = await client.RunAsync(new(args[1], UseLocalProfile: false), "QueryAccounts");
    Console.WriteLine(JsonSerializer.Serialize(result, SettingsStore.JsonOptions));
    return result.Ok ? 0 : 1;
}
if (args.Length == 2 && args[0] == "--account-change")
{
    var request = JsonSerializer.Deserialize<AccountDataRequest>(await Console.In.ReadLineAsync() ?? "", SettingsStore.JsonOptions)
        ?? throw new InvalidDataException("Missing disposable account request");
    var result = await client.RunAsync(new(args[1], UseLocalProfile: false), "ManageAccount", accountData: request);
    Console.WriteLine(JsonSerializer.Serialize(result, SettingsStore.JsonOptions));
    return result.Ok ? 0 : 1;
}
if (args.Length > 0 && args[0] == "--discover")
{
    var roots = args.Length > 1 ? new[] { new RepositorySearchRoot(args[1], 4) }
        : RepositoryDiscovery.DefaultRoots(AppContext.BaseDirectory, Environment.CurrentDirectory);
    Console.WriteLine(JsonSerializer.Serialize(await RepositoryLocator.FindAsync(roots), SettingsStore.JsonOptions));
    return 0;
}
if (args.Length >= 2 && args[0] == "--inspect")
{
    var result = await client.RunAsync(new(args[1]), "Inspect");
    Console.WriteLine(JsonSerializer.Serialize(result, SettingsStore.JsonOptions));
    return result.Ok ? 0 : 1;
}
if (args.Length >= 3 && args[0] == "--operation")
{
    var result = await client.RunAsync(new(args[2]), args[1], args.Length > 3 ? args[3] : "All", Console.WriteLine,
        progress: update => Console.WriteLine("STAGE: " + update.Stage + " · " + update.Detail));
    Console.WriteLine(JsonSerializer.Serialize(result, SettingsStore.JsonOptions));
    return result.Ok ? 0 : 1;
}
if (args.Length >= 2 && args[0] == "--register")
{
    var request = JsonSerializer.Deserialize<AccountRequest>(await Console.In.ReadLineAsync() ?? "", SettingsStore.JsonOptions)
        ?? throw new ArgumentException("Account request required on stdin.");
    var result = await client.RunAsync(new(args[1]), "Register", log: Console.WriteLine,
        progress: update => Console.WriteLine("STAGE: " + update.Stage), account: request);
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
File.WriteAllText(Path.Combine(fixture, ".waypoint-test-fixture"), "disposable checks only");
try
{
    if (OperatingSystem.IsWindows()) await InstanceChecks.RunAsync(Check);
    ModChecks.Run(fixture, Check);
    LocalizationChecks.Run(Check);
    await SetupChecks.RunAsync(fixture, Check);
    string settingsPath = Path.Combine(fixture, "settings.json");
    Check(SettingsStore.Load(settingsPath).RepositoryPath == "", "first run has no machine-specific path");
    Check(SettingsStore.Load(settingsPath) is { AutomaticBackups: true, HotReload: true }, "backups and Gateway watch default on for every save choice");
    File.WriteAllText(settingsPath, "{\"RepositoryPath\":\"older-settings\",\"UseLocalProfile\":false}");
    Check(SettingsStore.Load(settingsPath) is { AutomaticBackups: true, HotReload: true }, "older settings inherit backup and watch defaults");
    Check(SettingsStore.Load(settingsPath).Language == "en", "older settings default to English without changing the selected checkout");
    Check(SettingsStore.Load(settingsPath) is { CloseToTray: false, CloseBehaviorConfirmed: false, StopServicesOnExit: false, AlwaysAskOnClose: false }, "older settings leave new close preferences off and offer a first-close choice");
    var closeSettings = new LauncherSettings();
    Check(WindowClosePolicy.Decide(closeSettings, false, false, false) == WindowCloseAction.Ask, "first close asks without enabling tray mode");
    Check(WindowClosePolicy.Decide(closeSettings, true, false, false) == WindowCloseAction.Ask, "first close can offer to keep an operation running in the tray");
    Check(WindowClosePolicy.Decide(closeSettings with { CloseBehaviorConfirmed = true }, false, false, false) == WindowCloseAction.Exit, "confirmed default exits normally");
    Check(WindowClosePolicy.Decide(closeSettings with { CloseBehaviorConfirmed = true }, true, false, false) == WindowCloseAction.Wait, "normal exit protects active operations");
    var traySettings = closeSettings with { CloseToTray = true, CloseBehaviorConfirmed = true };
    Check(WindowClosePolicy.Decide(traySettings, true, false, false) == WindowCloseAction.Hide, "hiding keeps active operations running");
    Check(WindowClosePolicy.Decide(traySettings, false, true, false) == WindowCloseAction.Exit, "explicit tray exit bypasses the hide preference");
    Check(WindowClosePolicy.Decide(traySettings, true, true, false) == WindowCloseAction.Wait, "explicit exit still protects active operations");
    Check(WindowClosePolicy.Decide(closeSettings, true, false, true) == WindowCloseAction.Exit, "Windows session ending never opens a close prompt or hides instead");
    var alwaysAsk = traySettings with { AlwaysAskOnClose = true };
    Check(WindowClosePolicy.Decide(alwaysAsk, false, false, false) == WindowCloseAction.Ask && WindowClosePolicy.Decide(alwaysAsk, true, false, false) == WindowCloseAction.Ask, "always ask overrides a remembered close mode, including while work is active");
    Check(WindowClosePolicy.Decide(alwaysAsk, false, true, false) == WindowCloseAction.Exit, "explicit tray Exit remains a direct exit even with always ask enabled");
    SettingsStore.Save(alwaysAsk, settingsPath);
    Check(SettingsStore.Load(settingsPath) is { AlwaysAskOnClose: true, CloseBehaviorConfirmed: true, CloseToTray: true }, "always ask persists independently of the remembered default action");
    SettingsStore.Save(traySettings with { RepositoryPath = "preserved-checkout", Language = "ru" }, settingsPath);
    Check(SettingsStore.Load(settingsPath) is { CloseToTray: true, CloseBehaviorConfirmed: true, RepositoryPath: "preserved-checkout", Language: "ru" }, "tray preferences round-trip alongside workspace and language");
    SettingsStore.Save(traySettings with { StopServicesOnExit = true }, settingsPath);
    Check(SettingsStore.Load(settingsPath) is { CloseToTray: true, StopServicesOnExit: true }, "tray hiding and full-exit service stopping persist as independent preferences");
    Check(HotReloadStatus.Read("[gateway] hot reload: File.cs saved\n", true, true).State == "Pending", "a source save reports pending without claiming application");
    Check(HotReloadStatus.Read("[gateway] hot reload #2 applied in 0.42 seconds (types: X)\n[gateway] hot reload failed: 1 compile error(s): broken", true, true).State == "Compile failed", "latest explicit Gateway event takes precedence over an older applied edit");
    Check(HotReloadStatus.Read("dotnet watch Build succeeded", true, true).State == "Watching", "successful builds alone do not prove a hot edit applied");
    Check(HotReloadStatus.Read("15:00:00.123 [gateway] 127.0.0.1 chat \"[gateway] hot reload #1 applied\"", true, true).State == "Watching", "chat text cannot masquerade as a confirmed Gateway reload event");
    Check(HotReloadStatus.Read("[gateway] hot reload #1 applied", false, false).State == "Stopped", "stale log success cannot override a stopped process");
    Check(HotReloadStatus.Read("[gateway] hot reload: File.cs saved\n[gateway] listening on 127.0.0.1:6112\n", true, true).State == "Gateway restarted", "startup after a pending edit is reported as a restart even when the launcher opens afterward");
    Check(new ServiceStatus("Gateway", "Starting", 0, "", "", [6112], 0, 0, "", 123).IsRunning, "watcher stays owned/running during child restart gaps");
    var tracker = new HotReloadTracker();
    var gatewayGeneration = new ServiceStatus("Gateway", "Ready", 10, "first", "", [6112], 0, 0, "", 123);
    string savedEvent = "15:00:00.123 [gateway] hot reload: File.cs saved\n";
    Check(tracker.Update(gatewayGeneration, savedEvent).State == "Pending", "first watched process retains explicit pending event");
    tracker.Update(gatewayGeneration with { ProcessId = 0 }, savedEvent);
    Check(tracker.Update(gatewayGeneration with { ProcessId = 11, StartedUtc = "second" }, savedEvent).State == "Gateway restarted",
        "child restart gaps produce a verified restart even when the old log ended pending");
    Check(tracker.Update(gatewayGeneration with { ProcessId = 11, StartedUtc = "second" }, savedEvent).State == "Gateway restarted",
        "stale pending output cannot overwrite the observed restart on the next poll");
    Check(tracker.Update(gatewayGeneration with { ProcessId = 11, StartedUtc = "second" }, savedEvent + "15:01:00.123 [gateway] hot reload #1 applied\n").State == "Applied",
        "new Gateway events supersede the previous restart state");
    var original = new LauncherSettings(@"C:\A checkout with spaces\LocalExile", false);
    SettingsStore.Save(original, settingsPath);
    Check(SettingsStore.Load(settingsPath) == original, "settings round-trip paths with spaces and runtime selection");
    SettingsStore.Save(original with { UseLocalProfile = true }, settingsPath);
    Check(SettingsStore.Load(settingsPath).UseLocalProfile, "atomic settings replacement");
    SettingsStore.Save(original with { Language = "ru" }, settingsPath);
    Check(SettingsStore.Load(settingsPath) == original with { Language = "ru" }, "Russian preference persists with existing repository and runtime choices");
    var appearance=new AppearanceSettings(Mode:"System",Scheme:"custom",DarkColors:new(){["PrimaryFill"]="#4567AB"},Font:"OpenDyslexic",TextScale:125,StrongFocus:true,ReduceMotion:true);
    SettingsStore.Save(original with{Appearance=appearance},settingsPath);
    var appearanceSettings=SettingsStore.Load(settingsPath);
    Check(appearanceSettings.RepositoryPath==original.RepositoryPath && appearanceSettings.Appearance is{Mode:"System",Scheme:"custom",Font:"OpenDyslexic",TextScale:125,StrongFocus:true,ReduceMotion:true} && appearanceSettings.Appearance.DarkColors!["PrimaryFill"]=="#4567AB", "appearance, custom colors and accessibility preferences persist without changing runtime selection");
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
    Check(start.FailedStage == "Checking setup", "failed operations identify the actual stage");
    var unpaired = Path.Combine(fixture, "Client");
    Directory.CreateDirectory(unpaired);
    File.WriteAllText(Path.Combine(unpaired, "Content.ggpk"), "fixture archive");
    File.WriteAllText(Path.Combine(unpaired, "PathOfExile.exe"), "fixture original");
    File.WriteAllText(Path.Combine(unpaired, "Game.patched.exe"), "preserve prepared fixture");
    var isolatedPreparation = new RuntimeClient(Path.Combine(AppContext.BaseDirectory, "FixtureRuntimeBridge.ps1"));
    var prepare = await isolatedPreparation.RunAsync(new(fixture), "PrepareClient");
    Check(!prepare.Ok && prepare.Message.Contains("without keys"), "preparation refuses an executable without matching keys");
    Check(!File.Exists(Path.Combine(unpaired, "Game.exe")) && File.ReadAllText(Path.Combine(unpaired, "Game.patched.exe")) == "preserve prepared fixture",
        "preparation refuses unpaired client before writing anything");

    var progress = new OperationProgressReader();
    Check(!progress.Append("extracting object-").Any(), "partial extraction lines do not invent progress");
    Check(progress.Append("types (missing) ...\r\nother text\nextracting tables (missing) ...\n").SequenceEqual(["Extracting object types", "Extracting tables"]),
        "split extraction messages produce named stages in order");
    var clientStep = SetupGuidance.Describe(new("Client archive", false, "missing"), fixture);
    Check(clientStep.Action == "ClientFolder" && clientStep.Instructions.Contains(Path.Combine(fixture, "Client")) && clientStep.Instructions.Contains("do not move"),
        "missing client instructions identify the physical copy destination");
    Check(SetupGuidance.Describe(new("Existing save database", false, ""), fixture).Action == "Guide",
        "missing existing saves offer recovery guidance without an empty replacement");
    string sensitive = "password=\"private phrase\"\ntoken=tok-123\nAuthorization: Bearer abc.def.ghi\nKey64: XYZ+/=\n" +
        "clientDigestHash=" + new string('a', 64) + "\nName = HiddenCharacter, connected\nHiddenCharacter entered the world\n" +
        "{\"username\":\"HiddenAccount\",\"secret\":\"keep-out\"}\nHiddenAccount logged in\n" +
        @"C:\Users\OtherPerson\Game\client.log F:\PrivateCheckout\Client\logs" + "\nperson@example.com\nKnownPlayer\n";
    string redacted = DiagnosticReport.Redact(sensitive, ["KnownPlayer"], [@"F:\PrivateCheckout"]);
    Check(new[] { "private phrase", "tok-123", "abc.def.ghi", "XYZ+/=", new string('a', 64), "HiddenCharacter", "HiddenAccount", "keep-out", "OtherPerson", "PrivateCheckout", "person@example.com", "KnownPlayer" }
        .All(value => !redacted.Contains(value)), "diagnostics redact credentials, labelled and repeated names, private paths and email addresses");
    string escaped = DiagnosticReport.Redact(JsonSerializer.Serialize(new { password = "head\"tail-secret", file = @"F:\PrivateCheckout\logs", home = @"C:\Users\OtherPerson\logs" },
        new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }), privatePaths: [@"F:\PrivateCheckout"]);
    Check(!escaped.Contains("tail-secret") && !escaped.Contains("PrivateCheckout") && !escaped.Contains("OtherPerson"), "diagnostics handle JSON escaped credentials and paths");
    Check(LogReader.Filter("INFO ready\nWARNING timeout\nERROR failed", "time", true).Contains("WARNING timeout") &&
        !LogReader.Filter("INFO ready\nWARNING timeout\nERROR failed", "time", true).Contains("ERROR"), "log search and severity filters combine");
    string gameLogs = Path.Combine(fixture, "Client", "logs");
    Directory.CreateDirectory(gameLogs);
    File.WriteAllText(Path.Combine(gameLogs, "credentials.json"), "not a log");
    Check(LogReader.GameLog(fixture).EndsWith("Client.txt"), "game log lookup never selects arbitrary private files");
    File.WriteAllText(Path.Combine(gameLogs, "Client.txt"), new string('z', 100_000));
    Check(LogReader.Tail(LogReader.GameLog(fixture)).Length == 65_536, "log reads remain bounded");

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
    string commit = new string('b', 40);
    string report = DiagnosticReport.Create(healthy with { SourceCommit = commit, BuildCommit = "Auth=1.0.0+" + commit }, "test", string.Join('\n', Enumerable.Range(0, 300).Select(n => "OP_" + n)),
        "failed", "Game", sensitive, ["KnownPlayer"]);
    Check(report.Contains("Source commit: " + commit) && report.Contains("Built Auth: 1.0.0+" + commit) && report.Contains("OP_299") && !report.Contains("OP_0\n") && !report.Contains("private phrase"),
        "diagnostic reports bound output, redact secrets and retain validated source identity");

    string discoveryRoot = Path.Combine(fixture, "discovery");
    string MakeCheckout(string relative)
    {
        string path = Path.GetFullPath(Path.Combine(discoveryRoot, relative));
        foreach (string marker in new[] { "GameServer.slnx", "scripts/dev/live.ps1", "src/GameServer.Core/GameServer.Core.csproj", "src/GameServer.Gateway/GameServer.Gateway.csproj" })
        {
            string target = Path.Combine(path, marker);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, marker.EndsWith(".ps1") ? "throw 'Discovery must not execute scripts.'" : "fixture");
        }
        return path;
    }
    string checkoutA = MakeCheckout("renamed fork");
    string checkoutB = MakeCheckout("projects/nested/another name");
    MakeCheckout("Client/must not scan");
    MakeCheckout("data/must not scan");
    MakeCheckout(".git/must not scan");
    MakeCheckout("node_modules/must not scan");
    Directory.CreateDirectory(Path.Combine(discoveryRoot, "lookalike"));
    File.WriteAllText(Path.Combine(discoveryRoot, "lookalike", "GameServer.slnx"), "not LocalExile");
    var discovered = RepositoryDiscovery.Find([new(discoveryRoot, 3), new(discoveryRoot, 2)]);
    Check(discovered.Paths.Length == 2 && discovered.Paths.Contains(checkoutA) && discovered.Paths.Contains(checkoutB),
        "discovery recognizes renamed/nested checkouts, deduplicates roots and skips game/generated/Git directories");
    Check(!RepositoryDiscovery.Find([new(discoveryRoot, 0)]).Paths.Any() && RepositoryDiscovery.Find([new(discoveryRoot, 3)], maximumDirectories: 1).Limited,
        "discovery respects depth and directory budgets");
    bool cancelled = false;
    try { RepositoryDiscovery.Find([new(discoveryRoot, 3)], new CancellationToken(true)); } catch (OperationCanceledException) { cancelled = true; }
    Check(cancelled, "discovery can be cancelled without touching repositories");
    var tip = GitRepositoryProbe.ParseUpstream("ref: refs/heads/default-branch\tHEAD\r\n" + commit + "\tHEAD\r\n");
    Check(tip?.Branch == "default-branch" && tip.Commit == commit && GitRepositoryProbe.ParseUpstream("untrusted incomplete output") is null,
        "upstream lookup resolves the advertised default branch and validates the HEAD hash");
    var noGit = await GitRepositoryProbe.ReadLocalAsync(checkoutB);
    Check(noGit.Commit == "" && noGit.HasTrackedChanges is null, "source folders without Git remain selectable with unknown commit status");

    async Task Git(params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = checkoutA, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string key in start.Environment.Keys.Where(k => k.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray()) start.Environment.Remove(key);
        start.ArgumentList.Add("-c"); start.ArgumentList.Add("core.hooksPath=" + Path.Combine(fixture, "no-hooks"));
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(); await output;
        if (process.ExitCode != 0) throw new Exception("Git fixture: " + await error);
    }
    await Git("init", "--initial-branch=fixture");
    await Git("add", ".");
    await Git("-c", "user.name=Launcher checks", "-c", "user.email=fixture@example.invalid", "-c", "commit.gpgsign=false", "commit", "-m", "Fixture");
    await Git("pack-refs", "--all");
    var localHead = await GitRepositoryProbe.ReadLocalAsync(checkoutA);
    Check(localHead.Commit.Length == 40 && localHead.Branch == "fixture" && localHead.HasTrackedChanges == false,
        "local HEAD lookup reads packed refs and identifies clean tracked files");
    File.AppendAllText(Path.Combine(checkoutA, "GameServer.slnx"), " modified");
    var changedHead = await GitRepositoryProbe.ReadLocalAsync(checkoutA);
    Check(changedHead.Commit == localHead.Commit && changedHead.HasTrackedChanges == true,
        "matching HEAD does not hide modified working files");
    await Git("checkout", "--detach");
    var detachedHead = await GitRepositoryProbe.ReadLocalAsync(checkoutA);
    Check(detachedHead.Commit == localHead.Commit && detachedHead.Branch == "Detached HEAD", "detached HEAD checkouts can be compared accurately");
    string worktreePath = Path.Combine(fixture, "linked-worktree");
    await Git("worktree", "add", "--detach", worktreePath, "HEAD");
    Check((await GitRepositoryProbe.ReadLocalAsync(worktreePath)).Commit == localHead.Commit,
        "local HEAD lookup supports Git worktrees with a .git file");
    var match = new DiscoveredRepository(checkoutA, localHead, true, false);
    var lookup = new RepositoryLookupResult(new("fixture", localHead.Commit, DateTimeOffset.UtcNow), [match], 1, false, null);
    Check(lookup.SingleMatch == match && (lookup with { Repositories = [match, match with { Root = checkoutB }] }).SingleMatch is null &&
        (lookup with { Repositories = [match with { MatchesUpstream = false }] }).SingleMatch is null && (lookup with { Limited = true }).SingleMatch is null,
        "only a single verified upstream HEAD match is eligible for automatic selection");
    var offlineLookup = await RepositoryLocator.FindAsync([new(discoveryRoot, 3)], upstreamReader: _ => Task.FromResult<UpstreamHead?>(null));
    Check(offlineLookup.Upstream is null && offlineLookup.UpstreamError is not null && offlineLookup.Repositories.Length == 2 && offlineLookup.SingleMatch is null,
        "offline lookup retains local choices and does not claim an upstream match");
    var matchedLookup = await RepositoryLocator.FindAsync([new(discoveryRoot, 3)], upstreamReader: _ => Task.FromResult<UpstreamHead?>(lookup.Upstream));
    Check(matchedLookup.SingleMatch?.Root == checkoutA && matchedLookup.Repositories[0].MatchesUpstream && matchedLookup.Repositories[0].Head.HasTrackedChanges == true,
        "lookup ranks verified matches first while preserving the modified-files warning");
    var accountCharacter = new PlayerCharacter("char1", "OriginalHero", 77, "Witch", "Elementalist", "Standard", "", true);
    var accountFixture = new PlayerAccount("account1", "Original", "", "revision", [accountCharacter, accountCharacter with { Id = "char2" }], 4);
    var copiedNames = AccountTools.ProposeCharacterNames(accountFixture, "VeryLongAccountNameForCopies", ["OriginalHero_VeryLongAcc"]);
    Check(copiedNames.Count == 2 && copiedNames.Values.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 2 && copiedNames.Values.All(n => AccountTools.ValidName(n, 23)),
        "account copy proposes distinct valid character names within the upstream length limit");
    Check(AccountTools.ValidName("Test_Account-123") && !AccountTools.ValidName("no spaces") && !AccountTools.ValidName("ab") && !AccountTools.ValidName(new string('a', 33)),
        "new account names follow the launcher's supported registration rules");
    Check(accountCharacter.DisplayClass == "Elementalist" && (accountCharacter with { Ascendancy = "" }).DisplayClass == L.Text("Witch"),
        "character rows prefer ascendancy and otherwise show the base class");
    var accountRoundTrip = JsonSerializer.Deserialize<BridgeResult>(JsonSerializer.Serialize(new BridgeResult(true, "ok", null, AccountData: new(true, true, "", [accountFixture]))), SettingsStore.JsonOptions);
    Check(accountRoundTrip?.AccountData?.Accounts.Single().Characters[0].Level == 77, "account data has a typed bridge result independent of emulator assemblies");
    Console.WriteLine($"{passed} checks passed.");
    return 0;
}
finally
{
    // Fixture is created above with a fresh GUID; never accept an external cleanup target.
    if (Directory.Exists(fixture))
    {
        if (!Path.GetFullPath(fixture).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) throw new IOException("Fixture cleanup escaped the temporary directory.");
        // Git marks object files read-only on Windows, including this test's private packed refs/objects.
        foreach (string file in Directory.EnumerateFiles(fixture, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(fixture, true);
    }
}
