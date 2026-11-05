using System.Numerics;
using Machines.Import.Model;
using Machines.Import.Scenarios;
using Machines.Sim;
using Machines.Sim.Ai;
using Machines.Sim.World;

namespace Machines.Tests.Sim;

/// <summary>
/// What an AI squad does with its script's lines (MachLogTaskAttackOperation, MachLogRaces::findTargetClosestTo and
/// findMostValuableTarget, MachLogDropLandMineOperation), the research it does and the allies it makes.
/// </summary>
public class AiAttackTests
{
    private static readonly Lazy<GameData> DataLoader = new(() => GameData.Load(RepoPaths.OriginalDataFolder()));
    private static readonly Vector2 Field = new(800, 800);
    private static readonly Vector2 Point = new(700, 700);

    /// <summary>m_desert with Blue on the squads given, which gather at <see cref="Point"/>, and nothing of its own in them.</summary>
    private static GameWorld Match(string squads)
    {
        var data = DataLoader.Value;
        var world = MatchSetup.Create(data, "m_desert", "m_desert",
            new Dictionary<Race, Controller> { [Race.Red] = Controller.Player, [Race.Blue] = Controller.Ai }, seed: 7);
        var setup = data.LoadScenario("m_desert").Races.Single(r => r.Race == Race.Blue);
        world.AiControllers.Clear();
        world.AiControllers.Add(new AiController(Race.Blue, AiScriptParser.Parse($"PRODUCTION LIST\nEND PRODUCTION LIST\n{squads}"),
            setup with { AggressorAssemblyPoints = [Point] }));
        foreach (var machine in world.Machines.Where(m => m.Race == Race.Blue))
            machine.Squad = 9;
        return world;
    }

    private static Machine Grunt(GameWorld world, Race race, Vector2 at, int squad = 0)
    {
        var grunt = world.AddMachine(race, FormationTests.FreePointNear(world, at), "AGGRESSOR", "GRUNT", 2, "L_BOLTER");
        grunt.Squad = squad;
        return grunt;
    }

    private static void Run(GameWorld world, float seconds, Func<bool>? until = null)
    {
        for (var i = 0; i < seconds * GameWorld.TicksPerSecond; i++)
        {
            world.Step();
            if (until?.Invoke() == true)
                return;
        }
    }

    private static bool SetOut(Machine machine) => machine.Order is AttackMoveOrder or AttackOrder;

    [Fact]
    public void A_mission_script_calling_in_squadron_1_starts_the_first_squad_and_no_other()
    {
        var world = Match("SQUAD\nAI ATTACK DELAY 999 ASSEMBLY 0\nEND SQUAD\nSQUAD\nAI ATTACK DELAY 999 ASSEMBLY 0\nEND SQUAD");
        var lines = ScenarioParser.Parse("RACES_DEFAULT\nRED PC\nENDRACES_DEFAULT\nCONDITIONS\nTIMER now 1\nENDCONDITIONS\nACTIONS\nACTIVATE_SQUADRONS ON now RACE BLUE SQUADRONS 1\nENDACTIONS\n");
        world.Script = MissionScript.Compile(DataLoader.Value.LoadScenario("m_desert") with { Conditions = lines.Conditions, Actions = lines.Actions }, null);
        var first = Grunt(world, Race.Blue, Point);
        var second = Grunt(world, Race.Blue, Point + new Vector2(10, 0), squad: 1);

        Run(world, 40, () => SetOut(first));

        Assert.True(SetOut(first));
        Assert.False(SetOut(second));
    }

    [Fact]
    public void A_squad_with_a_minimum_sets_out_only_once_everyone_is_within_50_m_of_its_assembly_point()
    {
        var world = Match("SQUAD\nAI ATTACK DELAY 0 MINIMUM 2 ASSEMBLY 0\nEND SQUAD");
        var near = Grunt(world, Race.Blue, Point + new Vector2(10, 0));
        var far = Grunt(world, Race.Blue, Point + new Vector2(150, 0));

        Run(world, 8);
        Assert.False(SetOut(near));
        Assert.IsType<MoveOrder>(far.Order);

        Run(world, 120, () => SetOut(near) && SetOut(far));
        Assert.True(SetOut(near) && SetOut(far));
        Assert.InRange(Vector2.Distance(far.Position, Point), 0, 52);
    }

    [Fact]
    public void Once_all_who_set_out_are_gone_the_squad_gathers_again()
    {
        var world = Match("SQUAD\nAI ATTACK DELAY 0 MINIMUM 1 ASSEMBLY 0\nEND SQUAD");
        var first = Grunt(world, Race.Blue, Point);
        Run(world, 60, () => SetOut(first));
        Assert.True(SetOut(first));

        world.Damage(first, 1_000_000, Race.Red);
        var next = Grunt(world, Race.Blue, Point + new Vector2(150, 0));
        Run(world, 25);

        Assert.False(SetOut(next));
        Assert.IsType<MoveOrder>(next.Order);
    }

    [Theory]
    [InlineData("AI ATTACK DELAY 0 ASSEMBLY 0")]
    [InlineData("AI ATTACK DELAY 0 MINIMUM 1 NO_CLUSTER_CHECK ASSEMBLY 0")]
    public void A_squad_without_a_proximity_check_goes_at_once_from_where_it_stands(string line)
    {
        var world = Match($"SQUAD\n{line}\nEND SQUAD");
        var far = Grunt(world, Race.Blue, Point + new Vector2(150, 0));

        Run(world, 15, () => SetOut(far));

        Assert.True(SetOut(far));
        Assert.True(Vector2.Distance(far.Position, Point) > 100);
    }

    [Fact]
    public void A_squad_below_its_minimum_waits_at_its_assembly_point()
    {
        var world = Match("SQUAD\nAI ATTACK DELAY 0 MINIMUM 2 ASSEMBLY 0\nEND SQUAD");
        var alone = Grunt(world, Race.Blue, Point + new Vector2(150, 0));

        Run(world, 60);

        Assert.False(SetOut(alone));
        Assert.InRange(Vector2.Distance(alone.Position, Point), 0, 50);
    }

    [Theory]
    [InlineData("DO 1", 3)]
    [InlineData("", 4)]
    public void Do_ends_the_squads_rebuilding(string count, int left)
    {
        var world = Match($"SQUAD\nAI ATTACK DELAY 0 {count}\nNUMBER 4 0 CONSTRUCTOR 1 DOZER\nEND SQUAD");
        world.Races[Race.Blue].Bmu = 50_000;
        for (var i = 0; i < 4; i++)
            world.AddMachine(Race.Blue, FormationTests.FreePointNear(world, Point + new Vector2(i * 10, 0)), "CONSTRUCTOR", "DOZER", 1, null).Squad = 0;
        List<Machine> Squad() => world.Machines.Where(m => m.Race == Race.Blue && m.Squad == 0 && !m.IsDead).ToList();
        Run(world, 40);
        Assert.Equal(4, Squad().Count);

        world.Damage(Squad()[0], 1_000_000, Race.Red);
        Run(world, 300, () => Squad().Count == 4);

        Assert.Equal(left, Squad().Count);
    }

    [Fact]
    public void What_is_left_of_a_squad_past_its_do_fights_on()
    {
        var world = Match("SQUAD\nAI ATTACK DELAY 0 DO 1 ASSEMBLY 0\nEND SQUAD");
        var grunt = Grunt(world, Race.Blue, Point);

        Run(world, 15, () => SetOut(grunt));

        Assert.True(SetOut(grunt));
    }

    [Fact]
    public void The_ai_researches_everything_open_to_it_not_only_what_a_squad_is_short_of()
    {
        var world = Match("");
        world.Races[Race.Blue].Bmu = 50_000;
        var lab = world.AddConstruction(Race.Blue, Point, "HARDWARE_LAB", "MILITARY", 1, 0, complete: true);
        var technician = world.AddMachine(Race.Blue, FormationTests.FreePointNear(world, Point + new Vector2(30, 0)), "TECHNICIAN", "LAB_TECH", 2, null);
        Assert.True(world.Races[Race.Blue].Available.Count > 1);

        Run(world, 120, () => lab.ResearchQueue.Count > 1);

        Assert.True(lab.ResearchQueue.Count > 1);
        Assert.Contains(technician.Id, lab.Technicians);
    }

    [Fact]
    public void A_squad_goes_for_what_can_fight_before_what_stands_nearer()
    {
        var world = Match("SQUAD\nAI ATTACK DELAY 0 ASSEMBLY 0\nEND SQUAD");
        var grunt = Grunt(world, Race.Blue, Field);
        var dozer = world.AddMachine(Race.Red, FormationTests.FreePointNear(world, grunt.Position + new Vector2(14, 0)), "CONSTRUCTOR", "DOZER", 1, null);
        var fighter = Grunt(world, Race.Red, grunt.Position + new Vector2(26, 0), squad: 1);

        Run(world, 5, () => grunt.Order is AttackOrder);

        Assert.Equal(fighter.Id, Assert.IsType<AttackOrder>(grunt.Order).Target);
        Assert.False(dozer.IsDead);
    }

    [Fact]
    public void A_squad_after_resources_goes_for_carriers_and_mines_not_the_fighters_next_to_it()
    {
        var world = Match("SQUAD\nAI ATTACK DELAY 0 RESOURCE ASSEMBLY 0\nEND SQUAD");
        var grunt = Grunt(world, Race.Blue, Field);
        // Its own look round would find the fighter in reach of its scanner; here it is the squad's choice that counts.
        grunt.Alert.NextLook = 5;
        Grunt(world, Race.Red, grunt.Position + new Vector2(0, 120), squad: 1);
        world.AddMachine(Race.Red, FormationTests.FreePointNear(world, Field + new Vector2(200, 0)), "RESOURCE_CARRIER", null, 1, null);
        var wanted = world.Actors.Where(a => a.Race == Race.Red && (a is Machine { Spec.Type: "RESOURCE_CARRIER" } || a is Construction c && (c.IsType("MINE") || c.IsType("SMELTER"))))
            .MinBy(a => Vector2.Distance(a.Position, grunt.Position))!;

        Run(world, 5, () => SetOut(grunt));

        // What it meets on the way it takes on, and goes on from there.
        var heading = grunt.Order switch { AttackMoveOrder move => move.Target, AttackOrder attack => attack.Then, _ => null };
        Assert.InRange(Vector2.Distance(wanted.Position, heading!.Value), 0, 1);
    }

    [Fact]
    public void An_ion_strike_goes_for_the_most_valuable_target_but_not_one_right_at_the_cannon()
    {
        var world = Match("SQUAD\nAI ATTACK DELAY 0 USE ION_ORBITAL_CANNON\nEND SQUAD");
        var pod = world.Constructions.First(c => c.Race == Race.Blue && c.IsType("POD"));
        world.Races[Race.Blue].Researched.Add(GameWorld.IonCannonResearch);
        world.Step();
        var cannon = Assert.Single(pod.Weapons);
        cannon.Reload = 0;
        // 50 compared with the squared distance, as in the original: about 7 m. The strongest of Red stands within it.
        foreach (var side in new[] { new Vector2(5, 0), new Vector2(-5, 0), new Vector2(0, 5), new Vector2(0, -5), new Vector2(3, 3) })
            world.AddMachine(Race.Red, pod.Position + side, "AGGRESSOR", "KNIGHT", 3, "LR_HEAVY_BOLTER2_X2").Squad = 1;

        Run(world, 40, () => world.Blasts.Count > 0);

        Assert.True(Vector2.Distance(Assert.Single(world.Blasts).Position, pod.Position) > 7);
    }

    [Fact]
    public void A_spy_lays_its_mines_in_the_ring_round_the_pod_and_goes_for_more()
    {
        var world = Match("SQUAD\nAI DROP_MINES 40 60 POD\nEND SQUAD");
        var pod = world.Constructions.First(c => c.Race == Race.Blue && c.IsType("POD"));
        var spy = world.AddMachine(Race.Blue, FormationTests.FreePointNear(world, pod.Position + new Vector2(50, 0)), "SPY_LOCATOR", null, 3, null);
        spy.Squad = 0;
        var mines = spy.Mines;

        Run(world, 180, () => world.LandMines.Count == mines);

        Assert.Equal(mines, world.LandMines.Count);
        Assert.All(world.LandMines, mine => Assert.InRange(Vector2.Distance(mine.Position, pod.Position), 25, 75));
    }

    [Fact]
    public void A_squads_defcon_holds_for_whoever_joins_it()
    {
        var world = Match("SQUAD\nDEFCON HIGH\nAI ATTACK DELAY 100 ASSEMBLY 0\nEND SQUAD");
        var grunt = Grunt(world, Race.Blue, Point);
        grunt.DefCon = DefCon.Low;

        Run(world, 5);

        Assert.Equal(DefCon.High, grunt.DefCon);
    }

    // ---- alliances --------------------------------------------------------------------------------

    private static (string Name, string Planet) ThreeRaceMap()
    {
        var catalogue = ScenarioCatalogueParser.Load(RepoPaths.OriginalData("sceninfo.dat"));
        var map = catalogue.Skirmish.SelectMany(t => t.Scenarios).First(s => DataLoader.Value.LoadScenario(s.Name).Races.Count >= 3);
        return (map.Name, map.Planet);
    }

    private static GameWorld Three(MatchOptions options)
    {
        var (name, planet) = ThreeRaceMap();
        var races = DataLoader.Value.LoadScenario(name).Races.Select(r => r.Race).Order().Take(3).ToList();
        return MatchSetup.Create(DataLoader.Value, name, planet,
            new Dictionary<Race, Controller> { [races[0]] = Controller.Player, [races[1]] = Controller.Ai, [races[2]] = Controller.Ai }, seed: 5, options);
    }

    [Fact]
    public void Computers_ally_against_whoever_is_winning()
    {
        var world = Three(new MatchOptions());
        var races = world.Races.Keys.Order().ToList();
        world.Races[races[0]].Score.BmusMined = 400_000;

        Run(world, 320, () => !world.AreEnemies(races[1], races[2]));

        Assert.False(world.AreEnemies(races[1], races[2]));
        Assert.False(world.AreEnemies(races[2], races[1]));
        Assert.True(world.AreEnemies(races[1], races[0]));
    }

    [Fact]
    public void Nobody_changes_sides_in_a_campaign_mission()
    {
        var world = Three(new MatchOptions { Victory = VictoryCondition.Scripted });
        var races = world.Races.Keys.Order().ToList();
        var before = world.AreEnemies(races[1], races[2]);
        world.Races[races[0]].Score.BmusMined = 400_000;

        Run(world, 320);

        Assert.Equal(before, world.AreEnemies(races[1], races[2]));
    }

    // ---- what a scenario says of its races ---------------------------------------------------------

    private static GameWorld Scenario(string name, VictoryCondition victory)
    {
        var catalogue = ScenarioCatalogueParser.Load(RepoPaths.OriginalData("sceninfo.dat"));
        var entry = catalogue.AllScenarios.First(s => s.Name == name);
        var races = DataLoader.Value.LoadScenario(name).Races.Select(r => r.Race).Order().ToList();
        return MatchSetup.Create(DataLoader.Value, name, entry.Planet,
            races.ToDictionary(r => r, r => r == races[0] ? Controller.Player : Controller.Ai), seed: 5, new MatchOptions { Victory = victory });
    }

    [Fact]
    public void Default_victory_is_the_scenarios_own_script_when_it_has_one()
    {
        var world = Scenario("seige", VictoryCondition.Default);

        Assert.NotNull(world.Script);
        Assert.Equal(VictoryCondition.Default, world.Options.Victory);
    }

    [Fact]
    public void Default_victory_on_a_map_without_a_script_is_annihilation()
    {
        var world = MatchSetup.Create(DataLoader.Value, "m_desert", "m_desert",
            new Dictionary<Race, Controller> { [Race.Red] = Controller.Player, [Race.Blue] = Controller.Ai }, seed: 7,
            new MatchOptions { Victory = VictoryCondition.Default });

        Assert.Null(world.Script);
        Assert.Equal(VictoryCondition.Annihilation, world.Options.Victory);
    }

    [Fact]
    public void Default_victory_falls_to_annihilation_when_the_enemy_is_gone()
    {
        var world = MatchSetup.Create(DataLoader.Value, "m_desert", "m_desert",
            new Dictionary<Race, Controller> { [Race.Red] = Controller.Player, [Race.Blue] = Controller.Ai }, seed: 7,
            new MatchOptions { Victory = VictoryCondition.Default });

        foreach (var actor in world.Actors.Where(a => a.Race == Race.Blue).ToList())
            world.Damage(actor, 10_000_000, Race.Red);
        Run(world, 10, () => world.Winner is not null);

        Assert.Equal(Race.Red, world.Winner);
    }

    [Fact]
    public void A_virtual_beacon_counts_as_one_that_stands()
    {
        var setup = DataLoader.Value.LoadScenario("1o2").Races.First(r => r.VirtualBeacon is not null);
        var world = Scenario("1o2", VictoryCondition.Scripted);
        foreach (var beacon in world.Constructions.Where(c => c.Race == setup.Race && c.IsType("BEACON")).ToList())
            world.Damage(beacon, 10_000_000, Race.Red);
        Run(world, 1);

        Assert.Equal(setup.VirtualBeacon == "LEVEL_3_BEACON" ? 3 : 1, world.BeaconLevel(setup.Race));
    }

    [Fact]
    public void A_race_without_a_beacon_has_none()
    {
        var world = Match("");

        Assert.Equal(0, world.BeaconLevel(Race.Blue));
    }

    [Fact]
    public void A_race_with_score_off_is_marked_for_the_statistics()
    {
        var scenario = DataLoader.Value.LoadScenario("1o2");
        var world = Scenario("1o2", VictoryCondition.Scripted);

        Assert.Contains(scenario.Races, r => !r.ScoreDisplayed);
        Assert.All(scenario.Races, r => Assert.Equal(r.ScoreDisplayed, world.Races[r.Race].ScoreDisplayed));
    }
}
