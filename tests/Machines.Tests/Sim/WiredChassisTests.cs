using System.Numerics;
using System.Reflection;
using Machines.Import.Model;
using Machines.Sim;
using Machines.Sim.Combat;
using Machines.Sim.Survival;
using Machines.Sim.World;

namespace Machines.Tests.Sim;

/// <summary>
/// The specials of Wired's own machines: Old Iron's plough and ram, the Harvester's harvest, the Widow's lurk, the
/// Colossus' anchor, the dash the Hornet and Old Iron have of their own, and the Marshal's field and rally. One rule each.
/// </summary>
public class WiredChassisTests
{
    private static readonly Lazy<GameData> DataLoader = new(() => GameData.Load(RepoPaths.OriginalDataFolder()));

    private static GameData Data => DataLoader.Value;

    /// <summary>A run in which no wave comes while a test lasts.</summary>
    private static GameWorld NewRun(int seed = 7)
    {
        var world = MatchSetup.Create(Data, "m_desert", "m_desert",
            new Dictionary<Race, Controller> { [Race.Red] = Controller.Player, [Race.Blue] = Controller.Ai }, seed,
            new MatchOptions { Wired = new WiredOptions() });
        world.Wired!.Countdown = 100_000;
        return world;
    }

    private static WiredState Red(GameWorld world) => world.Wired!.Heroes[Race.Red];

    private static Machine Hero(GameWorld world) => world.HeroOf(Race.Red)!;

    /// <returns>Why the command was refused, or null.</returns>
    private static string? Do(GameWorld world, Command command)
    {
        world.Issue(command);
        world.Step();
        return world.Rejected.FirstOrDefault()?.Reason;
    }

    /// <returns>All that happened in that time.</returns>
    private static List<GameEvent> Run(GameWorld world, float seconds, Action? everyTick = null)
    {
        var events = new List<GameEvent>();
        for (var i = 0; i < MathF.Round(seconds * GameWorld.TicksPerSecond); i++)
        {
            world.Step();
            events.AddRange(world.Events);
            everyTick?.Invoke();
        }
        return events;
    }

    /// <summary>Asks the world something it keeps to itself, or has it do it.</summary>
    private static object? Ask(GameWorld world, string method, params object?[] arguments) =>
        typeof(GameWorld).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(world, arguments);

    /// <summary>A place with open ground 70 m round it, well away from every building.</summary>
    private static Vector2 OpenGround(GameWorld world) => Enumerable.Range(0, 400)
        .Select(i => new Vector2(200 + i % 20 * 60, 200 + i / 20 * 60))
        .First(p => world.Constructions.All(c => Vector2.Distance(p, c.Position) > 200)
            && world.Nav.IsAreaFree((p - new Vector2(70, 70), p + new Vector2(70, 70))));

    /// <summary>Sets a machine down at a place, standing, with nothing on its mind.</summary>
    private static void Place(GameWorld world, Machine machine, Vector2 at, float facing = 0)
    {
        Ask(world, "StartAfresh", machine);
        machine.Doorway = null;
        machine.Position = at;
        machine.Facing = facing;
    }

    /// <summary>
    /// A run whose hero wears the chassis and the items, out on open ground away from the pod, facing east. A hero at the
    /// wheel with no input neither moves nor shoots.
    /// </summary>
    private static GameWorld OutOn(string chassis, bool driven = false, params (WiredSlot Slot, string Item)[] items)
    {
        var world = NewRun();
        world.Races[Race.Red].Bmu = 100000;
        Assert.Null(Do(world, new WiredHangarCommand(Race.Red, true)));
        var state = Red(world);
        if (state.Loadout.Chassis.Catalogue != chassis)
            Assert.Null(Do(world, new WiredChassisCommand(Race.Red, state.Add(WiredKind.Chassis, chassis, 0).Id)));
        foreach (var (slot, item) in items)
            Assert.Null(Do(world, new WiredEquipCommand(Race.Red, state.Add(WiredCatalogue.KindOf(item)!.Value, item, 0).Id, slot)));
        Assert.Null(Do(world, new WiredHangarCommand(Race.Red, false)));
        var hero = Hero(world);
        Place(world, hero, OpenGround(world));
        hero.DrivenByPlayer = driven;
        world.Step();
        return world;
    }

    /// <summary>A battle of the top bracket past its opening, each race's hero on the chassis given, all at the wheel.</summary>
    private static GameWorld Battle(string red, string blue, string green, string yellow)
    {
        var chassis = new Dictionary<Race, string> { [Race.Red] = red, [Race.Blue] = blue, [Race.Green] = green, [Race.Yellow] = yellow };
        var heroes = chassis.Select(c => new BattleHero(c.Key, c.Key.ToString(), c.Value, BattleBots.Loadout(c.Value, BattleCatalogue.Tier(c.Value)))).ToList();
        var world = Battles.Begun(new BattleOptions { Tier = 8, TeamA = [Race.Red, Race.Blue], TeamB = [Race.Green, Race.Yellow], Heroes = heroes });
        foreach (var (race, worn) in chassis)
            Assert.Equal(worn, world.Wired!.Heroes[race].Loadout.Chassis.Catalogue);
        return world;
    }

    /// <summary>An unarmed machine of the waves' race whose centre lies that far beyond the edge of another machine's.</summary>
    private static Machine Victim(GameWorld world, Machine beside, float metres, float angle = 0, string type = "AGGRESSOR", string subType = "GRUNT", int level = 1)
    {
        var victim = world.AddMachine(Race.Blue, Vector2.Zero, type, subType, level, null);
        victim.Position = beside.Position + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * (metres + victim.Radius);
        return victim;
    }

    /// <summary>Makes a machine of the hero's race one of its squad, as buying it would.</summary>
    private static Machine Escort(GameWorld world, Vector2 at, string type = "AGGRESSOR", string subType = "GRUNT", int level = 1)
    {
        var machine = world.AddMachine(Race.Red, at, type, subType, level, null);
        machine.Squad = WiredBalance.EscortSquad;
        Red(world).EscortIds.Add(machine.Id);
        return machine;
    }

    /// <returns>How much of its durability a hit takes off a target.</returns>
    private static int Drawn(GameWorld world, Actor target, int damage, Actor? shooter = null, int? blaster = null)
    {
        var before = target.HitPoints + target.Armour;
        world.Damage(target, damage, shooter?.Race ?? Race.Red, shooter, blaster: blaster);
        return before - target.HitPoints - target.Armour;
    }

    /// <returns>What the kill raised: a step clears the events, so they are taken at once.</returns>
    private static List<GameEvent> Kill(GameWorld world, Machine victim, Machine? killer)
    {
        var before = world.Events.Count;
        world.Damage(victim, 1_000_000, killer?.Race ?? Race.Red, killer);
        Assert.True(victim.IsDead);
        return world.Events.Skip(before).ToList();
    }

    private static bool Tuned(WeaponMount weapon) => !ReferenceEquals(weapon.Spec, weapon.Base);

    // ---- Old Iron: the plough ---------------------------------------------------------------------

    [Fact]
    public void Old_iron_takes_a_hit_from_the_front_at_65_per_cent_and_one_from_behind_or_the_side_whole()
    {
        var world = NewRun();
        var at = OpenGround(world);
        var iron = world.AddMachine(Race.Blue, at, "AGGRESSOR", "GRUNT", 5, null);
        iron.Facing = 0;
        Machine Shooter(float degrees) =>
            world.AddMachine(Race.Red, at + new Vector2(MathF.Cos(degrees * MathF.PI / 180), MathF.Sin(degrees * MathF.PI / 180)) * 20, "AGGRESSOR", "GRUNT", 1, null);

        Assert.Equal(65, Drawn(world, iron, 100, Shooter(0)));
        Assert.Equal(65, Drawn(world, iron, 100, Shooter(55)));
        Assert.Equal(100, Drawn(world, iron, 100, Shooter(180)));
        Assert.Equal(100, Drawn(world, iron, 100, Shooter(90)));
    }

    [Fact]
    public void A_hit_that_comes_from_nobody_is_not_ploughed_aside_and_a_blast_from_one_ahead_is()
    {
        var world = NewRun();
        var at = OpenGround(world);
        var iron = world.AddMachine(Race.Blue, at, "AGGRESSOR", "GRUNT", 5, null);
        iron.Facing = 0;
        var ahead = world.AddMachine(Race.Red, at + new Vector2(20, 0), "AGGRESSOR", "GRUNT", 1, null);
        var gone = world.AddMachine(Race.Red, at + new Vector2(20, 5), "AGGRESSOR", "GRUNT", 1, null);
        world.Damage(gone, 1_000_000, Race.Blue);

        // A mine or a strike from the sky, and a round whose shooter has fallen since.
        Assert.Equal(100, Drawn(world, iron, 100));
        Assert.Equal(100, Drawn(world, iron, 100, gone));
        // A blast knows who set it off.
        Assert.Equal(65, Drawn(world, iron, 100, blaster: ahead.Id));
    }

    [Fact]
    public void A_body_without_a_plough_takes_the_hit_whole_and_draws_no_chance_for_it()
    {
        // Three worlds alike: one hits a Sarge from the front, one an Old Iron, one nothing.
        var worlds = Enumerable.Range(0, 3).Select(_ =>
        {
            var world = NewRun();
            var at = OpenGround(world);
            var sarge = world.AddMachine(Race.Blue, at, "AGGRESSOR", "GRUNT", 4, null);
            var iron = world.AddMachine(Race.Blue, at + new Vector2(0, 40), "AGGRESSOR", "GRUNT", 5, null);
            sarge.Facing = iron.Facing = 0;
            var atSarge = world.AddMachine(Race.Red, at + new Vector2(20, 0), "AGGRESSOR", "GRUNT", 1, null);
            var atIron = world.AddMachine(Race.Red, at + new Vector2(20, 40), "AGGRESSOR", "GRUNT", 1, null);
            return (World: world, Sarge: sarge, Iron: iron, AtSarge: atSarge, AtIron: atIron);
        }).ToList();
        var (plain, ploughed, untouched) = (worlds[0], worlds[1], worlds[2]);

        Assert.Equal(37, Drawn(plain.World, plain.Sarge, 37, plain.AtSarge));
        Assert.InRange(Drawn(ploughed.World, ploughed.Iron, 37, ploughed.AtIron), 24, 25);

        var next = worlds.Select(w => w.World.Random.Next()).ToList();
        Assert.Equal(next[2], next[0]);
        Assert.NotEqual(next[2], next[1]);
    }

    // ---- Old Iron: the ram ------------------------------------------------------------------------

    [Fact]
    public void A_dash_of_old_iron_rams_an_enemy_once_for_60_throws_it_on_and_stops_it()
    {
        var world = OutOn("grunt5", driven: true);
        var hero = Hero(world);
        var victim = Victim(world, hero, 10, type: "AGGRESSOR", subType: "NINJA", level: 5);
        world.Issue(new MoveCommand(Race.Blue, [victim.Id], victim.Position + new Vector2(0, 60)));
        world.Step();
        Assert.IsType<MoveOrder>(victim.Order);
        var durability = victim.HitPoints + victim.Armour;

        world.Issue(new WiredDashCommand(Race.Red, Vector2.UnitX));
        var events = new List<GameEvent>();
        for (var i = 0; i < 20 && !events.Exists(e => e.Kind == GameEventKind.WiredRammed); i++)
        {
            var x = victim.Position.X;
            world.Step();
            events.AddRange(world.Events);
            if (!events.Exists(e => e.Kind == GameEventKind.WiredRammed))
                continue;
            Assert.Equal(WiredBalance.RamDamage, durability - victim.HitPoints - victim.Armour);
            Assert.True(victim.Position.X - x >= WiredBalance.RamPush, $"thrown {victim.Position.X - x} m");
            Assert.IsType<IdleOrder>(victim.Order);
        }
        var rammed = Assert.Single(events, e => e.Kind == GameEventKind.WiredRammed);
        Assert.Equal((victim.Id, hero.Id), (rammed.Actor, rammed.Other));

        // Once a dash: the rest of it passes the victim by.
        events.AddRange(Run(world, 1));
        Assert.Single(events, e => e.Kind == GameEventKind.WiredRammed);

        // The next dash rams it again.
        Run(world, 10);
        Place(world, hero, victim.Position - new Vector2(10 + victim.Radius, 0));
        world.Issue(new WiredDashCommand(Race.Red, Vector2.UnitX));
        Assert.Single(Run(world, 1), e => e.Kind == GameEventKind.WiredRammed && e.Actor == victim.Id);
    }

    [Fact]
    public void A_ram_passes_a_glider_an_ally_and_a_building_by()
    {
        var world = OutOn("grunt5", driven: true);
        var hero = Hero(world);
        var glider = Victim(world, hero, 6, 0.3f, "ADMINISTRATOR", "BOSS", 1);
        Assert.Equal(Locomotion.Glider, glider.Stats.Locomotion);
        var ally = Escort(world, hero.Position + new Vector2(9, -2));
        var enemy = Victim(world, hero, 9, -0.2f);
        var building = world.AddConstruction(Race.Blue, hero.Position + new Vector2(40, 0), "SMELTER", null, 1, 0, complete: true);
        var whole = new[] { (Actor)glider, ally, building }.Select(a => a.HitPoints + a.Armour).ToList();

        var events = new List<GameEvent>();
        world.Issue(new WiredDashCommand(Race.Red, Vector2.UnitX));
        events.AddRange(Run(world, 1));

        var rammed = events.Where(e => e.Kind == GameEventKind.WiredRammed).Select(e => e.Actor).ToList();
        Assert.Equal([enemy.Id], rammed);
        Assert.Equal(whole, new[] { (Actor)glider, ally, building }.Select(a => a.HitPoints + a.Armour));
    }

    [Fact]
    public void A_rammed_player_hero_is_thrown_along_and_keeps_its_driver()
    {
        var world = Battle("grunt5", "reaper5", "reaper5", "reaper5");
        var at = OpenGround(world);
        var ram = Battles.Hero(world, Race.Red);
        var driven = Battles.Hero(world, Race.Green);
        Place(world, ram, at);
        Place(world, driven, at + new Vector2(10 + driven.Radius, 0));
        Place(world, Battles.Hero(world, Race.Blue), at + new Vector2(0, -50));
        Place(world, Battles.Hero(world, Race.Yellow), at + new Vector2(0, 50));
        world.Issue(new DriveCommand(Race.Green, driven.Id, true, Aim: at + new Vector2(0, 60), Weapons: 0));
        world.Step();
        var x = driven.Position.X;

        world.Issue(new WiredDashCommand(Race.Red, Vector2.UnitX));
        var events = Run(world, 1);

        Assert.Contains(events, e => e.Kind == GameEventKind.WiredRammed && e.Actor == driven.Id && e.Other == ram.Id);
        Assert.True(driven.Position.X - x >= WiredBalance.RamPush);
        Assert.True(driven.DrivenByPlayer);
        Assert.True(world.DriveInput.ContainsKey(driven.Id));
        Assert.True(world.PlayerAims.ContainsKey(driven.Id));
    }

    // ---- the Harvester ----------------------------------------------------------------------------

    [Fact]
    public void A_harvester_s_own_kill_within_15_m_gives_it_6_per_cent_of_its_armour_and_one_further_off_nothing()
    {
        var world = OutOn("reaper5", driven: true);
        var hero = Hero(world);
        var gained = (int)MathF.Round(hero.MaxArmour * WiredBalance.HarvestArmour);
        Assert.True(gained > 0);
        hero.Armour = 0;

        Assert.DoesNotContain(Kill(world, Victim(world, hero, 16, MathF.PI / 2), hero), e => e.Kind == GameEventKind.WiredHarvest);
        Assert.Equal(0, hero.Armour);

        var near = Victim(world, hero, 14);
        var harvest = Assert.Single(Kill(world, near, hero), e => e.Kind == GameEventKind.WiredHarvest);
        Assert.Equal(gained, hero.Armour);
        Assert.Equal((hero.Id, near.Id, gained.ToString()), (harvest.Actor, harvest.Other, harvest.Detail));

        // No more than all of it.
        hero.Armour = hero.MaxArmour - 1;
        Kill(world, Victim(world, hero, 14, MathF.PI), hero);
        Assert.Equal(hero.MaxArmour, hero.Armour);
    }

    [Fact]
    public void Only_the_harvester_s_own_kills_count_and_an_escort_harvester_gets_the_armour_only()
    {
        var world = OutOn("reaper5", driven: true);
        var hero = Hero(world);
        var state = Red(world);
        hero.Armour = 0;

        var grunt = Escort(world, hero.Position + new Vector2(0, -12));
        Kill(world, Victim(world, hero, 8), grunt);
        Assert.Equal(0, hero.Armour);
        Assert.Empty(state.Harvests);

        var reaper = Escort(world, hero.Position + new Vector2(0, 30), "AGGRESSOR", "ASSASSIN", 5);
        reaper.Armour = 0;
        var events = Kill(world, Victim(world, reaper, 8, MathF.PI / 2), reaper);
        Assert.Equal((int)MathF.Round(reaper.MaxArmour * WiredBalance.HarvestArmour), reaper.Armour);
        Assert.Contains(events, e => e.Kind == GameEventKind.WiredHarvest && e.Actor == reaper.Id);
        Assert.Empty(state.Harvests);
    }

    [Fact]
    public void A_hero_s_harvest_pulls_in_the_wrecks_within_15_m_of_the_kill()
    {
        var world = OutOn("reaper5", driven: true);
        var hero = Hero(world);
        var state = Red(world);
        var victim = Victim(world, hero, 14);
        var beside = world.AddMachine(Race.Blue, victim.Position + new Vector2(0, 10), "AGGRESSOR", "GRUNT", 1, null);
        var beyond = world.AddMachine(Race.Blue, victim.Position + new Vector2(0, -20), "AGGRESSOR", "GRUNT", 1, null);
        Kill(world, beside, null);
        Kill(world, beyond, null);
        world.Step();
        Assert.Equal(2, world.Debris.Count);
        var collected = state.Stats.WrecksCollected;

        Kill(world, victim, hero);
        world.Step();

        var paid = world.Events.Where(e => e.Kind == GameEventKind.WiredWreckCollected).ToList();
        Assert.Equal(2, paid.Count);
        Assert.All(paid, e => Assert.Equal(hero.Id, e.Actor));
        Assert.All(paid, e => Assert.True(int.Parse(e.Detail!) > 0));
        Assert.Equal(collected + 2, state.Stats.WrecksCollected);
        Assert.Equal(beyond.Position, Assert.Single(world.Debris).Position);
        Assert.Empty(state.Harvests);
    }

    [Fact]
    public void A_harvest_waits_for_its_wreck_and_is_paid_on_the_next_tick()
    {
        // A kill made after the fighting of a tick (as a ram's is) has no wreck lying yet.
        var world = OutOn("reaper5", driven: true);
        var hero = Hero(world);
        var state = Red(world);
        var victim = Victim(world, hero, 14);

        Kill(world, victim, hero);
        Assert.Equal([victim.Position], state.Harvests);
        Assert.Empty(world.Debris);

        world.Step();
        Assert.Contains(world.Events, e => e.Kind == GameEventKind.WiredWreckCollected && e.Actor == hero.Id);
        Assert.Empty(world.Debris);
        Assert.Empty(state.Harvests);
    }

    [Fact]
    public void A_harvester_in_a_battle_gets_the_armour_and_nothing_else()
    {
        var world = Battle("reaper5", "reaper5", "reaper5", "reaper5");
        var at = OpenGround(world);
        var harvester = Battles.Hero(world, Race.Red);
        var victim = Battles.Hero(world, Race.Green);
        Place(world, harvester, at);
        Place(world, victim, at + new Vector2(14 + victim.Radius, 0));
        harvester.Armour = 0;

        Kill(world, victim, harvester);
        world.Step();

        Assert.Equal((int)MathF.Round(harvester.MaxArmour * WiredBalance.HarvestArmour), harvester.Armour);
        Assert.Empty(world.Wired!.Heroes[Race.Red].Harvests);
        Assert.DoesNotContain(world.Events, e => e.Kind == GameEventKind.WiredWreckCollected);
    }

    // ---- the Widow --------------------------------------------------------------------------------

    /// <summary>A Widow of the hero's race at the wheel, standing with its weapons held.</summary>
    private static Machine Widow(GameWorld world, Vector2 at)
    {
        var widow = world.AddMachine(Race.Red, at, "AGGRESSOR", "ASSASSIN", 6, "L_MULTI_LAUNCHER1_R_PULSE_CANNON");
        Assert.Equal(2, widow.Weapons.Count);
        Assert.All(widow.Weapons, w => Assert.True(w.DealsDamage));
        Drive(world, widow, at);
        return widow;
    }

    private static void Drive(GameWorld world, Machine widow, Vector2 at, float forward = 0, float turn = 0, int weapons = 0) =>
        world.Issue(new DriveCommand(Race.Red, widow.Id, true, Forward: forward, Turn: turn, Aim: at + new Vector2(80, 0), Weapons: weapons));

    private static void Lurking(Machine widow) => Assert.All(widow.Weapons, w =>
    {
        Assert.Equal(w.Base.Range * WiredBalance.LurkRange, w.Spec.Range);
        Assert.Equal(w.Base.Damage * WiredBalance.LurkDamage, w.Spec.Damage);
    });

    [Fact]
    public void A_widow_lurks_once_it_has_stood_still_for_one_and_a_half_seconds()
    {
        var world = NewRun();
        var widow = Widow(world, OpenGround(world));

        Run(world, 1.4f);
        Assert.DoesNotContain(widow.Weapons, Tuned);
        Run(world, 0.2f);
        Lurking(widow);
    }

    [Fact]
    public void The_lurk_carries_the_first_burst_and_what_is_fired_after_it_flies_as_made()
    {
        var world = NewRun();
        var at = OpenGround(world);
        var widow = Widow(world, at);
        Run(world, 1.6f);
        Lurking(widow);

        Drive(world, widow, at, weapons: -1);
        var rounds = new List<(string Weapon, bool Tuned)>();
        for (var i = 0; i < 15 * GameWorld.TicksPerSecond; i++)
        {
            var tuned = widow.Weapons.ToDictionary(w => w.Base.TypeName, Tuned);
            world.Step();
            rounds.AddRange(world.Events.Where(e => e.Kind == GameEventKind.Fired && e.Actor == widow.Id).Select(e => (e.Detail!, tuned[e.Detail!])));
        }

        foreach (var weapon in widow.Weapons)
        {
            var fired = rounds.Where(r => r.Weapon == weapon.Base.TypeName).Select(r => r.Tuned).ToList();
            var first = fired.TakeWhile(t => t).Count();
            if (weapon.Base.BurstTiming != "VARIABLE")
                Assert.Equal(Math.Max(1, weapon.Base.BurstRounds), first);
            Assert.True(first >= 1, $"{weapon.Base.TypeName} fired nothing lurking");
            Assert.True(fired.Count > first, $"{weapon.Base.TypeName} fired no second burst");
            Assert.DoesNotContain(true, fired.Skip(first));
        }
        // Standing on where it fired from, it does not lurk again.
        Assert.DoesNotContain(widow.Weapons, Tuned);
        Assert.True(world.Wired!.Bodies[widow.Id].LurkSpent);
    }

    [Fact]
    public void A_widow_turning_on_the_spot_still_lurks()
    {
        var world = NewRun();
        var at = OpenGround(world);
        var widow = Widow(world, at);
        Run(world, 1.6f);
        var facing = widow.Facing;

        Drive(world, widow, at, turn: 1);
        Run(world, 1);

        Assert.NotEqual(facing, widow.Facing);
        Assert.True(Vector2.Distance(at, widow.Position) <= WiredBalance.LurkDrift);
        Lurking(widow);
    }

    [Fact]
    public void A_widow_that_fired_lurks_again_once_it_has_moved_and_stood_still_anew()
    {
        var world = NewRun();
        var at = OpenGround(world);
        var widow = Widow(world, at);
        Run(world, 1.6f);
        Drive(world, widow, at, weapons: -1);
        Run(world, 6);
        Assert.True(world.Wired!.Bodies[widow.Id].LurkSpent);

        Drive(world, widow, at, forward: 1);
        Run(world, 1);
        Assert.True(Vector2.Distance(at, widow.Position) > 1);
        Drive(world, widow, at);
        Run(world, 3);

        Assert.False(world.Wired.Bodies[widow.Id].LurkSpent);
        Lurking(widow);
    }

    [Fact]
    public void The_hud_fills_the_lurk_light_of_a_widow_hero()
    {
        var world = OutOn("widow6", driven: true);
        var hero = Hero(world);
        Assert.Contains(hero.Weapons, w => w.DealsDamage);

        var view = world.SpecialView(Race.Red)!;
        Assert.Equal(("Lurk", "Standing"), (view.Name, view.Phase));
        Assert.True(view.SecondsLeft > 1);
        Assert.True(view.Share < 1);
        Run(world, 1.6f);

        view = world.SpecialView(Race.Red)!;
        Assert.Equal(("Lurk", "Lurking", 1f, 0f), (view.Name, view.Phase, view.Share, view.SecondsLeft));

        world.Issue(new DriveCommand(Race.Red, hero.Id, true, Aim: hero.Position + new Vector2(80, 0), Weapons: -1));
        Run(world, 1);
        view = world.SpecialView(Race.Red)!;
        Assert.Equal(("Lurk", "Spent", 0f), (view.Name, view.Phase, view.Share));
    }

    [Theory]
    [InlineData("widow6")]
    [InlineData("reaper5")]
    public void A_special_without_a_key_never_shows_the_key_ready_and_says_what_the_key_says(string chassis)
    {
        var world = OutOn(chassis, driven: true);
        var state = Red(world);
        Run(world, 1.6f);

        var view = world.SpecialView(Race.Red)!;
        Assert.Equal((false, "this chassis has nothing to use"), (view.Ready, view.Why));
        Assert.Equal(view.Why, world.WhyNoSpecial(state));
        Assert.Equal(view.Why, Do(world, new WiredSpecialCommand(Race.Red)));
    }

    // ---- the Colossus -----------------------------------------------------------------------------

    private static readonly (WiredSlot, string)[] Guns = [(WiredSlot.Weapon(Mounting.Left), "BOLTER"), (WiredSlot.Weapon(Mounting.Top), "HEAVY_BOLTER1")];

    /// <summary>A Colossus hero with two guns, on its own.</summary>
    private static GameWorld Colossus(bool driven = false, params (WiredSlot, string)[] more) => OutOn("ballista5", driven, [.. Guns, .. more]);

    private static void Anchored(GameWorld world)
    {
        Assert.Null(Do(world, new WiredSpecialCommand(Race.Red)));
        Run(world, WiredBalance.AnchorTime + 0.1f);
        Assert.Equal(AnchorPhase.Anchored, world.AnchorPhaseOf(Hero(world)));
    }

    [Fact]
    public void A_colossus_locks_its_tracks_in_two_seconds_and_goes_nowhere_meanwhile_on_orders()
    {
        var world = Colossus();
        var hero = Hero(world);
        var at = hero.Position;

        Assert.Null(Do(world, new WiredSpecialCommand(Race.Red)));
        Assert.Contains(world.Events, e => e.Kind == GameEventKind.WiredAnchoring && e.Actor == hero.Id);
        var view = world.SpecialView(Race.Red)!;
        Assert.Equal(("Anchor", "Locking", true), (view.Name, view.Phase, view.Ready));
        world.Issue(new MoveCommand(Race.Red, [hero.Id], at + new Vector2(30, 0)));
        var events = Run(world, 1.8f);
        Assert.Equal(AnchorPhase.Locking, world.AnchorPhaseOf(hero));
        Assert.DoesNotContain(events, e => e.Kind == GameEventKind.WiredAnchored);

        events = Run(world, 0.3f);
        Assert.Equal(AnchorPhase.Anchored, world.AnchorPhaseOf(hero));
        Assert.Single(events, e => e.Kind == GameEventKind.WiredAnchored && e.Actor == hero.Id);
        Assert.True(Vector2.Distance(at, hero.Position) < 0.01f);
        Assert.IsType<MoveOrder>(hero.Order);
    }

    [Fact]
    public void A_colossus_at_the_wheel_turns_but_goes_neither_forward_nor_sideways_while_its_tracks_lock()
    {
        var world = Colossus(driven: true);
        var hero = Hero(world);
        var (at, facing) = (hero.Position, hero.Facing);

        Assert.Null(Do(world, new WiredSpecialCommand(Race.Red)));
        world.Issue(new DriveCommand(Race.Red, hero.Id, true, Forward: 1, Turn: 1, Strafe: 1));
        Run(world, 2.5f);

        Assert.True(Vector2.Distance(at, hero.Position) < 0.01f);
        Assert.NotEqual(facing, hero.Facing);
    }

    [Fact]
    public void Anchored_its_guns_reach_further_and_reload_sooner_and_it_takes_two_thirds_while_its_armour_holds()
    {
        var world = Colossus();
        var hero = Hero(world);
        Assert.Null(Do(world, new WiredSpecialCommand(Race.Red)));

        // Not while the tracks are still locking.
        Assert.DoesNotContain(hero.Weapons, Tuned);
        Assert.Equal(90, Drawn(world, hero, 90));

        Run(world, WiredBalance.AnchorTime + 0.1f);
        Assert.All(hero.Weapons, w =>
        {
            Assert.Equal(w.Base.Range * WiredBalance.AnchorRange, w.Spec.Range);
            Assert.Equal(w.Base.ReloadTime * WiredBalance.AnchorReload, w.Spec.ReloadTime);
            Assert.Equal(w.Base.Damage, w.Spec.Damage);
        });
        Assert.Equal(60, Drawn(world, hero, 90));

        hero.Armour = 0;
        Assert.Equal(90, Drawn(world, hero, 90));
    }

    [Fact]
    public void Released_a_colossus_stands_two_seconds_more_and_its_guns_are_as_made_at_once()
    {
        var world = Colossus();
        var hero = Hero(world);
        var at = hero.Position;
        Anchored(world);

        Assert.Null(Do(world, new WiredSpecialCommand(Race.Red, false)));
        Assert.Contains(world.Events, e => e.Kind == GameEventKind.WiredReleasing && e.Actor == hero.Id);
        Assert.Equal(AnchorPhase.Releasing, world.AnchorPhaseOf(hero));
        Assert.All(hero.Weapons, w => Assert.Same(w.Base, w.Spec));
        Assert.Equal(90, Drawn(world, hero, 90));

        var events = Run(world, 1.8f);
        Assert.Equal(AnchorPhase.Releasing, world.AnchorPhaseOf(hero));
        events.AddRange(Run(world, 0.3f));
        Assert.Equal(AnchorPhase.Free, world.AnchorPhaseOf(hero));
        Assert.Single(events, e => e.Kind == GameEventKind.WiredReleased && e.Actor == hero.Id);
        Assert.All(hero.Weapons, w => Assert.Same(w.Base, w.Spec));
        Assert.True(Vector2.Distance(at, hero.Position) < 0.01f);
    }

    [Fact]
    public void Let_go_while_locking_a_colossus_is_free_at_once()
    {
        var world = Colossus();
        var hero = Hero(world);
        Assert.Null(Do(world, new WiredSpecialCommand(Race.Red)));
        Run(world, 1);

        Assert.Null(Do(world, new WiredSpecialCommand(Race.Red, false)));
        Assert.Equal(AnchorPhase.Free, world.AnchorPhaseOf(hero));
        Assert.Contains(world.Events, e => e.Kind == GameEventKind.WiredReleased);

        Assert.DoesNotContain(Run(world, 2), e => e.Kind == GameEventKind.WiredAnchored);
        Assert.DoesNotContain(hero.Weapons, Tuned);
    }

    [Fact]
    public void Asked_again_for_what_it_does_a_colossus_does_nothing_and_releasing_it_cannot_anchor()
    {
        var world = Colossus();
        var hero = Hero(world);
        Assert.Null(Do(world, new WiredSpecialCommand(Race.Red)));
        Run(world, 1);

        // A second "on" does not start the locking over.
        Assert.Null(Do(world, new WiredSpecialCommand(Race.Red)));
        Assert.DoesNotContain(world.Events, e => e.Kind == GameEventKind.WiredAnchoring);
        Run(world, 1);
        Assert.Equal(AnchorPhase.Anchored, world.AnchorPhaseOf(hero));
        Assert.Null(Do(world, new WiredSpecialCommand(Race.Red)));
        Assert.Equal(AnchorPhase.Anchored, world.AnchorPhaseOf(hero));

        Assert.Null(Do(world, new WiredSpecialCommand(Race.Red, false)));
        Assert.Equal("the tracks are being released", Do(world, new WiredSpecialCommand(Race.Red)));
        Assert.Null(Do(world, new WiredSpecialCommand(Race.Red, false)));
        Assert.Equal(AnchorPhase.Releasing, world.AnchorPhaseOf(hero));
    }

    [Fact]
    public void A_colossus_with_thrusters_cannot_dash_until_its_tracks_are_free()
    {
        var world = Colossus(false, (WiredSlot.Module(0), "thrusters"));
        var state = Red(world);
        Assert.Null(Do(world, new WiredSpecialCommand(Race.Red)));

        Assert.Equal("the Colossus is anchored", Do(world, new WiredDashCommand(Race.Red, Vector2.UnitX)));
        Run(world, WiredBalance.AnchorTime + 0.1f);
        Assert.Equal("the Colossus is anchored", Do(world, new WiredDashCommand(Race.Red, Vector2.UnitX)));
        Assert.Null(Do(world, new WiredSpecialCommand(Race.Red, false)));
        Assert.Equal("the Colossus is anchored", world.WhyNoDash(state));

        Run(world, WiredBalance.AnchorTime + 0.1f);
        Assert.Null(Do(world, new WiredDashCommand(Race.Red, Vector2.UnitX)));
    }

    [Fact]
    public void An_order_given_an_anchored_colossus_waits_and_is_carried_out_once_it_is_free()
    {
        var world = Colossus();
        var hero = Hero(world);
        var at = hero.Position;
        var target = at + new Vector2(20, 0);
        Anchored(world);

        world.Issue(new MoveCommand(Race.Red, [hero.Id], target));
        Run(world, 3);
        Assert.True(Vector2.Distance(at, hero.Position) < 0.01f);
        Assert.IsType<MoveOrder>(hero.Order);

        Assert.Null(Do(world, new WiredSpecialCommand(Race.Red, false)));
        Run(world, WiredBalance.AnchorTime - 0.1f);
        Assert.Equal(AnchorPhase.Releasing, world.AnchorPhaseOf(hero));
        Assert.True(Vector2.Distance(at, hero.Position) < 0.01f);

        Run(world, 10);
        Assert.Equal(AnchorPhase.Free, world.AnchorPhaseOf(hero));
        Assert.True(Vector2.Distance(target, hero.Position) < 5, $"{Vector2.Distance(target, hero.Position)} m short");
    }

    [Fact]
    public void An_anchored_colossus_is_not_refitted_for_its_tuned_guns_and_a_refit_is_tuned_at_the_next_tick()
    {
        var world = Colossus();
        var hero = Hero(world);
        var state = Red(world);
        Anchored(world);
        var (refits, mounts) = (hero.Refits, hero.Weapons.ToList());

        Run(world, 1);
        Ask(world, "Wear", state, state.Loadout);
        Assert.Equal(refits, hero.Refits);
        Assert.Equal(mounts, hero.Weapons);
        Assert.All(hero.Weapons, w => Assert.True(Tuned(w)));

        var left = state.Loadout.Weapon(Mounting.Left)!;
        Ask(world, "Wear", state, state.Loadout.With(WiredSlot.Weapon(Mounting.Left), left with { Mk = 2 }));
        Assert.Equal(refits + 1, hero.Refits);
        Assert.DoesNotContain(hero.Weapons, Tuned);
        world.Step();
        Assert.All(hero.Weapons, w => Assert.True(Tuned(w)));
        Run(world, 1);
        Assert.Equal(refits + 1, hero.Refits);
    }

    [Fact]
    public void Another_chassis_or_a_new_body_stands_free()
    {
        var world = Colossus();
        var hero = Hero(world);
        var state = Red(world);
        var colossus = state.Loadout.Chassis;
        var grunt = state.Items.First(i => i.Kind == WiredKind.Chassis && i.Catalogue != "ballista5");
        Anchored(world);

        Assert.Null(Ask(world, "ChangeChassis", state, grunt.Id));
        Assert.Equal(AnchorPhase.Free, world.AnchorPhaseOf(hero));
        world.Step();
        Assert.Null(Ask(world, "ChangeChassis", state, colossus.Id));
        Assert.Equal(AnchorPhase.Free, world.AnchorPhaseOf(hero));
        world.Step();
        Assert.Equal(AnchorPhase.Free, world.AnchorPhaseOf(hero));

        Anchored(world);
        state.SpareBodies = 1;
        world.Damage(hero, 1_000_000, Race.Blue);
        Run(world, WiredBalance.RespawnTime + 1);
        var reborn = Hero(world);
        Assert.NotEqual(hero.Id, reborn.Id);
        Assert.Equal(AnchorPhase.Free, world.AnchorPhaseOf(reborn));
        Assert.False(world.Wired!.Bodies.ContainsKey(hero.Id));
    }

    // ---- the Hornet's and Old Iron's own dash -----------------------------------------------------

    [Theory]
    [InlineData("boss3", 8f, "Afterburner")]
    [InlineData("grunt5", 10f, "Ram")]
    public void Hornet_and_old_iron_dash_without_thrusters_and_never_hear_of_them(string chassis, float cooldown, string name)
    {
        var world = OutOn(chassis, driven: true);
        var state = Red(world);
        var hero = Hero(world);
        Assert.DoesNotContain(state.Loadout.Modules, m => m?.Catalogue == "thrusters");
        var view = world.SpecialView(Race.Red)!;
        Assert.Equal((name, true, null), (view.Name, view.Ready, view.Why));
        var at = hero.Position;

        Assert.Null(Do(world, new WiredDashCommand(Race.Red, Vector2.UnitX)));
        Assert.Equal(world.Time - GameWorld.Dt + cooldown, state.DashReadyAt, 3);
        Run(world, 0.5f);
        Assert.True(hero.Position.X - at.X > 10, $"dashed {hero.Position.X - at.X} m");

        var why = Do(world, new WiredDashCommand(Race.Red, Vector2.UnitX));
        Assert.Equal("that is not ready yet", why);
        view = world.SpecialView(Race.Red)!;
        Assert.Equal((false, "that is not ready yet"), (view.Ready, view.Why));
        Assert.True(view.SecondsLeft > cooldown - 1);
    }

    // ---- the Marshal ------------------------------------------------------------------------------

    [Fact]
    public void A_marshal_steadies_the_aim_of_escorts_within_30_m_and_not_its_own()
    {
        var world = OutOn("commander6");
        var hero = Hero(world);
        var near = Escort(world, hero.Position + new Vector2(29, 0));
        var far = Escort(world, hero.Position + new Vector2(0, 31));
        var stranger = world.AddMachine(Race.Red, hero.Position + new Vector2(-10, 0), "AGGRESSOR", "GRUNT", 1, null);

        Assert.Equal(WiredBalance.FieldAim, (float)Ask(world, "FieldAim", near)!);
        Assert.Equal(0f, (float)Ask(world, "FieldAim", far)!);
        Assert.Equal(0f, (float)Ask(world, "FieldAim", hero)!);
        Assert.Equal(0f, (float)Ask(world, "FieldAim", stranger)!);
        Assert.Equal(WiredBalance.FieldAim, (float)Ask(world, "AimPenalty", far, null)! - (float)Ask(world, "AimPenalty", near, null)!, 3);
    }

    [Fact]
    public void A_marshal_has_the_armour_of_escorts_near_it_grow_back_30_per_cent_faster()
    {
        var world = OutOn("commander6");
        var hero = Hero(world);
        var near = Escort(world, hero.Position + new Vector2(20, 0));
        var far = Escort(world, hero.Position + new Vector2(-50, 0));
        foreach (var escort in new[] { near, far })
            (escort.Armour, escort.ArmourRegen) = (0, 0);

        Run(world, 1);

        float Grown(Machine m) => m.Armour + m.ArmourRegen;
        Assert.True(Grown(far) > 0);
        Assert.True(near.Armour < near.MaxArmour);
        Assert.Equal(1 + WiredBalance.FieldRepair, Grown(near) / Grown(far), 2);
        Assert.Equal(0f, (float)Ask(world, "FieldRepair", hero)!);
    }

    [Fact]
    public void In_a_battle_a_marshal_steadies_its_team_mates_and_not_its_enemies()
    {
        var world = Battle("commander6", "reaper5", "reaper5", "reaper5");
        var at = OpenGround(world);
        var marshal = Battles.Hero(world, Race.Red);
        var mate = Battles.Hero(world, Race.Blue);
        var enemy = Battles.Hero(world, Race.Green);
        Place(world, marshal, at);
        Place(world, mate, at + new Vector2(20, 0));
        Place(world, enemy, at + new Vector2(0, 20));
        Place(world, Battles.Hero(world, Race.Yellow), at + new Vector2(0, -60));

        Assert.Equal(WiredBalance.FieldAim, (float)Ask(world, "FieldAim", mate)!);
        Assert.Equal(WiredBalance.FieldRepair, (float)Ask(world, "FieldRepair", mate)!);
        Assert.Equal(0f, (float)Ask(world, "FieldAim", enemy)!);
        Assert.Equal(0f, (float)Ask(world, "FieldAim", marshal)!);
    }

    [Fact]
    public void A_rally_calls_the_squad_to_the_marshal_in_the_order_of_their_ids_and_waits_40_s()
    {
        var world = OutOn("commander6");
        var hero = Hero(world);
        var state = Red(world);
        var pod = world.WiredPod(Race.Red)!;
        var first = Escort(world, hero.Position + new Vector2(60, 0));
        var aboard = Escort(world, world.Nav.NearestFreePoint(pod.Position + new Vector2(0, 40), crossesWater: false)!.Value);
        var second = Escort(world, hero.Position + new Vector2(-50, 20));
        Assert.Null(Do(world, new EnterGarrisonCommand(Race.Red, [aboard.Id], pod.Id)));
        for (var i = 0; i < 20 * 20 && aboard.Inside is null; i++)
            world.Step();
        Assert.Equal(pod.Id, aboard.Inside);
        var (from1, from2) = (first.Position, second.Position);

        Assert.Null(Do(world, new WiredSpecialCommand(Race.Red)));

        var rallied = world.Events.Where(e => e.Kind == GameEventKind.WiredSquadRallied).ToList();
        Assert.Equal([(first.Id, from1), (second.Id, from2)], rallied.Select(e => (e.Actor, e.Position)));
        foreach (var escort in new[] { first, second })
        {
            Assert.True(Vector2.Distance(hero.Position, escort.Position) < 20, $"{Vector2.Distance(hero.Position, escort.Position)} m off");
            Assert.Equal(new FollowOrder(hero.Id), escort.Order);
        }
        Assert.Equal(pod.Id, aboard.Inside);
        Assert.Equal(world.Time - GameWorld.Dt + WiredBalance.RallyCooldown, state.RallyReadyAt, 3);
        var view = world.SpecialView(Race.Red)!;
        Assert.Equal(("Rally", 2, 3, false), (view.Name, view.Count, view.Most, view.Ready));
        Assert.Equal("that is not ready yet", Do(world, new WiredSpecialCommand(Race.Red)));
    }

    [Fact]
    public void A_marshal_without_a_squad_out_in_the_open_calls_nobody_and_waits_for_nothing()
    {
        var world = OutOn("commander6");
        var state = Red(world);

        Assert.Equal("there is no squad to call", Do(world, new WiredSpecialCommand(Race.Red)));
        Assert.Equal(float.NegativeInfinity, state.RallyReadyAt);
        Assert.DoesNotContain(world.Events, e => e.Kind == GameEventKind.WiredSquadRallied);
    }

    [Fact]
    public void A_marshal_in_a_battle_has_no_squad_to_rally()
    {
        var world = Battle("commander6", "reaper5", "reaper5", "reaper5");

        Assert.Equal("there is no squad in a battle", Battles.Do(world, new WiredSpecialCommand(Race.Red)));
    }

    // ---- all of it in lockstep --------------------------------------------------------------------

    [Fact]
    public void Two_worlds_fed_the_same_commands_keep_the_same_checksums_through_an_anchor_a_lurk_and_a_ram()
    {
        static GameWorld Fought()
        {
            var world = Battle("ballista5", "grunt5", "widow6", "reaper5");
            var at = OpenGround(world);
            Place(world, Battles.Hero(world, Race.Red), at);
            Place(world, Battles.Hero(world, Race.Blue), at + new Vector2(0, 30));
            Place(world, Battles.Hero(world, Race.Green), at + new Vector2(12, 30));
            Place(world, Battles.Hero(world, Race.Yellow), at + new Vector2(-60, -60));
            world.Step();
            return world;
        }
        var worlds = new[] { Fought(), Fought() };
        var (colossus, widow) = (Battles.Hero(worlds[0], Race.Red), Battles.Hero(worlds[0], Race.Green));
        var seen = new List<GameEvent>();
        var spent = false;
        void Feed(params Command[] commands)
        {
            foreach (var world in worlds)
            {
                foreach (var command in commands)
                    world.Issue(command);
            }
        }
        void Go(float seconds)
        {
            for (var i = 0; i < seconds * GameWorld.TicksPerSecond; i++)
            {
                foreach (var world in worlds)
                    world.Step();
                Assert.Equal(worlds[0].Checksum(), worlds[1].Checksum());
                seen.AddRange(worlds[0].Events);
                spent |= worlds[0].Wired!.Bodies.TryGetValue(widow.Id, out var body) && body.LurkSpent;
            }
        }

        Feed(new WiredSpecialCommand(Race.Red), new DriveCommand(Race.Green, widow.Id, true, Aim: colossus.Position, Weapons: 0));
        Go(2.2f);
        Assert.All(widow.Weapons.Where(w => w.DealsDamage), w => Assert.True(Tuned(w)));
        Feed(new DriveCommand(Race.Green, widow.Id, true, Aim: colossus.Position, Weapons: -1));
        Go(3);
        Feed(new WiredDashCommand(Race.Blue, Vector2.UnitX));
        Go(2);
        Feed(new WiredSpecialCommand(Race.Red, false));
        Go(3);

        Assert.Contains(seen, e => e.Kind == GameEventKind.WiredAnchored && e.Actor == colossus.Id);
        Assert.Contains(seen, e => e.Kind == GameEventKind.WiredRammed && e.Actor == widow.Id);
        Assert.Contains(seen, e => e.Kind == GameEventKind.WiredReleasing && e.Actor == colossus.Id);
        Assert.True(spent, "the widow never fired from its lurk");
    }
}
