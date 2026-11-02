using System.Text.Json;
using System.Text.Json.Serialization;

namespace Waypoint;

/// <summary>A mod on a crafted item: a Mods.dat Id, its list (implicit, explicit …) and one value per stat.</summary>
sealed class CraftedMod
{
    public string Id { get; set; } = "";
    public string Section { get; set; } = "explicit";
    public List<int> Values { get; set; } = [];
}

/// <summary>An item the crafter composed: what /item_give takes (the server's Agents/docs/dev/item-crafter.md), with a
/// label and when it was saved.</summary>
sealed class CraftedItem
{
    public string Key { get; set; } = Guid.NewGuid().ToString("N");
    public string Label { get; set; } = "";
    public string Base { get; set; } = "";
    public string Rarity { get; set; } = "rare";
    public int ItemLevel { get; set; } = 84;
    public int Quality { get; set; }
    public bool Identified { get; set; } = true;
    public List<string> States { get; set; } = [];
    /// <summary>A rare's two Words.dat keys (prefix, suffix); null for a random name.</summary>
    public List<uint>? Name { get; set; }
    /// <summary>As /sockets takes them ("RRG B"); "" for none, null for what the base gets by default.</summary>
    public string? Sockets { get; set; }
    public List<CraftedMod> Mods { get; set; } = [];
    public DateTime Saved { get; set; }

    public CraftedItem Copy() => JsonSerializer.Deserialize<CraftedItem>(JsonSerializer.Serialize(this, CraftedItems.Json), CraftedItems.Json)!;

    static readonly JsonSerializerOptions SpecJson = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    /// <summary>The /item_give argument: one line of JSON.</summary>
    public string Spec() => JsonSerializer.Serialize(new
    {
        @base = Base,
        rarity = Rarity,
        itemLevel = ItemLevel,
        quality = Quality > 0 ? Quality : (int?)null,
        identified = Identified ? (bool?)null : false,
        states = States.Count > 0 ? States : null,
        name = Rarity == "rare" && Name is [_, _] ? Name : null,
        sockets = Sockets,
        mods = Mods.Select(m => new { id = m.Id, section = m.Section, values = m.Values }),
    }, SpecJson);
}

/// <summary>The crafter's saved items, in %LOCALAPPDATA%\Waypoint\crafted-items.json beside the settings: Waypoint's own
/// file, never in the checkout.</summary>
static class CraftedItems
{
    internal static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

#if WINDOWS
    static string File => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Waypoint", "crafted-items.json");
#else
    static string File => Path.Combine(Platform.LocalData, "Waypoint", "crafted-items.json");
#endif

    public static List<CraftedItem> Load()
    {
        try { return System.IO.File.Exists(File) ? JsonSerializer.Deserialize<List<CraftedItem>>(System.IO.File.ReadAllText(File), Json) ?? [] : []; }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return []; }
    }

    public static void Save(IEnumerable<CraftedItem> items)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(File)!);
        string temp = File + ".tmp";
        System.IO.File.WriteAllText(temp, JsonSerializer.Serialize(items.ToList(), Json));
        System.IO.File.Move(temp, File, overwrite: true);
    }
}
