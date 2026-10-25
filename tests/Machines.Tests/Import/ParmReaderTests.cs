using Machines.Import.Parm;

namespace Machines.Tests.Import;

public class ParmReaderTests
{
    [Fact]
    public void Reads_sections_records_and_section_lines()
    {
        const string text = """
            // header comment
            MACHINES
            TYPE AGGRESSOR GRUNT    // trailing comment
            HW_COSTS 35 35 50
            END
            END MACHINES

            GENERAL_DATA
            MAX_MOVE_GRADIENT	65.0
            END
            """;

        var document = ParmReader.Read(text);

        Assert.Equal(["MACHINES", "GENERAL_DATA"], document.Sections.Select(s => s.Name));

        var grunt = Assert.Single(document.Section("MACHINES")!.Records);
        Assert.Equal("AGGRESSOR GRUNT", grunt.TypeName);
        var costs = Assert.Single(grunt.Lines);
        Assert.Equal("HW_COSTS", costs.Keyword);
        Assert.Equal(["35", "35", "50"], costs.Args);
        Assert.Equal(4, costs.LineNumber);

        var gradient = Assert.Single(document.Section("GENERAL_DATA")!.Lines);
        Assert.Equal(["65.0"], gradient.Args);
    }

    [Fact]
    public void Unclosed_record_reports_its_line()
    {
        const string text = """
            WEAPONS
            TYPE BOLTER
            RANGE 35
            END WEAPONS
            """;

        var error = Assert.Throws<ParmFormatException>(() => ParmReader.Read(text));
        Assert.Equal(4, error.LineNumber);
    }

    [Fact]
    public void Mismatched_section_end_is_rejected()
    {
        const string text = """
            MACHINES
            END WEAPONS
            """;

        Assert.Throws<ParmFormatException>(() => ParmReader.Read(text));
    }

    [Fact]
    public void Unclosed_section_is_rejected()
    {
        Assert.Throws<ParmFormatException>(() => ParmReader.Read("MACHINES\n"));
    }
}
