namespace Launcher.Core;

public sealed record ServiceExitResult(bool ShouldExit, BridgeResult Operation);

/// <summary>Fresh inventory, the existing graceful stop, then verification. Never stops the game.</summary>
public static class ServiceExitWorkflow
{
    public static async Task<ServiceExitResult> PrepareAsync(Func<string, Task<BridgeResult>> execute, Func<Task<bool>> leaveRunning)
    {
        var inspected = await execute("Inspect");
        if (!inspected.Ok || inspected.Snapshot is null) return Failed(inspected, "Could not verify services before exiting.");
        if (inspected.Snapshot.GameRunning)
        {
            bool leave = await leaveRunning();
            return new(leave, inspected with { Message = leave ? "Exiting without stopping the game or services." : "Exit cancelled. Close the game before stopping services." });
        }
        if (!inspected.Snapshot.HasServers) return new(true, inspected);
        var stopped = await execute("Stop");
        if (!stopped.Ok) return new(false, stopped);
        var verified = await execute("Inspect");
        if (!verified.Ok || verified.Snapshot is null) return Failed(verified, "Could not verify that services stopped. Waypoint will stay open.");
        if (verified.Snapshot.HasServers || verified.Snapshot.GameRunning)
            return new(false, verified with { Ok = false, Message = "The game or services are still running. Waypoint will stay open. No force termination was attempted." });
        return new(true, verified with { Message = "Services stopped. Exiting Waypoint." });
    }

    static ServiceExitResult Failed(BridgeResult result, string message) => new(false,
        result with { Ok = false, Message = result.Ok ? message : result.Message });
}
