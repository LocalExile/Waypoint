using System.IO.Compression;
using System.Text;

namespace Waypoint.Tests;

// LocalExile's releases: versions in SemVer's order, GitHub's answer, what an install is, what a zip may touch, the --json events.
public class LocalExileReleaseTests
{
    [Theory]
    [InlineData("v0.1.0", "v0.1.0-beta")]            // the release follows its pre-release
    [InlineData("0.1.0-beta", "0.1.0-alpha")]
    [InlineData("0.1.0-beta.2", "0.1.0-beta.1")]
    [InlineData("0.1.0-beta.10", "0.1.0-beta.9")]    // numbers, not text
    [InlineData("0.1.0-beta.1", "0.1.0-beta")]       // more identifiers
    [InlineData("0.1.0-beta", "0.1.0-1")]            // text after numbers
    [InlineData("0.2.0-beta", "0.1.9")]
    [InlineData("0.10.0", "0.9.0")]
    public void A_newer_version_is_newer(string newer, string older)
    {
        Assert.True(ReleaseVersion.Parse(newer)!.IsNewerThan(ReleaseVersion.Parse(older)!));
        Assert.False(ReleaseVersion.Parse(older)!.IsNewerThan(ReleaseVersion.Parse(newer)!));
    }

    [Fact]
    public void The_build_of_a_version_does_not_count() =>
        Assert.False(ReleaseVersion.Parse("0.1.0-beta")!.IsNewerThan(ReleaseVersion.Parse("0.1.0-beta+cc0f9c3")!));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("latest")]
    [InlineData("1.2")]
    public void Anything_else_is_no_version(string? text) => Assert.Null(ReleaseVersion.Parse(text));

    [Fact]
    public void The_release_names_its_zip_and_checksum()
    {
        var release = LocalExileReleases.ParseRelease("""
            {"tag_name":"v0.1.0-beta","html_url":"https://github.com/LocalExile/LocalExile/releases/tag/v0.1.0-beta",
             "assets":[{"name":"LocalExile-v0.1.0-beta-win-x64.zip.sha256","browser_download_url":"https://x/z.sha256","size":102},
                       {"name":"LocalExile-v0.1.0-beta-win-x64.zip","browser_download_url":"https://x/z.zip","size":151334001}]}
            """)!;
        Assert.Equal("0.1.0-beta", release.Version.ToString());
        Assert.Equal("https://x/z.zip", release.ZipUrl);
        Assert.Equal("https://x/z.sha256", release.HashUrl);
        Assert.Equal(151334001, release.ZipBytes);
    }

    [Fact]
    public void A_release_without_its_files_says_so()
    {
        var release = LocalExileReleases.ParseRelease("""{"tag_name":"v1.0.0","assets":[]}""")!;
        Assert.Null(release.ZipUrl);
        Assert.Null(release.HashUrl);
    }

    static string TempFolder()
    {
        string folder = Path.Combine(Path.GetTempPath(), "waypoint-release-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    static string Zip(string folder, params string[] names)
    {
        string path = Path.Combine(folder, "test.zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (string name in names)
        {
            using var writer = new StreamWriter(zip.CreateEntry(name).Open(), Encoding.UTF8);
            writer.Write("new " + name);
        }
        return path;
    }

    const string Exe = "LocalExile/LocalExile.exe", Manifest = "LocalExile/release.json";

    [Theory]
    [InlineData("LocalExile/data/saves.db")]
    [InlineData("LocalExile/data/login-keys.json")]
    [InlineData("LocalExile/DATA/saves.db")]
    [InlineData("LocalExile/Client/Content.ggpk")]
    [InlineData("LocalExile/../outside.txt")]
    [InlineData("LocalExile/app/../../outside.txt")]
    [InlineData("elsewhere/file.txt")]
    [InlineData("LocalExile/C:/file.txt")]
    public async Task A_zip_that_would_touch_what_is_the_players_changes_nothing(string bad)
    {
        string folder = TempFolder(), install = Path.Combine(folder, "install");
        try
        {
            Directory.CreateDirectory(install);
            string zip = Zip(folder, "LocalExile/app/a.dll", bad, Exe, Manifest);
            await Assert.ThrowsAsync<InvalidDataException>(() => LocalExileReleases.ExtractAsync(zip, install, null, CancellationToken.None));
            Assert.False(File.Exists(Path.Combine(install, "LocalExile.exe")));
            Assert.False(File.Exists(Path.Combine(install, "app", "a.dll")));
            Assert.False(File.Exists(Path.Combine(folder, "outside.txt")));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public async Task An_update_replaces_the_program_and_keeps_the_players_files()
    {
        string folder = TempFolder(), install = Path.Combine(folder, "install");
        try
        {
            Directory.CreateDirectory(Path.Combine(install, "data"));
            Directory.CreateDirectory(Path.Combine(install, "Client"));
            Directory.CreateDirectory(Path.Combine(install, "app"));
            File.WriteAllText(Path.Combine(install, "data", "saves.db"), "mine");
            File.WriteAllText(Path.Combine(install, "Client", "Content.ggpk"), "mine");
            File.WriteAllText(Path.Combine(install, "app", "a.dll"), "old");
            string zip = Zip(folder, "LocalExile/app/a.dll", "LocalExile/data/schema/schema.json", Exe, Manifest);

            await LocalExileReleases.ExtractAsync(zip, install, null, CancellationToken.None);

            Assert.Equal("mine", File.ReadAllText(Path.Combine(install, "data", "saves.db")));
            Assert.Equal("mine", File.ReadAllText(Path.Combine(install, "Client", "Content.ggpk")));
            Assert.Equal("new LocalExile/app/a.dll", File.ReadAllText(Path.Combine(install, "app", "a.dll")));
            Assert.True(File.Exists(Path.Combine(install, "data", "schema", "schema.json")));
            Assert.True(LocalExileReleases.IsInstall(install));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void Release_json_goes_in_last_so_a_cut_short_update_is_offered_again()
    {
        string folder = TempFolder();
        try
        {
            using var zip = ZipFile.OpenRead(Zip(folder, Manifest, Exe, "LocalExile/app/a.dll"));
            var plan = LocalExileReleases.Plan(zip);
            Assert.Equal(LocalExileReleases.ExeName, plan[^2].Relative);
            Assert.Equal(LocalExileReleases.ManifestName, plan[^1].Relative);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void A_checkout_is_no_install_and_its_version_is_read_from_the_manifest()
    {
        string folder = TempFolder();
        try
        {
            File.WriteAllText(Path.Combine(folder, "LocalExile.exe"), "");
            Assert.False(LocalExileReleases.IsInstall(folder));
            File.WriteAllText(Path.Combine(folder, "release.json"), """{"version":"0.1.0-beta","commit":"cc0f9c3","client":"3.29.3b"}""");
            Assert.True(LocalExileReleases.IsInstall(folder));
            Assert.Equal("0.1.0-beta", LocalExileReleases.Installed(folder)!.ToString());
            File.WriteAllText(Path.Combine(folder, "GameServer.slnx"), "");
            Assert.False(LocalExileReleases.IsInstall(folder));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void Saves_are_copied_with_their_journal_before_an_update()
    {
        string folder = TempFolder();
        try
        {
            Assert.Null(LocalExileReleases.BackUpSaves(folder, null, DateTime.Now));
            Directory.CreateDirectory(Path.Combine(folder, "data"));
            File.WriteAllText(Path.Combine(folder, "data", "saves.db"), "db");
            File.WriteAllText(Path.Combine(folder, "data", "saves.db-wal"), "wal");
            string backup = LocalExileReleases.BackUpSaves(folder, ReleaseVersion.Parse("0.1.0"), new DateTime(2026, 11, 1, 12, 0, 0))!;
            Assert.EndsWith("before-update-0.1.0-20261101-120000", backup);
            Assert.Equal("db", File.ReadAllText(Path.Combine(backup, "saves.db")));
            Assert.Equal("wal", File.ReadAllText(Path.Combine(backup, "saves.db-wal")));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Theory]
    [InlineData("exile_1", "secret", "secret", true)]
    [InlineData("ab", "secret", "secret", false)]             // too short a name
    [InlineData("has space", "secret", "secret", false)]
    [InlineData("exile", "abc", "abc", false)]                // too short a password
    [InlineData("exile", "secret", "other", false)]           // they differ
    [InlineData("exile", "пароль12", "пароль12", false)]      // standard input's encoding would change it
    public void An_account_follows_the_rules(string name, string password, string again, bool fine) =>
        Assert.Equal(fine, LocalExileReleases.AccountProblem(name, password, again) is null);

    [Fact]
    public void A_question_is_read_from_its_event()
    {
        // As the real LocalExile.exe v0.1.0-beta writes it.
        var item = LocalExileEvent.Parse("""{"schema":1,"event":"needs-input","time":"2026-10-31T21:54:38.8\u002B00:00","option":"--client","message":"which folder holds the Path of Exile 3.29.3b client?","suggestion":"C:\\Program Files (x86)\\Grinding Gear Games\\Path of Exile"}""")!;
        Assert.Equal("needs-input", item.Name);
        Assert.Equal("--client", item.Option);
        Assert.Equal(@"C:\Program Files (x86)\Grinding Gear Games\Path of Exile", item.Suggestion);
    }

    [Fact]
    public void Progress_and_errors_keep_their_fields()
    {
        var progress = LocalExileEvent.Parse("""{"schema":1,"event":"progress","id":"extract","message":"[1/3] tables","current":1,"total":3}""")!;
        Assert.Equal((1L, 3L), (progress.Current!.Value, progress.Total!.Value));
        var error = LocalExileEvent.Parse("""{"schema":1,"event":"error","code":6,"kind":"Busy","message":"port(s) 6112 are in use"}""")!;
        Assert.Equal((6, "Busy"), (error.Code!.Value, error.Kind!));
    }

    [Theory]
    [InlineData("extract")]
    [InlineData("extract-object-types")]    // each entry reports under its own id
    [InlineData("server-gateway")]
    public void A_stage_has_a_name_of_its_own(string id) => Assert.NotEqual("raw", LocalExileReleases.Stage(id, "raw"));

    [Theory]
    [InlineData("plain text")]
    [InlineData("{broken")]
    [InlineData("""{"event":"step"}""")]
    [InlineData("""{"schema":1}""")]
    public void A_line_that_is_no_event_is_not_read_as_one(string line) => Assert.Null(LocalExileEvent.Parse(line));

    // The tool itself, through a stand-in with the same name: a batch file cannot be LocalExile.exe, so cmd plays it through the runner's own process code.
    static async Task<(int Code, List<string> Lines)> Run(string script, string? input = null)
    {
        var lines = new List<string>();
        int code = await ToolProcess.RunAsync("cmd.exe", ["/c", script], null, line => { lock (lines) lines.Add(line); }, CancellationToken.None, input, utf8: true);
        return (code, lines);
    }

    [Fact]
    public async Task A_password_reaches_the_tool_on_standard_input()
    {
        var (code, lines) = await Run("set /p answer=& call echo got %answer%", "secret");
        Assert.Equal(0, code);
        Assert.Contains("got secret", lines);
    }

    [Fact]
    public async Task A_tool_that_reads_nothing_does_not_fail_on_its_input()
    {
        var (code, _) = await Run("exit 7", new string('x', 100_000));
        Assert.Equal(7, code);
    }
}
