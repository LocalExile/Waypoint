using System.Text.Json.Nodes;

namespace Waypoint.Tests;

public class GameSystemsTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "waypoint-systems-" + Guid.NewGuid().ToString("N"));
    void Write(string path, string text)
    { path = Path.Combine(_root, path); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text); }
    GameSystems Catalog(bool supported = true)
    {
        Write("Modules/Core/Game/module.json", """{"id":"Game"}""");
        Write("Modules/Core/Vendors/module.json", """{"id":"Vendors","depends":["Game"]}""");
        Write("Modules/Extra/module.json", """{"id":"Extra","depends":["Vendors"]}""");
        Write("Leagues/Standard/league.json", """{"name":"Standard"}""");
        Write("Leagues/Standard/modules.json", """{"modules":["Core/Game","Core/Vendors"]}""");
        Write("Leagues/Ruthless/league.json", """{"name":"Ruthless","parent":"Standard"}""");
        Write("Leagues/Ruthless/modules.json", """{"modules":["Core/Game"]}""");
        Write("Leagues/Addon/league.json", """{"name":"Addon","base":"Standard","parent":"Standard"}""");
        Write("Leagues/Addon/modules.json", """{"modules":["Core/Game","Core/Vendors","Extra"]}""");
        return new(new(Path.Combine(_root, "Modules"), Path.Combine(_root, "Leagues"), Path.Combine(_root, "data", "league-modules.json"), supported));
    }
    [Fact]
    public void League_plans_preserve_other_leagues_and_shipped_files()
    {
        Write("data/league-modules.json", """{"Ruthless":{"add":["Core/Vendors"],"remove":[]},"Future":{"add":[],"remove":[]}}""");
        var catalog = Catalog();
        Assert.Contains("Core/Vendors", catalog.Leagues.Single(l => l.Name == "Ruthless").Systems);
        var original = File.ReadAllText(Path.Combine(_root, "Leagues/Standard/modules.json"));
        var planned = catalog.Plan(catalog.Leagues.Single(l => l.Name == "Standard"), ["Core/Game"]);
        Assert.Equal("Core/Vendors", planned["Standard"]!["remove"]![0]!.GetValue<string>());
        Assert.NotNull(planned["Future"]); Assert.NotNull(planned["Ruthless"]);
        var edited = catalog.Plan(catalog.Leagues.Single(l => l.Name == "Ruthless"), ["Core/Game"]);
        Assert.NotNull(edited["Future"]);
        Assert.Empty((JsonArray)edited["Ruthless"]!["add"]!);
        Assert.Equal(original, File.ReadAllText(Path.Combine(_root, "Leagues/Standard/modules.json")));
    }
    [Fact]
    public void League_cards_read_names_with_spaces_and_existing_mode_metadata()
    {
        Write("Leagues/SSF Ruthless/league.json", """{"name":"SSF Ruthless","ssf":true,"form":"ruthless","parent":"Standard","entryOf":"Standard","entryKind":"form","description":"A custom card."}""");
        Write("Leagues/SSF Ruthless/modules.json", """{"modules":["Core/Game"]}""");
        var league = Catalog().Leagues.Single(l => l.Name == "SSF Ruthless");
        Assert.True(league.Ssf); Assert.Equal("ruthless", league.Form);
        Assert.Equal("Standard", league.EntryOf); Assert.Equal("A custom card.", league.Description);
        Assert.EndsWith("SSF Ruthless.json", ModSettingsForm.UserFile("SSF Ruthless", "leagues"));
    }
    [Fact]
    public void Malformed_existing_choices_block_edits_without_replacing_the_file()
    {
        const string invalid = """{"Standard":{"add":"Core/Vendors","remove":[]}}""";
        Write("data/league-modules.json", invalid);
        Assert.Throws<InvalidDataException>(() => Catalog());
        Assert.Equal(invalid, File.ReadAllText(Path.Combine(_root, "data/league-modules.json")));
    }
    [Fact]
    public void Core_dependencies_missing_modules_and_unsupported_servers_block_changes()
    {
        var catalog = Catalog(); var league = catalog.Leagues.Single(l => l.Name == "Standard");
        Assert.Throws<InvalidOperationException>(() => catalog.Plan(league, ["Core/Vendors"]));
        Assert.Throws<InvalidOperationException>(() => catalog.Plan(league, ["Core/Game", "Extra"]));
        Assert.Throws<InvalidOperationException>(() => catalog.Plan(league, ["Core/Game", "Missing"]));
        var old = Catalog(false);
        Assert.Throws<InvalidOperationException>(() => old.Plan(old.Leagues[0], ["Core/Game"]));
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
