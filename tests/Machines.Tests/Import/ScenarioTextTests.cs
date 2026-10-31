using Machines.Import.Scenarios;
using Machines.Import.Text;

namespace Machines.Tests.Import;

/// <summary>
/// The campaign texts in the player's own copy of the game (<c>pdata</c>, found through <c>classic/game_path.txt</c>).
/// Skipped when there is no game copy.
/// </summary>
public class ScenarioTextTests
{
    internal static string? PdataFolder { get; } = FindPdata();

    private static string? FindPdata()
    {
        var repository = Path.GetDirectoryName(Path.GetDirectoryName(RepoPaths.OriginalDataFolder()))!;
        var gamePath = Path.Combine(repository, "classic", "game_path.txt");
        if (!File.Exists(gamePath))
            return null;
        var pdata = Path.Combine(File.ReadAllText(gamePath).Trim(), "pdata");
        return Directory.Exists(pdata) ? pdata : null;
    }

    private sealed class PdataFactAttribute : FactAttribute
    {
        public PdataFactAttribute()
        {
            if (PdataFolder is null)
                Skip = "no game copy (classic/game_path.txt)";
        }
    }

    [PdataFact]
    public void Every_text_file_reads_to_its_end()
    {
        // sceninfo.bin is the original's compiled catalogue, not a text.
        foreach (var file in Directory.EnumerateFiles(PdataFolder!, "*.bin").Where(f => Path.GetFileName(f) != "sceninfo.bin"))
        {
            var text = ScenarioTextParser.Load(file);
            Assert.Equal(text.Tasks.Count, text.TasksStartAvailable.Count);
            Assert.Equal(text.Objectives.Count, text.ObjectiveVoiceMails.Count);
        }
    }

    [PdataFact]
    public void Tutorial_one_has_its_briefing_task_and_spoken_objective()
    {
        var text = ScenarioTextParser.Load(Path.Combine(PdataFolder!, "tut01.bin"));
        Assert.Equal("Tutorial 1\nBasic Selection and Movement", ScenarioText.Plain(text.CampaignText));
        Assert.StartsWith("<w>Basic Selection and Movement", text.Objectives[0]);
        Assert.Equal("Sounds/menus/tut01ob1.wav", text.ObjectiveVoiceMails[0]);
        Assert.StartsWith("Try to make a habit", text.Hint);
        Assert.StartsWith("You have pressed 'Self Destruct'", text.LoseText);
        Assert.Equal(["> Select the 'REAPER' machine unit and move it first to point 'I' and then to point 'II'\n"], text.Tasks);
        Assert.Equal([true], text.TasksStartAvailable);
    }

    [PdataFact]
    public void Every_campaign_entry_has_its_text()
    {
        var catalogue = ScenarioCatalogueParser.Load(RepoPaths.OriginalData("sceninfo.dat"));
        var texts = catalogue.Campaign.Select(s => s.Text)
            .Concat(catalogue.Campaign.SelectMany(s => s.Planets).Select(p => p.Text))
            .Concat(catalogue.CampaignScenarios.Select(s => s.Text));
        foreach (var name in texts)
        {
            Assert.NotNull(name);
            var path = Path.Combine(PdataFolder!, Path.ChangeExtension(name, "bin"));
            Assert.True(File.Exists(path), $"{path} is missing");
            Assert.NotEmpty(ScenarioText.Plain(ScenarioTextParser.Load(path).CampaignText));
        }
    }
}
