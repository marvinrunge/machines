using System.Numerics;
using Machines.Import.Model;
using Machines.Sim;
using Machines.Sim.Combat;
using Machines.Sim.Navigation;
using Machines.Sim.Survival;
using Machines.Sim.World;

namespace Machines.Tests.Sim;

/// <summary>
/// Wired's sighted driver: the gun turns to where its driver looks at its turret's rate (a hull-only machine's hull at its
/// own), rounds go along the gun, and heroes that fight on their own wait for their gun as well.
/// </summary>
public class WiredSightTests
{
    private static readonly Lazy<GameData> DataLoader = new(() => GameData.Load(RepoPaths.OriginalDataFolder()));

    private static GameData Data => DataLoader.Value;

    private const float Degree = MathF.PI / 180;

    private static readonly WiredSlot Left = WiredSlot.Weapon(Mounting.Left), Right = WiredSlot.Weapon(Mounting.Right), Top = WiredSlot.Weapon(Mounting.Top);

    private static float Wrap(float angle) => MathF.IEEERemainder(angle, 2 * MathF.PI);

    private static float Bearing(Vector2 from, Vector2 to) => MathF.Atan2(to.Y - from.Y, to.X - from.X);

    private static Vector2 Along(Actor from, float bearing, float metres) => from.Position + new Vector2(MathF.Cos(bearing), MathF.Sin(bearing)) * metres;

    private static Vector2 Flat(Vector3 point) => new(point.X, point.Y);

    private static void Near(float expected, float actual, float within) => Assert.InRange(actual, expected - within, expected + within);

    /// <summary>
    /// A run that stays in its preparation, its hero out of the hangar on <paramref name="chassis"/> with the weapons given,
    /// facing away from its pod; and that heading, along which the field is free.
    /// </summary>
    private static (GameWorld World, Machine Hero, float Out) Run(string chassis = "grunt1", params (WiredSlot Slot, string Weapon)[] weapons) =>
        Run(chassis, null, weapons);

    private static (GameWorld World, Machine Hero, float Out) Run(string chassis, Action<GameWorld>? fit, params (WiredSlot Slot, string Weapon)[] weapons)
    {
        var world = WiredHeroTests.InHangar();
        world.Wired!.Countdown = 100_000;
        if (chassis != "grunt1")
            WiredHeroTests.Board(world, chassis);
        foreach (var (slot, weapon) in weapons)
            WiredHeroTests.Wear(world, weapon, slot);
        fit?.Invoke(world);
        Do(world, new WiredHangarCommand(Race.Red, false));
        Steps(world, 40);
        var hero = world.HeroOf(Race.Red)!;
        var away = Vector2.Normalize(hero.Position - world.WiredPod(Race.Red)!.Position);
        hero.Facing = MathF.Atan2(away.Y, away.X);
        return (world, hero, hero.Facing);
    }

    private static void Do(GameWorld world, Command command)
    {
        world.Issue(command);
        world.Step();
        Assert.Empty(world.Rejected);
    }

    private static void Steps(GameWorld world, int ticks)
    {
        for (var i = 0; i < ticks; i++)
            world.Step();
    }

    /// <summary>One tick at the wheel: the command, the step, and the hero's rounds fired in it.</summary>
    private static List<GameEvent> Drive(GameWorld world, Machine hero, float? yaw, Vector2? aim = null, float? height = null, float turn = 0, bool slow = false)
    {
        world.Issue(new DriveCommand(hero.Race, hero.Id, true, 0, turn, aim, aim is null ? 0 : 1, Slow: slow, AimHeight: height, Yaw: yaw));
        world.Step();
        return Fired(world, hero);
    }

    private static List<GameEvent> Fired(GameWorld world, Actor shooter) =>
        world.Events.Where(e => e.Kind == GameEventKind.Fired && e.Actor == shooter.Id).ToList();

    /// <summary>Holds the trigger on <paramref name="aim"/> until the first round is out: its event.</summary>
    private static GameEvent FirstRound(GameWorld world, Machine hero, float? yaw, Vector2 aim, float? height = null)
    {
        for (var i = 0; i < 100; i++)
            if (Drive(world, hero, yaw, aim, height) is [var round, ..])
                return round;
        throw new Xunit.Sdk.XunitException("no round was fired");
    }

    /// <summary>A machine of the enemy that stands where it is put, fires nothing and takes a good many hits.</summary>
    private static Machine Dummy(GameWorld world, Vector2 at)
    {
        var dummy = world.AddMachine(Race.Blue, at, "AGGRESSOR", "NINJA", 5, "L_MULTI_LAUNCHER7_T_GORILLA_PUNCH_R_MULTI_LAUNCHER7");
        dummy.Weapons.Clear();
        dummy.Order = new StandGroundOrder();
        return dummy;
    }

    /// <summary>An enemy Grunt that stands, fires nothing and does not die.</summary>
    private static Machine Foe(GameWorld world, Vector2 at)
    {
        var foe = world.AddMachine(Race.Blue, at, "AGGRESSOR", "GRUNT", 1, "R_BOLTER");
        foe.Weapons.Clear();
        foe.Order = new StandGroundOrder();
        return foe;
    }

    private static int Worth(Machine machine) => machine.HitPoints + machine.Armour;

    // How far a round may lie off its line: the spread, and a little for what the next tick makes of it.
    private static float Spread(GameWorld world, Machine hero) => (world.AimSpread(hero) + 0.5f) * Degree;

    // ---- the gun turns ------------------------------------------------------------------------------

    [Fact]
    public void A_drive_without_a_yaw_turns_no_gun_and_fires_at_the_point()
    {
        var (world, hero, way) = Run("grunt2");
        hero.Facing = way - 90 * Degree;
        var aim = Along(hero, way, 20);

        var round = FirstRound(world, hero, null, aim);

        Assert.False(world.HasYaw(hero.Id));
        Assert.Equal(0, world.HeadOf(hero.Id));
        Assert.True(Vector2.Distance(Flat(round.Mark!.Value), aim) <= 20 * MathF.Tan(Spread(world, hero)), $"{round.Mark} for {aim}");
    }

    [Fact]
    public void GunStep_lands_exactly_on_the_wanted_angle_within_a_step()
    {
        Assert.Equal((0.25f, true), GameWorld.GunStep(0.1f, 0.25f, 0.15f));
        Assert.Equal((-0.3f, true), GameWorld.GunStep(-0.2f, -0.3f, 0.15f));
        Assert.Equal((0.1f + 0.15f, false), GameWorld.GunStep(0.1f, 0.5f, 0.15f));
    }

    [Fact]
    public void GunStep_goes_the_short_way_round()
    {
        // From 3 rad to -3 rad is 0.28 rad on through pi, not 6 rad back.
        var (head, arrived) = GameWorld.GunStep(3, -3, 0.1f);
        Assert.False(arrived);
        Near(3.1f, head, 1e-5f);
        (head, arrived) = GameWorld.GunStep(3.1f, -3.0f, 0.1f);
        Assert.False(arrived);
        Near(3.2f - 2 * MathF.PI, head, 1e-5f);
        Assert.Equal((-3.1f, true), GameWorld.GunStep(3.1f, -3.1f, 0.1f));
    }

    [Fact]
    public void GunPoint_turns_the_aim_about_the_middle_by_the_lag()
    {
        var from = new Vector2(100, 200);
        var aim = new Vector2(130, 210);
        Assert.True(Vector2.Distance(aim, GameWorld.GunPoint(from, 0, aim)) < 1e-5f);
        foreach (var lag in new[] { 0.3f, -1.2f, 2.9f, -3.1f })
        {
            var point = GameWorld.GunPoint(from, lag, aim);
            Near(Vector2.Distance(from, aim), Vector2.Distance(from, point), 1e-3f);
            Near(0, Wrap(Bearing(from, point) - Bearing(from, aim) - lag), 1e-4f);
        }
    }

    [Fact]
    public void The_upper_body_turns_to_the_yaw_at_its_rate()
    {
        var (world, hero, _) = Run("grunt2");
        var yaw = hero.Facing + 85 * Degree;

        for (var tick = 1; tick <= 14; tick++)
        {
            Drive(world, hero, yaw);
            Assert.Equal(tick >= 10, world.OnSight(hero.Id));
            if (tick is 1 or 5 or 9)
                Near(tick * 9 * Degree, world.HeadOf(hero.Id), 1e-5f);
            if (tick >= 10)
                Assert.Equal(Wrap(world.PlayerYaws[hero.Id] - hero.Facing), world.HeadOf(hero.Id));
        }
        Assert.True(world.HasYaw(hero.Id));
    }

    [Fact]
    public void The_upper_body_turns_the_short_way_round()
    {
        var (world, hero, _) = Run("grunt2");
        var yaw = hero.Facing - 170 * Degree;

        Drive(world, hero, yaw);
        Near(-9 * Degree, world.HeadOf(hero.Id), 1e-5f);
        for (var tick = 2; tick <= 5; tick++)
            Drive(world, hero, yaw);
        Near(-45 * Degree, world.HeadOf(hero.Id), 1e-5f);
    }

    [Fact]
    public void Gyros_and_overload_set_the_turret_rate()
    {
        var (world, hero, _) = Run("grunt2", w =>
        {
            var gyro = WiredHeroTests.Wear(w, "gyro", WiredSlot.Module(0));
            Do(w, new WiredUpgradeCommand(Race.Red, gyro.Id));
        });
        var build = world.Wired!.Heroes[Race.Red].Build(Data);
        Assert.Equal(1.4f, build.TurnShare, 4);
        Near(180 * 1.4f, build.GunTurnRate, 1e-3f);

        Drive(world, hero, hero.Facing + 90 * Degree);
        Near(12.6f * Degree, world.HeadOf(hero.Id), 1e-5f);
    }

    [Fact]
    public void The_gun_keeps_its_bearing_while_the_hull_turns_under_it()
    {
        var (world, hero, _) = Run("grunt2");
        var yaw = hero.Facing + 30 * Degree;
        for (var tick = 0; tick < 5; tick++)
            Drive(world, hero, yaw);
        Assert.True(world.OnSight(hero.Id));
        var start = hero.Facing;

        for (var tick = 0; tick < 20; tick++)
        {
            Drive(world, hero, yaw, turn: 1);
            Assert.True(MathF.Abs(Wrap(world.GunHeading(hero) - yaw)) <= WiredBalance.GunPlay, $"tick {tick}: lag {Wrap(world.GunHeading(hero) - yaw)}");
            Assert.True(world.OnSight(hero.Id));
        }
        Near(45 * Degree, hero.Facing - start, 1e-3f);
    }

    // ---- where rounds go ----------------------------------------------------------------------------

    [Fact]
    public void A_round_on_the_sight_lands_as_without_a_yaw()
    {
        (List<Vector3?> Marks, int Worth) Shoot(bool sighted)
        {
            var (world, hero, way) = Run("grunt2");
            hero.Facing = way - 40 * Degree;
            var dummy = Dummy(world, Along(hero, way, 25));
            var (aim, height) = (dummy.Position, world.AimPoint(dummy).Z);
            float? yaw = sighted ? Bearing(hero.Position, aim) : null;
            for (var tick = 0; tick < 6; tick++)
                Drive(world, hero, yaw);
            var marks = new List<Vector3?>();
            for (var tick = 0; tick < 200; tick++)
            {
                marks.AddRange(Drive(world, hero, yaw, aim, height).Select(e => e.Mark));
                Assert.Equal(sighted, world.OnSight(hero.Id));
            }
            return (marks, Worth(dummy));
        }

        var (plain, sighted) = (Shoot(false), Shoot(true));
        Assert.True(plain.Marks.Count >= 5, $"{plain.Marks.Count} rounds");
        Assert.Equal(plain.Marks, sighted.Marks);
        Assert.Equal(plain.Worth, sighted.Worth);
    }

    [Fact]
    public void A_round_while_the_gun_turns_leaves_along_the_gun_line()
    {
        var (world, hero, _) = Run("grunt2");
        var rounds = 0;
        for (var tick = 0; tick < 120; tick++)
        {
            // The view keeps a radian ahead of the gun, so the gun never arrives.
            var yaw = world.GunHeading(hero) + 1;
            var aim = Along(hero, yaw, 30);
            foreach (var round in Drive(world, hero, yaw, aim, world.AimPoint(hero).Z))
            {
                rounds++;
                Assert.False(world.OnSight(hero.Id));
                var bearing = Bearing(hero.Position, Flat(round.Mark!.Value));
                Assert.True(MathF.Abs(Wrap(bearing - world.GunHeading(hero))) <= Spread(world, hero), $"mark at {bearing}, gun at {world.GunHeading(hero)}");
                Assert.True(MathF.Abs(Wrap(bearing - yaw)) > 30 * Degree);
            }
        }
        Assert.True(rounds >= 3, $"{rounds} rounds");
    }

    [Fact]
    public void A_bolt_off_the_sight_stops_at_the_first_machine_on_the_gun_line()
    {
        var (world, hero, way) = Run();
        var dummy = Dummy(world, Along(hero, way, 20));
        var (worth, yaw) = (Worth(dummy), way + 60 * Degree);

        var round = FirstRound(world, hero, yaw, Along(hero, yaw, 60), world.AimPoint(hero).Z);

        Assert.False(world.OnSight(hero.Id));
        Assert.True(Worth(dummy) < worth, "the machine on the gun line was not hit");
        Assert.True(Vector2.Distance(hero.Position, Flat(round.Mark!.Value)) < 20, $"{round.Mark}");

        // With nothing on the line the bolt goes as far as the bolter reaches, not to where the driver looks.
        (world, hero, way) = Run();
        yaw = way + 60 * Degree;
        round = FirstRound(world, hero, yaw, Along(hero, yaw, 60), world.AimPoint(hero).Z);
        Near(Data.Parm.Weapon("BOLTER").Range, Vector2.Distance(hero.Position, Flat(round.Mark!.Value)), 0.05f);
        Assert.True(MathF.Abs(Wrap(Bearing(hero.Position, Flat(round.Mark!.Value)) - way)) <= Spread(world, hero));
    }

    [Fact]
    public void A_bolt_off_the_sight_hits_a_machine_before_the_hill()
    {
        var (world, hero, way) = Run("grunt1", (Right, "AUTO_CANNON"));
        var line = new Vector2(MathF.Cos(way), MathF.Sin(way));
        // A ridge 12 m high across the gun line 28 to 40 m out, flat ground everywhere else.
        const float spacing = 2;
        var heights = new float[(int)(world.Planet.Surface.Width / spacing) + 2, (int)(world.Planet.Surface.Depth / spacing) + 2];
        for (var x = 0; x < heights.GetLength(0); x++)
            for (var y = 0; y < heights.GetLength(1); y++)
            {
                var off = new Vector2(x, y) * spacing - hero.Position;
                var (along, across) = (Vector2.Dot(off, line), MathF.Abs(off.X * line.Y - off.Y * line.X));
                heights[x, y] = along is > 28 and < 40 && across < 12 ? 12 : 0;
            }
        world.Terrain = new TerrainHeights(heights, spacing);
        var dummy = Dummy(world, Along(hero, way, 20));
        var (worth, yaw) = (Worth(dummy), way + 60 * Degree);
        var aim = Along(hero, yaw, 60);
        Assert.NotNull(world.Terrain.FirstBlocked(world.AimPoint(hero), new Vector3(Along(hero, way, 45), world.AimPoint(hero).Z), ends: 3));

        var impacts = 0;
        for (var tick = 0; tick < 10; tick++)
        {
            Drive(world, hero, yaw, aim, world.AimPoint(hero).Z);
            impacts += world.Events.Count(e => e.Kind == GameEventKind.Impact && e.Actor == hero.Id);
        }

        Assert.True(Worth(dummy) < worth, "the machine before the hill was not hit");
        Assert.Equal(0, impacts);
    }

    [Fact]
    public void A_flying_round_off_the_sight_flies_along_the_gun_line()
    {
        var (world, hero, way) = Run("grunt1", (Right, "PLASMA_RIFLE"));
        var yaw = way + 60 * Degree;

        FirstRound(world, hero, yaw, Along(hero, yaw, 40), world.AimPoint(hero).Z);

        var round = world.Projectiles.Single(p => p.Shooter == hero.Id);
        Assert.True(round.OffSight);
        // The dot's bearing turned by the lag: along the hull.
        Assert.True(MathF.Abs(Wrap(MathF.Atan2(round.Direction.Y, round.Direction.X) - way)) <= Spread(world, hero));
    }

    [Fact]
    public void A_hull_only_hero_fires_along_its_hull()
    {
        var (world, hero, way) = Run();
        var yaw = way + 60 * Degree;

        var round = FirstRound(world, hero, yaw, Along(hero, yaw, 30), world.AimPoint(hero).Z);

        Assert.Equal(way, hero.Facing);
        Assert.True(MathF.Abs(Wrap(Bearing(hero.Position, Flat(round.Mark!.Value)) - way)) <= Spread(world, hero));
    }

    [Fact]
    public void A_hull_only_hero_on_its_yaw_fires_at_the_point()
    {
        var (world, hero, way) = Run();
        var aim = Along(hero, way, 25);

        var round = FirstRound(world, hero, way, aim, world.AimPoint(hero).Z);

        Assert.True(world.OnSight(hero.Id));
        Assert.True(Vector2.Distance(Flat(round.Mark!.Value), aim) <= 25 * MathF.Tan(Spread(world, hero)), $"{round.Mark} for {aim}");
    }

    [Fact]
    public void Placed_weapons_go_off_at_the_dot()
    {
        var (world, hero, way) = Run("boss2", (Top, "BEE_BOMB"));
        hero.Facing = way - 90 * Degree;
        var aim = Along(hero, way, 20);

        var round = FirstRound(world, hero, way, aim, 2);

        Assert.False(world.OnSight(hero.Id));
        Assert.Null(round.Mark);
        Assert.Equal(aim, world.Blasts.Single().Position);
    }

    [Fact]
    public void The_gun_turns_while_a_battles_opening_holds_fire()
    {
        var tier = BattleCatalogue.Tier("grunt2");
        var options = BattleBots.Compose(new BattleOptions { Tier = tier, TeamA = [Race.Red, Race.Blue], TeamB = [Race.Green, Race.Yellow] },
            [new BattleHero(Race.Red, "Ace", "grunt2", BattleBots.Loadout("grunt2", tier))]);
        var world = Battles.New(options);
        var hero = Battles.Hero(world, Race.Red);
        var yaw = hero.Facing + 90 * Degree;

        for (var tick = 0; tick < 12; tick++)
        {
            Assert.Empty(Drive(world, hero, yaw, Along(hero, yaw, 20), 2));
            Assert.True(world.Battle!.InOpening(world.Time));
        }

        Assert.True(world.OnSight(hero.Id));
        Assert.Equal(Wrap(world.PlayerYaws[hero.Id] - hero.Facing), world.HeadOf(hero.Id));
    }

    [Fact]
    public void Letting_go_forgets_the_yaw_and_leaves_the_head()
    {
        var (world, hero, way) = Run("grunt2");
        hero.Facing = way;
        for (var tick = 0; tick < 5; tick++)
            Drive(world, hero, way + 90 * Degree);
        var head = world.HeadOf(hero.Id);
        Near(45 * Degree, head, 1e-5f);

        // The computer's aim takes the upper body over where it stands: with nothing to fire at it turns back to the hull
        // a step a tick, and then to what it attacks.
        Do(world, new DriveCommand(Race.Red, hero.Id, false));
        Assert.False(world.HasYaw(hero.Id));
        Near(36 * Degree, world.HeadOf(hero.Id), 1e-5f);
        var foe = Foe(world, Along(hero, way + 90 * Degree, 20));
        Do(world, new AttackCommand(Race.Red, [hero.Id], foe.Id));
        Near(45 * Degree, world.HeadOf(hero.Id), 1e-4f);
    }

    [Fact]
    public void A_yaw_that_is_not_a_number_is_no_yaw()
    {
        var (world, hero, _) = Run("grunt2");
        Drive(world, hero, float.NaN);
        Assert.False(world.HasYaw(hero.Id));
        Drive(world, hero, 1);
        Assert.True(world.HasYaw(hero.Id));
        Drive(world, hero, float.PositiveInfinity);
        Assert.False(world.HasYaw(hero.Id));
        Assert.Empty(world.PlayerYaws);
    }

    [Fact]
    public void Only_a_wired_match_reads_the_yaw()
    {
        var world = MatchSetup.Create(Data, "m_desert", "m_desert",
            new Dictionary<Race, Controller> { [Race.Red] = Controller.Player, [Race.Blue] = Controller.Ai }, 7);
        var door = world.Constructions.First(c => c.Race == Race.Red).EntranceOutside!.Value;
        var grunt = world.AddMachine(Race.Red, door, "AGGRESSOR", "GRUNT", 2, "L_BOLTER");
        var dummy = Dummy(world, Along(grunt, grunt.Facing + 90 * Degree, 20));
        var (worth, aim) = (Worth(dummy), dummy.Position);

        for (var tick = 0; tick < 40; tick++)
            Drive(world, grunt, grunt.Facing, aim, world.AimPoint(dummy).Z);

        Assert.Empty(world.PlayerYaws);
        Assert.Equal(0, world.HeadOf(grunt.Id));
        Assert.True(Worth(dummy) < worth, "the round did not go to the point");
    }

    [Fact]
    public void The_checksum_holds_yaw_and_head_only_while_sighted()
    {
        var (sighted, a, _) = Run("grunt2");
        var (plain, b, _) = Run("grunt2");
        for (var tick = 0; tick < 5; tick++)
        {
            Drive(sighted, a, a.Facing);
            Drive(plain, b, null);
            Assert.NotEqual(plain.Checksum(), sighted.Checksum());
        }
        for (var tick = 0; tick < 3; tick++)
        {
            Drive(sighted, a, null);
            Drive(plain, b, null);
            Assert.Equal(plain.Checksum(), sighted.Checksum());
        }
    }

    [Fact]
    public void A_dead_sighted_hero_leaves_PlayerYaws_and_the_checksum()
    {
        var (sighted, a, _) = Run("grunt2");
        var (plain, b, _) = Run("grunt2");
        for (var tick = 0; tick < 3; tick++)
        {
            Drive(sighted, a, a.Facing + 1);
            Drive(plain, b, null);
        }
        Assert.True(sighted.HasYaw(a.Id));

        sighted.Damage(a, 1_000_000, Race.Blue);
        plain.Damage(b, 1_000_000, Race.Blue);
        sighted.Step();
        plain.Step();

        Assert.True(a.IsDead);
        Assert.Empty(sighted.PlayerYaws);
        Assert.Equal(plain.Checksum(), sighted.Checksum());
    }

    [Fact]
    public void The_last_DriveCommand_of_a_turn_decides_the_yaw()
    {
        var (world, hero, _) = Run("grunt2");
        world.Issue(new DriveCommand(Race.Red, hero.Id, true, Yaw: 1));
        world.Issue(new DriveCommand(Race.Red, hero.Id, true, Yaw: 8));
        world.Step();
        Assert.Equal(MathF.IEEERemainder(8, 2 * MathF.PI), world.PlayerYaws[hero.Id]);

        world.Issue(new DriveCommand(Race.Red, hero.Id, true, Yaw: 1));
        world.Issue(new DriveCommand(Race.Red, hero.Id, true));
        world.Step();
        Assert.False(world.HasYaw(hero.Id));
    }

    // ---- rates --------------------------------------------------------------------------------------

    [Theory]
    [InlineData("grunt1", true, false, 115)]
    [InlineData("phantom6", true, false, 180)]
    [InlineData("grunt1", false, false, 45)]
    [InlineData("grunt1", true, true, 10)]
    public void A_sighted_hull_only_hero_turns_at_its_own_rate_up_to_the_cap(string chassis, bool sighted, bool slow, float degrees)
    {
        var (world, hero, way) = Run(chassis);
        for (var tick = 0; tick < 20; tick++)
            Drive(world, hero, sighted ? way : null, turn: 1, slow: slow);
        Near(degrees * Degree, hero.Facing - way, 1e-3f);
    }

    [Fact]
    public void GunTurn_is_one_in_every_row_and_scales_only_the_gun()
    {
        Assert.All(WiredCatalogue.Chassis, c => Assert.Equal(1f, c.GunTurn));

        foreach (var id in new[] { "grunt2", "grunt1" })
        {
            var loadout = HeroLoadout.Bare(new WiredItem(1, WiredKind.Chassis, id));
            var full = HeroBuild.Resolve(Data, loadout);
            var half = HeroBuild.Resolve(Data, loadout, WiredCatalogue.FindChassis(id)! with { GunTurn = 0.5f });
            Assert.Equal(full.GunTurnRate / 2, half.GunTurnRate);
            Assert.Equal(full.Stats.BodyTurnRate / 2, half.Stats.BodyTurnRate);
            Assert.Equal(full.Stats with { BodyTurnRate = half.Stats.BodyTurnRate }, half.Stats);
            Assert.Equal(full.HullAimRate / 2, half.HullAimRate);
            Assert.Equal(full.TurnShare, half.TurnShare);
            Assert.Equal(full.DrivenTurnRate(45), half.DrivenTurnRate(45));
            Assert.Equal(full.DrivenTurnRate(10), half.DrivenTurnRate(10));
        }
        Assert.True(HeroBuild.Resolve(Data, HeroLoadout.Bare(new WiredItem(1, WiredKind.Chassis, "grunt2"))).TurnsUpperBody);
        Assert.False(HeroBuild.Resolve(Data, HeroLoadout.Bare(new WiredItem(1, WiredKind.Chassis, "grunt1"))).TurnsUpperBody);
    }

    // ---- heroes fighting on their own ---------------------------------------------------------------

    /// <summary>The tick a machine told to attack a foe first fires, and how far its gun then stood from the foe's middle.</summary>
    private static (int Tick, float Lag) FirstShotAt(GameWorld world, Machine shooter, Machine foe, int ticks)
    {
        Do(world, new AttackCommand(Race.Red, [shooter.Id], foe.Id));
        for (var tick = 1; tick < ticks; tick++)
        {
            foe.HitPoints = foe.MaxHitPoints;
            if (Fired(world, shooter).Count > 0)
                return (tick, Wrap(world.GunHeading(shooter) - Bearing(shooter.Position, foe.Position)));
            world.Step();
        }
        return (int.MaxValue, float.NaN);
    }

    [Fact]
    public void A_hero_fighting_on_its_own_opens_fire_only_with_its_gun_on_the_target()
    {
        var (world, hero, way) = Run("grunt2");
        hero.Facing = way - 60 * Degree;
        var foe = Foe(world, Along(hero, way, 30));
        var (heroTick, heroLag) = FirstShotAt(world, hero, foe, 100);
        // The line from the gun meets the foe's box within BotPlay of it: a box at 30 m is a few degrees wide.
        var box = MathF.Atan(foe.Radius * 1.5f / 30);
        Assert.True(MathF.Abs(heroLag) <= WiredBalance.BotPlay + box, $"the hero fired at tick {heroTick} with its gun {heroLag / Degree} degrees off");

        (world, hero, way) = Run("grunt2");
        var computer = world.AddMachine(Race.Red, Along(hero, way, 6), "AGGRESSOR", "GRUNT", 2, "L_BOLTER");
        computer.Facing = way - 60 * Degree;
        foe = Foe(world, Along(computer, way, 30));
        var (computerTick, computerLag) = FirstShotAt(world, computer, foe, 100);
        Assert.True(computerTick < heroTick, $"the computer's Grunt fired at tick {computerTick}, the hero at {heroTick}");
        Assert.True(MathF.Abs(computerLag) > WiredBalance.BotPlay + box, $"the computer's Grunt fired with its gun {computerLag / Degree} degrees off");
    }

    [Fact]
    public void A_hull_only_hero_bot_fires_once_it_faces_its_target()
    {
        var (world, hero, way) = Run();
        hero.Facing = way - 90 * Degree;
        var foe = Foe(world, Along(hero, way, 30));

        var (tick, _) = FirstShotAt(world, hero, foe, 3 * GameWorld.TicksPerSecond);

        Assert.True(tick < 3 * GameWorld.TicksPerSecond, "the hero did not fire within 3 s");
    }

    /// <summary>The ticks a machine attacking a foe right behind it takes to face it, and the most it turned in one, in degrees a second.</summary>
    private static (int Ticks, float Fastest) HalfTurn(GameWorld world, Machine machine, float way)
    {
        machine.Facing = way + MathF.PI;
        var foe = Foe(world, Along(machine, way, 15));
        var (facing, fastest) = (machine.Facing, 0f);
        Do(world, new AttackCommand(Race.Red, [machine.Id], foe.Id));
        for (var tick = 1; tick < 200; tick++)
        {
            fastest = MathF.Max(fastest, MathF.Abs(Wrap(machine.Facing - facing)) / Degree / GameWorld.Dt);
            facing = machine.Facing;
            if (MathF.Abs(Wrap(machine.Facing - way)) <= 0.05f)
                return (tick, fastest);
            foe.HitPoints = foe.MaxHitPoints;
            world.Step();
        }
        return (int.MaxValue, fastest);
    }

    [Fact]
    public void A_hull_only_hero_bot_turns_no_faster_than_its_gun_rate()
    {
        var (world, hero, way) = Run("phantom6");
        Assert.True(hero.Stats.RotationSpeed > 600);
        var build = world.Wired!.Heroes[Race.Red].Build(Data);
        Near(180, build.HullAimRate, 1e-3f);
        var (ticks, fastest) = HalfTurn(world, hero, way);
        Assert.InRange(ticks, 20, 60);
        Assert.True(fastest <= build.HullAimRate + 0.01f, $"turned at {fastest} degrees a second");

        // A Grunt 1's own turn is slower than the cap: the hero turns as the computer's Grunt 1 does.
        (world, hero, way) = Run();
        var turn = HalfTurn(world, hero, way);
        (world, hero, way) = Run();
        var computer = world.AddMachine(Race.Red, Along(hero, way, 6), "AGGRESSOR", "GRUNT", 1, "R_BOLTER");
        Assert.Equal(turn, HalfTurn(world, computer, way));
        Assert.True(turn.Ticks < 60);
    }

    [Fact]
    public void A_hull_only_hero_standing_its_ground_turns_to_what_it_fires_at()
    {
        var (world, hero, way) = Run();
        hero.Facing = way - 90 * Degree;
        var foe = Foe(world, Along(hero, way, 25));
        Do(world, new StandGroundCommand(Race.Red, [hero.Id]));
        var fired = false;
        for (var tick = 0; tick < 2 * GameWorld.TicksPerSecond && !fired; tick++)
        {
            foe.HitPoints = foe.MaxHitPoints;
            world.Step();
            fired = Fired(world, hero).Count > 0;
        }
        Assert.True(fired, "the hero standing its ground did not fire within 2 s");
        Assert.True(MathF.Abs(Wrap(hero.Facing - way)) <= 0.1f, $"facing {(hero.Facing - way) / Degree} degrees off its foe");

        // A turreted hero turns only its upper body.
        (world, hero, way) = Run("grunt2");
        hero.Facing = way - 90 * Degree;
        var facing = hero.Facing;
        foe = Foe(world, Along(hero, way, 25));
        Do(world, new StandGroundCommand(Race.Red, [hero.Id]));
        for (var tick = 0; tick < 2 * GameWorld.TicksPerSecond; tick++)
        {
            foe.HitPoints = foe.MaxHitPoints;
            world.Step();
        }
        Assert.Equal(facing, hero.Facing);
        Near(0, Wrap(world.GunHeading(hero) - Bearing(hero.Position, foe.Position)), 1e-3f);
    }

    [Fact]
    public void A_hero_bots_punch_waits_until_it_faces_its_target()
    {
        var (world, hero, way) = Run("gorilla5", w =>
            Do(w, new WiredUnequipCommand(Race.Red, w.Wired!.Heroes[Race.Red].Items.Single(i => i.Catalogue == "BOLTER").Id)));
        hero.Facing = way + MathF.PI;
        var foe = Foe(world, Along(hero, way, 8));
        Do(world, new AttackCommand(Race.Red, [hero.Id], foe.Id));
        for (var tick = 0; tick < 10 * GameWorld.TicksPerSecond; tick++)
        {
            foe.HitPoints = foe.MaxHitPoints;
            world.Step();
            if (Fired(world, hero).Any(e => e.Detail == "GORILLA_PUNCH"))
            {
                var off = Wrap(Bearing(hero.Position, foe.Position) - hero.Facing);
                Assert.True(MathF.Abs(off) <= WiredBalance.PunchPlay, $"punched at tick {tick} with its foe {off / Degree} degrees off");
                return;
            }
        }
        Assert.Fail("the gorilla never punched");
    }

    // ---- foes a hero cannot get at --------------------------------------------------------------------

    [Fact]
    public void A_hero_gives_up_a_foe_it_can_neither_shoot_nor_come_at()
    {
        // An armed foe in the middle of an enemy building: no line to it is clear and no place round it is free. At low
        // DefCon the hero would take on the nearest armed enemy of its own accord, and it answers what hits it.
        var (world, hero, way) = Run();
        var lab = world.AddConstruction(Race.Blue, Along(hero, way, 50), "HARDWARE_LAB", "MILITARY", 1, 0, complete: true);
        var foe = world.AddMachine(Race.Blue, lab.Position, "AGGRESSOR", "GRUNT", 1, "R_BOLTER");
        foe.Order = new StandGroundOrder();
        Assert.False(world.Nav.IsFree(foe.Position, false));
        Do(world, new DefConCommand(Race.Red, [hero.Id], DefCon.Low));
        Do(world, new AttackCommand(Race.Red, [hero.Id], foe.Id));
        Assert.True(hero.Order is AttackOrder);

        // An attack on the foe, or one it put aside for another fight and takes up again after.
        bool Going(Order order) => order is AttackOrder attack && (attack.Target == foe.Id || attack.Back is { } back && Going(back.Order));
        var last = -1;
        for (var tick = 0; tick < 40 * GameWorld.TicksPerSecond; tick++)
        {
            foe.HitPoints = foe.MaxHitPoints;
            hero.HitPoints = hero.MaxHitPoints;
            // From 20 s on, the foe is taken to hit the hero now and then.
            if (tick >= 20 * GameWorld.TicksPerSecond && tick % GameWorld.TicksPerSecond == 0)
                world.Damage(hero, 1, Race.Blue, foe);
            world.Step();
            if (Going(hero.Order))
                last = tick;
        }

        // It goes round the building to a place on the rings round the foe, finds no better shot there and lets it be for
        // good: it may take on the building instead, but neither of its own accord nor when hit does it go for the foe again.
        Assert.True(last < 20 * GameWorld.TicksPerSecond, $"the hero still went for the foe after {last * GameWorld.Dt} s");
    }

    [Fact]
    public void A_battle_hero_lets_be_an_enemy_pressed_into_the_enemy_pod()
    {
        // An enemy pressed into its own pod: the pod hides it from every side and no place round it is free. A battle
        // hero fighting by itself at low DefCon would take on the nearest armed enemy of its own accord, the pods aside,
        // and it answers what hits it.
        var world = Battles.Begun();
        var (home, pod) = (world.BattlePod(0)!, world.BattlePod(1)!);
        var toward = Vector2.Normalize(home.Position - pod.Position);
        var edge = pod.Position;
        while (pod.DistanceFrom(edge) <= 0)
            edge += toward * 0.25f;
        var foe = world.AddMachine(Race.Green, edge - toward * 3, "AGGRESSOR", "GRUNT", 1, "R_BOLTER");
        foe.Order = new StandGroundOrder();
        Battles.Away(world, Race.Red, edge + toward * 30);
        // The enemy heroes stand far off, behind the hero's own pod, out of every reach that counts here.
        foreach (var race in new[] { Race.Green, Race.Yellow })
            Battles.Away(world, race, home.Position + (home.Position - pod.Position) * 0.5f);
        var hero = Battles.Hero(world, Race.Red);
        hero.DrivenByPlayer = false;
        Assert.False(world.Nav.IsFree(foe.Position, false));
        Assert.False(world.ClearShot(hero, foe));
        Do(world, new DefConCommand(Race.Red, [hero.Id], DefCon.Low));
        Do(world, new AttackCommand(Race.Red, [hero.Id], foe.Id));
        Assert.True(hero.Order is AttackOrder);

        // An attack on the foe, or one it put aside for another fight and takes up again after.
        bool Going(Order order) => order is AttackOrder attack && (attack.Target == foe.Id || attack.Back is { } back && Going(back.Order));
        var last = -1;
        for (var tick = 0; tick < 40 * GameWorld.TicksPerSecond; tick++)
        {
            foe.HitPoints = foe.MaxHitPoints;
            hero.HitPoints = hero.MaxHitPoints;
            // From 20 s on, the foe is taken to hit the hero now and then.
            if (tick >= 20 * GameWorld.TicksPerSecond && tick % GameWorld.TicksPerSecond == 0)
                world.Damage(hero, 1, Race.Green, foe);
            world.Step();
            if (Going(hero.Order))
                last = tick;
        }

        Assert.True(last < 20 * GameWorld.TicksPerSecond, $"the hero still went for the foe after {last * GameWorld.Dt} s");
    }

    [Fact]
    public void A_hero_shoots_a_foe_pressed_into_its_pod()
    {
        var (world, hero, _) = Run();
        var pod = world.WiredPod(Race.Red)!;
        // A point 3 m inside the pod's footprint, between its middle and the hero.
        var inside = hero.Position;
        while (pod.DistanceFrom(inside) > 0)
            inside += Vector2.Normalize(pod.Position - hero.Position) * 0.25f;
        inside += Vector2.Normalize(pod.Position - hero.Position) * 3;
        var foe = Foe(world, inside);
        var computer = world.AddMachine(Race.Red, Along(hero, hero.Facing, 6), "AGGRESSOR", "GRUNT", 1, "R_BOLTER");

        Assert.True(world.ClearShot(hero, foe));
        Assert.False(world.ClearShot(computer, foe));

        var worth = Worth(foe);
        Do(world, new AttackCommand(Race.Red, [hero.Id], foe.Id));
        for (var tick = 0; tick < 5 * GameWorld.TicksPerSecond && Worth(foe) == worth; tick++)
            world.Step();
        Assert.True(Worth(foe) < worth, "the hero did not hit the foe in its pod");
    }
}
