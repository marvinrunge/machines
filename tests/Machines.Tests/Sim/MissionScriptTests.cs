using System.Numerics;
using Machines.Import.Audio;
using Machines.Import.Model;
using Machines.Import.Scenarios;
using Machines.Sim;
using Machines.Sim.World;

namespace Machines.Tests.Sim;

/// <summary>The campaign missions' scripts (CONDITIONS and ACTIONS), run as the original ran them.</summary>
public class MissionScriptTests
{
    private static readonly Lazy<GameData> DataLoader = new(() => GameData.Load(RepoPaths.OriginalDataFolder()));
    private static readonly Lazy<ScenarioCatalogue> Catalogue = new(() => ScenarioCatalogueParser.Load(RepoPaths.OriginalData("sceninfo.dat")));

    public static TheoryData<string> CampaignScenarios()
    {
        var names = new TheoryData<string>();
        foreach (var scenario in Catalogue.Value.CampaignScenarios)
            names.Add(scenario.Name);
        return names;
    }

    private static GameWorld Mission(string name, CampaignCarry? carry = null, Func<Scenario, Scenario>? edit = null)
    {
        var data = DataLoader.Value;
        var entry = Catalogue.Value.CampaignScenarios.First(s => s.Name == name);
        var scenario = data.LoadScenario(name);
        var world = MatchSetup.Create(data, name, entry.Planet, MissionScript.CampaignPlayers(scenario), seed: 3,
            new MatchOptions { Victory = VictoryCondition.Scripted, Campaign = carry });
        if (edit is not null)
            world.Script = MissionScript.Compile(edit(scenario), carry);
        return world;
    }

    /// <summary>The scenario with its script replaced by these lines.</summary>
    private static Func<Scenario, Scenario> Script(string conditions, string actions) => scenario =>
    {
        var parsed = ScenarioParser.Parse($"RACES_DEFAULT\nRED PC\nENDRACES_DEFAULT\nCONDITIONS\n{conditions}\nENDCONDITIONS\nACTIONS\n{actions}\nENDACTIONS\n");
        return scenario with { Conditions = parsed.Conditions, Actions = parsed.Actions };
    };

    private static List<GameEvent> Run(GameWorld world, float seconds)
    {
        var events = new List<GameEvent>();
        for (var i = 0; i < seconds * GameWorld.TicksPerSecond; i++)
        {
            world.Step();
            events.AddRange(world.Events);
        }
        return events;
    }

    [Fact]
    public void The_campaign_has_its_25_missions()
    {
        Assert.Equal(25, Catalogue.Value.CampaignScenarios.Count());
    }

    [Theory]
    [MemberData(nameof(CampaignScenarios))]
    public void Every_mission_script_uses_only_what_the_remake_knows(string name)
    {
        var data = DataLoader.Value;
        var scenario = data.LoadScenario(name);
        var vids = VoiceMailParser.Load(RepoPaths.OriginalData(Path.Combine("sounds", "vemail", "vemail.dat"))).Select(v => v.Id).ToHashSet();
        var conditions = scenario.Conditions.Select(c => c.Name).ToHashSet();

        Assert.All(scenario.Conditions, c => Assert.Contains(c.Type, MissionScript.ConditionTypes));
        Assert.All(scenario.Conditions.SelectMany(c => c.ReferencedConditions), r => Assert.Contains(r, conditions));
        Assert.All(scenario.Actions, a => Assert.Contains(a.Type, MissionScript.ActionTypes));
        Assert.All(scenario.Actions, a => Assert.Contains(a.Condition, conditions));
        Assert.All(scenario.Actions.Where(a => a.Type == "VOICE_MAIL"), a => Assert.Contains(a.Args[a.Args.ToList().IndexOf("VID") + 1], vids));
        Assert.Contains(MissionScript.ReinforcementsVoiceMail, vids);
        foreach (var line in scenario.Actions.SelectMany(a => a.Body).Where(b => b.Keyword == "MACHINE"))
            Assert.NotNull(data.Parm.Machine(line.Args.Count >= 3 ? $"{line.Args[0]} {line.Args[2]}" : line.Args[0]));

        // It loads as a campaign match and its script starts running.
        var world = Mission(name);
        Assert.NotNull(world.Script);
        Run(world, 2);
    }

    [Fact]
    public void Tutorial_one_is_won_by_going_to_the_first_marker_and_then_the_second()
    {
        var world = Mission("tut01");
        var machine = world.Machines.First(m => m.Race == Race.Red);

        // The second marker counts only after the first: its WON is DISABLED until loc1 enables it.
        machine.Position = new Vector2(444, 390);
        Run(world, 12);
        Assert.Null(world.Winner);

        machine.Position = new Vector2(342, 390);
        var events = Run(world, 12);
        Assert.Contains(events, e => e.Kind == GameEventKind.VoiceMail && e.Detail == "VID_ASSASSIN_TASKED1" && e.Other == (int)Race.Red);

        machine.Position = new Vector2(444, 390);
        events = Run(world, 12);
        Assert.Equal(Race.Red, world.Winner);
        Assert.True(world.Script!.Tasks[1]);
        Assert.Contains(events, e => e.Kind == GameEventKind.TaskChanged && e.Other == 1 && e.Detail == "COMPLETE");
    }

    [Fact]
    public void Losing_every_machine_loses_tutorial_one_and_nobody_wins()
    {
        var world = Mission("tut01");
        foreach (var machine in world.Machines.Where(m => m.Race == Race.Red).ToList())
            machine.HitPoints = 0;
        Run(world, 5);
        Assert.True(world.Races[Race.Red].Defeated);
        Assert.Null(world.Winner);
    }

    [Fact]
    public void A_scripted_match_is_not_decided_by_annihilation()
    {
        // Blue has nothing in tutorial one, which would end a skirmish at once.
        var world = Mission("tut01");
        Run(world, 5);
        Assert.Null(world.Winner);
        Assert.False(world.Races[Race.Red].Defeated);
    }

    [Fact]
    public void Enabling_an_action_restarts_its_timer()
    {
        var world = Mission("tut01", edit: Script(
            "TIMER t 10\nTIMER go 30",
            "WON ON t RACE RED DISABLED\nENABLE_ACTION ON go t"));
        Run(world, 39);
        Assert.Null(world.Winner);
        Run(world, 2);
        Assert.Equal(Race.Red, world.Winner);
    }

    [Fact]
    public void Any_all_and_not_combine_conditions()
    {
        var world = Mission("tut01", edit: Script(
            "TIMER a 2\nTIMER b 6\nALL both a b\nNOT notb b\nALL early a notb\nANY either a b",
            "SET_SCENARIO_FLAG NAME early\nSET_SCENARIO_FLAG NAME both\nSET_SCENARIO_FLAG NAME either"));
        Run(world, 4);
        Assert.Equal(["early", "either"], world.Script!.FlagsSet);
        Run(world, 5);
        Assert.Equal(["early", "either", "both"], world.Script!.FlagsSet);
    }

    [Fact]
    public void A_scenario_flag_comes_from_the_won_mission_that_set_it()
    {
        var script = Script("SCENARIO_FLAG miss0 FROM deepspac", "WON ON miss0 RACE RED");
        var without = Mission("tut01", edit: script);
        Run(without, 5);
        Assert.Null(without.Winner);

        var carry = new CampaignCarry { Flags = { ["deepspac"] = ["miss0"] } };
        var with = Mission("tut01", carry, script);
        Run(with, 5);
        Assert.Equal(Race.Red, with.Winner);
    }

    [Fact]
    public void Fired_at_notes_which_race_shot_at_which()
    {
        var world = Mission("tut01", edit: Script("FIRED_AT shot RACE RED FIRED AT BLUE", "WON ON shot RACE RED"));
        var red = world.Machines.First(m => m.Race == Race.Red && m.CanFight);
        var blue = world.AddMachine(Race.Blue, red.Position + new Vector2(15, 0), "AGGRESSOR", "GRUNT", 1, "R_BOLTER");
        Assert.False(world.HasFiredAt(Race.Red, Race.Blue));
        world.Issue(new AttackCommand(Race.Red, [red.Id], blue.Id));
        Run(world, 8);
        Assert.True(world.HasFiredAt(Race.Red, Race.Blue));
        Assert.Equal(Race.Red, world.Winner);
    }

    [Fact]
    public void Unit_counts_look_at_type_level_and_sub_type()
    {
        var world = Mission("tut01", edit: Script(
            "HIGH_UNIT_COUNT three RACE BLUE 3 AGGRESSOR 3 KNIGHT\nLOW_UNIT_COUNT none RACE BLUE 1 AGGRESSOR",
            "SET_SCENARIO_FLAG NAME three\nSET_SCENARIO_FLAG NAME none"));
        var at = new Vector2(600, 600);
        world.AddMachine(Race.Blue, at, "AGGRESSOR", "KNIGHT", 3, "LR_HEAVY_BOLTER2_X2");
        world.AddMachine(Race.Blue, at + new Vector2(10, 0), "AGGRESSOR", "KNIGHT", 3, "LR_HEAVY_BOLTER2_X2");
        world.AddMachine(Race.Blue, at + new Vector2(20, 0), "AGGRESSOR", "KNIGHT", 4, "LR_PLASMA_CANNON2_X2");
        Run(world, 6);
        Assert.Empty(world.Script!.FlagsSet);

        world.AddMachine(Race.Blue, at + new Vector2(30, 0), "AGGRESSOR", "KNIGHT", 3, "LR_HEAVY_BOLTER2_X2");
        Run(world, 6);
        Assert.Equal(["three"], world.Script!.FlagsSet);
    }

    [Fact]
    public void Bmu_counts_compare_the_bank()
    {
        var world = Mission("tut01", edit: Script("BMU_COUNT rich RACE RED GREATER_THAN_EQUAL 500", "WON ON rich RACE RED"));
        world.Races[Race.Red].Bmu = 499;
        Run(world, 3);
        Assert.Null(world.Winner);
        world.Races[Race.Red].Bmu = 500;
        Run(world, 3);
        Assert.Equal(Race.Red, world.Winner);
    }

    [Fact]
    public void Research_complete_waits_for_the_item()
    {
        var world = Mission("tut01", edit: Script("RESEARCH_COMPLETE ion RACE RED POD 1 WCOMBO T_ION_ORBITAL_CANNON", "WON ON ion RACE RED"));
        Run(world, 3);
        Assert.Null(world.Winner);
        world.Races[Race.Red].Researched.Add(GameWorld.IonCannonResearch);
        Run(world, 3);
        Assert.Equal(Race.Red, world.Winner);
    }

    [Fact]
    public void Change_race_turns_everything_of_a_race_within_the_range()
    {
        var world = Mission("tut01", edit: Script("TIMER now 1", "CHANGE_RACE ON now ORIGINAL_RACE BLUE TARGET_RACE RED AT 600 600 RANGE 50"));
        var near = world.AddMachine(Race.Blue, new Vector2(620, 600), "AGGRESSOR", "GRUNT", 1, "R_BOLTER");
        var far = world.AddMachine(Race.Blue, new Vector2(700, 600), "AGGRESSOR", "GRUNT", 1, "R_BOLTER");
        Run(world, 2);
        Assert.Equal(Race.Red, near.Race);
        Assert.Equal(Race.Blue, far.Race);
    }

    [Fact]
    public void An_explosion_hurts_machines_less_the_further_they_stand()
    {
        var world = Mission("tut01", edit: Script("TIMER now 1", "EXPLOSION ON now AT 600 600 RANGE 30 DAMAGE 200 SIZE 10"));
        var middle = world.AddMachine(Race.Blue, new Vector2(600, 600), "CONSTRUCTOR", "DOZER", 1, null);
        var near = world.AddMachine(Race.Blue, new Vector2(612, 600), "CONSTRUCTOR", "DOZER", 1, null);
        var far = world.AddMachine(Race.Blue, new Vector2(650, 600), "CONSTRUCTOR", "DOZER", 1, null);
        var events = Run(world, 2);
        Assert.Contains(events, e => e.Kind == GameEventKind.Explosion && e.Position == new Vector2(600, 600) && e.Detail == "10");
        Assert.True(middle.IsDead || middle.HitPoints < near.HitPoints);
        Assert.True(near.HitPoints < near.MaxHitPoints);
        Assert.Equal(far.MaxHitPoints, far.HitPoints);
    }

    [Fact]
    public void Activating_a_squadron_starts_the_block_its_script_calls_by_that_number()
    {
        // home.spl's block marked "SQUAD 10" is its tenth, the machines of home.scn's SQUAD 9: squadrons count from 1.
        var world = Mission("home", edit: Script("TIMER now 1", "ACTIVATE_SQUADRONS ON now RACE BLUE SQUADRONS 10"));

        Run(world, 2);

        Assert.Equal([(Race.Blue, 9)], world.Script!.ActivatedSquadrons);
    }

    [Fact]
    public void Reinforcements_appear_at_their_point_and_join_their_squad()
    {
        var world = Mission("tut01", edit: Script("TIMER now 1",
            "REINFORCEMENTS ON now RACE BLUE\nMACHINE AGGRESSOR 3 KNIGHT LR_HEAVY_BOLTER2_X2\nMACHINE AGGRESSOR 3 KNIGHT LR_HEAVY_BOLTER2_X2\nPOINT 440 400\nASSIGN 5\nEND"));
        var events = Run(world, 2);
        var knights = world.Machines.Where(m => m.Race == Race.Blue && m.Spec.SubType == "KNIGHT").ToList();
        Assert.Equal(2, knights.Count);
        Assert.All(knights, k => Assert.Equal(4, k.Squad));
        Assert.All(knights, k => Assert.InRange(Vector2.Distance(k.Position, new Vector2(440, 400)), 0, 30));
        Assert.True(Vector2.Distance(knights[0].Position, knights[1].Position) > 1);
        Assert.Contains(events, e => e.Kind == GameEventKind.VoiceMail && e.Detail == MissionScript.ReinforcementsVoiceMail && e.Other == (int)Race.Blue);
    }

    [Fact]
    public void Sat_brings_in_the_best_survivors_of_commsat()
    {
        var survivors = new List<SurvivingMachine>
        {
            new(Race.Red, "AGGRESSOR", "GRUNT", 1, "R_BOLTER"),
            new(Race.Red, "AGGRESSOR", "KNIGHT", 3, "LR_HEAVY_BOLTER2_X2"),
            new(Race.Red, "ADMINISTRATOR", "BOSS", 1, "L_BOLTER"),
            new(Race.Blue, "AGGRESSOR", "NINJA", 5, "L_MULTI_LAUNCHER7_T_GORILLA_PUNCH_R_MULTI_LAUNCHER7"),
        };
        var before = Mission("sat").Machines.Count(m => m.Race == Race.Red);
        var world = Mission("sat", new CampaignCarry { Survivors = { ["commsat"] = survivors } });
        Run(world, 3);
        var red = world.Machines.Where(m => m.Race == Race.Red).ToList();
        Assert.Equal(before + 3, red.Count);
        Assert.Contains(red, m => m.Spec.SubType == "KNIGHT" && m.Stats.HardwareLevel == 3);
        Assert.DoesNotContain(red, m => m.Spec.SubType == "NINJA");
    }

    [Fact]
    public void Survivors_are_the_machines_left_standing()
    {
        var world = Mission("tut01");
        var survivors = MissionScript.Survivors(world, Race.Red);
        Assert.Equal(world.Machines.Count(m => m.Race == Race.Red), survivors.Count);
        Assert.All(survivors, s => Assert.Equal(Race.Red, s.Race));
    }

    [Fact]
    public void Home_is_lost_when_the_control_centre_falls()
    {
        var world = Mission("home");
        var control = world.Artefacts.Single(a => a.SubType == 0);
        Assert.Equal("contrl", control.Type.Name);
        world.Damage(control, 100000, Race.Blue);
        Run(world, 7);
        Assert.True(world.Races[Race.Red].Defeated);
        Assert.Null(world.Winner);
    }

    // Every mission is lost with nothing left, though not always by ALL_UNITS_DEAD: sat's loss is enabled by a timer at
    // the start and needs no aggressors and no administrators, hilly's by having no aggressors and then no factory.
    [Theory]
    [MemberData(nameof(CampaignScenarios))]
    public void Losing_every_machine_and_building_loses_the_mission(string name)
    {
        var world = Mission(name);
        foreach (var actor in world.Machines.Where(m => m.Race == Race.Red).Cast<Actor>().Concat(world.Constructions.Where(c => c.Race == Race.Red)).ToList())
            actor.HitPoints = 0;
        Run(world, 10);
        Assert.NotEqual(Race.Red, world.Winner);
        Assert.True(world.Races[Race.Red].Defeated);
    }

    [Fact]
    public void Home_is_won_when_the_enemy_pods_are_gone()
    {
        var world = Mission("home");
        foreach (var pod in world.Constructions.Where(c => c.Race == Race.Blue && c.IsType("POD")).ToList())
            pod.HitPoints = 0;
        Run(world, 7);
        Assert.Equal(Race.Red, world.Winner);
        Assert.True(world.Script!.Tasks[1]);
        Assert.True(world.Script.Tasks[2]);
    }

    [Fact]
    public void Dogbar_pays_for_the_attack_from_the_captured_mining_camp()
    {
        // Red starts with no BMUs and only four Dozers to build with; the briefing says to capture the mining camp
        // in the west. Its smelter brings a share of Blue's BMUs, enough for a civilian factory and carriers.
        var world = Mission("dogbar");
        Assert.Equal(0f, world.Races[Race.Red].Bmu);
        var camp = new Vector2(640, 1690);
        foreach (var guard in world.Actors.Where(a => a.Race == Race.Blue && Vector2.Distance(a.Position, camp) < 300
            && a is Machine { Spec.Type: not "RESOURCE_CARRIER" } or Construction { Spec.Type: "MISSILE_EMPLACEMENT" }).ToList())
            guard.HitPoints = 0;
        var dozers = world.Machines.Where(m => m.Race == Race.Red && m.IsType("CONSTRUCTOR")).ToList();
        foreach (var (dozer, i) in dozers.Select((d, i) => (d, i)))
            dozer.Position = camp + new Vector2(150 + i * 8, 0);
        var smelter = world.Constructions.Single(c => c.Race == Race.Blue && c.IsType("SMELTER") && Vector2.Distance(c.Position, camp) < 300);
        var carriers = world.Machines.Where(m => m.Race == Race.Blue && m.IsType("RESOURCE_CARRIER")).ToList();
        Run(world, 1);
        Assert.All(carriers, c => Assert.IsType<TransportOrder>(c.Order));

        var blue = world.Races[Race.Blue].Bmu;
        world.Issue(new CaptureCommand(Race.Red, dozers.Select(d => d.Id).ToList(), smelter.Id));
        for (var i = 0; i < 300 && smelter.Race != Race.Red; i++)
            Run(world, 1);

        Assert.Equal(Race.Red, smelter.Race);
        // A level 1 smelter is 1000 of Blue's 5500 BMUs of storage (pod 500, smelters 1000 and 4000).
        Assert.InRange(world.Races[Race.Red].Bmu, blue * 1000 / 5500 * 0.8f, 1000);
        Assert.Null(world.WhyCannotPlace(Race.Red, "FACTORY", "CIVILIAN", 1, camp + new Vector2(150, 120), 0));
        // Blue's carriers no longer deliver to the captured smelter (the AI sends them to its pod instead).
        Run(world, 1);
        Assert.All(carriers, c => Assert.False(c.Order is TransportOrder t && t.Smelter == smelter.Id));
    }

    [Fact]
    public void Desspy_is_won_when_the_overseer_gets_home()
    {
        var world = Mission("desspy");
        var overseer = world.Machines.First(m => m.Race == Race.Red && m.Spec.SubType == "OVERSEER");
        overseer.Position = new Vector2(250, 1190);
        Run(world, 7);
        Assert.Equal(Race.Red, world.Winner);
    }

    [Fact]
    public void Deep_space_hands_the_launchers_destroyed_on_to_suicide()
    {
        // In deepspac the shield (artefact 1) and two of the six launchers go: flags miss5 and miss4 are set, and the
        // mission is won.
        var deep = Mission("deepspac");
        foreach (var shield in deep.Artefacts.Where(a => a.SubType == 1).ToList())
            deep.Damage(shield, 1_000_000, Race.Red);
        foreach (var launcher in deep.Constructions.Where(c => c.Race == Race.Blue && c.Spec.Type == "MISSILE_EMPLACEMENT" && c.WeaponCombo == "LR_LARGE_MISSILE_X2").Take(2).ToList())
            launcher.HitPoints = 0;
        Run(deep, 7);
        Assert.Equal(Race.Red, deep.Winner);
        Assert.Equal(["miss4", "miss5"], deep.Script!.FlagsSet.Order());

        // In suicide each flag sends one more wave to the player: miss5's after two minutes.
        var suicide = DataLoader.Value.LoadScenario("suicide");
        int Line(GameWorld world, string condition) => world.Script!.Fired.Count(l => l == suicide.Actions.First(a => a.Condition == condition).LineNumber);
        var carried = Mission("suicide", new CampaignCarry { Flags = { ["deepspac"] = deep.Script.FlagsSet } });
        var alone = Mission("suicide");
        Run(carried, 125);
        Run(alone, 125);
        Assert.Equal(1, Line(carried, "helloboys1"));
        Assert.Equal(0, Line(alone, "helloboys1"));
        Assert.Equal(0, Line(carried, "helloboys2"));
    }

    [Fact]
    public void Finger_lands_the_troops_its_apc_carries_to_the_drop_off()
    {
        // finger.spl's squadron 10 is one unarmed APC with assembly point 8, the drop-off at 257, 431: waiting for
        // its attack it drives there, and the script lands three assassins where it stands.
        var world = Mission("finger");
        var finger = DataLoader.Value.LoadScenario("finger");
        var dropOff = finger.Actions.First(a => a.Condition == "apcdropoff" && a.Type == "REINFORCEMENTS").LineNumber;
        for (var second = 0; second < 600 && !world.Script!.Fired.Contains(dropOff); second++)
            Run(world, 1);
        Assert.Contains(dropOff, world.Script!.Fired);
        Assert.Contains(world.Machines, m => m.Race == Race.Blue && m.Spec.Type == "APC" && Vector2.Distance(m.Position, new Vector2(257, 431)) < 15);
    }

    [Fact]
    public void A_race_displayed_as_another_wears_its_colours_and_passes_them_on_until_it_changes_sides()
    {
        // home.scn's Green stands in a DISPLAY AS BLUE block; the script here turns all of Green over to Red later.
        var world = Mission("home", edit: Script("TIMER late 300", "CHANGE_RACE ON late ORIGINAL_RACE GREEN TARGET_RACE RED AT 500 1000 RANGE 3000"));
        var green = world.Actors.Where(a => a.Race == Race.Green).ToList();
        Assert.NotEmpty(green);
        Assert.All(green, a => Assert.Equal(Race.Blue, a.ShownRace));
        Assert.All(world.Actors.Where(a => a.Race != Race.Green), a => Assert.Null(a.DisplayAs));

        // What its constructors put up and its factories make wears the disguise too (MachLogConstructOperation,
        // MachLogFactory::createNewMachine).
        var constructor = world.Machines.First(m => m.Race == Race.Green && m.Spec.Type == "CONSTRUCTOR");
        var plan = world.BuildingPlans[Race.Green][0];
        world.Issue(new PlaceConstructionCommand(Race.Green, [constructor.Id], plan.Type, plan.SubType, plan.HardwareLevel,
            new Vector2(plan.Position.X, plan.Position.Y), plan.Orientation));
        world.Step();
        Assert.Empty(world.Rejected);
        Assert.Equal(Race.Blue, world.Constructions.MaxBy(c => c.Id)!.DisplayAs);

        var factory = world.AddConstruction(Race.Green, constructor.Position + new Vector2(60, 0), "FACTORY", "CIVILIAN", 1, 0, complete: true);
        factory.DisplayAs = Race.Blue;
        world.Races[Race.Green].Bmu = 5000;
        factory.ProductionQueue.Insert(0, new ResearchKey("CONSTRUCTOR", "DOZER", 1, null));
        var built = Run(world, 120).First(e => e.Kind == GameEventKind.MachineBuilt && e.Other == factory.Id);
        Assert.Equal(Race.Blue, world.Find<Machine>(built.Actor)!.ShownRace);

        // Changing sides ends it: the turned actors look like what they now are (MachActor::race).
        Run(world, 300 - world.Time + 1);
        Assert.DoesNotContain(world.Actors, a => a.Race == Race.Green);
        Assert.All(green.Where(a => !a.IsDead), a => Assert.Equal(Race.Red, a.ShownRace));
    }

    [Fact]
    public void The_script_goes_into_the_checksum()
    {
        var a = Mission("tut01", edit: Script("TIMER t 1", "SET_SCENARIO_FLAG NAME t"));
        var b = Mission("tut01", edit: Script("TIMER t 100", "SET_SCENARIO_FLAG NAME t"));
        Run(a, 2);
        Run(b, 2);
        Assert.NotEqual(a.Checksum(), b.Checksum());
    }
}
