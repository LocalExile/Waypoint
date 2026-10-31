using Launcher.Core;

static class ServiceExitChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var empty = new RuntimeSnapshot("fixture", "fixture", "", "", "", "", [], [], []);
        var server = new ServiceStatus("Gateway", "Ready", 12, "", "", [], 0, 0, "");
        var running = empty with { Services = [server] };
        var game = running with { Services = [server, new("Game", "Running", 13, "", "", [], 0, 0, "")] };
        async Task<(ServiceExitResult Result, string Calls, int Prompts)> Run(BridgeResult[] results, bool leave = false)
        {
            var calls = new List<string>(); int prompts = 0;
            var prepared = await ServiceExitWorkflow.PrepareAsync(action =>
            {
                calls.Add(action);
                if (calls.Count > results.Length) throw new Exception("Unexpected runtime action while preparing exit.");
                return Task.FromResult(results[calls.Count - 1]);
            }, () => { prompts++; return Task.FromResult(leave); });
            return (prepared, string.Join(",", calls), prompts);
        }
        var noServices = await Run([new(true, "ok", empty)]);
        check(noServices.Result.ShouldExit && noServices.Calls == "Inspect" && noServices.Prompts == 0, "exit with no owned services needs no stop command or game prompt");
        var failedInspect = await Run([new(false, "inspection failed", null)]);
        check(!failedInspect.Result.ShouldExit && !failedInspect.Result.Operation.Ok && failedInspect.Calls == "Inspect", "failed inspection keeps the launcher open and never attempts to stop processes");
        var missing = await Run([new(true, "ok", null)]);
        check(!missing.Result.ShouldExit && !missing.Result.Operation.Ok, "missing runtime inventory cannot be treated as proof that services stopped");
        var cancelled = await Run([new(true, "ok", game)]);
        check(!cancelled.Result.ShouldExit && cancelled.Result.Operation.Ok && cancelled.Calls == "Inspect" && cancelled.Prompts == 1, "cancelling while the game runs does not send a stop or game-close command");
        var leave = await Run([new(true, "ok", game)], true);
        check(leave.Result.ShouldExit && leave.Calls == "Inspect" && leave.Prompts == 1, "explicit leave-running choice permits exit without touching the game or services");
        var stopped = await Run([new(true, "ok", running), new(true, "stopped", empty), new(true, "ok", empty)]);
        check(stopped.Result.ShouldExit && stopped.Calls == "Inspect,Stop,Inspect" && stopped.Prompts == 0, "graceful stop is preceded and followed by fresh service inventory");
        var failedStop = await Run([new(true, "ok", running), new(false, "could not signal", running)]);
        check(!failedStop.Result.ShouldExit && !failedStop.Result.Operation.Ok && failedStop.Calls == "Inspect,Stop", "graceful stop failure stays open without retrying or force termination");
        var failedVerify = await Run([new(true, "ok", running), new(true, "ok", empty), new(false, "check failed", null)]);
        check(!failedVerify.Result.ShouldExit && !failedVerify.Result.Operation.Ok, "failed post-stop verification blocks exit");
        var remainingWatcher = empty with { Services = [server with { ProcessId = 0, WatchProcessId = 99 }] };
        var remaining = await Run([new(true, "ok", running), new(true, "ok", empty), new(true, "ok", remainingWatcher)]);
        check(!remaining.Result.ShouldExit && !remaining.Result.Operation.Ok, "a remaining Gateway watcher blocks exit even without a current child process");
        var appeared = await Run([new(true, "ok", running), new(true, "ok", empty), new(true, "ok", game)]);
        check(!appeared.Result.ShouldExit && !appeared.Result.Operation.Ok && appeared.Calls == "Inspect,Stop,Inspect", "a newly observed game or server blocks completion without a forceful cleanup");
    }
}
