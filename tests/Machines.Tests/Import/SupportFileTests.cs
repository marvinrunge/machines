using Machines.Import.Audio;
using Machines.Import.Model;
using Machines.Import.Parm;
using Machines.Import.Research;
using Machines.Import.Scenarios;
using Machines.Import.Text;

namespace Machines.Tests.Import;

/// <summary>Strings, sounds, voice mail, factory build lists and the small settings files.</summary>
public class SupportFileTests
{
    private static readonly Lazy<StringTable> StringsData = new(() => StringTableParser.Load(RepoPaths.OriginalData("machstrg.xml")));
    private static readonly Lazy<ResearchTree> TreeData = new(() => ResearchTreeParser.Load(RepoPaths.OriginalData("global.rst")));

    private static StringTable Strings => StringsData.Value;
    private static string Sounds => Path.Combine(RepoPaths.OriginalDataFolder(), "sounds");

    [Fact]
    public void String_table_matches_the_file()
    {
        Assert.Equal(736, Strings.Strings.Count);
        Assert.Equal("Move (M)", Strings[1]);
        Assert.Equal("Knight Templar", Strings[155]);
        Assert.Equal("Eradicator - with Vortex Singularity", Strings[5007]);
    }

    [Fact]
    public void Font_glyph_bytes_survive_as_characters()
    {
        // "Launch cost <BMU sign>500": the byte 0x81 is a glyph in the game's font.
        Assert.Equal($"Nuke Attack (N) Launch cost {StringTable.BmuSign}500", Strings[51]);
    }

    [Fact]
    public void Every_menu_name_in_sceninfo_has_a_string()
    {
        var catalogue = ScenarioCatalogueParser.Load(RepoPaths.OriginalData("sceninfo.dat"));
        var ids = catalogue.Campaign.Select(s => s.StringId)
            .Concat(catalogue.Campaign.SelectMany(s => s.Planets).Select(p => p.StringId))
            .Concat(catalogue.AllScenarios.Select(s => s.StringId))
            .Concat(catalogue.Skirmish.Concat(catalogue.Multiplayer).Select(t => t.StringId));

        Assert.All(ids, id => Assert.True(Strings.Strings.ContainsKey(id), $"no string {id}"));
    }

    [Theory]
    [InlineData("snddef64.dat", true)]
    [InlineData("snddef32.dat", true)]
    [InlineData("Sd2d64.dat", false)]
    [InlineData("Sd2d32.dat", false)]
    public void Sound_definitions_match_the_file(string file, bool threeD)
    {
        var sounds = SoundDefinitionParser.Load(Path.Combine(Sounds, file));

        // One copy-paste slip in the 2D set: SID_CONTROL (Sd2d64.dat line 106) is still marked 3D.
        Assert.All(sounds.Where(s => s.Id != "SID_CONTROL"), s => Assert.Equal(threeD, s.Is3D));
        var bolter = sounds.Single(s => s.Id == "SID_BOLTER");
        Assert.Equal(("sounds/bolter.wav", 1250, true), (bolter.File, bolter.Priority, bolter.Preload));
    }

    [Fact]
    public void Full_sound_sets_contain_the_reduced_ones()
    {
        var full = SoundDefinitionParser.Load(Path.Combine(Sounds, "snddef64.dat")).Select(s => s.Id).ToHashSet();
        var reduced = SoundDefinitionParser.Load(Path.Combine(Sounds, "snddef32.dat")).Select(s => s.Id);

        Assert.Subset(full, reduced.ToHashSet());
        Assert.Contains("SID_NINJA_MOVEMENT", full);
        Assert.DoesNotContain("SID_NINJA_MOVEMENT", reduced);
    }

    [Fact]
    public void Voice_mail_matches_the_file()
    {
        var mails = VoiceMailParser.Load(Path.Combine(Sounds, "vemail", "vemail.dat"));

        Assert.Equal(356, mails.Count);
        var first = mails[0];
        Assert.Equal(("VID_GRUNT_SELF_DESTRUCT", @"sounds\vemail\Grunt\Gt_SDT_1.wav", "VM_FULL_FUNCTION", 10, false),
            (first.Id, first.File, first.Type, first.Priority, first.Preload));
    }

    [Fact]
    public void Every_voice_mail_used_by_a_scenario_is_defined()
    {
        var defined = VoiceMailParser.Load(Path.Combine(Sounds, "vemail", "vemail.dat")).Select(m => m.Id).ToHashSet();
        var used = Directory.GetFiles(RepoPaths.OriginalDataFolder(), "*.scn")
            .SelectMany(path => ScenarioParser.Load(path).Actions)
            .Where(a => a.Type == "VOICE_MAIL")
            .Select(a => a.Args[a.Args.ToList().IndexOf("VID") + 1])
            .Distinct();

        Assert.All(used, id => Assert.Contains(id, defined));
    }

    [Fact]
    public void Factory_build_lists_name_research_items()
    {
        var lists = FactoryBuildListParser.Load(RepoPaths.OriginalData("factory.bld"));

        Assert.Equal(["MILITARY 1", "MILITARY 3", "MILITARY 4", "MILITARY 5", "CIVILIAN 1", "CIVILIAN 3", "CIVILIAN 5"],
            lists.Select(l => $"{l.FactoryType} {l.HardwareLevel}"));
        Assert.Equal(new ResearchKey("AGGRESSOR", "GRUNT", 1, "R_BOLTER"), lists[0].Machines[0]);

        // The original looks every entry up in the research tree and asserts if one is missing.
        Assert.All(lists.SelectMany(l => l.Machines), key => Assert.NotNull(TreeData.Value.Find(key)));
    }

    [Fact]
    public void Settings_files_match()
    {
        var zenith = SettingsParser.Load(RepoPaths.OriginalData("zenith.dat"));
        Assert.Equal((25f, 85f, 41f), (zenith["MIN_HEIGHT"], zenith["MAX_HEIGHT"], zenith["ELEVATION"]));

        Assert.Equal(80f, SettingsParser.Load(RepoPaths.OriginalData("stats.ini"))["CONNECTION_TIME"]);
        Assert.Equal(0.030f, SettingsParser.Load(RepoPaths.OriginalData("simstats.ini"))["targetRenderInterval"]);

        var logos = FileListParser.Load(RepoPaths.OriginalData("logos.dat"));
        Assert.Equal(80, logos.Count);
        Assert.Equal("logo1.bmp", logos[0]);
    }
}
