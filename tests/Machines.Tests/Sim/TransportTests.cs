using System.Numerics;
using Machines.Import.Model;
using Machines.Sim;
using Machines.Sim.World;

namespace Machines.Tests.Sim;

/// <summary>APCs: boarding by volume, carrying, deploying, dying with their passengers.</summary>
public class TransportTests
{
    private static readonly Lazy<GameData> DataLoader = new(() => GameData.Load(RepoPaths.OriginalDataFolder()));

    private static GameWorld NewMatch() =>
        MatchSetup.Create(DataLoader.Value, "m_desert", "m_desert",
            new Dictionary<Race, Controller> { [Race.Red] = Controller.Player, [Race.Blue] = Controller.Player }, seed: 7);

    private static readonly Vector2 Field = new(800, 800);

    private static void Run(GameWorld world, float seconds, Func<bool>? until = null)
    {
        for (var i = 0; i < seconds * GameWorld.TicksPerSecond; i++)
        {
            world.Step();
            if (until?.Invoke() == true)
                return;
        }
    }

    private static Machine Grunt(GameWorld world, Vector2 at) => world.AddMachine(Race.Red, at, "AGGRESSOR", "GRUNT", 1, "R_BOLTER");

    [Fact]
    public void Machines_board_an_apc_ride_along_and_deploy_at_the_target()
    {
        var world = NewMatch();
        var apc = world.AddMachine(Race.Red, Field, "APC", null, 2, null);
        var a = Grunt(world, Field + new Vector2(20, 0));
        var b = Grunt(world, Field + new Vector2(0, 20));

        world.Issue(new BoardCommand(Race.Red, [a.Id, b.Id], apc.Id));
        Run(world, 20, () => apc.Passengers.Count == 2);
        Assert.Equal([a.Id, b.Id], apc.Passengers.Order());
        Assert.Equal(apc.Id, a.Inside);
        Assert.Equal(20, world.CarriedVolume(apc));

        var target = Field + new Vector2(120, 40);
        world.Issue(new DeployCommand(Race.Red, [apc.Id], target));
        Run(world, 40, () => apc.Passengers.Count == 0);

        Assert.Empty(apc.Passengers);
        Assert.Null(a.Inside);
        Assert.InRange(Vector2.Distance(a.Position, target), 0, 30);
        Assert.InRange(Vector2.Distance(b.Position, target), 0, 30);
        Assert.NotEqual(a.Position, b.Position);
    }

    [Fact]
    public void An_apc_takes_only_what_fits_its_capacity()
    {
        var world = NewMatch();
        // A level-5 APC carries 60 volume: a Behemoth (120) does not fit, grunts (10) do.
        var apc = world.AddMachine(Race.Red, Field, "APC", null, 5, null);
        var behemoth = world.AddMachine(Race.Red, Field + new Vector2(25, 0), "CONSTRUCTOR", "BEHEMOTH", 4, null);
        var grunt = Grunt(world, Field + new Vector2(0, 20));

        world.Issue(new BoardCommand(Race.Red, [behemoth.Id, grunt.Id], apc.Id));
        Run(world, 20);

        Assert.Equal([grunt.Id], apc.Passengers);
        Assert.Null(behemoth.Inside);
    }

    [Fact]
    public void Passengers_die_with_their_apc()
    {
        var world = NewMatch();
        var apc = world.AddMachine(Race.Red, Field, "APC", null, 2, null);
        var grunt = Grunt(world, Field + new Vector2(15, 0));
        world.Issue(new BoardCommand(Race.Red, [grunt.Id], apc.Id));
        Run(world, 20, () => grunt.Inside is not null);

        world.Damage(apc, 100000, Race.Blue);
        Run(world, 1);

        Assert.Null(world.Find(apc.Id));
        Assert.Null(world.Find(grunt.Id));
    }

    [Fact]
    public void A_nearly_destroyed_apc_lets_its_passengers_out()
    {
        var world = NewMatch();
        var apc = world.AddMachine(Race.Red, Field, "APC", null, 2, null);
        var grunt = Grunt(world, Field + new Vector2(15, 0));
        world.Issue(new BoardCommand(Race.Red, [grunt.Id], apc.Id));
        Run(world, 20, () => grunt.Inside is not null);

        apc.HitPoints = apc.MaxHitPoints / 20;
        world.Step();

        Assert.Null(grunt.Inside);
        Assert.Empty(apc.Passengers);
    }

    [Fact]
    public void Machines_too_big_for_the_doors_stay_out_of_a_garrison()
    {
        var world = NewMatch();
        var garrison = world.AddConstruction(Race.Red, Field, "GARRISON", null, 1, 0, complete: true);
        var ninja = world.AddMachine(Race.Red, Field + new Vector2(0, 40), "AGGRESSOR", "NINJA", 5, "L_MULTI_LAUNCHER7_T_GORILLA_PUNCH_R_MULTI_LAUNCHER7");

        world.Issue(new EnterGarrisonCommand(Race.Red, [ninja.Id], garrison.Id));
        world.Step();

        Assert.Contains(world.Rejected, r => r.Reason.Contains("too big"));
    }
}
