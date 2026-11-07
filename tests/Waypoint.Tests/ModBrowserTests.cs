using Waypoint;

namespace Waypoint.Tests;

public class ModBrowserTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "waypoint-mod-browser-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Declining_preparation_ends_the_job_without_running_or_retrying_it()
    {
        int prompts = 0, runs = 0, waits = 0;
        var result = await ModBrowser.WaitForWorkspaceAsync(async () => await Workspace.RunPreparedAsync(
            () => { prompts++; return Task.FromResult(false); },
            () => { runs++; return Task.FromResult(new BridgeResult(true, "done", null)); }),
            _ => { }, () => { waits++; throw new Exception("Cancellation must not retry"); });
        Assert.True(result!.Cancelled);
        Assert.False(result.Ok);
        Assert.Equal(1, prompts);
        Assert.Equal(0, runs);
        Assert.Equal(0, waits);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Busy_workspace_waits_but_a_completed_operation_is_not_repeated(bool ok)
    {
        int attempts = 0, waits = 0, runs = 0;
        var expected = new BridgeResult(ok, ok ? "done" : "build failed", null);
        var result = await ModBrowser.WaitForWorkspaceAsync(async () =>
        {
            if (++attempts < 3) return null;
            return await Workspace.RunPreparedAsync(() => Task.FromResult(true),
                () => { runs++; return Task.FromResult(expected); });
        }, _ => { }, () => { waits++; return Task.CompletedTask; });
        Assert.Same(expected, result);
        Assert.Equal(3, attempts);
        Assert.Equal(2, waits);
        Assert.Equal(1, runs);
    }

    [Fact]
    public async Task Failed_preparation_does_not_start_or_retry_the_operation()
    {
        int attempts = 0;
        await Assert.ThrowsAsync<IOException>(() => ModBrowser.WaitForWorkspaceAsync(async () =>
        {
            attempts++;
            return await Workspace.RunPreparedAsync(() => throw new IOException("preparation failed"),
                () => throw new Exception("Operation must not start"));
        }, _ => { }));
        Assert.Equal(1, attempts);
    }

    [Fact]
    public void Older_and_unknown_running_servers_show_a_restart_notice()
    {
        var state = new RuntimeSnapshot(_root, "", "", "", "", "", [],
            [new("Gateway", "Ready", 123, "", "", [], 0, 0, "")], [], ModEnvironment: new(true, _root, true));
        Assert.NotEmpty(ModsText.ChoicesNotice(state)); // release/no source
        Assert.Empty(ModsText.ChoicesNotice(state with { Services = [] }));
        Assert.Empty(ModsText.ChoicesNotice(state with { ModEnvironment = null }));
        string host = Path.Combine(_root, "src", "GameServer.Gateway", "Modding", "ModHost.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(host)!);
        File.WriteAllText(host, "if (id is StateFile) return;");
        Assert.NotEmpty(ModsText.ChoicesNotice(state));
        File.WriteAllText(host, "else if (id == StateFile) busy = !ChoicesChanged();");
        Assert.Empty(ModsText.ChoicesNotice(state));
        File.WriteAllText(host, new string('x', 128 * 1024 + 1));
        Assert.NotEmpty(ModsText.ChoicesNotice(state)); // unreadable/oversized source stays conservative
    }

    [Fact]
    public void Saving_a_choice_preserves_other_choices_and_does_not_claim_live_state()
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, "mod-state.txt");
        File.WriteAllText(path, "alpha off\nbeta on\n");
        ModCatalog.WriteChoice(_root, "alpha", true);
        var choices = ModCatalog.ReadChoices(_root);
        Assert.True(choices["alpha"]);
        Assert.True(choices["beta"]);
        ModCatalog.WriteChoice(_root, "alpha", false);
        Assert.False(ModCatalog.ReadChoices(_root)["alpha"]);
        Assert.Single(File.ReadAllLines(path), l => l.StartsWith("alpha "));
        Assert.Throws<ArgumentException>(() => ModCatalog.WriteChoice(_root, "../escape", true));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
}
