using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Waypoint.Tests;

public sealed class LeagueCustomizationTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "waypoint-league-plans-" + Guid.NewGuid().ToString("N"));
    ContentEnvironment Environment => new(Path.Combine(_root, "Modules"), Path.Combine(_root, "Leagues"), Path.Combine(_root, "data/league-modules.json"), true, true, true);
    void Write(string path, string text)
    { path = Path.Combine(_root, path); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text); }
    void Profile(string name, string family, string modules, string flags = "")
    {
        Write($"Leagues/{name}/league.json", "{\"name\":\"" + name + "\",\"base\":\"" + family + "\",\"parent\":" + (name is "Standard" or "Void" ? "null" : "\"Void\"") + ",\"description\":\"Fixture.\"" + flags + "}");
        Write($"Leagues/{name}/modules.json", "{\"modules\":" + modules + "}");
        Write($"Leagues/{name}/Source/Fixture.cs", "// template\npublic sealed class FixtureLeague : League { }");
    }
    GameSystems Catalog()
    {
        Write("Modules/Core/Game/module.json", """{"id":"Game"}""");
        Write("Modules/Core/Vendors/module.json", """{"id":"Vendors","depends":["Game"]}""");
        Write("Modules/Core/Harvest/module.json", """{"id":"Harvest","depends":["Game"]}""");
        Write("Modules/Survivors/Run/module.json", """{"id":"Run","depends":["Game"]}""");
        Profile("Standard", "Standard", """["Core/Game","Core/Vendors","Core/Harvest"]""");
        Profile("SSF Standard", "Standard", """["Core/Game","Core/Vendors"]""", ",\"ssf\":true,\"entryOf\":\"Standard\",\"entryKind\":\"ssf\"");
        Profile("Survivors", "Survivors", """["Core/Game","Survivors/Run"]""");
        Profile("Void", "Void", "[]", ",\"creatable\":false");
        return new(Environment);
    }
    [Fact]
    public void Creation_copies_a_defined_mode_without_mutating_existing_identity()
    {
        var catalog = Catalog(); var template = catalog.Leagues.Single(l => l.Name == "SSF Standard");
        string before = File.ReadAllText(Path.Combine(template.Folder, "league.json"));
        var request = catalog.PlanCreate(template, "My Solo League", "Custom solo profile.", template.BaseSystems);
        Assert.Equal("SSF Standard", request["template"]!.GetValue<string>());
        Assert.DoesNotContain(catalog.Profiles(catalog.Leagues.Single(l => l.Name == "Survivors")), l => l.Ssf);
        Assert.Equal(before, File.ReadAllText(Path.Combine(template.Folder, "league.json")));
        Assert.False(Directory.Exists(Path.Combine(Environment.Leagues, "My Solo League")));
    }
    [Fact]
    public void Isolated_void_may_keep_its_core_game_foundation_but_no_gameplay_addons()
    {
        Catalog(); Write("Leagues/Void/modules.json", """{"modules":["Core/Game"]}""");
        var catalog = new GameSystems(Environment); var template = catalog.Leagues.Single(l => l.Name == "Standard");
        Assert.NotNull(catalog.PlanCreate(template, "My League", "Custom.", template.BaseSystems));
        Write("Leagues/Void/modules.json", """{"modules":["Core/Game","Core/Vendors"]}""");
        catalog = new(Environment); template = catalog.Leagues.Single(l => l.Name == "Standard");
        Assert.Throws<InvalidOperationException>(() => catalog.PlanCreate(template, "My League", "Custom.", template.BaseSystems));
    }
    [Fact]
    public void Family_mode_and_required_bundle_checks_block_unknown_combinations()
    {
        var catalog = Catalog(); var survivors = catalog.Leagues.Single(l => l.Name == "Survivors");
        Assert.False(catalog.ModuleOffered(survivors, "Core/Vendors"));
        Assert.Contains("Survivors/Run", catalog.RequiredModules(survivors));
        Assert.NotNull(catalog.CompositionError(survivors, ["Core/Game"]));
        Assert.NotNull(catalog.CompositionError(survivors, ["Core/Game", "Survivors/Run", "Core/Vendors"]));
        var ssf = catalog.Leagues.Single(l => l.Name == "SSF Standard");
        Assert.NotNull(catalog.CompositionError(ssf, ["Core/Game", "Core/Harvest"]));
        Assert.Null(catalog.CompositionError(catalog.Leagues.Single(l => l.Name == "Standard"), ["Core/Game", "Core/Vendors"]));
    }
    [Theory]
    [InlineData("../Other")]
    [InlineData("CON")]
    [InlineData(" League")]
    [InlineData("Two.")]
    [InlineData("Event\nLeague")]
    public void New_names_do_not_escape_or_alias_windows_paths(string name) => Assert.False(GameSystems.SafeLeagueName(name));
    [Fact]
    public void Custom_code_is_never_silently_cloned_and_review_tracks_source_changes()
    {
        var catalog = Catalog(); var template = catalog.Leagues.Single(l => l.Name == "Standard");
        Assert.True(GameSystems.EmptyTemplate(template.Folder));
        Write("Leagues/Standard/Source/Fixture.cs", "public sealed class Rules : League { public override string ToString() => \"custom\"; }");
        Assert.False(GameSystems.EmptyTemplate(template.Folder));
        Assert.Throws<InvalidOperationException>(() => catalog.PlanCreate(template, "My League", "Custom.", template.BaseSystems));
        Assert.False(JsonNode.DeepEquals(catalog.Review, GameSystems.CaptureReview(Environment)));
    }
    [Fact]
    public void Explicit_card_links_group_modes_without_hiding_custom_flags()
    {
        var catalog = Catalog();
        var standard = Assert.Single(catalog.Cards, f => f.Name == "Standard");
        Assert.Equal("Standard", standard.Primary.Name);
        Assert.Contains(standard.Variants, l => l.Ssf);
        Assert.DoesNotContain(catalog.Cards, f => f.Name == "SSF Standard");
        Write("Modules/Phrecia/Classes/module.json", """{"id":"Classes","depends":["Game"]}""");
        Profile("Phrecia", "Standard", """["Core/Game","Phrecia/Classes"]""");
        catalog = new(Environment);
        Assert.Contains(catalog.Cards, f => f.Name == "Phrecia");
        Assert.False(catalog.ModuleOffered(catalog.Leagues.Single(l => l.Name == "Standard"), "Phrecia/Classes"));
        Profile("My Solo League", "Standard", """["Core/Game","Core/Vendors"]""", ",\"ssf\":true");
        Write("Leagues/My Solo League/.waypoint-league.json", """{"version":1,"template":"SSF Standard"}""");
        catalog = new(Environment);
        var custom = Assert.Single(catalog.Cards, f => f.Name == "My Solo League");
        Assert.True(Assert.Single(custom.Variants).Ssf);
        Assert.DoesNotContain(catalog.Cards.Single(f => f.Name == "Standard").Variants, l => l.Custom);
    }
    [Fact]
    public void Five_player_flags_do_not_merge_a_hardcore_flag_with_its_death_destination()
    {
        Catalog();
        Profile("Allflame", "Allflame", """["Core/Game"]""");
        Profile("Hardcore Allflame", "Allflame", """["Core/Game"]""", ",\"hardcore\":true");
        Write("Leagues/Hardcore Allflame/league.json", """{"name":"Hardcore Allflame","parent":"Allflame","base":"Allflame","hardcore":true}""");
        Profile("Hardcore", "Standard", """["Core/Game"]""", ",\"hardcore\":true,\"entryOf\":\"Standard\",\"entryKind\":\"hardcore\"");
        Profile("Phrecia", "Standard", """["Core/Game"]""");
        var catalog = new GameSystems(Environment);
        Assert.Equal(new[] { "Allflame", "Hardcore Allflame", "Phrecia", "Standard", "Survivors" }, catalog.Cards.Where(c => c.Visible).Select(c => c.Name).Order());
        Assert.DoesNotContain(catalog.Cards.Single(c => c.Name == "Allflame").Variants, l => l.Hardcore);
        Assert.Contains(catalog.Cards.Single(c => c.Name == "Standard").Variants, l => l.Name == "Hardcore");
        // Compatibility grouping is deliberately independent of the client flag graph.
        Assert.Equal("Allflame", catalog.SourceFamilyName(catalog.Leagues.Single(l => l.Name == "Hardcore Allflame")));
    }
    [Fact]
    public void Explicit_mode_links_determine_presentation_even_with_a_different_game_base()
    {
        Catalog();
        Profile("Other rules", "Other base", """["Core/Game"]""", ",\"entryOf\":\"Standard\",\"entryKind\":\"form\",\"form\":\"ruthless\"");
        var catalog = new GameSystems(Environment); var mode = catalog.Leagues.Single(l => l.Name == "Other rules");
        Assert.Equal("Other rules", catalog.SourceFamilyName(mode));
        Assert.Equal("Standard", catalog.FlagOf(mode)!.Name);
        Assert.True(catalog.ReachableMode(mode));
        Assert.DoesNotContain(catalog.Cards, c => c.Name == mode.Name);
    }
    [Fact]
    public void Mode_option_paths_follow_explicit_nested_links_and_hidden_ancestors()
    {
        Catalog();
        Profile("Hardcore", "Standard", """["Core/Game"]""", ",\"hardcore\":true,\"entryOf\":\"Standard\",\"entryKind\":\"hardcore\"");
        Profile("SSF Hardcore", "Standard", """["Core/Game"]""", ",\"hardcore\":true,\"ssf\":true,\"entryOf\":\"Hardcore\",\"entryKind\":\"ssf\"");
        var catalog = new GameSystems(Environment);
        var combined = catalog.Leagues.Single(l => l.Name == "SSF Hardcore");
        Assert.Equal("Standard", catalog.FlagOf(combined)!.Name);
        Assert.Equal("Hardcore → Solo self-found", catalog.ModePath(combined));
        Assert.True(catalog.ReachableMode(combined));
        Write("Leagues/Hardcore/league.json", """{"name":"Hardcore","parent":"Standard","entryOf":"Standard","entryKind":"hardcore","hardcore":true,"creatable":false}""");
        catalog = new(Environment); combined = catalog.Leagues.Single(l => l.Name == "SSF Hardcore");
        Assert.True(catalog.OfferedDefinition(combined)); // Sent by the server, but its checkbox path cannot be reached.
        Assert.False(catalog.ReachableMode(combined));
        Assert.Contains(catalog.Cards.Single(c => c.Name == "Standard").Variants, l => l == combined);
    }
    [Fact]
    public void Broken_and_ambiguous_mode_links_are_never_promoted_to_player_flags()
    {
        Catalog();
        Profile("Orphan", "Standard", """["Core/Game"]""", ",\"entryOf\":\"Missing\",\"entryKind\":\"ssf\"");
        Profile("Cycle A", "Standard", """["Core/Game"]""", ",\"entryOf\":\"Cycle B\",\"entryKind\":\"ssf\"");
        Profile("Cycle B", "Standard", """["Core/Game"]""", ",\"entryOf\":\"Cycle A\",\"entryKind\":\"form\"");
        Profile("Empty link", "Standard", """["Core/Game"]""", ",\"entryOf\":\"\",\"entryKind\":\"ssf\"");
        Profile("Unknown kind", "Standard", """["Core/Game"]""", ",\"entryOf\":\"Standard\",\"entryKind\":\"unknown\"");
        Profile("Duplicate SSF", "Standard", """["Core/Game"]""", ",\"entryOf\":\"Standard\",\"entryKind\":\"ssf\"");
        var catalog = new GameSystems(Environment);
        Assert.Equal(new[] { "Standard", "Survivors" }, catalog.Cards.Where(c => c.Visible).Select(c => c.Name));
        foreach (string name in new[] { "Orphan", "Cycle A", "Cycle B", "Empty link", "Unknown kind", "Duplicate SSF", "SSF Standard" })
            Assert.False(catalog.ReachableMode(catalog.Leagues.Single(l => l.Name == name)));
        Assert.Contains(catalog.Cards, c => c.Name == "Empty link" && !c.IsFlag && !c.Visible);
    }
    [Fact]
    public void Offered_flags_follow_source_loading_visibility_and_order_not_module_editability()
    {
        Catalog();
        Profile("Ended", "Standard", "[]", ",\"ended\":true");
        Profile("Hidden", "Standard", "[]", ",\"creatable\":false");
        Profile("No Source", "Standard", "[]"); Directory.Delete(Path.Combine(_root, "Leagues/No Source/Source"), true);
        Profile("Broken", "Standard", "[]"); Write("Leagues/Broken/league.json", """{"name":"Broken","parent":"Missing"}""");
        Profile("No Game", "Standard", "[]"); // The server's card listing does not check gameplay module editability.
        Profile("Binary", "Standard", "[]"); Directory.Delete(Path.Combine(_root, "Leagues/Binary/Source"), true);
        Directory.CreateDirectory(Path.Combine(_root, "Leagues/Binary/Binaries"));
        Write("Leagues/Binary/league.json", """{"name":"Binary","parent":"Standard","description":""}""");
        var catalog = new GameSystems(Environment);
        foreach (string name in new[] { "Ended", "Hidden", "No Source", "Broken", "Void" }) Assert.False(catalog.Cards.Single(c => c.Name == name).Visible);
        Assert.True(catalog.Cards.Single(c => c.Name == "No Game").Visible);
        Assert.Equal("The Binary league.", catalog.Leagues.Single(l => l.Name == "Binary").Description);
        var expected = Directory.GetDirectories(Environment.Leagues).Select(Path.GetFileName).Where(n => n is "Standard" or "Survivors" or "No Game" or "Binary")
            .OrderBy(n => n == "Standard" ? 0 : 1);
        Assert.Equal(expected, catalog.Cards.Where(c => c.Visible).Select(c => c.Name));
    }
    [Fact]
    public void Visibility_updates_linked_variants_and_preserves_a_creation_card()
    {
        var catalog = Catalog(); var standard = catalog.Leagues.Single(l => l.Name == "Standard");
        Write("Leagues/SSF Standard/league.json", """{"name":"SSF Standard","base":"Standard","parent":"Standard","ssf":true,"entryOf":"Standard","entryKind":"ssf"}""");
        catalog = new(Environment); standard = catalog.Leagues.Single(l => l.Name == "Standard");
        Assert.Equal(new[] { "SSF Standard", "Standard" }, catalog.VisibilityPlan(standard, false).Select(l => l.Name));
        Write("Leagues/Survivors/league.json", """{"name":"Survivors","base":"Survivors","parent":"Void","creatable":false}""");
        catalog = new(Environment); standard = catalog.Leagues.Single(l => l.Name == "Standard");
        Assert.Throws<InvalidOperationException>(() => catalog.VisibilityPlan(standard, false));
    }
    [Fact]
    public async Task Frontend_and_authoritative_adapter_review_the_same_files()
    {
        var catalog = Catalog();
        var start = new ProcessStartInfo(Platform.Python) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in new[] { Path.Combine(AppContext.BaseDirectory, "LeagueData.py"), Environment.Modules, Environment.Leagues, Environment.Choices,
            Path.Combine(_root, "data/saves.db"), Path.Combine(_root, "history"), "Inspect", "check" }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        await process.StandardInput.WriteLineAsync("{}"); process.StandardInput.Close();
        string output = await process.StandardOutput.ReadToEndAsync();
        string error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, error + output);
        Assert.True(JsonNode.DeepEquals(catalog.Review, JsonNode.Parse(output)!["Review"]));
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
