using System.Numerics;
using System.Reflection;
using Machines.Import.Model;
using Machines.Sim;
using Machines.Sim.Ai;
using Machines.Sim.Survival;
using Machines.Sim.World;

namespace Machines.Tests.Sim;

/// <summary>
/// The spy line of Wired: the mines a spy hero lays with the special's key, how many it carries and when they come back,
/// the race's fifteen on the ground shared with the minefield, and the Phantom's shadow, in which a quiet hero is lost to
/// every enemy farther than 25 m.
/// </summary>
public class WiredSpyTests
{
    private static GameData Data => Battles.Data;

    private static GameWorld NewRun(string chassis, int seed = 7) =>
        MatchSetup.Create(Data, "m_desert", "m_desert",
            new Dictionary<Race, Controller> { [Race.Red] = Controller.Player, [Race.Blue] = Controller.Ai }, seed,
            new MatchOptions { Wired = new WiredOptions { Chassis = chassis } });

    /// <summary>A run on a chassis whose first wave waits as long as a test takes, the hero at the pod's door.</summary>
    private static GameWorld Waiting(string chassis)
    {
        var world = NewRun(chassis);
        world.Wired!.Countdown = 100_000;
        world.Step();
        Assert.Equal(chassis, Red(world).Loadout.Chassis.Catalogue);
        return world;
    }

    private static WiredState Red(GameWorld world) => world.Wired!.Heroes[Race.Red];

    private static Machine Hero(GameWorld world) => world.HeroOf(Race.Red)!;

    private static Construction Pod(GameWorld world) => world.WiredPod(Race.Red)!;

    /// <returns>Why the command was refused, or null.</returns>
    private static string? Do(GameWorld world, Command command)
    {
        world.Issue(command);
        world.Step();
        return world.Rejected.FirstOrDefault()?.Reason;
    }

    /// <summary>The special's key: a spy hero lays a mine.</summary>
    private static string? Lay(GameWorld world) => Do(world, new WiredSpecialCommand(Race.Red));

    /// <returns>All that happened in that time.</returns>
    private static List<GameEvent> Run(GameWorld world, float seconds)
    {
        var events = new List<GameEvent>();
        for (var i = 0; i < seconds / GameWorld.Dt; i++)
        {
            world.Step();
            events.AddRange(world.Events);
        }
        return events;
    }

    /// <summary>Lays mines a little more than the gap between two apart.</summary>
    private static void LayMines(GameWorld world, int mines)
    {
        for (var i = 0; i < mines; i++)
        {
            Assert.Null(Lay(world));
            Run(world, WiredBalance.MineLayGap + 0.05f);
        }
    }

    private static int MinesOf(GameWorld world, Race race) => world.LandMines.Count(m => m.Race == race);

    /// <summary>Puts the minefield ability into the hero's first slot.</summary>
    private static void TakeMinefield(GameWorld world)
    {
        var state = Red(world);
        state.Loadout = state.Loadout.With(WiredSlot.Ability(0), state.Add(WiredKind.Ability, "minefield", 0));
    }

    /// <returns>Why the minefield thrown at a point was refused, or null; it is ready again at once.</returns>
    private static string? Minefield(GameWorld world, Vector2 aim)
    {
        var why = Do(world, new WiredAbilityCommand(Race.Red, 0, aim));
        Red(world).AbilityReadyAt.Clear();
        return why;
    }

    /// <summary>A place with open ground 70 m round it, at least that far from the pod.</summary>
    private static Vector2 OpenGround(GameWorld world, float fromPod)
    {
        var pod = Pod(world).Position;
        return Enumerable.Range(0, 900).Select(i => new Vector2(100 + i % 30 * 50, 100 + i / 30 * 50))
            .Where(p => Vector2.Distance(p, pod) >= fromPod)
            .OrderBy(p => Vector2.DistanceSquared(p, pod)).ThenBy(p => p.X).ThenBy(p => p.Y)
            .First(p => world.Nav.IsAreaFree((p - new Vector2(70), p + new Vector2(70))));
    }

    /// <summary>The hero on open ground 150 m from the pod, at the wheel: it stands there and fires at nothing of its own accord.</summary>
    private static Machine OutInTheOpen(GameWorld world)
    {
        var hero = Hero(world);
        hero.DrivenByPlayer = true;
        hero.Position = OpenGround(world, 150);
        hero.Path.Clear();
        world.Step();
        return hero;
    }

    /// <summary>
    /// A place that far from the hero's edge, away from the pod, or turned from that way by a share of a whole turn
    /// (half: towards the pod).
    /// </summary>
    private static Vector2 AtDistance(GameWorld world, Machine hero, float metres, float turn = 0)
    {
        var away = Vector2.Normalize(hero.Position - Pod(world).Position);
        var (cos, sin) = (DetMath.Cos(turn * MathF.Tau), DetMath.Sin(turn * MathF.Tau));
        var way = new Vector2(away.X * cos - away.Y * sin, away.X * sin + away.Y * cos);
        return hero.Position + way * (metres + hero.Radius);
    }

    private static Machine Grunt(GameWorld world, Vector2 at) => world.AddMachine(Race.Blue, at, "AGGRESSOR", "GRUNT", 1, "R_BOLTER");

    /// <summary>A spy of the waves: it looks for enemies as any machine does, and shoots at nobody.</summary>
    private static Machine Unarmed(GameWorld world, Vector2 at) =>
        world.AddMachine(Race.Blue, at, WiredDirector.Spy.Type, WiredDirector.Spy.SubType, WiredDirector.Spy.HardwareLevel, WiredDirector.Spy.WeaponCombo);

    /// <summary>Puts the hero on a chassis in the open hangar, without the way there.</summary>
    private static void Board(GameWorld world, string chassis)
    {
        var state = Red(world);
        var item = state.Items.FirstOrDefault(i => i.Catalogue == chassis) ?? state.Add(WiredKind.Chassis, chassis, 0);
        Assert.Null(Do(world, new WiredChassisCommand(Race.Red, item.Id)));
        Assert.Equal(chassis, state.Loadout.Chassis.Catalogue);
    }

    /// <summary>Asks the world something it keeps to itself, or has it do it.</summary>
    private static object? Call(GameWorld world, string method, params object[] arguments) =>
        typeof(GameWorld).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(world, arguments);

    // ---- laying a mine ----------------------------------------------------------------------------

    [Theory]
    [InlineData("spy3", 5)]
    [InlineData("spy5", 8)]
    [InlineData("phantom6", 8)]
    public void A_spy_hero_lays_a_mine_where_it_stands_from_what_it_carries_and_gives_nothing_away(string chassis, int carried)
    {
        var world = Waiting(chassis);
        var state = Red(world);
        var hero = Hero(world);
        Assert.Equal(carried, hero.Mines);
        var at = hero.Position;

        Assert.Null(Lay(world));

        Assert.Equal(carried - 1, hero.Mines);
        var mine = Assert.Single(world.LandMines);
        Assert.Equal((Race.Red, at), (mine.Race, mine.Position));
        Assert.Contains(world.Events, e => e.Kind == GameEventKind.MineLaid && e.Actor == hero.Id && e.Other == mine.Id);
        Assert.False(float.IsNegativeInfinity(state.MineLaidAt));
        // Laying mines is no shot: a Phantom stays in its shadow.
        Assert.True(float.IsNegativeInfinity(state.RevealedAt));
        Assert.True(float.IsNegativeInfinity(state.LastShotAt));
    }

    [Fact]
    public void The_next_mine_waits_a_second()
    {
        var world = Waiting("spy5");
        Assert.Null(Lay(world));

        Assert.Equal("that is not ready yet", Lay(world));
        Run(world, 0.5f);
        Assert.Equal("that is not ready yet", Lay(world));
        Run(world, 0.5f);
        Assert.Null(Lay(world));

        Assert.Equal(2, world.LandMines.Count);
        Assert.Equal(6, Hero(world).Mines);
    }

    [Fact]
    public void Out_of_mines_comes_before_the_wait_and_the_wait_before_the_mines_on_the_ground()
    {
        var world = Waiting("spy3");
        var hero = Hero(world);

        LayMines(world, 5);
        Assert.Equal("no mines left", Lay(world));
        Assert.Equal(5, world.LandMines.Count);

        // Its last one just down: out of mines and waiting both, and out of mines is what it hears.
        hero.Mines = 1;
        Assert.Null(Lay(world));
        Assert.Equal("no mines left", Lay(world));

        // A mine to lay, the wait not over and the ground full: the wait.
        hero.Mines = 1;
        for (var id = 9001; MinesOf(world, Race.Red) < WiredBalance.MostMines; id++)
            world.LandMines.Add(new LandMineState(id, Race.Red, hero.Position));
        Assert.Equal("that is not ready yet", Lay(world));
        Run(world, WiredBalance.MineLayGap);
        Assert.Equal("15 mines lie already", Lay(world));
        Assert.Equal(1, hero.Mines);
    }

    [Fact]
    public void No_mine_goes_down_from_inside_the_pod_or_from_a_hero_that_is_down()
    {
        var world = Waiting("spy5");
        var (hero, pod) = (Hero(world), Pod(world));

        Assert.Null(Do(world, new EnterGarrisonCommand(Race.Red, [hero.Id], pod.Id)));
        for (var i = 0; i < 20 * 20 && hero.Inside is null; i++)
            world.Step();
        Assert.Equal(pod.Id, hero.Inside);
        Assert.Equal("not from inside the pod", Lay(world));
        Assert.Equal("not from inside the pod", world.WhyNoMine(Red(world)));

        world.Damage(hero, 1_000_000, Race.Blue);
        world.Step();
        Assert.Equal("the hero is down", Lay(world));
        Assert.Equal("the hero is down", world.WhyNoMine(Red(world)));
        Assert.Empty(world.LandMines);
    }

    [Fact]
    public void A_race_has_fifteen_mines_on_the_ground_at_most_its_hero_s_and_its_minefields_together()
    {
        var world = Waiting("phantom6");
        var hero = Hero(world);
        TakeMinefield(world);
        var aim = AtDistance(world, hero, 30, 0.25f);
        // The enemy's mines count for nothing.
        world.LandMines.Add(new LandMineState(9001, Race.Blue, OpenGround(world, 300)));

        LayMines(world, 4);
        Assert.Null(Minefield(world, aim));
        Assert.Null(Minefield(world, aim));
        Assert.Equal(14, MinesOf(world, Race.Red));
        Assert.Null(Lay(world));
        Assert.Equal(WiredBalance.MostMines, MinesOf(world, Race.Red));
        Run(world, WiredBalance.MineLayGap);

        Assert.Equal("15 mines lie already", Lay(world));
        Assert.Equal("15 mines lie already", world.SpecialView(Race.Red)!.Why);
        Assert.Equal("too many mines on the ground", Minefield(world, aim));
        Assert.Equal(3, hero.Mines);

        // One gone off makes room for the next.
        world.LandMines.Remove(world.LandMines.First(m => m.Race == Race.Red));
        Assert.Null(Lay(world));
        Assert.Equal(WiredBalance.MostMines, MinesOf(world, Race.Red));
    }

    [Fact]
    public void A_minefield_needs_room_for_all_five_of_its_mines_the_key_for_one()
    {
        var world = Waiting("phantom6");
        var hero = Hero(world);
        TakeMinefield(world);
        var aim = AtDistance(world, hero, 30, 0.25f);

        // Eleven of the race's own, one from the key and ten from minefields: no room for five, room for one.
        LayMines(world, 1);
        Assert.Null(Minefield(world, aim));
        Assert.Null(Minefield(world, aim));
        Assert.Equal(11, MinesOf(world, Race.Red));
        Assert.Equal("too many mines on the ground", Minefield(world, aim));
        Assert.Equal(11, MinesOf(world, Race.Red));
        Assert.Null(Lay(world));
        Assert.Equal(12, MinesOf(world, Race.Red));

        // Two gone off leave ten: the five fit exactly, and then the key has no room.
        world.LandMines.Remove(world.LandMines.First(m => m.Race == Race.Red));
        world.LandMines.Remove(world.LandMines.First(m => m.Race == Race.Red));
        Assert.Null(Minefield(world, aim));
        Assert.Equal(WiredBalance.MostMines, MinesOf(world, Race.Red));
        Run(world, WiredBalance.MineLayGap);
        Assert.Equal("15 mines lie already", Lay(world));
        Assert.Equal(6, hero.Mines);
    }

    [Fact]
    public void In_a_battle_mines_wait_for_the_opening_and_come_with_each_life_only()
    {
        var tier = BattleCatalogue.Tier("spy5");
        var options = BattleBots.Compose(new BattleOptions { Tier = tier, TeamA = [Race.Red, Race.Blue], TeamB = [Race.Green, Race.Yellow], Lives = 2 },
            [new BattleHero(Race.Red, "Ace", "spy5", BattleBots.Loadout("spy5", tier))]);
        var world = Battles.New(options);
        var state = world.Wired!.Heroes[Race.Red];

        Assert.Equal("the battle has not begun yet", Battles.Do(world, new WiredSpecialCommand(Race.Red)));
        Battles.Run(world, BattleBalance.Opening);
        Assert.False(world.Battle!.InOpening(world.Time));

        Assert.Null(Battles.Do(world, new WiredSpecialCommand(Race.Red)));
        var first = Battles.Hero(world, Race.Red);
        Assert.Equal(7, first.Mines);
        Assert.Single(world.LandMines);
        // What would fill a run's hero at a wave or a change of chassis gives nothing in a battle.
        Call(world, "RefillMines", state);
        Assert.Equal(7, first.Mines);

        Battles.Kill(world, Race.Red, Race.Green);
        Battles.Run(world, options.RespawnWait + 1);

        var second = Battles.Hero(world, Race.Red);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(8, second.Mines);
    }

    // ---- refills ----------------------------------------------------------------------------------

    [Fact]
    public void A_spy_hero_comes_back_with_a_full_load()
    {
        var world = Waiting("spy3");
        Red(world).SpareBodies = 1;
        var first = Hero(world);
        LayMines(world, 2);
        Assert.Equal(3, first.Mines);

        world.Damage(first, 1_000_000, Race.Blue);
        Run(world, WiredBalance.RespawnTime + 0.5f);

        var second = Hero(world);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(5, second.Mines);
    }

    [Fact]
    public void Every_wave_brings_a_full_load()
    {
        var world = Waiting("spy5");
        LayMines(world, 2);
        Assert.Equal(6, Hero(world).Mines);

        world.Wired!.Countdown = 0;
        world.Step();

        Assert.Equal((WiredPhase.Wave, 1), (world.Wired.Phase, world.Wired.Wave));
        Assert.Equal(8, Hero(world).Mines);
    }

    [Fact]
    public void A_change_of_chassis_brings_its_load_and_a_change_of_parts_none()
    {
        var world = Waiting("spy3");
        var state = Red(world);
        var hero = Hero(world);
        LayMines(world, 2);
        Assert.Null(Do(world, new WiredHangarCommand(Race.Red, true)));

        // Other weapons refit the hero, on the same chassis: the mines stay as they are.
        var weapon = state.Loadout.Weapons.OfType<WiredItem>().First();
        Assert.Null(Do(world, new WiredUnequipCommand(Race.Red, weapon.Id)));
        Assert.Equal(1, hero.Refits);
        Assert.Equal(3, hero.Mines);

        Board(world, "spy5");
        Assert.Equal(8, hero.Mines);
        Board(world, "grunt1");
        Assert.Equal(0, hero.Mines);
        Board(world, "spy3");
        Assert.Equal(5, hero.Mines);
        Assert.Same(hero, Hero(world));
    }

    // ---- the original's orders ----------------------------------------------------------------------

    [Theory]
    [InlineData("spy5")]
    [InlineData("phantom6")]
    public void A_spy_hero_drops_fetches_and_hides_nothing_by_the_original_s_orders_and_the_waves_spies_still_do(string chassis)
    {
        var world = Waiting(chassis);
        var hero = OutInTheOpen(world);
        var at = hero.Position;
        var carried = hero.Mines;
        world.AddConstruction(Race.Red, at + new Vector2(-50, -40), "GARRISON", null, 1, 0, complete: true);

        Assert.Equal("not in Wired", Do(world, new DropLandMinesCommand(Race.Red, [hero.Id], [at + new Vector2(20, 0)])));
        Assert.Equal("not in Wired", Do(world, new RefillLandMinesCommand(Race.Red, [hero.Id])));
        Assert.Equal("not in Wired", Do(world, new CamouflageCommand(Race.Red, [hero.Id])));
        Run(world, 3);
        Assert.Equal(carried, hero.Mines);
        Assert.False(hero.Camouflaged);
        Assert.Empty(world.LandMines);

        // A spy of the waves lays its mines, fetches more at its garrison and hides, as the original's spies do.
        var spy = Unarmed(world, at + new Vector2(50, 40));
        var point = spy.Position + new Vector2(15, 0);
        Assert.Null(Do(world, new DropLandMinesCommand(Race.Blue, [spy.Id], [point])));
        Run(world, 5);
        Assert.Equal(4, spy.Mines);
        Assert.Single(world.LandMines, m => m.Race == Race.Blue);

        var garrison = world.AddConstruction(Race.Blue, at + new Vector2(50, -40), "GARRISON", null, 1, 0, complete: true);
        Assert.Null(Do(world, new RefillLandMinesCommand(Race.Blue, [spy.Id])));
        var wentIn = false;
        for (var i = 0; i < 20 * 30 && !(wentIn && spy.Inside is null && spy.Mines == 5); i++)
        {
            world.Step();
            wentIn |= spy.Inside == garrison.Id;
        }
        Assert.True(wentIn);
        Assert.Equal(5, spy.Mines);

        Assert.Null(Do(world, new CamouflageCommand(Race.Blue, [spy.Id])));
        Run(world, 3);
        Assert.True(spy.Camouflaged);
    }

    // ---- the Phantom's shadow -----------------------------------------------------------------------

    [Theory]
    [InlineData("phantom6", 30, false)]
    [InlineData("phantom6", 20, true)]
    [InlineData("spy5", 30, true)]
    [InlineData("grunt1", 30, true)]
    public void A_quiet_phantom_is_lost_to_an_enemy_beyond_25_m_and_any_other_hero_is_not(string chassis, float metres, bool seen)
    {
        var world = Waiting(chassis);
        var hero = OutInTheOpen(world);
        var grunt = Grunt(world, AtDistance(world, hero, metres));
        world.Step();
        Assert.Equal(metres, grunt.DistanceTo(hero), 0.01f);
        Assert.True(world.Time - MathF.Max(Red(world).LastShotAt, Red(world).RevealedAt) >= WiredBalance.ShadowQuiet);

        Assert.Equal(!seen, world.Shadowed(grunt, hero));
        Assert.Equal(seen, world.NearestEnemy(grunt, world.ScannerReach(grunt)) == hero);
        var (attacked, fired) = (false, false);
        for (var i = 0; i < 20 * 4; i++)
        {
            world.Step();
            attacked |= grunt.Order is AttackOrder attack && attack.Target == hero.Id;
            fired |= world.Events.Any(e => e.Kind == GameEventKind.Fired && e.Actor == grunt.Id);
        }

        Assert.Equal(seen, attacked);
        Assert.Equal(seen, fired);
        // Its own side sees it wherever it is.
        var escort = world.AddMachine(Race.Red, AtDistance(world, hero, 40, 0.25f), "AGGRESSOR", "GRUNT", 1, "R_BOLTER");
        Assert.False(world.Shadowed(escort, hero));
    }

    [Fact]
    public void A_phantom_that_fires_or_calls_a_vortex_is_seen_until_it_has_been_quiet_for_three_seconds()
    {
        var world = Waiting("phantom6");
        var state = Red(world);
        var hero = OutInTheOpen(world);
        Assert.NotEmpty(hero.Weapons);
        var lookout = Unarmed(world, AtDistance(world, hero, 30));
        var target = Unarmed(world, AtDistance(world, hero, 15, 0.5f));
        world.Step();
        Assert.True(world.Shadowed(lookout, hero));
        Assert.Null(world.NearestEnemy(lookout, 100));

        // It fires: seen at once.
        hero.DrivenByPlayer = false;
        world.Issue(new AttackCommand(Race.Red, [hero.Id], target.Id));
        for (var i = 0; i < 20 * 5 && !world.Events.Any(e => e.Kind == GameEventKind.Fired && e.Actor == hero.Id); i++)
            world.Step();
        Assert.Contains(world.Events, e => e.Kind == GameEventKind.Fired && e.Actor == hero.Id);
        Assert.False(world.Shadowed(lookout, hero));
        Assert.Same(hero, world.NearestEnemy(lookout, 100));

        // Its fire over, the shadow falls again after three seconds.
        hero.DrivenByPlayer = true;
        world.Damage(target, 1_000_000, Race.Blue);
        var lastShot = state.LastShotAt;
        Run(world, WiredBalance.ShadowQuiet - 0.5f);
        Assert.Equal(lastShot, state.LastShotAt);
        Assert.False(world.Shadowed(lookout, hero));
        Run(world, 0.6f);
        Assert.True(world.Shadowed(lookout, hero));
        Assert.Null(world.NearestEnemy(lookout, 100));

        // A vortex gives it away as a shot does, though it is no round of a weapon.
        state.Loadout = state.Loadout.With(WiredSlot.Ability(0), state.Add(WiredKind.Ability, "vortex", 0));
        Assert.Null(Do(world, new WiredAbilityCommand(Race.Red, 0, AtDistance(world, hero, 70, 0.5f))));
        Assert.Equal(lastShot, state.LastShotAt);
        Assert.False(world.Shadowed(lookout, hero));
        Assert.Same(hero, world.NearestEnemy(lookout, 100));
        Run(world, WiredBalance.ShadowQuiet + 0.1f);
        Assert.True(world.Shadowed(lookout, hero));
    }

    [Theory]
    [InlineData("phantom6", true)]
    [InlineData("spy5", false)]
    public void An_attack_on_a_hero_that_falls_quiet_beyond_25_m_is_dropped_in_a_shadow_and_not_taken_up_again(string chassis, bool dropped)
    {
        var world = Waiting(chassis);
        var state = Red(world);
        var hero = OutInTheOpen(world);
        var grunt = Grunt(world, AtDistance(world, hero, 30));
        // Seen a moment ago, so that the attack is taken on.
        state.RevealedAt = world.Time;
        var seenAt = world.Time;
        Assert.Null(Do(world, new AttackCommand(Race.Blue, [grunt.Id], hero.Id)));
        Assert.Equal(hero.Id, Assert.IsType<AttackOrder>(grunt.Order).Target);
        float? lostAt = null;
        var takenUpAgain = false;

        for (var i = 0; i < 20 * (WiredBalance.ShadowQuiet + 3); i++)
        {
            world.Step();
            var onHero = grunt.Order is AttackOrder attack && attack.Target == hero.Id;
            if (!onHero)
                lostAt ??= world.Time;
            else if (lostAt is not null)
                takenUpAgain = true;
        }

        Assert.Equal(dropped, lostAt is not null);
        Assert.False(takenUpAgain);
        Assert.True(grunt.DistanceTo(hero) > WiredBalance.ShadowReach, $"the grunt came up to {grunt.DistanceTo(hero)} m, so this shows nothing");
        if (lostAt is { } lost)
            Assert.InRange(lost - seenAt, WiredBalance.ShadowQuiet, WiredBalance.ShadowQuiet + 0.2f);
    }

    /// <summary>
    /// A run of the Phantom whose first wave is down to one hunter, with a last group that never comes; the hero at the wheel
    /// out in the open, the hunter to one side of the pod, the other side from the hero.
    /// </summary>
    private static (GameWorld World, WiredFoe Hunter) OneHunter()
    {
        for (var seed = 1; seed <= 40; seed++)
        {
            var world = NewRun("phantom6", seed);
            var run = world.Wired!;
            run.Countdown = 0;
            world.Step();
            world.Step();
            Assert.Equal(WiredPhase.Wave, run.Phase);
            if (run.Foes.FirstOrDefault(f => f.HuntsHero) is not { } hunter)
                continue;
            run.Groups.Clear();
            run.Groups.Add(new WiredGroup(100_000, [hunter.Plan]));
            foreach (var other in run.Foes.Where(f => f != hunter).ToList())
                world.Damage(other.Machine, 1_000_000, Race.Blue);
            var hero = OutInTheOpen(world);
            var pod = Pod(world).Position;
            var away = Vector2.Normalize(hero.Position - pod);
            hunter.Machine.Position = world.Nav.NearestFreePoint(pod + new Vector2(-away.Y, away.X) * 150, crossesWater: false)!.Value;
            hunter.Machine.Path.Clear();
            world.Step();
            Assert.Equal(hunter, Assert.Single(run.Foes));
            return (world, hunter);
        }
        throw new InvalidOperationException("no first wave with a hunter");
    }

    [Fact]
    public void A_hunter_that_loses_the_phantom_marches_on_the_pod_and_is_not_sent_back_at_the_next_reviews()
    {
        var (world, hunter) = OneHunter();
        var (run, state, hero, pod) = (world.Wired!, Red(world), Hero(world), Pod(world));
        bool OnHero() => hunter.Machine.Order is AttackOrder attack && attack.Target == hero.Id;
        // Hidden from the start, it was sent to the pod with the others.
        Assert.False(OnHero());

        // Seen, the next review sets it on the hero.
        state.RevealedAt = world.Time;
        run.NextReview = world.Time;
        world.Step();
        Assert.True(OnHero());
        Assert.Equal(pod.EntranceOutside, Assert.IsType<AttackOrder>(hunter.Machine.Order).Then);

        // Quiet again: it goes for the pod, and review after review leaves it at that.
        Run(world, WiredBalance.ShadowQuiet + 0.2f);
        Assert.False(OnHero());
        var reviews = 0;
        for (var i = 0; i < 20 * 3 * WiredBalance.OrdersEvery; i++)
        {
            var next = run.NextReview;
            world.Step();
            reviews += run.NextReview != next ? 1 : 0;
            Assert.False(OnHero(), $"sent back at {world.Time} s");
            Assert.True(hunter.Machine.DistanceTo(hero) > WiredBalance.ShadowReach);
        }
        Assert.True(reviews >= 3, $"{reviews} reviews");
        Assert.True(hunter.Machine.Order is AttackMoveOrder || hunter.Machine.Order is AttackOrder attack && attack.Target == pod.Id,
            $"{hunter.Machine.Order}");
    }

    [Fact]
    public void An_enemy_mine_still_goes_off_under_a_hidden_phantom()
    {
        var world = Waiting("phantom6");
        var hero = OutInTheOpen(world);
        var lookout = Unarmed(world, AtDistance(world, hero, 60));
        world.Step();
        Assert.True(world.Shadowed(lookout, hero));
        var before = hero.HitPoints + hero.Armour;

        world.LandMines.Add(new LandMineState(9001, Race.Blue, hero.Position + new Vector2(2, 0)));
        var events = Run(world, 0.5f);

        Assert.Contains(events, e => e.Kind == GameEventKind.MineExploded && e.Actor == hero.Id);
        Assert.Empty(world.LandMines);
        Assert.True(hero.HitPoints + hero.Armour < before);
    }

    private static GameWorld BattleOf(string chassis)
    {
        var tier = BattleCatalogue.Tier(chassis);
        var options = BattleBots.Compose(new BattleOptions { Tier = tier, TeamA = [Race.Red, Race.Blue], TeamB = [Race.Green, Race.Yellow] },
            [new BattleHero(Race.Red, "Ace", chassis, BattleBots.Loadout(chassis, tier))]);
        return Battles.Begun(options);
    }

    /// <summary>A free place that far from a machine's edge, clear of both capture circles.</summary>
    private static Vector2 Afield(GameWorld world, Actor near, float metres, float radius) =>
        Enumerable.Range(0, 64).Select(k => near.Position + new Vector2(DetMath.Cos(k * MathF.Tau / 64), DetMath.Sin(k * MathF.Tau / 64)) * (metres + radius))
            .First(p => world.Nav.IsFree(p, crossesWater: false)
                && Enumerable.Range(0, 2).All(team => Vector2.Distance(p, world.BattlePod(team)!.Position) > BattleBalance.CaptureRadius + 10));

    [Theory]
    [InlineData("phantom6", false)]
    [InlineData("spy5", true)]
    public void A_battle_bot_does_not_fight_a_hidden_phantom(string chassis, bool fought)
    {
        var world = BattleOf(chassis);
        var (red, green) = (Battles.Hero(world, Race.Red), Battles.Hero(world, Race.Green));
        red.Position = Afield(world, green, 45, red.Radius);
        red.Path.Clear();
        world.Step();
        Assert.Equal(!fought, world.Shadowed(green, red));
        var commands = new List<Command>();

        new BattleBot(world, Race.Green, commands.Add).Think();

        Assert.Equal(fought, commands.OfType<AttackCommand>().Any(c => c.Target == red.Id));
    }

    [Fact]
    public void In_a_battle_the_enemy_s_circle_shows_the_phantom_and_out_of_it_the_shadow_falls_again()
    {
        var world = BattleOf("phantom6");
        var state = world.Wired!.Heroes[Race.Red];
        var (red, green) = (Battles.Hero(world, Race.Red), Battles.Hero(world, Race.Green));
        var circle = world.BattlePod(1)!.Position;
        // The enemy hero well away from its pod, and the Phantom between the pods, both clear of the circles.
        Battles.Away(world, Race.Green, Afield(world, world.BattlePod(1)!, 80, 0));
        Battles.Away(world, Race.Red, Afield(world, green, 50, red.Radius));
        world.Step();
        Assert.True(world.Shadowed(green, red));

        Battles.IntoCircle(world, Race.Red);
        Battles.Run(world, 1);
        Assert.True(Vector2.Distance(red.Position, circle) <= BattleBalance.CaptureRadius);
        Assert.True(green.DistanceTo(red) > WiredBalance.ShadowReach, $"the enemy stands {green.DistanceTo(red)} m off, so this shows nothing");
        Assert.False(world.Shadowed(green, red));
        Assert.True(world.Time - state.RevealedAt < 0.1f);

        Battles.Away(world, Race.Red, Afield(world, green, 50, red.Radius));
        Battles.Run(world, WiredBalance.ShadowQuiet + 0.2f);
        Assert.True(world.Shadowed(green, red));
    }

    // ---- what the HUD reads -----------------------------------------------------------------------

    [Fact]
    public void The_hud_reads_the_mines_carried_of_the_most_and_the_wait_for_the_next()
    {
        var world = Waiting("spy3");

        var view = world.SpecialView(Race.Red)!;
        Assert.Equal((ChassisSpecial.Mines, "Mines", (string?)null, 5, 5, true, (string?)null),
            (view.Special, view.Name, view.Phase, view.Count, view.Most, view.Ready, view.Why));
        Assert.Equal((0f, 1f), (view.SecondsLeft, view.Share));

        Assert.Null(Lay(world));
        view = world.SpecialView(Race.Red)!;
        Assert.Equal((4, 5, false, "that is not ready yet"), (view.Count, view.Most, view.Ready, view.Why));
        Assert.InRange(view.SecondsLeft, 0.9f, WiredBalance.MineLayGap);
        Assert.InRange(view.Share, 0, 0.1f);

        Run(world, WiredBalance.MineLayGap + 0.05f);
        view = world.SpecialView(Race.Red)!;
        Assert.Equal((4, true, 0f, 1f), (view.Count, view.Ready, view.SecondsLeft, view.Share));
    }

    [Fact]
    public void A_phantom_s_hud_shows_it_hidden_or_seen_with_the_seconds_until_it_is_hidden()
    {
        var world = Waiting("phantom6");
        var state = Red(world);

        var view = world.SpecialView(Race.Red)!;
        Assert.Equal((ChassisSpecial.Mines | ChassisSpecial.Shadow, "Mines", "Hidden", 0f, 1f, 8, 8, true),
            (view.Special, view.Name, view.Phase, view.SecondsLeft, view.Share, view.Count, view.Most, view.Ready));

        state.RevealedAt = world.Time;
        view = world.SpecialView(Race.Red)!;
        Assert.Equal(("Seen", WiredBalance.ShadowQuiet, 0f), (view.Phase, view.SecondsLeft, view.Share));

        Run(world, 1);
        view = world.SpecialView(Race.Red)!;
        Assert.Equal("Seen", view.Phase);
        Assert.InRange(view.SecondsLeft, 1.9f, 2.05f);

        Run(world, 2.05f);
        view = world.SpecialView(Race.Red)!;
        Assert.Equal(("Hidden", 0f, 1f), (view.Phase, view.SecondsLeft, view.Share));
        // The mines are its key all the while.
        Assert.True(view.Ready);
    }

    // ---- the same game ------------------------------------------------------------------------------

    [Fact]
    public void Two_runs_fed_the_same_commands_lay_and_set_off_the_same_mines()
    {
        (List<ulong> Sums, int Laid, int Exploded) Play()
        {
            var world = Waiting("spy5");
            var hero = Hero(world);
            var start = hero.Position;
            var goal = OpenGround(world, 100);
            var (sums, laid, exploded) = (new List<ulong>(), 0, 0);
            world.Issue(new MoveCommand(Race.Red, [hero.Id], goal));
            for (var i = 0; i < 20 * 16; i++)
            {
                if (i < 20 * 8 && i % 25 == 5)
                    world.Issue(new WiredSpecialCommand(Race.Red));
                if (i == 20 * 8)
                {
                    var grunt = Grunt(world, world.Nav.NearestFreePoint(start, crossesWater: false)!.Value);
                    world.Issue(new MoveCommand(Race.Blue, [grunt.Id], goal));
                }
                world.Step();
                laid += world.Events.Count(e => e.Kind == GameEventKind.MineLaid);
                exploded += world.Events.Count(e => e.Kind == GameEventKind.MineExploded);
                if (i % 20 == 0)
                    sums.Add(world.Checksum());
            }
            return (sums, laid, exploded);
        }

        var (first, second) = (Play(), Play());

        Assert.True(first.Laid >= 5, $"{first.Laid} mines laid");
        Assert.True(first.Exploded > 0, "no mine went off");
        Assert.Equal((first.Laid, first.Exploded), (second.Laid, second.Exploded));
        Assert.Equal(first.Sums, second.Sums);
    }
}
