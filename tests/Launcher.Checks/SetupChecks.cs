using System.Diagnostics;
using Launcher.Core;

/// <summary>The setup guide's engine on throwaway folders: finding a client, a copy that stops and goes on, and a run of
/// steps that stops and is run again.</summary>
static class SetupChecks
{
    public static async Task RunAsync(string fixture, Action<bool, string> check)
    {
        string root = Path.Combine(fixture, "setup");
        Directory.CreateDirectory(root);
        Classify(root, check);
        await CopyAsync(root, check);
        Keys(root, check);
        await LinksAsync(root, check);
        await SequenceAsync(check);
        await CloneAsync(root, check);
        ExtractScope(root, check);
        check(Prerequisites.HasSdk("8.0.427 [C:\\sdk]\n10.0.403 [C:\\sdk]\n") && !Prerequisites.HasSdk("9.0.320 [C:\\sdk]\n"), "only a .NET 10 SDK counts");
        check(PythonLookup.VersionOf("Python 3.12.4") == new Version(3, 12, 4) && PythonLookup.VersionOf("Python 3.9.0b1") == new Version(3, 9, 0)
            && PythonLookup.VersionOf("Python was not found; run without arguments to install from the Microsoft Store") is null,
            "the version is read from python --version, and the Store's shortcut is not a Python");
        check(PythonLookup.NewEnough(new(3, 9, 0)) && PythonLookup.NewEnough(new(4, 0, 0)) && !PythonLookup.NewEnough(new(3, 8, 10)) && !PythonLookup.NewEnough(new(2, 7, 18)),
            "Python 3.9+ is required");
        check(Prerequisites.MergedPath(@"C:\Apps;C:\Users\p\AppData\Local\Microsoft\WindowsApps", [@"C:\Apps", @"C:\Python39", @"c:\apps", @"C:\Python39\Scripts"])
            == @"C:\Python39;C:\Python39\Scripts;C:\Apps;C:\Users\p\AppData\Local\Microsoft\WindowsApps" && Prerequisites.MergedPath(@"C:\Apps", [@"c:\apps"]) == @"C:\Apps",
            "a PATH entry an installer added goes in front of the Store's python shortcut, once");
        check(PythonLookup.AdoptedPath(@"C:\Apps;C:\Python39\;C:\WindowsApps", @"C:\Python39") == @"C:\Python39;C:\Python39\Scripts;C:\Apps;C:\WindowsApps",
            "an adopted Python's folder and its Scripts go first on PATH, once");
        var elsewhere = new PythonSearch(new(@"C:\Python39\python.exe", new(3, 9, 13), OnPath: false), null, StoreAlias: true);
        check(PythonLookup.Describe(elsewhere).Contains("3.9.13") && PythonLookup.Describe(elsewhere).Contains(@"C:\Python39") && !PythonLookup.Describe(elsewhere).Contains("Store")
            && PythonLookup.Describe(new(new(@"C:\Python312\python.exe", new(3, 12, 4), OnPath: true), new(3, 12, 4), false)).Contains(@"C:\Python312"),
            "the requirement names the Python found and its folder");
        check(PythonLookup.Describe(new(null, new(3, 8, 10), false)).Contains("3.8.10") && PythonLookup.Describe(new(null, null, true)) != PythonLookup.Describe(new(null, null, false)),
            "too old says which version, and the Store's shortcut is told apart from no Python");
        var search = await PythonLookup.FindAsync();
        var onPath = await ToolProcess.AskAsync(Platform.Python, "--version");
        check(onPath is not { Code: 0 } answer || PythonLookup.VersionOf(answer.Output) is not { } version || !PythonLookup.NewEnough(version) || search.Usable is { OnPath: true },
            "python on PATH is the Python when it is new enough");
    }

    static string Client(string root, string name, params (string File, int Bytes)[] files)
    {
        string folder = Path.Combine(root, name);
        foreach (var (file, bytes) in files)
        {
            string path = Path.Combine(folder, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var content = new byte[bytes];
            new Random(bytes).NextBytes(content);
            File.WriteAllBytes(path, content);
        }
        return folder;
    }

    /// <summary>Setup extracts only what the servers read when the checkout's extract-data.ps1 takes -Servers.</summary>
    static void ExtractScope(string root, Action<bool, string> check)
    {
        string Checkout(string name, string param)
        {
            string folder = Path.Combine(root, name);
            Directory.CreateDirectory(Path.Combine(folder, "scripts", "dev"));
            File.WriteAllText(Path.Combine(folder, "scripts", "dev", "extract-data.ps1"), $"# extracts game data\r\nparam({param})\r\n");
            return folder;
        }
        check(SetupSequence.ExtractsForServers(Checkout("split", "[switch]$Force, [string[]]$Only = @(), [switch]$List, [string]$ExportManifest, [switch]$Servers")),
            "a checkout whose extract-data.ps1 takes -Servers extracts only what the servers read");
        check(!SetupSequence.ExtractsForServers(Checkout("before-split", "[switch]$Force, [string[]]$Only = @(), [switch]$List, [string]$ServersList")),
            "a checkout from before the split, or a longer name that starts with Servers, extracts everything");
        check(!SetupSequence.ExtractsForServers(Path.Combine(root, "no-checkout")), "no extract-data.ps1 extracts everything");
    }

    static void Classify(string root, Action<bool, string> check)
    {
        string standalone = Client(root, "standalone", ("Content.ggpk", 10), ("PathOfExile.exe", 10));
        string ready = Client(root, "ready", ("Content.ggpk", 10), ("Game.exe", 10), ("Game.patched.exe", 10));
        string steam = Client(root, "steam", ("PathOfExileSteam.exe", 10), ("Bundles2/_.index.bin", 10));
        string empty = Client(root, "empty", ("readme.txt", 10));
        check(ClientLocator.Classify(standalone) == ClientKind.Standalone && ClientLocator.Classify(ready) == ClientKind.Ready, "a standalone client and a prepared one are told apart");
        check(ClientLocator.Classify(steam) == ClientKind.Unsupported && ClientLocator.Describe(steam) is { Usable: false }, "a Steam layout is found, and reported as unusable");
        check(ClientLocator.Classify(empty) is null && ClientLocator.Describe(empty) is null, "a folder without a client is not one");
        check(ClientLocator.IsKnownBuild(standalone) == false && ClientLocator.IsKnownBuild(empty) is null, "an executable of another size is not the build LocalExile needs");
    }

    static async Task CopyAsync(string root, Action<bool, string> check)
    {
        string source = Client(root, "source", ("Content.ggpk", 6_000_000), ("PathOfExile.exe", 2000), ("Bundles2/a.bin", 3000), ("logs/Client.txt", 500));
        string checkout = Path.Combine(root, "copy-checkout");
        Directory.CreateDirectory(checkout);
        string target = Path.Combine(checkout, "Client");

        var plan = ClientCopy.Plan(source, checkout);
        check(Path.GetFileName(plan.Files[^1].Target) == "Content.ggpk", "Content.ggpk goes last, so having it means having the whole client");
        check(plan.Files.All(f => !f.Source.Contains("logs")), "the client's own logs are not copied");
        check(plan.Remaining == plan.Total && plan.Total == 6_005_000, "the plan counts what is left to copy");

        using (var stopped = new CancellationTokenSource())
        {
            stopped.Cancel();
            bool threw = false;
            try { await ClientCopy.RunAsync(plan, null, stopped.Token); }
            catch (OperationCanceledException) { threw = true; }
            check(threw && !File.Exists(Path.Combine(target, "Content.ggpk")), "a stopped copy leaves no file under its real name");
        }

        // A copy stopped halfway through the big file.
        Directory.CreateDirectory(target);
        byte[] original = File.ReadAllBytes(Path.Combine(source, "Content.ggpk"));
        File.WriteAllBytes(Path.Combine(target, "Content.ggpk.partial"), original[..4_500_000]);
        plan = ClientCopy.Plan(source, checkout);
        check(plan.Remaining == 1_505_000, "a partial file counts as done as far as it goes");
        await ClientCopy.RunAsync(plan, null, CancellationToken.None);
        check(File.ReadAllBytes(Path.Combine(target, "Content.ggpk")).SequenceEqual(original) && !File.Exists(Path.Combine(target, "Content.ggpk.partial")),
            "a copy goes on from its partial file and ends with the same bytes");
        check(File.Exists(Path.Combine(target, "Bundles2", "a.bin")) && ClientLocator.Classify(target) == ClientKind.Standalone, "the whole client arrives, subfolders included");

        // A source that changed since: the partial file does not match it, so it starts over.
        File.Delete(Path.Combine(target, "Content.ggpk"));
        var other = new byte[4_500_000];
        new Random(7).NextBytes(other);
        File.WriteAllBytes(Path.Combine(target, "Content.ggpk.partial"), other);
        await ClientCopy.RunAsync(ClientCopy.Plan(source, checkout), null, CancellationToken.None);
        check(File.ReadAllBytes(Path.Combine(target, "Content.ggpk")).SequenceEqual(original), "a partial file that does not match the source is started over");

        // A prepared client differs from its source: a finished file is left alone.
        File.WriteAllBytes(Path.Combine(target, "Content.ggpk"), [1, 2, 3]);
        await ClientCopy.RunAsync(ClientCopy.Plan(source, checkout), null, CancellationToken.None);
        check(new FileInfo(Path.Combine(target, "Content.ggpk")).Length == 3, "running again never overwrites a client that was prepared");

        var huge = new ClientCopyPlan(target, [], long.MaxValue / 2, long.MaxValue / 2, null);
        bool refused = false;
        try { ClientCopy.RequireSpace(huge); }
        catch (IOException) { refused = true; }
        check(refused, "a copy that does not fit is refused before it starts");
    }

    static void Keys(string root, Action<bool, string> check)
    {
        // A prepared client from another checkout, and that checkout's keys.
        string other = Path.Combine(root, "other");
        Client(other, "Client", ("Content.ggpk", 10), ("Game.exe", 10), ("Game.patched.exe", 10));
        Directory.CreateDirectory(Path.Combine(other, "data"));
        File.WriteAllText(Path.Combine(other, "data", "login-keys.json"), "keys");

        string fresh = Path.Combine(root, "fresh");
        Directory.CreateDirectory(fresh);
        var plan = ClientCopy.Plan(Path.Combine(other, "Client"), fresh);
        check(plan.Keys is not null && plan.Files.Any(f => f.Target.EndsWith("Game.patched.exe", StringComparison.Ordinal)), "a prepared client brings its keys to a checkout without any");

        string keyed = Path.Combine(root, "keyed");
        Directory.CreateDirectory(Path.Combine(keyed, "data"));
        File.WriteAllText(Path.Combine(keyed, "data", "login-keys.json"), "own keys");
        plan = ClientCopy.Plan(Path.Combine(other, "Client"), keyed);
        check(plan.Keys is null && !plan.Files.Any(f => f.Target.EndsWith("Game.patched.exe", StringComparison.Ordinal)), "a checkout's own keys stay, and the executable patched for other keys does not come");
    }

    static void Link(string link, string target)
    {
        if (!OperatingSystem.IsWindows()) { Directory.CreateSymbolicLink(link, target); return; }
        var info = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        foreach (string argument in new[] { "/c", "mklink", "/J", link, target }) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
    }

    static string Keyed(string folder, string publicKey)
    {
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "login-keys.json");
        File.WriteAllText(path, $$"""{"dsaY":"{{publicKey}}"}""");
        return path;
    }

    static async Task LinksAsync(string root, Action<bool, string> check)
    {
        // A prepared client beside its checkout, the public key inside its patched executable, as the server's patch writes it.
        string owner = Path.Combine(root, "link-owner");
        string shared = Client(owner, "Client", ("Content.ggpk", 10), ("Game.exe", 10));
        File.WriteAllBytes(Path.Combine(shared, "Game.patched.exe"), [1, 2, 0xAB, 0xCD, 0xEF, 0x12, 3, 4]);
        string keys = Keyed(Path.Combine(owner, "data"), "00ABCDEF12");
        File.WriteAllText(Path.Combine(owner, "data", "gateway_list.original.txt"), "list");
        check(ClientCopy.KeysFit(shared, keys), "keys are recognized inside the client patched for them");
        check(!ClientCopy.KeysFit(shared, Keyed(Path.Combine(root, "other-keys"), "99887766")), "another checkout's keys are not");
        File.WriteAllText(Path.Combine(root, "other-keys", "broken.json"), "not json");
        check(!ClientCopy.KeysFit(shared, Path.Combine(root, "other-keys", "broken.json")) && !ClientCopy.KeysFit(shared, Path.Combine(root, "missing.json")), "keys that cannot be read are no match");

        string fresh = Path.Combine(root, "link-fresh");
        Directory.CreateDirectory(fresh);
        check(ClientCopy.SharedKeys(shared, fresh, []) == Path.Combine(owner, "data"), "a prepared client can be shared with a checkout that has no keys");
        check(ClientCopy.SharedKeys(Path.Combine(root, "standalone"), fresh, []) is null, "a client not prepared yet is never shared: preparing would change the player's install");
        string keyed = Path.Combine(root, "link-keyed");
        Keyed(Path.Combine(keyed, "data"), "99887766");
        check(ClientCopy.SharedKeys(shared, keyed, []) is null, "a checkout with other keys cannot share a client patched for different ones");

        await ClientCopy.ShareAsync(shared, fresh, Path.Combine(owner, "data"), _ => { }, CancellationToken.None);
        string link = Path.Combine(fresh, "Client");
        check(new DirectoryInfo(link).LinkTarget is not null && ClientLocator.Classify(link) == ClientKind.Ready, "sharing links the checkout to the client");
        check(File.ReadAllText(Path.Combine(fresh, "data", "login-keys.json")) == File.ReadAllText(keys) && File.Exists(Path.Combine(fresh, "data", "gateway_list.original.txt")), "the keys come with a shared client");

        // A checkout already linked to the client vouches for it when the folder is not inside a checkout.
        string outside = Path.Combine(root, "shelf");
        string shelved = Client(outside, "Game", ("Content.ggpk", 10), ("Game.exe", 10));
        File.WriteAllBytes(Path.Combine(shelved, "Game.patched.exe"), [0xAB, 0xCD, 0xEF, 0x12]);
        string linked = Path.Combine(root, "link-linked");
        Directory.CreateDirectory(linked);
        Link(Path.Combine(linked, "Client"), shelved);
        Keyed(Path.Combine(linked, "data"), "ABCDEF12");
        check(ClientCopy.SharedKeys(shelved, fresh + "-second", [linked]) == Path.Combine(linked, "data"), "a client shared by another checkout brings that checkout's keys");

        // The folder a link led to is gone.
        string moved = Path.Combine(root, "moved");
        Directory.CreateDirectory(moved);
        string dead = Path.Combine(root, "link-dead");
        Directory.CreateDirectory(dead);
        Link(Path.Combine(dead, "Client"), moved);
        Directory.Delete(moved);
        string source = Client(root, "dead-source", ("Content.ggpk", 100), ("PathOfExile.exe", 10));
        await ClientCopy.RunAsync(ClientCopy.Plan(source, dead), null, CancellationToken.None);
        check(new DirectoryInfo(Path.Combine(dead, "Client")).LinkTarget is null && ClientLocator.Classify(Path.Combine(dead, "Client")) == ClientKind.Standalone, "a link that leads nowhere gives way to the copy");

        // A link to a folder that holds other things: replacing it must not touch them.
        string kept = Client(root, "kept", ("precious.txt", 10));
        string wrong = Path.Combine(root, "link-wrong");
        Directory.CreateDirectory(wrong);
        Link(Path.Combine(wrong, "Client"), kept);
        await ClientCopy.RunAsync(ClientCopy.Plan(source, wrong), null, CancellationToken.None);
        check(File.Exists(Path.Combine(kept, "precious.txt")) && !File.Exists(Path.Combine(kept, "Content.ggpk")) && ClientLocator.Classify(Path.Combine(wrong, "Client")) == ClientKind.Standalone,
            "replacing a link leaves the folder it pointed at untouched");

        // A copy never writes through a link to a working client.
        bool refused = false;
        try { await ClientCopy.RunAsync(ClientCopy.Plan(source, fresh), null, CancellationToken.None); }
        catch (IOException) { refused = true; }
        check(refused && !File.Exists(Path.Combine(shared, "PathOfExile.exe")), "a copy never writes through a link to a working client");

        string started = Path.Combine(root, "link-started");
        Client(started, "Client", ("Content.ggpk.partial", 10));
        refused = false;
        try { await ClientCopy.ShareAsync(shared, started, Path.Combine(owner, "data"), _ => { }, CancellationToken.None); }
        catch (IOException) { refused = true; }
        check(refused && File.Exists(Path.Combine(started, "Client", "Content.ggpk.partial")), "sharing leaves a copy stopped halfway alone");

        // The fixture's own cleanup does not go through junctions.
        foreach (string made in new[] { link, Path.Combine(linked, "Client") }) Directory.Delete(made);
    }

    static async Task SequenceAsync(Action<bool, string> check)
    {
        var log = new List<string>();
        bool firstDone = false;
        using var stop = new CancellationTokenSource();
        SetupTask[] tasks =
        [
            new("first", () => firstDone, (_, _) => { log.Add("first"); firstDone = true; return Task.CompletedTask; }),
            new("second", null, async (_, token) => { log.Add("second"); stop.Cancel(); await Task.Delay(TimeSpan.FromSeconds(30), token); }),
            new("third", null, (_, _) => { log.Add("third"); return Task.CompletedTask; }),
        ];
        var states = new Dictionary<int, SetupState>();
        var outcome = await SetupSequence.RunAsync(tasks, (i, state, _) => states[i] = state, stop.Token);
        check(outcome == SetupOutcome.Stopped && states[1] == SetupState.Stopped && !states.ContainsKey(2) && !log.Contains("third"), "a stop ends the running step and leaves the rest");

        log.Clear();
        tasks[1] = tasks[1] with { Run = (_, _) => { log.Add("second"); return Task.CompletedTask; } };
        outcome = await SetupSequence.RunAsync(tasks, (i, state, _) => states[i] = state, CancellationToken.None);
        check(outcome == SetupOutcome.Finished && states[0] == SetupState.Skipped && log.SequenceEqual(["second", "third"]), "run again, it skips what is done and goes on");

        tasks[1] = tasks[1] with { Run = (_, _) => throw new IOException("no disk") };
        string reason = "";
        outcome = await SetupSequence.RunAsync(tasks, (i, state, detail) => { if (state == SetupState.Failed) reason = detail; }, CancellationToken.None);
        check(outcome == SetupOutcome.Failed && reason == "no disk", "a step that fails ends the run with its reason");
    }

    static async Task CloneAsync(string root, Action<bool, string> check)
    {
        string origin = Path.Combine(root, "origin");
        Directory.CreateDirectory(origin);
        await Git(origin, "init", "--initial-branch=main");
        File.WriteAllText(Path.Combine(origin, "GameServer.slnx"), "<Solution />");
        await Git(origin, "add", ".");
        await Git(origin, "-c", "user.name=Launcher checks", "-c", "user.email=fixture@example.invalid", "-c", "commit.gpgsign=false", "commit", "-m", "Fixture");

        string target = Path.Combine(root, "clone");
        await SetupSequence.CloneAsync(origin, target, _ => { }, CancellationToken.None);
        check(File.Exists(Path.Combine(target, "GameServer.slnx")), "the download clones into the chosen folder");

        bool refused = false;
        try { await SetupSequence.CloneAsync(origin, target, _ => { }, CancellationToken.None); }
        catch (IOException) { refused = true; }
        check(refused && File.Exists(Path.Combine(target, "GameServer.slnx")), "a folder with anything in it is refused and left alone");

        string stopped = Path.Combine(root, "stopped-clone");
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        bool threw = false;
        try { await SetupSequence.CloneAsync(origin, stopped, _ => { }, stop.Token); }
        catch (OperationCanceledException) { threw = true; }
        check(threw && !Directory.Exists(stopped), "a stopped download leaves no half-made folder");
    }

    static async Task Git(string directory, params string[] arguments)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        await output;
        if (process.ExitCode != 0) throw new Exception("Git fixture: " + await error);
    }
}
