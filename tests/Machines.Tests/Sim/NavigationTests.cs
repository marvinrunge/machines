using System.Numerics;
using Machines.Import.Model;
using Machines.Sim;
using Machines.Sim.World;

namespace Machines.Tests.Sim;

/// <summary>Paths over the original obstacles, and what keeps big battles cheap (the M3 gate: 4 × 200 machines).</summary>
public class NavigationTests
{
    private static readonly Lazy<GameData> DataLoader = new(() => GameData.Load(RepoPaths.OriginalDataFolder()));

    private static GameWorld NewMatch(string map) =>
        MatchSetup.Create(DataLoader.Value, map, map,
            new Dictionary<Race, Controller> { [Race.Red] = Controller.Player, [Race.Blue] = Controller.Player }, seed: 3);

    private static Vector2 FreePointNear(GameWorld world, Vector2 around)
    {
        for (var r = 0f; r < 400; r += 4)
            for (var a = 0; a < 16; a++)
            {
                var p = around + new Vector2(MathF.Cos(a * MathF.Tau / 16), MathF.Sin(a * MathF.Tau / 16)) * r;
                if (world.Nav.IsFree(p, crossesWater: false))
                    return p;
            }
        throw new InvalidOperationException("no free ground");
    }

    private static void AssertWalkable(GameWorld world, Vector2 start, List<Vector2> path)
    {
        var from = start;
        foreach (var point in path)
        {
            Assert.True(world.Nav.LineOfSight(from, point, crossesWater: false), $"{from} to {point} crosses a wall");
            from = point;
        }
    }

    [Fact]
    public void A_goal_out_in_the_lake_ends_the_path_on_the_shore_without_combing_the_map()
    {
        var world = NewMatch("m_green");
        // m_green's lake lies in the middle of the map: the water nearest the centre.
        var centre = new Vector2(world.Planet.Surface.Width / 2, world.Planet.Surface.Depth / 2);
        var lake = Enumerable.Range(0, 200 * 200)
            .Select(i => centre + new Vector2(i % 200 - 100, i / 200 - 100) * 4)
            .Where(p => !world.Nav.IsFree(p, crossesWater: false) && world.Nav.IsFree(p, crossesWater: true))
            .MinBy(p => Vector2.Distance(p, centre));
        var start = FreePointNear(world, new Vector2(445, 1250));

        var path = world.Nav.FindPath(start, lake, crossesWater: false);

        Assert.NotEmpty(path);
        Assert.True(world.Nav.IsFree(path[^1], crossesWater: false));
        Assert.True(Vector2.Distance(path[^1], lake) < Vector2.Distance(start, lake));
        AssertWalkable(world, start, path);
        Assert.Equal(0, world.Nav.GaveUp);
    }

    [Fact]
    public void A_machine_set_down_on_rock_walks_out_first()
    {
        var world = NewMatch("m_desert");
        var onRock = new Vector2(800, 840);
        Assert.False(world.Nav.IsFree(onRock, crossesWater: false));

        var path = world.Nav.FindPath(onRock, new Vector2(800, 818), crossesWater: false);

        Assert.True(path.Count >= 2);
        Assert.True(world.Nav.IsFree(path[0], crossesWater: false));
        AssertWalkable(world, path[0], path.Skip(1).ToList());
    }

    [Fact]
    public void A_group_sent_to_one_place_shares_one_search()
    {
        var world = NewMatch("m_green");
        var home = FreePointNear(world, new Vector2(445, 1250));
        var machines = Enumerable.Range(0, 40)
            .Select(i => world.AddMachine(Race.Red, FreePointNear(world, home + new Vector2(i % 8 * 6, i / 8 * 6)), "AGGRESSOR", "GRUNT", 2, "L_BOLTER"))
            .ToList();
        var far = FreePointNear(world, new Vector2(1480, 480));

        world.Issue(new MoveCommand(Race.Red, machines.Select(m => m.Id).ToList(), far));
        world.Step();

        Assert.All(machines, m => Assert.NotEmpty(m.Path));
        Assert.All(machines, m => AssertWalkable(world, m.Position, m.Path.Skip(m.PathIndex).ToList()));
        // One A* for the first machine, one shared search for the others.
        Assert.InRange(world.Nav.Searches, 1, 3);
    }

    [Fact]
    public void Nearest_enemy_prefers_armed_machines_then_distance()
    {
        var world = NewMatch("m_desert");
        var at = FreePointNear(world, new Vector2(800, 700));
        var scout = world.AddMachine(Race.Red, at, "AGGRESSOR", "GRUNT", 2, "L_BOLTER");
        var unarmed = world.AddMachine(Race.Blue, at + new Vector2(10, 0), "CONSTRUCTOR", "DOZER", 1, null);
        world.AddMachine(Race.Blue, at + new Vector2(30, 0), "AGGRESSOR", "GRUNT", 2, "L_BOLTER");
        var armedNear = world.AddMachine(Race.Blue, at + new Vector2(0, 20), "AGGRESSOR", "GRUNT", 2, "L_BOLTER");
        world.Step();

        Assert.Same(armedNear, world.NearestEnemy(scout, 60));
        Assert.Same(unarmed, world.NearestEnemy(scout, 12));
        Assert.Null(world.NearestEnemy(scout, 2));
    }
}
