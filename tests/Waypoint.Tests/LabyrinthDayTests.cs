using Waypoint;

namespace Waypoint.Tests;

public class LabyrinthDayTests
{
    // `GameServer.Gateway labyrinth 2026-11-07`, the Eternal tier (poelab.com's "Uber Nov 07"), with a log line between.
    const string Lines = """
        {"type":"day","seed":20764,"date":"2026-11-07"}
        {"type":"tier","tier":4,"name":"The Eternal Labyrinth"}
        [gateway] a log line that is not JSON
        {"type":"room","tier":4,"index":0,"area":"EndGame_Labyrinth_OH_branch","name":"Estate Walkways","slot":0,"place":1,"x":0,"y":1,"links":[-1,1,2],"secrets":[{"id":"Hidden","name":"","number":70}],"izaro":0}
        {"type":"room","tier":4,"index":1,"area":"EndGame_Labyrinth_RT_straight","name":"Basilica Passage","slot":0,"place":2,"x":1,"y":0,"links":[0,2],"secrets":[{"id":"SilverKey","name":"Silver Key","number":49},{"id":"Argus","name":"","number":69}],"izaro":0}
        {"type":"room","tier":4,"index":2,"area":"EndGame_Labyrinth_DG_quad_door","name":"Domain Crossing","slot":0,"place":3,"x":2,"y":1,"links":[0,1,3,4],"secrets":[{"id":"GoldDoor","name":"Gold Door","number":50},{"id":"SilverKey","name":"Silver Key","number":51}],"izaro":0}
        {"type":"room","tier":4,"index":3,"area":"EndGame_Labyrinth_RT_end_bottleneck","name":"Basilica Annex","slot":0,"place":4,"x":2.5,"y":0.5,"links":[2],"secrets":[{"id":"GoldKey","name":"Gold Key","number":52},{"id":"Gauntlet","name":"","number":67}],"izaro":0}
        {"type":"room","tier":4,"index":4,"area":"EndGame_Labyrinth_boss_1","name":"Aspirant\u0027s Trial","slot":0,"place":5,"x":3,"y":1,"links":[2,5],"secrets":[{"id":"ChargeDisruptorsEndgame1","name":"Charge Disruptors1","number":60}],"izaro":1}
        {"type":"room","tier":4,"index":5,"area":"EndGame_Labyrinth_DG_branch_bottleneck_1_#","name":"Domain Walkways","slot":1,"place":1,"x":0,"y":1,"links":[4,8,6],"secrets":[{"id":"Payload","name":"","number":72}],"izaro":0}
        {"type":"room","tier":4,"index":6,"area":"EndGame_Labyrinth_EH_end_bottleneck","name":"Mansion Annex","slot":1,"place":2,"x":0.5,"y":0,"links":[5],"secrets":[{"id":"SilverKey","name":"Silver Key","number":53},{"id":"Gauntlet","name":"","number":71}],"izaro":0}
        {"type":"room","tier":4,"index":7,"area":"EndGame_Labyrinth_P_end_bottleneck","name":"Sanitorium Annex","slot":1,"place":3,"x":2,"y":2,"links":[8],"secrets":[{"id":"SilverDoorReward","name":"Silver Cache","number":54}],"izaro":0}
        {"type":"room","tier":4,"index":8,"area":"EndGame_Labyrinth_C_branch_bottleneck_1","name":"Sepulchre Halls","slot":1,"place":4,"x":1.5,"y":1,"links":[5,7,9],"secrets":[],"izaro":0}
        {"type":"room","tier":4,"index":9,"area":"EndGame_Labyrinth_boss_1","name":"Aspirant\u0027s Trial","slot":1,"place":5,"x":3,"y":1,"links":[8,10],"secrets":[{"id":"PowerPylonsEndgame1","name":"Power Pylons1","number":64},{"id":"ChargeDisruptorsEndgame2","name":"Charge Disruptors2","number":60}],"izaro":2}
        {"type":"room","tier":4,"index":10,"area":"EndGame_Labyrinth_C_branch_bottleneck_1","name":"Sepulchre Halls","slot":2,"place":1,"x":0,"y":1,"links":[9,11,13],"secrets":[{"id":"Puzzle","name":"","number":68},{"id":"Hidden","name":"","number":74}],"izaro":0}
        {"type":"room","tier":4,"index":11,"area":"EndGame_Labyrinth_RT_branch_bottleneck_1","name":"Basilica Halls","slot":2,"place":2,"x":1,"y":0,"links":[10,15,12],"secrets":[{"id":"Hidden","name":"","number":73}],"izaro":0}
        {"type":"room","tier":4,"index":12,"area":"EndGame_Labyrinth_C_end_bottleneck","name":"Sepulchre Annex","slot":2,"place":3,"x":1.3,"y":0.7,"links":[11],"secrets":[{"id":"SilverDoorReward","name":"Silver Cache","number":56}],"izaro":0}
        {"type":"room","tier":4,"index":13,"area":"EndGame_Labyrinth_DG_branch_bottleneck_1_#","name":"Domain Walkways","slot":2,"place":4,"x":2,"y":2,"links":[10,14,15],"secrets":[],"izaro":0}
        {"type":"room","tier":4,"index":14,"area":"EndGame_Labyrinth_EH_end_bottleneck","name":"Mansion Annex","slot":2,"place":5,"x":1.7,"y":1.3,"links":[13],"secrets":[{"id":"SilverDoorReward","name":"Silver Cache","number":58}],"izaro":0}
        {"type":"room","tier":4,"index":15,"area":"EndGame_Labyrinth_boss_2_end","name":"Aspirant\u0027s Trial","slot":2,"place":6,"x":3,"y":1,"links":[11,13,-1],"secrets":[{"id":"ChargeDisruptorsEndgame2","name":"Charge Disruptors2","number":60},{"id":"PowerPylonsEndgame2","name":"Power Pylons2","number":64}],"izaro":3}
        """;

    [Fact]
    public void The_days_map_numbers_its_rooms_and_finds_the_shortest_way_and_what_each_room_holds()
    {
        var day = LabyrinthDay.Parse(Lines);
        Assert.Equal((20764u, new DateOnly(2026, 11, 7)), (day.Seed, day.Date));
        var tier = Assert.Single(day.Tiers);
        Assert.Equal((4, 16), (tier.Tier, tier.Rooms.Count));
        // poelab.com's notes for the day: Argus in 2, the silver key in 3, gauntlets in 4, 6 and 7, the puzzle in 11,
        // Darkshrines in 1, 11 and 12 (rooms numbered left to right).
        Assert.Equal([2], tier.Holding(LabFeature.Argus));
        Assert.Contains(3, tier.Holding(LabFeature.SilverKey));
        Assert.Equal([4, 7], tier.Holding(LabFeature.TrapGauntlet));
        Assert.Equal([6], tier.Holding(LabFeature.EscortGauntlet));
        Assert.Equal([11], tier.Holding(LabFeature.Puzzle));
        Assert.Equal([1, 11, 12], tier.Holding(LabFeature.Darkshrine));
        // Drawn by side: the way in at 6 o'clock, the doors to rooms 2 and 3 (up-right and right on the map) at 12 and 2
        // o'clock; Izaro's room 5 in from the left and on to the right.
        var sides = tier.DoorSides();
        Assert.Equal([(-1, 270.0), (1, 90.0), (2, 30.0)], sides[0].OrderBy(s => s.Key).Select(s => (s.Key, s.Value)));
        Assert.Equal([(2, 180.0), (5, 0.0)], sides[4].OrderBy(s => s.Key).Select(s => (s.Key, s.Value)));
        // The way through: 1, 3, Izaro, 6, 8, Izaro, 11, then either 12 or 15 (as long), Izaro.
        var (rooms, links, length) = tier.ShortestWay();
        Assert.Equal(9, length);
        Assert.Equal([1, 3, 5, 6, 8, 10, 11, 12, 15, 16], rooms.Select(r => tier.Rooms[r].Number).Order());
        Assert.Contains((2, 4), links);
        Assert.DoesNotContain((0, 1), links);
        Assert.Equal([(1, (IReadOnlyList<string>)["Charge disruptors"]), (2, ["Fonts", "Charge disruptors"]), (3, ["Charge disruptors", "Fonts"])],
            tier.IzaroFights().Select(f => (f.Fight, f.Mechanics)).ToList(), new FightComparer());
        // The mode came with /lab_layout's date: a Gateway without it is never asked (it would start serving).
        Assert.False(LabyrinthDay.HasLayouts(new CommandCatalog([new ChatCommand("lab_layout", "/lab_layout [1|2|3|4] (dev: today's layout)")])));
        Assert.True(LabyrinthDay.HasLayouts(new CommandCatalog([new ChatCommand("lab_layout", "/lab_layout [1|2|3|4] [yyyy-mm-dd] (dev: a day's layout)")])));
    }

    sealed class FightComparer : IEqualityComparer<List<(int, IReadOnlyList<string>)>>
    {
        public bool Equals(List<(int, IReadOnlyList<string>)>? a, List<(int, IReadOnlyList<string>)>? b) =>
            a!.Count == b!.Count && a.Zip(b).All(p => p.First.Item1 == p.Second.Item1 && p.First.Item2.SequenceEqual(p.Second.Item2));
        public int GetHashCode(List<(int, IReadOnlyList<string>)> obj) => obj.Count;
    }
}
