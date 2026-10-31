using System.Globalization;
using System.Text.Json.Nodes;

namespace GameServer.Launcher;

sealed record AccountRow(string Name, string Created, int Characters, IReadOnlyList<CharacterRow> CharacterRows);

sealed record CharacterRow(string Name, string Class, int Level, string League, string LastTown, string LastPlayed, string State);

/// <summary>The accounts and their characters, from the dev host's "accounts" (Agents/docs/dev-server.md in the
/// server): the server owns its save database, the launcher only asks.</summary>
static class AccountList
{
    /// <summary>The classes the dev host's create_character takes.</summary>
    public static readonly string[] Classes = ["Scion", "Marauder", "Ranger", "Witch", "Duelist", "Templar", "Shadow"];

    public static async Task<List<AccountRow>> LoadAsync()
    {
        var reply = (await DevHost.RunAsync(new JsonObject { ["op"] = "accounts" }))[0];
        return Parse(reply);
    }

    internal static List<AccountRow> Parse(JsonObject reply) =>
        [.. (reply["accounts"]?.AsArray() ?? []).OfType<JsonObject>().Select(a =>
        {
            var characters = (a["characters"]?.AsArray() ?? []).OfType<JsonObject>().Select(Row).ToList();
            return new AccountRow(Text(a, "name"), When(a["created"]), characters.Count, characters);
        })];

    static CharacterRow Row(JsonObject c) => new(Text(c, "name"), Text(c, "class"), c["level"]?.GetValue<int>() ?? 0,
        Text(c, "league"), c["last_town"]?.GetValue<string>() ?? "(never in town)", When(c["last_played"]),
        c["dead"]?.GetValue<string>() is { } dead ? dead == "permanent" ? "dead (permanent)" : $"died in {dead}" : "");

    static string Text(JsonObject o, string key) => o[key]?.GetValue<string>() ?? "";

    static string When(JsonNode? iso) =>
        DateTimeOffset.TryParse(iso?.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)
            ? at.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "";
}
