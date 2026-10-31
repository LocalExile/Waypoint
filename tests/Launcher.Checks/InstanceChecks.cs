using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.Versioning;
using Launcher.Core;

[SupportedOSPlatform("windows")]
static class InstanceChecks
{
    static AppInstanceIdentity Identity(string version = "0.6.2", string build = "fixture") => new(Environment.ProcessId, version, build, Environment.ProcessPath!);
    static string Namespace() => "Waypoint.Instance.Checks." + Guid.NewGuid().ToString("N");
    public static void Host(string name)
    {
        using var instance = new AppInstance(Identity(), name);
        if (!instance.IsPrimary) throw new Exception("The disposable fixture could not own its private instance namespace.");
        instance.StartListening((request, _) => Task.FromResult(AppActivationPolicy.Evaluate(instance.Identity, request, @"C:\Fixture checkout", false, false)));
        Console.WriteLine("READY " + Environment.ProcessId); Console.Out.Flush();
        _ = Console.ReadLine(); // EOF or an explicit line closes this disposable host normally.
    }
    static Process StartHost(string name)
    {
        var info = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add(typeof(InstanceChecks).Assembly.Location); info.ArgumentList.Add("--instance-host"); info.ArgumentList.Add(name);
        return Process.Start(info)!;
    }
    public static async Task RunAsync(Action<bool, string> check)
    {
        var current = Identity();
        var request = new AppLaunchRequest(current);
        check(AppStartupOptions.Parse(["--repo", @".\checkouts\..\LocalExile", "--allow-multiple"], @"C:\Fixture") == new AppStartupOptions(@"C:\Fixture\LocalExile", true), "Startup normalizes a relative checkout and requires an explicit multiple-instance override.");
        foreach (string[] args in new[] { new[] { "--repo" }, new[] { "--repo", "--allow-multiple" }, new[] { "--repo", "a", "--repo", "b" } })
        {
            bool rejected = false; try { AppStartupOptions.Parse(args, @"C:\Fixture"); } catch (ArgumentException) { rejected = true; }
            check(rejected, "Invalid or repeated checkout arguments fail before creating a workspace.");
        }
        check(AppActivationPolicy.Evaluate(current, request, @"C:\Fixture", false, false).Status == AppActivationStatus.Focused, "A normal repeated launch only requests focus.");
        check(AppActivationPolicy.Evaluate(current, request with { RepositoryPath = @"c:\fixture\" }, @"C:\Fixture", false, false).Status == AppActivationStatus.Focused, "Equivalent checkout casing and trailing separators do not create a conflict.");
        check(AppActivationPolicy.Evaluate(current, request with { RepositoryPath = @"C:\Different" }, @"C:\Fixture", true, false) is { Status: AppActivationStatus.DifferentCheckout, Busy: true, RepositoryPath: @"C:\Fixture" }, "A different requested checkout preserves the current checkout and reports an active operation.");
        check(AppActivationPolicy.Evaluate(current, request with { Sender = current with { Build = "other" } }, "", false, false).Status == AppActivationStatus.DifferentBuild, "Different builds of the same version are distinguished.");
        check(AppActivationPolicy.Evaluate(current, request with { Sender = current with { Version = "0.6.1" } }, "", false, false).Status == AppActivationStatus.DifferentBuild, "A different version requires an explicit conflict notice.");
        check(AppActivationPolicy.Evaluate(current, request, "", true, false) is { Status: AppActivationStatus.Focused, Busy: true }, "Bringing back a busy window does not interrupt its operation.");
        check(AppActivationPolicy.Evaluate(current, request, "", false, true).Status == AppActivationStatus.Closing, "A closing workspace is not reused for another action.");

        string name = Namespace();
        using (var first = new AppInstance(current, name))
        {
            check(first.IsPrimary, "The first instance owns a disposable namespace.");
            using var second = new AppInstance(current with { ExecutablePath = @"D:\Other folder\Renamed.exe" }, name);
            check(!second.IsPrimary, "Install folder and executable filename cannot create another default owner.");
            first.StartListening((r, _) => Task.FromResult(AppActivationPolicy.Evaluate(current, r, @"C:\Fixture", false, false)));
            var reply = await second.ActivateAsync(request, timeout: TimeSpan.FromSeconds(4));
            check(reply.Status == AppActivationStatus.Focused && reply.Running == current, "An authenticated real pipe round trip identifies the owner and requests focus.");
            var conflict = await second.ActivateAsync(request with { RepositoryPath = @"C:\Different" });
            check(conflict.Status == AppActivationStatus.DifferentCheckout && conflict.RepositoryPath == @"C:\Fixture", "Checkout conflict handling travels through the same typed activation channel.");
            // A malformed client must not stop the listener or allocate its claimed large payload.
            using (var bad = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
            {
                await bad.ConnectAsync(2000);
                byte[] header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, int.MaxValue);
                await bad.WriteAsync(header);
            }
            check((await second.ActivateAsync(request)).Status == AppActivationStatus.Focused, "A malformed oversized packet does not terminate the activation listener.");
            bool relativeRejected = false;
            try { await second.ActivateAsync(request with { RepositoryPath = "relative" }); } catch (InvalidDataException) { relativeRejected = true; }
            check(relativeRejected, "Activation never forwards an unresolved relative path.");
        }
        using (var replacement = new AppInstance(current, name)) check(replacement.IsPrimary, "Normal disposal immediately permits a new owner without a stale marker file.");

        using (var owner = new AppInstance(current, name))
        using (var waiting = new AppInstance(current, name))
        {
            var pending = waiting.ActivateAsync(request);
            await Task.Delay(120);
            int attempts = 0;
            owner.StartListening((r, _) => Task.FromResult(++attempts < 3 ? new AppActivationReply(AppActivationStatus.Starting, current)
                : AppActivationPolicy.Evaluate(current, r, "", false, false)));
            check((await pending).Status == AppActivationStatus.Focused && attempts == 3,
                "A second launch waits for the endpoint and first window rather than creating a duplicate during startup.");
            using var stalled = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await stalled.ConnectAsync(2000);
            check((await waiting.ActivateAsync(request, timeout: TimeSpan.FromSeconds(7))).Status == AppActivationStatus.Focused,
                "A client that never sends a request times out without permanently blocking later launches.");
        }
        using (var owner = new AppInstance(current, name))
        using (var waiting = new AppInstance(current, name))
        {
            bool timedOut = false;
            try { await waiting.ActivateAsync(request, timeout: TimeSpan.FromMilliseconds(200)); } catch (OperationCanceledException) { timedOut = true; }
            using var third = new AppInstance(current, name);
            check(timedOut && !third.IsPrimary, "An unreachable owner does not grant another process permission to create a workspace.");
        }

        string race = Namespace();
        var claimants = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => new AppInstance(current, race))));
        try { check(claimants.Count(c => c.IsPrimary) == 1, "Eight simultaneous claims elect exactly one owner."); }
        finally { foreach (var claimant in claimants) claimant.Dispose(); }

        string childName = Namespace();
        using var host = StartHost(childName);
        try
        {
            string? ready = await host.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(8));
            check(ready == "READY " + host.Id, "A separate disposable process publishes its private activation endpoint.");
            using var client = new AppInstance(current, childName);
            check(!client.IsPrimary && (await client.ActivateAsync(request)).Running.ProcessId == host.Id, "A separate process discovers the same owner through the Windows user/session pipe.");
            host.Kill(); await host.WaitForExitAsync(); // Only the Process object created above; simulate a fixture-host crash.
            using var recovered = new AppInstance(current, childName);
            check(recovered.IsPrimary, "A crashed owning process leaves no persistent single-instance lock.");
            recovered.StartListening((r, _) => Task.FromResult(AppActivationPolicy.Evaluate(current, r, "", false, false)));
            check((await client.ActivateAsync(request)).Running.ProcessId == current.ProcessId, "The same endpoint works again after crash recovery.");
        }
        finally
        {
            if (!host.HasExited) { await host.StandardInput.WriteLineAsync("exit"); await host.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
        }
    }
}
