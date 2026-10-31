using System.Text;
using Waypoint;

namespace Waypoint.Tests;

public class LogTailTests : IDisposable
{
    readonly string _file = Path.Combine(Path.GetTempPath(), $"lxtail-{Guid.NewGuid():N}.log");

    public void Dispose() => File.Delete(_file);

    void Append(string text)
    {
        // As a server writes: shared, so the tail can read meanwhile.
        using var s = new FileStream(_file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        s.Write(Encoding.UTF8.GetBytes(text));
    }

    [Fact]
    public void Each_read_returns_the_complete_lines_written_since_the_last()
    {
        var tail = new LogTail(_file);
        Assert.Empty(tail.Read());
        Append("one\r\ntwo\nthr");
        Assert.Equal(["one", "two"], tail.Read());
        Assert.Empty(tail.Read());
        Append("ee\n");
        Assert.Equal(["three"], tail.Read());
    }

    [Fact]
    public void A_character_split_between_writes_is_kept_whole()
    {
        var tail = new LogTail(_file);
        byte[] arrow = Encoding.UTF8.GetBytes("S→C\n");
        File.WriteAllBytes(_file, arrow[..2]);
        Assert.Empty(tail.Read());
        using (var s = new FileStream(_file, FileMode.Append)) s.Write(arrow, 2, arrow.Length - 2);
        Assert.Equal(["S→C"], tail.Read());
    }

    [Fact]
    public void A_file_written_again_from_the_start_starts_over()
    {
        var tail = new LogTail(_file);
        Append("an old server's line that is long\n");
        Assert.Single(tail.Read());
        File.Delete(_file);
        Assert.Empty(tail.Read());
        Assert.True(tail.Restarted);
        Append("new\n");
        Assert.Equal(["new"], tail.Read());
    }

    [Fact]
    public void A_shorter_file_starts_over_too()
    {
        var tail = new LogTail(_file);
        Append("first line of a long log\nsecond line of it\n");
        Assert.Equal(2, tail.Read().Count);
        File.WriteAllText(_file, "fresh\n");
        Assert.Equal(["fresh"], tail.Read());
        Assert.True(tail.Restarted);
    }

    [Fact]
    public void A_large_file_is_followed_from_its_end_without_a_broken_first_line()
    {
        Append(string.Concat(Enumerable.Range(0, 1000).Select(i => $"line {i:0000}\n")));
        var tail = new LogTail(_file, initialBytes: 100);
        var lines = tail.Read();
        Assert.Equal("line 0999", lines[^1]);
        Assert.All(lines, l => Assert.Matches(@"^line \d{4}$", l));
        Assert.True(lines.Count < 12);
    }

    [Theory]
    [InlineData("[gateway] listening on 127.0.0.1:6112", false, Severity.Info)]
    [InlineData("Unhandled exception. System.IO.IOException: disk full", false, Severity.Error)]
    [InlineData("build failed: GameServer.Gateway", false, Severity.Error)]
    [InlineData("    0 Error(s)", false, Severity.Info)]
    [InlineData("WARNING: 1 game data file(s) missing", false, Severity.Warning)]
    [InlineData("[gateway] 127.0.0.1:5000 slow handler: 0x7d took 73 ms (ticks answered meanwhile)", false, Severity.Warning)]
    [InlineData("anything at all", true, Severity.Error)]
    [InlineData("   ", true, Severity.Info)]
    public void Lines_are_classified_by_what_they_report(string line, bool errorFile, Severity expected) =>
        Assert.Equal(expected, LogSeverity.Of(line, errorFile));
}
