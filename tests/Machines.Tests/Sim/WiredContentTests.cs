using System.Numerics;
using Machines.Import.Model;
using Machines.Sim;
using Machines.Sim.Combat;
using Machines.Sim.Survival;
using Machines.Sim.World;

namespace Machines.Tests.Sim;

/// <summary>What fills a Wired run: the hero's abilities and dash, the chips and their links, the special waves.</summary>
public class WiredContentTests
{
    private static readonly Lazy<GameData> DataLoader = new(() => GameData.Load(RepoPaths.OriginalDataFolder()));

    private static GameData Data => DataLoader.Value;

    private static readonly ResearchKey Ninja = new("AGGRESSOR", "NINJA", 5, "L_MULTI_LAUNCHER7_T_GORILLA_PUNCH_R_MULTI_LAUNCHER7");

    private static GameWorld NewRun(WiredOptions? options = null, int seed = 7) =>
        MatchSetup.Create(Data, "m_desert", "m_desert",
            new Dictionary<Race, Controller> { [Race.Red] = Controller.Player, [Race.Blue] = Controller.Ai }, seed,
            new MatchOptions { Wired = options ?? new WiredOptions() });

    /// <summary>A run with the given wave just begun, of the given kind; with <paramref name="spawn"/> it arrives that far from the pod and not at the far end of the map.</summary>
    private static GameWorld AtWave(int wave, WiredSpecial special = WiredSpecial.None, int seed = 7, float spawn = 0)
    {
        var world = NewRun(seed: seed);
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
    private static GameWorld Quiet(int wave = 1)
    {
        var world = AtWave(wave);
        var run = world.Wired!;
        foreach (var foe in run.Foes)
            Kill(world, foe.Machine);
        run.Groups.Clear();
        run.Groups.Add(new WiredGroup(100_000, [new WiredFoePlan(new ResearchKey("AGGRESSOR", "GRUNT", 1, "R_BOLTER"))]));
        world.Step();
        Assert.Empty(run.Foes);
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
        for (var i = 0; i < seconds / GameWorld.Dt; i++)
        {
            world.Step();
            events.AddRange(world.Events);
        }
        return events;
    }

    private static void Kill(GameWorld world, Machine machine, Race by = Race.Red) => world.Damage(machine, 1_000_000, by);

    private static Machine Grunt(GameWorld world, Vector2 at, Race race = Race.Blue) =>
        world.AddMachine(race, at, "AGGRESSOR", "GRUNT", 1, "R_BOLTER");

    /// <summary>A machine that takes a good many hits.</summary>
    private static Machine Tough(GameWorld world, Vector2 at, Race race = Race.Blue) =>
        world.AddMachine(race, at, Ninja.Type, Ninja.SubType, Ninja.HardwareLevel, Ninja.WeaponCombo);

    /// <summary>A machine that shoots at nobody.</summary>
    private static Machine Unarmed(GameWorld world, Vector2 at) =>
        world.AddMachine(Race.Blue, at, WiredDirector.Spy.Type, WiredDirector.Spy.SubType, WiredDirector.Spy.HardwareLevel, WiredDirector.Spy.WeaponCombo);

    private static Machine Escort(GameWorld world, Vector2 at) => Enrolled(world, Grunt(world, at, Race.Red));

    /// <summary>Makes a machine one of the squad, as buying it would.</summary>
    private static Machine Enrolled(GameWorld world, Machine machine)
    {
        machine.Squad = WiredBalance.EscortSquad;
        Red(world).EscortIds.Add(machine.Id);
        return machine;
    }

    /// <summary>A free point at a distance from the hero, on the side away from the pod.</summary>
    private static Vector2 Away(GameWorld world, float metres, float across = 0)
    {
        var way = Vector2.Normalize(Hero(world).Position - Pod(world).Position);
        var place = Hero(world).Position + way * metres + new Vector2(-way.Y, way.X) * across;
        return world.Nav.NearestFreePoint(place, crossesWater: false)!.Value;
    }

    /// <summary>Puts an item on the hero, past the hangar: what fits and pays is not the question here.</summary>
    private static WiredItem Wear(GameWorld world, string id, WiredSlot slot, int mk = 1)
    {
        var state = Red(world);
        var item = state.Add(WiredCatalogue.KindOf(id)!.Value, id, 0, mk);
        state.Loadout = state.Loadout.With(slot, item);
        return item;
    }

    private static void Chip(GameWorld world, string id, int socket = 0) => Wear(world, id, WiredSlot.Chip(socket));

    private static WiredAbilityCommand Ability(Vector2 aim, int slot = 0) => new(Race.Red, slot, aim);

    /// <returns>How much of its durability a hit takes off a target.</returns>
    private static int Drawn(GameWorld world, Actor target, int damage, Race? by = null, Actor? shooter = null, string? weapon = null)
    {
        var before = target.HitPoints + target.Armour;
        world.Damage(target, damage, by, shooter, weapon);
        return before - target.HitPoints - target.Armour;
    }

    // ---- abilities --------------------------------------------------------------------------------

    [Fact]
    public void An_ability_has_to_be_in_its_slot_and_the_hero_on_its_feet()
    {
        var world = Quiet();

        Assert.Equal("no ability in this slot", Do(world, Ability(Hero(world).Position)));
        Assert.Equal("no such ability slot", Do(world, Ability(Hero(world).Position, slot: 2)));

        Wear(world, "supercharge", WiredSlot.Ability(1));
        Kill(world, Hero(world), Race.Blue);
        world.Step();
        Assert.Equal("the hero is down", Do(world, Ability(Vector2.Zero, slot: 1)));
    }

    [Fact]
    public void The_vortex_comes_down_at_the_aim_point_and_leaves_the_hero_free()
    {
        var world = Quiet();
        var state = Red(world);
        Wear(world, "vortex", WiredSlot.Ability(0));
        var hero = Hero(world);
        var victim = Grunt(world, Away(world, 60));

        world.Issue(Ability(victim.Position));
        var events = Run(world, 12);

        Assert.True(victim.IsDead);
        Assert.Contains(events, e => e is { Kind: GameEventKind.WiredAbilityUsed, Detail: "vortex", Other: 0 });
        Assert.Contains(events, e => e is { Kind: GameEventKind.Fired, Detail: "VORTEX" } && e.Actor == hero.Id);
        Assert.False(hero.IsDead);

        // It can be driven at once, unlike the Eradicator after its vortex.
        Assert.InRange(state.AbilitySecondsLeft(0, world.Time), 20, 30);
    }

    [Fact]
    public void The_vortex_reaches_ninety_metres_and_keeps_away_from_the_hero()
    {
        var world = Quiet();
        var state = Red(world);
        Wear(world, "vortex", WiredSlot.Ability(0));
        var hero = Hero(world);

        Assert.Equal("that is out of reach", Do(world, Ability(hero.Position + new Vector2(95, 0))));
        Assert.Equal(0, state.AbilitySecondsLeft(0, world.Time));

        var before = hero.HitPoints + hero.Armour;
        Assert.Null(Do(world, Ability(hero.Position)));
        Run(world, 12);

        Assert.Equal(before, hero.HitPoints + hero.Armour);
    }

    [Fact]
    public void An_ability_waits_for_its_cooldown()
    {
        var world = Quiet();
        var state = Red(world);
        Wear(world, "supercharge", WiredSlot.Ability(1));
        var aim = Hero(world).Position;

        Assert.Null(Do(world, Ability(aim, slot: 1)));
        Assert.Equal(30, state.AbilitySecondsLeft(1, world.Time), 0.2f);
        Assert.Equal("that is not ready yet", Do(world, Ability(aim, slot: 1)));

        Run(world, 30);
        Assert.Null(Do(world, Ability(aim, slot: 1)));
    }

    /// <summary>A machine of the wave within reach of the hero, that its guns will not bring down before the orb arrives.</summary>
    private static Machine Enlisted(GameWorld world)
    {
        var machine = Tough(world, Away(world, 25));
        world.Wired!.Foes.Add(new WiredFoe(machine, new WiredFoePlan(Ninja), false, world.Time));
        world.Step();
        return machine;
    }

    /// <summary>Throws the orb at a machine until it has come over.</summary>
    private static void Turn(GameWorld world, Machine machine)
    {
        world.Issue(Ability(machine.Position));
        for (var i = 0; i < 20 * 5 && machine.Race != Race.Red; i++)
            world.Step();
        Assert.Equal(Race.Red, machine.Race);
    }

    [Fact]
    public void Treachery_turns_a_machine_for_thirty_seconds_and_it_still_counts_as_an_enemy()
    {
        var world = Quiet();
        var run = world.Wired!;
        Wear(world, "treachery", WiredSlot.Ability(0));
        var machine = Enlisted(world);

        Turn(world, machine);
        world.Step();

        var turned = Assert.Single(run.Turned);
        Assert.Equal(machine, turned.Foe.Machine);
        Assert.Equal(world.Time + WiredBalance.TreacheryTime, turned.BackAt, 1f);
        Assert.Empty(run.Foes);
        Assert.Equal(2, run.EnemiesLeft);
        Assert.Equal(DefCon.Low, machine.DefCon);

        var events = Run(world, WiredBalance.TreacheryTime + 0.5f);

        Assert.Equal(Race.Blue, machine.Race);
        Assert.Empty(run.Turned);
        Assert.Contains(run.Foes, f => f.Machine == machine);
        Assert.Contains(events, e => e.Kind == GameEventKind.WiredFoeWentBack && e.Actor == machine.Id);
    }

    [Fact]
    public void A_turned_machine_goes_back_at_once_when_nobody_is_left_to_fight()
    {
        var world = Quiet();
        var run = world.Wired!;
        Wear(world, "treachery", WiredSlot.Ability(0));
        var machine = Enlisted(world);
        run.Groups.Clear();

        world.Issue(Ability(machine.Position));
        var events = Run(world, 3);

        Assert.Contains(events, e => e.Kind == GameEventKind.WiredFoeWentBack && e.Actor == machine.Id);
        Assert.Equal(Race.Blue, machine.Race);
        Assert.Equal(WiredPhase.Wave, run.Phase);
        Assert.Contains(run.Foes, f => f.Machine == machine);
    }

    [Fact]
    public void A_machine_that_falls_while_turned_is_simply_gone()
    {
        var world = Quiet();
        var run = world.Wired!;
        Wear(world, "treachery", WiredSlot.Ability(0));
        var machine = Enlisted(world);
        Turn(world, machine);
        world.Step();
        var kills = Red(world).Stats.Kills;

        Kill(world, machine);
        world.Step();
        world.Step();

        Assert.Empty(run.Turned);
        Assert.Equal(1, run.EnemiesLeft);
        Assert.Equal(kills, Red(world).Stats.Kills);
    }

    [Fact]
    public void Treachery_needs_something_to_turn_and_that_is_no_boss()
    {
        var world = AtWave(5);
        var state = Red(world);
        Wear(world, "treachery", WiredSlot.Ability(0));
        var boss = world.Wired!.Bosses.First().Machine;
        var hero = Hero(world);

        Assert.Equal("nothing there to turn", Do(world, Ability(hero.Position)));
        Assert.Equal(0, state.AbilitySecondsLeft(0, world.Time));

        boss.Position = Away(world, 40);
        Assert.Equal("nothing there to turn", world.WhyNoAbility(state, 0, boss.Position));
    }

    [Fact]
    public void An_orb_that_reaches_a_boss_does_nothing()
    {
        var world = AtWave(5, spawn: 300);
        var boss = world.Wired!.Bosses.First().Machine;
        var traitor = world.AddMachine(Race.Red, world.Nav.NearestFreePoint(boss.Position + new Vector2(0, 30), crossesWater: false)!.Value,
            "ADMINISTRATOR", "COMMANDER", 4, "L_MULTI_LAUNCHER5_R_TREACHERY");

        var events = new List<GameEvent>();
        for (var i = 0; i < 20 * 20; i++)
        {
            traitor.HitPoints = traitor.MaxHitPoints;
            world.Step();
            events.AddRange(world.Events);
        }

        Assert.Contains(events, e => e is { Kind: GameEventKind.Fired, Detail: "ORB_OF_TREACHERY" });
        Assert.Equal(Race.Blue, boss.Race);
    }

    [Fact]
    public void The_first_boss_hits_with_a_share_of_its_vortex_and_others_of_its_type_keep_theirs()
    {
        var world = AtWave(5, spawn: 300);
        var boss = world.Wired!.Bosses.First().Machine;
        var plain = world.AddMachine(Race.Blue, world.Nav.NearestFreePoint(boss.Position + new Vector2(0, 30), crossesWater: false)!.Value,
            "AGGRESSOR", "BALLISTA", 3, "T_VORTEX");

        Assert.Equal(0.2f, WiredDirector.BossOf(5)!.Damage);
        Assert.Equal(plain.Weapons[0].Spec.Damage * 0.2f, boss.Weapons[0].Spec.Damage, 3);
        Assert.Equal(150, plain.Weapons[0].Spec.Damage, 3);
    }

    [Fact]
    public void A_discharge_runs_out_as_far_as_its_reach_all_round()
    {
        var world = Quiet();
        Wear(world, "discharge", WiredSlot.Ability(0));
        var (ahead, aside, far) = (Unarmed(world, Away(world, 8)), Unarmed(world, Away(world, 7, 6)), Unarmed(world, Away(world, 18)));
        world.Step();

        world.Issue(Ability(Hero(world).Position));
        var events = Run(world, 2);

        // An arc kills a spy; the hero's own bolter meanwhile does not.
        Assert.True(ahead.IsDead);
        Assert.True(aside.IsDead);
        Assert.False(far.IsDead);
        Assert.Single(events, e => e is { Kind: GameEventKind.Fired, Detail: "ELECTRIC_CHARGE" });
    }

    [Fact]
    public void An_ability_is_no_weapon_in_the_statistics_nor_a_round_fired()
    {
        var world = Quiet();
        var state = Red(world);
        Wear(world, "discharge", WiredSlot.Ability(0));
        Wear(world, "supercharge", WiredSlot.Ability(1));
        var (lastShot, firing) = (state.LastShotAt, state.FiringSince);

        Assert.Null(Do(world, Ability(Hero(world).Position, 1)));

        Assert.Equal((lastShot, firing), (state.LastShotAt, state.FiringSince));
        Assert.DoesNotContain("SUPERCHARGE_SUPER", state.Stats.Weapons.Keys);

        var spy = Unarmed(world, Away(world, 8));
        world.Step();
        var dealt = state.Stats.DamageDealt;
        world.Issue(Ability(Hero(world).Position));
        Run(world, 2);

        // What the lightning did counts for the run all the same, only for no weapon: an electric charger carried keeps its own.
        Assert.True(spy.IsDead);
        Assert.True(state.Stats.DamageDealt > dealt);
        Assert.DoesNotContain("ELECTRIC_CHARGE", state.Stats.Weapons.Keys);
    }

    [Fact]
    public void Supercharge_heals_the_hero_and_the_squad_near_it()
    {
        var world = Quiet();
        Wear(world, "supercharge", WiredSlot.Ability(0));
        var hero = Hero(world);
        var (near, far) = (Escort(world, Away(world, 20)), Escort(world, Away(world, 60)));
        hero.HitPoints = near.HitPoints = far.HitPoints = 1;

        Assert.Null(Do(world, Ability(hero.Position)));

        Assert.Equal(1 + (int)(hero.MaxHitPoints * WiredBalance.SuperchargeHeal), hero.HitPoints, 1f);
        Assert.Equal(1 + (int)(near.MaxHitPoints * WiredBalance.SuperchargeHeal), near.HitPoints, 1f);
        Assert.Equal(1, far.HitPoints);
    }

    [Fact]
    public void A_minefield_lies_in_front_of_the_hero_and_goes_off_under_the_enemy()
    {
        var world = Quiet();
        Wear(world, "minefield", WiredSlot.Ability(0));
        var hero = Hero(world);
        var aim = Away(world, 40);
        var ahead = Vector2.Normalize(aim - hero.Position);

        world.Issue(Ability(aim));
        var events = Run(world, 0.1f);

        Assert.Equal(WiredBalance.MinesLaid, world.LandMines.Count);
        Assert.Equal(WiredBalance.MinesLaid, events.Count(e => e.Kind == GameEventKind.MineLaid));
        Assert.All(world.LandMines, m =>
        {
            Assert.Equal(Race.Red, m.Race);
            Assert.True(Vector2.Dot(m.Position - hero.Position, ahead) > 0);
        });

        var mine = world.LandMines[2];
        var victim = Tough(world, mine.Position);
        var durability = victim.HitPoints + victim.Armour;
        Run(world, 1);

        Assert.DoesNotContain(mine, world.LandMines);
        Assert.True(victim.HitPoints + victim.Armour < durability);
    }

    [Fact]
    public void An_ion_call_strikes_where_it_is_aimed_and_comes_back_after_five_waves()
    {
        var world = Quiet(2);
        var run = world.Wired!;
        var state = Red(world);
        Wear(world, "ionstrike", WiredSlot.Ability(0));
        var victim = Grunt(world, Away(world, 120));

        world.Issue(Ability(victim.Position));
        var events = Run(world, 7);

        Assert.True(victim.IsDead);
        Assert.Contains(events, e => e is { Kind: GameEventKind.Fired, Detail: "ION_ORBITAL_CANNON" });
        Assert.Equal(5, state.AbilityWavesLeft(0, run.Wave));
        Assert.Equal(0, state.AbilitySecondsLeft(0, world.Time));

        run.Wave = 6;
        Assert.Equal("that is not ready yet", world.WhyNoAbility(state, 0, Hero(world).Position));
        run.Wave = 7;
        Assert.Null(world.WhyNoAbility(state, 0, Hero(world).Position));
    }

    // ---- the dash ---------------------------------------------------------------------------------

    [Fact]
    public void Only_thrusters_dash()
    {
        var world = Quiet();

        Assert.Equal("the hero has no thrusters", Do(world, new WiredDashCommand(Race.Red, Vector2.UnitX)));
    }

    [Fact]
    public void A_dash_throws_the_hero_some_way_and_then_has_to_cool_down()
    {
        var world = Quiet();
        Wear(world, "thrusters", WiredSlot.Module(0));
        var hero = Hero(world);
        var start = hero.Position;
        var way = Away(world, 30) - start;

        world.Issue(new WiredDashCommand(Race.Red, way * 3));
        var events = Run(world, 0.6f);

        Assert.InRange(Vector2.Distance(hero.Position, start), 13, 17);
        Assert.True(Vector2.Dot(hero.Position - start, way) > 0);
        Assert.Contains(events, e => e.Kind == GameEventKind.WiredDashed);
        Assert.IsType<IdleOrder>(hero.Order);

        Assert.Equal("that is not ready yet", Do(world, new WiredDashCommand(Race.Red, way)));
        Run(world, WiredBalance.DashCooldowns[0]);
        Assert.Null(Do(world, new WiredDashCommand(Race.Red)));
    }

    [Fact]
    public void Better_thrusters_cool_down_sooner()
    {
        var world = Quiet();
        Wear(world, "thrusters", WiredSlot.Module(0), mk: 3);
        var state = Red(world);

        Assert.Null(Do(world, new WiredDashCommand(Race.Red, Vector2.UnitX)));

        Assert.Equal(world.Time + WiredBalance.DashCooldowns[2], state.DashReadyAt, 0.2f);
    }

    // ---- the board --------------------------------------------------------------------------------

    [Fact]
    public void There_are_six_chips_of_each_family()
    {
        Assert.True(WiredChips.All.Count >= 24);
        Assert.All(Enum.GetValues<ChipFamily>(), family => Assert.Equal(6, WiredChips.All.Count(c => c.Family == family)));
        Assert.Equal(WiredChips.All.Count, WiredChips.All.Select(c => c.Id).Distinct().Count());
    }

    [Fact]
    public void Two_chips_of_a_family_on_a_trace_are_linked_and_stronger()
    {
        var world = NewRun();
        Chip(world, "tough", 0);
        Chip(world, "damper", 1);
        var state = Red(world);

        Assert.True(WiredChips.Linked(state.Loadout.Chips, 0));
        Assert.True(WiredChips.Linked(state.Loadout.Chips, 1));
        Assert.Equal(0.2f * 1.5f, state.ChipValue("tough"), 0.001f);
        Assert.Equal(0.35f * 1.5f, state.ChipValue("damper"), 0.001f);
        Assert.Equal(0.3f, WiredChips.Building(state.Loadout).Armour, 0.001f);
    }

    [Theory]
    [InlineData(0, 3, true)] // the bridge
    [InlineData(5, 0, true)]
    [InlineData(1, 3, false)]
    [InlineData(2, 5, false)]
    public void Only_a_trace_links(int one, int other, bool linked)
    {
        var world = NewRun();
        Chip(world, "tough", one);
        Chip(world, "nano", other);

        Assert.Equal(linked, WiredChips.Linked(Red(world).Loadout.Chips, one));
    }

    [Fact]
    public void Chips_of_two_families_do_not_link()
    {
        var world = NewRun();
        Chip(world, "tough", 0);
        Chip(world, "marksman", 1);

        Assert.False(WiredChips.Linked(Red(world).Loadout.Chips, 0));
        Assert.Equal(0.2f, Red(world).ChipValue("tough"), 0.001f);
    }

    [Fact]
    public void Chips_light_up_for_the_hud_while_they_work()
    {
        var world = Quiet();
        var hero = Hero(world);
        Chip(world, "overload");
        Assert.Empty(WiredChips.Lit(world, Red(world)));

        hero.HitPoints = 1;

        Assert.Contains("overload", WiredChips.Lit(world, Red(world)));
    }

    // ---- offensive chips --------------------------------------------------------------------------

    [Fact]
    public void Marksman_and_glowing_barrels_change_the_build()
    {
        var world = NewRun();
        var bare = HeroBuild.Resolve(Data, Red(world).Loadout);

        Chip(world, "marksman", 0);
        Chip(world, "hotbarrels", 2);
        var build = HeroBuild.Resolve(Data, Red(world).Loadout);

        Assert.Equal(bare.Range * 1.15f, build.Range, 0.01f);
        Assert.Equal(bare.DamagePerSecond * 1.15f, build.DamagePerSecond, 0.05f);
        Assert.Equal(bare.PowerDraw + 1, build.PowerDraw);
    }

    [Fact]
    public void Sustained_fire_grows_with_time_up_to_its_limit()
    {
        var world = Quiet();
        var state = Red(world);
        var (hero, target) = (Hero(world), Tough(world, Away(world, 40)));
        Chip(world, "sustained");

        Assert.Equal(100, Drawn(world, target, 100, Race.Red, hero, "BOLTER"));

        state.LastShotAt = world.Time;
        state.FiringSince = world.Time - 10;
        Assert.Equal(120, Drawn(world, target, 100, Race.Red, hero, "BOLTER"));

        state.FiringSince = world.Time - 60;
        Assert.Equal(130, Drawn(world, target, 100, Race.Red, hero, "BOLTER"));
    }

    [Fact]
    public void The_chips_only_count_for_the_heros_own_shots_at_the_enemy()
    {
        var world = Quiet();
        var state = Red(world);
        var target = Tough(world, Away(world, 40));
        var escort = Escort(world, Away(world, 20));
        Chip(world, "sustained");
        state.LastShotAt = world.Time;
        state.FiringSince = world.Time - 10;

        Assert.Equal(100, Drawn(world, target, 100, Race.Red, escort, "BOLTER"));
    }

    [Fact]
    public void Shots_a_moment_apart_are_unbroken_fire_and_a_pause_ends_it()
    {
        var world = Quiet();
        var state = Red(world);
        var hero = Hero(world);
        var target = Unarmed(world, Away(world, 10));

        Assert.Contains(Run(world, 4), e => e.Kind == GameEventKind.Fired && e.Actor == hero.Id);

        Assert.True(world.Time - state.FiringSince > 2, $"firing for {world.Time - state.FiringSince} s");
        Assert.True(world.Time - state.LastShotAt < WiredChips.FireGap(hero));

        var began = state.FiringSince;
        Kill(world, target);
        Run(world, WiredChips.FireGap(hero) + 1);
        Unarmed(world, Away(world, 10));
        Run(world, 2);

        Assert.True(state.FiringSince > began + 3);
    }

    [Fact]
    public void Revenge_burns_for_ten_seconds_once_the_armour_is_gone()
    {
        var world = Quiet();
        var (hero, target) = (Hero(world), Tough(world, Away(world, 40)));
        Chip(world, "revenge");
        var armour = hero.Armour;

        Assert.Equal(100, Drawn(world, target, 100, Race.Red, hero, "BOLTER"));
        world.Damage(hero, armour + 1, Race.Blue);
        Assert.Equal(0, hero.Armour);

        Assert.Equal(130, Drawn(world, target, 100, Race.Red, hero, "BOLTER"));
        Assert.Contains("revenge", WiredChips.Lit(world, Red(world)));

        Run(world, 10.1f);
        Assert.Equal(100, Drawn(world, target, 100, Race.Red, hero, "BOLTER"));
    }

    [Fact]
    public void Shrapnel_hurts_what_stands_round_a_kill()
    {
        var world = Quiet();
        var run = world.Wired!;
        Chip(world, "shrapnel");
        var at = Away(world, 60);
        var (victim, near, far) = (Tough(world, at), Tough(world, at + new Vector2(5, 0)), Tough(world, at + new Vector2(0, 25)));
        run.Foes.Add(new WiredFoe(victim, new WiredFoePlan(Ninja), false, world.Time));
        world.Step();
        var durability = (near.HitPoints + near.Armour, far.HitPoints + far.Armour);

        Kill(world, victim);
        var events = Run(world, 0.2f);

        Assert.Equal((int)(victim.MaxHitPoints * 0.15f), durability.Item1 - near.HitPoints - near.Armour);
        Assert.Equal(durability.Item2, far.HitPoints + far.Armour);
        Assert.Contains(events, e => e.Kind == GameEventKind.WiredShrapnel);
    }

    [Fact]
    public void Overload_speeds_the_reloads_of_a_hurt_hero()
    {
        var world = Quiet();
        var hero = Hero(world);
        Chip(world, "overload");
        var weapon = hero.Weapons[0];

        weapon.Reload = 1;
        Run(world, 0.5f);
        Assert.Equal(0.5f, weapon.Reload, 0.02f);

        hero.HitPoints = 1;
        weapon.Reload = 1;
        Run(world, 0.5f);
        Assert.Equal(0.3f, weapon.Reload, 0.02f);
    }

    // ---- defensive chips --------------------------------------------------------------------------

    [Fact]
    public void Emergency_repair_saves_the_hero_once_a_wave()
    {
        var world = Quiet();
        var hero = Hero(world);
        Chip(world, "lastditch");

        world.Damage(hero, 1_000_000, Race.Blue);

        Assert.Contains(world.Events, e => e.Kind == GameEventKind.WiredHeroSaved);
        Run(world, 0.1f);
        Assert.False(hero.IsDead);
        Assert.Equal(1, hero.HitPoints);

        // For three seconds nothing hurts it.
        world.Damage(hero, 1_000_000, Race.Blue);
        Assert.False(hero.IsDead);

        Run(world, 3);
        world.Damage(hero, 1_000_000, Race.Blue);
        Assert.True(hero.IsDead);
    }

    [Fact]
    public void Reactive_armour_takes_a_share_off_the_hit_after_a_hit()
    {
        var world = Quiet();
        var hero = Hero(world);
        Chip(world, "reactive");

        Assert.Equal(10, Drawn(world, hero, 10, Race.Blue));
        Assert.Equal(8, Drawn(world, hero, 10, Race.Blue));

        Run(world, WiredBalance.ReactiveTime + 0.5f);
        Assert.Equal(10, Drawn(world, hero, 10, Race.Blue));
    }

    [Fact]
    public void Nano_healing_works_out_of_fire_only()
    {
        var world = Quiet();
        var hero = Hero(world);
        Chip(world, "nano");
        hero.HitPoints = 10;

        Run(world, 6.2f);
        Assert.Equal(12, hero.HitPoints);

        Red(world).LastHitAt = world.Time;
        Run(world, 3);
        Assert.Equal(12, hero.HitPoints);
    }

    [Fact]
    public void Last_bastion_holds_near_the_pod_while_there_is_armour()
    {
        var world = Quiet();
        var hero = Hero(world);
        Chip(world, "bastion");

        Assert.True(hero.DistanceTo(Pod(world)) <= 40);
        Assert.Equal(8, Drawn(world, hero, 10, Race.Blue));

        hero.Armour = 0;
        Assert.Equal(10, Drawn(world, hero, 10, Race.Blue));
    }

    [Fact]
    public void A_damper_softens_blasts_and_mines_and_nothing_else()
    {
        var world = Quiet();
        var hero = Hero(world);
        Chip(world, "damper");

        Assert.Equal(13, Drawn(world, hero, 20, Race.Blue, weapon: "VORTEX"));
        Assert.Equal(13, Drawn(world, hero, 20, Race.Blue, weapon: GameWorld.LandMineBlast));
        Assert.Equal(20, Drawn(world, hero, 20, Race.Blue, weapon: "BOLTER"));
    }

    [Fact]
    public void Even_a_small_hit_on_a_protected_hero_counts_for_a_point()
    {
        var world = Quiet();
        var hero = Hero(world);
        Chip(world, "damper", 0);
        Chip(world, "tough", 1);

        Assert.Equal(1, Drawn(world, hero, 1, Race.Blue, weapon: "VORTEX"));
    }

    // ---- salvage chips ----------------------------------------------------------------------------

    [Fact]
    public void A_scrap_magnet_fetches_wrecks_from_further_off()
    {
        var world = NewRun();
        Chip(world, "magnet");

        Assert.Equal(15, WiredChips.Building(Red(world).Loadout).PickupReach);
        Assert.True(HeroBuild.Resolve(Data, Red(world).Loadout).PickupReach >= 15);
    }

    [Fact]
    public void The_recycler_brings_more_and_never_what_was_paid()
    {
        var world = NewRun();
        var state = Red(world);
        var item = state.Add(WiredKind.Weapon, "BOLTER", 100);

        Assert.Equal(50, state.SellValue(item));

        Chip(world, "recycler", 0);
        Assert.Equal(75, state.SellValue(item));

        Chip(world, "trader", 1);
        Assert.Equal(87, state.SellValue(item));
    }

    [Fact]
    public void A_trader_lowers_the_shops_prices()
    {
        var world = NewRun();
        Chip(world, "trader");

        Assert.Equal(90, Red(world).Priced(100));
    }

    [Fact]
    public void Treasure_trove_makes_elites_leave_more_crates()
    {
        var world = NewRun();
        Assert.Equal(1, WiredChips.LootShare(Red(world)));

        Chip(world, "trove");
        Assert.Equal(1.5f, WiredChips.LootShare(Red(world)), 0.001f);

        var (plain, lucky) = (Crates(false), Crates(true));
        Assert.True(lucky > plain, $"{plain} crates without it, {lucky} with it");

        static int Crates(bool trove)
        {
            var crates = 0;
            for (var seed = 1; seed <= 200; seed++)
            {
                var world = Quiet();
                if (trove)
                    Chip(world, "trove");
                var run = world.Wired!;
                world.Random.Next(seed);
                var elite = Grunt(world, Away(world, 80));
                run.Foes.Add(new WiredFoe(elite, new WiredFoePlan(new ResearchKey("AGGRESSOR", "GRUNT", 1, "R_BOLTER")) { Elite = true }, false, world.Time));
                for (var i = 0; i < seed % 17; i++)
                    world.Random.Next();
                Kill(world, elite);
                world.Step();
                crates += world.WiredPickups.Count;
            }
            return crates;
        }
    }

    [Fact]
    public void Gold_vein_pays_more_as_the_waves_go_on()
    {
        var world = AtWave(7);
        var state = Red(world);
        Assert.Equal(0, WiredChips.WaveExtra(state, 7));

        Chip(world, "vein");
        Assert.Equal(10, WiredChips.WaveExtra(state, 4));
        Assert.Equal(20, WiredChips.WaveExtra(state, 7));
        Assert.Equal(30, WiredChips.WaveExtra(state, 10));

        var bank = world.Races[Race.Red].Bmu;
        var events = new List<GameEvent>();
        for (var i = 0; i < 20 * 120 && world.Wired!.Phase == WiredPhase.Wave; i++)
        {
            foreach (var foe in world.Wired.Foes)
                world.Damage(foe.Machine, 1_000_000, null);
            world.Step();
            events.AddRange(world.Events);
        }
        var paid = WiredBalance.WaveBonus(7) + 20;
        Assert.Contains(events, e => e.Kind == GameEventKind.WiredWaveCleared && e.Detail == paid.ToString());
        Assert.Equal(bank + paid, world.Races[Race.Red].Bmu, 0.01f);
    }

    [Fact]
    public void A_savings_bank_keeps_half_through_a_death()
    {
        var world = Quiet();
        world.Races[Race.Red].Bmu = 100;
        Chip(world, "savings");

        Kill(world, Hero(world), Race.Blue);
        world.Step();

        Assert.Equal(50, world.Races[Race.Red].Bmu, 0.01f);
    }

    [Fact]
    public void Without_savings_a_death_takes_the_bank()
    {
        var world = Quiet();
        world.Races[Race.Red].Bmu = 100;

        Kill(world, Hero(world), Race.Blue);
        world.Step();

        Assert.Equal(0, world.Races[Race.Red].Bmu, 0.01f);
    }

    // ---- command chips ----------------------------------------------------------------------------

    [Fact]
    public void A_radio_booster_helps_the_squads_aim_near_the_hero()
    {
        var world = Quiet();
        var (near, far) = (Escort(world, Away(world, 20)), Escort(world, Away(world, 60)));
        var stranger = Grunt(world, Away(world, 20, 5), Race.Red);

        Assert.Equal(0, WiredChips.SquadAim(world, near));

        Chip(world, "booster");
        Assert.Equal(15, WiredChips.SquadAim(world, near));
        Assert.Equal(0, WiredChips.SquadAim(world, far));
        Assert.Equal(0, WiredChips.SquadAim(world, stranger));
        Assert.Equal(0, WiredChips.SquadAim(world, Hero(world)));
    }

    [Fact]
    public void Squad_tactics_makes_room_for_one_more_and_linked_for_two()
    {
        var world = NewRun();
        Assert.Equal(0, WiredChips.SquadExtra(Red(world)));

        Chip(world, "tactics", 0);
        Assert.Equal(1, WiredChips.SquadExtra(Red(world)));

        Chip(world, "booster", 1);
        Assert.Equal(2, WiredChips.SquadExtra(Red(world)));
    }

    [Fact]
    public void A_rally_call_speeds_the_squad_while_it_follows()
    {
        var world = Quiet();
        var escort = Escort(world, Away(world, 20));
        Chip(world, "rally");

        Assert.Equal(1, WiredChips.Pace(world, escort));

        escort.Order = new FollowOrder(Hero(world).Id);
        Assert.Equal(1.2f, WiredChips.Pace(world, escort), 0.001f);
        Assert.Equal(1, WiredChips.Pace(world, Hero(world)));
    }

    [Fact]
    public void A_field_workshop_mends_the_squad_near_the_hero()
    {
        var world = Quiet();
        var (near, far) = (Enrolled(world, Tough(world, Away(world, 20), Race.Red)), Escort(world, Away(world, 60)));
        near.HitPoints = 100;
        far.HitPoints = 2;
        Chip(world, "workshop");

        Run(world, 5);

        Assert.InRange(near.HitPoints, 136, 154);
        Assert.Equal(2, far.HitPoints);
    }

    [Fact]
    public void Veterans_come_one_level_better_where_there_is_one()
    {
        var world = NewRun();
        var state = Red(world);
        var grunt = new ResearchKey("AGGRESSOR", "GRUNT", 1, "R_BOLTER");

        Assert.Equal(grunt, WiredChips.Veteran(state, grunt));

        Chip(world, "veterans", 0);
        Assert.Equal(new ResearchKey("AGGRESSOR", "GRUNT", 2, "L_BOLTER"), WiredChips.Veteran(state, grunt));

        var best = new ResearchKey("AGGRESSOR", "KNIGHT", 5, "LRT_MULTI_LAUNCHER4_X3");
        Assert.Equal(best, WiredChips.Veteran(state, best));

        Chip(world, "tactics", 1);
        Assert.Equal(("GRUNT", 3), (WiredChips.Veteran(state, grunt).SubType, WiredChips.Veteran(state, grunt).HardwareLevel));
    }

    [Fact]
    public void Covering_fire_turns_the_squad_on_what_hits_the_hero()
    {
        var world = Quiet();
        var hero = Hero(world);
        var escort = Escort(world, Away(world, 15));
        var shooter = Grunt(world, Away(world, 40));
        Chip(world, "cover");
        world.Step();

        world.Damage(hero, 2, Race.Blue, shooter, "BOLTER");

        Assert.Equal(shooter.Id, Assert.IsType<AttackOrder>(escort.Order).Target);
    }

    // ---- special waves ----------------------------------------------------------------------------

    [Fact]
    public void Every_third_wave_without_a_boss_is_special_from_the_fourth_on()
    {
        var special = Enumerable.Range(1, 25).Where(WiredDirector.IsSpecialWave);

        Assert.Equal([4, 8, 12, 16, 19, 23], special);
    }

    [Fact]
    public void A_special_wave_is_never_the_kind_of_the_last_and_comes_when_it_is_open()
    {
        var random = new Random(3);
        var seen = new HashSet<WiredSpecial>();
        var last = WiredSpecial.None;
        for (var i = 0; i < 400; i++)
        {
            var kind = WiredDirector.RollSpecial(30, last, random);
            Assert.NotEqual(last, kind);
            Assert.NotEqual(WiredSpecial.None, kind);
            seen.Add(kind);
            last = kind;
        }
        Assert.Equal(7, seen.Count);

        for (var i = 0; i < 50; i++)
            Assert.Contains(WiredDirector.RollSpecial(4, WiredSpecial.None, random), new[] { WiredSpecial.Night, WiredSpecial.Storm });
    }

    [Fact]
    public void The_fourth_wave_of_a_run_is_announced_as_special()
    {
        var world = AtWave(3);
        var run = world.Wired!;
        Assert.Equal(WiredSpecial.None, run.NextSpecial);

        for (var i = 0; i < 20 * 120 && run.Phase == WiredPhase.Wave; i++)
        {
            foreach (var foe in run.Foes)
                Kill(world, foe.Machine);
            world.Step();
        }

        var kind = run.NextSpecial;
        Assert.NotEqual(WiredSpecial.None, kind);

        run.Countdown = 0;
        var events = Run(world, 0.2f);
        Assert.Equal(kind, run.Special);
        Assert.Contains(events, e => e is { Kind: GameEventKind.WiredSpecialWave, Other: 4 } && e.Detail == kind.ToString());
    }

    [Fact]
    public void Night_darkens_the_sim_for_the_wave_and_only_for_it()
    {
        var world = AtWave(4, WiredSpecial.Night);
        var run = world.Wired!;

        Assert.Equal(WiredSpecial.Night, run.Special);
        Assert.Equal(WiredBalance.NightDarkness, world.ForcedDarkness);
        Run(world, 3);
        Assert.Equal(WiredBalance.NightDarkness, world.Darkness);

        for (var i = 0; i < 20 * 120 && run.Phase == WiredPhase.Wave; i++)
        {
            foreach (var foe in run.Foes)
                Kill(world, foe.Machine);
            world.Step();
        }

        Assert.Null(world.ForcedDarkness);
        Assert.Equal(WiredSpecial.None, run.Special);
    }

    [Fact]
    public void The_first_waves_bring_a_crowd_of_weaker_machines_worth_what_the_budget_bought()
    {
        foreach (var difficulty in Enum.GetValues<WiredDifficulty>())
        {
            for (var wave = 1; wave < WiredBalance.CrowdUntil; wave++)
            {
                var bought = WiredDirector.Buy(Data, wave, WiredBalance.Budget(wave, difficulty), new Random(wave));
                var crowd = Machines(WiredDirector.Plan(Data, wave, difficulty, new Random(wave)));

                Assert.Equal(Math.Max(bought.Count, WiredBalance.CrowdSize), crowd.Count);
                Assert.All(crowd, m => Assert.InRange(m.HitPoints, 0.01f, 1));
                Assert.All(crowd, m => Assert.Equal(m.HitPoints, m.Damage));
                Assert.Equal(bought.Count, crowd.Sum(m => m.Worth), 3);
                Assert.True(crowd.Sum(m => m.HitPoints) <= WiredBalance.CrowdStrength(wave) * bought.Count + 0.001f);
            }
        }
        var later = WiredBalance.CrowdUntil + 1;
        Assert.All(Machines(WiredDirector.Plan(Data, later, WiredDifficulty.Normal, new Random(later))), m => Assert.Equal(1, m.Worth));
    }

    [Fact]
    public void A_storm_brings_twice_as_many_of_the_fastest_on_the_ground()
    {
        // From the wave on that a plain wave brings what it bought and no crowd of weaker machines.
        for (var wave = WiredBalance.CrowdUntil + 1; wave <= 40; wave++)
        {
            if (WiredDirector.IsBossWave(wave))
                continue;
            var usual = Machines(WiredDirector.Plan(Data, wave, WiredDifficulty.Normal, new Random(wave)));
            var storm = Machines(WiredDirector.Plan(Data, wave, WiredDifficulty.Normal, new Random(wave), WiredSpecial.Storm));

            Assert.Equal(Math.Min(WiredBalance.MostInWave, usual.Count * WiredBalance.StormShare), storm.Count);
            Assert.All(storm, m => Assert.NotEqual("BOSS", m.Key.SubType));
            Assert.True(storm.Select(m => m.Key.TypeName + m.Key.HardwareLevel).Distinct().Count() <= WiredBalance.StormKinds);
        }
    }

    private static List<WiredFoePlan> Machines(WiredWavePlan plan) => plan.Groups.SelectMany(g => g.Machines).ToList();

    [Fact]
    public void Spies_arrive_first_and_lay_mines_on_the_way_and_round_the_pod()
    {
        var plan = WiredDirector.Plan(Data, 8, WiredDifficulty.Normal, new Random(5), WiredSpecial.Spies);
        Assert.Equal(0, plan.Groups[0].After);
        Assert.All(plan.Groups[0].Machines, m => Assert.Equal((WiredRole.Spy, "SPY_LOCATOR"), (m.Role, m.Key.Type)));
        Assert.True(plan.Groups[0].Machines.Count >= WiredBalance.FewestSpies);
        Assert.All(plan.Groups.Skip(1), g => Assert.True(g.After >= WiredBalance.SpyLead));

        var world = AtWave(8, WiredSpecial.Spies, spawn: 300);
        var run = world.Wired!;
        var spies = run.Foes.Where(f => f.Plan.Role == WiredRole.Spy).Select(f => f.Machine).ToList();
        Assert.NotEmpty(spies);
        Assert.All(spies, s => Assert.IsType<DropMinesOrder>(s.Order));

        // The rest of the wave is out of the way, so that the spies can do their work.
        for (var i = 0; i < 20 * 150 && spies.Any(s => s.Mines > 0); i++)
        {
            foreach (var foe in run.Foes.Where(f => f.Plan.Role != WiredRole.Spy).ToList())
                Kill(world, foe.Machine);
            world.Step();
        }

        var pod = Pod(world);
        var mines = world.LandMines.Where(m => m.Race == run.Enemy).ToList();
        Assert.True(mines.Count >= spies.Count * 3, $"{mines.Count} mines");
        Assert.Contains(mines, m => Vector2.Distance(m.Position, pod.Position) is >= WiredBalance.MineRingNear - 8 and <= WiredBalance.MineRingFar + 8);
        Assert.Contains(mines, m => Vector2.Distance(m.Position, pod.Position) > WiredBalance.MineRingFar + 8);
    }

    [Fact]
    public void A_spy_out_of_mines_comes_to_the_pod_like_the_others()
    {
        var world = AtWave(8, WiredSpecial.Spies);
        var run = world.Wired!;
        var spy = run.Foes.First(f => f.Plan.Role == WiredRole.Spy).Machine;

        spy.Mines = 0;
        spy.Order = new IdleOrder();
        Run(world, WiredBalance.OrdersEvery + 0.5f);

        Assert.IsType<MoveOrder>(spy.Order);
    }

    [Fact]
    public void A_turncoat_goes_for_the_squad_and_turns_it()
    {
        var plan = WiredDirector.Plan(Data, 8, WiredDifficulty.Normal, new Random(5), WiredSpecial.Treason);
        Assert.Single(Machines(plan), m => m.Role == WiredRole.Turncoat);
        Assert.Equal(WiredDirector.Turncoat, plan.Groups[0].Machines[0].Key);

        var world = AtWave(8, WiredSpecial.Treason, spawn: 300);
        var run = world.Wired!;
        var turncoat = run.Foes.Single(f => f.Plan.Role == WiredRole.Turncoat).Machine;
        Assert.Equal(("COMMANDER", "L_MULTI_LAUNCHER5_R_TREACHERY"), (turncoat.Spec.SubType, turncoat.WeaponCombo));

        // Without a squad it marches with the others.
        Assert.IsType<AttackMoveOrder>(turncoat.Order);

        // Sturdy enough to last until an orb arrives, as the launcher fires too.
        var escort = Enrolled(world, Tough(world, world.Nav.NearestFreePoint(turncoat.Position + new Vector2(0, 40), crossesWater: false)!.Value, Race.Red));
        turncoat.Order = new IdleOrder();
        for (var i = 0; i < 20 * 6 && turncoat.Order is not AttackOrder; i++)
            world.Step();
        Assert.Equal(escort.Id, Assert.IsType<AttackOrder>(turncoat.Order).Target);

        for (var i = 0; i < 20 * 60 && escort.Race == Race.Red && !escort.IsDead; i++)
        {
            turncoat.HitPoints = turncoat.MaxHitPoints;
            world.Step();
        }
        Assert.False(escort.IsDead);
        Assert.Equal(run.Enemy, escort.Race);

        // What it turned fights on for the wave.
        Run(world, WiredBalance.OrdersEvery + 0.5f);
        Assert.Contains(run.Foes, f => f.Machine == escort);
    }

    [Fact]
    public void An_air_raid_is_gliders_and_nothing_else()
    {
        for (var wave = 9; wave <= 40; wave++)
        {
            if (WiredDirector.IsBossWave(wave))
                continue;
            var machines = Machines(WiredDirector.Plan(Data, wave, WiredDifficulty.Normal, new Random(wave), WiredSpecial.AirRaid));

            Assert.True(machines.Count >= WiredBalance.FewestRaiders);
            Assert.All(machines, m => Assert.Equal("BOSS", m.Key.SubType));
        }
    }

    [Fact]
    public void A_siege_stays_far_off_and_shells_the_pod()
    {
        var plan = WiredDirector.Plan(Data, 11, WiredDifficulty.Normal, new Random(5), WiredSpecial.Siege);
        Assert.All(Machines(plan), m => Assert.Equal((WiredRole.Besieger, WiredDirector.Besieger), (m.Role, m.Key)));
        Assert.True(Machines(plan).Count >= WiredBalance.FewestBesiegers);
        Assert.All(Machines(WiredDirector.Plan(Data, 27, WiredDifficulty.Normal, new Random(5), WiredSpecial.Siege)), m => Assert.Equal(WiredDirector.BigBesieger, m.Key));

        var world = AtWave(11, WiredSpecial.Siege, spawn: 300);
        var run = world.Wired!;
        var pod = Pod(world);
        var besieger = run.Foes[0].Machine;
        Assert.Equal(pod.Id, Assert.IsType<AttackOrder>(besieger.Order).Target);
        var durability = pod.HitPoints + pod.Armour;

        Run(world, 80);

        Assert.True(pod.HitPoints + pod.Armour < durability);
        Assert.True(besieger.DistanceTo(pod) > 60, $"{besieger.DistanceTo(pod)} m");
    }

    [Fact]
    public void An_ion_storm_announces_each_strike_before_it_comes_down()
    {
        var world = AtWave(12, WiredSpecial.IonStorm);
        var run = world.Wired!;
        run.Groups.Clear();
        run.Groups.Add(new WiredGroup(100_000, [new WiredFoePlan(WiredDirector.Spy)]));
        foreach (var foe in run.Foes)
            Kill(world, foe.Machine);

        var warnings = new List<(float At, Vector2 Where)>();
        var strikes = new List<float>();
        for (var i = 0; i < 20 * 60; i++)
        {
            var before = world.Blasts.Count;
            world.Step();
            foreach (var warning in world.Events.Where(e => e.Kind == GameEventKind.WiredIonWarning))
            {
                warnings.Add((world.Time, warning.Position));
                var strike = Assert.Single(run.Strikes);
                Assert.Equal((WiredBalance.IonRadius, warning.Position), (strike.Radius, strike.Position));
                Assert.Equal(world.Time + WiredBalance.IonWarning, strike.At, 0.1f);
            }
            if (world.Blasts.Count > before)
                strikes.Add(world.Time);
        }

        Assert.True(warnings.Count >= 2, $"{warnings.Count} warnings");
        Assert.Equal(WiredBalance.IonEvery, warnings[1].At - warnings[0].At, 0.1f);
        Assert.Equal(WiredBalance.IonWarning, strikes[0] - warnings[0].At, 0.1f);
        Assert.Equal(WiredBalance.IonEvery - WiredBalance.IonWarning, warnings[0].At - run.WaveStarted, 0.2f);
    }

    [Fact]
    public void Strikes_not_yet_down_end_with_the_wave()
    {
        var world = AtWave(12, WiredSpecial.IonStorm);
        var run = world.Wired!;
        run.Strikes.Add(new WiredStrike(Hero(world).Position, WiredBalance.IonRadius, world.Time + 100));

        for (var i = 0; i < 20 * 120 && run.Phase == WiredPhase.Wave; i++)
        {
            foreach (var foe in run.Foes)
                Kill(world, foe.Machine);
            world.Step();
        }

        Assert.Empty(run.Strikes);
    }

    [Fact]
    public void Abilities_and_special_waves_replay_alike()
    {
        Assert.Equal(Played(), Played());

        static (ulong, int) Played()
        {
            var world = AtWave(12, WiredSpecial.IonStorm, seed: 11);
            Wear(world, "minefield", WiredSlot.Ability(0));
            Wear(world, "thrusters", WiredSlot.Module(0));
            Chip(world, "nano", 0);
            Chip(world, "tough", 1);
            Run(world, 5);
            world.Issue(Ability(Away(world, 30)));
            world.Issue(new WiredDashCommand(Race.Red, Vector2.UnitX));
            Run(world, 40);
            return (world.Checksum(), world.Wired!.EnemiesLeft);
        }
    }

    // ---- what the review found ----------------------------------------------------------------------

    [Fact]
    public void A_hero_has_only_so_many_mines_on_the_ground()
    {
        var world = NewRun();
        Wear(world, "minefield", WiredSlot.Ability(0));
        var aim = Away(world, 30);

        for (var cast = 0; cast < WiredBalance.MostMines / WiredBalance.MinesLaid; cast++)
        {
            Assert.Null(Do(world, Ability(aim)));
            Red(world).AbilityReadyAt.Clear();
        }
        Assert.Equal(WiredBalance.MostMines, world.LandMines.Count);

        Assert.Equal("too many mines on the ground", Do(world, Ability(aim)));
        Assert.Equal(WiredBalance.MostMines, world.LandMines.Count);

        // A field that has gone off makes room for the next, and the enemy's mines count for nothing.
        world.LandMines.RemoveRange(0, WiredBalance.MinesLaid);
        world.LandMines.Add(new LandMineState(9999, Race.Blue, aim));
        Assert.Null(Do(world, Ability(aim)));
    }

    [Theory]
    [InlineData(1, 1, 4)]
    [InlineData(1, 2, 5)]
    [InlineData(5, 1, 5)]
    [InlineData(10, 1, 6)]
    [InlineData(10, 2, 6)]
    [InlineData(15, 1, 6)]
    public void Squad_tactics_brings_the_next_size_forward_and_no_further_than_the_largest(int wave, int extra, int size) =>
        Assert.Equal(size, WiredBalance.SquadLimit(wave, extra));

    [Fact]
    public void With_squad_tactics_the_squad_still_stops_at_the_largest_size()
    {
        var world = AtWave(15);
        Chip(world, "tactics");
        var run = world.Wired!;
        foreach (var foe in run.Foes)
            Kill(world, foe.Machine);
        run.Groups.Clear();
        run.Bosses.ToList().ForEach(b => Kill(world, b.Machine));
        Run(world, 0.2f);
        Assert.Equal(WiredPhase.Intermission, run.Phase);
        world.Races[Race.Red].Bmu = 100000;
        Hero(world).Position = Pod(world).EntranceOutside!.Value;
        Assert.Null(Do(world, new WiredHangarCommand(Race.Red, true)));
        var key = new ResearchKey("AGGRESSOR", "GRUNT", 1, "R_BOLTER");

        for (var i = 0; i < WiredBalance.LargestSquad; i++)
            Assert.Null(Do(world, new WiredSquadBuyCommand(Race.Red, key)));

        Assert.Equal("the squad is full", Do(world, new WiredSquadBuyCommand(Race.Red, key)));
        Assert.Equal(WiredBalance.LargestSquad, world.Escorts(Race.Red).Count());
    }

    [Fact]
    public void The_chips_stay_with_an_escort_put_in_another_squadron()
    {
        var world = NewRun();
        var escort = Escort(world, Away(world, 20));
        Chip(world, "rally");
        escort.Order = new FollowOrder(Hero(world).Id);
        Assert.Equal(1.2f, WiredChips.Pace(world, escort), 0.001f);

        escort.Squad = 2;

        Assert.Equal(1.2f, WiredChips.Pace(world, escort), 0.001f);
    }

    [Fact]
    public void A_crate_holds_no_chip_that_is_still_to_be_chosen_from()
    {
        var world = NewRun(new WiredOptions { ChipPool = ["nano", "tough", "marksman"] });
        var state = Red(world);
        world.OfferChips(state);
        Assert.Equal(3, state.ChipChoice.Count);

        for (var crate = 0; crate < 300; crate++)
        {
            world.WiredPickups.Add(new WiredPickup(1000 + crate, Hero(world).Position, 5));
            world.Step();
            Assert.Empty(world.WiredPickups);
            Assert.DoesNotContain(state.Items, i => i.Kind == WiredKind.Chip);
            state.Items.RemoveAll(i => !state.Loadout.Holds(i.Id));
        }
    }

    [Fact]
    public void A_new_offer_of_chips_can_hold_those_of_the_one_left_unchosen()
    {
        var world = NewRun(new WiredOptions { ChipPool = ["nano", "tough", "marksman"] });
        var state = Red(world);

        world.OfferChips(state);
        world.OfferChips(state);

        Assert.Equal(3, state.ChipChoice.Count);
    }

    [Fact]
    public void A_turncoat_goes_on_to_the_next_escort()
    {
        var world = AtWave(8, WiredSpecial.Treason, spawn: 300);
        var run = world.Wired!;
        var turncoat = run.Foes.Single(f => f.Plan.Role == WiredRole.Turncoat).Machine;
        var door = Pod(world).EntranceOutside!.Value;
        var (first, second) = (Enrolled(world, Tough(world, Away(world, 20), Race.Red)), Enrolled(world, Tough(world, Away(world, 25), Race.Red)));
        bool AfterSecond() => turncoat.Order is AttackOrder attack && attack.Target == second.Id;
        void LookOver()
        {
            for (var i = 0; i < 20 * (WiredBalance.OrdersEvery + 1) && !AfterSecond(); i++)
            {
                turncoat.HitPoints = turncoat.MaxHitPoints;
                world.Step();
            }
        }

        // The first is lost to it, and it marches on as an attack ends.
        first.Race = run.Enemy;
        turncoat.Order = new AttackMoveOrder(door);
        LookOver();
        Assert.True(AfterSecond());

        // Nor does anything else keep it from the squad.
        turncoat.Order = new AttackOrder(Pod(world).Id, Then: door);
        LookOver();
        Assert.True(AfterSecond());

        // With no squad left it is a machine like the others.
        second.Race = run.Enemy;
        turncoat.Order = new AttackMoveOrder(door);
        LookOver();
        Assert.False(AfterSecond());
    }

    [Fact]
    public void A_healer_at_work_does_not_count_as_hanging()
    {
        var world = AtWave(1);
        var run = world.Wired!;
        var away = Pod(world).Position + new Vector2(0, 400);
        var patient = Tough(world, world.Nav.NearestFreePoint(away, crossesWater: false)!.Value);
        var healer = world.AddMachine(Race.Blue, world.Nav.NearestFreePoint(away + new Vector2(4, 0), crossesWater: false)!.Value, "ADMINISTRATOR", "OVERSEER", 2, "L_PULSE_RIFLE_R_SUPERCHARGE_ADVANCED");
        run.Groups.Clear();
        var foe = new WiredFoe(healer, new WiredFoePlan(healer.Key) { Healer = true }, huntsHero: false, world.Time);
        run.Foes.Add(foe);
        run.LastGroupAt = world.Time;
        patient.Order = new StandGroundOrder();
        healer.Order = new HealOrder(patient.Id);

        for (var i = 0; i < 20 * (WiredBalance.StandingTime + 20); i++)
        {
            // Only the healer is in question here: its patient is left where it is.
            patient.HitPoints = 1;
            foreach (var other in run.Foes.Where(f => f != foe))
                other.StirredAt = world.Time;
            world.Step();
            Assert.IsType<HealOrder>(healer.Order);
            if (foe.Fetched)
                break;
        }

        Assert.False(foe.Fetched);
        Assert.True(healer.DistanceTo(Pod(world)) > WiredBalance.PullDistance * 2);
    }

    // ---- what was left of the reviews ---------------------------------------------------------------

    /// <summary>Sends a machine into the pod and waits until it is in.</summary>
    private static void GoIn(GameWorld world, Machine machine)
    {
        Assert.Null(Do(world, new EnterGarrisonCommand(machine.Race, [machine.Id], Pod(world).Id)));
        for (var i = 0; i < 20 * 20 && machine.Inside is null; i++)
            world.Step();
        Assert.Equal(Pod(world).Id, machine.Inside);
    }

    [Fact]
    public void A_hero_in_the_pod_is_told_so_and_not_that_it_is_down()
    {
        var world = Quiet();
        Wear(world, "supercharge", WiredSlot.Ability(0));
        Wear(world, "thrusters", WiredSlot.Module(0));
        var hero = Hero(world);

        GoIn(world, hero);

        Assert.Equal("not from inside the pod", world.WhyNoAbility(Red(world), 0, hero.Position));
        Assert.Equal("not from inside the pod", Do(world, Ability(hero.Position)));
        Assert.Equal("not from inside the pod", Do(world, new WiredDashCommand(Race.Red, Vector2.UnitX)));
    }

    [Fact]
    public void A_hero_that_fell_is_still_told_it_is_down()
    {
        var world = Quiet();
        Wear(world, "supercharge", WiredSlot.Ability(0));
        Wear(world, "thrusters", WiredSlot.Module(0));

        Kill(world, Hero(world), Race.Blue);
        world.Step();

        Assert.Equal("the hero is down", world.WhyNoAbility(Red(world), 0, Vector2.Zero));
        Assert.Equal("the hero is down", Do(world, new WiredDashCommand(Race.Red, Vector2.UnitX)));
    }

    /// <summary>The driver's notes on a machine: none of them should be left once it has left the player's hands.</summary>
    private static void NoDriverLeft(GameWorld world, Machine machine)
    {
        Assert.False(machine.DrivenByPlayer);
        Assert.False(world.DriveInput.ContainsKey(machine.Id));
        Assert.False(world.PlayerAims.ContainsKey(machine.Id));
        Assert.False(world.PlayerWeapons.ContainsKey(machine.Id));
        Assert.False(world.PlayerHeads.ContainsKey(machine.Id));
        Assert.DoesNotContain(machine.Id, world.PlayerButtons);
    }

    private static DriveCommand Take(Machine machine, Vector2 aim) =>
        new(Race.Red, machine.Id, Driving: true, Forward: 1, Aim: aim, Weapons: 1, Head: 0.5f, Buttons: true);

    [Fact]
    public void A_turned_machine_the_player_drives_goes_back_out_of_the_players_hands_and_takes_its_orders()
    {
        var world = Quiet();
        var run = world.Wired!;
        Wear(world, "treachery", WiredSlot.Ability(0));
        var machine = Enlisted(world);
        Turn(world, machine);
        world.Step();
        world.Issue(Take(machine, Hero(world).Position));
        world.Step();
        Assert.True(machine.DrivenByPlayer);

        Run(world, WiredBalance.TreacheryTime);

        Assert.Equal(Race.Blue, machine.Race);
        NoDriverLeft(world, machine);
        Assert.Contains(run.Foes, f => f.Machine == machine);
        Assert.IsNotType<IdleOrder>(machine.Order);

        // The client's last word on it does not bring it back.
        world.Issue(new DriveCommand(Race.Red, machine.Id, Driving: false));
        world.Issue(Take(machine, Hero(world).Position));
        world.Step();
        NoDriverLeft(world, machine);
    }

    [Fact]
    public void A_driven_escort_turned_by_the_enemy_leaves_the_players_hands()
    {
        var world = Quiet();
        var escort = Escort(world, Away(world, 30));
        var traitor = world.AddMachine(Race.Blue, Away(world, 60), "ADMINISTRATOR", "COMMANDER", 4, "L_MULTI_LAUNCHER5_R_TREACHERY");
        traitor.Weapons.RemoveAll(w => !w.IsTreachery);
        world.Issue(new AttackCommand(Race.Blue, [traitor.Id], escort.Id));

        for (var i = 0; i < 20 * 30 && escort.Race == Race.Red; i++)
        {
            world.Issue(Take(escort, escort.Position));
            world.Step();
        }

        Assert.Equal(Race.Blue, escort.Race);
        NoDriverLeft(world, escort);
    }

    [Fact]
    public void Outside_a_run_a_turned_machine_keeps_its_driver_as_it_did()
    {
        var world = MatchSetup.Create(Data, "m_desert", "m_desert",
            new Dictionary<Race, Controller> { [Race.Red] = Controller.Player, [Race.Blue] = Controller.Player }, 7);
        var door = world.Constructions.First(c => c.Race == Race.Red && c.IsType("POD")).EntranceOutside!.Value;
        var driven = Grunt(world, world.Nav.NearestFreePoint(door + new Vector2(30, 30), crossesWater: false)!.Value, Race.Red);
        var traitor = world.AddMachine(Race.Blue, world.Nav.NearestFreePoint(door + new Vector2(60, 60), crossesWater: false)!.Value,
            "ADMINISTRATOR", "COMMANDER", 4, "L_MULTI_LAUNCHER5_R_TREACHERY");
        traitor.Weapons.RemoveAll(w => !w.IsTreachery);
        world.Issue(new AttackCommand(Race.Blue, [traitor.Id], driven.Id));

        for (var i = 0; i < 20 * 30 && driven.Race == Race.Red; i++)
        {
            world.Issue(new DriveCommand(Race.Red, driven.Id, Driving: true));
            world.Step();
        }

        Assert.Equal(Race.Blue, driven.Race);
        Assert.True(driven.DrivenByPlayer);
    }
}
