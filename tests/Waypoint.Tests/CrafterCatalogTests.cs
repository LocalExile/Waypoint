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

    [Fact]
    public void An_item_Path_of_Building_copied_opens_with_its_modifiers_found_by_their_words()
    {
        var catalog = CrafterCatalog.Parse(Lines + "\n" + """{"type":"word","list":2,"key":1222507149,"text":" Abyss"}""");
        // As PoB copies an item it crafted: the prefix's Id, then lines with its tags; the mana mod's two lines go together.
        var read = ItemImport.Read("""
            Rarity: RARE
            Dire Abyss
            Iron Ring
            Crafted: true
            Prefix: {range:0}IncreasedLife7
            Suffix: None
            Item Level: 70
            Implicits: 0
            {tags:life}{prefix}+100 to maximum Life
            15% reduced maximum Mana
            Regenerate 1.2 Mana per second
            {crafted}Has no modifier in the crafter
            Corrupted
            """, catalog);
        var item = read.Item;
        Assert.Equal(("Metadata/Items/Rings/Ring1", "rare", 70, "Dire Abyss"), (item.Base, item.Rarity, item.ItemLevel, item.Label));
        Assert.Equal([4053401100u, 1222507149u], item.Name);
        Assert.Equal(["corrupted"], item.States);
        Assert.Equal(["IncreasedLife7", "ReducedMana1"], item.Mods.Select(m => m.Id));
        Assert.Equal([100], item.Mods[0].Values);
        Assert.Equal(["15% reduced maximum Mana", "Regenerate 1.2 Mana per second"], catalog.Lines(catalog.Mods["ReducedMana1"], item.Mods[1].Values));
        Assert.Equal(["{crafted}Has no modifier in the crafter"], read.Unmatched.Select(l => l.Text.Trim()));
        // A value no tier rolls is kept as it is; a unique is not read.
        Assert.Equal([130], ItemImport.Read("Rarity: MAGIC\nIron Ring\nImplicits: 0\n+130 to maximum Life", catalog).Item.Mods.Single().Values);
        Assert.Equal(0, Assert.Throws<ImportException>(() => ItemImport.Read("Doedre's Damning\nIron Ring\nImplicits: 0", catalog)).Line);
    }

    [Fact]
    public void An_item_the_game_copied_takes_the_tier_its_ranges_name()
    {
        var catalog = CrafterCatalog.Parse(Lines);
        // The game's Ctrl+Alt+C: a header per modifier, each value with its tier's range. 500 is beyond every tier: the range
        // picks the tier, and the value stays.
        var read = ItemImport.Read("""
            Item Class: Rings
            Rarity: Rare
            Sol Hold
            Iron Ring
            --------
            Requirements:
            Level: 44
            --------
            Item Level: 84
            --------
            { Prefix Modifier "Robust" (Tier: 2) — Life }
            +500(80-99) to maximum Life
            { Suffix Modifier "" — Mana }
            15(10-20)% reduced maximum Mana
            Regenerate 1.2(1-1.5) Mana per second
            (Mana is what skills cost)
            { Suffix Modifier "of Nothing" — Attribute }
            Allocates a passive the crafter has no modifier for
            --------
            Hunter Item
            """, catalog);
        Assert.Equal(("rare", 84, "Sol Hold"), (read.Item.Rarity, read.Item.ItemLevel, read.Item.Label));
        Assert.Equal(["hunter"], read.Item.States);
        Assert.Equal(["IncreasedLife6", "ReducedMana1"], read.Item.Mods.Select(m => m.Id));
        Assert.Equal([500], read.Item.Mods[0].Values);
        Assert.Equal(["Allocates a passive the crafter has no modifier for"], read.Unmatched.Select(l => l.Text.Trim()));
        Assert.Equal(ImportStatus.Skipped, read.Lines.Single(l => l.Text.StartsWith('(')).Status);
    }

    [Fact]
    public void A_line_break_in_a_description_makes_two_lines()
    {
        var description = new StatDescription(["charges"], [new DescriptionVariant(["#"], @"Used {0} times\nDoes not Queue", [])]);
        var mod = new CraftMod("Charges1", "", 1, 1, 1, [0], [], [new CraftStat("charges", 3, 3)]);
        Assert.Equal(["Used 3 times", "Does not Queue"], ModText.Lines(mod, _ => description, [3]));
    }

    [Fact]
    public void A_Gateway_is_only_asked_for_the_crafter_when_it_has_the_hook()
    {
        // One built before the hook would take `crafter` for a normal start.
        Assert.False(CrafterCatalog.HasCrafter(new CommandCatalog([new ChatCommand("item", "/item <name>")])));
        Assert.True(CrafterCatalog.HasCrafter(new CommandCatalog([new ChatCommand("item", "/item <name>"), new ChatCommand("item_give", "/item_give <item as JSON>")])));
    }

    [Fact]
    public void A_window_stays_inside_its_monitor()
    {
        // Centred on a small owner in a corner: moved, as far as it takes and no further.
        Assert.Equal((0, 0, 1320, 840), FormWindow.Fit((-210, -140, 1320, 840), (0, 0, 1920, 1040)));
        // Larger than a laptop's screen at 150%.
        Assert.Equal((0, 0, 1280, 672), FormWindow.Fit((-20, -84, 1320, 840), (0, 0, 1280, 672)));
        // Already inside, on a monitor to the left of the main one: left alone.
        Assert.Equal((-1800, 100, 1320, 840), FormWindow.Fit((-1800, 100, 1320, 840), (-1920, 0, 1920, 1040)));
    }
}
