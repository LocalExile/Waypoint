using Waypoint;

namespace Waypoint.Tests;

// The console's suggestions: the command's name, then each argument from the Gateway's values or the usage's choices.
public class CompletionTests
{
    static readonly CommandCatalog Catalog = new(
    [
        new("goto", "/goto <area id or name> [seed] (dev: change area)", ["area"]),
        new("spawn_mob", "/spawn_mob <MonsterVarieties row, Id or name>[@level] [friendly] [normal|magic|rare|unique] [name]", ["monster"]),
        new("item", "/item <BaseItemTypes row, Id, name or unique> [count] [magic|rare] (into the inventory)", ["item"]),
        new("cosmetic", "/cosmetic portal <name, number or part of a name> | /cosmetic list [filter] | /cosmetic off (the portal cosmetic)"),
        new("tp", "/tp <x> <y> | <+dx> <+dy> | <id> | <name> [n] | list [filter] (dev: teleport)"),
        new("despawn", "/despawn [all | <id>] (default: the last one spawned)"),
        new("set_aoe", "/set_aoe <percent> | off (dev: area)"),
        new("gem_xp", "/gem_xp <amount|near> (dev)"),
        new("charges", "/charges [endurance|frenzy|power <count>] | /charges add <type> [count] | /charges clear (dev)"),
    ]);

    static readonly Dictionary<string, IReadOnlyList<ArgumentValue>> Values = new()
    {
        ["area"] = [new("1_1_town", "Lioneye's Watch · server-built"), new("Programming", "Programming World · server-built"),
            new("Programming_Lite", "Programming World (Lite) · server-built"), new("2_6_1", "The Twilight Strand · client-only")],
        ["monster"] = [new("Hillock", "Metadata/Monsters/ZombieBoss/ZombieBossHillockNormal"), new("Rhoa", "Metadata/Monsters/Rhoa/Rhoa")],
        ["item"] = [new("Small Life Flask", "Metadata/Items/Flasks/FlaskLife1"), new("Small Mana Flask", "Metadata/Items/Flasks/FlaskMana1")],
    };

    static CompletionResult Complete(string line, Dictionary<string, IReadOnlyList<ArgumentValue>>? values = null) =>
        Completion.For(line, Catalog, k => (values ?? Values).GetValueOrDefault(k));

    static IEnumerable<string> Inserts(CompletionResult r) => r.Items.Select(s => s.Insert);

    [Fact]
    public void The_first_word_completes_the_command_name()
    {
        var r = Complete("/go");
        Assert.Equal(0, r.From);
        Assert.Equal(["/goto"], Inserts(r));
        Assert.Equal("<area id or name> [seed]", r.Items[0].Detail);
        Assert.Empty(Complete("hello").Items);
    }

    [Fact]
    public void A_named_argument_is_suggested_from_its_kinds_values_by_id_and_by_detail()
    {
        var r = Complete("/goto Prog");
        Assert.Equal("/goto ".Length, r.From);
        Assert.Equal(["Programming", "Programming_Lite"], Inserts(r));
        Assert.Equal(["2_6_1"], Inserts(Complete("/goto twilight")));
        Assert.Equal(4, Complete("/goto ").Items.Count);
    }

    [Fact]
    public void A_kind_not_loaded_yet_asks_for_its_values()
    {
        var r = Complete("/goto Pro", []);
        Assert.Equal("area", r.Loading);
        Assert.Empty(r.Items);
    }

    [Fact]
    public void A_whole_value_of_several_words_moves_on_to_the_next_argument()
    {
        // /item <name> [count] [magic|rare]: the name, a count, then the rarity.
        Assert.Equal(["Small Life Flask", "Small Mana Flask"], Inserts(Complete("/item small")));
        var r = Complete("/item Small Life Flask 2 ");
        Assert.Equal("/item Small Life Flask 2 ".Length, r.From);
        Assert.Equal(["magic", "rare"], Inserts(r));
        Assert.Equal(["rare"], Inserts(Complete("/item Small Life Flask 2 r")));
    }

    [Fact]
    public void A_monsters_level_suffix_belongs_to_its_name()
    {
        Assert.Equal(["friendly"], Inserts(Complete("/spawn_mob Hillock@10 ")));
        Assert.Equal(["normal", "magic", "rare", "unique"], Inserts(Complete("/spawn_mob Rhoa friendly ")));
        Assert.Equal(["unique"], Inserts(Complete("/spawn_mob Rhoa friendly un")));
    }

    [Fact]
    public void The_usages_choices_come_from_brackets_and_from_each_forms_leading_word()
    {
        Assert.Equal(["portal", "list", "off"], Inserts(Complete("/cosmetic ")));
        Assert.Equal(["list"], Inserts(Complete("/tp ")));
        Assert.Equal(["all"], Inserts(Complete("/despawn ")));
        Assert.Equal(["off"], Inserts(Complete("/set_aoe ")));
        Assert.Equal(["near"], Inserts(Complete("/gem_xp ")));
        Assert.Equal(["endurance", "frenzy", "power", "add", "clear"], Inserts(Complete("/charges ")));
    }

    [Fact]
    public void A_row_given_by_its_id_shows_its_name_and_ranks_by_it()
    {
        var values = new Dictionary<string, IReadOnlyList<ArgumentValue>>
        {
            ["monster"] = [new("Chillcaller", "Metadata/Monsters/Skeletons/Chill"), new("Metadata/Monsters/ZombieBoss/ZombieBossHillock", "Hillock")],
        };
        var r = Complete("/spawn_mob hill", values);
        Assert.Equal(["Metadata/Monsters/ZombieBoss/ZombieBossHillock", "Chillcaller"], Inserts(r));
        Assert.Equal("Hillock", r.Items[0].Label);
        Assert.Equal("Metadata/Monsters/ZombieBoss/ZombieBossHillock", r.Items[0].Detail);
    }

    [Fact]
    public void A_name_several_rows_share_counts_as_given_like_their_ids()
    {
        var values = new Dictionary<string, IReadOnlyList<ArgumentValue>>
        {
            ["item"] = [new("Metadata/Items/Flasks/FlaskLife1", "Small Life Flask"), new("Metadata/Items/Flasks/FlaskLife1Royale", "Small Life Flask")],
        };
        Assert.Equal(["magic", "rare"], Inserts(Complete("/item Small Life Flask 2 ", values)));
        Assert.Equal(["magic", "rare"], Inserts(Complete("/item Metadata/Items/Flasks/FlaskLife1 2 ", values)));
    }

    [Fact]
    public void The_gateways_log_lines_beside_its_values_are_left_out()
    {
        var values = CommandCatalog.ParseValues(
            "[gateway] atlas: 1932 areas from data/area-transitions.json\n{\"value\":\"Programming\",\"detail\":\"Programming World\"}\n");
        Assert.Equal([new ArgumentValue("Programming", "Programming World")], values);
    }

    [Fact]
    public void A_value_to_supply_is_never_offered_as_a_word()
    {
        // A Gateway built before the argument kinds: /goto's usage alone must not suggest "area".
        var older = new CommandCatalog([new("goto", "/goto <area id or name> [seed] (dev: change area)"),
            new("accuracy", "/accuracy [monster level] (hit chance)")]);
        Assert.Empty(Completion.For("/goto ", older, _ => null).Items);
        Assert.Empty(Completion.For("/goto Programming ", older, _ => null).Items);
        Assert.Empty(Completion.For("/accuracy ", older, _ => null).Items);
    }

    [Fact]
    public void Past_the_last_argument_and_for_unknown_commands_there_is_nothing()
    {
        Assert.Empty(Complete("/goto Programming 5 ").Items);
        Assert.Empty(Complete("/nothing ").Items);
    }
}
