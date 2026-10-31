namespace Launcher.Core;

public sealed record ClientCopyFile(string Source, string Target, long Length);

/// <param name="Keys">The source checkout's data folder when its keys come along with its patched client.</param>
public sealed record ClientCopyPlan(string Target, ClientCopyFile[] Files, long Total, long Remaining, string? Keys);

public sealed record ClientCopyProgress(string File, long Done, long Total);

/// <summary>
/// Copies a client into a checkout's <c>Client</c> folder, never touching the source. It can stop at any moment and go on
/// later: a file is written as <c>name.partial</c> and takes its real name only when complete, so a file with its real name is
/// always whole. (That is also why it is never compared with the source: Content.ggpk changes once the client is prepared.)
/// Content.ggpk goes last, so a checkout that has it has all of the client.
/// </summary>
public static class ClientCopy
{
    const int BufferSize = 4 * 1024 * 1024;
    /// <summary>Room left over on the drive, so a copy does not end with a full disk.</summary>
    const long Margin = 2_000_000_000;

    /// <summary>A patched client only works with the keys it was patched for: they come with it when the checkout has none
    /// yet, and otherwise it stays behind and the client is patched again.</summary>
    public static ClientCopyPlan Plan(string source, string checkout)
    {
        string target = Path.Combine(checkout, "Client");
        string keys = Path.Combine(checkout, "data", "login-keys.json");
        string? sourceData = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(source)) is { } parent ? Path.Combine(parent, "data") : null;
        bool withKeys = ClientLocator.Classify(source) == ClientKind.Ready && !File.Exists(keys)
            && sourceData is not null && File.Exists(Path.Combine(sourceData, "login-keys.json"));
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
        var files = new List<ClientCopyFile>();
        foreach (var file in new DirectoryInfo(root).EnumerateFiles("*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(root, file.FullName);
            // The client's own logs are not part of the game; a leftover .partial is another copy's.
            if (relative.StartsWith("logs" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || relative.EndsWith(".partial", StringComparison.OrdinalIgnoreCase)) continue;
            if (!withKeys && relative.Equals("Game.patched.exe", StringComparison.OrdinalIgnoreCase)) continue;
            files.Add(new(file.FullName, Path.Combine(target, relative), file.Length));
        }
        files = files.OrderBy(f => Path.GetFileName(f.Target).Equals("Content.ggpk", StringComparison.OrdinalIgnoreCase)).ToList();
        long total = files.Sum(f => f.Length), remaining = 0;
        foreach (var file in files)
        {
            if (File.Exists(file.Target)) continue;
            string partial = file.Target + ".partial";
            remaining += file.Length - (File.Exists(partial) ? Math.Min(new FileInfo(partial).Length, file.Length) : 0);
        }
        return new(target, files.ToArray(), total, remaining, withKeys ? sourceData : null);
    }

    /// <summary>Free bytes on the drive of a path, which need not exist yet; null when it cannot be told.</summary>
    public static long? FreeSpace(string path)
    {
        try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).AvailableFreeSpace; }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException) { return null; }
    }

    public static void RequireSpace(ClientCopyPlan plan)
    {
        if (FreeSpace(plan.Target) is { } free && free < plan.Remaining + Margin)
            throw new IOException(L.Text("Not enough free space: the client needs {0:0.#} GB more on this drive, and {1:0.#} GB is free.", plan.Remaining / 1e9, free / 1e9));
    }

    public static async Task RunAsync(ClientCopyPlan plan, Action<ClientCopyProgress>? progress, CancellationToken stop)
    {
        RequireSpace(plan);
        if (new DirectoryInfo(plan.Target) is { Exists: true } folder && (folder.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException(L.Text("Client must be a separate physical copy in this checkout, not a link to another folder."));
        long done = plan.Files.Where(f => File.Exists(f.Target)).Sum(f => f.Length);
        var report = System.Diagnostics.Stopwatch.StartNew();
        foreach (var file in plan.Files)
        {
            if (File.Exists(file.Target)) continue;
            stop.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.GetDirectoryName(file.Target)!);
            string partial = file.Target + ".partial";
            await using (var input = Open(file.Source))
            await using (var output = new FileStream(partial, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await Resume(input, output, stop);
                done += output.Position;
                var buffer = new byte[BufferSize];
                while (await input.ReadAsync(buffer, stop) is var read and > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), stop);
                    done += read;
                    if (report.ElapsedMilliseconds >= 200)
                    {
                        report.Restart();
                        progress?.Invoke(new(Path.GetFileName(file.Target), done, plan.Total));
                    }
                }
            }
            File.SetLastWriteTimeUtc(partial, File.GetLastWriteTimeUtc(file.Source));
            File.Move(partial, file.Target);
        }
        if (plan.Keys is { } data)
        {
            Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(plan.Target)!, "data"));
            foreach (string name in new[] { "login-keys.json", "gateway_list.original.txt" })
                if (File.Exists(Path.Combine(data, name)))
                    File.Copy(Path.Combine(data, name), Path.Combine(Path.GetDirectoryName(plan.Target)!, "data", name), overwrite: false);
        }
        progress?.Invoke(new("", plan.Total, plan.Total));
    }

    static FileStream Open(string source)
    {
        try { return new(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan); }
        catch (IOException e) { throw new IOException(L.Text("{0} could not be read. Close the game and try again.", source), e); }
    }

    /// <summary>Goes on from a partial file when its last block still matches the source; a different source starts it over.</summary>
    static async Task Resume(FileStream input, FileStream output, CancellationToken stop)
    {
        long length = output.Length;
        if (length == 0) return;
        if (length <= input.Length)
        {
            int check = (int)Math.Min(length, BufferSize);
            byte[] written = new byte[check], original = new byte[check];
            output.Position = input.Position = length - check;
            await output.ReadExactlyAsync(written, stop);
            await input.ReadExactlyAsync(original, stop);
            if (written.AsSpan().SequenceEqual(original)) return;
        }
        output.SetLength(0);
        output.Position = input.Position = 0;
    }
}
