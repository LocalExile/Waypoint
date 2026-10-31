using Waypoint;

namespace Waypoint.Tests;

public class CommandCatalogTests
{
    static readonly CommandCatalog Catalog = new(CommandCatalog.Parse("""
        {"name":"gem","usage":"/gem <name> [level] [quality] (a gem into the inventory)"}
        {"name":"gem_xp","usage":"/gem_xp <amount|near> (dev: set gem experience)"}
        {"name":"item","usage":"/item <name> [count] (into the inventory)"}
        {"name":"item_tab","usage":"/item_tab <tab> <count>"}
        {"name":"commands","usage":"/commands [page | name] (/com; the command list)"}
        {"name":"spawn_mob","usage":"/spawn_mob <monster>"}
        """));

    [Fact]
    public void The_gateway_export_is_read_with_each_usage()
    {
        Assert.Equal(6, Catalog.All.Count);
        var gem = Catalog.All[0];
        Assert.Equal("/gem <name> [level] [quality]", gem.Short);
        Assert.Equal("a gem into the inventory", gem.Explanation);
        Assert.Equal("", Catalog.All[3].Explanation);
    }

    [Fact]
    public void Suggestions_start_with_the_typed_name_then_contain_it()
    {
        Assert.Equal(["item", "item_tab"], Catalog.Suggest("/it").Select(c => c.Name));
        Assert.Equal(["gem", "gem_xp"], Catalog.Suggest("/GEM").Select(c => c.Name));
        Assert.Equal(["spawn_mob"], Catalog.Suggest("/mob").Select(c => c.Name));
        Assert.Equal(6, Catalog.Suggest("/").Count);
    }

    [Fact]
    public void Suggestions_stop_once_the_name_is_typed_and_outside_commands()
    {
        Assert.Empty(Catalog.Suggest("/item Iron Ring"));
        Assert.Empty(Catalog.Suggest("hello"));
        Assert.Empty(Catalog.Suggest(""));
    }

    [Fact]
    public void The_line_names_its_command_for_the_usage_hint()
    {
        Assert.Equal("item", Catalog.Of("/item Iron Ring rare")?.Name);
        Assert.Equal("commands", Catalog.Of("/com 2")?.Name);
        Assert.Null(Catalog.Of("/nothing"));
        Assert.Null(Catalog.Of("item"));
    }
}
