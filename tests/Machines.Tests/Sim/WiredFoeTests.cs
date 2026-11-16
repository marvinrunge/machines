using System.Numerics;
using System.Reflection;
using Machines.Game;
using Machines.Import.Model;
using Machines.Sim;
using Machines.Sim.Combat;
using Machines.Sim.Survival;
using Machines.Sim.World;

namespace Machines.Tests.Sim;

/// <summary>
/// The waves from the first of Wired's own machines on, and how the machines of the waves and of the squad use their
/// bodies without a driver: the dearest tiers, the Colossi of a siege, the second list of bosses and the builder's towers,
/// Old Iron's ram and the Colossus' anchor, and the escorts and veterans the hangar sells.
/// </summary>
public class WiredFoeTests
{
    private static readonly Lazy<GameData> DataLoader = new(() => GameData.Load(RepoPaths.OriginalDataFolder()));

    private static GameData Data => DataLoader.Value;

    private static readonly ResearchKey Colossus = WiredDirector.ColossusBesieger;

    private static readonly ResearchKey OldIron = new("AGGRESSOR", "GRUNT", 5, "L_PLASMA_CANNON2_R_HEAVY_BOLTER2_T_MULTI_LAUNCHER3");

    private static readonly ResearchKey Ninja = new("AGGRESSOR", "NINJA", 5, "L_MULTI_LAUNCHER7_T_GORILLA_PUNCH_R_MULTI_LAUNCHER7");

    private static GameWorld NewRun(int seed = 7) =>
        MatchSetup.Create(Data, "m_desert", "m_desert",
            new Dictionary<Race, Controller> { [Race.Red] = Controller.Player, [Race.Blue] = Controller.Ai }, seed,
            new MatchOptions { Wired = new WiredOptions() });

    /// <summary>A run in which no wave comes while a test lasts; the enemy is Blue.</summary>
    private static GameWorld Waiting()
    {
        var world = NewRun();
        world.Wired!.Countdown = 100_000;
        Assert.Equal(Race.Blue, world.Wired.Enemy);
        return world;
    }

    /// <summary>
    /// A run with the given wave just begun, of the given kind; with <paramref name="spawn"/> it arrives that far from the
    /// pod, and with <paramref name="chassis"/> the hero wears that chassis.
    /// </summary>
    private static GameWorld AtWave(int wave, WiredSpecial special = WiredSpecial.None, float spawn = 0, string? chassis = null)
    {
        var world = NewRun();
        if (chassis is not null && Red(world).Loadout.Chassis.Catalogue != chassis)
        {
            world.Races[Race.Red].Bmu = 100000;
            Assert.Null(Do(world, new WiredHangarCommand(Race.Red, true)));
            Assert.Null(Do(world, new WiredChassisCommand(Race.Red, Red(world).Add(WiredKind.Chassis, chassis, 0).Id)));
            Assert.Null(Do(world, new WiredHangarCommand(Race.Red, false)));
        }
        if (spawn > 0)
        {
            world.Wired!.SpawnPoints.Clear();
            world.Wired.SpawnPoints.Add(world.Nav.NearestFreePoint(Pod(world).Position + new Vector2(spawn, 0), crossesWater: false)!.Value);
        }
        world.Wired!.Wave = wave - 1;
        world.Wired.Countdown = 0;
        world.Wired.NextSpecial = special;
        world.Step();
        world.Step();
        Assert.Equal((WiredPhase.Wave, wave), (world.Wired.Phase, world.Wired.Wave));
        return world;
    }

    /// <summary>A wave with nobody on the map that will not end by itself: a last group is still to come, some day.</summary>
    private static GameWorld Quiet(int wave, string? chassis = null)
    {
        var world = AtWave(wave, chassis: chassis);
        var run = world.Wired!;
        foreach (var foe in run.Foes)
            Kill(world, foe.Machine);
        run.Groups.Clear();
        run.Groups.Add(new WiredGroup(100_000, [new WiredFoePlan(new ResearchKey("AGGRESSOR", "GRUNT", 1, "R_BOLTER"))]));
        world.Step();
        Assert.Empty(run.Foes);
        return world;
    }

    private static GameWorld InHangar()
    {
        var world = NewRun();
        world.Races[Race.Red].Bmu = 100000;
        Assert.Null(Do(world, new WiredHangarCommand(Race.Red, true)));
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

    /// <returns>All that happened in that time.</returns>
    private static List<GameEvent> Run(GameWorld world, float seconds)
    {
        var events = new List<GameEvent>();
        for (var i = 0; i < MathF.Round(seconds * GameWorld.TicksPerSecond); i++)
        {
            world.Step();
            events.AddRange(world.Events);
        }
        return events;
    }

    private static void Kill(GameWorld world, Actor actor) => world.Damage(actor, 1_000_000, Race.Red);

    /// <summary>Has the world do something it keeps to itself.</summary>
    private static object? Ask(GameWorld world, string method, params object?[] arguments) =>
        typeof(GameWorld).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(world, arguments);

    /// <summary>A place with open ground 70 m round it, well away from every building, and the way from the pod to it.</summary>
    private static (Vector2 At, Vector2 Way) OpenGround(GameWorld world)
    {
        var at = Enumerable.Range(0, 400)
            .Select(i => new Vector2(200 + i % 20 * 60, 200 + i / 20 * 60))
            .First(p => world.Constructions.All(c => Vector2.Distance(p, c.Position) > 200)
                && world.Nav.IsAreaFree((p - new Vector2(70, 70), p + new Vector2(70, 70))));
        return (at, Vector2.Normalize(at - Pod(world).Position));
    }

    private static Machine Add(GameWorld world, Race race, ResearchKey key, Vector2 at, float facing = 0)
    {
        var machine = world.AddMachine(race, at, key.Type, key.SubType, key.HardwareLevel, key.WeaponCombo);
        machine.Facing = facing;
        return machine;
    }

    /// <summary>Makes a machine of the hero's race one of its squad, as buying it would.</summary>
    private static Machine Enrolled(GameWorld world, Machine machine)
    {
        machine.Squad = WiredBalance.EscortSquad;
        Red(world).EscortIds.Add(machine.Id);
        return machine;
    }

    /// <summary>Sets the hero down at a place, at the wheel with no input: it neither moves nor shoots.</summary>
    private static Machine HeroAt(GameWorld world, Vector2 at)
    {
        var hero = Hero(world);
        Ask(world, "StartAfresh", hero);
        hero.Doorway = null;
        hero.Position = at;
        hero.DrivenByPlayer = true;
        return hero;
    }

    private static float Angle(Vector2 way) => MathF.Atan2(way.Y, way.X);

    private static int Cost(WiredTier tier) => Data.Parm.Machine(tier.TypeName).Level(tier.Level).Cost;

    private static WiredTier Tier(string subType, int level) => WiredCatalogue.Tiers.First(t => t.SubType == subType && t.Level == level);

    private static ResearchKey Key(WiredTier tier) => new(tier.Type, tier.SubType, tier.Level, tier.Combos[0]);

    // ---- what a wave buys ---------------------------------------------------------------------------

    [Fact]
    public void Up_to_the_first_wave_of_wireds_own_machines_a_wave_buys_what_it_always_did()
    {
        Assert.Equal(38, WiredDirector.DearestFrom);
        for (var wave = 1; wave < WiredDirector.DearestFrom; wave++)
        {
            var budget = WiredBalance.Budget(wave, WiredDifficulty.Normal);
            for (var seed = 0; seed < 20; seed++)
            {
                var bought = WiredDirector.Buy(Data, wave, budget, new Random(seed)).Select(p => (p.Key, p.Elite)).ToList();
                Assert.Equal(ByListOrder(wave, budget, new Random(seed)), bought);
            }
        }
    }

    /// <summary>A wave's purchase as the newest tiers in the list's order make it, the four the budget still covers.</summary>
    private static List<(ResearchKey Key, bool Elite)> ByListOrder(int wave, float budget, Random random)
    {
        var open = WiredCatalogue.Tiers.Where(t => t.Wave <= wave).ToList();
        var bought = new List<(ResearchKey, bool)>();
        (ResearchKey, bool) Foe(WiredTier tier) =>
            (new ResearchKey(tier.Type, tier.SubType, tier.Level, tier.Combos[random.Next(tier.Combos.Count)]),
                wave >= WiredBalance.EliteFrom && random.Next(100) < WiredBalance.ElitePercent);
        while (bought.Count < WiredBalance.MostInWave)
        {
            var left = budget;
            var within = open.Where(t => Cost(t) <= left).TakeLast(WiredBalance.TierSpread).ToList();
            if (within.Count == 0)
                break;
            var tier = within[random.Next(within.Count)];
            budget -= Cost(tier);
            bought.Add(Foe(tier));
        }
        if (bought.Count == 0)
            bought.Add(Foe(open[0]));
        return bought;
    }

    [Fact]
    public void From_then_on_a_wave_buys_from_the_dearest_tiers_it_can_pay()
    {
        foreach (var wave in new[] { 38, 42, 46, 50, 60 })
        {
            var dearest = WiredCatalogue.Tiers.Where(t => t.Wave <= wave).OrderByDescending(Cost).Take(WiredBalance.TierSpread)
                .Select(t => (t.TypeName, t.Level)).ToList();
            var bought = WiredDirector.Buy(Data, wave, 1e9f, new Random(wave));

            Assert.Equal(WiredBalance.MostInWave, bought.Count);
            Assert.All(bought, p => Assert.Contains((p.Key.TypeName, p.Key.HardwareLevel), dearest));
        }
        // Old Iron and the Harvester are the dearest of wave 46 but for the Colossus to come: they come at once.
        Assert.Contains(WiredDirector.Buy(Data, 46, 1e9f, new Random(1)), p => (p.Key.SubType, p.Key.HardwareLevel) == ("GRUNT", 5));
    }

    [Fact]
    public void From_wave_36_on_a_wave_is_never_weaker_than_one_before_it()
    {
        var best = 0.0;
        for (var wave = 36; wave <= 60; wave++)
        {
            var budget = WiredBalance.Budget(wave, WiredDifficulty.Normal);
            var mean = Enumerable.Range(0, 100).Average(seed => WiredDirector.Buy(Data, wave, budget, new Random(seed)).Sum(Durability));

            Assert.True(mean >= best * 0.95, $"wave {wave}: {mean:F0} after {best:F0}");
            best = Math.Max(best, mean);
        }
    }

    /// <summary>The hit points and armour a machine of the waves comes with.</summary>
    private static double Durability(WiredFoePlan plan)
    {
        var level = Data.Parm.Machine(plan.Key.TypeName).Level(plan.Key.HardwareLevel);
        return Math.Max(1, (int)(level.HitPoints * plan.HitPoints)) + level.Armour;
    }

    // ---- sieges -------------------------------------------------------------------------------------

    [Fact]
    public void A_siege_from_wave_fifty_brings_colossi_that_anchor_where_the_pod_comes_within_their_reach()
    {
        var world = AtWave(WiredBalance.ColossusSiegeFrom, WiredSpecial.Siege, spawn: 300);
        var run = world.Wired!;
        var pod = Pod(world);
        var besiegers = run.Foes.Where(f => f.Plan.Role == WiredRole.Besieger).Select(f => f.Machine).ToList();
        Assert.NotEmpty(besiegers);
        Assert.All(besiegers, b => Assert.Equal(("BALLISTA", 5), (b.Spec.SubType, b.Stats.HardwareLevel)));
        var reach = 120 * WiredBalance.AnchorRange * 0.95f;
        var durability = pod.HitPoints + pod.Armour;

        // The first comes alone, so that no other stands in its way.
        var besieger = besiegers[0];
        for (var i = 0; i < 120 * GameWorld.TicksPerSecond && world.AnchorPhaseOf(besieger) == AnchorPhase.Free; i++)
        {
            foreach (var foe in run.Foes.Where(f => f.Machine != besieger).ToList())
                Kill(world, foe.Machine);
            world.Step();
        }

        Assert.Equal(AnchorPhase.Locking, world.AnchorPhaseOf(besieger));
        // Further off than its missiles reach as made.
        Assert.InRange(besieger.DistanceTo(pod), 120 * 0.95f, reach);
        Assert.Equal(durability, pod.HitPoints + pod.Armour);
        Run(world, 12);
        Assert.Equal(AnchorPhase.Anchored, world.AnchorPhaseOf(besieger));
        Assert.True(pod.HitPoints + pod.Armour < durability);
        Assert.Equal(pod.Id, Assert.IsType<AttackOrder>(besieger.Order).Target);
    }

    [Fact]
    public void A_besieger_anchors_for_the_pod_alone()
    {
        var world = Waiting();
        var (at, way) = OpenGround(world);
        var pod = Pod(world);
        var besieger = Add(world, Race.Blue, Colossus, at + way * 60, Angle(-way));
        world.Wired!.Foes.Add(new WiredFoe(besieger, new WiredFoePlan(Colossus) { Role = WiredRole.Besieger }, false, world.Time));
        besieger.Order = new AttackOrder(pod.Id);
        var escort = Enrolled(world, Add(world, Race.Red, Ninja, at - way * 60));
        escort.Order = new StandGroundOrder();

        var events = Run(world, 3);

        Assert.Equal(AnchorPhase.Free, world.AnchorPhaseOf(besieger));
        Assert.DoesNotContain(events, e => e.Kind == GameEventKind.WiredAnchoring);
    }

    // ---- the Colossus of the waves and of the squad -------------------------------------------------

    [Fact]
    public void A_colossus_of_the_waves_anchors_at_what_it_reaches_anchored_and_lets_go_six_seconds_after_the_last()
    {
        var world = Waiting();
        var (at, way) = OpenGround(world);
        var colossus = Add(world, Race.Blue, Colossus, at + way * 60, Angle(-way));
        // Beyond the reach of its missiles as made, within it anchored.
        var target = Enrolled(world, Add(world, Race.Red, Ninja, at - way * 60));
        target.Order = new StandGroundOrder();
        Assert.InRange(colossus.DistanceTo(target), 120 * 0.95f, 120 * WiredBalance.AnchorRange * 0.95f);
        var place = colossus.Position;

        world.Step();
        Assert.Equal(AnchorPhase.Locking, world.AnchorPhaseOf(colossus));
        Assert.Equal(target.Id, Assert.IsType<AttackOrder>(colossus.Order).Target);

        Run(world, WiredBalance.AnchorTime);
        Assert.Equal(AnchorPhase.Anchored, world.AnchorPhaseOf(colossus));
        Assert.All(colossus.Weapons, w => Assert.Equal(w.Base.Range * WiredBalance.AnchorRange, w.Spec.Range));
        var durability = target.HitPoints + target.Armour;
        Run(world, 8);
        Assert.True(target.HitPoints + target.Armour < durability);
        Assert.Equal(place, colossus.Position);

        Kill(world, target);
        Run(world, WiredBalance.AnchorIdle - 0.5f);
        Assert.Equal(AnchorPhase.Anchored, world.AnchorPhaseOf(colossus));
        Run(world, 1);
        Assert.Equal(AnchorPhase.Releasing, world.AnchorPhaseOf(colossus));
        Assert.Equal(place, colossus.Position);
        Run(world, WiredBalance.AnchorTime);
        Assert.Equal(AnchorPhase.Free, world.AnchorPhaseOf(colossus));
        // Free, it goes on to where it was bound: the pod.
        Run(world, 4);
        Assert.NotEqual(place, colossus.Position);
    }

    [Fact]
    public void A_colossus_of_the_waves_set_down_by_the_pod_stands_free_there()
    {
        var world = Quiet(WiredBalance.ColossusSiegeFrom + 1);
        var (at, way) = OpenGround(world);
        var colossus = Add(world, Race.Blue, Colossus, at + way * 60, Angle(-way));
        var run = world.Wired!;
        var foe = new WiredFoe(colossus, new WiredFoePlan(Colossus), false, world.Time);
        run.Foes.Add(foe);
        Enrolled(world, Add(world, Race.Red, Ninja, at - way * 60)).Order = new StandGroundOrder();
        Run(world, WiredBalance.AnchorTime + 0.5f);
        Assert.Equal(AnchorPhase.Anchored, world.AnchorPhaseOf(colossus));

        Ask(world, "Fetch", run, foe, Pod(world));

        Assert.Equal(AnchorPhase.Free, world.AnchorPhaseOf(colossus));
        Assert.All(colossus.Weapons, w => Assert.Same(w.Base, w.Spec));
    }

    [Fact]
    public void A_colossus_of_the_squad_anchors_on_hold_and_its_next_order_waits_for_the_tracks()
    {
        var world = Waiting();
        var (at, way) = OpenGround(world);
        var escort = Enrolled(world, Add(world, Race.Red, Colossus, at));

        Assert.Null(Do(world, new StandGroundCommand(Race.Red, [escort.Id])));
        Assert.Equal(AnchorPhase.Locking, world.AnchorPhaseOf(escort));
        // Not yet locked, the tracks are free at once.
        var events = new List<GameEvent>();
        Assert.Null(Do(world, new MoveCommand(Race.Red, [escort.Id], at + way * 40)));
        events.AddRange(world.Events);
        Assert.Equal(AnchorPhase.Free, world.AnchorPhaseOf(escort));
        Assert.Contains(events, e => e.Kind == GameEventKind.WiredReleased && e.Actor == escort.Id);

        Assert.Null(Do(world, new StandGroundCommand(Race.Red, [escort.Id])));
        var place = escort.Position;
        Run(world, WiredBalance.AnchorTime + 0.1f);
        Assert.Equal(AnchorPhase.Anchored, world.AnchorPhaseOf(escort));
        Assert.All(escort.Weapons, w => Assert.Equal(w.Base.Range * WiredBalance.AnchorRange, w.Spec.Range));

        Assert.Null(Do(world, new MoveCommand(Race.Red, [escort.Id], at - way * 40)));
        Assert.Equal(AnchorPhase.Releasing, world.AnchorPhaseOf(escort));
        Assert.IsType<MoveOrder>(escort.Order);
        Run(world, WiredBalance.AnchorTime - 0.2f);
        Assert.Equal(place, escort.Position);
        // Free, it turns on the spot as slowly as a Colossus does, and goes.
        Run(world, 15);
        Assert.Equal(AnchorPhase.Free, world.AnchorPhaseOf(escort));
        Assert.True(Vector2.Distance(escort.Position, at - way * 40) < Vector2.Distance(place, at - way * 40) - 10);
    }

    [Fact]
    public void An_anchored_colossus_turns_to_an_enemy_it_has_a_clear_shot_at_and_not_to_a_nearer_one_behind_cover()
    {
        var world = Waiting();
        var (at, way) = OpenGround(world);
        var escort = Enrolled(world, Add(world, Race.Red, Colossus, at, Angle(-way)));
        // Behind it and nearer, an enemy hidden by a machine of its own; before it and further off, one in the open.
        var hidden = world.AddMachine(Race.Blue, at + way * 40, "AGGRESSOR", "BALLISTA", 5, null);
        world.AddMachine(Race.Red, at + way * 20, "AGGRESSOR", "BALLISTA", 5, null);
        var open = world.AddMachine(Race.Blue, at - way * 60, "AGGRESSOR", "BALLISTA", 5, null);
        Assert.False(world.ClearShot(escort, hidden));
        Assert.True(world.ClearShot(escort, open));

        Assert.Null(Do(world, new StandGroundCommand(Race.Red, [escort.Id])));
        Run(world, WiredBalance.AnchorTime + 1);
        Assert.Equal(AnchorPhase.Anchored, world.AnchorPhaseOf(escort));
        var events = Run(world, 5);

        // It keeps its face to the one in the open and fires on.
        Vector2 Ahead() => new(MathF.Cos(escort.Facing), MathF.Sin(escort.Facing));
        Assert.True(Vector2.Dot(Ahead(), -way) > 0.95f, $"it faces {escort.Facing}");
        Assert.Contains(events, e => e.Kind == GameEventKind.Fired && e.Actor == escort.Id);
        Assert.False(open.IsDead);
        // With none left in the open, it turns to the one behind cover.
        Kill(world, open);
        Run(world, 10);
        Assert.True(Vector2.Dot(Ahead(), way) > 0.95f, $"it faces {escort.Facing}");
    }

    // ---- Old Iron, the Widow and the Hornet of the waves --------------------------------------------

    [Fact]
    public void An_old_iron_of_the_waves_rams_the_hero_it_attacks_close_in_front()
    {
        var world = Waiting();
        var (at, way) = OpenGround(world);
        var hero = HeroAt(world, at);
        var ram = Add(world, Race.Blue, OldIron, at + way * 12, Angle(-way));
        ram.Order = new AttackOrder(hero.Id);
        var durability = hero.HitPoints + hero.Armour;

        var events = new List<GameEvent>();
        for (var i = 0; i < GameWorld.TicksPerSecond && !events.Exists(e => e.Kind == GameEventKind.WiredDashed); i++)
        {
            world.Step();
            events.AddRange(world.Events);
        }
        Assert.Contains(events, e => e.Kind == GameEventKind.WiredDashed && e.Actor == ram.Id);
        // The dash breaks off nothing: it attacks on.
        Assert.Equal(hero.Id, Assert.IsType<AttackOrder>(ram.Order).Target);

        events.AddRange(Run(world, 0.5f));
        Assert.Contains(events, e => e.Kind == GameEventKind.WiredRammed && e.Actor == hero.Id && e.Other == ram.Id);
        Assert.True(hero.HitPoints + hero.Armour < durability || hero.IsDead);
    }

    [Theory]
    [InlineData(12f, false)]
    [InlineData(WiredBalance.RamReach + 4, true)]
    public void An_old_iron_rams_nothing_behind_it_or_further_off(float metres, bool facing)
    {
        var world = Waiting();
        var (at, way) = OpenGround(world);
        var hero = HeroAt(world, at);
        var ram = Add(world, Race.Blue, OldIron, at + way * metres, Angle(facing ? -way : way));
        ram.Order = new AttackOrder(hero.Id);

        var events = Run(world, 2);

        Assert.DoesNotContain(events, e => e.Kind is GameEventKind.WiredDashed or GameEventKind.WiredRammed);
    }

    [Fact]
    public void A_widow_of_the_waves_lurks_where_it_stands_and_a_hornet_of_theirs_never_dashes()
    {
        var world = Waiting();
        var (at, way) = OpenGround(world);
        var hero = HeroAt(world, at);
        var widow = Add(world, Race.Blue, Key(Tier("ASSASSIN", 6)), at + way * 60, Angle(-way));
        widow.Order = new StandGroundOrder();
        var hornet = Add(world, Race.Blue, Key(Tier("BOSS", 3)), at - way * 10, Angle(way));
        hornet.Order = new AttackOrder(hero.Id);

        var events = Run(world, WiredBalance.LurkStill + 0.2f);

        Assert.All(widow.Weapons.Where(w => w.DealsDamage), w => Assert.Equal(w.Base.Range * WiredBalance.LurkRange, w.Spec.Range));
        events.AddRange(Run(world, 3));
        Assert.DoesNotContain(events, e => e.Kind == GameEventKind.WiredDashed);
    }

    // ---- escorts and veterans -----------------------------------------------------------------------

    [Fact]
    public void Wireds_own_machines_are_sold_as_escorts_from_wave_twenty()
    {
        var own = WiredCatalogue.Tiers.Where(t => WiredMachines.IsWired(t.TypeName, t.Level)).ToList();
        Assert.Equal(7, own.Count);
        Assert.All(own, t => Assert.Equal(WiredBalance.OwnEscortsFrom, t.EscortFrom));
        Assert.All(WiredCatalogue.Tiers.Except(own), t => Assert.Equal(t.Wave, t.EscortFrom));

        var world = InHangar();
        var state = Red(world);
        var shop = new HangarShop(world, state);
        world.Wired!.Wave = WiredBalance.OwnEscortsFrom - 2;
        Assert.Equal(WiredBalance.OwnEscortsFrom - 1, world.Wired.ShopWave);
        Assert.DoesNotContain(shop.Escorts(), k => WiredMachines.IsWired(k.TypeName, k.HardwareLevel));
        Assert.All(own, t => Assert.Equal("that is not on offer yet", world.WhyNoEscort(state, Key(t))));

        world.Wired.Wave++;
        var offered = shop.Escorts().ToList();
        Assert.All(own, t => Assert.Contains(Key(t), offered));
        Assert.All(own, t => Assert.Null(world.WhyNoEscort(state, Key(t))));
    }

    [Fact]
    public void A_veteran_comes_only_as_the_hangar_sells_it_and_never_as_a_cheaper_machine()
    {
        var world = InHangar();
        var state = Red(world);
        var chip = state.Add(WiredCatalogue.KindOf("veterans")!.Value, "veterans", 0);
        state.Loadout = state.Loadout.With(WiredSlot.Chip(0), chip);
        var grunt3 = Key(Tier("GRUNT", 3));

        Assert.Equal(3, WiredChips.Veteran(Data, state, grunt3, WiredBalance.OwnEscortsFrom - 1).HardwareLevel);
        Assert.Equal(4, WiredChips.Veteran(Data, state, grunt3, WiredBalance.OwnEscortsFrom).HardwareLevel);
        // A Bee would come as a Hornet, which costs less: it stays a Bee. A Wasp comes as a Bee.
        var bee = Key(Tier("BOSS", 2));
        Assert.Equal(bee, WiredChips.Veteran(Data, state, bee, 60));
        Assert.Equal(2, WiredChips.Veteran(Data, state, Key(Tier("BOSS", 1)), 60).HardwareLevel);

        // What the hangar says the escort comes as is what comes, before the wave and at it.
        world.Wired!.Wave = WiredBalance.OwnEscortsFrom - 2;
        Assert.Equal(grunt3, world.EscortAs(state, grunt3));
        world.Wired.Wave++;
        var said = world.EscortAs(state, grunt3);
        Assert.Equal(4, said.HardwareLevel);
        Assert.Null(Do(world, new WiredSquadBuyCommand(Race.Red, grunt3)));
        var escort = Assert.Single(world.Escorts(Race.Red));
        Assert.Equal((said.SubType, said.HardwareLevel, said.WeaponCombo), (escort.Spec.SubType, escort.Stats.HardwareLevel, escort.WeaponCombo));
    }

    // ---- bosses -------------------------------------------------------------------------------------

    [Theory]
    [InlineData(35, "The Reaper", "ASSASSIN", 5, 2, false)]
    [InlineData(40, "Old Iron", "GRUNT", 5, 6, false)]
    [InlineData(45, "The Wall", "BALLISTA", 5, 0, false)]
    [InlineData(50, "The Queen", "BOSS", 2, 6, false)]
    [InlineData(55, "The Marshal", "COMMANDER", 6, 4, true)]
    [InlineData(60, "The Builder", "BEHEMOTH", 6, 0, false)]
    public void The_second_lap_brings_wireds_own_bosses_as_strong_as_the_lap(int wave, string name, string subType, int level, int guards, bool heal)
    {
        var world = AtWave(wave);
        var run = world.Wired!;

        var boss = Assert.Single(run.Bosses);
        Assert.Equal(name, boss.Plan.Boss!.Name);
        Assert.Equal((subType, level), (boss.Machine.Spec.SubType, boss.Machine.Stats.HardwareLevel));
        Assert.Equal(boss.Plan.Boss.HitPoints * WiredDirector.LapStrength(wave), boss.Plan.HitPoints);
        var made = Data.Parm.Machine(boss.Plan.Key.TypeName).Level(level);
        Assert.Equal((int)(made.HitPoints * boss.Plan.HitPoints), boss.Machine.MaxHitPoints);
        Assert.True(run.Foes.Count(f => f.Plan.Key == boss.Plan.Boss.Guard) >= guards);
        Assert.Equal(heal ? guards : 0, run.Foes.Count(f => f.Plan.Healer));
        // A boss, its guards and the rest of the wave fight with single weapons.
        Assert.All(run.Foes.SelectMany(f => f.Machine.Weapons), w => Assert.Equal(0, w.Copy));
        Assert.True(boss.Machine.CanFight);
    }

    [Fact]
    public void Nothing_the_waves_bring_is_a_geo_locator_and_every_one_of_wireds_own_can_be_drawn()
    {
        var keys = WiredCatalogue.Tiers.SelectMany(t => t.Combos.Select(c => new ResearchKey(t.Type, t.SubType, t.Level, c)))
            .Concat(WiredDirector.Bosses.Concat(WiredDirector.SecondLap).SelectMany(b => new[] { b.Machine, b.Guard }).OfType<ResearchKey>())
            .Concat([WiredDirector.Spy, WiredDirector.BetterSpy, WiredDirector.Turncoat, WiredDirector.Besieger, WiredDirector.BigBesieger, WiredDirector.ColossusBesieger])
            .ToList();

        Assert.DoesNotContain(keys, k => k.Type == "GEO_LOCATOR");
        foreach (var key in keys.Where(k => WiredMachines.IsWired(k.TypeName, k.HardwareLevel)))
        {
            var chassis = WiredCatalogue.FindChassis(HeroDonors.WiredChassis(key.Type, key.SubType, key.HardwareLevel) ?? "");
            Assert.True(chassis is not null, key.ToString());
            var weapons = WeaponCombos.Split(key.WeaponCombo!);
            Assert.NotEmpty(weapons);
            foreach (var (mounting, weapon) in weapons)
            {
                Assert.True(HeroDonors.For(weapon, mounting) is not null, $"{key}: {weapon}");
                Assert.True(WiredCatalogue.FindWeapon(weapon) is { } item && WiredCatalogue.WhyNotFit(chassis, mounting, item) is null, $"{key}: {weapon} on {mounting}");
            }
        }
        // The Titan has its one slot on top.
        Assert.Equal([Mounting.Top], WeaponCombos.Split(WiredDirector.SecondLap.Single(b => b.Towers is not null).Machine.WeaponCombo!).Select(w => w.Mounting));
    }

    [Theory]
    [InlineData("grunt1", "sentry3", 3)]
    [InlineData("boss1", "sentry4", 4)]
    public void The_builder_raises_towers_that_can_hit_the_hero_by_the_pod_one_after_the_other_and_they_fall_with_the_wave(string chassis, string sentry, int level)
    {
        var world = Quiet(60, chassis);
        var run = world.Wired!;
        var pod = Pod(world);
        // The hero is out, far from the pod: on the ground it gets the sentry for the ground, flying the one for the air.
        var hero = HeroAt(world, OpenGround(world).At);
        Assert.Equal(chassis == "boss1", hero.Stats.Locomotion == Locomotion.Glider);
        var door = pod.EntranceOutside ?? pod.Position;
        var builder = WiredDirector.BossOf(60)!;
        Assert.Equal("The Builder", builder.Name);
        var titan = Add(world, Race.Blue, builder.Machine,
            world.Nav.NearestFreePoint(pod.Position + Vector2.Normalize(pod.Position - door) * (pod.Radius + 30), crossesWater: false)!.Value);
        run.Foes.Add(new WiredFoe(titan, new WiredFoePlan(builder.Machine) { Boss = builder, HitPoints = 100 }, false, world.Time));
        titan.Order = new AttackOrder(pod.Id);

        var events = new List<GameEvent>();
        for (var i = 0; i < 60 * GameWorld.TicksPerSecond && world.TowersOf(Race.Blue).Count(t => t.Complete) < WiredBalance.BossTowers; i++)
        {
            world.Step();
            events.AddRange(world.Events);
            Assert.True(world.TowersOf(Race.Blue).Count(t => !t.Complete) <= 1);
        }

        var towers = world.TowersOf(Race.Blue);
        Assert.Equal(WiredBalance.BossTowers, towers.Count);
        Assert.All(towers, t => Assert.True(t.Complete));
        Assert.Equal(["turret3", sentry, "turret3", sentry], events.Where(e => e.Kind == GameEventKind.WiredTowerPlaced).Select(e => e.Detail));
        Assert.Equal([("TURRET", 3), ("SENTRY", level), ("TURRET", 3), ("SENTRY", level)], towers.Select(t => (t.Spec.SubType, t.Stats.HardwareLevel)));
        Assert.All(towers, t => Assert.True(world.CanFireAt(t, hero), $"{t.Spec.SubType} {t.Stats.HardwareLevel} cannot fire at the hero"));
        Assert.All(towers, t => Assert.True(t.DistanceFrom(door) >= WiredBalance.PodKeepOut));
        Assert.Equal(WiredBalance.BossTowers, Run(world, 5).Count(e => e.Kind == GameEventKind.WiredTowerPlaced) + WiredBalance.BossTowers);

        // One fallen, it raises another.
        Kill(world, towers[0]);
        Run(world, 1);
        Assert.Equal(WiredBalance.BossTowers, world.TowersOf(Race.Blue).Count);

        // The wave over, its towers are gone with it.
        run.Groups.Clear();
        Kill(world, titan);
        world.Step();
        Assert.Equal(WiredPhase.Intermission, run.Phase);
        world.Step();
        Assert.Empty(world.TowersOf(Race.Blue));
        Assert.DoesNotContain(world.Constructions, c => c.Race == Race.Blue && c.IsType(WiredTower.Type));
    }
}
