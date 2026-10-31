using GameServer.Launcher;

namespace GameServer.Launcher.Tests;

// Every launcher button is a live.ps1 switch; these are the command lines it runs.
public class LiveRequestTests
{
    [Fact]
    public void Play_passes_the_chosen_options()
    {
        Assert.Empty(new LiveRequest(LiveMode.All).Arguments());
        Assert.Equal(["-Walkmaps", "-NoBuild", "-NoExtract"], new LiveRequest(LiveMode.All, Walkmaps: true, NoBuild: true, NoExtract: true).Arguments());
        Assert.Equal(["-NoClient", "-Walkmaps"], new LiveRequest(LiveMode.Servers, Walkmaps: true).Arguments());
    }

    [Fact]
    public void Starting_only_the_game_takes_no_other_switch()
    {
        // live.ps1 refuses -ClientOnly with -Walkmaps and skips building and extraction for it.
        Assert.Equal(["-ClientOnly"], new LiveRequest(LiveMode.Client, Walkmaps: true, NoBuild: true, NoExtract: true).Arguments());
    }

    [Fact]
    public void A_restart_names_one_server_and_keeps_the_walk_map_switch()
    {
        Assert.Equal(["-Only", "Gateway", "-Walkmaps"], new LiveRequest(LiveMode.Restart, "Gateway", Walkmaps: true).Arguments());
        Assert.Equal(["-Only", "Auth", "-NoBuild"], new LiveRequest(LiveMode.Restart, "Auth", NoBuild: true).Arguments());
        Assert.Throws<ArgumentException>(() => new LiveRequest(LiveMode.Restart, "Host").Arguments());
        Assert.Throws<ArgumentException>(() => new LiveRequest(LiveMode.Restart).Arguments());
    }

    [Fact]
    public void Stop_takes_only_the_server_it_stops()
    {
        Assert.Equal(["-Stop"], new LiveRequest(LiveMode.Stop, Walkmaps: true, NoBuild: true).Arguments());
        Assert.Equal(["-Stop", "-Only", "Patch"], new LiveRequest(LiveMode.Stop, "Patch").Arguments());
        Assert.Equal("live.ps1 -Stop", new LiveRequest(LiveMode.Stop).Describe());
        Assert.Equal(["-Stop", "-NoClient"], new LiveRequest(LiveMode.StopServers, Walkmaps: true).Arguments());
    }

    [Fact]
    public void Turning_hot_reload_off_reaches_only_runs_that_start_a_gateway()
    {
        Assert.Equal(["-NoHotReload"], new LiveRequest(LiveMode.All, NoHotReload: true).Arguments());
        Assert.Equal(["-NoClient", "-NoHotReload"], new LiveRequest(LiveMode.Servers, NoHotReload: true).Arguments());
        Assert.Equal(["-Only", "Gateway", "-NoHotReload"], new LiveRequest(LiveMode.Restart, "Gateway", NoHotReload: true).Arguments());
        Assert.Equal(["-Only", "Auth"], new LiveRequest(LiveMode.Restart, "Auth", NoHotReload: true).Arguments());
        Assert.Equal(["-ClientOnly"], new LiveRequest(LiveMode.Client, NoHotReload: true).Arguments());
        Assert.Equal(["-Stop"], new LiveRequest(LiveMode.Stop, NoHotReload: true).Arguments());
    }

    /// <summary>A folder that looks like a server checkout to the launcher.</summary>
    static string Checkout(string dir)
    {
        Directory.CreateDirectory(Path.Combine(dir, "scripts", "dev"));
        File.WriteAllText(Path.Combine(dir, "GameServer.slnx"), "");
        File.WriteAllText(Path.Combine(dir, "scripts", "dev", "live.ps1"), "");
        return dir;
    }

    [Fact]
    public void The_server_is_the_saved_checkout_else_the_working_directory_else_the_players_choice()
    {
        string top = Path.Combine(Path.GetTempPath(), $"lxroot-{Guid.NewGuid():N}");
        try
        {
            string server = Checkout(Path.Combine(top, "LocalExile"));
            string other = Checkout(Path.Combine(top, "Other"));
            string launcher = Path.Combine(top, "LocalExile-Launcher");
            Directory.CreateDirectory(launcher);

            Assert.Equal(server, Repo.Find(server, other));
            Assert.Equal(other, Repo.Find(Path.Combine(top, "gone"), other));
            Assert.Equal(other, Repo.Find(null, other));
            Assert.Null(Repo.Find(launcher, launcher));
            Assert.Null(Repo.Find(null, top));
        }
        finally { Directory.Delete(top, recursive: true); }
    }

    [Fact]
    public void The_folder_picker_starts_at_a_LocalExile_checkout_beside_the_launchers_repository()
    {
        string top = Path.Combine(Path.GetTempPath(), $"lxroot-{Guid.NewGuid():N}");
        try
        {
            string exeFolder = Path.Combine(top, "LocalExile-Launcher", "src", "GameServer.Launcher", "bin");
            Directory.CreateDirectory(exeFolder);
            Assert.Equal(exeFolder, Repo.Suggested(exeFolder));
            string server = Checkout(Path.Combine(top, "LocalExile"));
            Assert.Equal(server, Repo.Suggested(exeFolder));
            Assert.Equal(server, Repo.Suggested(Path.Combine(top, "LocalExile-Launcher")));
        }
        finally { Directory.Delete(top, recursive: true); }
    }

    [Fact]
    public void The_checkouts_branch_comes_from_its_HEAD_a_short_commit_when_detached()
    {
        Assert.Equal("feat/launcher-support", Repo.BranchOf("ref: refs/heads/feat/launcher-support\n"));
        Assert.Equal("9297197", Repo.BranchOf("92971976069dc6abacd6d6b351df179fc85b8430\n"));
        Assert.Null(Repo.BranchOf(""));
        Assert.Null(Repo.BranchOf("not a head"));
    }

    [Fact]
    public void The_theme_is_saved_by_name()
    {
        string json = System.Text.Json.JsonSerializer.Serialize(new Settings { Theme = ThemeChoice.Dark });
        Assert.Contains("\"Theme\":\"Dark\"", json);
        Assert.Equal(ThemeChoice.Light, System.Text.Json.JsonSerializer.Deserialize<Settings>("{\"Theme\":\"Light\"}")!.Theme);
        Assert.Equal(ThemeChoice.System, System.Text.Json.JsonSerializer.Deserialize<Settings>("{}")!.Theme);
    }
}
