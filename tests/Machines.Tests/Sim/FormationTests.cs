using System.Numerics;
using Machines.Import.Model;
using Machines.Sim;
using Machines.Sim.World;

namespace Machines.Tests.Sim;

/// <summary>
/// Machines keep out of one another as in the original (MachLogMachineMotionSequencer, MachLogGroupMoveInfo): a group
/// drives side by side, those in the way make room, and nobody drives through anybody.
/// </summary>
public class FormationTests
{
    private static readonly Lazy<GameData> DataLoader = new(() => GameData.Load(RepoPaths.OriginalDataFolder()));

    internal static GameWorld NewMatch(string map = "m_green", MatchOptions? options = null) =>
        MatchSetup.Create(DataLoader.Value, map, map,
            new Dictionary<Race, Controller> { [Race.Red] = Controller.Player, [Race.Blue] = Controller.Player }, seed: 3, options);

    internal static Vector2 FreePointNear(GameWorld world, Vector2 around)
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

    /// <summary>Twelve grunts in four columns, six metres apart, as the reproduction of the bug set them up.</summary>
    private static List<Machine> Grunts(GameWorld world, Vector2 home, int count = 12) =>
        Enumerable.Range(0, count)
            .Select(i => world.AddMachine(Race.Red, FreePointNear(world, home + new Vector2(i % 4 * 6, i / 4 * 6)), "AGGRESSOR", "GRUNT", 2, "L_BOLTER"))
            .ToList();

    /// <summary>Pairs of machines closer than both their radii.</summary>
    internal static List<(Machine A, Machine B, float Distance)> Overlaps(IReadOnlyList<Machine> machines)
    {
        var pairs = new List<(Machine, Machine, float)>();
        for (var i = 0; i < machines.Count; i++)
            for (var j = i + 1; j < machines.Count; j++)
            {
                var distance = Vector2.Distance(machines[i].Position, machines[j].Position);
                if (distance < machines[i].Radius + machines[j].Radius - 0.01f)
                    pairs.Add((machines[i], machines[j], distance));
            }
        return pairs;
    }

    private static void AssertNeverOverlap(GameWorld world, IReadOnlyList<Machine> machines, int ticks, Func<bool>? done = null)
    {
        for (var t = 0; t < ticks; t++)
        {
            world.Step();
            var overlaps = Overlaps(machines);
            Assert.True(overlaps.Count == 0,
                $"tick {world.Tick}: {string.Join(", ", overlaps.Select(o => $"{o.A.Id}/{o.B.Id} {o.Distance:F2} m"))}");
            if (done?.Invoke() == true)
                return;
        }
    }

    [Fact]
    public void A_group_never_drives_into_itself()
    {
        var world = NewMatch();
        var machines = Grunts(world, FreePointNear(world, new Vector2(445, 1250)));
        var far = FreePointNear(world, new Vector2(1480, 480));
        world.Step();

        world.Issue(new MoveCommand(Race.Red, machines.Select(m => m.Id).ToList(), far));
        AssertNeverOverlap(world, machines, 20 * 300, () => machines.All(m => m.Order is IdleOrder));

        Assert.All(machines, m => Assert.IsType<IdleOrder>(m.Order));
        Assert.All(machines, m => Assert.InRange(Vector2.Distance(m.Position, far), 0, 80));
    }

    [Fact]
    public void A_group_rounds_corners_side_by_side()
    {
        // The pairs of members whose ways share a corner (or the first corner), with the original's motion and before it.
        int SharedCorners(bool original, bool firstOnly = false)
        {
            var world = NewMatch(options: new MatchOptions { OriginalMotion = original });
            var machines = Grunts(world, FreePointNear(world, new Vector2(445, 1250)));
            world.Issue(new MoveCommand(Race.Red, machines.Select(m => m.Id).ToList(), FreePointNear(world, new Vector2(1480, 480))));
            world.Step();
            var corners = machines.Select(m => m.Path.Take(firstOnly ? 1 : m.Path.Count - 1).ToList()).ToList();
            var shared = 0;
            for (var i = 0; i < corners.Count; i++)
                for (var j = i + 1; j < corners.Count; j++)
                    if (corners[i].Any(a => corners[j].Any(b => Vector2.Distance(a, b) < 1)))
                        shared++;
            return shared;
        }

        // Each keeps its place in the group round the corners (calculateGroupMovePortalPoint), where before they all
        // drove over the same corner points. Where its place would be in a wall, it keeps the corner, as in the original.
        var before = SharedCorners(original: false);
        Assert.True(before > 30);
        Assert.True(SharedCorners(original: true) <= before / 2);
        Assert.Equal(0, SharedCorners(original: true, firstOnly: true));
    }

    /// <summary>The middle of an open stretch of m_green, east to west.</summary>
    private static Vector2 Open(GameWorld world) => FreePointNear(world, new Vector2(370, 270));

    private static void Run(GameWorld world, IReadOnlyList<Machine> watched, float seconds, Func<bool>? done = null) =>
        AssertNeverOverlap(world, watched, (int)(seconds * GameWorld.TicksPerSecond), done);

    [Fact]
    public void A_resting_machine_of_the_same_side_makes_way()
    {
        var world = NewMatch();
        var at = Open(world);
        var mover = world.AddMachine(Race.Red, at - new Vector2(40, 0), "CONSTRUCTOR", "DOZER", 1, null);
        var resting = world.AddMachine(Race.Red, at, "CONSTRUCTOR", "DOZER", 1, null);
        world.Step();

        // Sent to the very place another of its side stands idle: that one steps aside (moveOutOfWay).
        world.Issue(new MoveCommand(Race.Red, [mover.Id], at));
        Run(world, [mover, resting], 30, () => mover.Order is IdleOrder);

        Assert.IsType<IdleOrder>(mover.Order);
        Assert.True(Vector2.Distance(mover.Position, at) < 0.5f, $"ended at {mover.Position}");
        Assert.True(Vector2.Distance(resting.Position, at) >= mover.Radius + resting.Radius - 0.01f);
    }

    [Fact]
    public void A_resting_enemy_is_driven_round()
    {
        var world = NewMatch();
        var at = Open(world);
        var mover = world.AddMachine(Race.Red, at - new Vector2(40, 0), "CONSTRUCTOR", "DOZER", 1, null);
        var enemy = world.AddMachine(Race.Blue, at, "CONSTRUCTOR", "DOZER", 1, null);
        world.Step();

        world.Issue(new MoveCommand(Race.Red, [mover.Id], at + new Vector2(40, 0)));
        var widest = 0f;
        AssertNeverOverlap(world, [mover, enemy], 20 * 30, () =>
        {
            widest = MathF.Max(widest, MathF.Abs(mover.Position.Y - at.Y));
            return mover.Order is IdleOrder;
        });

        // It swerves round (trySwerveMove), keeping its distance from another side's machine, and the enemy stays put.
        Assert.True(Vector2.Distance(mover.Position, at + new Vector2(40, 0)) < 0.5f, $"ended at {mover.Position}");
        Assert.True(widest > 5, $"swerved only {widest} m");
        Assert.Equal(at, enemy.Position);
    }

    [Fact]
    public void Head_on_machines_get_past_each_other()
    {
        var world = NewMatch();
        var at = Open(world);
        var red = world.AddMachine(Race.Red, at - new Vector2(40, 0), "CONSTRUCTOR", "DOZER", 1, null);
        var blue = world.AddMachine(Race.Blue, at + new Vector2(40, 0), "CONSTRUCTOR", "DOZER", 1, null);
        world.Step();

        world.Issue(new MoveCommand(Race.Red, [red.Id], at + new Vector2(40, 0)));
        world.Issue(new MoveCommand(Race.Blue, [blue.Id], at - new Vector2(40, 0)));
        Run(world, [red, blue], 40, () => red.Order is IdleOrder && blue.Order is IdleOrder);

        Assert.True(Vector2.Distance(red.Position, at + new Vector2(40, 0)) < 0.5f, $"red ended at {red.Position}");
        Assert.True(Vector2.Distance(blue.Position, at - new Vector2(40, 0)) < 0.5f, $"blue ended at {blue.Position}");
    }

    /// <summary>
    /// A grunt behind a slower dozer on one line, both sent east: returns how far the grunt strayed off the line and
    /// how long it first stood still on the way, in seconds.
    /// </summary>
    private static (float Strayed, float FirstWait) BehindASlowerOne(bool oneCommand, Controller controller)
    {
        var world = NewMatch();
        world.Races[Race.Red].Controller = controller;
        var at = Open(world) - new Vector2(60, 0);
        var grunt = world.AddMachine(Race.Red, at, "AGGRESSOR", "GRUNT", 1, "R_BOLTER");
        var dozer = world.AddMachine(Race.Red, at + new Vector2(10, 0), "CONSTRUCTOR", "DOZER", 1, null);
        world.Step();
        var target = at + new Vector2(125, 0);
        if (oneCommand)
            world.Issue(new MoveCommand(Race.Red, [grunt.Id, dozer.Id], target));
        else
        {
            world.Issue(new MoveCommand(Race.Red, [dozer.Id], target + new Vector2(5, 0)));
            world.Issue(new MoveCommand(Race.Red, [grunt.Id], target - new Vector2(5, 0)));
        }
        var (strayed, still, first, moved) = (0f, 0, 0, false);
        var last = grunt.Position;
        AssertNeverOverlap(world, [grunt, dozer], 20 * 40, () =>
        {
            strayed = MathF.Max(strayed, MathF.Abs(grunt.Position.Y - at.Y));
            if (grunt.Position != last)
            {
                if (still > 0 && first == 0)
                    first = still;
                still = 0;
                moved = true;
            }
            else if (moved && grunt.Order is MoveOrder)
                still++;
            last = grunt.Position;
            return grunt.Order is IdleOrder && dozer.Order is IdleOrder;
        });
        return (strayed, first / (float)GameWorld.TicksPerSecond);
    }

    [Fact]
    public void Group_mates_wait_rather_than_step_aside()
    {
        var (sameCommand, _) = BehindASlowerOne(oneCommand: true, Controller.Player);
        var (otherCommands, _) = BehindASlowerOne(oneCommand: false, Controller.Player);

        // Sent together, the grunt waits behind the dozer (objectHasSameCommandId); sent apart, it steps aside.
        Assert.True(sameCommand < 0.1f, $"strayed {sameCommand} m behind a group mate");
        Assert.True(otherCommands > 1, $"strayed only {otherCommands} m behind another's machine");
    }

    [Fact]
    public void Player_machines_retry_sooner_than_ai()
    {
        var (_, player) = BehindASlowerOne(oneCommand: true, Controller.Player);
        var (_, ai) = BehindASlowerOne(oneCommand: true, Controller.Ai);

        // Held up by a moving group mate, the player's machine tries again half a second later; the AI's waits two.
        Assert.Equal(0.5f, player, 1);
        Assert.Equal(2f, ai, 1);
    }

    [Fact]
    public void Two_groups_sent_to_one_place_end_apart()
    {
        var world = NewMatch();
        var home = FreePointNear(world, new Vector2(445, 1250));
        var first = Grunts(world, home, 6);
        var second = Grunts(world, home + new Vector2(0, 30), 6);
        var all = first.Concat(second).ToList();
        var target = FreePointNear(world, home + new Vector2(200, -60));
        world.Step();

        world.Issue(new MoveCommand(Race.Red, first.Select(m => m.Id).ToList(), target));
        Run(world, all, 60, () => first.All(m => m.Order is IdleOrder));
        world.Issue(new MoveCommand(Race.Red, second.Select(m => m.Id).ToList(), target));
        // They wait for each other where the places are taken, and look for a new way only after 10 s without headway.
        Run(world, all, 150, () => all.All(m => m.Order is IdleOrder));

        // The second lot finds the places taken: the first make way, or the second step aside or take places near by.
        Assert.All(all, m => Assert.IsType<IdleOrder>(m.Order));
        Assert.All(all, m => Assert.InRange(Vector2.Distance(m.Position, target), 0, 60));
    }

    [Fact]
    public void A_short_group_move_never_drives_into_itself()
    {
        var world = NewMatch();
        var home = FreePointNear(world, new Vector2(445, 1250));
        var machines = Grunts(world, home);
        world.Step();

        world.Issue(new MoveCommand(Race.Red, machines.Select(m => m.Id).ToList(), FreePointNear(world, home + new Vector2(260, -80))));
        AssertNeverOverlap(world, machines, 20 * 60, () => machines.All(m => m.Order is IdleOrder));

        Assert.All(machines, m => Assert.IsType<IdleOrder>(m.Order));
    }
}
