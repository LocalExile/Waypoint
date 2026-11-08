using Waypoint;

namespace Waypoint.Tests;

public class ModuleIconsTests
{
    [Fact]
    public void Known_modules_have_distinct_defaults_and_keep_the_original_seven()
    {
        string[] ids = ["Game", "Vendors", "Atlas", "Harvest", "Delve", "Commands", "Bench", "Heist", "Cosmetics", "Tattoos",
            "UniqueBelts", "NpcDialogue", "Pantheon", "Social", "HideoutEditing", "Portals", "QuestObjects", "NessaDevelopmentTabs",
            "PhreciaAscendancies", "PlayerBots", "Ruthless", "SurvivorsRewards", "SurvivorsRun", "SurvivorsSkills"];
        var icons = ids.Select(ModuleIcons.Default).ToArray();
        Assert.Equal(ids.Length, icons.Select(i => i.Key).Distinct().Count());
        Assert.Equal(new[] { "core", "vendors", "atlas", "harvest", "delve", "commands", "bench" }, icons.Take(7).Select(i => i.Key));
        Assert.All(icons.Take(7), i => Assert.NotNull(i.Lines));
        Assert.Equal("modules", ModuleIcons.Default("FutureModule").Key);
    }
    [Fact]
    public void Stale_or_invalid_personal_preferences_use_defaults_without_mutating_game_configuration()
    {
        var settings = new Settings();
        settings.ModuleIcons[ModuleIcons.Scope] = new() { ["ATLAS"] = "removed-icon", ["VENDORS"] = "sword" };
        Assert.Equal("atlas", ModuleIcons.Resolve(settings, "Atlas").Key);
        Assert.Equal("sword", ModuleIcons.Resolve(settings, "vendors").Key);
        Assert.NotEqual(ModuleIcons.Resource("Atlas"), ModuleIcons.Resource("Vendors"));
        Assert.Equal(ModuleIcons.Resource("Atlas"), ModuleIcons.Resource("ATLAS"));
        Assert.Equal(ModuleIcons.All.Count, ModuleIcons.All.Select(i => i.Key).Distinct().Count());
        Assert.Null(ModuleIcons.Find("../untrusted-file.svg"));
    }
}
