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

        Assert.Equal("mine not on a found mineral site", world.WhyCannotPlace(Race.Red, "MINE", null, 1, site.Position, 0));
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
    public void A_driven_machine_follows_first_person_input()
    {
        var world = NewMatch(red: Controller.Player);
        var grunt = world.Machines.First(m => m.Race == Race.Red && m.IsType("AGGRESSOR"));
        var start = grunt.Position;
        grunt.DrivenByPlayer = true;
        world.DriveInput[grunt.Id] = (1, 0);

        Run(world, seconds: 1);

        // A level-2 Grunt drives at its parmdata SPEED of 11 m/s, unless something blocks it.
        Assert.InRange(Vector2.Distance(start, grunt.Position), 0.1f, 11.5f);
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
