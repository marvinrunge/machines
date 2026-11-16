using System.Numerics;
using System.Reflection;
using System.Text.Json;
using Machines.Import.Model;
using Machines.Net;
using Machines.Sim;
using Machines.Sim.Combat;
using Machines.Sim.Survival;
using Machines.Sim.World;

namespace Machines.Tests.Sim;

/// <summary>
/// The constructor and spy lines of Wired and what the eight machines of its own bring with them, as far as the foundation
/// lays it: the chassis and their values, accuracy and the built-in dash, the squad limit, the new commands, and the
/// original's orders a Wired match leaves out.
/// </summary>
public class WiredLinesTests
{
    private static readonly Lazy<GameData> DataLoader = new(() => GameData.Load(RepoPaths.OriginalDataFolder()));

    private static GameData Data => DataLoader.Value;

    private static readonly Vector2 Field = new(800, 800);

    private static GameWorld NewRun(int seed = 7) =>
        MatchSetup.Create(Data, "m_desert", "m_desert",
            new Dictionary<Race, Controller> { [Race.Red] = Controller.Player, [Race.Blue] = Controller.Ai }, seed,
            new MatchOptions { Wired = new WiredOptions() });

    private static GameWorld NewMatch() =>
        MatchSetup.Create(Data, "m_desert", "m_desert",
            new Dictionary<Race, Controller> { [Race.Red] = Controller.Player, [Race.Blue] = Controller.Player }, seed: 7);

    private static WiredState Red(GameWorld world) => world.Wired!.Heroes[Race.Red];

    private static Machine Hero(GameWorld world) => world.HeroOf(Race.Red)!;

    /// <returns>Why the command was refused, or null.</returns>
    private static string? Do(GameWorld world, Command command)
    {
        world.Issue(command);
        world.Step();
        return world.Rejected.FirstOrDefault()?.Reason;
    }

    /// <summary>A run with its hangar open and a bank that does not get in the way.</summary>
    private static GameWorld InHangar()
    {
        var world = NewRun();
        world.Races[Race.Red].Bmu = 100000;
        Assert.Null(Do(world, new WiredHangarCommand(Race.Red, true)));
        return world;
    }

    /// <summary>Puts the hero on any chassis, without the way there.</summary>
    private static void Board(GameWorld world, string chassis)
    {
        var item = Red(world).Add(WiredKind.Chassis, chassis, 0);
        Assert.Null(Do(world, new WiredChassisCommand(Race.Red, item.Id)));
    }

    /// <summary>A run whose hero wears the chassis and is out of the hangar, not at the wheel.</summary>
    private static GameWorld OutOn(string chassis)
    {
        var world = InHangar();
        Board(world, chassis);
        Assert.Null(Do(world, new WiredHangarCommand(Race.Red, false)));
        Hero(world).DrivenByPlayer = false;
        return world;
    }

    private static HeroLoadout Loadout(string chassis, params (WiredSlot Slot, string Id, int Mk)[] parts)
    {
        var loadout = HeroLoadout.Bare(new WiredItem(1, WiredKind.Chassis, chassis));
        var id = 2;
        foreach (var (slot, item, mk) in parts)
            loadout = loadout.With(slot, new WiredItem(id++, WiredCatalogue.KindOf(item)!.Value, item, mk));
        return loadout;
    }

    private static List<GameEvent> Run(GameWorld world, float seconds, Action? everyTick = null)
    {
        var events = new List<GameEvent>();
        for (var i = 0; i < seconds * GameWorld.TicksPerSecond; i++)
        {
            world.Step();
            events.AddRange(world.Events);
            everyTick?.Invoke();
        }
        return events;
    }

    // ---- the lines --------------------------------------------------------------------------------

    [Theory]
    [InlineData("dozer1", "CONSTRUCTOR", "DOZER", 1, 50, 30, 0.25f, 9f, 65f, 80, ChassisSpecial.Towers, 2)]
    [InlineData("builder3", "CONSTRUCTOR", "BUILDER", 3, 100, 50, 0.75f, 8.5f, 45f, 160, ChassisSpecial.Towers, 4)]
    [InlineData("behemoth4", "CONSTRUCTOR", "BEHEMOTH", 4, 200, 75, 0.9f, 8f, 25f, 170, ChassisSpecial.Towers, 6)]
    [InlineData("titan6", "CONSTRUCTOR", "BEHEMOTH", 6, 300, 100, 1f, 9f, 30f, 180, ChassisSpecial.Towers, 8)]
    [InlineData("spy3", "SPY_LOCATOR", null, 3, 30, 6, 0.95f, 12f, 270f, 200, ChassisSpecial.Mines, 0)]
    [InlineData("spy5", "SPY_LOCATOR", null, 5, 36, 6, 1f, 20f, 720f, 280, ChassisSpecial.Mines, 0)]
    [InlineData("phantom6", "SPY_LOCATOR", null, 6, 120, 60, 1f, 20f, 720f, 280, ChassisSpecial.Mines | ChassisSpecial.Shadow, 0)]
    public void The_constructors_and_spies_are_the_original_s_machines_and_titan_and_phantom_wired_s_own(
        string id, string type, string? subType, int level, int hitPoints, int armour, float repair, float speed, float turn, int scanner,
        ChassisSpecial special, int towers)
    {
        var chassis = WiredCatalogue.FindChassis(id)!;
        var stats = Data.Parm.Machine(chassis.TypeName).Level(chassis.Level);
        var build = HeroBuild.Resolve(Data, Loadout(id));

        Assert.Equal((type, subType, level, special, towers), (chassis.Type, chassis.SubType, chassis.Level, chassis.Special, chassis.Towers));
        Assert.Equal((hitPoints, armour), (stats.HitPoints, stats.Armour));
        Assert.Equal((repair, speed, turn, scanner), (stats.RepairRate, stats.Speed, stats.RotationSpeed, stats.ScannerRange));
        Assert.Equal(hitPoints * WiredBalance.HeroHitPoints, build.Stats.HitPoints);
        Assert.Equal((int)MathF.Round(armour * WiredBalance.HeroArmour), build.Stats.Armour);
        // Bare, with no module to change them, the hero moves, turns, mends and sees as the level.
        Assert.Equal((repair, speed, turn, scanner), (build.Stats.RepairRate, build.Stats.Speed, build.Stats.RotationSpeed, build.Stats.ScannerRange));
        Assert.Equal(id is "titan6" or "phantom6", WiredMachines.IsWired(chassis.TypeName, chassis.Level));
        Assert.Equal(ChassisSpecial.None, WiredMachines.SpecialOf(chassis.TypeName, chassis.Level));
        Assert.Null(chassis.Dash);
    }

    [Fact]
    public void The_lines_hang_off_the_grunt_and_the_overseer_and_open_with_the_waves()
    {
        string[] constructors = ["dozer1", "builder3", "behemoth4", "titan6"], spies = ["spy3", "spy5", "phantom6"];

        Assert.Equal(constructors.Concat(spies), WiredCatalogue.Chassis.SkipLast(2).TakeLast(7).Select(c => c.Id));
        Assert.Equal(["grunt2", "dozer1", "builder3", "behemoth4"], constructors.Select(c => WiredCatalogue.FindChassis(c)!.Needs.Single()));
        Assert.Equal(["overseer2", "spy3", "spy5"], spies.Select(c => WiredCatalogue.FindChassis(c)!.Needs.Single()));
        Assert.Equal([6, 9, 12, 20, 6, 12, 20], constructors.Concat(spies).Select(c => WiredCatalogue.FindChassis(c)!.FromWave));
        Assert.Equal([420, 700, 1100, 2600, 420, 1000, 2600], constructors.Concat(spies).Select(c => WiredCatalogue.FindChassis(c)!.Price));
    }

    [Theory]
    [InlineData("dozer1")]
    [InlineData("builder3")]
    [InlineData("behemoth4")]
    [InlineData("titan6")]
    [InlineData("spy3")]
    [InlineData("spy5")]
    [InlineData("phantom6")]
    public void Each_constructor_and_spy_is_sold_from_its_wave_on_and_worn_as_a_plain_chassis(string id)
    {
        var world = InHangar();
        var (run, state, chassis) = (world.Wired!, Red(world), WiredCatalogue.FindChassis(id)!);
        state.Reached.Add(chassis.Needs.Single());

        // Between waves the shop sells for the wave to come: one wave short of it, it is refused.
        run.Wave = chassis.FromWave - 2;
        Assert.Equal(chassis.FromWave - 1, run.ShopWave);
        Assert.True(world.TooEarly(chassis));
        Assert.Equal($"opens at wave {chassis.FromWave}", Do(world, new WiredBuyCommand(Race.Red, id)));
        Assert.DoesNotContain(state.Items, i => i.Catalogue == id);
        run.Wave = chassis.FromWave - 1;
        Assert.Equal(chassis.FromWave, run.ShopWave);
        Assert.False(world.TooEarly(chassis));
        Assert.Null(Do(world, new WiredBuyCommand(Race.Red, id)));
        var bought = state.Items[^1];
        Assert.Equal(id, bought.Catalogue);
        Assert.Null(Do(world, new WiredChassisCommand(Race.Red, bought.Id)));

        var hero = Hero(world);
        Assert.Equal(id, HeroBuild.Resolve(Data, state.Loadout).Chassis.Id);
        Assert.Equal((chassis.Type, chassis.SubType, chassis.Level), (hero.Spec.Type, hero.Spec.SubType, hero.Stats.HardwareLevel));
        // The Grunt 1's Bolter comes along where it fits; every other slot takes one too, and the hero carries it there.
        Assert.Equal(chassis.Slots.Count(s => state.Loadout.Weapon(s.Mounting) is not null), hero.Weapons.Count);
        foreach (var slot in chassis.Slots.Where(s => state.Loadout.Weapon(s.Mounting) is null))
        {
            var bolter = state.Add(WiredKind.Weapon, "BOLTER", 0);
            Assert.Null(Do(world, new WiredEquipCommand(Race.Red, bolter.Id, WiredSlot.Weapon(slot.Mounting))));
        }
        hero = Hero(world);
        Assert.Equal(chassis.Slots.Select(s => s.Mounting).Order(), hero.Weapons.Select(w => w.Mounting).Order());
        Assert.All(hero.Weapons, w => Assert.Equal("BOLTER", w.Spec.TypeName));
    }

    [Fact]
    public void The_titan_builds_as_the_behemoth_5_and_the_phantom_carries_the_spy_5_s_mines()
    {
        var (titan, behemoth) = (Data.Parm.Machine("CONSTRUCTOR BEHEMOTH").Level(6), Data.Parm.Machine("CONSTRUCTOR BEHEMOTH").Level(5));
        var (phantom, spy) = (Data.Parm.Machine("SPY_LOCATOR").Level(6), Data.Parm.Machine("SPY_LOCATOR").Level(5));

        Assert.Equal(behemoth.ConstructionRate, titan.ConstructionRate);
        Assert.Equal(behemoth.MostAdvancedConstruction, titan.MostAdvancedConstruction);
        Assert.Equal((9f, 30f, 180), (titan.Speed, titan.RotationSpeed, titan.ScannerRange));
        Assert.Equal(((int?)8, spy.NightVision), (phantom.MineCapacity, phantom.NightVision));
        Assert.Equal(8, spy.MineCapacity);
        Assert.Equal((20f, 720f, 280), (phantom.Speed, phantom.RotationSpeed, phantom.ScannerRange));
        // Both mend fully, and their clearance, and so their radius, grows with their models: the Titan's is the
        // Behemoth's, the Phantom is 2.4 times the Spy 5 (tools/wired-models).
        Assert.Equal((1f, 1f), (titan.RepairRate, phantom.RepairRate));
        Assert.Equal((behemoth.HighClearance * 1.0f, behemoth.LowClearance * 1.0f), (titan.HighClearance, titan.LowClearance));
        Assert.Equal((spy.HighClearance * 2.4f, spy.LowClearance * 2.4f), (phantom.HighClearance, phantom.LowClearance));
    }

    [Fact]
    public void Constructors_and_spies_aim_at_60_the_phantom_at_75_and_every_other_chassis_as_its_data_says()
    {
        foreach (var chassis in WiredCatalogue.Chassis)
        {
            var level = Data.Parm.Machine(chassis.TypeName).Level(chassis.Level);
            var accuracy = HeroBuild.Resolve(Data, Loadout(chassis.Id)).Stats.Accuracy;
            var expected = chassis.Id switch
            {
                "phantom6" => 75,
                _ when chassis.Type is "CONSTRUCTOR" or "SPY_LOCATOR" or "GEO_LOCATOR" => WiredBalance.NoAccuracy,
                _ => level.Accuracy!.Value,
            };

            Assert.Equal(expected, accuracy);
        }
        Assert.Equal(60, WiredBalance.NoAccuracy);
    }

    // ---- the built-in dash ------------------------------------------------------------------------

    [Theory]
    [InlineData("grunt5", 0, 10f)]
    [InlineData("grunt5", 1, 10f)]
    [InlineData("grunt5", 2, 9f)]
    [InlineData("grunt5", 3, 6f)]
    [InlineData("boss3", 0, 8f)]
    [InlineData("boss3", 1, 8f)]
    [InlineData("boss3", 3, 6f)]
    [InlineData("grunt4", 0, null)]
    [InlineData("grunt4", 2, 9f)]
    public void Old_iron_and_the_hornet_dash_of_their_own_and_thrusters_only_bring_it_round_sooner(string chassis, int thrusters, float? cooldown)
    {
        var loadout = thrusters == 0 ? Loadout(chassis) : Loadout(chassis, (WiredSlot.Module(0), "thrusters", thrusters));

        Assert.Equal(cooldown, HeroBuild.Resolve(Data, loadout).DashCooldown);
    }

    [Fact]
    public void Only_old_iron_and_the_hornet_have_a_dash_of_their_own()
    {
        Assert.Equal(["grunt5", "boss3"], WiredCatalogue.Chassis.Where(c => c.Dash is not null).Select(c => c.Id));
        Assert.All(WiredCatalogue.Chassis.Where(c => c.Dash is null), c => Assert.Null(HeroBuild.Resolve(Data, Loadout(c.Id)).DashCooldown));
    }

    [Fact]
    public void The_specials_of_the_stage_one_machines_are_on_their_rows_and_only_the_bodies_go_for_every_machine()
    {
        var specials = WiredCatalogue.Chassis.Where(c => c.Special != ChassisSpecial.None).ToDictionary(c => c.Id, c => c.Special);

        Assert.Equal(ChassisSpecial.Plough, specials["grunt5"]);
        Assert.Equal(ChassisSpecial.Harvest, specials["reaper5"]);
        Assert.Equal(ChassisSpecial.Lurk, specials["widow6"]);
        Assert.Equal(ChassisSpecial.Anchor, specials["ballista5"]);
        Assert.Equal(ChassisSpecial.Afterburner, specials["boss3"]);
        Assert.Equal(ChassisSpecial.Field, specials["commander6"]);
        Assert.Equal(15, specials.Count);

        Assert.Equal(ChassisSpecial.Plough, WiredMachines.SpecialOf("AGGRESSOR GRUNT", 5));
        Assert.Equal(ChassisSpecial.Anchor, WiredMachines.SpecialOf("AGGRESSOR BALLISTA", 5));
        Assert.Equal(ChassisSpecial.None, WiredMachines.SpecialOf("AGGRESSOR BALLISTA", 4));
        Assert.Equal(ChassisSpecial.None, WiredMachines.SpecialOf("ADMINISTRATOR COMMANDER", 6));
        Assert.Equal(ChassisSpecial.None, WiredMachines.SpecialOf("SPY_LOCATOR", 6));
    }

    // ---- the squad --------------------------------------------------------------------------------

    [Fact]
    public void A_marshal_leads_two_more_escorts_than_the_wave_allows_and_another_chassis_none()
    {
        var world = InHangar();
        var state = Red(world);
        var wave = world.Wired!.ShopWave;
        Assert.Equal(WiredBalance.SquadLimit(wave), world.SquadLimit(state));

        Board(world, "commander6");
        Assert.Equal(WiredBalance.SquadLimit(wave) + WiredBalance.FieldSquad, world.SquadLimit(state));
        var key = new ResearchKey("AGGRESSOR", "GRUNT", 1, "R_BOLTER");
        for (var i = 0; i < world.SquadLimit(state); i++)
            Assert.Null(Do(world, new WiredSquadBuyCommand(Race.Red, key)));
        Assert.Equal("the squad is full", Do(world, new WiredSquadBuyCommand(Race.Red, key)));

        // Off the Marshal the escorts stay, and no more are bought.
        Board(world, "grunt1");
        Assert.Equal(WiredBalance.SquadLimit(wave), world.SquadLimit(state));
        Assert.Equal(WiredBalance.SquadLimit(wave) + WiredBalance.FieldSquad, world.Escorts(Race.Red).Count());
        Assert.Equal("the squad is full", Do(world, new WiredSquadBuyCommand(Race.Red, key)));
    }

    // ---- the commands -----------------------------------------------------------------------------

    [Fact]
    public void The_special_tower_and_scrap_commands_come_through_the_wire_as_they_went_in()
    {
        Command[] commands =
        [
            new WiredSpecialCommand(Race.Red),
            new WiredSpecialCommand(Race.Blue, false),
            new WiredTowerCommand(Race.Red, "sentry3", new Vector2(12.5f, -3.25f), 270),
            new WiredScrapTowerCommand(Race.Yellow, 17),
        ];

        foreach (var command in commands)
            Assert.Equal(command, JsonSerializer.Deserialize<Command>(JsonSerializer.Serialize(command, NetJson.Options), NetJson.Options));
        Assert.True(new WiredSpecialCommand(Race.Red).On);
    }

    [Fact]
    public void A_battle_takes_the_special_and_the_towers_past_the_hangar_it_has_not()
    {
        var world = Battles.Begun();

        Assert.Equal("there is no hangar in a battle", Battles.Do(world, new WiredHangarCommand(Race.Red, true)));
        Assert.Equal("this chassis has nothing to use", Battles.Do(world, new WiredSpecialCommand(Race.Red)));
        Assert.NotEqual("there is no hangar in a battle", Battles.Do(world, new WiredTowerCommand(Race.Red, "turret1", Battles.Hero(world, Race.Red).Position, 0)));
        Assert.NotEqual("there is no hangar in a battle", Battles.Do(world, new WiredScrapTowerCommand(Race.Red, 1)));
    }

    [Fact]
    public void A_chassis_without_a_key_of_its_own_says_so_and_a_downed_hero_uses_nothing()
    {
        var world = OutOn("grunt4");
        var state = Red(world);

        Assert.Equal("this chassis has nothing to use", Do(world, new WiredSpecialCommand(Race.Red)));
        world.Damage(Hero(world), 1_000_000, Race.Blue);
        world.Step();
        Assert.Null(world.HeroOf(Race.Red));
        Assert.Equal("the hero is down", world.WhyNoSpecial(state));
    }

    [Fact]
    public void Every_chassis_answers_what_its_special_shows_without_fault()
    {
        var world = InHangar();
        var state = Red(world);
        Assert.Null(world.SpecialView(Race.Red));

        foreach (var chassis in WiredCatalogue.Chassis.Where(c => c.Id != state.Loadout.Chassis.Catalogue))
        {
            Board(world, chassis.Id);
            var view = world.SpecialView(Race.Red);
            if (chassis.Special == ChassisSpecial.None)
                Assert.Null(view);
            else if (view is not null)
                Assert.Equal(chassis.Special, view.Special & chassis.Special);
            var why = world.WhyNoSpecial(state);
            // Only the anchor, the rally, the mines and the pulse are worked by the special's key; the towers have their own command.
            if ((chassis.Special & (ChassisSpecial.Anchor | ChassisSpecial.Field | ChassisSpecial.Mines | ChassisSpecial.Locate)) == 0)
                Assert.True(why is "this chassis has nothing to use" or "not from inside the pod", $"{chassis.Id}: {why}");
        }
        Assert.Null(world.SpecialView(Race.Blue));
    }

    // ---- what a Wired match leaves out ------------------------------------------------------------

    /// <summary>The original's building orders for a constructor, on buildings put down round a point: its own, a site and an enemy's.</summary>
    private static List<(string Name, Command Order)> BuildingOrders(GameWorld world, int constructor, Vector2 at)
    {
        var site = world.AddConstruction(Race.Red, at + new Vector2(0, -70), "GARRISON", null, 1, 0, complete: false);
        var enemy = world.AddConstruction(Race.Blue, at + new Vector2(-70, 50), "GARRISON", null, 1, 0, complete: true);
        var own = world.AddConstruction(Race.Red, at + new Vector2(-70, -50), "GARRISON", null, 1, 0, complete: true);
        own.HitPoints = own.MaxHitPoints / 2;
        return
        [
            ("place", new PlaceConstructionCommand(Race.Red, [constructor], "MISSILE_EMPLACEMENT", "TURRET", 1, at + new Vector2(30, 0), 0)),
            ("series", new SeriesCommand(Race.Red, [constructor], [new PlaceConstructionCommand(Race.Red, [], "MISSILE_EMPLACEMENT", "TURRET", 1, at + new Vector2(60, 40), 0)])),
            ("construct", new ConstructCommand(Race.Red, [constructor], site.Id)),
            ("repair", new RepairCommand(Race.Red, [constructor], own.Id)),
            ("capture", new CaptureCommand(Race.Red, [constructor], enemy.Id)),
            ("deconstruct", new DeconstructCommand(Race.Red, [constructor], own.Id)),
        ];
    }

    [Fact]
    public void Nobody_places_builds_mends_takes_or_pulls_down_a_building_in_a_run_and_in_a_match_a_dozer_does()
    {
        var match = NewMatch();
        match.Races[Race.Red].Bmu = 1000;
        var dozer = match.AddMachine(Race.Red, Field, "CONSTRUCTOR", "DOZER", 1, null);
        foreach (var (name, order) in BuildingOrders(match, dozer.Id, Field))
            Assert.True(Do(match, order) is null, $"{name} in a match: {match.Rejected.FirstOrDefault()?.Reason}");

        var run = OutOn("dozer1");
        var hero = Hero(run);
        foreach (var (name, order) in BuildingOrders(run, hero.Id, hero.Position))
            Assert.True(Do(run, order) == "not in Wired", $"{name} in a run: {run.Rejected.FirstOrDefault()?.Reason}");
        Assert.Equal("not in Wired", run.WhyCannotPlace(Race.Red, "MISSILE_EMPLACEMENT", "TURRET", 1, hero.Position + new Vector2(30, 0), 0));
    }

    /// <summary>A spy's own orders: lay a mine, fetch more, hide, break into an enemy's lab.</summary>
    private static List<(string Name, Command Order)> SpyOrders(GameWorld world, Race race, int spy, Vector2 at)
    {
        var lab = world.AddConstruction(race == Race.Red ? Race.Blue : Race.Red, at + new Vector2(0, 70), "HARDWARE_LAB", "MILITARY", 1, 0, complete: true);
        world.AddConstruction(race, at + new Vector2(-50, -40), "GARRISON", null, 1, 0, complete: true);
        return
        [
            ("drop", new DropLandMinesCommand(race, [spy], [at + new Vector2(20, 0)])),
            ("refill", new RefillLandMinesCommand(race, [spy])),
            ("camouflage", new CamouflageCommand(race, [spy])),
            ("enter", new EnterLabCommand(race, [spy], lab.Id)),
        ];
    }

    /// <summary>A place with open ground 70 m round it.</summary>
    private static Vector2 OpenGround(GameWorld world) => Enumerable.Range(0, 400).Select(i => new Vector2(200 + i % 20 * 60, 200 + i / 20 * 60))
        .First(p => world.Nav.IsAreaFree((p - new Vector2(70, 70), p + new Vector2(70, 70))));

    [Fact]
    public void A_spy_hero_lays_no_mines_by_hand_fetches_none_hides_not_and_breaks_in_nowhere_and_other_spies_do()
    {
        var match = NewMatch();
        var at = OpenGround(match);
        var spy = match.AddMachine(Race.Red, at, "SPY_LOCATOR", null, 3, null);
        foreach (var (name, order) in SpyOrders(match, Race.Red, spy.Id, at))
            Assert.True(Do(match, order) is null, $"{name} in a match: {match.Rejected.FirstOrDefault()?.Reason}");

        var run = OutOn("spy3");
        var hero = Hero(run);
        foreach (var (name, order) in SpyOrders(run, Race.Red, hero.Id, hero.Position))
            Assert.True(Do(run, order) == "not in Wired", $"{name} in a run: {run.Rejected.FirstOrDefault()?.Reason}");

        // The spies of the waves keep all of it.
        var foe = run.AddMachine(Race.Blue, hero.Position + new Vector2(80, 0), WiredDirector.Spy.Type, WiredDirector.Spy.SubType, WiredDirector.Spy.HardwareLevel, WiredDirector.Spy.WeaponCombo);
        Assert.NotEqual("not in Wired", Do(run, new DropLandMinesCommand(Race.Blue, [foe.Id], [foe.Position + new Vector2(20, 0)])));
        Assert.NotEqual("not in Wired", Do(run, new CamouflageCommand(Race.Blue, [foe.Id])));
    }

    [Fact]
    public void A_dozer_hero_opens_no_buildings_and_says_nothing_of_them()
    {
        var world = OutOn("dozer1");

        var events = Run(world, 3);

        Assert.DoesNotContain(events, e => e.Kind == GameEventKind.VoiceMail && e.Detail == "VID_POD_NEW_CONSTRUCTIONS");
    }

    [Fact]
    public void A_spy_hero_under_fire_stands_as_a_hero_where_a_spy_of_the_race_runs()
    {
        // Unarmed, so that it does not stand its ground for shooting back.
        var world = InHangar();
        Board(world, "spy3");
        foreach (var mounting in Enum.GetValues<Mounting>())
        {
            if (Red(world).Loadout.Weapon(mounting) is { } weapon)
                Assert.Null(Do(world, new WiredUnequipCommand(Race.Red, weapon.Id)));
        }
        Assert.Null(Do(world, new WiredHangarCommand(Race.Red, false)));
        var hero = Hero(world);
        hero.DrivenByPlayer = false;
        Assert.False(hero.CanFight);
        var spy = world.AddMachine(Race.Red, hero.Position + new Vector2(-40, 0), "SPY_LOCATOR", null, 3, null);
        var attackers = new[] { hero, spy }.Select(m => world.AddMachine(Race.Blue, m.Position + new Vector2(0, 30), "AGGRESSOR", "GRUNT", 1, "R_BOLTER")).ToList();
        world.Issue(new AttackCommand(Race.Blue, [attackers[0].Id], hero.Id));
        world.Issue(new AttackCommand(Race.Blue, [attackers[1].Id], spy.Id));
        var (heroEvaded, spyEvaded) = (false, false);

        Run(world, 8, () =>
        {
            heroEvaded |= hero.Order is EvadeOrder;
            spyEvaded |= spy.Order is EvadeOrder;
        });

        Assert.False(heroEvaded);
        Assert.True(spyEvaded, "the spy that is no hero did not run either, so this shows nothing");
    }

    /// <summary>Asks the world something it keeps to itself.</summary>
    private static object? Ask(GameWorld world, string method, params object[] arguments) =>
        typeof(GameWorld).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(world, arguments);

    [Fact]
    public void A_constructor_hero_is_a_fighter_its_escorts_count_on_and_run_to_where_a_dozer_of_a_match_is_none()
    {
        var run = OutOn("dozer1");
        var hero = Hero(run);
        var escort = run.AddMachine(Race.Red, run.Nav.NearestFreePoint(hero.Position + new Vector2(20, 0), crossesWater: false)!.Value, "AGGRESSOR", "GRUNT", 1, "R_BOLTER");
        run.Step();
        var heroStrength = (int)Ask(run, "MilitaryStrength", hero)!;

        Assert.True(heroStrength > 0);
        Assert.Equal((int)Ask(run, "MilitaryStrength", escort)! + heroStrength, (int)Ask(run, "MeasureLocalStrength", escort)!);
        Assert.Same(hero, Ask(run, "SafestFriend", escort, false));

        // In a match of the original's kind a Dozer is what the original makes of it: worth nothing to a fighter beside it.
        var match = NewMatch();
        var at = OpenGround(match);
        var grunt = match.AddMachine(Race.Red, at, "AGGRESSOR", "GRUNT", 1, "R_BOLTER");
        match.Step();
        var alone = (int)Ask(match, "MeasureLocalStrength", grunt)!;
        var dozer = match.AddMachine(Race.Red, at + new Vector2(20, 0), "CONSTRUCTOR", "DOZER", 1, null);
        match.Step();

        Assert.Equal(0, (int)Ask(match, "MilitaryStrength", dozer)!);
        Assert.Equal(alone, (int)Ask(match, "MeasureLocalStrength", grunt)!);
        Assert.NotSame(dozer, Ask(match, "SafestFriend", grunt, false));
    }

    // ---- tuned weapons and mines ------------------------------------------------------------------

    private static void Tune(WeaponMount mount, WeaponSpec? tuned) =>
        typeof(WeaponMount).GetMethod("Tune", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(mount, [tuned]);

    [Fact]
    public void A_tuned_weapon_comes_back_to_its_own_spec_and_a_refit_looks_past_the_tuning()
    {
        var spec = Data.Parm.Weapon("BOLTER");
        var mount = new WeaponMount(Mounting.Left, spec);
        var tuned = spec with { Range = spec.Range * 1.3f };

        Tune(mount, tuned);
        Assert.Same(tuned, mount.Spec);
        Assert.Same(spec, mount.Base);
        Tune(mount, null);
        Assert.Same(spec, mount.Spec);

        var world = InHangar();
        var hero = Hero(world);
        var worn = hero.Weapons[0];
        var stronger = worn.Base with { Damage = worn.Base.Damage * 1.4f };
        Tune(worn, stronger);
        var chip = Red(world).Add(WiredKind.Chip, "trader", 0);
        Assert.Null(Do(world, new WiredChipCommand(Race.Red, chip.Id, 0)));

        Assert.Equal(0, hero.Refits);
        Assert.Same(worn, hero.Weapons[0]);
        Assert.Same(stronger, worn.Spec);
    }

    [Theory]
    [InlineData("spy3", 5)]
    [InlineData("spy5", 8)]
    [InlineData("phantom6", 8)]
    public void A_spy_hero_starts_a_life_with_the_mines_its_chassis_carries(string chassis, int mines)
    {
        var tier = BattleCatalogue.Tier(chassis);
        var hero = new BattleHero(Race.Red, "Ace", chassis, BattleBots.Loadout(chassis, tier));
        var world = Battles.New(BattleBots.Compose(new BattleOptions { Tier = tier, TeamA = [Race.Red, Race.Blue], TeamB = [Race.Green, Race.Yellow] }, [hero]));

        Assert.Equal(chassis, Red(world).Loadout.Chassis.Catalogue);
        Assert.Equal(mines, Hero(world).Mines);
        Assert.Equal(mines, Hero(world).Stats.MineCapacity);
        Assert.All(world.Wired!.Heroes.Values.Where(s => s.Race != Race.Red && s.Build(Data).Chassis.Type != "SPY_LOCATOR"),
            s => Assert.Equal(0, world.HeroOf(s.Race)!.Mines));
    }

    // ---- the Phantom's shadow ---------------------------------------------------------------------

    [Theory]
    [InlineData("grunt1", false)]
    [InlineData("spy5", false)]
    [InlineData("phantom6", true)]
    public void A_damaging_ability_gives_away_only_a_hero_with_a_shadow_to_lose(string chassis, bool shadow)
    {
        var world = InHangar();
        var state = Red(world);
        if (state.Loadout.Chassis.Catalogue != chassis)
            Board(world, chassis);
        Assert.Null(Do(world, new WiredHangarCommand(Race.Red, false)));
        state.Loadout = state.Loadout.With(WiredSlot.Ability(0), state.Add(WiredKind.Ability, "discharge", 0));

        Assert.Null(Do(world, new WiredAbilityCommand(Race.Red, 0, Hero(world).Position)));

        // Any other hero keeps it at its start, so nothing goes into its fingerprint that did not before the shadow.
        Assert.Equal(shadow, !float.IsNegativeInfinity(state.RevealedAt));
    }

    [Fact]
    public void In_a_battle_the_other_team_s_circle_gives_away_only_a_hero_with_a_shadow_to_lose()
    {
        var tier = BattleCatalogue.Tier("phantom6");
        BattleHero Player(Race race, string chassis) => new(race, race.ToString(), chassis, BattleBots.Loadout(chassis, tier));
        var options = BattleBots.Compose(new BattleOptions { Tier = tier, TeamA = [Race.Red, Race.Blue], TeamB = [Race.Green, Race.Yellow] },
            [Player(Race.Red, "phantom6"), Player(Race.Blue, "widow6")]);
        var world = Battles.Begun(options);
        Battles.IntoCircle(world, Race.Red);
        Battles.IntoCircle(world, Race.Blue, 1);

        Battles.Run(world, 1);

        Assert.True(Vector2.Distance(Battles.Hero(world, Race.Blue).Position, world.BattlePod(1)!.Position) <= BattleBalance.CaptureRadius,
            "the widow did not stand in the circle, so this shows nothing");
        Assert.False(float.IsNegativeInfinity(Red(world).RevealedAt));
        Assert.True(float.IsNegativeInfinity(world.Wired!.Heroes[Race.Blue].RevealedAt));
    }
}
