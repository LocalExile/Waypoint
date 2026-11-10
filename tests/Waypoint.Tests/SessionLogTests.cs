using Launcher.Core;
using Waypoint;

namespace Waypoint.Tests;

public sealed class SessionLogTests : IDisposable
{
    readonly string folder = Path.Combine(Path.GetTempPath(), "Waypoint-session-log-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
    }

    /// <summary>As a text editor reads the log while Waypoint still writes it.</summary>
    static string Read(string file)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return new StreamReader(stream).ReadToEnd();
    }

    [Fact]
    public void Lines_reach_the_file_at_once_with_their_time_and_level_and_stay_across_sessions()
    {
        using (var first = new SessionLog(folder))
        {
            first.Write("> Install mod", at: new DateTime(2026, 11, 9, 14, 5, 6, 789));
            first.Write("Install mod failed: no such release", "ERROR", new DateTime(2026, 11, 9, 14, 5, 9));
            // Read while it is open, as a text editor would.
            using var stream = new FileStream(first.File, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            string[] lines = new StreamReader(stream).ReadToEnd().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(["2026-11-09 14:05:06.789 INFO  > Install mod", "2026-11-09 14:05:09.000 ERROR Install mod failed: no such release"], lines);
        }
        using (var second = new SessionLog(folder)) second.Write("second session");
        string[] all = File.ReadAllLines(Path.Combine(folder, SessionLog.Name));
        Assert.Equal(3, all.Length);
        Assert.EndsWith("second session", all[2]);
    }

    [Fact]
    public void Private_details_never_reach_the_file()
    {
        using var log = new SessionLog(folder) { PrivateNames = () => ["Exiled_Hero"] };
        log.Write(@"Exiled_Hero logged in from C:\Users\somebody\LocalExile as someone@example.com with password=hunter2");
        string text = Read(log.File);
        Assert.DoesNotContain("Exiled_Hero", text);
        Assert.DoesNotContain("somebody", text);
        Assert.DoesNotContain("someone@example.com", text);
        Assert.DoesNotContain("hunter2", text);
        Assert.Contains("logged in from", text);
    }

    [Fact]
    public void A_full_file_is_set_aside_and_only_two_older_ones_are_kept()
    {
        using var log = new SessionLog(folder, maxBytes: 200);
        for (int i = 0; i < 40; i++) log.Write($"line {i:00} of an operation that fills the file up");
        string[] files = Directory.GetFiles(folder).Select(Path.GetFileName).Order().ToArray()!;
        Assert.Equal(["waypoint.1.log", "waypoint.2.log", "waypoint.log"], files);
        // The newest line is in the file being written, and nothing is written twice.
        Assert.Contains("line 39", Read(Path.Combine(folder, "waypoint.log")));
        Assert.DoesNotContain("line 39", File.ReadAllText(Path.Combine(folder, "waypoint.1.log")));
    }

    [Fact]
    public void A_folder_that_cannot_be_written_turns_the_log_off_without_an_error()
    {
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, "not-a-folder");
        File.WriteAllText(file, "");
        using var log = new SessionLog(Path.Combine(file, "logs"));
        log.Write("this cannot be written");
        log.Write("and neither can this");
        Assert.False(Directory.Exists(Path.Combine(file, "logs")));
        Assert.Equal("12.3 s", SessionLog.Seconds(TimeSpan.FromSeconds(12.34)));
    }

    [Fact]
    public void A_report_for_support_holds_every_log_under_its_heading_and_saves_as_one_file()
    {
        var state = new RuntimeSnapshot(folder, "Default", "", "", "", "", [], [], []);
        string report = DiagnosticReport.Create(state, "0.14.4", "> Play", "Play failed",
            [("WAYPOINT LOG (waypoint.log)", "one\ntwo\nthree", 2), ("LOG: Gateway", "[mods] loaded hello", 150)], details: ["LocalExile: release 0.2.2-beta", "Mode: Simple"]);
        Assert.Contains("LocalExile: release 0.2.2-beta", report);
        Assert.Contains("Mode: Simple", report);
        Assert.Contains("Last failure: Play failed", report);
        Assert.Contains("WAYPOINT LOG (waypoint.log)", report);
        Assert.DoesNotContain("one", report.Split("WAYPOINT LOG")[1].Split("LOG: Gateway")[0]);
        Assert.Contains("[mods] loaded hello", report);
        // The report of one chosen log is what it was.
        Assert.Contains("SELECTED LOG: Gateway", DiagnosticReport.Create(state, "0.14.4", "", null, "Gateway", "line"));

        var now = new DateTime(2026, 11, 9, 14, 5, 6);
        Assert.Equal("waypoint-report-20261109-140506.txt", SupportReport.FileName(now));
        string path = SupportReport.Save(Path.Combine(folder, "reports"), report, now);
        Assert.Equal(report, File.ReadAllText(path));
    }
}
