using Launcher.Core;

namespace Waypoint.Tests;

public class LootFiltersTests
{
    static readonly ClientItemNames Names = ClientItemNames.From(
        ["Ring", "Ruby Ring", "Exalted Orb"], ["Rings", "Currency"], ["of the Brute"]);

    static LootFilterConversion Convert(params string[] lines) => LootFilters.Convert(lines, Names, "test.filter", DateTimeOffset.UnixEpoch);

    [Fact]
    public void Names_the_client_lacks_leave_their_lists_and_a_list_left_empty_turns_its_block_off()
    {
        var r = Convert(
            "#Online Item Filter", "#name:3.30 Strict", "#version:3.30.0",
            "Show # rings",
            "\tClass == \"Rings\"",
            "\tBaseType == \"Ruby Ring\" \"Mycelium Core\" # tiered",
            "\tSetFontSize 40",
            "",
            "Show",
            "\tClass == \"Viscera\"",
            "\tSetFontSize 45",
            "",
            "Hide",
            "\tBaseType Exalted \"Shaper's Head\"");

        Assert.Equal("3.30 Strict", r.OnlineName);
        Assert.Equal(3, r.Blocks);
        Assert.DoesNotContain(r.Lines, l => l.StartsWith("#Online Item Filter"));
        Assert.Contains("\tBaseType == \"Ruby Ring\" # tiered", r.Lines);
        Assert.Contains("\tBaseType Exalted", r.Lines);   // substring match, as the game's
        Assert.Contains("#\tClass == \"Viscera\"", r.Lines);
        Assert.Equal(1, r.DisabledEmptyLists);
        Assert.Equal(3, r.RemovedNames);
    }

    [Fact]
    public void A_block_with_a_condition_the_client_does_not_know_is_turned_off_and_an_unknown_action_only_loses_its_line()
    {
        var r = Convert(
            "Show", "\tFated True", "\tRarity Rare", "",
            "Show", "\tRarity Unique", "\tSetGlowColor 1 2 3", "\tPlayEffect Red");

        Assert.Equal(1, r.DisabledByCondition["Fated"]);
        Assert.Contains("#\tFated True", r.Lines);
        Assert.Contains("\tRarity Unique", r.Lines);
        Assert.DoesNotContain(r.Lines, l => l.Contains("SetGlowColor"));
        Assert.Contains("\tPlayEffect Red", r.Lines);
        Assert.Equal(1, r.RemovedLines);
    }

    [Theory]
    [InlineData(@"C:\Users\p\Documents\My Games\Path of Exile\LocalExile", @"C:\Users\p\Documents\My Games\Path of Exile")]
    [InlineData(@"C:\Users\p\Documents\My Games\Path of Exile\OnlineFilters\", @"C:\Users\p\Documents\My Games\Path of Exile")]
    [InlineData(@"C:\Users\p\Documents\My Games", @"C:\Users\p\Documents\My Games\Path of Exile")]
    public void A_picked_folder_becomes_the_folder_the_game_reads_filters_from(string picked, string expected) =>
        Assert.Equal(expected, LootFilters.ResolveFilterFolder(picked));

    [Fact]
    public void The_copy_is_named_after_the_online_name_or_the_file()
    {
        Assert.Equal(@"F:\3.30 Strict - LocalExile.filter", LootFilters.OutputPath(@"F:\", @"F:\OnlineFilters\qXPqoVCV", "3.30 Strict"));
        Assert.Equal(@"F:\Mine - LocalExile.filter", LootFilters.OutputPath(@"F:\", @"F:\Mine - LocalExile.filter", null));
    }
}
