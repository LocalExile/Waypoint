using Waypoint;

namespace Waypoint.Tests;

public class ModBrowserTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "waypoint-mod-browser-" + Guid.NewGuid().ToString("N"));

    static ModEntry Installed(string name = "", string description = "") => new("bossloot", "1.0.0", "feature", [], false, "Not checked", "", "", "", "binary-hash", name, description);
    static BrowseItem Offer(ModEntry entry, string version = "1.0.0") => new(ModSource.Official,
        new("bossloot", "Boss Loot", "Repository description", "Repository author", version, "feature", true, "mods/bossloot", [], true), null, entry);

    [Fact]
    public void Friendly_presentation_uses_available_metadata_without_changing_the_mod_identity()
    {
        var entry = Installed(); var row = new InstalledRow(entry, Offer(entry));
        Assert.Equal("Boss Loot", row.Title);
        Assert.Equal("Repository description", row.Description);
        Assert.Contains("Repository author", row.Metadata);
        Assert.Contains("Boss Loot", row.SearchText);
        Assert.Equal("bossloot", row.Entry.Id);
        var named = Installed("Local name", "Local description");
        Assert.Equal("Local name", new InstalledRow(named, Offer(named)).Title);
        Assert.Equal("Local description", new InstalledRow(named, Offer(named)).Description);
        Assert.Equal("bossloot", new InstalledRow(entry, null).Title);
    }

    [Fact]
    public void Installed_packages_without_repository_evidence_do_not_claim_they_are_up_to_date()
    {
        var entry = Installed();
        Assert.Equal(L.Text("Installed"), new InstalledRow(entry, null).PackageStatus);
        Assert.Equal(L.Text("Up to date"), new InstalledRow(entry, Offer(entry)).PackageStatus);
        var update = new InstalledRow(entry, Offer(entry, "1.1.0"));
        Assert.Equal(L.Text("Update available"), update.PackageStatus);
        Assert.True(update.HasButton); Assert.False(update.ShowDetails);
        var running = update with { Job = new ModJob(entry.Id, "Installing") };
        Assert.False(running.HasButton); Assert.False(running.ShowDetails); Assert.False(running.CanToggle);
        Assert.True(new InstalledRow(entry, null).ShowDetails);
    }

    [Fact]
    public void Global_choices_distinguish_unavailable_disabled_and_saved_enable_choices()
    {
        var entry = Installed("Boss Loot") with { Enabled = true, Status = "Not checked" };
        Assert.Equal(L.Text("Mod information unavailable"), ModsText.GlobalChoices(null, [entry]));
        Assert.Equal(L.Text("Mod information unavailable"), ModsText.GlobalChoices(new(false, _root, true), [entry]));
        Assert.Equal(L.Text("Mods are off for this checkout"), ModsText.GlobalChoices(new(true, _root, false), [entry]));
        Assert.Equal(L.Text("No mods enabled"), ModsText.GlobalChoices(new(true, _root, true), [entry with { Enabled = false }]));
        Assert.Equal(L.Text("Saved enabled: {0}", "Boss Loot"), ModsText.GlobalChoices(new(true, _root, true), [entry]));
        var several = Enumerable.Range(1, 5).Select(i => entry with { Id = "mod" + i, Name = "Mod " + i }).ToArray();
        Assert.Equal(L.Text("Saved enabled: {0}", "Mod 1, Mod 2, Mod 3" + L.Text(" + {0} more", 2)), ModsText.GlobalChoices(new(true, _root, true), several));
        Assert.Contains("Mod 5", ModsText.GlobalChoices(new(true, _root, true), several, compact: false));
    }

    [Fact]
    public void Global_live_count_requires_an_explicit_report_and_is_always_labelled_last_checked()
    {
        var state = new RuntimeSnapshot(_root, "", "", "", "", "", [],
            [new("Gateway", "Ready", 123, "", "", [], 0, 0, "")], [], ModEnvironment: new(true, _root, true));
        Assert.Equal(L.Text("Live load: not checked"), ModsText.GlobalRuntime(state));
        var report = "bossloot 1.0.0 - feature, 1 hook(s)\nother - not loaded, disabled\nbroken - not loaded, dependency missing";
        var checkedState = state with { ModEnvironment = new(true, _root, true, report) };
        Assert.Equal(L.Text("Last checked: {0} loaded", 1), ModsText.GlobalRuntime(checkedState));
        Assert.Equal(L.Text("Last checked: {0} loaded", 1), ModsText.GlobalRuntime(checkedState with { Services = [] }));
        Assert.Equal(L.Text("Live load: not checked"), ModsText.GlobalRuntime(state with { Services = [] }));
        Assert.Empty(ModsText.GlobalRuntime(state with { ModEnvironment = null }));
        Assert.Empty(ModsText.GlobalRuntime(state with { ModEnvironment = new(false, _root, true, report) }));
    }

    [Fact]
    public void Saved_choices_do_not_supply_a_runtime_load_observation()
    {
        var state = new RuntimeSnapshot(_root, "", "", "", "", "", [], [], [], ModEnvironment: new(true, _root, true));
        Assert.Equal(L.Text("Not checked"), ModsText.RuntimeStatus(state, "bossloot"));
        Assert.Equal(L.Text("Not checked"), ModsText.RuntimeStatus(state with { ModEnvironment = new(true, _root, false) }, "bossloot"));
        var checkedState = state with { ModEnvironment = new(true, _root, true, "bossloot 1.0.0 - feature, 1 hook(s)") };
        Assert.Equal(L.Text("Loaded (last checked)"), ModsText.RuntimeStatus(checkedState, "bossloot"));
        Assert.Equal(L.Text("Not checked"), ModsText.RuntimeStatus(checkedState, "other"));
        var failed = state with { ModEnvironment = new(true, _root, true, "bossloot - not loaded, disabled") };
        Assert.Equal(L.Text("Not loaded (last checked)"), ModsText.RuntimeStatus(failed, "bossloot"));
    }

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
