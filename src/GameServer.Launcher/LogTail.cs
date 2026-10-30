using System.Text;
using System.Text.RegularExpressions;

namespace GameServer.Launcher;

public enum Severity { Info, Warning, Error }

sealed record LogLine(string Source, string Text, Severity Severity, DateTime At);

/// <summary>
/// Follows one log file: each <see cref="Read"/> returns the lines appended since the last one. A file that is new,
/// shorter than before (live.ps1 deletes a server's logs when it restarts it) or gone starts over. A large file is
/// followed from its last <see cref="InitialBytes"/>, so the client's log does not have to be read whole.
/// </summary>
sealed class LogTail(string path, long initialBytes = 512 * 1024)
{
    public string Path { get; } = path;
    public long InitialBytes { get; } = initialBytes;

    long _position = -1;
    Decoder _decoder = new UTF8Encoding(false).GetDecoder();
    readonly StringBuilder _partial = new();

    /// <summary>The file started over since the last read: what was shown of it is gone.</summary>
    public bool Restarted { get; private set; }

    public List<string> Read()
    {
        Restarted = false;
        var lines = new List<string>();
        if (!File.Exists(Path))
        {
            if (_position > 0) Reset();
            return lines;
        }
        try
        {
            using var stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long length = stream.Length;
            if (_position < 0 || length < _position)
            {
                if (_position >= 0) Reset();
                _position = Math.Max(0, length - InitialBytes);
                // Mid-file: the first line is a fragment.
                if (_position > 0) _partial.Append('\0');
            }
            if (length == _position) return lines;
            stream.Position = _position;
            var bytes = new byte[Math.Min(length - _position, 4 * 1024 * 1024)];
            int read = stream.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
            _position += read;
            var chars = new char[_decoder.GetCharCount(bytes, 0, read)];
            _decoder.GetChars(bytes, 0, read, chars, 0);
            foreach (char c in chars)
            {
                if (c == '\n')
                {
                    string line = _partial.ToString().TrimEnd('\r');
                    _partial.Clear();
                    if (!line.StartsWith('\0')) lines.Add(line);
                }
                else _partial.Append(c);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return lines;
    }

    void Reset()
    {
        Restarted = true;
        _position = -1;
        _partial.Clear();
        _decoder = new UTF8Encoding(false).GetDecoder();
    }
}

static partial class LogSeverity
{
    /// <summary>What a log line reports. A server's .err file holds only errors; in the others an exception, an error or a
    /// failure is one, and a warning, a slow handler or tick ("took 73 ms", ClientInbox.SlowAfter) is a warning.</summary>
    public static Severity Of(string line, bool errorFile = false)
    {
        if (errorFile && line.Trim().Length > 0) return Severity.Error;
        if (ErrorWords().IsMatch(line)) return Severity.Error;
        if (WarningWords().IsMatch(line)) return Severity.Warning;
        return Severity.Info;
    }

    // "0 Error(s)" is MSBuild's summary of a good build.
    [GeneratedRegex(@"\b(?:exception|failed|failure|fatal|unhandled|crash(?:ed)?)\b|(?<!\b0 )\berrors?\b", RegexOptions.IgnoreCase)]
    private static partial Regex ErrorWords();

    [GeneratedRegex(@"\b(warning|warn|slow|took \d+ ms|over \d+ ms)\b", RegexOptions.IgnoreCase)]
    private static partial Regex WarningWords();
}
