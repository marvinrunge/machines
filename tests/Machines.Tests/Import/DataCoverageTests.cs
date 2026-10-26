using Machines.Import.Audio;
using Machines.Import.Parm;
using Machines.Import.Planets;
using Machines.Import.Research;
using Machines.Import.Scenarios;
using Machines.Import.Text;

namespace Machines.Tests.Import;

/// <summary>
/// The M0 gate: every file in <c>data/original</c> has an importer, and every one of them loads.
/// A file added without an importer, or one that stops loading, fails this test.
/// </summary>
public class DataCoverageTests
{
    private static readonly Dictionary<string, Action<string>> ImportersByName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["parmdata.dat"] = path => ParmDataParser.Load(path),
        ["sceninfo.dat"] = path => ScenarioCatalogueParser.Load(path),
        ["zenith.dat"] = path => SettingsParser.Load(path),
        ["stats.ini"] = path => SettingsParser.Load(path),
        ["simstats.ini"] = path => SettingsParser.Load(path),
        ["logos.dat"] = path => FileListParser.Load(path),
        ["factory.bld"] = path => FactoryBuildListParser.Load(path),
        ["global.rst"] = path => ResearchTreeParser.Load(path),
        ["global.cst"] = path => ConstructionOrderParser.Load(path),
        ["machstrg.xml"] = path => StringTableParser.Load(path),
        ["vemail.dat"] = path => VoiceMailParser.Load(path),
        ["snddef32.dat"] = path => SoundDefinitionParser.Load(path),
        ["snddef64.dat"] = path => SoundDefinitionParser.Load(path),
        ["Sd2d32.dat"] = path => SoundDefinitionParser.Load(path),
        ["Sd2d64.dat"] = path => SoundDefinitionParser.Load(path),
    };

    private static readonly Dictionary<string, Action<string>> ImportersByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".scn"] = path => ScenarioParser.Load(path),
        [".spl"] = path => AiScriptParser.Load(path),
        [".rsi"] = path => ScenarioResearchParser.Load(path),
        [".psf"] = path => SurfaceParser.Load(path),
        [".csp"] = path => ConfigSpaceParser.Load(path),
        [".env"] = path => EnvironmentParser.Load(path),
    };

    [Fact]
    public void Every_original_data_file_has_an_importer_and_loads()
    {
        var folder = RepoPaths.OriginalDataFolder();
        var files = Directory.GetFiles(folder, "*", SearchOption.AllDirectories);
        var problems = new List<string>();

        foreach (var path in files)
        {
            var relative = Path.GetRelativePath(folder, path);
            var importer = ImportersByName.GetValueOrDefault(Path.GetFileName(path))
                ?? ImportersByExtension.GetValueOrDefault(Path.GetExtension(path));
            if (importer is null)
            {
                problems.Add($"{relative}: no importer");
                continue;
            }

            try
            {
                importer(path);
            }
            catch (ParmFormatException e)
            {
                problems.Add($"{relative}: {e.Message}");
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
        Assert.Equal(362, files.Length);
    }
}
