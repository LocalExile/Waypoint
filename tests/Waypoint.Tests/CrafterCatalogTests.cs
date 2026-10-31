using Waypoint;

namespace Waypoint.Tests;

public class CrafterCatalogTests
{
    // As the Gateway's crafter mode prints them, with a log line between.
    const string Lines = """
        {"type":"class","id":"Ring","name":"Rings"}
        {"type":"pool","id":0,"mods":["IncreasedLife6","IncreasedLife7","ReducedMana1"]}
        [gateway] catalogs: 3 lines that are not JSON
        {"type":"mod","id":"IncreasedLife6","name":"Robust","generation":1,"domain":1,"level":36,"families":[0],"tags":["life"],"stats":[{"id":"base_maximum_life","min":80,"max":99}]}
        {"type":"mod","id":"IncreasedLife7","name":"Virile","generation":1,"domain":1,"level":44,"families":[0],"tags":["life"],"stats":[{"id":"base_maximum_life","min":100,"max":114}]}
        {"type":"mod","id":"ReducedMana1","name":"","generation":2,"domain":1,"level":1,"families":[1],"tags":[],"stats":[{"id":"mana_+%","min":-20,"max":-10},{"id":"mana_regeneration_rate_per_minute","min":60,"max":90}]}
        {"type":"base","id":"Metadata/Items/Rings/Ring1","name":"Iron Ring","class":"Ring","width":1,"height":1,"dropLevel":1,"requirements":[0,0,0],"sockets":0,"quality":false,"implicits":[],"pool":0,"influence":{},"crafted":0,"corrupted":0,"eldritch":0}
        {"type":"desc","stats":["base_maximum_life"],"variants":[{"when":["#"],"text":"{0:+d} to maximum Life","handlers":[]}]}
        {"type":"desc","stats":["mana_+%"],"variants":[{"when":["1|#"],"text":"{0}% increased maximum Mana","handlers":[]},{"when":["#|-1"],"text":"{0}% reduced maximum Mana","handlers":[["negate",1]]}]}
        {"type":"desc","stats":["mana_regeneration_rate_per_minute"],"variants":[{"when":["!0"],"text":"Regenerate {0} Mana per second","handlers":[["per_minute_to_per_second",1]]}]}
        {"type":"word","list":1,"key":4053401100,"text":"Dire"}
        """;

    [Fact]
    public void A_mod_reads_as_the_game_words_it_with_its_range_or_its_values()
    {
        var catalog = CrafterCatalog.Parse(Lines);
        var ring = Assert.Single(catalog.Bases);
        Assert.Equal(("Iron Ring", "Rings", 3), (ring.Name, catalog.Classes[ring.Class], catalog.Pool(ring, "pool").Count));
        var life = catalog.Mods["IncreasedLife7"];
        Assert.Equal(["+(100-114) to maximum Life"], catalog.Lines(life));
        Assert.Equal(["+105 to maximum Life"], catalog.Lines(life, [105]));
        // A negative stat takes its "reduced" variant and is negated; per-minute regeneration shows per second.
        var mana = catalog.Mods["ReducedMana1"];
        Assert.Equal(["(10-20)% reduced maximum Mana", "Regenerate (1-1.5) Mana per second"], catalog.Lines(mana));
        Assert.Equal(["15% reduced maximum Mana", "Regenerate 1.2 Mana per second"], catalog.Lines(mana, [-15, 72]));
        Assert.Equal(["Regenerate 1.2 Mana per second"], catalog.Lines(mana, [0, 72]).Where(l => l.Length > 0));
        // Tiers count down from the highest level of a group.
        Assert.Equal((1, 2), (catalog.Tier(0, life), catalog.Tier(0, catalog.Mods["IncreasedLife6"])));
        Assert.Equal(["IncreasedLife7", "IncreasedLife6"], catalog.Tiers(0, life).Select(m => m.Id));
        Assert.Equal("Dire", Assert.Single(catalog.Words).Text);
    }
}
