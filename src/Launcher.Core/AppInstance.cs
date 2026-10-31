using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace Launcher.Core;

public sealed record AppInstanceIdentity(int ProcessId, string Version, string Build, string ExecutablePath);
public sealed record AppLaunchRequest(AppInstanceIdentity Sender, string? RepositoryPath = null);
public enum AppActivationStatus { Focused, DifferentBuild, DifferentCheckout, Starting, Closing }
public sealed record AppActivationReply(AppActivationStatus Status, AppInstanceIdentity Running, string RepositoryPath = "", bool Busy = false);
public sealed record AppStartupOptions(string? RepositoryPath, bool AllowMultiple)
{
    public static AppStartupOptions Parse(string[] arguments, string workingDirectory)
    {
        string? repository = null; bool multiple = false;
        for (int i = 0; i < arguments.Length; i++)
        {
            if (arguments[i] == "--allow-multiple") multiple = true;
            else if (arguments[i] == "--repo")
            {
                if (repository is not null || ++i == arguments.Length || arguments[i].StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException("Supply one folder after --repo.");
                repository = NormalizeRepository(arguments[i], workingDirectory);
            }
        }
        return new(repository, multiple);
    }
    public static string NormalizeRepository(string path, string? workingDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 4096) throw new ArgumentException("The requested checkout path is empty or too long.");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path, workingDirectory ?? Environment.CurrentDirectory));
    }
    public static bool SameRepository(string left, string right)
    {
        if (left.Length == 0 || right.Length == 0) return left == right;
        try { return string.Equals(NormalizeRepository(left), NormalizeRepository(right), StringComparison.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }
}

public static class AppActivationPolicy
{
    public static AppActivationReply Evaluate(AppInstanceIdentity running, AppLaunchRequest request, string repository, bool busy, bool closing)
    {
        var status = closing ? AppActivationStatus.Closing
            : request.Sender.Version != running.Version || request.Sender.Build != running.Build ? AppActivationStatus.DifferentBuild
            : request.RepositoryPath is { } selected && !AppStartupOptions.SameRepository(selected, repository) ? AppActivationStatus.DifferentCheckout
            : AppActivationStatus.Focused;
        // Activation never changes settings, selection, services, or an in-flight operation.
        return new(status, running, repository, busy);
    }
}

/// <summary>Cooperative, same-user/session desktop ownership. Independent of product name and install folder.</summary>
[SupportedOSPlatform("windows")]
public sealed class AppInstance : IDisposable
{
    const int Protocol = 1, MaximumPacket = 16384;
    readonly string name;
    readonly InstanceLock ownership;
    readonly CancellationTokenSource lifetime = new();
    readonly object pipeGate = new();
    NamedPipeServerStream? currentPipe;
    bool disposed;
    public AppInstanceIdentity Identity { get; }
    public bool IsPrimary => ownership.IsPrimary;
    public static string DefaultName
    {
        get
        {
            using var user = WindowsIdentity.GetCurrent(); using var process = Process.GetCurrentProcess();
            return "EmulatorWorkspaceLauncher.Desktop.v1." +
                Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(user.User!.Value)))[..20] + "." + process.SessionId;
        }
    }

    public AppInstance(AppInstanceIdentity identity, string? instanceName = null)
    {
        Identity = identity; name = instanceName ?? DefaultName;
        if (name.Length > 120 || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '.' and not '-'))
            throw new ArgumentException("Unsupported app-instance identifier.", nameof(instanceName));
        ownership = new("Local\\" + name);
    }

    public void StartListening(Func<AppLaunchRequest, CancellationToken, Task<AppActivationReply>> activate)
    {
        if (!IsPrimary) throw new InvalidOperationException("Only the owning instance can listen.");
        lock (pipeGate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (currentPipe is not null) throw new InvalidOperationException("The activation listener is already running.");
            currentPipe = CreatePipe(); // Publish before constructing the workspace; second launches may arrive immediately.
        }
        _ = ListenAsync(activate);
    }
    NamedPipeServerStream CreatePipe() => new(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, MaximumPacket, MaximumPacket);
    async Task ListenAsync(Func<AppLaunchRequest, CancellationToken, Task<AppActivationReply>> activate)
    {
        while (!lifetime.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            lock (pipeGate)
            {
                if (disposed) return;
                pipe = currentPipe ??= CreatePipe();
            }
            try
            {
                await pipe.WaitForConnectionAsync(lifetime.Token).ConfigureAwait(false);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(3));
                await WriteAsync(pipe, new Greeting(Protocol, Identity), timeout.Token).ConfigureAwait(false);
                var request = await ReadAsync<AppLaunchRequest>(pipe, timeout.Token).ConfigureAwait(false);
                Validate(request);
                if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint client) || request.Sender.ProcessId != client)
                    throw new InvalidDataException("Activation sender does not match its pipe connection.");
                var reply = await activate(request, timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false);
                await WriteAsync(pipe, reply, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException or ObjectDisposedException or JsonException or ArgumentException or InvalidOperationException)
            { /* A timed-out, truncated or unsupported client cannot terminate the workspace listener. */ }
            finally
            {
                lock (pipeGate) { pipe.Dispose(); if (ReferenceEquals(currentPipe, pipe)) currentPipe = null; }
            }
        }
    }

    public async Task<AppActivationReply> ActivateAsync(AppLaunchRequest request, Action<int>? allowForeground = null, TimeSpan? timeout = null)
    {
        Validate(request);
        using var deadline = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(5));
        while (true)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.ConnectAsync(deadline.Token).ConfigureAwait(false);
                var greeting = await ReadAsync<Greeting>(pipe, deadline.Token).ConfigureAwait(false);
                if (greeting.Protocol != Protocol || !GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint server) ||
                    greeting.Identity is null || greeting.Identity.ProcessId != server)
                    throw new InvalidDataException("The running app uses an unsupported activation protocol.");
                allowForeground?.Invoke(greeting.Identity.ProcessId);
                await WriteAsync(pipe, request, deadline.Token).ConfigureAwait(false);
                var reply = await ReadAsync<AppActivationReply>(pipe, deadline.Token).ConfigureAwait(false);
                if (reply.Running != greeting.Identity || !Enum.IsDefined(reply.Status)) throw new InvalidDataException("The activation reply does not identify the running app.");
                if (reply.Status != AppActivationStatus.Starting) return reply;
            }
            catch (IOException) when (!deadline.IsCancellationRequested) { }
            await Task.Delay(100, deadline.Token).ConfigureAwait(false);
        }
    }
    static void Validate(AppLaunchRequest request)
    {
        if (request.Sender is null || request.Sender.ProcessId <= 0 || request.Sender.Version is not { Length: > 0 and <= 128 } ||
            request.Sender.Build is not { Length: > 0 and <= 512 } || request.Sender.ExecutablePath is not { Length: > 0 and <= 4096 } ||
            request.RepositoryPath is { Length: > 4096 }) throw new InvalidDataException("Invalid app activation request.");
        if (request.RepositoryPath is { } repo && !Path.IsPathFullyQualified(repo)) throw new InvalidDataException("Activation requires a full checkout path.");
    }
    static async Task<T> ReadAsync<T>(Stream stream, CancellationToken token)
    {
        byte[] header = new byte[4]; await stream.ReadExactlyAsync(header, token).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > MaximumPacket) throw new InvalidDataException("Invalid activation packet length.");
        byte[] bytes = new byte[length]; await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(bytes) ?? throw new InvalidDataException("Empty activation packet.");
    }
    static async Task WriteAsync<T>(Stream stream, T value, CancellationToken token)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        if (bytes.Length > MaximumPacket) throw new InvalidDataException("Activation packet is too large.");
        byte[] header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await stream.WriteAsync(header, token).ConfigureAwait(false); await stream.WriteAsync(bytes, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }
    public void Dispose()
    {
        lock (pipeGate)
        {
            if (disposed) return;
            disposed = true; lifetime.Cancel(); currentPipe?.Dispose(); currentPipe = null;
        }
        // Close the endpoint before releasing ownership, so a replacement can bind immediately.
        ownership.Dispose();
    }
    sealed record Greeting(int Protocol, AppInstanceIdentity Identity);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);

    // Mutex ownership is thread-affine. A dedicated lifetime thread makes async startup/disposal safe.
    sealed class InstanceLock : IDisposable
    {
        readonly ManualResetEventSlim release = new(false);
        readonly Thread thread;
        public bool IsPrimary { get; }
        public InstanceLock(string name)
        {
            var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            thread = new(() =>
            {
                try
                {
                    using var mutex = new Mutex(false, name);
                    bool acquired; try { acquired = mutex.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
                    ready.SetResult(acquired);
                    if (acquired) { release.Wait(); mutex.ReleaseMutex(); }
                }
                catch (Exception ex) { ready.TrySetException(ex); }
            }) { IsBackground = true, Name = "Waypoint instance ownership" };
            thread.Start();
            IsPrimary = ready.Task.GetAwaiter().GetResult();
        }
        public void Dispose() { release.Set(); thread.Join(); release.Dispose(); }
    }
}
