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
        await SequenceAsync(check);
        await CloneAsync(root, check);
        check(Prerequisites.HasSdk("8.0.427 [C:\\sdk]\n10.0.403 [C:\\sdk]\n") && !Prerequisites.HasSdk("9.0.320 [C:\\sdk]\n"), "only a .NET 10 SDK counts");
        check(Prerequisites.PythonIsNewEnough("Python 3.12.4") && !Prerequisites.PythonIsNewEnough("Python 3.8.10") &&
            !Prerequisites.PythonIsNewEnough("Python was not found; run without arguments to install from the Microsoft Store"),
            "Python 3.9+ is required, and the Store's alias is not a Python");
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

    static void Classify(string root, Action<bool, string> check)
    {
        string standalone = Client(root, "standalone", ("Content.ggpk", 10), ("PathOfExile.exe", 10));
        string ready = Client(root, "ready", ("Content.ggpk", 10), ("Game.exe", 10), ("Game.patched.exe", 10));
        string steam = Client(root, "steam", ("PathOfExileSteam.exe", 10), ("Bundles2/_.index.bin", 10));
        string empty = Client(root, "empty", ("readme.txt", 10));
        check(ClientLocator.Classify(standalone) == ClientKind.Standalone && ClientLocator.Classify(ready) == ClientKind.Ready, "a standalone client and a prepared one are told apart");
        check(ClientLocator.Classify(steam) == ClientKind.Unsupported && ClientLocator.Describe(steam) is { Usable: false }, "a Steam layout is found, and reported as unusable");
        check(ClientLocator.Classify(empty) is null && ClientLocator.Describe(empty) is null, "a folder without a client is not one");
        check(ClientLocator.IsKnownBuild(standalone) == false && ClientLocator.IsKnownBuild(empty) is null, "an executable of another size is not the 3.29.3b build");
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
