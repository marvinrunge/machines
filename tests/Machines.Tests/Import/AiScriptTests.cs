using System.Numerics;
using Machines.Import.Model;
using Machines.Import.Parm;
using Machines.Import.Research;
using Machines.Import.Scenarios;

namespace Machines.Tests.Import;

/// <summary>Reads every shipped <c>.spl</c> AI script and checks it against the research tree and the scenarios.</summary>
public class AiScriptTests
{
    private static readonly Lazy<IReadOnlyDictionary<string, AiScript>> AllFiles = new(() =>
        Directory.GetFiles(RepoPaths.OriginalDataFolder(), "*.spl")
            .ToDictionary(path => Path.GetFileName(path), path => AiScriptParser.Load(path)));

    private static readonly Lazy<ResearchTree> TreeData = new(() => ResearchTreeParser.Load(RepoPaths.OriginalData("global.rst")));

    private static IReadOnlyDictionary<string, AiScript> Scripts => AllFiles.Value;

    private static IEnumerable<(string File, T Item)> Each<T>(Func<AiScript, IEnumerable<T>> select) =>
        Scripts.OrderBy(s => s.Key).SelectMany(s => select(s.Value).Select(item => (s.Key, item)));

    [Fact]
    public void Every_shipped_script_parses()
    {
        Assert.Equal(96, Scripts.Count);
        Assert.True(Each(s => s.Squads).Count() > 1400);
    }

    [Fact]
    public void Every_ordered_machine_is_in_the_research_tree_except_the_demon_warlord()
    {
        // The original looks every NUMBER line up in the research tree and asserts if it is missing.
        // The Demon Warlord (virus) has no research item; deswar.scn, the only user, is not in the game menus.
        var missing = Each(s => s.Production.Concat(s.Squads.SelectMany(q => q.Members)))
            .Where(x => TreeData.Value.Find(x.Item.Machine) is null)
            .Select(x => $"{x.File}:{x.Item.LineNumber} {x.Item.Machine}");

        Assert.Equal(["deswar.spl:73 ADMINISTRATOR COMMANDER 5 L_PULSE_CANNON_R_VIRUS"], missing);
    }

    [Fact]
    public void Scenario_squad_numbers_point_at_existing_squadrons()
    {
        // A race always has squadrons 0 to 9; scripts with more squads add extra ones.
        var folder = RepoPaths.OriginalDataFolder();
        var problems = new List<string>();

        foreach (var path in Directory.GetFiles(folder, "*.scn").Order())
        {
            var scenario = ScenarioParser.Load(path);
            foreach (var race in scenario.Races.Where(r => r.AiScript is not null))
            {
                var script = Scripts[Path.GetFileName(race.AiScript!)];
                var squadrons = Math.Max(10, script.Squads.Count);
                problems.AddRange(race.Machines.Where(m => m.Squad >= squadrons)
                    .Select(m => $"{Path.GetFileName(path)}:{m.LineNumber} squad {m.Squad}"));
            }
        }

        Assert.Empty(problems);
    }

    [Fact]
    public void Malformed_lines_are_read_like_the_original_and_reported()
    {
        var places = Each(s => s.Warnings).Select(x => $"{x.File}:{x.Item.LineNumber}").Distinct().ToList();

        Assert.Equal(
        [
            "1o2.spl:15", "1o2.spl:21", "2o1.spl:21",
            "control.spl:15", "control.spl:21", "control.spl:27",
            "deepspac.spl:15", "deepspac.spl:69", "deepspac.spl:75",
            "desspy.spl:15", "dogbar.spl:83", "fseig_b.spl:24", "fseig_r.spl:24",
            "gracko.spl:16", "gracko.spl:25", "gracko.spl:32", "gracko.spl:72",
            "hilly.spl:47", "sat.spl:21", "talon8.spl:15", "talon8.spl:85",
        ], places);
    }

    [Fact]
    public void A_patrol_with_a_stray_do_count_walks_to_the_map_corner_as_in_the_original()
    {
        // AI PATROL DELAY 0 950 760 850 760 DO 4  — the original reads "DO 4" as the point (0, 4).
        var patrol = Assert.IsType<PatrolOperation>(Scripts["control.spl"].Squads[0].Operations.Single());

        Assert.Equal(0f, patrol.DelayMinutes);
        Assert.Equal([new Vector2(950, 760), new Vector2(850, 760), new Vector2(0, 4)], patrol.Path);
    }

    [Fact]
    public void A_missing_end_merges_two_squads_as_in_the_original()
    {
        Assert.Contains(Scripts["m_1o2R.spl"].IgnoredLines, l => l is { Keyword: "SQUAD", LineNumber: 108 });
        Assert.Contains(Scripts["m_greenR.spl"].IgnoredLines, l => l is { Keyword: "SQUAD", LineNumber: 148 });
    }

    [Fact]
    public void Desert_script_matches_the_file()
    {
        var script = Scripts["desert.spl"];

        // Only N_MINES-style lines, which the original ignores.
        Assert.Empty(script.Production);
        Assert.Contains(script.IgnoredLines, l => l.Keyword == "N_MINES");

        var raid = script.Squads[0];
        var attack = Assert.IsType<AttackOperation>(raid.Operations.Single());
        Assert.Equal((5f, AttackTarget.Resource, 3), (attack.DelayMinutes, attack.Target, attack.Minimum));
        Assert.Equal(
            [new ResearchKey("ADMINISTRATOR", "BOSS", 2, "T_BEE_BOMB"), new ResearchKey("AGGRESSOR", "GRUNT", 2, "L_BOLTER")],
            raid.Members.Select(m => m.Machine));
        Assert.Equal([1, 2], raid.Members.Select(m => m.Count));

        Assert.IsType<ConstructOperation>(script.Squads[1].Operations.Single());

        var locate = Assert.IsType<LocateOperation>(script.Squads[2].Operations.Single());
        Assert.Equal((15, 500f), (locate.DesiredSites, locate.MaxRangeFromPod));

        Assert.Contains(script.Squads, s => s.Operations.OfType<DropMinesOperation>().Any(d => d is { MinRange: 5, MaxRange: 100, Start: { X: 340, Y: 580 } }));
        Assert.Contains(script.Squads, s => s.Operations.OfType<AttackOperation>().Any(a => a is { DelayMinutes: 60, UseIonCannon: true }));
        Assert.Equal(new ResearchKey("TECHNICIAN", "LAB_TECH", 2, null), script.Squads[^1].Members.Single().Machine);
    }

    [Fact]
    public void Attack_keywords()
    {
        const string text = """
            PRODUCTION LIST
            END PRODUCTION LIST
            SQUAD
            AI ATTACK DELAY 2.5 OBJECT MISSILE SUBTYPE TURRET POINT 100 200 ASSEMBLY 3 DO 2 USE NUKE NO_CLUSTER_CHECK
            DEFCON HIGH
            END SQUAD
            """;

        var squad = AiScriptParser.Parse(text).Squads.Single();
        var attack = Assert.IsType<AttackOperation>(squad.Operations.Single());

        Assert.Equal("HIGH", squad.Defcon);
        Assert.Equal((2.5f, AttackTarget.Object, "MISSILE_EMPLACEMENT", "TURRET"), (attack.DelayMinutes, attack.Target, attack.ObjectType, attack.ObjectSubType));
        Assert.Equal((new Vector2(100, 200), 3, 2), (attack.StartPoint!.Value, attack.AssemblyPoint, attack.DoCount));
        Assert.True(attack is { UseNuke: true, UseIonCannon: false, NoClusterCheck: true });
    }

    [Fact]
    public void Unknown_operations_and_unclosed_blocks_are_rejected()
    {
        Assert.Throws<ParmFormatException>(() => AiScriptParser.Parse("END\nSQUAD\nAI DANCE\nEND SQUAD\n"));
        Assert.Throws<ParmFormatException>(() => AiScriptParser.Parse("END\nSQUAD\nAI CONSTRUCT\n"));
        Assert.Throws<ParmFormatException>(() => AiScriptParser.Parse("NUMBER 1 0 GEO_LOCATOR 1\n"));
    }
}
