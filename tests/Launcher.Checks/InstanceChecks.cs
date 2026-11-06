using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Text.Json;
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
    static async Task WritePacket<T>(Stream stream, T value)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value), header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await stream.WriteAsync(header); await stream.WriteAsync(bytes); await stream.FlushAsync();
    }
    static async Task<T> ReadPacket<T>(Stream stream)
    {
        byte[] header = new byte[4]; await stream.ReadExactlyAsync(header);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > 16384) throw new InvalidDataException("Unexpected fixture packet.");
        byte[] bytes = new byte[length]; await stream.ReadExactlyAsync(bytes);
        return JsonSerializer.Deserialize<T>(bytes)!;
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
            bool secondaryRejected = false;
            try { second.StartListening((r, _) => Task.FromResult(AppActivationPolicy.Evaluate(current, r, "", false, false))); }
            catch (InvalidOperationException) { secondaryRejected = true; }
            check(secondaryRejected, "A secondary copy cannot publish an activation listener.");
            first.StartListening((r, _) => Task.FromResult(AppActivationPolicy.Evaluate(current, r, @"C:\Fixture", false, false)));
            bool duplicateRejected = false;
            try { first.StartListening((r, _) => Task.FromResult(AppActivationPolicy.Evaluate(current, r, "", false, false))); }
            catch (InvalidOperationException) { duplicateRejected = true; }
            check(duplicateRejected, "The primary copy cannot start a second activation listener.");
            var reply = await second.ActivateAsync(request, timeout: TimeSpan.FromSeconds(4));
            check(reply.Status == AppActivationStatus.Focused && reply.Running == current, "An authenticated real pipe round trip identifies the owner and requests focus.");
            using (var legacy = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
            {
                await legacy.ConnectAsync(2000);
                var greeting = await ReadPacket<JsonElement>(legacy);
                await WritePacket(legacy, request);
                var legacyReply = await ReadPacket<AppActivationReply>(legacy);
                check(greeting.GetProperty("Protocol").GetInt32() == 1 && legacyReply.Running == current,
                    "An older client can still activate the stable discovery pipe directly.");
            }
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

        string legacyName = Namespace();
        using (var server = new NamedPipeServerStream(legacyName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
        using (var client = new AppInstance(current, legacyName))
        {
            var serve = Task.Run(async () => {
                await server.WaitForConnectionAsync();
                await WritePacket(server, new { Protocol = 1, Identity = current });
                _ = await ReadPacket<AppLaunchRequest>(server);
                await WritePacket(server, new AppActivationReply(AppActivationStatus.Focused, current));
            });
            check((await client.ActivateAsync(request)).Running == current, "A new client authenticates an older owner without a private endpoint.");
            await serve.WaitAsync(TimeSpan.FromSeconds(5));
        }
        string forgedName = Namespace();
        var forgedIdentity = current with { ProcessId = int.MaxValue };
        string forgedEndpoint = forgedName + "." + forgedIdentity.ProcessId + "." + Guid.NewGuid().ToString("N");
        using (var broker = new NamedPipeServerStream(forgedName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
        using (var endpoint = new NamedPipeServerStream(forgedEndpoint, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
        using (var client = new AppInstance(current, forgedName))
        {
            var serve = Task.Run(async () => {
                await broker.WaitForConnectionAsync();
                var greeting = new { Protocol = 1, Identity = forgedIdentity, Endpoint = forgedEndpoint };
                await WritePacket(broker, greeting);
                await endpoint.WaitForConnectionAsync();
                await WritePacket(endpoint, greeting);
                return await endpoint.ReadAsync(new byte[1]);
            });
            bool rejected = false, foregroundAllowed = false;
            try { await client.ActivateAsync(request, _ => foregroundAllowed = true); }
            catch (InvalidDataException) { rejected = true; }
            check(rejected && !foregroundAllowed && await serve.WaitAsync(TimeSpan.FromSeconds(5)) == 0,
                "A redirected endpoint with a forged server PID receives neither activation data nor foreground permission.");
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
            // Keep a real client handle open across the crash. Windows retains that pipe instance until the client
            // closes too, even after the server process and its mutex owner have gone.
            using var interrupted = new NamedPipeClientStream(".", childName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await interrupted.ConnectAsync(2000);
            host.Kill(); await host.WaitForExitAsync(); // Only the Process object created above; simulate a fixture-host crash.
            using var recovered = new AppInstance(current, childName);
            check(recovered.IsPrimary, "A crashed owning process leaves no persistent single-instance lock.");
            recovered.StartListening((r, _) => Task.FromResult(AppActivationPolicy.Evaluate(current, r, "", false, false)));
            check(!interrupted.SafePipeHandle.IsClosed && (await client.ActivateAsync(request)).Running.ProcessId == current.ProcessId,
                "Crash recovery rebinds while the previous client's pipe handle is still open.");
            interrupted.Dispose();
            check((await client.ActivateAsync(request)).Running.ProcessId == current.ProcessId,
                "The replacement listener keeps working after the interrupted client closes.");
        }
        finally
        {
            if (!host.HasExited) { await host.StandardInput.WriteLineAsync("exit"); await host.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
        }
    }
}
