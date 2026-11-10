using System.Text;

namespace Launcher.Core;

/// <summary>
/// Waypoint's own log on disk (<c>logs\waypoint.log</c> in Waypoint's folder, not the checkout's): what it ran, how long
/// it took and why it failed, kept across sessions so that a failure can still be read after Waypoint is closed, the
/// setup guide's included (it runs before there is a checkout). Every line is redacted as a diagnostic report is
/// (<see cref="DiagnosticReport.Redact"/>), since a user sends this file to someone else. Writing never throws: a folder
/// that cannot be written turns the log off for the session.
/// </summary>
public sealed class SessionLog : IDisposable
{
    public const string Name = "waypoint.log";
    /// <summary>The size at which the file is set aside (<c>waypoint.1.log</c>, the older <c>waypoint.2.log</c>).</summary>
    public const long MaxBytes = 1_000_000;
    /// <summary>How many set-aside files are kept.</summary>
    public const int Kept = 2;

    /// <summary>The running Waypoint's log; null before <see cref="Open"/>.</summary>
    public static SessionLog? Current { get; private set; }

    /// <summary>The file being written.</summary>
    public string File { get; }
    /// <summary>Account and character names Waypoint has seen, left out of every line; read on the writing thread.</summary>
    public Func<IEnumerable<string>>? PrivateNames { get; set; }

    readonly object _gate = new();
    readonly long _maxBytes;
    StreamWriter? _writer;
    long _bytes;
    bool _off;

    public SessionLog(string folder, long maxBytes = MaxBytes)
    {
        File = Path.Combine(folder, Name);
        _maxBytes = maxBytes;
    }

    /// <summary>Starts the running Waypoint's log in <paramref name="folder"/> with a line that marks the session and the
    /// lines of <paramref name="header"/> (the version, the system).</summary>
    public static SessionLog Open(string folder, params string[] header)
    {
        var log = new SessionLog(folder);
        log.Write("==== Waypoint started ====");
        foreach (string line in header) log.Write(line);
        Current?.Dispose();
        return Current = log;
    }

    /// <summary>How long something took, as the log writes it: "12.3 s", the same in every language.</summary>
    public static string Seconds(TimeSpan elapsed) =>
        elapsed.TotalSeconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " s";

    /// <summary>One entry: the time, <paramref name="level"/> (INFO, WARN, ERROR) and the redacted text.</summary>
    public void Write(string text, string level = "INFO", DateTime? at = null)
    {
        lock (_gate)
        {
            if (_off) return;
            try
            {
                string entry = $"{at ?? DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level,-5} {Redacted(text.TrimEnd())}";
                if (_writer is null) Start();
                else if (_bytes > _maxBytes) { SetAside(); Start(); }
                _writer!.WriteLine(entry);
                _bytes += Encoding.UTF8.GetByteCount(entry) + Environment.NewLine.Length;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException
                or System.Security.SecurityException)
            {
                // A log that cannot be written must not break what it describes.
                _off = true;
                try { _writer?.Dispose(); } catch (Exception inner) when (inner is IOException or ObjectDisposedException) { }
                _writer = null;
            }
        }
    }

    string Redacted(string text)
    {
        IEnumerable<string> names = [];
        // The names are collected on the interface's thread: a list that changes while it is read is read next time.
        try { names = PrivateNames?.Invoke().ToArray() ?? []; }
        catch (InvalidOperationException) { }
        return DiagnosticReport.Redact(text, names);
    }

    void Start()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(File)!);
        if (System.IO.File.Exists(File) && new FileInfo(File).Length > _maxBytes) SetAside();
        // Others may read it while Waypoint runs (a text editor, a tail), and each line reaches the disk at once.
        var stream = new FileStream(File, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        _bytes = stream.Length;
        _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
    }

    /// <summary>waypoint.log becomes waypoint.1.log, that one waypoint.2.log, and the oldest goes.</summary>
    void SetAside()
    {
        _writer?.Dispose();
        _writer = null;
        string Older(int n) => Path.Combine(Path.GetDirectoryName(File)!, $"waypoint.{n}.log");
        for (int n = Kept; n >= 1; n--)
        {
            string from = n == 1 ? File : Older(n - 1);
            if (System.IO.File.Exists(from)) System.IO.File.Move(from, Older(n), overwrite: true);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _off = true;
            try { _writer?.Dispose(); } catch (Exception e) when (e is IOException or ObjectDisposedException) { }
            _writer = null;
        }
    }
}
