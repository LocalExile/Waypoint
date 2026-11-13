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

    // A cluster jewel as the Gateway's crafter mode prints it: no implicit, the enchantments a tool starts with, and what
    // they can be changed to. A theme opens its own pool, where its notables are.
    const string ClusterLines = """
        {"type":"class","id":"Jewel","name":"Jewels"}
        {"type":"pool","id":0,"mods":["AfflictionJewelSmallPassivesGrantLife_","AfflictionJewelSmallPassivesGrantLife2_"]}
        {"type":"pool","id":1,"mods":["AfflictionJewelSmallPassivesGrantLife_","AfflictionJewelSmallPassivesGrantLife2_","AfflictionNotablePrismaticHeart__"]}
        {"type":"pool","id":2,"mods":["AfflictionJewelSmallPassivesGrantLife_","AfflictionJewelSmallPassivesGrantLife2_"]}
        {"type":"mod","id":"JewelExpansionPassiveNodes","name":"","generation":3,"domain":21,"level":1,"families":[3829],"tags":[],"stats":[{"id":"local_jewel_expansion_passive_node_index","min":0,"max":0},{"id":"local_jewel_expansion_passive_node_count","min":0,"max":0}]}
        {"type":"mod","id":"JewelExpansionJewelNodesLarge1","name":"of Potential","generation":3,"domain":21,"level":1,"families":[3830],"tags":[],"stats":[{"id":"local_jewel_expansion_jewels_count","min":1,"max":1}]}
        {"type":"mod","id":"JewelExpansionJewelNodesLarge2___","name":"of Possibility","generation":3,"domain":21,"level":1,"families":[3830],"tags":[],"stats":[{"id":"local_jewel_expansion_jewels_count","min":2,"max":2}]}
        {"type":"mod","id":"AfflictionJewelSmallPassivesGrantLife_","name":"Hale","generation":1,"domain":21,"level":1,"families":[4150],"tags":["life"],"stats":[{"id":"local_affliction_jewel_small_nodes_grant_maximum_life","min":2,"max":3}]}
        {"type":"mod","id":"AfflictionJewelSmallPassivesGrantLife2_","name":"Healthy","generation":1,"domain":21,"level":1,"families":[4150],"tags":["life"],"stats":[{"id":"local_affliction_jewel_small_nodes_grant_maximum_life","min":4,"max":7}]}
        {"type":"mod","id":"AfflictionNotablePrismaticHeart__","name":"Notable","generation":1,"domain":21,"level":1,"families":[4200],"tags":[],"stats":[{"id":"local_affliction_notable_prismatic_heart","min":1,"max":1}]}
        {"type":"base","id":"Metadata/Items/Jewels/JewelPassiveTreeExpansionLarge","name":"Large Cluster Jewel","class":"Jewel","width":1,"height":1,"dropLevel":1,"requirements":[0,0,0],"sockets":0,"quality":false,"implicits":[],"pool":0,"influence":{},"crafted":0,"corrupted":0,"eldritch":0,"enchants":[{"id":"JewelExpansionPassiveNodes","values":[1,8],"ranges":[[1,17],[8,12]]},{"id":"JewelExpansionJewelNodesLarge2___","values":[2],"ranges":[[2,2]]},{"id":"NotInTheCatalog","values":[1],"ranges":[[1,1]]}],"cluster":{"size":2,"passives":{"mod":"JewelExpansionPassiveNodes","min":8,"max":12},"sockets":[{"count":1,"mod":"JewelExpansionJewelNodesLarge1"},{"count":2,"mod":"JewelExpansionJewelNodesLarge2___"}],"themes":[{"value":1,"name":"Axe and Sword Damage","tag":"affliction_axe_and_sword_damage","stats":[{"id":"axe_damage_+%","value":12},{"id":"sword_damage_+%","value":12}],"pool":2},{"value":13,"name":"Fire Damage","tag":"affliction_fire_damage","stats":[{"id":"fire_damage_+%","value":12}],"pool":1}]}}
        {"type":"desc","stats":["local_jewel_expansion_passive_node_index"],"variants":[{"when":["#"],"text":"{0}","handlers":[["tree_expansion_jewel_passive",1]]}]}
        {"type":"desc","stats":["local_jewel_expansion_passive_node_count"],"variants":[{"when":["1"],"text":"Adds 1 Passive Skill","handlers":[]},{"when":["#"],"text":"Adds {0} Passive Skills","handlers":[]}]}
        {"type":"desc","stats":["local_jewel_expansion_jewels_count"],"variants":[{"when":["1"],"text":"1 Added Passive Skill is a Jewel Socket","handlers":[]},{"when":["2|#"],"text":"{0} Added Passive Skills are Jewel Sockets","handlers":[]}]}
        {"type":"desc","stats":["local_affliction_jewel_small_nodes_grant_maximum_life"],"variants":[{"when":["#"],"text":"Added Small Passive Skills also grant: {0:+d} to Maximum Life","handlers":[]}]}
        {"type":"desc","stats":["local_affliction_notable_prismatic_heart"],"variants":[{"when":["#"],"text":"1 Added Passive Skill is Prismatic Heart","handlers":[]}]}
        {"type":"desc","stats":["fire_damage_+%"],"variants":[{"when":["1|#"],"text":"{0}% increased Fire Damage","handlers":[]}]}
        {"type":"desc","stats":["axe_damage_+%"],"variants":[{"when":["1|#"],"text":"Axe Attacks deal {0}% increased Damage","handlers":[]}]}
        {"type":"desc","stats":["sword_damage_+%"],"variants":[{"when":["1|#"],"text":"Sword Attacks deal {0}% increased Damage","handlers":[]}]}
        """;

    [Fact]
    public void A_cluster_jewel_starts_with_its_enchantments_and_its_theme_opens_its_notables()
    {
        var catalog = CrafterCatalog.Parse(ClusterLines);
        var large = Assert.Single(catalog.Bases);
        var cluster = large.Cluster!;
        Assert.Equal(("JewelExpansionPassiveNodes", 8, 12, 2), (cluster.Passives, cluster.Min, cluster.Max, cluster.Themes.Count));
        Assert.Equal([(1, "JewelExpansionJewelNodesLarge1"), (2, "JewelExpansionJewelNodesLarge2___")], cluster.Sockets);

        // No implicit and no pool names them: the base does, with the values of the jewel a tool starts with.
        var mods = catalog.NewMods(large);
        Assert.Equal([("JewelExpansionPassiveNodes", "enchant"), ("JewelExpansionJewelNodesLarge2___", "enchant")], mods.Select(m => (m.Id, m.Section)));
        Assert.Equal([[1, 8], [2]], mods.Select(m => m.Values));
        var passives = catalog.Mods[mods[0].Id];
        // The passive count has no range in the game's data: the base's is used.
        Assert.Equal(((1, 17), (8, 12)), (CrafterCatalog.Range(large, mods[0], passives, 0), CrafterCatalog.Range(large, mods[0], passives, 1)));

        // The mod's first value names the theme, which the game words as what the small passives grant; alone, the mod
        // has no line for it rather than a bare number.
        Assert.Equal(["Adds 8 Passive Skills"], catalog.Lines(passives, mods[0].Values));
        Assert.Equal("Axe and Sword Damage", catalog.Theme(large, mods)!.Name);
        Assert.Equal(["Adds 8 Passive Skills", "Added Small Passive Skills grant: Axe Attacks deal 12% increased Damage",
            "Added Small Passive Skills grant: Sword Attacks deal 12% increased Damage"], catalog.Lines(large, mods[0], passives));
        // A theme opens its own affixes: Fire Damage has a notable the first theme and a jewel without one do not.
        Assert.DoesNotContain(catalog.Pool(large, "pool", mods), m => m.Id == "AfflictionNotablePrismaticHeart__");
        mods[0].Values[catalog.ThemeIndex(large)] = 13;
        Assert.Equal(("Fire Damage", 1), (catalog.Theme(large, mods)!.Name, catalog.PoolId(large, "pool", mods)));
        Assert.Contains(catalog.Pool(large, "pool", mods), m => m.Id == "AfflictionNotablePrismaticHeart__");
        Assert.DoesNotContain(catalog.Pool(large, "pool"), m => m.Id == "AfflictionNotablePrismaticHeart__");
        mods[0].Values[0] = 99;
        Assert.Null(catalog.Theme(large, mods));
        Assert.Equal(0, catalog.PoolId(large, "pool", mods));
        mods[0].Values[0] = 13;
        // What /item_give is sent names each in the enchant list.
        string spec = new CraftedItem { Base = large.Id, Rarity = "normal", Mods = mods }.Spec();
        Assert.Contains("""{"id":"JewelExpansionPassiveNodes","section":"enchant","values":[13,8]}""", spec);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(spec, "\"section\":\"enchant\"").Count);

        // An item copied from the game: its count, its sockets and its theme are read from its enchantment lines, and its
        // notable is found among the theme's affixes.
        var read = ItemImport.Read("""
            Item Class: Jewels
            Rarity: Rare
            Armageddon Joy
            Large Cluster Jewel
            --------
            Item Level: 84
            --------
            Adds 10 Passive Skills (enchant)
            1 Added Passive Skill is a Jewel Socket (enchant)
            Added Small Passive Skills grant: 12% increased Fire Damage (enchant)
            Allocates something the crafter has no enchantment for (enchant)
            --------
            Added Small Passive Skills also grant: +7 to Maximum Life
            1 Added Passive Skill is Prismatic Heart
            """, catalog);
        Assert.Equal([("JewelExpansionPassiveNodes", "enchant"), ("JewelExpansionJewelNodesLarge1", "enchant"),
            ("AfflictionJewelSmallPassivesGrantLife2_", "explicit"), ("AfflictionNotablePrismaticHeart__", "explicit")], read.Item.Mods.Select(m => (m.Id, m.Section)));
        Assert.Equal([[13, 10], [1], [7], [1]], read.Item.Mods.Select(m => m.Values));
        Assert.Equal(["Allocates something the crafter has no enchantment for (enchant)"], read.Unmatched.Select(l => l.Text.Trim()));

        // A Gateway that does not list them yet: the base has none, as before.
        var ring = CrafterCatalog.Parse(Lines).Bases.Single();
        Assert.Empty(ring.Enchants);
        Assert.Null(ring.Cluster);
        Assert.Empty(CrafterCatalog.Parse(Lines).NewMods(ring));
    }

    [Fact]
    public void A_line_break_in_a_description_makes_two_lines()
    {
        var description = new StatDescription(["charges"], [new DescriptionVariant(["#"], @"Used {0} times\nDoes not Queue", [])]);
        var mod = new CraftMod("Charges1", "", 1, 1, 1, [0], [], [new CraftStat("charges", 3, 3)]);
        Assert.Equal(["Used 3 times", "Does not Queue"], ModText.Lines(mod, _ => description, [3]));
    }

    [Fact]
    public void A_Gateway_is_only_asked_for_the_crafter_when_its_assembly_has_the_hook()
    {
        // One built before the hook would take `crafter` for a normal start; its assembly is read, not run.
        string folder = Directory.CreateTempSubdirectory("waypoint-crafter-").FullName;
        try
        {
            string with = Assembly(folder, "WithHook", "ItemCrafter"), without = Assembly(folder, "WithoutHook", "ItemRolls");
            Assert.True(CrafterCatalog.HasCrafter(with));
            Assert.False(CrafterCatalog.HasCrafter(without));
            Assert.False(CrafterCatalog.HasCrafter(Path.Combine(folder, "missing.dll")));
            File.WriteAllText(Path.Combine(folder, "text.dll"), "not an assembly");
            Assert.False(CrafterCatalog.HasCrafter(Path.Combine(folder, "text.dll")));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void A_releases_Gateway_is_read_from_the_assembly_beside_its_host()
    {
        Assert.Equal(Path.Combine("app", "GameServer.Gateway.dll"), CrafterCatalog.GatewayAssembly(Path.Combine("app", "GameServer.Gateway.exe")));
        Assert.Equal(Path.Combine("app", "GameServer.Gateway.dll"), CrafterCatalog.GatewayAssembly(Path.Combine("app", "GameServer.Gateway")));
        string built = Path.Combine("src", "GameServer.Gateway", "bin", "Debug", "net10.0", "GameServer.Gateway.dll");
        Assert.Equal(built, CrafterCatalog.GatewayAssembly(built));
    }

    /// <summary>A small assembly on disk that defines one type, as a Gateway build defines ItemCrafter.</summary>
    static string Assembly(string folder, string name, string type)
    {
        var builder = new System.Reflection.Emit.PersistedAssemblyBuilder(new System.Reflection.AssemblyName(name), typeof(object).Assembly);
        builder.DefineDynamicModule(name).DefineType(type, System.Reflection.TypeAttributes.Public).CreateType();
        string file = Path.Combine(folder, name + ".dll");
        builder.Save(file);
        return file;
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
