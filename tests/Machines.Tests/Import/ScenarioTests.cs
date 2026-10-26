using System.Numerics;
using Machines.Import.Model;
using Machines.Import.Parm;
using Machines.Import.Scenarios;

namespace Machines.Tests.Import;

/// <summary>Reads every shipped <c>.scn</c> file and checks it against <c>parmdata.dat</c> and the other data files.</summary>
public class ScenarioTests
{
    private static readonly Lazy<IReadOnlyDictionary<string, Scenario>> AllFiles = new(() =>
        Directory.GetFiles(RepoPaths.OriginalDataFolder(), "*.scn")
            .ToDictionary(path => Path.GetFileName(path), path => ScenarioParser.Load(path)));

    private static readonly Lazy<ParmData> ParmLoader = new(() => ParmDataParser.Load(RepoPaths.OriginalData("parmdata.dat")));

    private static IReadOnlyDictionary<string, Scenario> Scenarios => AllFiles.Value;
    private static ParmData Parm => ParmLoader.Value;

    private static IEnumerable<(string File, T Item)> Each<T>(Func<Scenario, IEnumerable<T>> select) =>
        Scenarios.OrderBy(s => s.Key).SelectMany(s => select(s.Value).Select(item => (s.Key, item)));

    [Fact]
    public void Every_shipped_scenario_parses()
    {
        Assert.Equal(73, Scenarios.Count);
    }

    [Fact]
    public void Every_placed_machine_and_building_exists_in_parmdata()
    {
        var machines = Each(s => s.Races.SelectMany(r => r.Machines))
            .Where(x => !Parm.Machines.Any(m => m.TypeName == x.Item.TypeName && m.Levels.Any(l => l.HardwareLevel == x.Item.HardwareLevel)))
            .Select(x => $"{x.File}:{x.Item.LineNumber}");
        var buildings = Each(s => s.Races.SelectMany(r => r.Constructions))
            .Where(x => !Parm.Constructions.Any(c => c.TypeName == x.Item.TypeName && c.Levels.Any(l => l.HardwareLevel == x.Item.HardwareLevel)))
            .Select(x => $"{x.File}:{x.Item.LineNumber}");

        Assert.Empty(machines);
        Assert.Empty(buildings);
    }

    [Fact]
    public void Every_reinforcement_machine_exists_in_parmdata()
    {
        // MACHINE <type> <level> [<subtype>] [<weapon combo>]
        var missing = Each(s => s.Actions.Where(a => a.Type == "REINFORCEMENTS").SelectMany(a => a.Body))
            .Where(x => x.Item.Keyword == "MACHINE")
            .Where(x =>
            {
                var args = x.Item.Args;
                var typeName = args[0] is "AGGRESSOR" or "ADMINISTRATOR" or "TECHNICIAN" or "CONSTRUCTOR"
                    ? $"{args[0]} {args[2]}"
                    : args[0];
                return !Parm.Machines.Any(m => m.TypeName == typeName && m.Levels.Any(l => l.HardwareLevel == int.Parse(args[1])));
            })
            .Select(x => $"{x.File}:{x.Item.LineNumber}");

        Assert.Empty(missing);
    }

    [Fact]
    public void Every_referenced_ai_script_and_research_file_exists()
    {
        var folder = RepoPaths.OriginalDataFolder();
        bool Missing(string path) => !File.Exists(Path.Combine(folder, Path.GetFileName(path)));

        Assert.Empty(Each(s => s.Races.Where(r => r.AiScript is not null)).Where(x => Missing(x.Item.AiScript!)).Select(x => x.File));
        Assert.Empty(Each(s => s.Research is { } r ? new[] { r.Default, r.Low, r.Medium, r.High } : []).Where(x => Missing(x.Item)).Select(x => x.File));

        // test.scn is the only scenario without research items.
        Assert.Equal(["test.scn"], Scenarios.Where(s => s.Value.Research is null).Select(s => s.Key));
    }

    [Fact]
    public void Malformed_lines_are_read_like_the_original_and_reported()
    {
        var warnings = Each(s => s.Warnings).Select(x => $"{x.File} {x.Item}").ToList();

        Assert.Equal(
        [
            "deepspac.scn line 140: SET_SCENARIO_FLAG: 'NAME' where ON is expected",
            "deepspac.scn line 141: SET_SCENARIO_FLAG: 'NAME' where ON is expected",
            "deepspac.scn line 142: SET_SCENARIO_FLAG: 'NAME' where ON is expected",
            "deepspac.scn line 143: SET_SCENARIO_FLAG: 'NAME' where ON is expected",
            "deepspac.scn line 144: SET_SCENARIO_FLAG: 'NAME' where ON is expected",
            "deepspac.scn line 145: SET_SCENARIO_FLAG: 'NAME' where ON is expected",
            "m_contrl.scn line 116: SMELTER: '0T' read as 0",
            "multidog.scn line 34: SITE: 'MEDIUM' read as 0",
            "multidog.scn line 40: SITE: 'HIGH' read as 0",
        ], warnings);
    }

    [Fact]
    public void Misspelt_building_flags_are_ignored_like_the_original()
    {
        var unknown = Each(s => s.Races.SelectMany(r => r.Constructions).Where(c => c.UnknownWords.Count > 0))
            .Select(x => $"{x.File}:{x.Item.LineNumber} {string.Join(' ', x.Item.UnknownWords)}")
            .ToList();

        Assert.Equal(13, unknown.Count);
        Assert.All(unknown, u => Assert.Matches(@" (REBUILT|REBUILD2|REBUILDY|T_NUCLEAR_MISSILE)$", u));
    }

    [Fact]
    public void Lines_outside_sections_are_ignored_like_the_original()
    {
        // tut02 and tut03 close their RED block twice; the assembly points after the first ENDRACE never load.
        Assert.Equal(
        [
            "savegame.scn #include",
            "tut02.scn ASSEMBLY", "tut02.scn ASSEMBLY", "tut02.scn ENDRACE",
            "tut03.scn ASSEMBLY", "tut03.scn ASSEMBLY", "tut03.scn ENDRACE",
        ], Each(s => s.IgnoredLines).Select(x => $"{x.File} {x.Item.Keyword}"));
        Assert.Empty(Scenarios["tut02.scn"].Race(Race.Red)!.AggressorAssemblyPoints);
    }

    [Fact]
    public void First_campaign_mission_matches_the_file()
    {
        var scenario = Scenarios["1o1.scn"];

        Assert.Equal(DefaultController.Player, scenario.DefaultPlayers[Race.Red]);
        Assert.Equal(DefaultController.Ai, scenario.DefaultPlayers[Race.Blue]);
        Assert.Equal(DefaultController.None, scenario.DefaultPlayers[Race.Green]);
        Assert.Equal("data/1o1.rsi", scenario.Research!.Default);

        var red = scenario.Race(Race.Red)!;
        Assert.Null(red.AiScript);
        Assert.Equal((600, 0), (red.Bmu, red.Ore));
        Assert.Equal(new CameraStart(new Vector3(410, 570, 100), -90, 41, 0, "ZENITH"), red.Camera);
        Assert.Equal(7, red.Machines.Count);
        var overseer = red.Machines[0];
        Assert.Equal(("ADMINISTRATOR OVERSEER", 3, "LR_PULSE_RIFLE_X2"), (overseer.TypeName, overseer.HardwareLevel, overseer.WeaponCombo));
        var pod = red.Constructions[0];
        Assert.Equal(("POD", -270, "T_ION_ORBITAL_CANNON"), (pod.TypeName, pod.Orientation, pod.WeaponCombo));

        var blue = scenario.Race(Race.Blue)!;
        Assert.Equal("data/1o1.spl", blue.AiScript);
        Assert.Equal(35000, blue.Bmu);
        Assert.Equal(7, blue.AggressorAssemblyPoints.Count);
        Assert.Single(blue.AdministratorAssemblyPoints);
        Assert.Equal([0, 0, 0], blue.Machines.Take(3).Select(m => m.Squad));

        var factory = blue.Constructions[0];
        Assert.Equal(("FACTORY MILITARY", true, false), (factory.TypeName, factory.Rebuild, factory.NotBuilt));
        var lab = blue.Constructions[2];
        Assert.Equal("HARDWARE_LAB MILITARY", lab.TypeName);
        // Typo in the file: a truncated combo, kept as written (the original ignores building combos).
        Assert.Contains(blue.Constructions, c => c.WeaponCombo == "LR_AUTO_CANNON_X" && c.Rebuild);

        Assert.Equal(["ALL_UNITS_DEAD allDeadRed", "LOW_UNIT_COUNT noBlue"], scenario.Conditions.Select(c => $"{c.Type} {c.Name}"));
        Assert.Equal(["LOST allDeadRed", "WON noBlue"], scenario.Actions.Select(a => $"{a.Type} {a.Condition}"));
    }

    [Fact]
    public void Mineral_sites_include_only_uncommented_lines()
    {
        var sites = Scenarios["desert.scn"].MineralSites;
        Assert.Equal(9, sites.Count);
        Assert.Equal(new MineralSite(1, 30000, new Vector3(240, 160, 0), ResourceLevel.Default, sites[0].LineNumber), sites[0]);
    }

    [Fact]
    public void A_site_missing_its_height_loses_its_resource_level_as_in_the_original()
    {
        // SITE 1 4100 658.683 284.126  MEDIUM  — MEDIUM lands in the height slot.
        var site = Scenarios["multidog.scn"].MineralSites.Single(s => s.LineNumber == 34);
        Assert.Equal((0f, ResourceLevel.Default), (site.Position.Z, site.Level));
    }

    [Fact]
    public void Artefacts_and_their_destruction_effects()
    {
        var artefacts = Scenarios["control.scn"].Artefacts!;

        var control = artefacts.Types.Single(t => t.Name == "control");
        Assert.Equal((true, 8000, 80000, 6028), (control.Active, control.HitPoints, control.Armour, control.StringId));
        Assert.Equal(4, control.Boundary.Count);
        Assert.Equal(4, artefacts.Placements.Count(p => p.TypeName == "cool1"));
        Assert.Equal([101, 102, 103, 104], artefacts.OnDestroy.Select(d => d.Id));
        Assert.All(artefacts.OnDestroy, d => Assert.Equal(-19990, d.Armour));
    }

    [Fact]
    public void Reinforcements_keep_their_body_lines()
    {
        var reinforcement = Scenarios["1o2.scn"].Actions.First(a => a.Type == "REINFORCEMENTS");

        Assert.Equal("rein1", reinforcement.Condition);
        Assert.False(reinforcement.Enabled);
        Assert.Equal(["RACE", "RED"], reinforcement.Args);
        Assert.Equal(7, reinforcement.Body.Count);
        Assert.Equal(["297", "1500"], reinforcement.Body[^1].Args);
    }

    [Fact]
    public void Undefined_conditions_are_rejected()
    {
        const string text = """
            RACES_DEFAULT
            RED PC
            ENDRACES_DEFAULT
            CONDITIONS
            TIMER soon 10
            ALL both soon later
            ENDCONDITIONS
            ACTIONS
            WON ON soon RACE RED
            ENDACTIONS
            """;

        var error = Assert.Throws<ParmFormatException>(() => ScenarioParser.Parse(text));
        Assert.Contains("'later'", error.Message);
    }

    [Fact]
    public void Alternative_site_must_follow_a_building_plan()
    {
        const string text = """
            RACE BLUE data/x.spl
            CONSTRUCTIONS
            MINE 1 10 10 0 0
            ALTERNATIVE_SITE 20 20 0 90
            ENDRACE
            """;

        Assert.Throws<ParmFormatException>(() => ScenarioParser.Parse(text));
    }
}
