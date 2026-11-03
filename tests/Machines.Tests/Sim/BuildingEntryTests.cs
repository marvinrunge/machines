using System.Numerics;
using Machines.Import.Model;
using Machines.Sim;
using Machines.Sim.World;

namespace Machines.Tests.Sim;

/// <summary>
/// Going into buildings as in the original: driven in first person through the door and over the floor behind it
/// (MachLog1stPersonMachineHandler), or sent in, and standing at a bay there.
/// </summary>
public class BuildingEntryTests
{
    private static GameWorld NewMatch(bool originalMotion = true) =>
        FormationTests.NewMatch(options: new MatchOptions { OriginalMotion = originalMotion });

    private static Vector2 Open(GameWorld world) => FormationTests.FreePointNear(world, new Vector2(370, 270));

    private static Machine Grunt(GameWorld world, Vector2 at) => world.AddMachine(Race.Red, at, "AGGRESSOR", "GRUNT", 1, "R_BOLTER");

    private static void Drive(GameWorld world, Machine machine, float seconds, float forward = 1, Func<bool>? until = null)
    {
        for (var i = 0; i < seconds * GameWorld.TicksPerSecond && until?.Invoke() != true; i++)
        {
            world.Issue(new DriveCommand(machine.Race, machine.Id, Driving: true, forward));
            world.Step();
        }
    }

    private static void Run(GameWorld world, float seconds, Func<bool>? until = null)
    {
        for (var i = 0; i < seconds * GameWorld.TicksPerSecond && until?.Invoke() != true; i++)
            world.Step();
    }

    /// <summary>A machine before the door of a building, looking in through it.</summary>
    private static Machine BeforeDoor(GameWorld world, Construction building, Func<GameWorld, Vector2, Machine> make)
    {
        var way = Vector2.Normalize(building.EntranceInside!.Value - building.EntranceOutside!.Value);
        var machine = make(world, building.EntranceOutside.Value - way * 6);
        machine.Facing = MathF.Atan2(way.Y, way.X);
        return machine;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_driven_machine_goes_in_through_the_door_and_out_again(bool originalMotion)
    {
        var world = NewMatch(originalMotion);
        var garrison = world.AddConstruction(Race.Red, Open(world), "GARRISON", null, 1, 0, complete: true);
        var grunt = BeforeDoor(world, garrison, Grunt);

        Drive(world, grunt, 10, until: () => grunt.Inside is not null);
        Assert.Equal(garrison.Id, grunt.Inside);
        Assert.Contains(grunt.Id, garrison.Garrisoned);

        // On to the back wall, which holds it.
        Drive(world, grunt, 10);
        var (min, max) = garrison.Interior!.Value;
        Assert.InRange(grunt.Position.X, min.X, max.X);
        Assert.InRange(grunt.Position.Y, min.Y, max.Y);
        Assert.Equal(garrison.Id, grunt.Inside);

        // Turned about, it drives out by the door it came in by.
        grunt.Facing += MathF.PI;
        grunt.Position = new Vector2(grunt.Position.X, garrison.EntranceInside!.Value.Y);
        Drive(world, grunt, 10, until: () => grunt.Inside is null);
        Assert.Null(grunt.Inside);
        Assert.Empty(garrison.Garrisoned);
        Drive(world, grunt, 3);
        Assert.False(garrison.Footprint.Min.X <= grunt.Position.X && grunt.Position.X <= garrison.Footprint.Max.X);
        Assert.Null(grunt.Doorway);
    }

    [Fact]
    public void Walls_hold_a_driven_machine_on_both_sides()
    {
        var world = NewMatch();
        var garrison = world.AddConstruction(Race.Red, Open(world), "GARRISON", null, 1, 0, complete: true);
        // At the side wall, away from the door.
        var (min, max) = garrison.Footprint;
        var grunt = Grunt(world, new Vector2((min.X + max.X) / 2, min.Y - 6));
        grunt.Facing = MathF.PI / 2;

        Drive(world, grunt, 5);

        Assert.Null(grunt.Inside);
        Assert.True(grunt.Position.Y <= min.Y);
    }

    [Fact]
    public void Only_a_machine_that_fits_drives_in_and_never_into_an_enemy_building()
    {
        var world = NewMatch();
        var at = Open(world);
        var lab = world.AddConstruction(Race.Red, at, "HARDWARE_LAB", "CIVILIAN", 1, 0, complete: true);
        var behemoth = BeforeDoor(world, lab, (w, p) => w.AddMachine(Race.Red, p, "CONSTRUCTOR", "BEHEMOTH", 4, null));
        Assert.False(GameWorld.FitsThrough(behemoth, lab));
        Drive(world, behemoth, 6);
        Assert.Null(behemoth.Inside);

        var enemy = BeforeDoor(world, lab, (w, p) => w.AddMachine(Race.Blue, p + new Vector2(0, 2), "AGGRESSOR", "GRUNT", 1, "R_BOLTER"));
        Drive(world, enemy, 6);
        Assert.Null(enemy.Inside);
    }

    [Fact]
    public void An_unfinished_building_keeps_its_door_shut()
    {
        var world = NewMatch();
        var garrison = world.AddConstruction(Race.Red, Open(world), "GARRISON", null, 1, 0, complete: false);
        var grunt = BeforeDoor(world, garrison, Grunt);

        Drive(world, grunt, 6);

        Assert.Null(grunt.Inside);
    }

    [Fact]
    public void Any_machine_that_fits_goes_into_a_smelter_on_orders_and_comes_out_on_orders()
    {
        var world = NewMatch();
        var smelter = world.AddConstruction(Race.Red, Open(world), "SMELTER", null, 1, 0, complete: true);
        var grunt = Grunt(world, smelter.EntranceOutside!.Value + new Vector2(8, 0));

        world.Issue(new EnterGarrisonCommand(Race.Red, [grunt.Id], smelter.Id));
        Run(world, 30, () => grunt.Inside is not null);

        Assert.Equal(smelter.Id, grunt.Inside);
        Assert.Equal(smelter.Bays[0], grunt.Position);

        // Its one bay is taken: the next stays out.
        var second = Grunt(world, smelter.EntranceOutside.Value + new Vector2(8, 6));
        world.Issue(new EnterGarrisonCommand(Race.Red, [second.Id], smelter.Id));
        Run(world, 20);
        Assert.Null(second.Inside);

        world.Issue(new StopCommand(Race.Red, [grunt.Id]));
        world.Step();
        Assert.Null(grunt.Inside);
        Assert.Empty(smelter.Garrisoned);
    }

    [Fact]
    public void A_building_without_a_floor_inside_cannot_be_entered()
    {
        var world = NewMatch();
        var factory = world.AddConstruction(Race.Red, Open(world), "FACTORY", "CIVILIAN", 1, 0, complete: true);
        var grunt = Grunt(world, factory.EntranceOutside!.Value + new Vector2(8, 0));

        Assert.False(factory.HasInterior);
        world.Issue(new EnterGarrisonCommand(Race.Red, [grunt.Id], factory.Id));
        Run(world, 20);
        Assert.Null(grunt.Inside);
        Assert.IsType<IdleOrder>(grunt.Order);
    }

    [Fact]
    public void Technicians_take_a_research_bay_each_in_the_lab()
    {
        var world = NewMatch();
        var lab = world.AddConstruction(Race.Red, Open(world), "HARDWARE_LAB", "MILITARY", 3, 0, complete: true);
        var technicians = Enumerable.Range(0, 2)
            .Select(i => world.AddMachine(Race.Red, lab.EntranceOutside!.Value + new Vector2(10, i * 8), "TECHNICIAN", "LAB_TECH", 2, null)).ToList();

        world.Issue(new EnterLabCommand(Race.Red, technicians.Select(t => t.Id).ToList(), lab.Id));
        Run(world, 60, () => technicians.All(t => t.Inside is not null));

        Assert.All(technicians, t => Assert.Contains(t.Position, lab.Bays));
        Assert.NotEqual(technicians[0].Position, technicians[1].Position);
    }

    [Fact]
    public void A_technician_driven_into_its_lab_stays_to_work_when_let_go()
    {
        var world = NewMatch();
        var lab = world.AddConstruction(Race.Red, Open(world), "HARDWARE_LAB", "CIVILIAN", 1, 0, complete: true);
        var technician = BeforeDoor(world, lab, (w, p) => w.AddMachine(Race.Red, p, "TECHNICIAN", "LAB_TECH", 2, null));

        Drive(world, technician, 10, until: () => technician.Inside is not null);
        Assert.Equal(lab.Id, technician.Inside);
        Drive(world, technician, 0.5f);

        world.Issue(new DriveCommand(Race.Red, technician.Id, Driving: false));
        world.Step();

        Assert.Contains(technician.Id, lab.Technicians);
        Assert.Empty(lab.Garrisoned);
        Assert.Equal(lab.Bays[0], technician.Position);
        Assert.Null(technician.Doorway);
    }

    [Fact]
    public void A_machine_driven_round_a_lab_leaves_its_bay_to_the_technician()
    {
        var world = NewMatch();
        var lab = world.AddConstruction(Race.Red, Open(world), "HARDWARE_LAB", "CIVILIAN", 1, 0, complete: true);
        var grunt = BeforeDoor(world, lab, Grunt);
        Drive(world, grunt, 10, until: () => grunt.Inside is not null);

        var technician = world.AddMachine(Race.Red, lab.EntranceOutside!.Value + new Vector2(10, 0), "TECHNICIAN", "LAB_TECH", 2, null);
        world.Issue(new EnterLabCommand(Race.Red, [technician.Id], lab.Id));
        Run(world, 30, () => technician.Inside is not null);

        Assert.Contains(technician.Id, lab.Technicians);
        Assert.Equal(lab.Bays[0], technician.Position);
    }

    [Fact]
    public void A_machine_let_go_in_the_doorway_backs_out()
    {
        var world = NewMatch();
        var garrison = world.AddConstruction(Race.Red, Open(world), "GARRISON", null, 1, 0, complete: true);
        var grunt = BeforeDoor(world, garrison, Grunt);
        var edge = garrison.Footprint.Max.X - 1;

        Drive(world, grunt, 10, until: () => grunt.Position.X <= edge);
        Assert.Null(grunt.Inside);
        world.Issue(new DriveCommand(Race.Red, grunt.Id, Driving: false));
        world.Step();

        Assert.True(world.Nav.IsFree(grunt.Position, crossesWater: false));
        Assert.Null(grunt.Doorway);
    }
}
