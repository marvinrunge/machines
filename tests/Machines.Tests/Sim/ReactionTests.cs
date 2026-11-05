using System.Numerics;
using System.Reflection;
using Machines.Import.Model;
using Machines.Sim;
using Machines.Sim.World;

namespace Machines.Tests.Sim;

/// <summary>Alertness, what a hit sets off, calls for help, counterattacks and fear as the original has them.</summary>
public class ReactionTests
{
    private static readonly Lazy<GameData> DataLoader = new(() => GameData.Load(RepoPaths.OriginalDataFolder()));

    private static GameWorld NewMatch(Controller red = Controller.Player) =>
        MatchSetup.Create(DataLoader.Value, "m_moon", "m_moon",
            new Dictionary<Race, Controller> { [Race.Red] = red, [Race.Blue] = Controller.Player }, seed: 7);

    private static void Run(GameWorld world, float seconds, Func<bool>? until = null)
    {
        for (var i = 0; i < seconds * GameWorld.TicksPerSecond; i++)
        {
            world.Step();
            if (until?.Invoke() == true)
                return;
        }
    }

    /// <summary>The middle of an open square of ground 480 metres across.</summary>
    private static Vector2 Open(GameWorld world)
    {
        var (width, depth) = (world.Planet.Surface.Width, world.Planet.Surface.Depth);
        for (var y = 240f; y < depth - 240; y += 10)
            for (var x = 240f; x < width - 240; x += 10)
                if (world.Nav.IsAreaFree((new Vector2(x, y) - new Vector2(240), new Vector2(x, y) + new Vector2(240))))
                    return new Vector2(x, y);
        throw new InvalidOperationException("no open ground");
    }

    private static Machine Grunt(GameWorld world, Race race, Vector2 at) => world.AddMachine(race, at, "AGGRESSOR", "GRUNT", 2, "L_BOLTER");

    [Fact]
    public void Alertness_falls_to_a_floor_set_by_the_machines_level()
    {
        var world = NewMatch();
        var grunt = Grunt(world, Race.Red, Open(world));
        Assert.Equal(100, grunt.Alert.Level);

        Run(world, 10);
        Assert.InRange(grunt.Alert.Level, 50, 75);

        Run(world, 30);
        Assert.Equal(grunt.Stats.HardwareLevel * 4 + 10, grunt.Alert.Level);
    }

    [Fact]
    public void Machines_on_patrol_or_standing_their_ground_stay_wide_awake_and_moving_ones_half_so()
    {
        var world = NewMatch();
        var at = Open(world);
        var standing = Grunt(world, Race.Red, at);
        var patrol = Grunt(world, Race.Red, at + new Vector2(20, 0));
        var moving = Grunt(world, Race.Red, at + new Vector2(40, 0));
        world.Issue(new StandGroundCommand(Race.Red, [standing.Id]));
        world.Issue(new PatrolCommand(Race.Red, [patrol.Id], [at + new Vector2(20, 150), at + new Vector2(20, -150)]));
        world.Issue(new MoveCommand(Race.Red, [moving.Id], at + new Vector2(190, 190)));

        Run(world, 25);

        Assert.Equal(125, standing.Alert.Level);
        Assert.InRange(patrol.Alert.Level, 115, 125);
        Assert.InRange(moving.Alert.Level, 41, 45);
    }

    [Fact]
    public void A_hit_from_up_to_150_m_is_answered_and_from_further_left_be()
    {
        var world = NewMatch();
        var at = Open(world);
        var near = Grunt(world, Race.Red, at);
        var shooter = Grunt(world, Race.Blue, at + new Vector2(140, 0));
        shooter.Order = new StandGroundOrder();
        world.Damage(near, 1, Race.Blue, shooter);
        Assert.Equal(shooter.Id, Assert.IsType<AttackOrder>(near.Order).Target);

        var far = Grunt(world, Race.Red, at + new Vector2(0, -60));
        var distant = Grunt(world, Race.Blue, far.Position - new Vector2(170, 0));
        world.Damage(far, 1, Race.Blue, distant);
        Assert.IsNotType<AttackOrder>(far.Order);
    }

    [Fact]
    public void Being_hit_puts_a_machine_on_full_alert()
    {
        var world = NewMatch();
        var at = Open(world);
        var grunt = Grunt(world, Race.Red, at);
        Run(world, 40);

        world.Damage(grunt, 1, Race.Blue, Grunt(world, Race.Blue, at + new Vector2(170, 0)));

        Assert.Equal(125, grunt.Alert.Level);
    }

    [Fact]
    public void A_counterattack_given_up_ends_with_the_machine_back_where_it_stood()
    {
        var world = NewMatch();
        var at = Open(world);
        var grunt = Grunt(world, Race.Red, at);
        var runner = world.AddMachine(Race.Blue, at + new Vector2(140, 0), "AGGRESSOR", "GRUNT", 2, "L_BOLTER");
        runner.DefCon = DefCon.High;

        world.Damage(grunt, 1, Race.Blue, runner);
        var attack = Assert.IsType<AttackOrder>(grunt.Order);
        Assert.IsType<ReturnOrder>(attack.Back!.Order);

        // The target is gone: the machine pauses, then drives back.
        runner.HitPoints = 0;
        Run(world, 2);
        Assert.IsType<ReturnOrder>(grunt.Order);
        Run(world, 60, () => grunt.Order is IdleOrder);
        Assert.IsType<IdleOrder>(grunt.Order);
        Assert.InRange(Vector2.Distance(grunt.Position, at), 0, 6);
    }

    [Fact]
    public void A_counterattack_on_a_move_takes_the_move_up_again()
    {
        var world = NewMatch();
        var at = Open(world);
        var grunt = Grunt(world, Race.Red, at);
        var goal = at + new Vector2(0, 150);
        world.Issue(new MoveCommand(Race.Red, [grunt.Id], goal));
        world.Step();
        // Only once its order is some seconds old does a machine turn on what hits it (its virtual DefCon).
        Run(world, 8);
        var shooter = Grunt(world, Race.Blue, grunt.Position + new Vector2(100, 0));

        world.Damage(grunt, 1, Race.Blue, shooter);
        Assert.IsType<MoveOrder>(Assert.IsType<AttackOrder>(grunt.Order).Back!.Order);
        shooter.HitPoints = 0;
        Run(world, 2);

        Assert.Equal(goal, Assert.IsType<MoveOrder>(grunt.Order).Target);
    }

    [Fact]
    public void A_patrol_goes_on_after_a_fight()
    {
        var world = NewMatch();
        var at = Open(world);
        var grunt = Grunt(world, Race.Red, at);
        world.Issue(new PatrolCommand(Race.Red, [grunt.Id], [at + new Vector2(0, 150), at + new Vector2(0, -150)]));
        Run(world, 3);
        var enemy = world.AddMachine(Race.Blue, grunt.Position + new Vector2(25, 0), "CONSTRUCTOR", "DOZER", 1, null);
        enemy.DefCon = DefCon.High;

        Run(world, 10, () => grunt.Order is AttackOrder);
        Assert.IsType<AttackOrder>(grunt.Order);
        enemy.HitPoints = 0;
        Run(world, 2);

        Assert.IsType<PatrolOrder>(grunt.Order);
    }

    [Fact]
    public void A_death_alerts_the_friends_within_100_m()
    {
        var world = NewMatch();
        var at = Open(world);
        var victim = Grunt(world, Race.Red, at);
        var near = Grunt(world, Race.Red, at + new Vector2(90, 0));
        var far = Grunt(world, Race.Red, at + new Vector2(-120, 0));
        Run(world, 40);

        world.Damage(victim, 100000, Race.Blue);

        Assert.Equal(125, near.Alert.Level);
        Assert.True(far.Alert.Level < 100);
    }

    [Fact]
    public void The_first_to_go_for_a_target_warns_its_friends()
    {
        var world = NewMatch();
        var at = Open(world);
        var spotter = Grunt(world, Race.Red, at);
        var near = Grunt(world, Race.Red, at + new Vector2(0, 90));
        var target = world.AddMachine(Race.Blue, at + new Vector2(180, 0), "CONSTRUCTOR", "DOZER", 1, null);
        Run(world, 40);

        world.Issue(new AttackCommand(Race.Red, [spotter.Id], target.Id));
        world.Step();

        Assert.True(near.Alert.Level > 100);
    }

    [Fact]
    public void A_shot_from_a_player_in_first_person_calls_the_targets_friends_in()
    {
        var world = NewMatch();
        var at = Open(world);
        var victim = Grunt(world, Race.Red, at);
        var friend = Grunt(world, Race.Red, at + new Vector2(0, 120));
        var player = Grunt(world, Race.Blue, at + new Vector2(60, 0));
        world.Issue(new DriveCommand(Race.Blue, player.Id, Driving: true));
        world.Step();

        world.Damage(victim, 1, Race.Blue, player);

        Assert.Equal(125, friend.Alert.Level);
        Assert.Equal(player.Id, Assert.IsType<AttackOrder>(friend.Order).Target);
    }

    [Fact]
    public void An_ai_machine_under_fire_draws_its_first_idle_aggressor_within_100_m()
    {
        var world = NewMatch(red: Controller.Ai);
        var at = Open(world);
        var caller = world.AddMachine(Race.Red, at, "CONSTRUCTOR", "DOZER", 1, null);
        var helper = Grunt(world, Race.Red, at + new Vector2(80, 0));
        var other = Grunt(world, Race.Red, at + new Vector2(-130, 0));

        world.Damage(caller, 1, Race.Blue);

        Assert.IsType<MoveOrder>(helper.Order);
        Assert.IsType<IdleOrder>(other.Order);
    }

    [Fact]
    public void A_fighter_fears_only_what_it_cannot_hit_or_what_is_over_twice_as_strong()
    {
        var world = NewMatch();
        var at = Open(world);
        var grunt = Grunt(world, Race.Red, at);
        var bee = world.AddMachine(Race.Red, at + new Vector2(-150, 0), "ADMINISTRATOR", "BOSS", 2, "T_BEE_BOMB");
        var dozer = world.AddMachine(Race.Red, at + new Vector2(0, -180), "CONSTRUCTOR", "DOZER", 1, null);
        var equal = Grunt(world, Race.Blue, at + new Vector2(30, 0));
        var titan = world.AddMachine(Race.Blue, at + new Vector2(0, 30), "AGGRESSOR", "NINJA", 5, "L_MULTI_LAUNCHER7_T_GORILLA_PUNCH_R_MULTI_LAUNCHER7");
        var flyer = world.AddMachine(Race.Blue, at + new Vector2(-150, 30), "ADMINISTRATOR", "BOSS", 1, "L_WASP_LIGHT_STING");
        world.Step();
        var fears = typeof(GameWorld).GetMethod("Fears", BindingFlags.NonPublic | BindingFlags.Instance)!;
        bool Fears(Machine machine, Machine threat) => (bool)fears.Invoke(world, [machine, threat])!;

        Assert.False(Fears(grunt, equal));
        Assert.True(Fears(grunt, titan));
        Assert.False(Fears(bee, equal));
        Assert.True(Fears(bee, flyer));
        Assert.True(Fears(dozer, equal));
    }
}
