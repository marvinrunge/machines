using System.Numerics;
using Machines.Import.Model;
using Machines.Sim;
using Machines.Sim.World;

namespace Machines.Tests.Sim;

/// <summary>Follow, emplacement orders and what is picked with Shift.</summary>
public class OrderSeriesTests
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

    private static Machine Grunt(GameWorld world, Race race, Vector2 at) => world.AddMachine(race, at, "AGGRESSOR", "GRUNT", 1, "R_BOLTER");

    private static Machine Dozer(GameWorld world, Vector2 at) => world.AddMachine(Race.Red, at, "CONSTRUCTOR", "DOZER", 1, null);

    [Fact]
    public void A_follower_stays_with_its_leader_and_stops_when_it_is_gone()
    {
        var world = NewMatch();
        var leader = Grunt(world, Race.Red, Field);
        var follower = Grunt(world, Race.Red, Field + new Vector2(-30, 0));

        world.Issue(new FollowCommand(Race.Red, [follower.Id], leader.Id));
        world.Issue(new MoveCommand(Race.Red, [leader.Id], Field + new Vector2(150, 0)));
        Run(world, 60, () => leader.Order is IdleOrder && Vector2.Distance(leader.Position, follower.Position) < 15);

        Assert.IsType<FollowOrder>(follower.Order);
        Assert.InRange(Vector2.Distance(leader.Position, follower.Position), 0, 15);

        leader.HitPoints = 0;
        Run(world, 2, () => follower.Order is IdleOrder);
        Assert.IsType<IdleOrder>(follower.Order);
    }

    [Fact]
    public void A_machine_does_not_follow_itself()
    {
        var world = NewMatch();
        var grunt = Grunt(world, Race.Red, Field);

        world.Issue(new FollowCommand(Race.Red, [grunt.Id], grunt.Id));
        Run(world, 1);

        Assert.IsType<IdleOrder>(grunt.Order);
    }

    [Fact]
    public void A_move_goes_through_the_points_picked_with_shift()
    {
        var world = NewMatch();
        var grunt = Grunt(world, Race.Red, Field);
        var via = Field + new Vector2(0, 80);
        var target = Field + new Vector2(60, 0);

        world.Issue(new MoveCommand(Race.Red, [grunt.Id], target, [via]));
        var nearest = float.MaxValue;
        Run(world, 120, () =>
        {
            nearest = MathF.Min(nearest, Vector2.Distance(grunt.Position, via));
            return grunt.Order is IdleOrder;
        });

        Assert.InRange(nearest, 0, 5);
        Assert.InRange(Vector2.Distance(grunt.Position, target), 0, 5);
    }

    [Fact]
    public void A_series_is_carried_out_step_by_step()
    {
        var world = NewMatch();
        var dozer = Dozer(world, Field);
        var first = world.AddConstruction(Race.Red, Field + new Vector2(30, 0), "GARRISON", null, 1, 0, complete: true);
        var second = world.AddConstruction(Race.Red, Field + new Vector2(-60, 0), "GARRISON", null, 1, 0, complete: true);
        first.HitPoints = first.MaxHitPoints - 2;
        second.HitPoints = second.MaxHitPoints - 2;
        world.Races[Race.Red].Bmu = 1000;

        world.Issue(new SeriesCommand(Race.Red, [dozer.Id], [new RepairCommand(Race.Red, [], first.Id), new RepairCommand(Race.Red, [], second.Id)]));
        world.Step();
        Assert.Equal(new RepairOrder(first.Id), dozer.Order);

        Run(world, 60, () => dozer.Order == new RepairOrder(second.Id));
        Assert.Equal(first.MaxHitPoints, first.HitPoints);
        Assert.Equal(new RepairOrder(second.Id), dozer.Order);
    }

    [Fact]
    public void A_new_order_takes_the_rest_of_a_series_away()
    {
        var world = NewMatch();
        var grunt = Grunt(world, Race.Red, Field);

        world.Issue(new SeriesCommand(Race.Red, [grunt.Id],
            [new MoveCommand(Race.Red, [], Field + new Vector2(20, 0)), new MoveCommand(Race.Red, [], Field + new Vector2(200, 0))]));
        world.Step();
        world.Issue(new StopCommand(Race.Red, [grunt.Id]));
        Run(world, 5);

        Assert.IsType<IdleOrder>(grunt.Order);
        Assert.InRange(Vector2.Distance(grunt.Position, Field), 0, 5);
    }

    [Fact]
    public void Sites_in_a_series_stand_at_once_and_are_built_in_turn()
    {
        var world = NewMatch();
        var dozer = Dozer(world, Field);
        world.Races[Race.Red].Bmu = 1000;
        var (a, b) = (Field + new Vector2(30, 0), Field + new Vector2(60, 40));

        world.Issue(new SeriesCommand(Race.Red, [dozer.Id],
            [new PlaceConstructionCommand(Race.Red, [], "MISSILE_EMPLACEMENT", "TURRET", 1, a, 0), new PlaceConstructionCommand(Race.Red, [], "MISSILE_EMPLACEMENT", "TURRET", 1, b, 0)]));
        world.Step();
        Assert.Empty(world.Rejected);

        var sites = world.Constructions.Where(c => c.Race == Race.Red && !c.Complete).OrderBy(c => c.Id).ToList();
        Assert.Equal(2, sites.Count);
        Assert.Equal(new ConstructOrder(sites[0].Id), dozer.Order);
        Run(world, 200, () => sites[0].Complete);
        world.Step();
        Assert.Equal(new ConstructOrder(sites[1].Id), dozer.Order);
    }

    [Fact]
    public void A_constructor_says_it_moves_on_between_sites_and_only_waits_for_a_job_after_the_last()
    {
        var world = NewMatch();
        var dozer = Dozer(world, Field);
        world.Races[Race.Red].Bmu = 1000;
        world.Issue(new SeriesCommand(Race.Red, [dozer.Id],
            [new PlaceConstructionCommand(Race.Red, [], "MISSILE_EMPLACEMENT", "TURRET", 1, Field + new Vector2(30, 0), 0), new PlaceConstructionCommand(Race.Red, [], "MISSILE_EMPLACEMENT", "TURRET", 1, Field + new Vector2(60, 40), 0)]));
        var said = new List<string>();
        Run(world, 400, () =>
        {
            said.AddRange(world.Events.Where(e => e.Kind == GameEventKind.VoiceMail && e.Actor == dozer.Id).Select(e => e.Detail!));
            return said.Contains("VID_DOZER_AWAITING_NEW_JOB");
        });

        Assert.Equal(1, said.Count(m => m == "VID_DOZER_MOVING_TO_NEXT"));
        Assert.Equal(1, said.Count(m => m == "VID_DOZER_AWAITING_NEW_JOB"));
        Assert.True(said.IndexOf("VID_DOZER_MOVING_TO_NEXT") < said.IndexOf("VID_DOZER_AWAITING_NEW_JOB"));
    }

    [Fact]
    public void An_emplacement_shoots_at_what_it_was_told_to_and_gives_it_up_on_stop()
    {
        var world = NewMatch();
        var turret = world.AddConstruction(Race.Red, Field, "MISSILE_EMPLACEMENT", "TURRET", 1, 0, complete: true);
        Assert.NotEmpty(turret.Weapons);
        var range = turret.Weapons.Max(w => w.Spec.Range);
        // Off the line to the other one, whose flames would burn it on the way.
        var near = Grunt(world, Race.Blue, Field + new Vector2(range * 0.3f, range * 0.3f));
        var far = Grunt(world, Race.Blue, Field + new Vector2(range * 0.8f, 0));
        near.Order = new StandGroundOrder();
        far.Order = new StandGroundOrder();

        world.Issue(new AttackCommand(Race.Red, [turret.Id], far.Id));
        world.Step();
        world.Step();
        Assert.Equal(far.Id, world.AimOf(turret.Id)?.Id);

        world.Issue(new StopCommand(Race.Red, [turret.Id]));
        Run(world, 1);
        // Told nothing, it keeps what it shoots at while that stays in range, then takes the nearest the next time it
        // looks about, which its alertness decides (MachLogMissileEmplacement::update).
        far.HitPoints = 0;
        for (var i = 0; i < 30 && world.AimOf(turret.Id)?.Id != near.Id; i++)
            Run(world, 1);
        Assert.Equal(near.Id, world.AimOf(turret.Id)?.Id);
    }

    [Fact]
    public void A_silo_takes_no_attack_orders()
    {
        var world = NewMatch();
        var silo = world.AddConstruction(Race.Red, Field, "MISSILE_EMPLACEMENT", "ICBM", 5, 0, complete: true);
        var turret = world.AddConstruction(Race.Red, Field, "MISSILE_EMPLACEMENT", "TURRET", 1, 0, complete: false);

        Assert.False(GameWorld.TakesOrders(silo));
        Assert.False(GameWorld.TakesOrders(turret));
    }

    [Fact]
    public void A_carrier_calls_at_each_mine_of_its_round_before_it_delivers()
    {
        var world = NewMatch();
        var smelter = world.AddConstruction(Race.Red, Field, "SMELTER", null, 1, 0, complete: true);
        var first = world.AddConstruction(Race.Red, Field + new Vector2(70, 0), "MINE", null, 1, 0, complete: true);
        var second = world.AddConstruction(Race.Red, Field + new Vector2(-70, 0), "MINE", null, 1, 0, complete: true);
        (first.Ore, second.Ore) = (2, 2);
        var carrier = world.AddMachine(Race.Red, Field + new Vector2(40, 40), "RESOURCE_CARRIER", null, 1, null);

        world.Issue(new TransportCommand(Race.Red, [carrier.Id], first.Id, smelter.Id, [second.Id]));
        Run(world, 60, () => carrier.Order is TransportOrder { Delivering: true });

        Assert.Equal(4, carrier.Ore);
        Assert.Equal(0, first.Ore, 0.001f);
        Assert.Equal(0, second.Ore, 0.001f);
    }
}
