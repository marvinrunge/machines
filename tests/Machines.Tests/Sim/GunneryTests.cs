using System.Numerics;
using Machines.Import.Model;
using Machines.Sim;
using Machines.Sim.Combat;
using Machines.Sim.World;

namespace Machines.Tests.Sim;

/// <summary>Who fires at what, when, how well, and what the rounds do where they end (MachLogCanAttack, MachLogLinearProjectile).</summary>
public class GunneryTests
{
    private static readonly Lazy<GameData> DataLoader = new(() => GameData.Load(RepoPaths.OriginalDataFolder()));

    private static GameWorld NewMatch() =>
        MatchSetup.Create(DataLoader.Value, "m_desert", "m_desert",
            new Dictionary<Race, Controller> { [Race.Red] = Controller.Player, [Race.Blue] = Controller.Player, [Race.Green] = Controller.Player }, seed: 7);

    // An open patch of m_desert, away from both bases.
    private static readonly Vector2 Field = new(800, 800);

    private static void Run(GameWorld world, float seconds, Action? each = null)
    {
        for (var i = 0; i < seconds * GameWorld.TicksPerSecond; i++)
        {
            world.Step();
            each?.Invoke();
        }
    }

    private static Machine Grunt(GameWorld world, Race race, Vector2 at, int level = 2, string combo = "R_BOLTER")
    {
        var grunt = world.AddMachine(race, at, "AGGRESSOR", "GRUNT", level, combo);
        // Left to itself it holds still, neither evading nor taking anything on.
        grunt.DefCon = DefCon.High;
        return grunt;
    }

    private static Machine Dozer(GameWorld world, Race race, Vector2 at)
    {
        var dozer = world.AddMachine(race, at, "CONSTRUCTOR", "DOZER", 1, null);
        dozer.DefCon = DefCon.High;
        return dozer;
    }

    private static void Attack(GameWorld world, Actor shooter, Actor target) =>
        world.Issue(new AttackCommand(shooter.Race, [shooter.Id], target.Id));

    // ---- bursts -----------------------------------------------------------------------------------

    [Fact]
    public void The_reload_runs_from_the_start_of_a_burst()
    {
        var world = NewMatch();
        var (shooter, target) = (Grunt(world, Race.Red, Field, combo: "R_HEAVY_BOLTER1"), Dozer(world, Race.Blue, Field + new Vector2(20, 0)));
        target.HitPoints = 1000000;
        Attack(world, shooter, target);
        var fired = new List<float>();
        Run(world, 12, () => fired.AddRange(world.Events.Where(e => e.Kind == GameEventKind.Fired).Select(_ => world.Time)));

        Assert.True(fired.Count >= 6);
        var reload = shooter.Weapons[0].Spec.ReloadTime;
        Assert.All(fired.Zip(fired.Skip(1)), gap => Assert.InRange(gap.Second - gap.First, reload - 0.11f, reload + 0.11f));
    }

    [Fact]
    public void A_variable_burst_has_from_one_round_to_all()
    {
        var world = NewMatch();
        var shooter = world.AddMachine(Race.Red, Field, "AGGRESSOR", "KNIGHT", 3, "LR_PLASMA_CANNON1_X2");
        var target = Dozer(world, Race.Blue, Field + new Vector2(30, 0));
        target.HitPoints = 1000000;
        var spec = shooter.Weapons[0].Spec;
        Assert.Equal("VARIABLE", spec.BurstTiming);
        Attack(world, shooter, target);

        var bursts = new List<int>();
        var (start, rounds) = (0f, 0);
        Run(world, 80, () =>
        {
            foreach (var _ in world.Events.Where(e => e.Kind == GameEventKind.Fired && e.Actor == shooter.Id && e.Detail == spec.TypeName))
            {
                if (world.Time - start > spec.BurstDuration + 0.2f)
                {
                    if (rounds > 0)
                        bursts.Add(rounds);
                    (start, rounds) = (world.Time, 0);
                }
                rounds++;
            }
        });

        // Two weapons fire together, so that a burst of the same number of rounds comes twice over.
        Assert.Contains(bursts, b => b == 2);
        Assert.Contains(bursts, b => b == 4);
    }

    [Fact]
    public void A_flamethrower_throws_its_balls_at_once_and_they_burn_what_they_reach()
    {
        var world = NewMatch();
        var turret = world.AddConstruction(Race.Red, Field, "MISSILE_EMPLACEMENT", "TURRET", 1, 0, complete: true);
        Assert.Contains(turret.Weapons, w => w.Spec.TypeName == "FLAME_THROWER1");
        var target = Dozer(world, Race.Blue, Field + new Vector2(10, 0));
        target.HitPoints = 1000000;
        var fired = 0;
        var hits = 0;
        Run(world, 12, () =>
        {
            fired += world.Events.Count(e => e.Kind == GameEventKind.Fired);
            hits += world.Events.Count(e => e.Kind == GameEventKind.Hit && e.Actor == target.Id);
        });

        Assert.True(fired > 0);
        Assert.True(hits > fired);
    }

    // ---- aim --------------------------------------------------------------------------------------

    [Fact]
    public void Inaccuracy_starts_from_the_accuracy_and_hits_shake_it_up_to_75()
    {
        var aim = new Gunnery(70);
        Assert.Equal(30, aim.Inaccuracy);
        aim.Shake(100);
        Assert.Equal(75, aim.Inaccuracy);
        aim.Settle(100);
        Assert.Equal(30, aim.Inaccuracy);
        aim.Learn(10);
        aim.Settle(100);
        Assert.Equal(20, aim.Inaccuracy);
    }

    [Fact]
    public void A_hit_spoils_a_fighting_machines_aim_by_up_to_40_points()
    {
        var world = NewMatch();
        var grunt = Grunt(world, Race.Red, Field);
        var aim = world.GunneryOf(grunt);
        var before = aim.Inaccuracy;
        world.Damage(grunt, grunt.MaxHitPoints / 2 + grunt.Armour, Race.Blue);
        var lost = grunt.MaxHitPoints - grunt.HitPoints;
        Assert.True(lost > 0);
        Assert.Equal(before + lost / (float)grunt.MaxHitPoints * 40, aim.Inaccuracy, 2);
    }

    [Fact]
    public void A_machine_that_keeps_on_aiming_stops_missing()
    {
        var world = NewMatch();
        var (shooter, target) = (Grunt(world, Race.Red, Field, combo: "R_HEAVY_BOLTER1"), Dozer(world, Race.Blue, Field + new Vector2(25, 0)));
        target.HitPoints = 1000000;
        Attack(world, shooter, target);
        Run(world, 12);
        var (fired, hit) = (0, 0);
        Run(world, 20, () =>
        {
            fired += world.Events.Count(e => e.Kind == GameEventKind.Fired);
            hit += world.Events.Count(e => e.Kind == GameEventKind.Hit && e.Actor == target.Id);
        });

        Assert.True(fired >= 10);
        Assert.Equal(fired, hit);
    }

    [Fact]
    public void A_bolt_that_misses_can_hit_what_stands_behind_the_target()
    {
        var hits = 0;
        for (var seed = 0; seed < 8 && hits == 0; seed++)
        {
            var world = MatchSetup.Create(DataLoader.Value, "m_desert", "m_desert",
                new Dictionary<Race, Controller> { [Race.Red] = Controller.Player, [Race.Blue] = Controller.Player }, seed);
            var shooter = Grunt(world, Race.Red, Field, combo: "R_HEAVY_BOLTER1");
            var target = Dozer(world, Race.Blue, Field + new Vector2(25, 0));
            target.HitPoints = 1000000;
            var behind = Dozer(world, Race.Blue, Field + new Vector2(25 + target.Radius + 4, 0));
            behind.HitPoints = 1000000;
            world.GunneryOf(shooter).Shake(100);
            Attack(world, shooter, target);
            Run(world, 6, () => hits += world.Events.Count(e => e.Kind == GameEventKind.Hit && e.Actor == behind.Id));
        }
        Assert.True(hits > 0);
    }

    // ---- where rounds end -------------------------------------------------------------------------

    [Fact]
    public void A_missile_bursts_on_a_friend_in_its_way_and_hurts_all_round()
    {
        var world = NewMatch();
        var shooter = world.AddMachine(Race.Red, Field, "AGGRESSOR", "NINJA", 5, "L_MULTI_LAUNCHER7_T_GORILLA_PUNCH_R_MULTI_LAUNCHER7");
        shooter.DefCon = DefCon.High;
        var target = Dozer(world, Race.Blue, Field + new Vector2(70, 0));
        target.HitPoints = 1000000;
        Attack(world, shooter, target);
        while (world.Projectiles.Count == 0)
            world.Step();

        // Behind the round's back a friend drives into its way, low down where the round has come to, an enemy beside it
        // too far from the target for the burst there to reach.
        var friend = Dozer(world, Race.Red, Field + new Vector2(64, 0));
        var near = Dozer(world, Race.Blue, Field + new Vector2(60, 7));
        (friend.HitPoints, near.HitPoints) = (1000000, 1000000);
        var hit = new HashSet<int>();
        Run(world, 2, () => hit.UnionWith(world.Events.Where(e => e.Kind == GameEventKind.Hit).Select(e => e.Actor)));

        Assert.Contains(friend.Id, hit);
        Assert.Contains(near.Id, hit);
    }

    [Fact]
    public void A_round_spent_at_its_range_bursts_where_it_is()
    {
        var world = NewMatch();
        var shooter = world.AddMachine(Race.Red, Field, "AGGRESSOR", "KNIGHT", 3, "LR_PLASMA_CANNON1_X2");
        shooter.DefCon = DefCon.High;
        var target = Dozer(world, Race.Blue, Field + new Vector2(40, 0));
        target.HitPoints = 1000000;
        Attack(world, shooter, target);
        while (world.Projectiles.Count == 0)
            world.Step();
        var round = world.Projectiles[0];
        // The target's gone, and the round flies on over where it stood: at the end of its flight something stands.
        var end = round.Start + round.Direction * round.Weapon.Range;
        target.HitPoints = 0;
        var there = Dozer(world, Race.Green, end + round.Direction * 2);
        there.HitPoints = 1000000;
        var before = there.HitPoints;
        Run(world, round.Weapon.Range / round.Weapon.ProjectileSpeed + 0.5f);

        Assert.True(there.HitPoints < before);
    }

    [Fact]
    public void Land_mines_go_up_in_a_burst_at_twice_its_reach_and_in_a_blast()
    {
        var world = NewMatch();
        world.LandMines.Add(new LandMineState(1, Race.Blue, Field + new Vector2(25, 0)));
        world.LandMines.Add(new LandMineState(2, Race.Blue, Field + new Vector2(200, 0)));
        world.StartBlast(DataLoader.Value.Parm.Weapons.Single(w => w.TypeName == "VORTEX"), Field, Race.Red);
        var went = new List<GameEvent>();
        Run(world, 4, () => went.AddRange(world.Events.Where(e => e.Kind == GameEventKind.MineExploded)));

        Assert.Equal(2, world.LandMines[0].Id);
        Assert.Single(world.LandMines);
        Assert.Equal(1, Assert.Single(went).Other);
    }

    // ---- when machines fire ------------------------------------------------------------------------

    [Fact]
    public void A_machine_on_its_way_at_high_defcon_does_not_fire()
    {
        var world = NewMatch();
        var shooter = Grunt(world, Race.Red, Field);
        Dozer(world, Race.Blue, Field + new Vector2(20, 8));
        world.Issue(new MoveCommand(Race.Red, [shooter.Id], Field + new Vector2(60, 0)));
        var fired = 0;
        Run(world, 5, () => fired += world.Events.Count(e => e.Kind == GameEventKind.Fired));

        Assert.Equal(0, fired);
    }

    [Fact]
    public void A_machine_without_an_upper_body_fires_only_where_it_faces()
    {
        var world = NewMatch();
        var shooter = Grunt(world, Race.Red, Field, level: 1, combo: "R_BOLTER");
        Assert.False(shooter.CanTurnUpperBody);
        shooter.Facing = 0;
        var behind = Dozer(world, Race.Blue, Field - new Vector2(20, 0));
        world.Issue(new StandGroundCommand(Race.Red, [shooter.Id]));
        var fired = 0;
        Run(world, 3, () => fired += world.Events.Count(e => e.Kind == GameEventKind.Fired));
        Assert.Equal(0, fired);

        behind.Position = Field + new Vector2(20, 0);
        Run(world, 3, () => fired += world.Events.Count(e => e.Kind == GameEventKind.Fired));
        Assert.True(fired > 0);
    }

    [Fact]
    public void An_upper_body_has_to_turn_before_it_fires()
    {
        var world = NewMatch();
        var shooter = Grunt(world, Race.Red, Field);
        Assert.True(shooter.CanTurnUpperBody);
        shooter.Facing = 0;
        Dozer(world, Race.Blue, Field - new Vector2(20, 0));
        world.Issue(new StandGroundCommand(Race.Red, [shooter.Id]));
        var firstShot = float.NaN;
        Run(world, 3, () =>
        {
            if (float.IsNaN(firstShot) && world.Events.Any(e => e.Kind == GameEventKind.Fired))
                firstShot = world.Time;
        });

        // 180 degrees a second, half way round less what the weapon can deviate.
        Assert.InRange(firstShot, 0.6f, 1.2f);
    }

    [Fact]
    public void A_tower_turns_at_its_rate()
    {
        var world = NewMatch();
        var turret = world.AddConstruction(Race.Red, Field, "MISSILE_EMPLACEMENT", "TURRET", 3, 0, complete: true);
        var way = new Vector2(-30, 0);
        var target = Dozer(world, Race.Blue, Field + way);
        target.HitPoints = 1000000;
        var firstShot = float.NaN;
        Run(world, 4, () =>
        {
            if (float.IsNaN(firstShot) && world.Events.Any(e => e.Kind == GameEventKind.Fired))
                firstShot = world.Time;
        });

        // At 60 degrees a second it comes round in three seconds, less what its weapons can deviate.
        Assert.InRange(firstShot, 2, 3.2f);
    }

    // ---- what can fire at what -----------------------------------------------------------------------

    [Fact]
    public void Sentries_and_the_short_ranged_leave_flyers_as_the_original_does()
    {
        var world = NewMatch();
        var flyer = world.AddMachine(Race.Blue, Field, "ADMINISTRATOR", "BOSS", 1, "L_WASP_LIGHT_STING");
        var dozer = Dozer(world, Race.Blue, Field + new Vector2(10, 0));
        var ssm = world.AddConstruction(Race.Red, Field + new Vector2(60, 0), "MISSILE_EMPLACEMENT", "SENTRY", 3, 0, complete: true);
        var sam = world.AddConstruction(Race.Red, Field + new Vector2(0, 60), "MISSILE_EMPLACEMENT", "SENTRY", 4, 0, complete: true);
        var bee = world.AddMachine(Race.Red, Field + new Vector2(60, 60), "ADMINISTRATOR", "BOSS", 2, "T_BEE_BOMB");
        var flamer = world.AddMachine(Race.Red, Field + new Vector2(-60, 0), "AGGRESSOR", "GRUNT", 3, "R_FLAME_THROWER2");
        var bolter = Grunt(world, Race.Red, Field + new Vector2(-60, 60));

        Assert.False(world.CanFireAt(ssm, flyer));
        Assert.True(world.CanFireAt(ssm, dozer));
        Assert.True(world.CanFireAt(sam, flyer));
        Assert.False(world.CanFireAt(sam, dozer));
        Assert.False(world.CanFireAt(bee, flyer));
        Assert.True(world.CanFireAt(bee, dozer));
        Assert.False(world.CanFireAt(flamer, flyer));
        Assert.True(world.CanFireAt(bolter, flyer));
    }

    [Fact]
    public void A_punch_does_not_reach_a_flyer()
    {
        var world = NewMatch();
        var flyer = world.AddMachine(Race.Blue, Field + new Vector2(10, 0), "ADMINISTRATOR", "BOSS", 1, "L_WASP_LIGHT_STING");
        var walker = Dozer(world, Race.Blue, Field + new Vector2(10, 5));
        world.StartBlast(DataLoader.Value.Parm.Weapons.Single(w => w.TypeName == "GORILLA_PUNCH"), Field, Race.Red, direction: new Vector2(1, 0));
        Run(world, 2);

        Assert.Equal(flyer.MaxHitPoints, flyer.HitPoints);
        Assert.True(walker.IsDead);
    }

    [Fact]
    public void Buildings_and_friends_are_cover_enemies_are_not()
    {
        var world = NewMatch();
        var (shooter, target) = (Grunt(world, Race.Red, Field), Dozer(world, Race.Blue, Field + new Vector2(30, 0)));
        Assert.True(world.ClearShot(shooter, target));

        var enemy = Dozer(world, Race.Blue, Field + new Vector2(15, 0));
        world.Step();
        Assert.True(world.ClearShot(shooter, target));

        enemy.Position = Field + new Vector2(15, 40);
        var friend = Dozer(world, Race.Red, Field + new Vector2(15, 0));
        world.Step();
        Assert.False(world.ClearShot(shooter, target));

        friend.Position = Field + new Vector2(-40, 40);
        var wall = world.AddConstruction(Race.Blue, Field + new Vector2(15, 0), "SMELTER", null, 1, 0, complete: true);
        world.Step();
        Assert.False(world.ClearShot(shooter, target));
        Assert.True(world.ClearShot(shooter, wall));
    }

    [Fact]
    public void A_machine_behind_cover_holds_its_fire_and_moves_closer()
    {
        var world = NewMatch();
        var shooter = Grunt(world, Race.Red, Field);
        var target = Dozer(world, Race.Blue, Field + new Vector2(30, 0));
        Dozer(world, Race.Red, Field + new Vector2(15, 0));
        world.Issue(new StandGroundCommand(Race.Red, [shooter.Id]));
        var fired = 0;
        Run(world, 3, () => fired += world.Events.Count(e => e.Kind == GameEventKind.Fired));
        Assert.Equal(0, fired);

        var from = shooter.Position;
        Attack(world, shooter, target);
        Run(world, 3);
        Assert.NotEqual(from, shooter.Position);
    }

    // ---- the vortex, the end of an Eradicator and a silo ---------------------------------------------

    private static Machine Eradicator(GameWorld world, Vector2 at)
    {
        var eradicator = world.AddMachine(Race.Red, at, "AGGRESSOR", "BALLISTA", 3, "T_VORTEX");
        eradicator.DefCon = DefCon.High;
        return eradicator;
    }

    [Fact]
    public void A_vortex_stays_clear_of_the_one_who_fires_it_and_holds_him_for_nine_seconds_unless_ordered()
    {
        var world = NewMatch();
        var eradicator = Eradicator(world, Field);
        var target = Dozer(world, Race.Blue, Field + new Vector2(20, 0));
        Attack(world, eradicator, target);
        while (world.Blasts.Count == 0)
            world.Step();

        Assert.Equal(46, Vector2.Distance(world.Blasts[0].Position, eradicator.Position), 1);
        Assert.True(world.Busy(eradicator));
        Run(world, 8);
        Assert.True(world.Busy(eradicator));

        world.Issue(new StopCommand(Race.Red, [eradicator.Id]));
        world.Step();
        Assert.False(world.Busy(eradicator));
    }

    [Fact]
    public void A_vortex_goes_off_where_its_target_will_be()
    {
        var world = NewMatch();
        var eradicator = Eradicator(world, Field);
        var target = world.AddMachine(Race.Blue, Field + new Vector2(80, -60), "AGGRESSOR", "GRUNT", 2, "R_BOLTER");
        target.DefCon = DefCon.High;
        world.Issue(new MoveCommand(Race.Blue, [target.Id], Field + new Vector2(80, 60)));
        Run(world, 2);
        Attack(world, eradicator, target);
        while (world.Blasts.Count == 0)
            world.Step();

        // Whichever way its path has taken it, 1.3 s ahead of it.
        var heading = new Vector2(MathF.Cos(target.Facing), MathF.Sin(target.Facing));
        Assert.InRange(Vector2.Dot(world.Blasts[0].Position - target.Position, heading), 3, 12);
    }

    [Fact]
    public void An_eradicator_that_destroys_itself_goes_with_its_vortex()
    {
        var world = NewMatch();
        var eradicator = Eradicator(world, Field);
        world.Issue(new SelfDestructCommand(Race.Red, [eradicator.Id]));
        var blasts = new List<Blast>();
        while (blasts.Count == 0 && world.Time < 30)
        {
            world.Step();
            blasts.AddRange(world.Blasts);
        }

        Assert.Equal(Field, Assert.Single(blasts.Distinct()).Position);
    }

    [Fact]
    public void An_eradicator_shot_in_its_countdown_goes_with_its_vortex_too()
    {
        var world = NewMatch();
        var eradicator = Eradicator(world, Field);
        world.Issue(new SelfDestructCommand(Race.Red, [eradicator.Id]));
        world.Step();
        eradicator.HitPoints = 0;
        world.Step();

        Assert.Equal(Field, Assert.Single(world.Blasts).Position);
    }

    [Fact]
    public void An_eradicator_not_charged_goes_without()
    {
        var world = NewMatch();
        var eradicator = Eradicator(world, Field);
        eradicator.Weapons[0].Reload = 5;
        world.Issue(new SelfDestructCommand(Race.Red, [eradicator.Id]));
        world.Step();
        eradicator.HitPoints = 0;
        world.Step();

        Assert.Empty(world.Blasts);
    }

    [Fact]
    public void A_charged_silo_that_destroys_itself_launches_on_itself()
    {
        var world = NewMatch();
        var silo = world.AddConstruction(Race.Red, Field, "MISSILE_EMPLACEMENT", "ICBM", 5, 0, complete: true);
        silo.Weapons[0].Reload = 0;
        world.Issue(new SelfDestructCommand(Race.Red, [silo.Id]));
        var launched = false;
        Run(world, 30, () => launched |= world.Events.Any(e => e.Kind == GameEventKind.Fired && e.Detail == "NUCLEAR_MISSILE"));

        Assert.True(launched);
    }

    // ---- towers ---------------------------------------------------------------------------------------

    [Fact]
    public void A_launcher_in_good_repair_leaves_what_is_close_alone()
    {
        var world = NewMatch();
        var launcher = world.AddConstruction(Race.Red, Field, "MISSILE_EMPLACEMENT", "LAUNCHER", 4, 0, complete: true);
        var near = Dozer(world, Race.Blue, Field + new Vector2(15, 0));
        near.HitPoints = 1000000;
        Run(world, 10);
        Assert.NotEqual(near.Id, world.AimOf(launcher.Id)?.Id);

        launcher.HitPoints = launcher.MaxHitPoints / 5;
        Run(world, 40);
        Assert.Equal(near.Id, world.AimOf(launcher.Id)?.Id);
    }

    [Fact]
    public void A_tower_sees_past_its_weapons_and_keeps_its_target_till_it_has_gone()
    {
        var world = NewMatch();
        var turret = world.AddConstruction(Race.Red, Field, "MISSILE_EMPLACEMENT", "TURRET", 3, 0, complete: true);
        var range = turret.Weapons.Max(w => w.Spec.Range);
        var target = Dozer(world, Race.Blue, Field + new Vector2(range * 1.2f, 0));
        target.HitPoints = 1000000;
        var fired = 0;
        Run(world, 8, () => fired += world.Events.Count(e => e.Kind == GameEventKind.Fired));
        Assert.Equal(target.Id, world.AimOf(turret.Id)?.Id);
        Assert.Equal(0, fired);

        target.Position = Field + new Vector2(range * 1.6f, 0);
        world.Step();
        Assert.Null(world.AimOf(turret.Id));
    }

    [Fact]
    public void A_players_tower_changes_from_what_is_harmless_only_for_what_is_not()
    {
        var world = NewMatch();
        var turret = world.AddConstruction(Race.Red, Field, "MISSILE_EMPLACEMENT", "TURRET", 3, 0, complete: true);
        var range = turret.Weapons.Max(w => w.Spec.Range);
        var harmless = Dozer(world, Race.Blue, Field + new Vector2(range * 0.4f, 0));
        harmless.HitPoints = 1000000;
        Run(world, 8);
        Assert.Equal(harmless.Id, world.AimOf(turret.Id)?.Id);

        var other = Dozer(world, Race.Blue, Field + new Vector2(range * 0.4f, 8));
        other.HitPoints = 1000000;
        Run(world, 30);
        Assert.Equal(harmless.Id, world.AimOf(turret.Id)?.Id);

        var armed = Grunt(world, Race.Blue, Field + new Vector2(range * 0.3f, -12));
        armed.HitPoints = 1000000;
        Run(world, 60);
        Assert.Equal(armed.Id, world.AimOf(turret.Id)?.Id);
    }

    // ---- the orb --------------------------------------------------------------------------------------

    [Fact]
    public void The_orb_of_treachery_takes_an_ally_it_hits_too()
    {
        var world = NewMatch();
        world.Races[Race.Red].Allies.Add(Race.Blue);
        world.Races[Race.Blue].Allies.Add(Race.Red);
        var shooter = world.AddMachine(Race.Red, Field, "AGGRESSOR", "KNIGHT", 3, "T_TREACHERY");
        shooter.DefCon = DefCon.High;
        world.GunneryOf(shooter).Learn(100);
        world.GunneryOf(shooter).Settle(100);
        var target = world.AddMachine(Race.Green, Field + new Vector2(60, 0), "CONSTRUCTOR", "DOZER", 1, null);
        target.DefCon = DefCon.High;
        Attack(world, shooter, target);
        while (world.Projectiles.Count == 0)
            world.Step();
        var ally = Dozer(world, Race.Blue, Field + new Vector2(55, 0));
        Run(world, 2);

        Assert.Equal(Race.Red, ally.Race);
    }

    // ---- night ----------------------------------------------------------------------------------------

    [Fact]
    public void Darkness_follows_the_light_between_the_planets_thresholds()
    {
        var keys = new List<ColourKey> { new(0, new Vector4(0, 0, 0, 1)), new(90, new Vector4(0.9f, 0.9f, 0.9f, 1)) };
        var environment = new PlanetEnvironment(
            new Dictionary<string, IReadOnlyList<ColourKey>> { ["sun"] = keys },
            new Dictionary<string, Orbit> { ["orbit"] = new Orbit(0, 90, 1000, 0, 100, 0) },
            [new Satellite("sun", "orbit", null, null, "sun", null)],
            null!,
            []);

        Assert.Equal(1, GameWorld.DarknessAt(environment, 0));
        Assert.Equal(0, GameWorld.DarknessAt(environment, 50));
        // The light is 0.3 at a third of the way up, half way between the default 0.25 and 0.35.
        Assert.Equal(0.5f, GameWorld.DarknessAt(environment, 50f / 3), 2);
    }

    [Fact]
    public void In_the_dark_a_scanner_reaches_half_as_far_but_for_night_vision_and_near_a_commander()
    {
        var world = NewMatch();
        var grunt = Grunt(world, Race.Red, Field);
        var overseer = world.AddMachine(Race.Red, Field + new Vector2(0, 300), "ADMINISTRATOR", "OVERSEER", 2, "R_BOLTER");
        Assert.True(overseer.Stats.NightVision);
        world.Darkness = 1;
        Assert.Equal(grunt.Stats.ScannerRange / 2f, world.ScannerReach(grunt));
        Assert.Equal(overseer.Stats.ScannerRange, world.ScannerReach(overseer));

        world.Issue(new FormSquadronCommand(Race.Red, [grunt.Id, overseer.Id], 1));
        world.Step();
        world.Darkness = 1;
        // With its commander 300 m off it sees no better; at 100 m it perceives half the dark.
        Assert.Equal(grunt.Stats.ScannerRange / 2f, world.ScannerReach(grunt));
        overseer.Position = Field + new Vector2(0, 100);
        Assert.Equal(grunt.Stats.ScannerRange * 0.75f, world.ScannerReach(grunt), 1);
    }

    [Fact]
    public void A_clock_of_its_own_gives_the_same_darkness_every_time()
    {
        var world = NewMatch();
        Assert.Equal(GameWorld.DarknessAt(world.Planet.Environment, 123), GameWorld.DarknessAt(world.Planet.Environment, 123));
    }
}
