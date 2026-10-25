using Machines.Import.Model;
using Machines.Import.Parm;
using Machines.Import.Research;

namespace Machines.Tests.Import;

/// <summary>Pins the importers for <c>global.rst</c> and <c>global.cst</c> to the shipped files.</summary>
public class ResearchTreeTests
{
    private static readonly Lazy<ResearchTree> TreeData = new(() => ResearchTreeParser.Load(RepoPaths.OriginalData("global.rst")));
    private static readonly Lazy<ConstructionOrder> OrderData = new(() => ConstructionOrderParser.Load(RepoPaths.OriginalData("global.cst")));
    private static readonly Lazy<ParmData> ParmLoader = new(() => ParmDataParser.Load(RepoPaths.OriginalData("parmdata.dat")));

    private static ResearchTree Tree => TreeData.Value;
    private static ConstructionOrder Order => OrderData.Value;
    private static ParmData Parm => ParmLoader.Value;

    [Fact]
    public void Reads_every_item_plus_the_implicit_large_missile_items()
    {
        var fromFile = Tree.Items.Where(i => !i.Implicit).ToList();
        Assert.Equal(52, fromFile.Count(i => i.Kind == ResearchKind.Machine));
        Assert.Equal(8, fromFile.Count(i => i.Kind == ResearchKind.Construction));

        // Knight and Ballista are the only aggressors with level-4 entries.
        Assert.Equal(
            ["AGGRESSOR BALLISTA 4 LR_LARGE_MISSILE_X2", "AGGRESSOR KNIGHT 4 LR_LARGE_MISSILE_X2"],
            Tree.Items.Where(i => i.Implicit).Select(i => i.Key.ToString()).Order());
        Assert.Equal(51, Tree.Items.Count(i => i.Parent is not null));
    }

    [Fact]
    public void Knight_with_plasma_cannons_needs_the_wraith_reaper()
    {
        var enforcer = Tree.Get(new ResearchKey("AGGRESSOR", "KNIGHT", 3, "LR_PLASMA_CANNON1_X2"));

        Assert.Equal(new ResearchKey("AGGRESSOR", "ASSASSIN", 3, "LR_PLASMA_RIFLE_X2"), enforcer.Parent);
        Assert.Equal((1, 120, 100), (enforcer.TechLevel, enforcer.ResearchCost, enforcer.BuildingCost));
        Assert.Equal(LabType.Military, enforcer.Lab);
    }

    [Fact]
    public void Civilian_items_and_items_without_sub_types()
    {
        var ranger = Tree.Get(new ResearchKey("GEO_LOCATOR", null, 2, null));

        Assert.Equal(LabType.Civilian, ranger.Lab);
        Assert.Equal(new ResearchKey("GEO_LOCATOR", null, 1, null), ranger.Parent);
        Assert.Equal((1, 30, 30), (ranger.TechLevel, ranger.ResearchCost, ranger.BuildingCost));
    }

    [Fact]
    public void Ion_cannon_is_a_construction_item_that_needs_a_brainbox()
    {
        var ionCannon = Tree.Get(new ResearchKey("POD", null, 1, "T_ION_ORBITAL_CANNON"));

        Assert.Equal(ResearchKind.Construction, ionCannon.Kind);
        Assert.Equal((3, 240, 300), (ionCannon.TechLevel, ionCannon.ResearchCost, ionCannon.BuildingCost));
    }

    [Fact]
    public void Every_machine_item_exists_in_parmdata()
    {
        Assert.All(Tree.Items.Where(i => i.Kind == ResearchKind.Machine),
            item => Parm.Machine(item.Key.TypeName).Level(item.Key.HardwareLevel));
    }

    [Theory]
    // Plain parmdata cost.
    [InlineData("AGGRESSOR", "GRUNT", 1, "R_BOLTER", 35)]
    // FACTORY_COST RELATIVE 2.
    [InlineData("AGGRESSOR", "GRUNT", 1, "R_ELECTRIC_CHARGE", 37)]
    // FACTORY_COST RELATIVE -5 and -20: ignored, exactly as in the original.
    [InlineData("ADMINISTRATOR", "OVERSEER", 2, "L_PULSE_RIFLE_R_SUPERCHARGE_ADVANCED", 100)]
    [InlineData("ADMINISTRATOR", "COMMANDER", 4, "L_MULTI_LAUNCHER5_R_SUPERCHARGE_SUPER", 300)]
    // Implicit items are set to 100.
    [InlineData("AGGRESSOR", "KNIGHT", 4, "LR_LARGE_MISSILE_X2", 100)]
    public void Factory_price_applies_only_positive_adjustments(string type, string subType, int level, string combo, int price)
    {
        var item = Tree.Get(new ResearchKey(type, subType, level, combo));
        Assert.Equal(price, ResearchTree.FactoryPrice(item, Parm));
    }

    [Fact]
    public void Construction_order_matches_the_file()
    {
        Assert.Equal(26, Order.Entries.Count);
        Assert.Equal("BEACON 1", Order.Entries[0].ToString());
        Assert.Equal("MISSILE_EMPLACEMENT ICBM 5", Order.Entries[^1].ToString());
        Assert.DoesNotContain(Order.Entries, e => e.Type == "POD");
    }

    [Theory]
    [InlineData("CONSTRUCTOR DOZER", 1, 8, "MISSILE_EMPLACEMENT TURRET 2")]
    [InlineData("CONSTRUCTOR BUILDER", 3, 19, "MISSILE_EMPLACEMENT SENTRY 4")]
    [InlineData("CONSTRUCTOR BEHEMOTH", 5, 26, "MISSILE_EMPLACEMENT ICBM 5")]
    public void Constructors_can_raise_everything_up_to_their_most_advanced_entry(string constructor, int level, int count, string last)
    {
        var mostAdvanced = Parm.Machine(constructor).Level(level).MostAdvancedConstruction!;
        var buildable = Order.UpTo(mostAdvanced).ToList();

        Assert.Equal(count, buildable.Count);
        Assert.Equal(last, buildable[^1].ToString());
    }

    [Fact]
    public void Unknown_keywords_and_missing_parents_are_rejected()
    {
        Assert.Throws<ParmFormatException>(() => ResearchTreeParser.Parse("MACHINE GEO_LOCATOR\nHWLEVEL 1\nCOLOUR RED\nEND\n"));

        const string orphan = """
            MACHINE GEO_LOCATOR
            HWLEVEL 2
            SWLEVEL 1
            TECHLEVEL 1
            RESEARCH_COST 30
            BUILDING_COST 30
            PARENT GEO_LOCATOR 1
            END
            """;
        var error = Assert.Throws<ParmFormatException>(() => ResearchTreeParser.Parse(orphan));
        Assert.Contains("parent GEO_LOCATOR 1", error.Message);
    }
}
