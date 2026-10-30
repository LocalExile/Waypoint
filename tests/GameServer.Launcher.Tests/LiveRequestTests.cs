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
    }

    [Fact]
    public void The_repository_root_is_the_working_directory_or_the_executables_folder_never_a_parent()
    {
        string root = Path.Combine(Path.GetTempPath(), $"lxroot-{Guid.NewGuid():N}");
        string inside = Path.Combine(root, "src", "GameServer.Launcher");
        Directory.CreateDirectory(Path.Combine(root, "scripts", "dev"));
        Directory.CreateDirectory(inside);
        File.WriteAllText(Path.Combine(root, "GameServer.slnx"), "");
        File.WriteAllText(Path.Combine(root, "scripts", "dev", "live.ps1"), "");
        try
        {
            Assert.Equal(root, Repo.Find(root, inside));
            Assert.Equal(root, Repo.Find(inside, root));
            Assert.Null(Repo.Find(inside, inside));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
