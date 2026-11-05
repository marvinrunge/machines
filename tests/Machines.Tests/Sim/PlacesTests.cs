using System.Numerics;
using Machines.Import.Model;
using Machines.Sim;
using Machines.Sim.World;
using Xunit.Abstractions;

namespace Machines.Tests.Sim;

/// <summary>
/// Where machines stand at their work, as in the original: attackers and healers each pick a free place round the
/// target (MachLogSpacialManipulation::getNearestFreeSpacePoint), constructors take build points round the building.
/// </summary>
public class PlacesTests(ITestOutputHelper output)
{
    private static Vector2 Open(GameWorld world) => FormationTests.FreePointNear(world, new Vector2(370, 270));

    private static void Steps(GameWorld world, float seconds, Action? each = null)
    {
        for (var t = 0; t < (int)(seconds * GameWorld.TicksPerSecond); t++)
        {
            world.Step();
            each?.Invoke();
        }
    }

    private string Report(IEnumerable<Machine> machines, Vector2 centre) =>
        string.Join("; ", machines.Select(m => $"{m.Id} at {Vector2.Distance(m.Position, centre):F1} m {m.Order.GetType().Name}"));

    [Fact]
    public void Free_space_search_follows_the_clock_pattern()
    {
        var world = FormationTests.NewMatch();
        var centre = Open(world);
        Vector2 At(int sector) => centre + new Vector2(DetMath.Cos(sector * MathF.Tau / 16), DetMath.Sin(sector * MathF.Tau / 16)) * 30;

        // Straight out along the axis first, worked out with DetMath, so bit for bit the same on every computer.
        Assert.True(world.FindFreeSpace(centre, Vector2.UnitX, 30, 2, out var first));
        Assert.Equal(At(0), first);

        // A resting machine there sends it one sector round, then one sector back the other way.
        var blocker = world.AddMachine(Race.Red, At(0), "CONSTRUCTOR", "DOZER", 1, null);
        world.Step();
        Assert.True(world.FindFreeSpace(centre, Vector2.UnitX, 30, 2, out var second));
        Assert.Equal(At(1), second);
        world.AddMachine(Race.Blue, At(1), "CONSTRUCTOR", "DOZER", 1, null);
        world.Step();
        Assert.True(world.FindFreeSpace(centre, Vector2.UnitX, 30, 2, out var third));
        Assert.Equal(At(-1), third);

        // One on the move does not count.
        world.Issue(new MoveCommand(Race.Red, [blocker.Id], blocker.Position - new Vector2(60, 0)));
        world.Step();
        Assert.True(world.FindFreeSpace(centre, Vector2.UnitX, 30, 2, out var moving));
        Assert.Equal(At(0), moving);
    }

    [Fact]
    public void Attackers_spread_round_a_target()
    {
        var world = FormationTests.NewMatch();
        var home = Open(world);
        var garrison = world.AddConstruction(Race.Blue, FormationTests.FreePointNear(world, home + new Vector2(110, 0)), "GARRISON", null, 1, 0, complete: true);
        var grunts = Enumerable.Range(0, 12)
            .Select(i => world.AddMachine(Race.Red, FormationTests.FreePointNear(world, home + new Vector2(i % 4 * 6, i / 4 * 6)), "AGGRESSOR", "GRUNT", 2, "L_BOLTER"))
            .ToList();
        world.Step();

        world.Issue(new AttackCommand(Race.Red, grunts.Select(g => g.Id).ToList(), garrison.Id));
        var fired = new HashSet<int>();
        Steps(world, 40, () => fired.UnionWith(world.Events.Where(e => e.Kind == GameEventKind.Fired).Select(e => e.Actor)));

        output.WriteLine(Report(grunts, garrison.Position));
        var overlaps = FormationTests.Overlaps(grunts);
        Assert.True(overlaps.Count == 0, string.Join(", ", overlaps.Select(o => $"{o.A.Id}/{o.B.Id} {o.Distance:F2} m")));
        // One still behind a friend holds its fire and moves closer, as in the original (MachLogAttack, BEHIND_COVER).
        Assert.True(grunts.Count(g => fired.Contains(g.Id)) >= 10, $"{grunts.Count(g => fired.Contains(g.Id))} of 12 fired");
    }

    [Fact]
    public void Constructors_take_build_points()
    {
        var world = FormationTests.NewMatch();
        var home = Open(world);
        var site = world.AddConstruction(Race.Red, FormationTests.FreePointNear(world, home + new Vector2(70, 0)), "GARRISON", null, 1, 0, complete: false);
        var dozers = Enumerable.Range(0, 6)
            .Select(i => world.AddMachine(Race.Red, FormationTests.FreePointNear(world, home + new Vector2(i % 3 * 8, i / 3 * 8)), "CONSTRUCTOR", "DOZER", 1, null))
            .ToList();
        world.Step();

        world.Issue(new ConstructCommand(Race.Red, dozers.Select(d => d.Id).ToList(), site.Id));
        var built = new HashSet<int>();
        var before = site.Built;
        Steps(world, 40, () =>
        {
            foreach (var dozer in dozers)
                if (dozer.Order is ConstructOrder && dozer.Path.Count == 0 && dozer.DistanceTo(site) < 3.4f * dozer.Radius + 1)
                    built.Add(dozer.Id);
        });

        output.WriteLine(Report(dozers, site.Position) + $"; built {site.Built - before:F0}");
        var overlaps = FormationTests.Overlaps(dozers);
        Assert.True(overlaps.Count == 0, string.Join(", ", overlaps.Select(o => $"{o.A.Id}/{o.B.Id} {o.Distance:F2} m")));
        Assert.True(site.Built > before);
        Assert.Equal(6, built.Count);
        // Nobody stands at the door.
        var door = site.EntranceOutside!.Value;
        Assert.All(dozers, d => Assert.True(Vector2.Distance(d.Position, door) > d.Radius, $"{d.Id} at the door"));
    }

    [Fact]
    public void Behemoths_leave_a_point_between_them()
    {
        var world = FormationTests.NewMatch();
        var home = Open(world);
        var site = world.AddConstruction(Race.Red, FormationTests.FreePointNear(world, home + new Vector2(70, 0)), "FACTORY", "MILITARY", 1, 0, complete: false);
        var behemoths = Enumerable.Range(0, 2)
            .Select(i => world.AddMachine(Race.Red, FormationTests.FreePointNear(world, home + new Vector2(0, i * 14)), "CONSTRUCTOR", "BEHEMOTH", 4, null))
            .ToList();
        world.Step();

        world.Issue(new ConstructCommand(Race.Red, behemoths.Select(d => d.Id).ToList(), site.Id));
        Steps(world, 40);

        output.WriteLine(Report(behemoths, site.Position));
        var (min, max) = site.Footprint;
        var spacing = MathF.Max((max.X - min.X) / ((int)((max.X - min.X) / 14) + 1), (max.Y - min.Y) / ((int)((max.Y - min.Y) / 14) + 1));
        Assert.True(Vector2.Distance(behemoths[0].Position, behemoths[1].Position) > 1.5f * spacing,
            $"{Vector2.Distance(behemoths[0].Position, behemoths[1].Position):F1} m apart, points {spacing:F1} m apart");
    }

    [Fact]
    public void Healers_stand_off_round_their_patient()
    {
        var world = FormationTests.NewMatch();
        var home = Open(world);
        var patient = world.AddMachine(Race.Red, FormationTests.FreePointNear(world, home + new Vector2(60, 0)), "AGGRESSOR", "GRUNT", 2, "L_BOLTER");
        patient.HitPoints = 1;
        var healers = Enumerable.Range(0, 3)
            .Select(i => world.AddMachine(Race.Red, FormationTests.FreePointNear(world, home + new Vector2(0, i * 8)), "ADMINISTRATOR", "OVERSEER", 2, "L_PULSE_RIFLE_R_SUPERCHARGE_ADVANCED"))
            .ToList();
        world.Step();

        world.Issue(new HealCommand(Race.Red, healers.Select(h => h.Id).ToList(), patient.Id));
        var nearest = float.MaxValue;
        Steps(world, 20, () => nearest = MathF.Min(nearest, healers.Min(h => Vector2.Distance(h.Position, patient.Position))));

        output.WriteLine(Report(healers, patient.Position));
        var all = healers.Append(patient).ToList();
        var overlaps = FormationTests.Overlaps(all);
        Assert.True(overlaps.Count == 0, string.Join(", ", overlaps.Select(o => $"{o.A.Id}/{o.B.Id} {o.Distance:F2} m")));
        Assert.True(nearest >= 5 - 0.01f, $"came to {nearest:F2} m");
        Assert.True(patient.HitPoints > 1);
    }
}
