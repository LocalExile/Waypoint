using System.Text.Json.Nodes;

namespace Waypoint.Tests;

public class WaypointToolsModTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("waypoint-tools-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    string Data(params string[] parts) => Path.Combine([_root, "data", WaypointToolsMod.Id, .. parts]);

    void WriteLoaded(string json)
    {
        Directory.CreateDirectory(Data());
        File.WriteAllText(Data("loaded.json"), json);
    }

    [Fact]
    public void The_mod_counts_as_loaded_only_in_a_running_Gateway_with_the_action()
    {
        Assert.False(WaypointToolsMod.Loaded(_root, "item_give", _ => true));
        WriteLoaded("""{"protocol":1,"version":"1.0.0","pid":42,"actions":["item_give"]}""");
        Assert.True(WaypointToolsMod.Loaded(_root, "item_give", pid => pid == 42));
        // Left by a Gateway that stopped.
        Assert.False(WaypointToolsMod.Loaded(_root, "item_give", _ => false));
        Assert.False(WaypointToolsMod.Loaded(_root, "teleport", _ => true));
        WriteLoaded("""{"protocol":2,"pid":42,"actions":["item_give"]}""");
        Assert.False(WaypointToolsMod.Loaded(_root, "item_give", _ => true));
        WriteLoaded("not json");
        Assert.False(WaypointToolsMod.Loaded(_root, "item_give", _ => true));
    }

    [Fact]
    public void Installed_means_its_folder_has_a_manifest()
    {
        Assert.False(WaypointToolsMod.Installed(_root));
        Directory.CreateDirectory(Path.Combine(_root, "Mods", "Binaries", WaypointToolsMod.Id));
        File.WriteAllText(Path.Combine(_root, "Mods", "Binaries", WaypointToolsMod.Id, "mod.json"), "{}");
        Assert.True(WaypointToolsMod.Installed(_root));
    }

    [Fact]
    public async Task An_item_goes_as_a_whole_request_and_its_reply_is_read_and_removed()
    {
        // The mod's side: the request in place (never the .tmp), then the reply.
        var mod = Task.Run(async () =>
        {
            string requests = Data("requests");
            while (!Directory.Exists(requests) || Directory.GetFiles(requests, "*.json").Length == 0) await Task.Delay(20);
            string file = Directory.GetFiles(requests, "*.json").Single();
            var request = (JsonObject)JsonNode.Parse(File.ReadAllText(file))!;
            string id = Path.GetFileNameWithoutExtension(file);
            Directory.CreateDirectory(Data("replies"));
            File.WriteAllText(Data("replies", id + ".json"), $$"""{"protocol":1,"id":"{{id}}","ok":true,"message":"Added Iron Ring"}""");
            File.Delete(file);
            return request;
        });
        var reply = await WaypointToolsMod.GiveItemAsync(_root, """{"base":"Metadata/Items/Rings/Ring1"}""", character: null, TimeSpan.FromSeconds(10));
        var request = await mod;
        Assert.Equal(new ToolsReply(true, "Added Iron Ring"), reply);
        Assert.Equal("item_give", (string?)request["action"]);
        // No character: the mod gives it to the one in game.
        Assert.False(request.ContainsKey("character"));
        Assert.Equal("Metadata/Items/Rings/Ring1", (string?)request["item"]?["base"]);
        Assert.Empty(Directory.GetFiles(Data("replies")));
    }

    [Fact]
    public async Task A_named_character_goes_with_the_request()
    {
        var reply = WaypointToolsMod.GiveItemAsync(_root, """[{"base":"Iron Ring"}]""", "Exile", TimeSpan.FromSeconds(10));
        string requests = Data("requests");
        while (!Directory.Exists(requests) || Directory.GetFiles(requests, "*.json").Length == 0) await Task.Delay(20);
        string file = Directory.GetFiles(requests, "*.json").Single();
        var request = (JsonObject)JsonNode.Parse(File.ReadAllText(file))!;
        Assert.Equal("Exile", (string?)request["character"]);
        Assert.IsType<JsonArray>(request["item"]);
        Directory.CreateDirectory(Data("replies"));
        File.WriteAllText(Data("replies", Path.GetFileName(file)), """{"ok":false,"message":"Exile is not in game"}""");
        Assert.Equal(new ToolsReply(false, "Exile is not in game"), await reply);
    }

    [Fact]
    public async Task A_request_nobody_answers_is_taken_back()
    {
        var reply = await WaypointToolsMod.GiveItemAsync(_root, """{"base":"Iron Ring"}""", null, TimeSpan.FromMilliseconds(300));
        Assert.False(reply.Ok);
        // Not left for a Gateway that starts later to give long after it was asked for.
        Assert.Empty(Directory.GetFiles(Data("requests")));
    }
}
