using Machines.Import.Model;
using Machines.Import.Parm;

namespace Machines.Import.Scenarios;

/// <summary>
/// Reads <c>sceninfo.dat</c>. Follows <c>MachGuiDatabase</c>:
/// <code>
/// CAMPAIGNS
///   SYSTEM name IDS n TEXT t FLIC f [AFTER s]
///     PLANET name IDS n FLIC f TEXT t [AFTER s]
///       SCENARIO name PLANET p IDS n TEXT t MUSIC m [AFTER s] [MAXPLAYERS n]
///         CAMPAIGN / BRIEFING / ENTRY / WIN / LOSE / DEBRIEFING / DEBRIEFINGLOSE file
///         SAVE_MACHINES race
///       ENDSCENARIO
///     ENDPLANET
///   ENDSYSTEM
/// END
/// SKIRMISHES (and MULTI_PLAYER)
///   SMALL | MEDIUM | LARGE
///     TERRAIN name IDS n TEXT t
///       SCENARIO ...            one line each, no ENDSCENARIO
///     ENDTERRAIN
///   END
/// END
/// </code>
/// </summary>
public static class ScenarioCatalogueParser
{
    private static readonly HashSet<string> MediaSlots = ["CAMPAIGN", "BRIEFING", "ENTRY", "WIN", "LOSE", "DEBRIEFING", "DEBRIEFINGLOSE"];

    public static ScenarioCatalogue Load(string path) => Parse(File.ReadAllText(path));

    public static ScenarioCatalogue Parse(string text)
    {
        var lines = TextLines.Read(text).ToList();
        var i = 0;

        List<StarSystem> campaign = [];
        List<SkirmishTerrain> skirmish = [], multiplayer = [];

        while (i < lines.Count)
        {
            var line = lines[i++];
            switch (line.Keyword)
            {
                case "CAMPAIGNS": campaign = ReadCampaign(lines, ref i); break;
                case "SKIRMISHES": skirmish = ReadTerrains(lines, ref i); break;
                case "MULTI_PLAYER": multiplayer = ReadTerrains(lines, ref i); break;
                default: throw new ParmFormatException($"unexpected '{line.Keyword}'", line.LineNumber);
            }
        }

        return new ScenarioCatalogue(campaign, skirmish, multiplayer);
    }

    private static List<StarSystem> ReadCampaign(List<ParmLine> lines, ref int i)
    {
        var systems = new List<StarSystem>();
        for (var line = Next(lines, ref i); line.Keyword != "END"; line = Next(lines, ref i))
        {
            Expect(line, "SYSTEM");
            var (attributes, after) = Attributes(line);
            var planets = new List<CampaignPlanet>();

            for (var inner = Next(lines, ref i); inner.Keyword != "ENDSYSTEM"; inner = Next(lines, ref i))
            {
                Expect(inner, "PLANET");
                var (planetAttributes, planetAfter) = Attributes(inner);
                var scenarios = new List<CatalogueScenario>();

                for (var entry = Next(lines, ref i); entry.Keyword != "ENDPLANET"; entry = Next(lines, ref i))
                {
                    Expect(entry, "SCENARIO");
                    scenarios.Add(ReadCampaignScenario(entry, lines, ref i));
                }

                planets.Add(new CampaignPlanet(inner.Args[0], Int(planetAttributes, "IDS", inner), planetAttributes.GetValueOrDefault("TEXT"),
                    planetAttributes.GetValueOrDefault("FLIC"), planetAfter, scenarios, inner.LineNumber));
            }

            systems.Add(new StarSystem(line.Args[0], Int(attributes, "IDS", line), attributes.GetValueOrDefault("TEXT"),
                attributes.GetValueOrDefault("FLIC"), after, planets, line.LineNumber));
        }
        return systems;
    }

    private static CatalogueScenario ReadCampaignScenario(ParmLine header, List<ParmLine> lines, ref int i)
    {
        var media = new Dictionary<string, string>();
        Race? saveMachines = null;

        for (var line = Next(lines, ref i); line.Keyword != "ENDSCENARIO"; line = Next(lines, ref i))
        {
            if (line.Args.Count != 1)
                throw new ParmFormatException($"{line.Keyword} needs one value", line.LineNumber);

            if (line.Keyword == "SAVE_MACHINES")
                saveMachines = Enum.TryParse<Race>(line.Args[0], ignoreCase: true, out var race) && Enum.IsDefined(race)
                    ? race
                    : throw new ParmFormatException($"unknown race '{line.Args[0]}'", line.LineNumber);
            else if (MediaSlots.Contains(line.Keyword))
                media[line.Keyword] = line.Args[0];
            else
                throw new ParmFormatException($"unexpected '{line.Keyword}' in a campaign scenario", line.LineNumber);
        }

        return Scenario(header, media, saveMachines);
    }

    private static List<SkirmishTerrain> ReadTerrains(List<ParmLine> lines, ref int i)
    {
        var terrains = new List<SkirmishTerrain>();
        for (var sizeLine = Next(lines, ref i); sizeLine.Keyword != "END"; sizeLine = Next(lines, ref i))
        {
            var size = sizeLine.Keyword switch
            {
                "SMALL" => MapSize.Small,
                "MEDIUM" => MapSize.Medium,
                "LARGE" => MapSize.Large,
                _ => throw new ParmFormatException($"expected SMALL, MEDIUM or LARGE, found '{sizeLine.Keyword}'", sizeLine.LineNumber),
            };

            for (var line = Next(lines, ref i); line.Keyword != "END"; line = Next(lines, ref i))
            {
                Expect(line, "TERRAIN");
                var (attributes, _) = Attributes(line);
                var scenarios = new List<CatalogueScenario>();
                for (var entry = Next(lines, ref i); entry.Keyword != "ENDTERRAIN"; entry = Next(lines, ref i))
                {
                    Expect(entry, "SCENARIO");
                    scenarios.Add(Scenario(entry, new Dictionary<string, string>(), null));
                }
                terrains.Add(new SkirmishTerrain(size, line.Args[0], Int(attributes, "IDS", line), attributes.GetValueOrDefault("TEXT"), scenarios, line.LineNumber));
            }
        }
        return terrains;
    }

    private static CatalogueScenario Scenario(ParmLine line, Dictionary<string, string> media, Race? saveMachines)
    {
        var (attributes, after) = Attributes(line);
        return new CatalogueScenario(
            Name: line.Args[0],
            Planet: attributes.TryGetValue("PLANET", out var planet) ? planet : throw new ParmFormatException("SCENARIO needs a PLANET", line.LineNumber),
            StringId: Int(attributes, "IDS", line),
            Text: attributes.GetValueOrDefault("TEXT"),
            Music: attributes.ContainsKey("MUSIC") ? Int(attributes, "MUSIC", line) : null,
            After: after,
            MaxPlayers: attributes.ContainsKey("MAXPLAYERS") ? Int(attributes, "MAXPLAYERS", line) : null,
            Media: media,
            SaveMachines: saveMachines,
            LineNumber: line.LineNumber);
    }

    /// <summary>
    /// The <c>KEY value</c> pairs after an entry's name. <c>AFTER</c> comes last and takes every remaining word,
    /// as in the original (<c>AFTER gracko resfac</c> needs both).
    /// </summary>
    private static (Dictionary<string, string> Values, List<string> After) Attributes(ParmLine line)
    {
        if (line.Args.Count == 0)
            throw new ParmFormatException($"{line.Keyword} needs a name", line.LineNumber);

        var attributes = new Dictionary<string, string>();
        for (var a = 1; a < line.Args.Count; a += 2)
        {
            if (line.Args[a] == "AFTER")
            {
                var after = line.Args.Skip(a + 1).ToList();
                return after.Count > 0 ? (attributes, after) : throw new ParmFormatException("AFTER needs at least one name", line.LineNumber);
            }
            if (line.Args[a] is not ("IDS" or "TEXT" or "FLIC" or "NAME" or "PLANET" or "MUSIC" or "MAXPLAYERS"))
                throw new ParmFormatException($"unknown attribute '{line.Args[a]}'", line.LineNumber);
            if (a + 1 >= line.Args.Count)
                throw new ParmFormatException($"{line.Args[a]} needs a value", line.LineNumber);
            if (!attributes.TryAdd(line.Args[a], line.Args[a + 1]))
                throw new ParmFormatException($"{line.Args[a]} appears twice", line.LineNumber);
        }
        return (attributes, []);
    }

    private static int Int(Dictionary<string, string> attributes, string key, ParmLine line) =>
        attributes.TryGetValue(key, out var value)
            ? ParmDataParser.ParseInt(value, line)
            : throw new ParmFormatException($"{line.Keyword} needs {key}", line.LineNumber);

    private static ParmLine Next(List<ParmLine> lines, ref int i) =>
        i < lines.Count ? lines[i++] : throw new ParmFormatException("file ends inside a block", lines.Count == 0 ? 0 : lines[^1].LineNumber);

    private static void Expect(ParmLine line, string keyword)
    {
        if (line.Keyword != keyword)
            throw new ParmFormatException($"expected {keyword}, found '{line.Keyword}'", line.LineNumber);
    }
}
