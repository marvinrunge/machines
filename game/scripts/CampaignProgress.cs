using System.Text;
using System.Text.Json;
using Godot;
using System.Text.Json.Serialization;
using Machines.Import.Model;
using Machines.Import.Scenarios;
using Machines.Sim.World;

namespace Machines.Game;

/// <summary>
/// The campaign's players and how far each has come (MachGuiDbPlayer and MachGuiDbPlayerScenario in the original's
/// database file), kept in <c>saves/campaign.json</c>: for every mission played its attempts, highest score and
/// whether it was won, and for a won one the flags it set and the machines that came through it.
/// </summary>
public sealed class CampaignProgress
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public List<CampaignPlayer> Players { get; set; } = [];

    /// <summary>The name of the player playing the campaign now.</summary>
    public string? Current { get; set; }

    public static string FilePath => System.IO.Path.Combine(Game.SaveFolder, "campaign.json");

    [JsonIgnore]
    public CampaignPlayer? CurrentPlayer => Players.FirstOrDefault(p => p.Name == Current);

    /// <summary>The players saved, or none when there is no file yet or it cannot be read.</summary>
    public static CampaignProgress Load()
    {
        try
        {
            return File.Exists(FilePath) ? JsonSerializer.Deserialize<CampaignProgress>(File.ReadAllText(FilePath), Json) ?? new() : new();
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            GD.PushWarning($"campaign progress not read: {exception.Message}");
            return new();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(Game.SaveFolder);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
    }
}

public sealed class CampaignPlayer
{
    public string Name { get; set; } = "";

    /// <summary>The mission the mission page picks first: the one last chosen, or the next after one won.</summary>
    public string? LastScenario { get; set; }

    /// <summary>The mission played last, which the player page's status names.</summary>
    public string? LastPlayed { get; set; }

    /// <summary>What happened in each mission played, by scenario name.</summary>
    public Dictionary<string, MissionRecord> Missions { get; set; } = [];

    public bool HasWon(string scenario) => Missions.TryGetValue(scenario, out var record) && record.Won;

    /// <summary>Whether everything an entry comes <c>AFTER</c> has been won (MachGuiDbElement::isAvailable).</summary>
    public bool IsAvailable(IEnumerable<string> after) => after.All(HasWon);

    public int TotalScore => Missions.Values.Sum(m => m.BestScore);

    /// <summary>What the next mission takes over: the flags and machines of the missions won.</summary>
    public CampaignCarry Carry()
    {
        var carry = new CampaignCarry();
        foreach (var (scenario, record) in Missions.Where(m => m.Value.Won))
        {
            carry.Flags[scenario] = [.. record.Flags];
            if (record.Survivors.Count > 0)
                carry.Survivors[scenario] = [.. record.Survivors];
        }
        return carry;
    }

    /// <summary>
    /// A mission played to its end (MachGuiStartupScreens::contextVictory and contextDefeat): one more attempt and
    /// the highest score; a win keeps the flags it set and, for a mission that saves them, its surviving machines.
    /// </summary>
    public void Record(string scenario, bool won, int score, IEnumerable<string> flags, List<SurvivingMachine>? survivors)
    {
        if (!Missions.TryGetValue(scenario, out var record))
            Missions[scenario] = record = new MissionRecord();
        if (record.Played == 0 || score > record.BestScore)
            record.BestScore = score;
        record.Played++;
        LastPlayed = scenario;
        if (!won)
            return;
        record.Won = true;
        record.Flags = flags.Distinct().ToList();
        if (survivors is not null)
            record.Survivors = survivors;
        LastScenario = Campaign.NextAfter(scenario, this) ?? scenario;
    }
}

public sealed class MissionRecord
{
    public bool Won { get; set; }
    public int BestScore { get; set; }
    public int Played { get; set; }
    public List<string> Flags { get; set; } = [];
    public List<SurvivingMachine> Survivors { get; set; } = [];
}

/// <summary>
/// The campaign as the original's menus offer it (sceninfo.dat's CAMPAIGNS): its missions, the matches they start
/// and the texts and films of the player's game copy that go with them.
/// </summary>
public static class Campaign
{
    private static ScenarioCatalogue? _catalogue;

    public static ScenarioCatalogue Catalogue =>
        _catalogue ??= ScenarioCatalogueParser.Load(System.IO.Path.Combine(Paths.OriginalData, "sceninfo.dat"));

    /// <summary>Whether a match is a campaign mission: its script decides it.</summary>
    public static bool IsCampaign(MatchRequest request) => request.Options.Victory == VictoryCondition.Scripted;

    public static CatalogueScenario? Entry(string scenario) =>
        Catalogue.CampaignScenarios.FirstOrDefault(s => s.Name.Equals(scenario, StringComparison.OrdinalIgnoreCase));

    /// <summary>The mission to pick after <paramref name="scenario"/> was won: the next one open, in the catalogue's order.</summary>
    public static string? NextAfter(string scenario, CampaignPlayer player) => Catalogue.CampaignScenarios
        .SkipWhile(s => !s.Name.Equals(scenario, StringComparison.OrdinalIgnoreCase)).Skip(1)
        .FirstOrDefault(s => player.IsAvailable(s.After))?.Name;

    /// <summary>A file of the game copy named in sceninfo.dat (a film or a picture), when it is there.</summary>
    public static string? Media(string? gameFolder, string? relative)
    {
        if (gameFolder is null || string.IsNullOrEmpty(relative))
            return null;
        var path = System.IO.Path.Combine(gameFolder, relative.Replace('\\', '/'));
        if (File.Exists(path))
            return path;
        // The game copy spells some names otherwise (VicTut.BMP for victut.bmp): match regardless of case.
        var folder = System.IO.Path.GetDirectoryName(path);
        return folder is not null && Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder).FirstOrDefault(f => System.IO.Path.GetFileName(f).Equals(System.IO.Path.GetFileName(path), StringComparison.OrdinalIgnoreCase))
            : null;
    }

    /// <summary>
    /// The match of a mission for <paramref name="player"/>: its races as the scenario's RACES_DEFAULT has them, in
    /// their own colours, and what the missions won before hand on.
    /// </summary>
    public static MatchRequest Request(CatalogueScenario entry, CampaignPlayer player)
    {
        var scenario = Paths.Data.LoadScenario(entry.Name);
        var players = MissionScript.CampaignPlayers(scenario);
        var local = players.Where(p => p.Value == Controller.Player).Select(p => p.Key).DefaultIfEmpty(players.Keys.Min()).First();
        var starts = new StartPositions(local, players, players.Keys.ToDictionary(r => r, r => r));
        return new MatchRequest(entry.Name, entry.Planet, starts,
            new MatchOptions { Victory = VictoryCondition.Scripted, Campaign = player.Carry() });
    }

    /// <summary>A box for a campaign text (see <see cref="Rich"/>), scrolling when it runs long.</summary>
    public static RichTextLabel TextView(int size)
    {
        var view = new RichTextLabel
        {
            BbcodeEnabled = true,
            ScrollActive = true,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            SelectionEnabled = false,
            FocusMode = Control.FocusModeEnum.None,
        };
        view.AddThemeFontSizeOverride("normal_font_size", size);
        view.AddThemeColorOverride("default_color", MenuTheme.Text);
        view.AddThemeConstantOverride("line_separation", 4);
        return view;
    }

    /// <summary>
    /// A campaign text for a <see cref="RichTextLabel"/>: <c>&lt;w&gt;</c> turns the rest of its line white (the
    /// original's headings and task marks), shown bright here; a line of a no-break space alone is an empty line.
    /// </summary>
    public static string Rich(string text)
    {
        var bright = MenuTheme.Bright.ToHtml(false);
        var lines = new List<string>();
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            var line = raw.Replace("[", "[lb]");
            var white = line.IndexOf("<w>", StringComparison.Ordinal);
            if (white >= 0)
                line = $"{line[..white]}[color=#{bright}]{line[(white + 3)..].Replace("<w>", "")}[/color]";
            lines.Add(raw.Replace("<w>", "").Trim(' ', ' ').Length == 0 ? "" : line);
        }
        return string.Join("\n", lines).Trim('\n');
    }

    /// <summary>
    /// The mission's objective and tasks (MachGuiCtxBriefing::displayObjectives): before the match the tasks shown
    /// from the start, during it those the script has made known (<see cref="Tasks"/>).
    /// </summary>
    public static string Objectives(Machines.Import.Text.ScenarioText text, MissionScript? script, Func<int, string, string> strings) =>
        (text.Objectives.Count > 0 ? text.Objectives[0] : "") + Tasks(text, script, ended: false, strings);

    /// <summary>
    /// The mission's tasks under their heading: with no <paramref name="script"/> those shown from the start, otherwise
    /// those the script has made known, marked done, or once the match has <paramref name="ended"/> failed
    /// (MachGuiCtxDeBriefing).
    /// </summary>
    public static string Tasks(Machines.Import.Text.ScenarioText text, MissionScript? script, bool ended, Func<int, string, string> strings)
    {
        var shown = new StringBuilder();
        var heading = false;
        for (var i = 0; i < text.Tasks.Count; i++)
        {
            var known = script is null
                ? i < text.TasksStartAvailable.Count && text.TasksStartAvailable[i]
                : script.Tasks.ContainsKey(i + 1) || i < text.TasksStartAvailable.Count && text.TasksStartAvailable[i];
            if (!known)
                continue;
            if (!heading)
            {
                shown.Append($"\n<w>{strings(2420, "Tasks")}\n");
                heading = true;
            }
            shown.Append('\n').Append(text.Tasks[i]);
            var done = script?.Tasks.GetValueOrDefault(i + 1) == true;
            if (done)
                shown.Append($"<w>{strings(2430, "Completed")}\n");
            else if (ended && script is not null)
                shown.Append($"<w>{strings(2431, "Failed")}\n");
        }
        return shown.ToString();
    }
}
