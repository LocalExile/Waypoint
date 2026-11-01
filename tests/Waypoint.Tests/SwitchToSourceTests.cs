using System.Diagnostics;

namespace Waypoint.Tests;

public class SwitchToSourceTests : IDisposable
{
    readonly string _top = Path.Combine(Path.GetTempPath(), "Waypoint-switch-" + Guid.NewGuid().ToString("N"));
    string Old => Path.Combine(_top, "old");
    string Copy => Path.Combine(_top, "copy");

    public SwitchToSourceTests()
    {
        Directory.CreateDirectory(Path.Combine(Old, "data"));
        Directory.CreateDirectory(Path.Combine(Old, "Mods", "Binaries"));
        Directory.CreateDirectory(Path.Combine(Old, "app"));
        Directory.CreateDirectory(Path.Combine(Old, "Tools", "python"));
        Directory.CreateDirectory(Path.Combine(Old, "Client"));
        foreach (string file in new[] { "LocalExile.exe", "release.json", "README.txt", @"data\saves.db", @"Mods\Binaries\mine.dll", @"app\GameServer.Gateway.exe", @"Client\Content.ggpk", "my notes.txt" })
            File.WriteAllText(Path.Combine(Old, file), "stand-in");
        File.SetAttributes(Path.Combine(Old, "app", "GameServer.Gateway.exe"), FileAttributes.ReadOnly);
        Directory.CreateDirectory(Copy);
    }

    public void Dispose()
    {
        // A link goes first, alone: a recursive delete must not reach what it points at.
        string link = Path.Combine(Copy, "Client");
        if (Directory.Exists(link) && new DirectoryInfo(link).LinkTarget is not null) Directory.Delete(link);
        foreach (var file in new DirectoryInfo(_top).EnumerateFiles("*", SearchOption.AllDirectories)) file.Attributes = FileAttributes.Normal;
        Directory.Delete(_top, recursive: true);
    }

    static SwitchChoices Choices(bool bring = true, bool keep = true, bool tested = true, bool give = true, bool shares = true, double copyGb = 0, bool hasSaves = false, int accounts = 2, int characters = 5) =>
        new(@"D:\New", @"D:\Old", true, bring, accounts, characters, keep, tested, give, shares, copyGb, hasSaves);

    static string[] Lines(SwitchChoices choices) => [.. SwitchToSource.Describe(choices).Select(l => l.Text)];

    [Fact]
    public void What_will_happen_says_what_the_choices_do()
    {
        Assert.Equal([
            @"Download LocalExile into D:\New and build it.",
            "Use the game client of your current LocalExile, so it takes no extra space.",
            "Copy your accounts and characters. Accounts: 2. Characters: 5.",
            "Install Frida, so that Give item works.",
            "Stay on the tested version.",
            "Leave your current LocalExile as it is. Each copy keeps its own progress.",
        ], Lines(Choices()));
    }

    [Fact]
    public void A_risky_choice_is_marked_and_says_where_things_stay()
    {
        var lines = SwitchToSource.Describe(Choices(bring: false, keep: false, tested: false, give: false, shares: false, copyGb: 69.5));
        Assert.Contains(lines, l => l.Text == "Copy the game client, about 70 GB." && !l.Attention);
        Assert.Contains(lines, l => l.Text == "Start with no accounts or characters. You create an account at the end.");
        foreach (string attention in new[] { "Leave out Frida. Waypoint offers it again when you press Give item.", "Update to the newest changes once a day.",
                     "After the new copy has worked for you, ask before deleting the old one.", @"Your old characters stay in D:\Old\data until you delete that folder." })
            Assert.Contains(lines, l => l.Text == attention && l.Attention);
    }

    [Fact]
    public void Saves_already_in_the_folder_are_called_out_and_unknown_counts_are_left_out()
    {
        string[] lines = Lines(Choices(hasSaves: true, accounts: -1, characters: -1));
        Assert.Contains("Copy your accounts and characters.", lines);
        Assert.Contains(@"The characters already in D:\New are backed up and set aside.", lines);
    }

    [Fact]
    public void The_last_page_warns_about_a_shared_client_only_when_there_is_one()
    {
        Assert.Contains("do not delete that folder yourself", SwitchToSource.Done(Choices()));
        Assert.DoesNotContain("do not delete that folder yourself", SwitchToSource.Done(Choices(shares: false)));
    }

    [Fact]
    public void Only_the_copys_own_tested_branch_counts()
    {
        Directory.CreateDirectory(Path.Combine(Copy, ".git"));
        Assert.False(SwitchToSource.OnTestedVersion(Copy));
        File.WriteAllText(Path.Combine(Copy, ".git", "HEAD"), "ref: refs/heads/main\n");
        Assert.False(SwitchToSource.OnTestedVersion(Copy));
        File.WriteAllText(Path.Combine(Copy, ".git", "HEAD"), $"ref: refs/heads/{SwitchToSource.TestedBranch}\n");
        Assert.True(SwitchToSource.OnTestedVersion(Copy));
    }

    [Fact]
    public void Removing_a_release_takes_its_program_files_and_nothing_of_the_players()
    {
        var removed = SwitchToSource.RemoveRelease(Old, Copy);
        Assert.False(removed.ClientMoved);
        Assert.False(LocalExileReleases.IsInstall(Old));
        foreach (string gone in new[] { "LocalExile.exe", "release.json", "README.txt" }) Assert.False(File.Exists(Path.Combine(Old, gone)));
        foreach (string gone in new[] { "app", "Tools" }) Assert.False(Directory.Exists(Path.Combine(Old, gone)));
        foreach (string kept in new[] { @"data\saves.db", @"Mods\Binaries\mine.dll", @"Client\Content.ggpk", "my notes.txt" }) Assert.True(File.Exists(Path.Combine(Old, kept)), kept);
    }

    [Fact]
    public void A_shared_client_moves_into_the_copy_on_the_same_drive()
    {
        Link(Path.Combine(Copy, "Client"), Path.Combine(Old, "Client"));
        Assert.True(SwitchToSource.RemoveRelease(Old, Copy).ClientMoved);
        Assert.False(Directory.Exists(Path.Combine(Old, "Client")));
        Assert.Null(new DirectoryInfo(Path.Combine(Copy, "Client")).LinkTarget);
        Assert.True(File.Exists(Path.Combine(Copy, "Client", "Content.ggpk")));
        Assert.False(Directory.Exists(Path.Combine(Copy, "Client.moving")));
    }

    [Fact]
    public void A_copy_linked_to_some_other_client_keeps_its_link_and_a_links_target_survives()
    {
        string elsewhere = Path.Combine(_top, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        File.WriteAllText(Path.Combine(elsewhere, "Content.ggpk"), "stand-in");
        Link(Path.Combine(Copy, "Client"), elsewhere);
        string scripts = Path.Combine(Old, "scripts");
        Link(scripts, elsewhere);
        Assert.False(SwitchToSource.RemoveRelease(Old, Copy).ClientMoved);
        Assert.False(Directory.Exists(scripts));
        Assert.True(File.Exists(Path.Combine(elsewhere, "Content.ggpk")));
        Assert.True(File.Exists(Path.Combine(Old, "Client", "Content.ggpk")));
        Assert.NotNull(new DirectoryInfo(Path.Combine(Copy, "Client")).LinkTarget);
    }

    [Fact]
    public void Only_a_release_can_be_removed_and_never_the_folder_in_use()
    {
        Assert.Throws<InvalidOperationException>(() => SwitchToSource.RemoveRelease(Copy, Old));
        Assert.Throws<InvalidOperationException>(() => SwitchToSource.RemoveRelease(Old, Old));
        Assert.True(LocalExileReleases.IsInstall(Old));
    }

    static void Link(string link, string target)
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }
}
