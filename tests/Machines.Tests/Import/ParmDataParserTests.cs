using Machines.Import.Model;
using Machines.Import.Parm;

namespace Machines.Tests.Import;

/// <summary>
/// Pins the importer to the shipped <c>parmdata.dat</c>. Expected values were read by hand from the
/// original file; if one of these fails, either the importer or the data changed.
/// </summary>
public class ParmDataParserTests
{
    private static readonly Lazy<ParmData> Data = new(() => ParmDataParser.Load(RepoPaths.OriginalData("parmdata.dat")));

    private static ParmData Parm => Data.Value;

    [Fact]
    public void Reads_every_record_in_the_file()
    {
        Assert.Equal(7, Parm.EvasionStrategies.Count);
        Assert.Equal(18, Parm.Machines.Count);
        Assert.Equal(13, Parm.Constructions.Count);
        Assert.Equal(31, Parm.Weapons.Count);

        Assert.Equal(37, Parm.Machines.Sum(m => m.Levels.Count));
        Assert.Equal(27, Parm.Constructions.Sum(c => c.Levels.Count));
    }

    [Fact]
    public void Levels_follow_the_original_hardware_level_tables()
    {
        foreach (var machine in Parm.Machines)
            Assert.Equal(HardwareLevels.For(machine.TypeName), machine.Levels.Select(l => l.HardwareLevel));
        foreach (var construction in Parm.Constructions)
            Assert.Equal(HardwareLevels.For(construction.TypeName), construction.Levels.Select(l => l.HardwareLevel));
    }

    [Fact]
    public void Grunt_levels_match_the_file()
    {
        var grunt = Parm.Machine("AGGRESSOR GRUNT");

        var scout = grunt.Level(1);
        Assert.Equal(35, scout.Cost);
        Assert.Equal(12, scout.HitPoints);
        Assert.Equal(10, scout.Armour);
        Assert.Equal(0.25f, scout.RepairRate);
        Assert.Equal(15f, scout.Speed);
        Assert.Equal(Locomotion.Wheels, scout.Locomotion);
        Assert.Equal("NEVER_EVADE", scout.Evasion);
        Assert.Equal("NONE", scout.BodyTurnLink);

        var warrior = grunt.Level(3);
        Assert.Equal(50, warrior.Cost);
        Assert.Equal(40, warrior.HitPoints);
        Assert.Equal(20, warrior.Armour);
        Assert.Equal(Locomotion.Tracks, warrior.Locomotion);
        Assert.Equal(-3.13f, warrior.LocomotionParameter);
        Assert.Equal(1, warrior.NumberOfWeapons);
    }

    [Theory]
    [InlineData("AGGRESSOR KNIGHT", 5, 300, 90, 80)]
    [InlineData("AGGRESSOR NINJA", 5, 450, 450, 250)]
    [InlineData("ADMINISTRATOR BOSS", 2, 350, 200, 250)]
    [InlineData("APC", 5, 300, 600, 200)]
    [InlineData("RESOURCE_CARRIER", 5, 350, 250, 30)]
    public void Machine_cost_hp_and_armour(string type, int level, int cost, int hp, int armour)
    {
        var stats = Parm.Machine(type).Level(level);
        Assert.Equal((cost, hp, armour), (stats.Cost, stats.HitPoints, stats.Armour));
    }

    [Fact]
    public void Role_specific_machine_values()
    {
        Assert.Equal(120, Parm.Machine("TECHNICIAN BRAIN_BOX").Level(5).ResearchRate);
        Assert.Equal(15, Parm.Machine("ADMINISTRATOR COMMANDER").Level(5).MaxSubordinates);
        Assert.True(Parm.Machine("ADMINISTRATOR OVERSEER").Level(2).NightVision);
        Assert.Equal(120, Parm.Machine("SPY_LOCATOR").Level(5).DownloadRate);
        Assert.Equal(1000, Parm.Machine("RESOURCE_CARRIER").Level(5).Capacity);
        Assert.Equal(
            new ConstructionRef("MISSILE_EMPLACEMENT", "ICBM", 5),
            Parm.Machine("CONSTRUCTOR BEHEMOTH").Level(5).MostAdvancedConstruction);
        Assert.Equal([10, 12, 14, 16, 18], Parm.Machine("AGGRESSOR KNIGHT").SoftwareResearchCosts);
    }

    [Fact]
    public void Every_machine_uses_a_known_evasion_strategy()
    {
        var names = Parm.EvasionStrategies.Select(e => e.Name).ToHashSet();
        Assert.All(Parm.Machines.SelectMany(m => m.Levels), level => Assert.Contains(level.Evasion, names));
    }

    [Fact]
    public void Construction_values_match_the_file()
    {
        var pod = Parm.Construction("POD").Level(1);
        Assert.Equal((1200, 2000, 6, 500), (pod.Cost, pod.HitPoints, pod.ExtractionRate, pod.Capacity));

        var mine = Parm.Construction("MINE");
        Assert.Equal([100, 180, 250], mine.Levels.Select(l => l.ExtractionRate!.Value));
        Assert.Equal([120, 500, 4000], mine.Levels.Select(l => l.Capacity!.Value));

        Assert.Equal([75, 150, 180, 225], Parm.Construction("FACTORY MILITARY").Levels.Select(l => l.BuildRate!.Value));
        Assert.Equal(6400, Parm.Construction("SMELTER").Level(3).MineDistance);
        Assert.Equal(15f, Parm.Construction("GARRISON").Level(1).RepairRate);
    }

    [Fact]
    public void Construction_geometry_is_split_by_level()
    {
        var lab = Parm.Construction("HARDWARE_LAB MILITARY");

        var level1Stations = lab.Level(1).Geometry.Where(g => g.Keyword == "STATION").ToList();
        var level3Stations = lab.Level(3).Geometry.Where(g => g.Keyword == "STATION").ToList();

        Assert.Single(level1Stations);
        Assert.Equal(4, level3Stations.Count);
        Assert.All(level3Stations, s => Assert.Equal("RESEARCH", s.Args[^1]));
        Assert.Equal(["-15.2", "-18.6", "15.3", "18.8"], lab.Level(3).Geometry.Single(g => g.Keyword == "BOUNDARY").Args);
    }

    [Theory]
    [InlineData("BOLTER", 4f, 35f, 1, 1.0f)]
    [InlineData("VORTEX", 150f, 90f, 1, 16.0f)]
    [InlineData("ION_ORBITAL_CANNON", 450f, 10000f, 1, 240f)]
    [InlineData("NUCLEAR_MISSILE", 750f, 10000f, 1, 360f)]
    [InlineData("AUTO_CANNON", 3f, 45f, 6, 2.5f)]
    public void Weapon_values_match_the_file(string type, float damage, float range, int rounds, float reload)
    {
        var weapon = Parm.Weapon(type);
        Assert.Equal((damage, range, rounds, reload), (weapon.Damage, weapon.Range, weapon.BurstRounds, weapon.ReloadTime));
    }

    [Fact]
    public void Burst_timing_and_presentation_lines_are_kept()
    {
        var pulseCannon = Parm.Weapon("PULSE_CANNON");
        Assert.Equal(3, pulseCannon.BurstRounds);
        Assert.Equal(1.0f, pulseCannon.BurstDuration);
        Assert.Equal("VARIABLE", pulseCannon.BurstTiming);

        Assert.Equal(8, Parm.Weapon("WASP_LIGHT_STING").Presentation.Count(l => l.Keyword == "LAUNCH_OFFSET"));
    }

    [Fact]
    public void Evasion_and_general_data_match_the_file()
    {
        Assert.Equal(-2, Parm.Evasion("NEVER_EVADE").GarrisonsPriority);
        Assert.Equal(-1, Parm.Evasion("RUN_AWAY").PodPriority);

        var general = Parm.General;
        Assert.Equal((-45f, 70f), (general.FirstPersonLookMinAngle, general.FirstPersonLookMaxAngle));
        Assert.Equal((45f, 10f), (general.FirstPersonMaxFastTurnRate, general.FirstPersonMaxSlowTurnRate));
        Assert.Equal(65f, general.MaxMoveGradient);
        Assert.Equal(5, general.PcPathFindingPriority);
    }

    [Fact]
    public void Wrong_number_of_level_values_names_the_line()
    {
        const string text = """
            EVASION_STRATEGIES
            END EVASION_STRATEGIES
            MACHINES
            TYPE AGGRESSOR GRUNT
            HW_COSTS 35 35
            END
            END MACHINES
            CONSTRUCTIONS
            END CONSTRUCTIONS
            WEAPONS
            END WEAPONS
            GENERAL_DATA
            END
            """;

        var error = Assert.Throws<ParmFormatException>(() => ParmDataParser.Parse(text));
        Assert.Equal(5, error.LineNumber);
        Assert.Contains("3 hardware levels", error.Message);
    }
}
