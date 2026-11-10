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
    public void Any_module_goes_with_any_league_but_one_made_for_an_isolated_league()
    {
        Catalog();
        // A league that leads to Standard, with a module of its own, as Ruthless and Phrecia are.
        Write("Modules/Ruthless/module.json", """{"id":"Ruthless","depends":["Game"]}""");
        Write("Leagues/Ruthless/league.json", """{"name":"Ruthless","base":"Standard","parent":"Standard","form":"ruthless","description":"Fixture."}""");
        Write("Leagues/Ruthless/modules.json", """{"modules":["Core/Game","Core/Vendors","Ruthless"]}""");
        Write("Leagues/Ruthless/Source/Fixture.cs", "public sealed class RuthlessLeague : League { }");
        var catalog = new GameSystems(Environment);
        var standard = catalog.Leagues.Single(l => l.Name == "Standard"); var survivors = catalog.Leagues.Single(l => l.Name == "Survivors");
        var ruthless = catalog.Leagues.Single(l => l.Name == "Ruthless");
        Assert.False(catalog.Isolated(standard));
        Assert.False(catalog.Isolated(ruthless));
        Assert.True(catalog.Isolated(survivors));

        // A core module the league does not ship with, and another league's own module: a module has no knowledge of
        // leagues, and any league can list it.
        Assert.True(catalog.ModuleOffered(ruthless, "Core/Harvest"));
        Assert.Null(catalog.CompositionError(ruthless, ["Core/Game", "Core/Vendors", "Ruthless", "Core/Harvest"]));
        Assert.True(catalog.ModuleOffered(standard, "Ruthless"));
        Assert.Null(catalog.CompositionError(standard, ["Core/Game", "Core/Vendors", "Ruthless"]));
        Assert.True(catalog.ModuleOffered(survivors, "Core/Vendors"));
        Assert.Null(catalog.CompositionError(survivors, ["Core/Game", "Survivors/Run", "Core/Vendors", "Ruthless"]));

        // A module LocalExile ships only in an isolated league stays out of the leagues that lead to Standard...
        Assert.True(catalog.IsolatedModule("Survivors/Run"));
        Assert.False(catalog.IsolatedModule("Ruthless"));
        Assert.False(catalog.IsolatedModule("Core/Game"));
        Assert.False(catalog.ModuleOffered(standard, "Survivors/Run"));
        Assert.False(catalog.ModuleOffered(ruthless, "Survivors/Run"));
        Assert.NotNull(catalog.CompositionError(standard, ["Core/Game", "Core/Vendors", "Survivors/Run"]));
        // ...and goes with a new league, which is isolated whatever it is made from.
        Assert.Null(catalog.CompositionError(standard, ["Core/Game", "Core/Vendors", "Survivors/Run"], forNewLeague: true));
        Assert.NotNull(catalog.PlanCreate(standard, "My Event", "Custom.", ["Core/Game", "Core/Vendors", "Survivors/Run"]));

        // The game is what LocalExile requires, and the one box that stays locked.
        Assert.Equal(["Core/Game"], catalog.RequiredModules(ruthless));
        Assert.Equal(["Core/Game"], catalog.RequiredModules(survivors));
        Assert.NotNull(catalog.CompositionError(standard, ["Core/Vendors"]));
        Assert.Null(catalog.CompositionError(standard, ["Core/Game", "Core/Vendors"]));
    }
    [Fact]
    public void A_league_can_run_without_its_own_bundle_and_its_user_is_told_what_is_left_out()
    {
        Catalog();
        Write("Modules/Ruthless/module.json", """{"id":"Ruthless","depends":["Game"]}""");
        Write("Leagues/Ruthless/league.json", """{"name":"Ruthless","base":"Standard","parent":"Standard","form":"ruthless","description":"Fixture."}""");
        Write("Leagues/Ruthless/modules.json", """{"modules":["Core/Game","Core/Vendors","Ruthless"]}""");
        Write("Leagues/Ruthless/Source/Fixture.cs", "public sealed class RuthlessLeague : League { }");
        var catalog = new GameSystems(Environment);
        var standard = catalog.Leagues.Single(l => l.Name == "Standard"); var survivors = catalog.Leagues.Single(l => l.Name == "Survivors");
        var ruthless = catalog.Leagues.Single(l => l.Name == "Ruthless");

        // The bundle: what the league's shipped list names outside Core. Standard has none.
        Assert.Equal(["Ruthless"], catalog.BundleModules(ruthless));
        Assert.Equal(["Survivors/Run"], catalog.BundleModules(survivors));
        Assert.Empty(catalog.BundleModules(standard));

        // LocalExile loads the league without it and keeps its saved data, so the choice is allowed...
        Assert.Null(catalog.CompositionError(ruthless, ["Core/Game", "Core/Vendors"]));
        Assert.Null(catalog.CompositionError(survivors, ["Core/Game"]));
        var plan = catalog.Plan(ruthless, ["Core/Game", "Core/Vendors"]);
        Assert.Equal(["Ruthless"], plan["Ruthless"]!["remove"]!.AsArray().Select(n => n!.GetValue<string>()));
        // ...and what is left out is named, for the interface to say before it saves.
        Assert.Equal("Ruthless", Assert.Single(catalog.BundleLeftOut(ruthless, ["Core/Game", "Core/Vendors"])).Path);
        Assert.Empty(catalog.BundleLeftOut(ruthless, ["Core/Game", "Ruthless"]));
        Assert.Empty(catalog.BundleLeftOut(standard, ["Core/Game"]));
        // A league made from one, with that league's mode: the same is said about the new league.
        Assert.NotNull(catalog.PlanCreate(ruthless, "My Event", "Custom.", ["Core/Game", "Core/Vendors"]));
        Assert.Single(catalog.BundleLeftOut(ruthless, ["Core/Game", "Core/Vendors"]));
    }
    [Fact]
    public void A_league_that_lists_a_module_that_is_not_installed_can_still_be_edited()
    {
        // A typo in a hand edit, or a module taken away since: LocalExile logs it and loads the league without it.
        Catalog(); Write("Leagues/Standard/modules.json", """{"modules":["Core/Game","Core/Vendors","Core/Harvest","PlayerBot"]}""");
        var catalog = new GameSystems(Environment); var standard = catalog.Leagues.Single(l => l.Name == "Standard");
        Assert.DoesNotContain("PlayerBot", catalog.BundleModules(standard));

        // Another change goes through with the entry left as it is, and the entry itself can be taken off the list.
        var plan = catalog.Plan(standard, ["Core/Game", "Core/Vendors", "PlayerBot"]);
        Assert.Equal(["Core/Harvest"], plan["Standard"]!["remove"]!.AsArray().Select(n => n!.GetValue<string>()));
        plan = catalog.Plan(standard, ["Core/Game", "Core/Vendors", "Core/Harvest"]);
        Assert.Equal(["PlayerBot"], plan["Standard"]!["remove"]!.AsArray().Select(n => n!.GetValue<string>()));

        // One that is not installed and new here is refused, and a new league starts from installed modules only.
        Assert.NotNull(catalog.CompositionError(standard, ["Core/Game", "Core/Vendors", "PlayerBot", "Nowhere"]));
        Assert.NotNull(catalog.CompositionError(standard, standard.BaseSystems, forNewLeague: true));
        Assert.Null(catalog.CompositionError(standard, ["Core/Game", "Core/Vendors", "Core/Harvest"], forNewLeague: true));
    }
    [Fact]
    public void A_release_hides_and_shows_only_the_leagues_made_in_Waypoint()
    {
        Catalog();
        Profile("My Event", "Standard", """["Core/Game","Core/Vendors"]""");
        Write("Leagues/My Event/.waypoint-league.json", """{"version":1,"template":"Standard"}""");
        // A release's own leagues are its files, which an update writes over.
        var release = new GameSystems(Environment with { ShippedLeaguesFixed = true });
        Assert.Throws<InvalidOperationException>(() => release.VisibilityPlan(release.Leagues.Single(l => l.Name == "Survivors"), visible: false));
        Assert.Equal(["My Event"], release.VisibilityPlan(release.Leagues.Single(l => l.Name == "My Event"), visible: false).Select(l => l.Name));
        // A source copy's leagues are its user's to change.
        var source = new GameSystems(Environment);
        Assert.Equal(["Survivors"], source.VisibilityPlan(source.Leagues.Single(l => l.Name == "Survivors"), visible: false).Select(l => l.Name));
    }
    [Fact]
    public void A_module_no_shipped_league_lists_is_an_add_on_for_any_league()
    {
        Catalog(); Write("Modules/PlayerBots/module.json", """{"id":"PlayerBots","depends":["Game"]}""");
        var catalog = new GameSystems(Environment);
        var standard = catalog.Leagues.Single(l => l.Name == "Standard"); var survivors = catalog.Leagues.Single(l => l.Name == "Survivors");
        Assert.True(catalog.AddOn("PlayerBots"));
        Assert.False(catalog.AddOn("Survivors/Run"));
        Assert.False(catalog.AddOn("Core/Harvest"));

        // Offered everywhere, on top of each league's own combination, and never required afterwards.
        Assert.True(catalog.ModuleOffered(standard, "PlayerBots"));
        Assert.True(catalog.ModuleOffered(survivors, "PlayerBots"));
        Assert.Null(catalog.CompositionError(standard, ["Core/Game", "Core/Vendors", "Core/Harvest", "PlayerBots"]));
        Assert.Null(catalog.CompositionError(survivors, ["Core/Game", "Survivors/Run", "PlayerBots"]));
        Assert.DoesNotContain("PlayerBots", catalog.BundleModules(standard));

        // It opens no door to a module made for an isolated league, and its own dependencies still count.
        Assert.False(catalog.ModuleOffered(standard, "Survivors/Run"));
        Assert.NotNull(catalog.CompositionError(standard, ["Core/Game", "Core/Vendors", "PlayerBots", "Survivors/Run"]));
        Assert.NotNull(catalog.CompositionError(standard, ["Core/Vendors", "PlayerBots"]));
        Write("Modules/Needy/module.json", """{"id":"Needy","depends":["Harvest"]}""");
        catalog = new(Environment);
        var ssf = catalog.Leagues.Single(l => l.Name == "SSF Standard");
        Assert.True(catalog.AddOn("Needy"));
        Assert.NotNull(catalog.CompositionError(ssf, ["Core/Game", "Core/Vendors", "Needy"]));
        standard = catalog.Leagues.Single(l => l.Name == "Standard");
        Assert.Null(catalog.CompositionError(standard, ["Core/Game", "Core/Vendors", "Core/Harvest", "Needy"]));

        // The plan is the user's own addition in data/league-modules.json; what LocalExile ships stays as it is.
        string shipped = File.ReadAllText(Path.Combine(standard.Folder, "modules.json"));
        var plan = catalog.Plan(standard, [.. standard.BaseSystems, "PlayerBots"]);
        Assert.Equal(["PlayerBots"], plan["Standard"]!["add"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Empty(plan["Standard"]!["remove"]!.AsArray());
        Assert.Equal(shipped, File.ReadAllText(Path.Combine(standard.Folder, "modules.json")));

        // Added by its user, it is still an add-on: unticking it is as free as ticking it was.
        Write("data/league-modules.json", plan.ToJsonString());
        catalog = new(Environment); standard = catalog.Leagues.Single(l => l.Name == "Standard");
        Assert.Contains("PlayerBots", standard.Systems);
        Assert.True(catalog.AddOn("PlayerBots"));
        Assert.Empty(catalog.Plan(standard, standard.BaseSystems)["Standard"]!["add"]!.AsArray());

        // A league made in Waypoint that lists it does not make it LocalExile's own combination.
        Profile("My Bots League", "Standard", """["Core/Game","Core/Vendors","PlayerBots"]""");
        Write("Leagues/My Bots League/.waypoint-league.json", """{"version":1,"template":"Standard"}""");
        catalog = new(Environment);
        Assert.True(catalog.AddOn("PlayerBots"));
        Assert.True(catalog.ModuleOffered(catalog.Leagues.Single(l => l.Name == "Survivors"), "PlayerBots"));
        // Nor is it part of that league's bundle: unticking it there says nothing about rules or content.
        var made = catalog.Leagues.Single(l => l.Name == "My Bots League");
        Assert.DoesNotContain("PlayerBots", catalog.BundleModules(made));
        Assert.Null(catalog.CompositionError(made, ["Core/Game", "Core/Vendors"]));
        Assert.Empty(catalog.BundleLeftOut(made, ["Core/Game", "Core/Vendors"]));

        // Once a league LocalExile ships lists it, it is part of that league's own bundle there.
        Profile("Bots", "Bots", """["Core/Game","PlayerBots"]""");
        catalog = new(Environment);
        Assert.False(catalog.AddOn("PlayerBots"));
        Assert.Contains("PlayerBots", catalog.BundleModules(catalog.Leagues.Single(l => l.Name == "Bots")));
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
