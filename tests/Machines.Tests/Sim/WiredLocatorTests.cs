using System.Numerics;
using System.Reflection;
using Machines.Import.Model;
using Machines.Sim;
using Machines.Sim.Ai;
using Machines.Sim.Combat;
using Machines.Sim.Survival;
using Machines.Sim.World;

namespace Machines.Tests.Sim;

/// <summary>
/// The locator line of Wired: Geo Locator 1, the crab, a second start beside Grunt 1 that a hero wears tuned for speed and
/// sight, and Geo Locator 3, the first chassis that flies; their pulse, which locates every enemy within the hero's scanner
/// reach for ten seconds so that the hero's side hits it a fifth harder; and what the original's geo locators do that a
/// hero does not.
/// </summary>
public class WiredLocatorTests
{
    private static GameData Data => Battles.Data;

    private static GameWorld NewRun(string chassis, string map = "m_desert", int seed = 7) =>
        MatchSetup.Create(Data, map, map,
            new Dictionary<Race, Controller> { [Race.Red] = Controller.Player, [Race.Blue] = Controller.Ai }, seed,
            new MatchOptions { Wired = new WiredOptions { Chassis = chassis } });

    private static GameWorld NewMatch() =>
        MatchSetup.Create(Data, "m_desert", "m_desert",
            new Dictionary<Race, Controller> { [Race.Red] = Controller.Player, [Race.Blue] = Controller.Player }, seed: 7);

    /// <summary>A run on a chassis whose first wave waits as long as a test takes, the hero at the pod's door, by day.</summary>
    private static GameWorld Waiting(string chassis, string map = "m_desert", int seed = 7)
    {
        var world = NewRun(chassis, map, seed);
        world.Wired!.Countdown = 100_000;
        world.ForcedDarkness = world.Darkness = 0;
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

    /// <summary>The special's key: a geo locator hero sends out its pulse.</summary>
    private static string? Pulse(GameWorld world) => Do(world, new WiredSpecialCommand(Race.Red));

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

    /// <summary>Steps the world until its time has come to <paramref name="time"/>.</summary>
    private static void Until(GameWorld world, float time)
    {
        while (world.Time < time - GameWorld.Dt / 2)
            world.Step();
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
    /// A point on free ground that many metres from a machine's centre, the <paramref name="skip"/>-th such of 64 round it.
    /// </summary>
    private static Vector2 Afield(GameWorld world, Actor near, float metres, int skip = 0) =>
        Enumerable.Range(0, 64).Select(k => near.Position + new Vector2(DetMath.Cos(k * MathF.Tau / 64), DetMath.Sin(k * MathF.Tau / 64)) * metres)
            .Where(p => world.Nav.IsFree(p, crossesWater: false)).Skip(skip).First();

    /// <summary>A spy of the waves: it looks for enemies as any machine does, and shoots at nobody.</summary>
    private static Machine Unarmed(GameWorld world, Vector2 at, Race race = Race.Blue) =>
        world.AddMachine(race, at, WiredDirector.Spy.Type, WiredDirector.Spy.SubType, WiredDirector.Spy.HardwareLevel, WiredDirector.Spy.WeaponCombo);

    /// <summary>A Knight 4 of the waves: tough enough to take a hit and count what it lost.</summary>
    private static Machine Tough(GameWorld world, Vector2 at) => world.AddMachine(Race.Blue, at, "AGGRESSOR", "KNIGHT", 4, "LR_MULTI_LAUNCHER3_X2");

    private static HeroLoadout Loadout(string chassis) => HeroLoadout.Bare(new WiredItem(1, WiredKind.Chassis, chassis));

    /// <summary>Asks the world something it keeps to itself.</summary>
    private static object? Call(GameWorld world, string method, params object?[] arguments) =>
        typeof(GameWorld).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(world, arguments);

    private static float Share(GameWorld world, Actor target, Actor? dealer) => (float)Call(world, "LocatedShare", target, dealer)!;

    private static int Durability(Actor actor) => actor.HitPoints + actor.Armour;

    // ---- the chassis as heroes --------------------------------------------------------------------

    [Theory]
    [InlineData("geo1", 36, 15, 0.25f, 16f, 90f, 160, 60, Locomotion.Spider)]
    [InlineData("geo3", 66, 15, 0.9f, 20f, 70f, 250, 60, Locomotion.Glider)]
    public void The_crab_hero_trades_toughness_for_speed_and_sight_and_the_glider_flies_as_its_data_says(
        string chassis, int hitPoints, int armour, float repair, float speed, float turn, int scanner, int accuracy, Locomotion locomotion)
    {
        var stats = HeroBuild.Resolve(Data, Loadout(chassis)).Stats;

        Assert.Equal((hitPoints, armour, repair), (stats.HitPoints, stats.Armour, stats.RepairRate));
        Assert.Equal((speed, turn, scanner), (stats.Speed, stats.RotationSpeed, stats.ScannerRange));
        Assert.Equal((accuracy, locomotion), (stats.Accuracy, stats.Locomotion));

        // The hero of a run on it is that machine.
        var hero = Hero(Waiting(chassis));
        Assert.Equal((hitPoints, armour, speed, scanner), (hero.MaxHitPoints, hero.MaxArmour, hero.Stats.Speed, hero.Stats.ScannerRange));
        Assert.Equal(locomotion, hero.Stats.Locomotion);
    }

    [Fact]
    public void Driven_the_crab_and_grunt_1_turn_alike_at_the_first_person_cap()
    {
        var turns = new[] { "geo1", "grunt1" }.Select(chassis =>
        {
            var world = Waiting(chassis);
            return world.DrivenTurnRate(Hero(world), slow: false);
        }).ToList();

        Assert.Equal(45f, Data.Parm.General.FirstPersonMaxFastTurnRate);
        Assert.Equal([45f, 45f], turns);
    }

    [Theory]
    [InlineData("geo3", 1.5f)]
    [InlineData("boss1", 1.5f)]
    [InlineData("geo1", 1f)]
    [InlineData("grunt1", 1f)]
    public void Whatever_flies_reaches_half_again_as_far_for_a_wreck(string chassis, float share)
    {
        Assert.Equal(WiredBalance.PickupReach * share, HeroBuild.Resolve(Data, Loadout(chassis)).PickupReach);
    }

    [Fact]
    public void Only_the_crab_is_tuned_as_a_hero_and_the_original_s_crab_stays_as_it_is()
    {
        foreach (var spec in Data.Parm.Machines)
        {
            foreach (var level in spec.Levels)
            {
                var asHero = WiredMachines.AsHero(spec.TypeName, level);
                if (spec.TypeName == "GEO_LOCATOR" && level.HardwareLevel == 1)
                    Assert.NotSame(level, asHero);
                else
                    Assert.Same(level, asHero);
            }
        }

        // The data and every machine outside a hero keep the original's crab: 30 hit points, 10 armour, 6 m/s.
        var crab = Data.Parm.Machine("GEO_LOCATOR").Level(1);
        Assert.Equal((30, 10, 6f, 40f, 70), (crab.HitPoints, crab.Armour, crab.Speed, crab.RotationSpeed, crab.ScannerRange));
        Assert.Null(crab.Accuracy);
        var match = NewMatch();
        var walker = match.AddMachine(Race.Red, OpenGround(match), "GEO_LOCATOR", null, 1, null);
        Assert.Equal((30, 10, 6f), (walker.MaxHitPoints, walker.MaxArmour, walker.Stats.Speed));
        // No name, no body and no model of Wired's own come with the tune.
        Assert.False(WiredMachines.IsWired("GEO_LOCATOR", 1));
        Assert.Null(WiredMachines.Find("GEO_LOCATOR", 1));
    }

    /// <summary>A place with open ground 70 m round it, in a match without a pod.</summary>
    private static Vector2 OpenGround(GameWorld match) => Enumerable.Range(0, 400).Select(i => new Vector2(200 + i % 20 * 60, 200 + i / 20 * 60))
        .First(p => match.Nav.IsAreaFree((p - new Vector2(70, 70), p + new Vector2(70, 70))));

    [Fact]
    public void The_crab_s_tune_carries_its_accelerations_along()
    {
        var crab = Data.Parm.Machine("GEO_LOCATOR").Level(1);
        var hero = WiredMachines.AsHero("GEO_LOCATOR", crab);
        var (move, turn) = (16f / 6, 90f / 40);

        Assert.Equal(crab.Acceleration * move, hero.Acceleration, 3);
        Assert.Equal(crab.TaskSpeed * move, hero.TaskSpeed, 3);
        Assert.Equal(crab.RotationAcceleration * turn, hero.RotationAcceleration, 3);
        Assert.Equal((crab.Locomotion, crab.Cost, crab.HighClearance, crab.LowClearance, crab.HardwareLevel),
            (hero.Locomotion, hero.Cost, hero.HighClearance, hero.LowClearance, hero.HardwareLevel));
    }

    [Fact]
    public void The_crab_walks_at_the_original_s_speed_over_its_own_and_everything_else_at_its_own()
    {
        var crab = Data.Parm.Machine("GEO_LOCATOR").Level(1);

        Assert.Equal(0.375f, WiredMachines.GaitShare("GEO_LOCATOR", 1));
        Assert.Equal(crab.Speed / WiredMachines.AsHero("GEO_LOCATOR", crab).Speed, WiredMachines.GaitShare("GEO_LOCATOR", 1));
        Assert.Equal(1f, WiredMachines.GaitShare("GEO_LOCATOR", 3));
        Assert.All(WiredCatalogue.Chassis.Where(c => c.Id != "geo1"), c => Assert.Equal(1f, WiredMachines.GaitShare(c.TypeName, c.Level)));
    }

    // ---- the catalogue ----------------------------------------------------------------------------

    [Fact]
    public void The_locators_close_the_catalogue_and_grunt_1_and_the_crab_are_its_roots()
    {
        Assert.Equal(34, WiredCatalogue.Chassis.Count);
        Assert.Equal("grunt1", WiredCatalogue.Chassis[0].Id);
        Assert.Equal(["geo1", "geo3"], WiredCatalogue.Chassis.TakeLast(2).Select(c => c.Id));
        Assert.Equal(["grunt1", "geo1"], WiredCatalogue.Roots.Select(c => c.Id));

        var (crab, glider) = (WiredCatalogue.FindChassis("geo1")!, WiredCatalogue.FindChassis("geo3")!);
        Assert.Equal(("GEO_LOCATOR", (string?)null, 1, "GEO_LOCATOR"), (crab.Type, crab.SubType, crab.Level, crab.TypeName));
        Assert.Equal((0, 0, 1, 6, 6, ChassisSpecial.Locate), (crab.Price, crab.FromWave, crab.Modules, crab.Load, crab.Power, crab.Special));
        Assert.Equal((280, 5, 2, 7, 9, ChassisSpecial.Locate), (glider.Price, glider.FromWave, glider.Modules, glider.Load, glider.Power, glider.Special));
        Assert.Equal(["geo1"], glider.Needs);
        Assert.All([crab, glider], c => Assert.Equal(new ChassisSlot(Mounting.Top, SlotSize.Light), Assert.Single(c.Slots)));
        Assert.All([crab, glider], c => Assert.False(c.Glider));
    }

    [Fact]
    public void Boss_1_opens_from_overseer_3_or_the_glider()
    {
        Assert.Equal(["overseer3", "geo3"], WiredCatalogue.FindChassis("boss1")!.Needs);
    }

    [Theory]
    [InlineData("geo3", Mounting.Top, "WASP_LIGHT_STING", "that fits only the top slot of the Boss line")]
    [InlineData("geo3", Mounting.Top, "BEE_BOMB", "that fits only the top slot of the Boss line")]
    [InlineData("geo1", Mounting.Top, "WASP_LIGHT_STING", "that fits only the top slot of the Boss line")]
    [InlineData("grunt1", Mounting.Right, "WASP_LIGHT_STING", "that fits only the top slot of the Boss line")]
    [InlineData("boss1", Mounting.Top, "WASP_LIGHT_STING", null)]
    [InlineData("geo3", Mounting.Top, "PLASMA_RIFLE", null)]
    public void The_stings_fit_the_top_of_the_boss_line_only_and_every_other_chassis_hears_the_same(string chassis, Mounting mounting, string weapon, string? why)
    {
        Assert.Equal(why, WiredCatalogue.WhyNotFit(WiredCatalogue.FindChassis(chassis)!, mounting, WiredCatalogue.FindWeapon(weapon)!));
    }

    [Fact]
    public void The_glider_is_sold_from_wave_5_and_the_crab_from_the_start()
    {
        var world = Waiting("grunt1");
        var run = world.Wired!;
        var (crab, glider) = (WiredCatalogue.FindChassis("geo1")!, WiredCatalogue.FindChassis("geo3")!);

        Assert.Equal(1, run.ShopWave);
        Assert.False(world.TooEarly(crab));
        run.Wave = 3;
        Assert.True(world.TooEarly(glider));
        run.Wave = 4;
        Assert.Equal(5, run.ShopWave);
        Assert.False(world.TooEarly(glider));
    }

    // ---- the pulse --------------------------------------------------------------------------------

    [Fact]
    public void A_pulse_locates_an_enemy_a_metre_inside_the_scanner_s_reach_and_not_one_a_metre_outside()
    {
        var world = Waiting("geo1");
        var hero = OutInTheOpen(world);
        var reach = world.ScannerReach(hero);
        Assert.Equal(160f, reach);
        var inside = Unarmed(world, Afield(world, hero, reach - 1));
        var outside = Unarmed(world, Afield(world, hero, reach + 1, 8));

        Assert.Null(Pulse(world));

        Assert.Equal(WiredBalance.LocateTime - GameWorld.Dt, world.LocatedFor(inside.Id), 3);
        Assert.Equal(0, world.LocatedFor(outside.Id));
        Assert.Equal([inside.Id], world.Wired!.Located.Keys);
        var located = Assert.Single(world.Events, e => e.Kind == GameEventKind.WiredLocated);
        Assert.Equal((hero.Id, (int?)1), (located.Actor, located.Other));
    }

    [Fact]
    public void At_night_the_pulse_reaches_as_far_as_the_scanner_sees_in_the_dark()
    {
        var world = Waiting("geo1");
        var hero = OutInTheOpen(world);
        world.ForcedDarkness = world.Darkness = 1;
        var reach = world.ScannerReach(hero);
        Assert.Equal(80f, reach);
        var inside = Unarmed(world, Afield(world, hero, reach - 1));
        var outside = Unarmed(world, Afield(world, hero, 120, 8));

        Assert.Null(Pulse(world));

        Assert.True(world.LocatedFor(inside.Id) > 0);
        Assert.Equal(0, world.LocatedFor(outside.Id));
    }

    [Fact]
    public void Allies_machines_inside_and_fallen_ones_are_not_located()
    {
        var world = Waiting("geo1");
        var hero = OutInTheOpen(world);
        var ally = Unarmed(world, Afield(world, hero, 40), Race.Red);
        var inside = Unarmed(world, Afield(world, hero, 50, 8));
        inside.Inside = Pod(world).Id;
        var fallen = Unarmed(world, Afield(world, hero, 60, 16));
        var foe = Unarmed(world, Afield(world, hero, 70, 24));
        world.Damage(fallen, 1_000_000, Race.Red);
        Assert.True(fallen.IsDead);

        Assert.Null(Pulse(world));

        Assert.Equal([foe.Id], world.Wired!.Located.Keys);
        Assert.Equal((int?)1, Assert.Single(world.Events, e => e.Kind == GameEventKind.WiredLocated).Other);
        Assert.All([ally, inside, fallen], m => Assert.Equal(0, world.LocatedFor(m.Id)));
    }

    [Fact]
    public void A_later_pulse_renews_the_location_and_never_shortens_it()
    {
        var world = Waiting("geo1");
        var (run, state, hero) = (world.Wired!, Red(world), OutInTheOpen(world));
        var foe = Unarmed(world, Afield(world, hero, 60));
        var first = world.Time;
        Assert.Null(Pulse(world));
        Assert.Equal(first + WiredBalance.LocateTime, run.Located[foe.Id]);

        // A second pulse, its wait set aside, five seconds on: located for ten seconds from then.
        Run(world, 5);
        state.LocateReadyAt = float.NegativeInfinity;
        var second = world.Time;
        Assert.Null(Pulse(world));
        Assert.Equal(second + WiredBalance.LocateTime, run.Located[foe.Id]);

        // One located for longer than a pulse would make it keeps its time.
        var longer = world.Time + 3 * WiredBalance.LocateTime;
        run.Located[foe.Id] = longer;
        state.LocateReadyAt = float.NegativeInfinity;
        Assert.Null(Pulse(world));
        Assert.Equal(longer, run.Located[foe.Id]);
    }

    [Fact]
    public void The_next_pulse_waits_twenty_seconds_and_not_a_tick_longer()
    {
        var world = Waiting("geo3");
        var state = Red(world);
        var pulsed = world.Time;
        Assert.Null(Pulse(world));
        Assert.Equal(pulsed + WiredBalance.LocateCooldown, state.LocateReadyAt);

        Assert.Equal("that is not ready yet", Pulse(world));
        Until(world, pulsed + WiredBalance.LocateCooldown - 0.1f);
        Assert.Equal("that is not ready yet", world.WhyNoLocate(state));
        Assert.Equal("that is not ready yet", Pulse(world));
        Until(world, pulsed + WiredBalance.LocateCooldown);
        Assert.Null(world.WhyNoLocate(state));
        Assert.Null(Pulse(world));
    }

    [Fact]
    public void A_pulse_with_nothing_in_reach_goes_out_all_the_same_and_spends_the_wait()
    {
        var world = Waiting("geo1");
        var state = Red(world);
        OutInTheOpen(world);
        Assert.DoesNotContain(world.Machines, m => m.Race != Race.Red);

        Assert.Null(Pulse(world));

        Assert.Equal((int?)0, Assert.Single(world.Events, e => e.Kind == GameEventKind.WiredLocated).Other);
        Assert.Empty(world.Wired!.Located);
        Assert.Equal("that is not ready yet", world.WhyNoLocate(state));
    }

    [Fact]
    public void No_pulse_goes_out_from_inside_the_pod_or_from_a_hero_that_is_down()
    {
        var world = Waiting("geo1");
        var (state, hero, pod) = (Red(world), Hero(world), Pod(world));

        Assert.Null(Do(world, new EnterGarrisonCommand(Race.Red, [hero.Id], pod.Id)));
        for (var i = 0; i < 20 * 20 && hero.Inside is null; i++)
            world.Step();
        Assert.Equal(pod.Id, hero.Inside);
        Assert.Equal("not from inside the pod", Pulse(world));
        Assert.Equal("not from inside the pod", world.WhyNoLocate(state));

        world.Damage(hero, 1_000_000, Race.Blue);
        world.Step();
        Assert.Equal("the hero is down", Pulse(world));
        Assert.Equal("the hero is down", world.WhyNoLocate(state));
        Assert.True(float.IsNegativeInfinity(state.LocateReadyAt));
    }

    /// <summary>A battle of the first tier with Red's hero on the crab, the others bots of the tier: crabs again for Blue and Yellow.</summary>
    private static GameWorld CrabBattle()
    {
        var options = BattleBots.Compose(new BattleOptions { Tier = 1, TeamA = [Race.Red, Race.Blue], TeamB = [Race.Green, Race.Yellow] },
            [new BattleHero(Race.Red, "Ace", "geo1", BattleBots.Loadout("geo1", 1))]);
        return Battles.New(options);
    }

    [Fact]
    public void In_a_battle_the_pulse_waits_for_the_opening_and_then_locates_enemy_heroes()
    {
        var world = CrabBattle();
        var state = world.Wired!.Heroes[Race.Red];
        Assert.Equal("geo1", state.Loadout.Chassis.Catalogue);

        Assert.Equal("the battle has not begun yet", Battles.Do(world, new WiredSpecialCommand(Race.Red)));
        Battles.Run(world, BattleBalance.Opening + GameWorld.Dt);
        Assert.False(world.Battle!.InOpening(world.Time));
        var red = Battles.Hero(world, Race.Red);
        var (green, blue) = (Battles.Hero(world, Race.Green), Battles.Hero(world, Race.Blue));
        Battles.Away(world, Race.Green, Afield(world, red, 100));
        Assert.True(Vector2.Distance(green.Position, red.Position) < world.ScannerReach(red));

        Assert.Null(Battles.Do(world, new WiredSpecialCommand(Race.Red)));

        Assert.True(world.LocatedFor(green.Id) > 0);
        Assert.Equal(0, world.LocatedFor(blue.Id));
    }

    [Fact]
    public void The_pulse_is_no_shot_and_gives_nothing_away()
    {
        var world = Waiting("geo1");
        var state = Red(world);
        var hero = OutInTheOpen(world);
        Unarmed(world, Afield(world, hero, 60));

        Assert.Null(Pulse(world));

        Assert.True(float.IsNegativeInfinity(state.LastShotAt));
        Assert.True(float.IsNegativeInfinity(state.RevealedAt));
        Assert.DoesNotContain(world.Events, e => e.Kind == GameEventKind.Fired);
    }

    [Theory]
    [InlineData("grunt1", "this chassis has nothing to use")]
    [InlineData("phantom6", null)]
    public void A_chassis_without_the_pulse_has_none_to_send_out_and_its_key_does_what_it_did(string chassis, string? special)
    {
        var world = Waiting(chassis);
        var state = Red(world);

        Assert.Equal("this chassis has nothing to use", world.WhyNoLocate(state));
        Assert.Equal(special, world.WhyNoSpecial(state));
        Assert.Equal(special, Pulse(world));
        Assert.True(float.IsNegativeInfinity(state.LocateReadyAt));
        Assert.DoesNotContain(world.Events, e => e.Kind == GameEventKind.WiredLocated);
    }

    // ---- what a located machine takes -------------------------------------------------------------

    [Fact]
    public void A_located_foe_takes_a_fifth_more_from_the_hero_an_escort_and_a_tower_and_from_its_own_side_and_a_mine_nothing_more()
    {
        var world = Waiting("geo1");
        var (run, state, hero) = (world.Wired!, Red(world), OutInTheOpen(world));
        var foe = Tough(world, Afield(world, hero, 40));
        var escort = Unarmed(world, Afield(world, hero, 20, 8), Race.Red);
        state.EscortIds.Add(escort.Id);
        var tower = world.AddConstruction(Race.Red, Afield(world, hero, 30, 16), "MISSILE_EMPLACEMENT", "TURRET", 1, 0, complete: true);
        var friend = Unarmed(world, Afield(world, hero, 50, 24));

        Assert.All(new Actor[] { hero, escort, tower }, dealer => Assert.Equal(1f, Share(world, foe, dealer)));
        Assert.Null(Pulse(world));
        Assert.True(world.LocatedFor(foe.Id) > 0);

        Assert.All(new Actor[] { hero, escort, tower }, dealer => Assert.Equal(WiredBalance.LocateShare, Share(world, foe, dealer)));
        Assert.Equal(1f, Share(world, foe, friend));
        Assert.Equal(1f, Share(world, foe, null));
        // Located itself, the friend takes no more from its own side.
        Assert.True(world.LocatedFor(friend.Id) > 0);
        Assert.Equal(1f, Share(world, friend, foe));

        // A real hit from the hero, and a mine's, which has no dealer; the tower is taken down first, so that it adds nothing.
        world.Damage(tower, 1_000_000, Race.Blue);
        Assert.True(tower.IsDead);
        var before = Durability(foe);
        world.Damage(foe, 10, Race.Red, hero, "BOLTER");
        Assert.Equal(12, before - Durability(foe));
        before = Durability(foe);
        world.LandMines.Add(new LandMineState(9001, Race.Red, foe.Position));
        var events = Run(world, 0.5f);
        Assert.Contains(events, e => e.Kind == GameEventKind.MineExploded && e.Actor == foe.Id);
        Assert.Equal(80, before - Durability(foe));
        Assert.Contains(foe.Id, run.Located.Keys);
    }

    [Fact]
    public void Ten_seconds_on_the_foe_is_located_no_more_and_takes_as_before()
    {
        var world = Waiting("geo1");
        var (run, hero) = (world.Wired!, OutInTheOpen(world));
        // Unarmed, so that the hero still stands to deal the hit when the time is up.
        var foe = Unarmed(world, Afield(world, hero, 40));
        var pulsed = world.Time;
        Assert.Null(Pulse(world));

        Until(world, pulsed + WiredBalance.LocateTime - GameWorld.Dt);
        Assert.Equal(WiredBalance.LocateShare, Share(world, foe, hero));
        Assert.Equal(GameWorld.Dt, world.LocatedFor(foe.Id), 3);
        world.Step();

        Assert.Equal(1f, Share(world, foe, hero));
        Assert.Equal(0, world.LocatedFor(foe.Id));
        // The run's next tick forgets it.
        Assert.Contains(foe.Id, run.Located.Keys);
        world.Step();
        Assert.Empty(run.Located);
    }

    [Fact]
    public void A_located_foe_that_falls_is_forgotten_at_once_and_leaves_nothing_in_the_checksum()
    {
        // Two runs alike but for the first foe: in one it stands in the scanner's reach at the pulse, in the other out of it.
        // Then it falls in both, long before the ten seconds are up.
        (WiredRun Run, Machine Second) Fallen(bool inReach)
        {
            var world = Waiting("geo1");
            var (run, hero) = (world.Wired!, OutInTheOpen(world));
            var first = Unarmed(world, Afield(world, hero, inReach ? 40 : world.ScannerReach(hero) + 20));
            var second = Unarmed(world, Afield(world, hero, 60, 8));
            var pulsed = world.Time;
            Assert.Null(Pulse(world));
            Assert.Equal(inReach ? new[] { first.Id, second.Id } : [second.Id], run.Located.Keys);

            world.Damage(first, 1_000_000, Race.Red);
            world.Step();

            Assert.True(world.Time < pulsed + WiredBalance.LocateTime / 2);
            Assert.Equal(WiredBalance.LocateTime - 2 * GameWorld.Dt, world.LocatedFor(second.Id), 3);
            return (run, second);
        }

        var (located, second) = Fallen(inReach: true);
        var (never, _) = Fallen(inReach: false);

        // The second foe, still standing, stays located: the control.
        Assert.Equal([second.Id], located.Located.Keys);
        Assert.Equal(never.Fingerprint(), located.Fingerprint());
    }

    [Fact]
    public void A_hit_on_a_machine_not_located_draws_no_chance_more_than_without_a_geo_locator()
    {
        // Seven points of a hit, as a fifth more is 8.4: that .4 is a chance drawn (ten would come to twelve exactly).
        int Draw(string chassis, bool locateTarget)
        {
            var world = Waiting(chassis);
            var hero = OutInTheOpen(world);
            var other = Tough(world, Afield(world, hero, 40));
            var target = Tough(world, Afield(world, hero, 60, 8));
            if (chassis == "geo1")
            {
                world.Wired!.Located[other.Id] = world.Time + WiredBalance.LocateTime;
                if (locateTarget)
                    world.Wired.Located[target.Id] = world.Time + WiredBalance.LocateTime;
            }
            world.Damage(target, 7, Race.Red, hero, "BOLTER");
            return world.Random.Next();
        }

        var without = Draw("grunt1", locateTarget: false);

        Assert.Equal(without, Draw("geo1", locateTarget: false));
        // The control: on a located target the hit comes to no whole number, which draws, and the world's chance moves on.
        Assert.NotEqual(without, Draw("geo1", locateTarget: true));
    }

    // ---- the Phantom's shadow ---------------------------------------------------------------------

    [Fact]
    public void A_located_phantom_is_seen_in_a_battle_at_any_distance_until_its_location_runs_out()
    {
        var tier = BattleCatalogue.Tier("phantom6");
        var options = BattleBots.Compose(new BattleOptions { Tier = tier, TeamA = [Race.Red, Race.Blue], TeamB = [Race.Green, Race.Yellow] },
            [new BattleHero(Race.Red, "Ace", "phantom6", BattleBots.Loadout("phantom6", tier))]);
        var world = Battles.Begun(options);
        var (red, green) = (Battles.Hero(world, Race.Red), Battles.Hero(world, Race.Green));
        // Both clear of the capture circles, 30 m apart, the Phantom quiet all along.
        Battles.Away(world, Race.Green, Afield(world, world.BattlePod(1)!, 80));
        red.Position = Enumerable.Range(0, 64)
            .Select(k => green.Position + new Vector2(DetMath.Cos(k * MathF.Tau / 64), DetMath.Sin(k * MathF.Tau / 64)) * (30 + red.Radius))
            .First(p => world.Nav.IsFree(p, crossesWater: false)
                && Enumerable.Range(0, 2).All(team => Vector2.Distance(p, world.BattlePod(team)!.Position) > BattleBalance.CaptureRadius + 10));
        red.Path.Clear();
        world.Step();
        Assert.True(world.Shadowed(green, red));
        Assert.Equal("Hidden", world.SpecialView(Race.Red)!.Phase);

        // An enemy geo locator's pulse locates it.
        world.Wired!.Located[red.Id] = world.Time + WiredBalance.LocateTime;
        var until = world.Time + WiredBalance.LocateTime;

        Assert.False(world.Shadowed(green, red));
        Assert.Same(red, world.NearestEnemy(green, 100));
        var commands = new List<Command>();
        new BattleBot(world, Race.Green, commands.Add).Think();
        Assert.Contains(commands.OfType<AttackCommand>(), c => c.Target == red.Id);
        var view = world.SpecialView(Race.Red)!;
        Assert.Equal("Seen", view.Phase);
        Assert.Equal(WiredBalance.LocateTime, view.SecondsLeft, 3);
        Assert.InRange(view.Share, 0, 1);

        Battles.Run(world, 4);
        view = world.SpecialView(Race.Red)!;
        Assert.Equal("Seen", view.Phase);
        Assert.Equal(until - world.Time, view.SecondsLeft, 3);
        Assert.InRange(view.Share, 0, 1);
        Assert.False(world.Shadowed(green, red));

        Until(world, until);
        Assert.True(world.Shadowed(green, red));
        Assert.Null(world.NearestEnemy(green, 100));
        view = world.SpecialView(Race.Red)!;
        Assert.Equal(("Hidden", 0f, 1f), (view.Phase, view.SecondsLeft, view.Share));
    }

    // ---- the original's locating ------------------------------------------------------------------

    [Theory]
    [InlineData("geo1")]
    [InlineData("geo3")]
    public void A_geo_locator_hero_goes_looking_for_no_ore_and_a_match_s_geo_locator_still_does(string chassis)
    {
        var world = Waiting(chassis);
        var hero = OutInTheOpen(world);
        var order = hero.Order;

        Assert.Equal("not in Wired", Do(world, new LocateCommand(Race.Red, [hero.Id], hero.Position + new Vector2(80, 0))));
        Assert.Same(order, hero.Order);

        var match = NewMatch();
        var at = OpenGround(match);
        var locator = match.AddMachine(Race.Red, at, "GEO_LOCATOR", null, WiredCatalogue.FindChassis(chassis)!.Level, null);
        Assert.Null(Do(match, new LocateCommand(Race.Red, [locator.Id], at + new Vector2(80, 0))));
        Assert.IsType<LocateOrder>(locator.Order);
    }

    [Fact]
    public void A_geo_locator_hero_under_fire_stands_as_a_hero_where_a_crab_of_the_race_runs()
    {
        var world = NewRun("geo1");
        world.Races[Race.Red].Bmu = 100000;
        Assert.Null(Do(world, new WiredHangarCommand(Race.Red, true)));
        // Unarmed, so that it does not stand its ground for shooting back.
        foreach (var mounting in Enum.GetValues<Mounting>())
        {
            if (Red(world).Loadout.Weapon(mounting) is { } weapon)
                Assert.Null(Do(world, new WiredUnequipCommand(Race.Red, weapon.Id)));
        }
        Assert.Null(Do(world, new WiredHangarCommand(Race.Red, false)));
        var hero = Hero(world);
        hero.DrivenByPlayer = false;
        Assert.False(hero.CanFight);
        var crab = world.AddMachine(Race.Red, hero.Position + new Vector2(-40, 0), "GEO_LOCATOR", null, 1, null);
        var attackers = new[] { hero, crab }.Select(m => world.AddMachine(Race.Blue, m.Position + new Vector2(0, 30), "AGGRESSOR", "GRUNT", 1, "R_BOLTER")).ToList();
        world.Issue(new AttackCommand(Race.Blue, [attackers[0].Id], hero.Id));
        world.Issue(new AttackCommand(Race.Blue, [attackers[1].Id], crab.Id));
        var (heroEvaded, crabEvaded) = (false, false);

        for (var i = 0; i < 8 * GameWorld.TicksPerSecond; i++)
        {
            world.Step();
            heroEvaded |= hero.Order is EvadeOrder;
            crabEvaded |= crab.Order is EvadeOrder;
        }

        Assert.False(heroEvaded);
        Assert.True(crabEvaded, "the crab that is no hero did not run either, so this shows nothing");
    }

    // ---- the glider as a hero ---------------------------------------------------------------------

    [Theory]
    [InlineData("geo3", false)]
    [InlineData("geo1", true)]
    public void A_foe_s_electric_charge_never_fires_at_the_glider(string chassis, bool fired)
    {
        var world = Waiting(chassis);
        var hero = OutInTheOpen(world);
        var foe = world.AddMachine(Race.Blue, Afield(world, hero, 8), "AGGRESSOR", "GRUNT", 1, "R_ELECTRIC_CHARGE");
        Assert.True(foe.DistanceTo(hero) < Data.Parm.Weapon("ELECTRIC_CHARGE").Range);

        var events = Run(world, 4);

        Assert.Equal(fired, events.Any(e => e.Kind == GameEventKind.Fired && e.Actor == foe.Id));
    }

    [Theory]
    [InlineData("geo3", false)]
    [InlineData("geo1", true)]
    public void A_mine_never_goes_off_under_the_glider(string chassis, bool goesOff)
    {
        var world = Waiting(chassis);
        var hero = OutInTheOpen(world);
        world.LandMines.Add(new LandMineState(9001, Race.Blue, hero.Position + new Vector2(2, 0)));

        var events = Run(world, 0.5f);

        Assert.Equal(goesOff, events.Any(e => e.Kind == GameEventKind.MineExploded && e.Actor == hero.Id));
        Assert.Equal(goesOff, world.LandMines.Count == 0);
    }

    [Theory]
    [InlineData("geo3", true)]
    [InlineData("geo1", false)]
    public void The_glider_s_way_goes_out_over_the_water_and_the_crab_s_ends_on_the_shore(string chassis, bool overWater)
    {
        var world = Waiting(chassis, "m_dales");
        var (nav, hero) = (world.Nav, Hero(world));
        var water = Enumerable.Range(0, nav.Width * nav.Depth).Select(i => nav.Centre(i % nav.Width, i / nav.Width))
            .Where(p => !nav.IsFree(p, crossesWater: false) && nav.IsFree(p, crossesWater: true))
            .MinBy(p => Vector2.DistanceSquared(p, hero.Position));

        Assert.Null(Do(world, new MoveCommand(Race.Red, [hero.Id], water)));

        Assert.NotEmpty(hero.Path);
        Assert.Equal(overWater, !nav.IsFree(hero.Path[^1], crossesWater: false));
        Assert.Equal(overWater, Vector2.Distance(hero.Path[^1], water) < 1);
    }

    // ---- the same game ----------------------------------------------------------------------------

    [Fact]
    public void Two_runs_fed_the_same_commands_with_pulses_keep_the_same_checksums()
    {
        (List<ulong> Sums, int Pulses, int Located) Play()
        {
            var world = Waiting("geo3");
            var hero = Hero(world);
            for (var i = 0; i < 4; i++)
                world.AddMachine(Race.Blue, Afield(world, hero, 50 + 25 * i, 4 * i), "AGGRESSOR", "GRUNT", 1, "R_BOLTER");
            var (sums, pulses, located) = (new List<ulong>(), 0, 0);
            for (var i = 0; i < 20 * 45; i++)
            {
                if (i % (20 * 10) == 3)
                    world.Issue(new WiredSpecialCommand(Race.Red));
                world.Step();
                foreach (var e in world.Events.Where(e => e.Kind == GameEventKind.WiredLocated))
                    (pulses, located) = (pulses + 1, located + e.Other!.Value);
                if (i % 20 == 0)
                    sums.Add(world.Checksum());
            }
            return (sums, pulses, located);
        }

        var (first, second) = (Play(), Play());

        Assert.True(first.Pulses >= 2, $"{first.Pulses} pulses");
        Assert.True(first.Located > 0, "nothing was located");
        Assert.Equal((first.Pulses, first.Located), (second.Pulses, second.Located));
        Assert.Equal(first.Sums, second.Sums);
    }

    [Fact]
    public void The_located_and_the_pulse_s_wait_count_in_the_fingerprint_only_while_set()
    {
        var world = Waiting("geo1");
        var (run, state) = (world.Wired!, Red(world));
        var (fingerprint, checksum) = (run.Fingerprint(), world.Checksum());

        run.Located[9001] = 42;
        Assert.NotEqual(fingerprint, run.Fingerprint());
        Assert.NotEqual(checksum, world.Checksum());
        run.Located.Clear();
        Assert.Equal(fingerprint, run.Fingerprint());

        state.LocateReadyAt = 42;
        Assert.NotEqual(fingerprint, run.Fingerprint());
        state.LocateReadyAt = float.NegativeInfinity;
        Assert.Equal(fingerprint, run.Fingerprint());
        Assert.Equal(checksum, world.Checksum());
    }

    [Theory]
    [InlineData("geo1")]
    [InlineData("geo3")]
    public void The_hud_reads_the_wait_for_the_pulse_and_the_enemies_located(string chassis)
    {
        var world = Waiting(chassis);
        var hero = OutInTheOpen(world);
        var foes = Enumerable.Range(0, 3).Select(i => Unarmed(world, Afield(world, hero, 40 + 20 * i, 8 * i))).ToList();

        var view = world.SpecialView(Race.Red)!;
        Assert.Equal((ChassisSpecial.Locate, "Locate", (string?)null, 0f, 1f, 0, 0, true, (string?)null),
            (view.Special, view.Name, view.Phase, view.SecondsLeft, view.Share, view.Count, view.Most, view.Ready, view.Why));

        Assert.Null(Pulse(world));
        view = world.SpecialView(Race.Red)!;
        Assert.Equal((3, false, "that is not ready yet"), (view.Count, view.Ready, view.Why));
        Assert.Equal(WiredBalance.LocateCooldown - GameWorld.Dt, view.SecondsLeft, 3);
        Assert.InRange(view.Share, 0, 0.01f);

        // One falls: the count is of those standing.
        world.Damage(foes[0], 1_000_000, Race.Red);
        Assert.Equal(2, world.SpecialView(Race.Red)!.Count);
        Run(world, WiredBalance.LocateCooldown);
        view = world.SpecialView(Race.Red)!;
        Assert.Equal((0, true, 0f, 1f), (view.Count, view.Ready, view.SecondsLeft, view.Share));
    }

    [Fact]
    public void Asking_about_the_pulse_and_the_located_changes_neither_the_world_nor_its_chance()
    {
        GameWorld Set()
        {
            var world = Waiting("geo1");
            var hero = OutInTheOpen(world);
            var foes = Enumerable.Range(0, 4).Select(i => Unarmed(world, Afield(world, hero, 40 + 20 * i, 8 * i))).ToList();
            Assert.Null(Pulse(world));
            // Two whose time is up and not yet forgotten: that is the next tick's work.
            world.Wired!.Located[foes[2].Id] = world.Time - 1;
            world.Wired.Located[foes[3].Id] = world.Time;
            return world;
        }
        var (asked, left) = (Set(), Set());
        var before = asked.Checksum();
        Assert.Equal(before, left.Checksum());

        foreach (var race in Enum.GetValues<Race>())
        {
            asked.SpecialView(race);
            if (asked.Wired!.Heroes.GetValueOrDefault(race) is { } state)
            {
                asked.WhyNoLocate(state);
                asked.WhyNoSpecial(state);
            }
        }
        var actors = asked.Machines.ToList();
        foreach (var actor in actors)
        {
            asked.LocatedFor(actor.Id);
            foreach (var other in actors)
                asked.Shadowed(actor, other);
        }
        asked.LocatedFor(0);

        Assert.Equal(before, asked.Checksum());
        Assert.Equal(4, asked.Wired!.Located.Count);
        Assert.Equal(left.Random.Next(), asked.Random.Next());
    }

    // ---- battle -----------------------------------------------------------------------------------

    [Fact]
    public void In_a_battle_the_crab_is_of_the_first_tier_the_glider_of_the_second_and_bots_take_them_beside_the_grunts()
    {
        Assert.Equal((1, 2), (BattleCatalogue.Tier("geo1"), BattleCatalogue.Tier("geo3")));
        Assert.Equal(["grunt1", "geo1"], BattleCatalogue.OfTier(1).Select(c => c.Id));
        Assert.Equal(["grunt2", "geo3"], BattleCatalogue.OfTier(2).Select(c => c.Id));
        // On every map the two bots of a team at tiers I and II are a grunt and a locator, which one first by the map.
        foreach (var map in BattleCatalogue.Maps)
        {
            Assert.Equal(["geo1", "grunt1"], new[] { BattleBots.Pick(map.Scenario, 1, 0), BattleBots.Pick(map.Scenario, 1, 1) }.Order());
            Assert.Equal(["geo3", "grunt2"], new[] { BattleBots.Pick(map.Scenario, 2, 0), BattleBots.Pick(map.Scenario, 2, 1) }.Order());
        }

        foreach (var (chassis, tier) in new[] { ("geo1", 1), ("geo3", 2) })
        {
            var items = BattleBots.Loadout(chassis, tier);
            Assert.Null(BattleBots.WhyNot(new BattleHero(Race.Red, "Ace", chassis, items), tier));
            var weapon = Assert.Single(items, i => i.Slot is { Kind: WiredSlotKind.Weapon });
            Assert.Equal(WiredSlot.Weapon(Mounting.Top), weapon.Slot);
        }
        // The first tier wears the start kit: the Bolter on the crab's top and nothing else.
        Assert.Equal([new BattleItem("BOLTER", WiredSlot.Weapon(Mounting.Top))], BattleBots.Loadout("geo1", 1));
    }
}
