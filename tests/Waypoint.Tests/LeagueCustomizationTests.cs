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
    public void League_families_group_modes_without_hiding_custom_leagues()
    {
        var catalog = Catalog();
        var standard = Assert.Single(catalog.Families, f => f.Name == "Standard");
        Assert.Equal("Standard", standard.Primary.Name);
        Assert.Contains(standard.Variants, l => l.Ssf);
        Assert.DoesNotContain(catalog.Families, f => f.Name == "SSF Standard");
        Write("Modules/Phrecia/Classes/module.json", """{"id":"Classes","depends":["Game"]}""");
        Profile("Phrecia", "Standard", """["Core/Game","Phrecia/Classes"]""");
        catalog = new(Environment);
        Assert.Contains(catalog.Families, f => f.Name == "Phrecia");
        Assert.False(catalog.ModuleOffered(catalog.Leagues.Single(l => l.Name == "Standard"), "Phrecia/Classes"));
        Profile("My Solo League", "Standard", """["Core/Game","Core/Vendors"]""", ",\"ssf\":true");
        Write("Leagues/My Solo League/.waypoint-league.json", """{"version":1,"template":"SSF Standard"}""");
        catalog = new(Environment);
        var custom = Assert.Single(catalog.Families, f => f.Name == "My Solo League");
        Assert.True(Assert.Single(custom.Variants).Ssf);
        Assert.DoesNotContain(catalog.Families.Single(f => f.Name == "Standard").Variants, l => l.Custom);
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
