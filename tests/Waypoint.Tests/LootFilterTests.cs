using Launcher.Core.LootFilter;

namespace Waypoint.Tests;

public class LootFilterTests
{
    // NeverSink's shapes: CRLF, a tagged rule, a disabled one commented out line by line, text between.
    const string Filter =
        "# NeverSink's filter\r\n" +
        "\r\n" +
        "Show # %D9 $type->currency $tier->t1exalted\r\n" +
        "\tClass == \"Stackable Currency\"\r\n" +
        "\tBaseType == \"Divine Orb\" \"Mirror of Kalandra\"\r\n" +
        "\tSetFontSize 45\r\n" +
        "\tPlayEffect Red\r\n" +
        "\r\n" +
        "#Show # %H8 $type->currency $tier->t2divine\r\n" +
        "#\tBaseType == \"Exalted Orb\"\r\n" +
        "#\tSetFontSize 40\r\n" +
        "\r\n" +
        "Hide # $type->currency $tier->trash\r\n" +
        "\tBaseType == \"Scroll of Wisdom\"\r\n" +
        "\tSetFontSize   18";

    const string Options = """
        Function CurrencyList($s, $shd = "SHD", $text) { QuickUI($s, $shd, [], $text, ["normalCurrency", "O"]); } // global
        Chapter(){
            SectionTitle("Currency & Rewards");
            Section("Currency", "Chaos", "E") {
                Function CurrencyList($s, $shd = "SH", $text) { QuickUI($s, $shd, ["StackSize?"], $text, ["Currency", "DO"]); }
                Search S0(3.0, "Currency Trash", "currency;trash") { BaseType "Scroll of Wisdom"; }
                Conditional("?NewTier") { Show; Class Maps; }
                CurrencyList([3.0, "Currency God Tier 1", "currency;t1exalted"], "S", "S Tier:");
                CurrencyList([3.0, "Currency God Tier 2", "currency;t2divine"]);
                CurrencyList(0, "HD", "Trash");
                BaseTypeMatrix("x");
            }
            Section("Other") { CurrencyList([1.0, "Other", "other;t1"]); }
        }
        """;

    [Fact]
    public void A_filter_is_written_back_unchanged_and_only_changed_rules_are_rewritten()
    {
        var doc = FilterDocument.Parse(Filter);
        Assert.Equal(Filter, doc.ToString());
        // A blank line or a comment does not end a rule: the game reads it up to the next Show, Hide or Minimal.
        const string Gaps = "Show\n\tBaseType \"Ring\"\n\n\t# a note\n\tFated True\n";
        var gaps = FilterDocument.Parse(Gaps);
        Assert.Equal(["BaseType", "Fated"], Assert.Single(gaps.Blocks).Lines.Where(l => !l.IsBlank).Select(l => l.Keyword));
        Assert.Equal(Gaps, gaps.ToString());
        var blocks = doc.ByKey();
        Assert.True(blocks["currency;t2divine"].Disabled);
        var (op, items) = FilterSyntax.List(blocks["currency;t1exalted"].Find("BaseType")!.Value);
        Assert.Equal("==", op);
        Assert.Equal(["Divine Orb", "Mirror of Kalandra"], items);

        var chapters = OptionFile.Parse(Options);
        var rules = OptionFile.Rules(chapters).ToList();
        var custom = new FilterCustomization();
        custom.For("Currency God Tier 1").Items = ["Divine Orb"];
        custom.For("Currency God Tier 1").Lines["PlayEffect"] = null;
        custom.For("Currency God Tier 1").Lines["MinimapIcon"] = "0 Red Star";
        custom.For("Currency God Tier 2").Visibility = "Show";
        custom.For("Currency Trash").Items = [];
        custom.For("Gone").Visibility = "Hide";
        custom = FilterCustomization.FromJson(custom.ToJson());

        Assert.Equal(["Gone"], custom.ApplyTo(doc, rules));
        Assert.Equal(
            "# NeverSink's filter\r\n" +
            "\r\n" +
            "Show # %D9 $type->currency $tier->t1exalted\r\n" +
            "\tClass == \"Stackable Currency\"\r\n" +
            "\tBaseType == \"Divine Orb\"\r\n" +
            "\tSetFontSize 45\r\n" +
            "\tMinimapIcon 0 Red Star\r\n" +
            "\r\n" +
            "Show # %H8 $type->currency $tier->t2divine\r\n" +
            "\tBaseType == \"Exalted Orb\"\r\n" +
            "\tSetFontSize 40\r\n" +
            "\r\n" +
            "#Hide # $type->currency $tier->trash\r\n" +
            "#\tSetFontSize 18",
            doc.ToString());
    }

    [Fact]
    public void Option_files_expand_functions_by_section_and_name_searches()
    {
        var chapters = OptionFile.Parse(Options);
        var chapter = Assert.Single(chapters);
        Assert.Equal("Currency & Rewards", chapter.Title);
        var sections = chapter.Children.OfType<OptionSection>().ToList();
        Assert.Equal(["Currency", "Other"], sections.Select(s => s.Title));

        var rules = OptionFile.Rules(sections[0].Children).ToList();
        Assert.Equal(["currency;t1exalted", "currency;t2divine", "currency;trash"], rules.Select(r => r.Key));
        Assert.All(rules, r => Assert.Equal("Currency", r.TierList));
        Assert.Equal(("S", "S Tier:", "DO"), (rules[0].Buttons, rules[0].Title, rules[0].TierFlags));
        Assert.Equal(("SH", "Currency God Tier 2"), (rules[1].Buttons, rules[1].Title));
        Assert.Equal(["StackSize?"], rules[1].Controls);
        Assert.Equal(("Currency Trash", "HD", 3.0), (rules[2].Id, rules[2].Buttons, rules[2].Version));
        Assert.Contains(sections[0].Children, n => n is OptionUnsupported { Name: "BaseTypeMatrix" });

        // The section's own CurrencyList is gone outside it: the global one applies.
        var other = Assert.Single(OptionFile.Rules(sections[1].Children));
        Assert.Equal(("SHD", "normalCurrency", "O"), (other.Buttons, other.TierList, other.TierFlags));
    }

    [Fact]
    public void A_drop_takes_the_first_matching_rule_after_the_continue_rules_before_it()
    {
        var doc = FilterDocument.Parse(
            "Show # $type->decorators $tier->all\r\n\tClass \"Currency\"\r\n\tSetFontSize 40\r\n\tPlayEffect Blue\r\n\tContinue\r\n\r\n" + Filter);
        SimItem Drop(string name) => new() { BaseType = name, Class = "Stackable Currency" };

        var divine = FilterMatcher.Match(doc, Drop("Divine Orb"), 83);
        Assert.Equal(("Show", "45", "Red"), (divine.Visibility, divine.Actions["SetFontSize"], divine.Actions["PlayEffect"]));
        Assert.Equal(["decorators;all", "currency;t1exalted"], divine.Rules.Select(r => r.Key));
        // The disabled rule is skipped: Exalted Orb keeps only the Continue rule's style.
        var exalted = FilterMatcher.Match(doc, Drop("Exalted Orb"), 83);
        Assert.Equal(("Show", "40", "decorators;all"), (exalted.Visibility, exalted.Actions["SetFontSize"], exalted.Rules[^1].Key));
        Assert.Equal("Hide", FilterMatcher.Match(doc, Drop("Scroll of Wisdom"), 83).Visibility);

        new FilterCustomization { TemporaryBeams = true }.ApplyTo(doc, []);
        Assert.Equal("Red Temp", doc.ByKey()["currency;t1exalted"].Find("PlayEffect")!.Value);
    }

    [Fact]
    public void An_item_copied_in_game_is_read_as_the_filter_sees_it()
    {
        var catalog = ItemCatalog.Parse(
            "Class,BaseType,DropLevel,Rarity,Height,Width,Game:Energy Shield\n" +
            "Body Armours,Vaal Regalia,68,NMR,3,2,175\n" +
            "Rings,\"Ring, Plain\",1,NMR,1,1,\n" +
            "Rings,Sapphire Ring,11,NMR,1,1,\n");
        // As the 3.29.3d client copied it with Ctrl+C.
        var copied = ItemText.Parse("""
            Item Class: Body Armours
            Rarity: Rare
            Brood Jack
            Vaal Regalia
            --------
            Energy Shield: 181
            --------
            Requirements:
            Level: 68 (unmet)
            Int: 194
            --------
            Sockets: W-W B 
            --------
            Item Level: 84
            --------
            { Prefix Modifier "Thorny" (Tier: 4) — Damage, Physical }
            Reflects 3(1-4) Physical Damage to Melee Attackers
            { Suffix Modifier "of the Cloud" (Tier: 8) — Elemental, Lightning, Resistance }
            +9(6-11)% to Lightning Resistance
            --------
            Corrupted
            """, catalog);
        var item = copied.Item;
        Assert.Equal(("Brood Jack", "Vaal Regalia", "Body Armours", "Rare"), (copied.Name, item.BaseType, item.Class, item.Rarity));
        Assert.Equal((84, 68, 175, 3, 2), (item.ItemLevel, item.DropLevel, item.BaseEnergyShield, item.Sockets, item.LinkedSockets));
        Assert.Equal(["Thorny", "of the Cloud"], item.ExplicitMods);
        Assert.True(item.Identified && item.Corrupted);
        Assert.Empty(copied.Notes);

        var doc = FilterDocument.Parse(
            "Show # $type->a $tier->b\r\n\tHasExplicitMod >=2 \"Thorny\" \"of the Cloud\" \"Merciless\"\r\n\tSocketGroup \"WW\"\r\n\tCorrupted True\r\n\tSetFontSize 44");
        Assert.Equal("44", FilterMatcher.Match(doc, item, 83).Actions["SetFontSize"]);

        // A magic item names its base between its affixes; a copy without the mod names says how to get them.
        var magic = ItemText.Parse("Item Class: Rings\nRarity: Magic\nSapphire Ring of the Whelpling\n--------\nItem Level: 84", catalog);
        Assert.Equal("Sapphire Ring", magic.Item.BaseType);
        Assert.Single(magic.Notes);
    }

    [Fact]
    public void A_mods_loot_filter_part_adds_its_rules_on_top_and_its_items_to_their_tiers()
    {
        string checkout = Path.Combine(Path.GetTempPath(), "lf-mods-" + Guid.NewGuid().ToString("N"));
        string mod = Path.Combine(checkout, "Mods", "Binaries", "excitingitems");
        Directory.CreateDirectory(mod);
        try
        {
            File.WriteAllText(Path.Combine(mod, "mod.json"), """{ "id": "excitingitems", "name": "Exciting Items" }""");
            File.WriteAllText(Path.Combine(mod, ModFilters.FileName), """
                {
                  "items": [
                    { "baseType": "Scroll of Wisdom", "tier": "currency;t1exalted" },
                    { "baseType": "Orb of Example", "tier": "currency;t1exalted", "class": "Stackable Currency" }
                  ],
                  "rules": [
                    { "id": "exciting", "title": "Exciting items",
                      "lines": [ "Show", "    SynthesisedItem True", "    SetFontSize 45", "    PlayEffect Yellow" ] }
                  ]
                }
                """);
            var problems = new List<string>();
            var mods = ModFilters.Read(checkout, problems);
            Assert.Empty(problems);
            var mine = Assert.Single(mods);
            Assert.Equal("Exciting Items", mine.ModName);

            var doc = FilterDocument.Parse(Filter);
            ModFilters.Apply(doc, mods, problems);
            var rule = Assert.Single(OptionFile.Rules([ModFilters.Chapter(mods, "Mods")!]));
            Assert.Equal(("mod:excitingitems/exciting", "Exciting items"), (rule.Id, rule.Title));
            var top = doc.Blocks.First();
            Assert.Equal(("mod:excitingitems;exciting", "Show"), (top.Key, top.Action));
            var byKey = doc.ByKey();
            Assert.Equal(["Divine Orb", "Mirror of Kalandra", "Scroll of Wisdom", "Orb of Example"],
                FilterSyntax.List(byKey["currency;t1exalted"].Find("BaseType")!.Value).Values);
            Assert.True(byKey["currency;trash"].Disabled);

            // The player's own tier list, saved before the mod came, leaves its items out: they go back to the mod's tier.
            var custom = new FilterCustomization();
            custom.For("Currency God Tier 1").Items = ["Divine Orb"];
            custom.ApplyTo(doc, OptionFile.Rules(OptionFile.Parse(Options)));
            ModFilters.Restore(doc, mods);
            Assert.Equal(["Divine Orb", "Scroll of Wisdom", "Orb of Example"], FilterSyntax.List(doc.ByKey()["currency;t1exalted"].Find("BaseType")!.Value).Values);
            Assert.Equal("Stackable Currency", ModFilters.Catalog(ItemCatalog.Parse("Class,BaseType\n"), mods).ByName["Orb of Example"].Class);

            // A rule another mod wrote into the file itself stays when the editor writes it again; this mod's own is written fresh.
            string written = "# >>> petloot (LocalExile mod) >>>\nShow\n\tBaseType \"Chaos Orb\"\n# <<< petloot <<<\n"
                + "# >>> excitingitems (LocalExile mod, from its loot-filter.json) >>>\nShow\n# <<< excitingitems <<<\n"
                + "# >>> oldmod (LocalExile mod, from its loot-filter.json) >>>\nShow\n# <<< oldmod <<<\n" + doc;
            Assert.Equal(["# >>> petloot (LocalExile mod) >>>", "Show", "\tBaseType \"Chaos Orb\"", "# <<< petloot <<<", ""],
                ModFilters.Foreign(written, ["excitingitems"]));
        }
        finally { Directory.Delete(checkout, true); }
    }
}
