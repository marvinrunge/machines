using Machines.Import.Model;
using Machines.Import.Parm;
using Machines.Import.Research;

namespace Machines.Tests.Import;

/// <summary>Reads every <c>.rsi</c> file the game shipped with and checks it against the research tree.</summary>
public class ScenarioResearchTests
{
    private static readonly Lazy<ResearchTree> TreeData = new(() => ResearchTreeParser.Load(RepoPaths.OriginalData("global.rst")));

    private static readonly Lazy<IReadOnlyDictionary<string, ScenarioResearch>> AllFiles = new(() =>
        Directory.GetFiles(RepoPaths.OriginalDataFolder(), "*.rsi")
            .ToDictionary(path => Path.GetFileName(path), path => ScenarioResearchParser.Load(path)));

    [Fact]
    public void Every_shipped_rsi_file_parses()
    {
        Assert.Equal(37, AllFiles.Value.Count);
        Assert.True(AllFiles.Value.Values.Sum(f => f.Entries.Count) > 3000);
    }

    /// <summary>
    /// Entries in the shipped files that name no research item. The Builder only exists at level 3,
    /// so the original's lookup asserts on this line; the scenario loader must skip it.
    /// </summary>
    private static readonly string[] KnownDataDefects = ["m_1o2.rsi:103 CONSTRUCTOR BUILDER 2"];

    [Fact]
    public void Every_entry_names_an_item_in_the_research_tree_except_known_defects()
    {
        var tree = TreeData.Value;
        var missing = AllFiles.Value
            .SelectMany(f => f.Value.Entries.Select(e => (File: f.Key, Entry: e)))
            .Where(x => tree.Find(x.Entry.Key) is null)
            .Select(x => $"{x.File}:{x.Entry.LineNumber} {x.Entry.Key}")
            .ToList();

        Assert.Equal(KnownDataDefects, missing);
    }

    [Fact]
    public void Items_made_available_unresearched_have_a_research_cost_except_in_tutorial_3()
    {
        // The original asserts this when it loads a scenario.
        var tree = TreeData.Value;
        var free = AllFiles.Value
            .SelectMany(f => f.Value.Entries.Select(e => (File: f.Key, Entry: e)))
            .Where(x => tree.Find(x.Entry.Key) is not null)
            .Where(x => !x.Entry.Researched && (x.Entry.ResearchCostOverride ?? tree.Get(x.Entry.Key).ResearchCost) <= 0)
            .Select(x => $"{x.File}:{x.Entry.LineNumber} {x.Entry.Key}")
            .ToList();

        // Tutorial 3 asks the player to research the Dozer, whose research cost is 0. The original only
        // checked this in debug builds; in the shipped game the research finishes as soon as it starts.
        Assert.Equal(["tut03.rsi:9 CONSTRUCTOR DOZER 1"], free);
    }

    [Fact]
    public void Only_the_two_stray_lines_in_fortseig_are_ignored()
    {
        var ignored = AllFiles.Value.Where(f => f.Value.IgnoredLines.Count > 0).ToList();

        var fortseig = Assert.Single(ignored);
        Assert.Equal("fortseig.rsi", fortseig.Key);
        Assert.All(fortseig.Value.IgnoredLines, l => Assert.Equal("Med", l.Keyword));
        Assert.Equal(2, fortseig.Value.IgnoredLines.Count);
    }

    [Fact]
    public void Default_file_gives_players_researched_scouts()
    {
        var first = AllFiles.Value["default.rsi"].Entries[0];

        Assert.Equal(RaceTarget.Pc, first.Race);
        Assert.True(first.Researched);
        Assert.Equal(new ResearchKey("AGGRESSOR", "GRUNT", 1, "R_BOLTER"), first.Key);
    }

    [Fact]
    public void Race_words_cost_overrides_and_the_default_race()
    {
        const string text = """
            CONSTRUCTION NOT POD 1 WCOMBO T_ION_ORBITAL_CANNON RESEARCH_COST 600
            RACE ALL
            MACHINE RESEARCHED GEO_LOCATOR 1 BUILDING_COST 0
            RACE YELLOW
            MACHINE NOT AGGRESSOR 2 SUB GRUNT WCOMBO L_BOLTER
            """;

        var entries = ScenarioResearchParser.Parse(text).Entries;

        Assert.Equal([RaceTarget.Ai, RaceTarget.All, RaceTarget.Yellow], entries.Select(e => e.Race));
        Assert.Equal(600, entries[0].ResearchCostOverride);
        Assert.Equal(ResearchKind.Construction, entries[0].Kind);
        Assert.Null(entries[1].BuildingCostOverride);
        Assert.False(entries[2].Researched);
        Assert.Equal(new ResearchKey("AGGRESSOR", "GRUNT", 2, "L_BOLTER"), entries[2].Key);
    }

    [Fact]
    public void Dangling_option_is_rejected()
    {
        Assert.Throws<ParmFormatException>(() => ScenarioResearchParser.Parse("MACHINE NOT AGGRESSOR 2 SUB\n"));
    }
}
