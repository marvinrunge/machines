using System.Numerics;
using Machines.Import.Model;
using Machines.Sim;
using Machines.Sim.Combat;
using Machines.Sim.World;

namespace Machines.Tests.Sim;

public class WeaponComboTests
{
    [Theory]
    [InlineData("L_BOLTER", "Left:BOLTER")]
    [InlineData("LR_HEAVY_BOLTER1_X2", "Left:HEAVY_BOLTER1 Right:HEAVY_BOLTER1")]
    [InlineData("LRT_PLASMA_CANNON2_X3", "Left:PLASMA_CANNON2 Right:PLASMA_CANNON2 Top:PLASMA_CANNON2")]
    [InlineData("L_PULSE_RIFLE_R_SUPERCHARGE_ADVANCED", "Left:PULSE_RIFLE Right:SUPERCHARGE_ADVANCED")]
    [InlineData("L_MULTI_LAUNCHER7_T_GORILLA_PUNCH_R_MULTI_LAUNCHER7", "Left:MULTI_LAUNCHER7 Top:GORILLA_PUNCH Right:MULTI_LAUNCHER7")]
    [InlineData("LR_AUTO_CANNON_X", "Left:AUTO_CANNON Right:AUTO_CANNON")]
    public void Combos_split_into_mounted_weapons(string combo, string expected)
    {
        Assert.Equal(expected, string.Join(' ', WeaponCombos.Split(combo).Select(w => $"{w.Mounting}:{w.Weapon}")));
    }
}

/// <summary>Runs real matches on the original m_desert map.</summary>
public class SimulationTests
{
    private static readonly Lazy<GameData> DataLoader = new(() => GameData.Load(RepoPaths.OriginalDataFolder()));

    private static GameWorld NewMatch(Controller red = Controller.Ai, int seed = 7) =>
        MatchSetup.Create(DataLoader.Value, "m_desert", "m_desert", new Dictionary<Race, Controller> { [Race.Red] = red, [Race.Blue] = Controller.Ai }, seed);

    [Fact]
    public void Match_starts_as_the_scenario_says()
    {
        var world = NewMatch();

        Assert.Equal(750, world.Races[Race.Red].Bmu);
        Assert.Contains(world.Constructions, c => c.Race == Race.Red && c.IsType("POD") && c.Complete);
        Assert.Equal(2, world.Machines.Count(m => m.Race == Race.Red && m.IsType("AGGRESSOR", "GRUNT")));
        Assert.Equal(2, world.Machines.Count(m => m.Race == Race.Red && m.IsType("CONSTRUCTOR", "DOZER")));
        Assert.True(world.BuildingPlans[Race.Blue].Count > 10);
        Assert.Contains(new ResearchKey("AGGRESSOR", "GRUNT", 1, "R_BOLTER"), world.Races[Race.Red].Researched);
    }

    [Fact]
    public void Paths_go_around_the_original_obstacles()
    {
        var world = NewMatch();
        var obstacle = world.Planet.ConfigSpace.Obstacles.First(o => !o.Water);
        var centre = obstacle.Polygon.Aggregate(Vector2.Zero, (s, p) => s + p) / obstacle.Polygon.Count;

        Assert.False(world.Nav.IsFree(centre, crossesWater: false));
        var start = new Vector2(394, 260);
        var path = world.Nav.FindPath(start, new Vector2(1200, 1200), crossesWater: false);
        Assert.NotEmpty(path);
        var previous = start;
        foreach (var point in path)
        {
            Assert.True(world.Nav.LineOfSight(previous, point, crossesWater: false));
            previous = point;
        }
    }

    [Fact]
    public void A_dozer_builds_a_mine_on_a_found_site_at_the_original_rate()
    {
        var world = NewMatch(red: Controller.Player);
        var dozer = world.Machines.First(m => m.Race == Race.Red && m.IsType("CONSTRUCTOR", "DOZER"));
        var site = world.Sites.OrderBy(s => Vector2.Distance(s.Position, dozer.Position))
            .First(s => world.WhyCannotPlace(Race.Red, "MINE", null, 1, s.Position, 0) != "your constructors cannot build that yet");

        Assert.Equal("mine too far from a found mineral site", world.WhyCannotPlace(Race.Red, "MINE", null, 1, site.Position, 0));
        site.DiscoveredBy.Add(Race.Red);
        var bmuBefore = world.Races[Race.Red].Bmu;

        world.Issue(new PlaceConstructionCommand(Race.Red, [dozer.Id], "MINE", null, 1, site.Position, 0));
        Run(world, seconds: 120);

        var mine = world.Constructions.Single(c => c.Race == Race.Red && c.IsType("MINE"));
        Assert.True(mine.Complete);
        Assert.Equal(site.Id, mine.MineralSite);
        // A level-1 mine costs 50 BMUs; the pod trickle (6 per minute) adds a little back.
        Assert.InRange(bmuBefore - world.Races[Race.Red].Bmu, 50 - 6 * 2.1f, 50);
        Assert.True(mine.Ore > 0);
    }

    [Fact]
    public void A_mine_goes_near_a_site_anyone_found_and_sites_are_shared()
    {
        var world = NewMatch(red: Controller.Player);
        var site = OpenSite(world);
        // Found by Blue's locators only.
        site.DiscoveredBy.Add(Race.Blue);

        // Within the mine's MINERAL_DISTANCE (900, squared: 30 m) of the site, not only on top of it (MachLogMine::validMineSite).
        Assert.Null(world.WhyCannotPlace(Race.Red, "MINE", null, 1, site.Position + new Vector2(20, 12), 0));
        Assert.Equal("mine too far from a found mineral site", world.WhyCannotPlace(Race.Red, "MINE", null, 1, site.Position + new Vector2(32, 0), 0));

        // Red and Blue both dig there.
        var red = world.AddConstruction(Race.Red, site.Position + new Vector2(20, 12), "MINE", null, 1, 0, complete: false);
        var blue = world.AddConstruction(Race.Blue, site.Position - new Vector2(20, 12), "MINE", null, 1, 0, complete: false);
        Assert.Equal(site.Id, red.MineralSite);
        Assert.Equal(site.Id, blue.MineralSite);
        red.MakeComplete();
        blue.MakeComplete();
        var ore = site.Ore;
        Run(world, seconds: 10);
        Assert.True(red.Ore > 0 && blue.Ore > 0);
        // Both drain the one deposit (to float rounding over 200 ticks).
        Assert.InRange(site.Ore, ore - red.Ore - blue.Ore - 0.5f, ore - red.Ore - blue.Ore + 0.5f);
    }

    [Fact]
    public void A_mine_whose_site_runs_dry_moves_on_to_another_in_reach()
    {
        var world = NewMatch(red: Controller.Player);
        var site = OpenSite(world);
        site.DiscoveredBy.Add(Race.Red);
        var mine = world.AddConstruction(Race.Red, site.Position + new Vector2(15, 0), "MINE", null, 1, 0, complete: true);
        Assert.Equal(site.Id, mine.MineralSite);

        // A second deposit 25 m from the mine, found later; then the first runs dry.
        var second = new MineralSiteState(world.Sites.Count, new MineralSite(1, 500, new System.Numerics.Vector3(mine.Position.X, mine.Position.Y + 25, 0), ResourceLevel.Default, 0));
        world.Sites.Add(second);
        second.DiscoveredBy.Add(Race.Blue);
        site.Ore = 0;
        Run(world, seconds: 3);

        Assert.Equal(second.Id, mine.MineralSite);
        Assert.True(second.Ore < 500);
    }

    /// <summary>A site with open ground 40 m around it, far enough from every pod and smelter for a mine.</summary>
    private static MineralSiteState OpenSite(GameWorld world) => world.Sites.Where(s => s.Ore > 100).OrderBy(s => s.Id)
        .First(s => world.Nav.IsAreaFree((s.Position - new Vector2(40, 40), s.Position + new Vector2(40, 40)))
            && !world.Constructions.Any(c => c.Spec.Type is "SMELTER" or "POD" && Vector2.Distance(c.Position, s.Position) < 150));

    [Fact]
    public void Grunts_fight_with_the_original_weapons_and_armour()
    {
        var world = NewMatch(red: Controller.Player);
        var red = world.AddMachine(Race.Red, new Vector2(800, 800), "AGGRESSOR", "GRUNT", 2, "L_BOLTER");
        var blue = world.AddMachine(Race.Blue, new Vector2(820, 800), "AGGRESSOR", "GRUNT", 1, "R_BOLTER");

        world.Issue(new AttackCommand(Race.Red, [red.Id], blue.Id));
        Run(world, seconds: 60, until: () => blue.IsDead || red.IsDead);

        Assert.True(blue.IsDead || red.IsDead);
        Assert.True(red.HitPoints > 0 || blue.HitPoints > 0);
    }

    [Fact]
    public void Ai_against_ai_builds_an_economy_and_fights()
    {
        var world = NewMatch();
        var events = new List<GameEvent>();
        Run(world, seconds: 15 * 60, onTick: () => events.AddRange(world.Events));

        foreach (var race in new[] { Race.Red, Race.Blue })
        {
            Assert.Contains(world.Constructions, c => c.Race == race && c.IsType("MINE") && c.Complete);
            Assert.Contains(events, e => e.Kind == GameEventKind.MachineBuilt && world.Find(e.Other ?? 0)?.Race == race
                || e.Kind == GameEventKind.MachineBuilt);
        }
        Assert.Contains(events, e => e.Kind == GameEventKind.SiteDiscovered);
        Assert.Contains(events, e => e.Kind == GameEventKind.ConstructionComplete);
        Assert.Contains(events, e => e.Kind == GameEventKind.Hit);
    }

    [Fact]
    public void A_placed_turret_gets_the_weapons_of_its_research_item()
    {
        var world = NewMatch(red: Controller.Player);
        var turret = world.AddConstruction(Race.Red, new Vector2(500, 300), "MISSILE_EMPLACEMENT", "TURRET", 2, 0, complete: true);

        Assert.Equal("LR_AUTO_CANNON_X2", turret.WeaponCombo);
        Assert.Equal(["AUTO_CANNON", "AUTO_CANNON"], turret.Weapons.Select(w => w.Spec.TypeName));
    }

    [Fact]
    public void Buildings_work_as_the_original_switched_them_on()
    {
        var world = NewMatch(red: Controller.Player);
        var garrison = world.AddConstruction(Race.Red, new Vector2(500, 300), "GARRISON", null, 1, 0, complete: true);
        var site = world.AddConstruction(Race.Red, new Vector2(560, 300), "GARRISON", null, 1, 0, complete: false);
        var factory = world.AddConstruction(Race.Red, new Vector2(620, 300), "FACTORY", "CIVILIAN", 1, 0, complete: true);
        var smelter = world.AddConstruction(Race.Red, new Vector2(500, 380), "SMELTER", null, 1, 0, complete: true);

        Assert.True(world.IsWorking(garrison));
        Assert.False(world.IsWorking(site));
        Assert.False(world.IsWorking(factory));
        factory.ProductionQueue.Add(new ResearchKey("CONSTRUCTOR", "DOZER", 1, null));
        Assert.True(world.IsWorking(factory));
        Assert.False(world.IsWorking(smelter));
        smelter.OreDroppedTick = world.Tick;
        Assert.True(world.IsWorking(smelter));
    }

    [Fact]
    public void A_new_machine_comes_out_on_the_factory_pad_and_steps_off_it()
    {
        var world = NewMatch(red: Controller.Player);
        // Somewhere open, the pad included.
        var spot = Enumerable.Range(0, 400).Select(i => new Vector2(200 + i % 20 * 30, 200 + i / 20 * 30))
            .First(p => world.WhyCannotPlace(Race.Red, "FACTORY", "CIVILIAN", 1, p, 90) is null
                        && world.Nav.IsFree(p + new Vector2(-0.6f, 21), crossesWater: false));
        var factory = world.AddConstruction(Race.Red, spot, "FACTORY", "CIVILIAN", 1, 90, complete: true);
        world.Races[Race.Red].Bmu = 5000;
        factory.ProductionQueue.Add(new ResearchKey("CONSTRUCTOR", "DOZER", 1, null));

        GameEvent? built = null;
        for (var tick = 0; tick < 20 * 120 && built is null; tick++)
        {
            world.Step();
            built = world.Events.FirstOrDefault(e => e.Kind == GameEventKind.MachineBuilt && e.Other == factory.Id);
        }

        Assert.NotNull(built);
        Assert.NotNull(factory.EntranceOutside);
        Assert.InRange(Vector2.Distance(built.Position, factory.EntranceOutside!.Value), 0, 0.01f);
        Assert.IsType<MoveOrder>(world.Find<Machine>(built.Actor)!.Order);
    }

    [Fact]
    public void A_technician_walks_into_the_lab_through_its_door()
    {
        var world = NewMatch(red: Controller.Player);
        var spot = Enumerable.Range(0, 400).Select(i => new Vector2(200 + i % 20 * 30, 200 + i / 20 * 30))
            .First(p => world.WhyCannotPlace(Race.Red, "HARDWARE_LAB", "CIVILIAN", 1, p, 0) is null
                        && world.Nav.IsFree(p + new Vector2(30, 0), crossesWater: false));
        var lab = world.AddConstruction(Race.Red, spot, "HARDWARE_LAB", "CIVILIAN", 1, 0, complete: true);
        var technician = world.AddMachine(Race.Red, spot + new Vector2(30, 0), "TECHNICIAN", "LAB_TECH", 2, null);
        world.Issue(new EnterLabCommand(Race.Red, [technician.Id], lab.Id));

        var nearestToDoor = float.MaxValue;
        for (var tick = 0; tick < 20 * 60 && technician.Inside is null; tick++)
        {
            world.Step();
            nearestToDoor = MathF.Min(nearestToDoor, Vector2.Distance(technician.Position, lab.EntranceOutside!.Value));
        }

        Assert.Equal(lab.Id, technician.Inside);
        Assert.InRange(nearestToDoor, 0, 2);
        Assert.Contains(technician.Id, lab.Technicians);
    }

    [Fact]
    public void A_plasma_bolt_strikes_when_it_arrives_and_misses_a_target_that_stepped_aside()
    {
        var world = NewMatch(red: Controller.Player);
        var spot = Enumerable.Range(0, 400).Select(i => new Vector2(200 + i % 20 * 30, 200 + i / 20 * 30))
            .First(p => Enumerable.Range(0, 9).All(k => world.Nav.IsFree(p + new Vector2(k * 10, 0), crossesWater: false)
                                                        && world.Nav.IsFree(p + new Vector2(80, k * 3), crossesWater: false)));
        var shooter = world.AddMachine(Race.Red, spot, "AGGRESSOR", "ASSASSIN", 2, "L_PLASMA_RIFLE");
        var target = world.AddMachine(Race.Blue, spot + new Vector2(80, 0), "AGGRESSOR", "GRUNT", 1, "R_BOLTER");
        world.Issue(new AttackCommand(Race.Red, [shooter.Id], target.Id));

        // The first bolt: launched, and still in flight for the 80 m at 130 m/s.
        var fired = -1L;
        for (var tick = 0; tick < 20 * 10 && fired < 0; tick++)
        {
            world.Step();
            if (world.Projectiles.Count > 0)
                fired = world.Tick;
        }
        Assert.True(fired >= 0, "no bolt was fired");
        var bolt = world.Projectiles[0];
        var hitPoints = target.HitPoints;
        world.Step();
        Assert.Equal(hitPoints, target.HitPoints);

        // The target steps out of the bolt's way: it flies on past and is spent.
        target.Position += new Vector2(0, 15);
        while (world.Projectiles.Contains(bolt))
            world.Step();
        // It flew its whole range: nothing stopped it.
        Assert.InRange(Vector2.Distance(bolt.Position, bolt.Start), bolt.Weapon.Range - 1, bolt.Weapon.Range + 0.01f);
    }

    [Fact]
    public void A_driven_machine_follows_first_person_input()
    {
        var world = NewMatch(red: Controller.Player);
        var grunt = world.Machines.First(m => m.Race == Race.Red && m.IsType("AGGRESSOR"));
        var start = grunt.Position;
        // Sent every tick while driving, as the client does.
        Run(world, seconds: 1, onTick: () => world.Issue(new DriveCommand(Race.Red, grunt.Id, Driving: true, Forward: 1)));

        Assert.True(grunt.DrivenByPlayer);

        // A level-2 Grunt drives at its parmdata SPEED of 11 m/s, unless something blocks it.
        Assert.InRange(Vector2.Distance(start, grunt.Position), 0.1f, 11.5f);
    }

    [Fact]
    public void A_driven_machine_slides_along_a_wall_and_drives_off_blocked_ground()
    {
        var world = NewMatch(red: Controller.Player);
        var centre = Enumerable.Range(0, 400).Select(i => new Vector2(200 + i % 20 * 60, 200 + i / 20 * 60))
            .First(p => world.Nav.IsAreaFree((p - new Vector2(70, 70), p + new Vector2(70, 70))));
        var garrison = world.AddConstruction(Race.Red, centre, "GARRISON", null, 1, 0, complete: true);
        var (min, _) = garrison.Footprint;
        var grunt = world.AddMachine(Race.Red, new Vector2(min.X - 6, centre.Y), "AGGRESSOR", "GRUNT", 2, "L_BOLTER");
        void Drive() => world.Issue(new DriveCommand(Race.Red, grunt.Id, Driving: true, Forward: 1));

        // Driven at the wall at 45°, it goes on along it instead of stopping dead (MachLog1stPersonMachineHandler::nextPositionOk).
        grunt.Facing = MathF.PI / 4;
        Run(world, seconds: 2, onTick: Drive);
        Assert.True(grunt.Position.Y - centre.Y > 8, $"slid only to {grunt.Position}");
        Assert.True(world.Nav.IsFree(grunt.Position, crossesWater: false));

        // Set down on the building's blocked ground, it can still drive out towards open ground.
        grunt.Position = new Vector2(min.X + 1, centre.Y);
        grunt.Facing = MathF.PI;
        Assert.False(world.Nav.IsFree(grunt.Position, crossesWater: false));
        Run(world, seconds: 1, onTick: Drive);
        Assert.True(world.Nav.IsFree(grunt.Position, crossesWater: false), $"still stuck at {grunt.Position}");
    }

    [Fact]
    public void Machines_that_turn_their_upper_body_aim_with_it_the_others_turn_to_face()
    {
        var world = NewMatch(red: Controller.Player);
        // BODY_TURN_LINK: none for a level-1 Grunt, "body" for a level-2 one.
        var twister = world.AddMachine(Race.Red, new Vector2(800, 800), "AGGRESSOR", "GRUNT", 2, "L_BOLTER");
        var stiff = world.AddMachine(Race.Red, new Vector2(800, 820), "AGGRESSOR", "GRUNT", 1, "R_BOLTER");
        var target = world.AddMachine(Race.Blue, new Vector2(812, 812), "AGGRESSOR", "GRUNT", 1, "R_BOLTER");
        Assert.True(twister.CanTurnUpperBody);
        Assert.False(stiff.CanTurnUpperBody);

        world.Issue(new AttackCommand(Race.Red, [twister.Id, stiff.Id], target.Id));
        Run(world, seconds: 1);

        // The Grunt 2 keeps its legs where they stood and turns its body on them; the Grunt 1 turns to face.
        Assert.Equal(0, twister.Facing);
        Assert.Equal(MathF.Atan2(812 - 820, 812 - 800), stiff.Facing, 2);
    }

    [Fact]
    public void Wrecks_burn_out_and_scavengers_gather_them_for_their_race()
    {
        var world = NewMatch(red: Controller.Player);
        world.Races[Race.Red].Bmu = 100;

        // A level-2 Grunt leaves half its cost (MachLogMobile::dropDebris); without scavengers it burns for a minute.
        var grunt = world.AddMachine(Race.Blue, new Vector2(800, 800), "AGGRESSOR", "GRUNT", 2, "L_BOLTER");
        grunt.HitPoints = 0;
        world.Step();
        var wreck = Assert.Single(world.Debris);
        Assert.Equal(grunt.Stats.Cost / 2, wreck.Quantity);
        Assert.Equal(GameWorld.DebrisBurnTime, wreck.ExpiresAt - world.Time, 0.1f);
        Run(world, seconds: 61);
        Assert.Empty(world.Debris);

        // A half-built building leaves a fifth of what was put in (MachLogConstruction::dropDebris).
        var site = world.AddConstruction(Race.Blue, new Vector2(900, 900), "GARRISON", null, 1, 0, complete: false);
        site.AddConstruction(site.Stats.Cost / 2f);
        site.HitPoints = 0;
        world.Step();
        Assert.Equal((int)(site.Stats.Cost * 50 / 100f / 5), Assert.Single(world.Debris).Quantity);
        world.Debris.Clear();

        // An idle Red scavenger 60 m off sees a Blue wreck, drives onto it and banks its BMUs (MachLogResourceCarrier::update).
        var open = Enumerable.Range(0, 400).Select(i => new Vector2(200 + i % 20 * 60, 200 + i / 20 * 60))
            .First(p => world.Nav.IsAreaFree((p - new Vector2(70, 70), p + new Vector2(70, 70))));
        var scavenger = world.AddMachine(Race.Red, open - new Vector2(60, 0), "RESOURCE_CARRIER", null, 3, null);
        Assert.True(scavenger.IsScavenger);
        var knight = world.AddMachine(Race.Blue, open, "AGGRESSOR", "KNIGHT", 3, "LR_HEAVY_BOLTER2_X2");
        knight.HitPoints = 0;
        world.Step();
        Assert.Single(world.Debris);
        var before = world.Races[Race.Red].Bmu;
        var events = new List<GameEvent>();
        Run(world, seconds: 15, until: () => world.Debris.Count == 0, onTick: () => events.AddRange(world.Events));

        Assert.Empty(world.Debris);
        Assert.Contains(events, e => e.Kind == GameEventKind.Scavenged && e.Actor == scavenger.Id);
        Assert.True(Vector2.Distance(scavenger.Position, open) <= 2.01f);
        // The pod trickles in its few BMUs meanwhile.
        Assert.InRange(world.Races[Race.Red].Bmu - before, knight.Stats.Cost / 2, knight.Stats.Cost / 2 + 2);
    }

    [Fact]
    public void A_locator_finds_a_deposit_only_by_driving_onto_it()
    {
        var world = NewMatch(red: Controller.Player);
        var site = OpenSite(world);
        // Sent past the deposit, 30 m to the side of the way: within its scanner range, not over it.
        var start = site.Position + new Vector2(-40, 30);
        var target = site.Position + new Vector2(40, 30);
        var locator = world.AddMachine(Race.Red, start, "GEO_LOCATOR", null, 1, null);
        Assert.True(locator.Stats.ScannerRange > 30);
        world.Issue(new LocateCommand(Race.Red, [locator.Id], target));
        world.Step();
        Assert.DoesNotContain(Race.Red, site.DiscoveredBy);

        // It turns off to drive over the deposit, which it finds only there (MachLogLocateOperation), then goes on.
        Vector2? foundAt = null;
        Run(world, seconds: 60, until: () => locator.Order is IdleOrder, onTick: () =>
        {
            if (foundAt is null && world.Events.Any(e => e.Kind == GameEventKind.SiteDiscovered && e.Other == site.Id))
                foundAt = locator.Position;
        });
        Assert.NotNull(foundAt);
        Assert.True(Vector2.Distance(foundAt.Value, site.Position) < 1, $"found it from {foundAt}");
        Assert.Contains(Race.Red, site.DiscoveredBy);
        Assert.True(Vector2.Distance(locator.Position, target) < 1);
    }

    [Fact]
    public void Carriers_load_and_drop_ore_standing_on_the_pads()
    {
        var world = NewMatch(red: Controller.Player);
        var site = OpenSite(world);
        site.DiscoveredBy.Add(Race.Red);
        var mine = world.AddConstruction(Race.Red, site.Position, "MINE", null, 1, 0, complete: true);
        var smelter = world.AddConstruction(Race.Red, site.Position + new Vector2(120, 0), "SMELTER", null, 1, 90, complete: true);
        Assert.Equal(2, mine.PickUpPads.Count);
        Assert.Equal(2, smelter.PutDownPads.Count);
        mine.Ore = 100;
        var carrier = world.AddMachine(Race.Red, site.Position + new Vector2(60, 40), "RESOURCE_CARRIER", null, 1, null);
        var reach = carrier.Stats.HighClearance * 1.5f;
        world.Issue(new TransportCommand(Race.Red, [carrier.Id], mine.Id, smelter.Id));

        // Loading happens on one of the mine's pads (PICKUP in parmdata), dropping on the smelter's (PUTDOWN).
        Vector2? loadedAt = null, droppedAt = null;
        var ore = 0;
        Run(world, seconds: 90, until: () => droppedAt is not null, onTick: () =>
        {
            if (carrier.Ore > ore)
                loadedAt ??= carrier.Position;
            else if (carrier.Ore < ore)
                droppedAt ??= carrier.Position;
            ore = carrier.Ore;
        });
        Assert.NotNull(loadedAt);
        Assert.NotNull(droppedAt);
        Assert.Contains(mine.PickUpPads, p => Vector2.Distance(p, loadedAt.Value) <= reach);
        Assert.Contains(smelter.PutDownPads, p => Vector2.Distance(p, droppedAt.Value) <= reach);
    }

    /// <summary>Flat ground with a ridge 30 m high running north to south at x 820 to 830.</summary>
    private static Machines.Sim.Navigation.TerrainHeights Ridge(GameWorld world)
    {
        const float spacing = 2;
        var heights = new float[(int)(world.Planet.Surface.Width / spacing) + 1, (int)(world.Planet.Surface.Depth / spacing) + 1];
        for (var x = 0; x < heights.GetLength(0); x++)
            for (var y = 0; y < heights.GetLength(1); y++)
                heights[x, y] = x * spacing is >= 820 and <= 830 ? 30 : 0;
        return new Machines.Sim.Navigation.TerrainHeights(heights, spacing);
    }

    [Fact]
    public void A_tower_does_not_shoot_through_a_hill()
    {
        foreach (var hill in new[] { false, true })
        {
            var world = NewMatch(red: Controller.Player);
            if (hill)
                world.Terrain = Ridge(world);
            var tower = world.AddConstruction(Race.Red, new Vector2(800, 800), "MISSILE_EMPLACEMENT", "TURRET", 2, 0, complete: true);
            var range = tower.Weapons.Min(w => w.Spec.Range);
            // An unarmed target, which stays where it is.
            var target = world.AddMachine(Race.Blue, new Vector2(800 + MathF.Min(range * 0.8f, 45), 800), "CONSTRUCTOR", "DOZER", 1, null);
            Assert.True(target.Position.X > 830);
            var events = new List<GameEvent>();
            Run(world, seconds: 3, onTick: () => events.AddRange(world.Events));

            // Over open ground the tower fires; with the ridge between them it holds its fire (canTurnToAttackAndNoCover).
            var fired = events.Count(e => e.Kind == GameEventKind.Fired && e.Actor == tower.Id);
            if (hill)
            {
                Assert.Equal(0, fired);
                Assert.Equal(target.MaxHitPoints, target.HitPoints);
            }
            else
                Assert.True(fired > 0);
        }
    }

    [Fact]
    public void A_round_flying_into_a_hill_strikes_the_ground_there()
    {
        var world = NewMatch(red: Controller.Player);
        world.Terrain = Ridge(world);
        // A plasma rifle's bolts fly to their mark (they can be dodged); driven, it fires at a point beyond the ridge.
        var assassin = world.AddMachine(Race.Red, new Vector2(800, 800), "AGGRESSOR", "ASSASSIN", 2, "L_PLASMA_RIFLE");
        Assert.True(GameWorld.Flies(assassin.Weapons[0]));
        var events = new List<GameEvent>();
        Run(world, seconds: 3, onTick: () =>
        {
            world.Issue(new DriveCommand(Race.Red, assassin.Id, Driving: true, Aim: new Vector2(860, 800)));
            events.AddRange(world.Events);
        });

        var impact = events.First(e => e.Kind == GameEventKind.Impact);
        Assert.InRange(impact.Position.X, 817, 821.5f);
        Assert.DoesNotContain(world.Projectiles, p => p.Position.X > 830);
    }

    /// <summary>A place with open ground 70 m round it.</summary>
    private static Vector2 OpenGround(GameWorld world) => Enumerable.Range(0, 400).Select(i => new Vector2(200 + i % 20 * 60, 200 + i / 20 * 60))
        .First(p => world.Nav.IsAreaFree((p - new Vector2(70, 70), p + new Vector2(70, 70))));

    [Fact]
    public void Initiative_decides_what_machines_take_on_of_their_own_accord()
    {
        var world = NewMatch(red: Controller.Player);
        var at = OpenGround(world);
        // An unarmed enemy beyond the Grunt's weapon range (35 m), within its scanner range.
        var dozer = world.AddMachine(Race.Blue, at + new Vector2(55, 0), "CONSTRUCTOR", "DOZER", 1, null);
        var normal = world.AddMachine(Race.Red, at, "AGGRESSOR", "GRUNT", 2, "L_BOLTER");
        var low = world.AddMachine(Race.Red, at + new Vector2(0, 8), "AGGRESSOR", "GRUNT", 2, "L_BOLTER");
        var standing = world.AddMachine(Race.Red, at + new Vector2(0, -8), "AGGRESSOR", "GRUNT", 2, "L_BOLTER");
        Assert.True(normal.Stats.ScannerRange > 55);
        Assert.Equal(DefCon.Normal, normal.DefCon);
        world.Issue(new DefConCommand(Race.Red, [low.Id], DefCon.Low));
        world.Issue(new DefConCommand(Race.Red, [standing.Id], DefCon.Low));
        world.Issue(new StandGroundCommand(Race.Red, [standing.Id]));
        Run(world, seconds: 1);

        // Low initiative hunts it down; normal leaves what it cannot shoot from where it stands and what does not
        // threaten it; standing ground never moves, whatever its initiative.
        Assert.IsType<AttackOrder>(low.Order);
        Assert.IsType<IdleOrder>(normal.Order);
        Assert.IsType<StandGroundOrder>(standing.Order);
        var standingAt = standing.Position;
        Run(world, seconds: 5);
        Assert.Equal(standingAt, standing.Position);
        Assert.True(Vector2.Distance(low.Position, dozer.Position) < Vector2.Distance(at, dozer.Position) - 5);
    }

    [Fact]
    public void A_machine_on_the_move_breaks_off_to_fight_only_with_the_initiative_and_then_moves_on()
    {
        foreach (var defCon in new[] { DefCon.Low, DefCon.High })
        {
            // Blue is played too, so no AI moves its dozer.
            var world = MatchSetup.Create(DataLoader.Value, "m_desert", "m_desert", new Dictionary<Race, Controller> { [Race.Red] = Controller.Player, [Race.Blue] = Controller.Player }, 7);
            var at = OpenGround(world);
            var grunt = world.AddMachine(Race.Red, at - new Vector2(60, 0), "AGGRESSOR", "GRUNT", 2, "L_BOLTER");
            world.Issue(new DefConCommand(Race.Red, [grunt.Id], defCon));
            // Sent slowly past an enemy dozer, at walking pace it is still on the way after VIRTUAL_DEFCON_INTERVAL.
            var destination = at + new Vector2(60, 0);
            var dozer = world.AddMachine(Race.Blue, at + new Vector2(0, 40), "CONSTRUCTOR", "DOZER", 1, null);
            // Worn down already, so the fight is short.
            (dozer.HitPoints, dozer.Armour) = (3, 0);
            world.Issue(new MoveCommand(Race.Red, [grunt.Id], destination));
            var attacked = false;
            Run(world, seconds: 40, onTick: () => attacked |= grunt.Order is AttackOrder { Resume: true });

            if (defCon == DefCon.High)
            {
                Assert.False(attacked);
                Assert.False(dozer.IsDead);
            }
            else
            {
                // It fought, and then went on to where it was sent.
                Assert.True(attacked);
                Assert.True(dozer.IsDead);
            }
            Assert.True(Vector2.Distance(grunt.Position, destination) < 3, $"{defCon} ended at {grunt.Position}");
        }
    }

    [Fact]
    public void Spies_lay_land_mines_which_go_off_under_enemies_and_refill_at_a_garrison()
    {
        var world = NewMatch(red: Controller.Player);
        var at = OpenGround(world);
        var spy = world.AddMachine(Race.Red, at, "SPY_LOCATOR", null, 3, null);
        Assert.Equal(5, spy.Mines);
        var points = new[] { at + new Vector2(20, 0), at + new Vector2(20, 20) };
        world.Issue(new DropLandMinesCommand(Race.Red, [spy.Id], points));
        Run(world, seconds: 15, until: () => spy.Order is IdleOrder);

        Assert.Equal(3, spy.Mines);
        Assert.Equal(points, world.LandMines.Select(m => m.Position).Select(p => new Vector2(MathF.Round(p.X), MathF.Round(p.Y))));

        // An enemy driving over the first goes up with it: 80 points of damage (MachLogLandMine::update).
        var grunt = world.AddMachine(Race.Blue, at + new Vector2(20, -30), "AGGRESSOR", "GRUNT", 3, "LR_HEAVY_BOLTER1_X2");
        var durability = grunt.Durability;
        world.Issue(new MoveCommand(Race.Blue, [grunt.Id], at + new Vector2(20, 0)));
        var events = new List<GameEvent>();
        Run(world, seconds: 10, until: () => world.LandMines.Count < 2, onTick: () => events.AddRange(world.Events));
        Assert.Single(world.LandMines);
        Assert.Contains(events, e => e.Kind == GameEventKind.MineExploded && e.Actor == grunt.Id);
        Assert.Equal(Machines.Sim.Combat.ArmourModel.ApplyHit(durability, 80), grunt.Durability);

        // At its garrison it takes on a full load and comes back.
        var garrison = world.AddConstruction(Race.Red, at + new Vector2(-40, 30), "GARRISON", null, 1, 0, complete: true);
        var back = spy.Position;
        world.Issue(new RefillLandMinesCommand(Race.Red, [spy.Id]));
        Run(world, seconds: 30, until: () => spy.Mines == 5 && spy.Order is IdleOrder);
        Assert.Equal(5, spy.Mines);
        Assert.True(Vector2.Distance(spy.Position, back) < 3);
    }

    [Fact]
    public void A_camouflaged_spy_is_passed_over_by_the_enemy_s_own_choice_of_targets()
    {
        var world = NewMatch(red: Controller.Player);
        var at = OpenGround(world);
        var tower = world.AddConstruction(Race.Blue, at, "MISSILE_EMPLACEMENT", "TURRET", 2, 0, complete: true);
        var spy = world.AddMachine(Race.Red, at + new Vector2(30, 0), "SPY_LOCATOR", null, 3, null);
        world.Issue(new CamouflageCommand(Race.Red, [spy.Id]));
        Run(world, seconds: 3);
        Assert.True(spy.Camouflaged);
        Assert.Equal(spy.MaxHitPoints, spy.HitPoints);
        Assert.Null(world.AimOf(tower.Id));
    }

    [Fact]
    public void Forming_a_squadron_takes_the_selected_machines_and_lets_its_old_members_go()
    {
        var world = NewMatch(red: Controller.Player);
        var grunts = world.Machines.Where(m => m.Race == Race.Red && m.IsType("AGGRESSOR")).Select(m => m.Id).ToList();
        var dozers = world.Machines.Where(m => m.Race == Race.Red && m.IsType("CONSTRUCTOR")).Select(m => m.Id).ToList();
        world.Issue(new FormSquadronCommand(Race.Red, grunts, 3));
        world.Step();
        Assert.All(grunts, id => Assert.Equal(3, world.Find<Machine>(id)!.Squad));

        world.Issue(new FormSquadronCommand(Race.Red, dozers, 3));
        world.Step();
        Assert.All(grunts, id => Assert.Null(world.Find<Machine>(id)!.Squad));
        Assert.All(dozers, id => Assert.Equal(3, world.Find<Machine>(id)!.Squad));
    }

    [Fact]
    public void Same_seed_gives_the_same_match()
    {
        string Fingerprint(GameWorld world) => string.Join(';', world.Actors.OrderBy(a => a.Id)
            .Select(a => $"{a.Id}:{a.Position.X:F2},{a.Position.Y:F2}:{a.HitPoints}")) + $"|{world.Races[Race.Red].Bmu:F2}|{world.Races[Race.Blue].Bmu:F2}";

        var a = NewMatch(seed: 3);
        var b = NewMatch(seed: 3);
        Run(a, seconds: 180);
        Run(b, seconds: 180);

        Assert.Equal(Fingerprint(a), Fingerprint(b));
    }

    private static void Run(GameWorld world, float seconds, Func<bool>? until = null, Action? onTick = null)
    {
        for (var i = 0; i < seconds * GameWorld.TicksPerSecond; i++)
        {
            world.Step();
            onTick?.Invoke();
            if (until?.Invoke() == true)
                return;
        }
    }
}
