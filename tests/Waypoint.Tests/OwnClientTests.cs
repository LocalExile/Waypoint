using System.Diagnostics;

namespace Waypoint.Tests;

/// <summary>A release whose client is a link to the user's own Path of Exile folder is told so in LocalExile's words, and can
/// be given a copy of its own on the user's word (<see cref="OwnClient"/>): the link works until a whole copy takes its
/// place, and the folder it led to is only read.</summary>
[Collection(nameof(ClientUpdateTests))]
public class OwnClientTests : IDisposable
{
    readonly string _top = Path.Combine(Path.GetTempPath(), "Waypoint-own-client-" + Guid.NewGuid().ToString("N"));
    string Folder => Path.Combine(_top, "LocalExile");
    string Client => Path.Combine(Folder, "Client");
    string Retail => Path.Combine(_top, "Path of Exile");
    string Copy => Path.Combine(Folder, OwnClient.NewFolder);
    string Aside => Path.Combine(Folder, OwnClient.OldLink);

    public OwnClientTests()
    {
        Directory.CreateDirectory(Path.Combine(Folder, "data"));
        File.WriteAllText(Path.Combine(Folder, "data", "login-keys.json"), "{}");
        File.WriteAllText(Path.Combine(Folder, "data", "saves.db"), "saves");
        MakeClient(Retail);
    }

    public void Dispose()
    {
        RemoveLinks(new DirectoryInfo(_top));
        if (Directory.Exists(_top)) Directory.Delete(_top, recursive: true);
    }

    /// <summary>Without following a link: one of these tests links a folder to the folder above it.</summary>
    static void RemoveLinks(DirectoryInfo folder)
    {
        foreach (var child in folder.EnumerateDirectories())
            if (child.LinkTarget is not null) child.Delete();
            else RemoveLinks(child);
    }

    /// <summary>A client LocalExile ran from: the game's files, and what LocalExile added beside them.</summary>
    static void MakeClient(string folder)
    {
        Directory.CreateDirectory(Path.Combine(folder, "Redist", "x64"));
        Directory.CreateDirectory(Path.Combine(folder, "logs"));
        File.WriteAllText(Path.Combine(folder, "PathOfExile.exe"), "the game");
        File.WriteAllText(Path.Combine(folder, "Game.exe"), "the game");
        File.WriteAllText(Path.Combine(folder, "Game.patched.exe"), "patched for this folder's keys");
        File.WriteAllText(Path.Combine(folder, "Content.ggpk"), new string('g', 5000));
        File.WriteAllText(Path.Combine(folder, "Redist", "x64", "runtime.dll"), "nested");
        File.WriteAllText(Path.Combine(folder, "logs", "Client.txt"), "the game's own log");
    }

    static Dictionary<string, string> Snapshot(string folder) => new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories)
        .ToDictionary(f => Path.GetRelativePath(folder, f.FullName), f => File.ReadAllText(f.FullName) + "|" + f.LastWriteTimeUtc.Ticks, StringComparer.OrdinalIgnoreCase);

    static bool IsLink(string folder) => new DirectoryInfo(folder).LinkTarget is not null;

    [Fact]
    public void A_client_of_its_own_needs_nothing()
    {
        MakeClient(Client);
        Assert.Null(OwnClient.Check(Folder));
        Directory.Delete(Client, recursive: true);
        Assert.Null(OwnClient.Check(Folder));
    }

    [Fact]
    public void A_link_to_the_users_game_folder_is_offered_a_copy()
    {
        Junction(Client, Retail);
        var plan = OwnClient.Check(Folder)!;
        Assert.Equal(Retail, plan.Source, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(Copy, plan.Copy.Target, StringComparer.OrdinalIgnoreCase);
        Assert.True(plan.HasSpace);
        Assert.Contains(plan.Copy.Files, f => f.Target.EndsWith("Game.patched.exe", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(plan.Copy.Files, f => f.Source.Contains(@"\logs\", StringComparison.OrdinalIgnoreCase));
        // The standing line is LocalExile's own warning, with the folder the link leads to.
        Assert.StartsWith($"Client is a link to {plan.Source}, not a copy. LocalExile changes files in that folder, and a Path of Exile update there can break this LocalExile.", OwnClient.Notice(plan));
        var (message, details) = OwnClient.Text(plan);
        Assert.Contains("The link works until the copy is whole", message);
        Assert.Contains(Retail, details);
        Assert.Contains(Client, details);
    }

    [Fact]
    public void A_link_to_another_LocalExiles_client_is_left_alone()
    {
        // LocalExile's own rule for its warning: the folder above the client holds a release.json or a GameServer.slnx.
        foreach (string marker in new[] { "release.json", "GameServer.slnx" })
        {
            string other = Path.Combine(_top, "Other " + marker);
            MakeClient(Path.Combine(other, "Client"));
            File.WriteAllText(Path.Combine(other, marker), "{}");
            Junction(Client, Path.Combine(other, "Client"));
            Assert.Null(OwnClient.Check(Folder));
            Directory.Delete(Client);
        }
    }

    [Fact]
    public void A_link_that_leads_to_no_client_is_the_setup_guides()
    {
        string empty = Path.Combine(_top, "empty");
        Directory.CreateDirectory(empty);
        Junction(Client, empty);
        Assert.Null(OwnClient.Check(Folder));
    }

    [Fact]
    public void A_release_inside_the_game_folder_is_not_copied_into_itself()
    {
        string inside = Path.Combine(Retail, "LocalExile");
        Directory.CreateDirectory(inside);
        Junction(Path.Combine(inside, "Client"), Retail);
        Assert.Null(OwnClient.Check(inside));
    }

    [Fact]
    public async Task The_move_copies_everything_and_only_reads_the_users_folder()
    {
        Junction(Client, Retail);
        var before = Snapshot(Retail);
        await OwnClient.MoveAsync(Folder, null, default);
        Assert.False(IsLink(Client));
        Assert.Equal(before, Snapshot(Retail));
        var copied = Snapshot(Client);
        foreach (var (name, content) in before.Where(f => !f.Key.StartsWith("logs", StringComparison.OrdinalIgnoreCase)))
            Assert.Equal(content, copied[name]);
        Assert.DoesNotContain(copied.Keys, k => k.StartsWith("logs", StringComparison.OrdinalIgnoreCase) || k == OwnClient.Complete);
        Assert.False(Directory.Exists(Copy));
        Assert.False(Directory.Exists(Aside));
        Assert.True(LocalExileReleases.IsSetUp(Folder));
        Assert.Null(OwnClient.Check(Folder));
    }

    [Fact]
    public async Task A_stopped_copy_leaves_the_link_and_goes_on_next_time()
    {
        Junction(Client, Retail);
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => OwnClient.MoveAsync(Folder, null, stop.Token));
        Assert.True(IsLink(Client));
        Assert.True(File.Exists(Path.Combine(Client, "Content.ggpk")));
        // Half a copy from that try: one file whole, one begun.
        Directory.CreateDirectory(Copy);
        File.Copy(Path.Combine(Retail, "Game.exe"), Path.Combine(Copy, "Game.exe"));
        File.WriteAllText(Path.Combine(Copy, "Content.ggpk.partial"), new string('g', 2000));
        Assert.True(OwnClient.Check(Folder)!.Copied > 0);
        await OwnClient.MoveAsync(Folder, null, default);
        Assert.False(IsLink(Client));
        Assert.Equal(new string('g', 5000), File.ReadAllText(Path.Combine(Client, "Content.ggpk")));
    }

    [Fact]
    public async Task What_an_earlier_try_copied_is_dropped_when_the_game_was_updated_since()
    {
        Junction(Client, Retail);
        Directory.CreateDirectory(Copy);
        File.WriteAllText(Path.Combine(Copy, "PathOfExile.exe"), "an older build of the game");
        File.SetLastWriteTimeUtc(Path.Combine(Copy, "PathOfExile.exe"), new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        await OwnClient.MoveAsync(Folder, null, default);
        Assert.Equal("the game", File.ReadAllText(Path.Combine(Client, "PathOfExile.exe")));
    }

    [Fact]
    public void A_move_cut_short_at_the_swap_is_finished_from_either_side()
    {
        // The copy is whole and the link was already set aside.
        Junction(Aside, Retail);
        MakeClient(Copy);
        File.WriteAllText(Path.Combine(Copy, OwnClient.Complete), "");
        Assert.True(OwnClient.Finish(Folder));
        Assert.False(IsLink(Client));
        Assert.True(File.Exists(Path.Combine(Client, "Content.ggpk")));
        Assert.False(File.Exists(Path.Combine(Client, OwnClient.Complete)));
        Assert.False(Directory.Exists(Aside));
        Assert.True(File.Exists(Path.Combine(Retail, "Content.ggpk")));
    }

    [Fact]
    public void A_link_set_aside_without_a_whole_copy_goes_back()
    {
        Junction(Aside, Retail);
        Directory.CreateDirectory(Copy);
        File.WriteAllText(Path.Combine(Copy, "Game.exe"), "half a copy");
        Assert.False(OwnClient.Finish(Folder));
        Assert.True(IsLink(Client));
        Assert.True(File.Exists(Path.Combine(Client, "Content.ggpk")));
        Assert.False(Directory.Exists(Aside));
        Assert.True(Directory.Exists(Copy));
    }

    [Fact]
    public void A_client_folder_of_its_own_is_never_replaced()
    {
        MakeClient(Client);
        File.WriteAllText(Path.Combine(Client, "mine.txt"), "mine");
        MakeClient(Copy);
        File.WriteAllText(Path.Combine(Copy, OwnClient.Complete), "");
        Assert.False(OwnClient.Finish(Folder));
        Assert.Equal("mine", File.ReadAllText(Path.Combine(Client, "mine.txt")));
    }

    [Fact]
    public void An_abandoned_copy_goes_only_when_no_link_waits_for_it()
    {
        Junction(Client, Retail);
        Directory.CreateDirectory(Copy);
        File.WriteAllText(Path.Combine(Copy, "Game.exe"), "half a copy");
        OwnClient.Discard(Folder);
        Assert.True(Directory.Exists(Copy));
        Directory.Delete(Client);
        MakeClient(Client);
        OwnClient.Discard(Folder);
        Assert.False(Directory.Exists(Copy));
        Assert.True(File.Exists(Path.Combine(Client, "Content.ggpk")));
    }

    [Fact]
    public void The_copy_is_asked_and_only_Copy_now_agrees()
    {
        Junction(Client, Retail);
        var plan = OwnClient.Check(Folder)!;
        string[]? shown = null;
        Assert.True(OwnClient.Ask(plan, (_, _, _, buttons) => { shown = buttons; return 0; }, _ => { }));
        Assert.Equal(["Copy now", "Open folder", "Cancel"], shown!);
        // The last button, and a box closed without an answer, change nothing.
        Assert.False(OwnClient.Ask(plan, (_, _, _, _) => 2, _ => { }));
        Assert.False(OwnClient.Ask(plan, (_, _, _, _) => -1, _ => { }));
    }

    [Fact]
    public void Without_the_room_it_only_tells()
    {
        Junction(Client, Retail);
        var plan = OwnClient.Check(Folder)! with { Free = 1000 };
        Assert.False(plan.HasSpace);
        string[]? shown = null;
        string? said = null;
        Assert.False(OwnClient.Ask(plan, (_, message, _, buttons) => { shown = buttons; said = message; return 0; }, _ => { }));
        Assert.Equal(["OK", "Open folder"], shown!);
        Assert.Contains("too full", said);
        Assert.Contains("nothing changes", said);
    }

    [Fact]
    public void Open_folder_shows_the_users_folder_and_asks_again()
    {
        Junction(Client, Retail);
        var plan = OwnClient.Check(Folder)!;
        var answers = new Queue<int>([1, 0]);
        string? opened = null;
        Assert.True(OwnClient.Ask(plan, (_, _, _, _) => answers.Dequeue(), path => opened = path));
        Assert.Equal(Retail, opened, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Without_the_users_word_nothing_changes()
    {
        Junction(Client, Retail);
        var result = await OwnClient.MakeCopyAsync(Folder, _ => false, null, Hold, default);
        Assert.Equal(OwnClient.Outcome.None, result.Outcome);
        Assert.NotNull(result.Plan);
        Assert.True(IsLink(Client));
        Assert.False(Directory.Exists(Copy));
    }

    [Fact]
    public async Task Copy_now_moves_and_a_busy_folder_or_a_failure_leaves_the_link()
    {
        Junction(Client, Retail);
        var busy = await OwnClient.MakeCopyAsync(Folder, _ => true, null, _ => Task.FromResult(false), default);
        Assert.Equal(OwnClient.Outcome.Failed, busy.Outcome);
        Assert.True(IsLink(Client));

        // The copy cannot be written: a file stands where its folder would be.
        File.WriteAllText(Copy, "in the way");
        var failed = await OwnClient.MakeCopyAsync(Folder, _ => true, null, Hold, default);
        Assert.Equal(OwnClient.Outcome.Failed, failed.Outcome);
        Assert.NotNull(failed.Reason);
        Assert.True(IsLink(Client));
        Assert.True(File.Exists(Path.Combine(Client, "Content.ggpk")));
        Assert.Contains(Retail, OwnClient.FailedText(failed.Plan!, failed.Reason!));
        File.Delete(Copy);

        using var stop = new CancellationTokenSource();
        stop.Cancel();
        var stopped = await OwnClient.MakeCopyAsync(Folder, _ => true, null, Hold, stop.Token);
        Assert.Equal(OwnClient.Outcome.Stopped, stopped.Outcome);
        Assert.True(IsLink(Client));

        var moved = await OwnClient.MakeCopyAsync(Folder, _ => true, null, Hold, default);
        Assert.Equal(OwnClient.Outcome.Moved, moved.Outcome);
        Assert.False(IsLink(Client));
        Assert.Equal(OwnClient.Outcome.None, (await OwnClient.MakeCopyAsync(Folder, _ => throw new InvalidOperationException("asked"), null, Hold, default)).Outcome);
    }

    [Fact]
    public void What_LocalExile_left_in_the_users_folder_is_named_not_removed()
    {
        Assert.Equal(["Game.exe", "Game.patched.exe"], OwnClient.Leftovers(Retail));
        // The user's own config.json is not LocalExile's.
        File.WriteAllText(Path.Combine(Retail, "config.json"), "{}");
        Assert.Equal(["Game.exe", "Game.patched.exe"], OwnClient.Leftovers(Retail));
        File.WriteAllText(Path.Combine(Retail, "config.json"), """{"GGG_CONFIG_FILE_NAME":"LocalExile\\production_Config.ini"}""");
        Assert.Equal(["Game.exe", "Game.patched.exe", "config.json"], OwnClient.Leftovers(Retail));
        Junction(Client, Retail);
        Assert.Contains("config.json", OwnClient.DoneText(OwnClient.Check(Folder)!));
        Assert.True(File.Exists(Path.Combine(Retail, "Game.patched.exe")));
    }

    static async Task<bool> Hold(Func<Task> work)
    {
        await work();
        return true;
    }

    static void Junction(string link, string target)
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
        process.WaitForExit();
        Assert.True(new DirectoryInfo(link).LinkTarget is not null, "the junction was made");
    }
}
