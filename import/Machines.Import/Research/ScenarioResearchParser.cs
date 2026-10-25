using Machines.Import.Model;
using Machines.Import.Parm;

namespace Machines.Import.Research;

/// <summary>
/// Reads a scenario's <c>.rsi</c> file. Follows the research part of <c>MachLogScenario</c>:
/// <code>
/// RACE RED
/// MACHINE RESEARCHED AGGRESSOR 1 SUB GRUNT WCOMBO R_BOLTER
/// CONSTRUCTION NOT POD 1 WCOMBO T_ION_ORBITAL_CANNON RESEARCH_COST 600
/// </code>
/// Any word other than <c>RESEARCHED</c> in the second position means not researched.
/// </summary>
public static class ScenarioResearchParser
{
    public static ScenarioResearch Load(string path) => Parse(File.ReadAllText(path));

    public static ScenarioResearch Parse(string text)
    {
        var entries = new List<ScenarioResearchEntry>();
        var ignored = new List<ParmLine>();
        var race = RaceTarget.Ai;

        foreach (var line in TextLines.Read(text))
        {
            switch (line.Keyword)
            {
                case "RACE":
                    if (line.Args.Count < 1)
                        throw new ParmFormatException("RACE needs a race", line.LineNumber);
                    race = line.Args[0] switch
                    {
                        "RED" => RaceTarget.Red,
                        "BLUE" => RaceTarget.Blue,
                        "GREEN" => RaceTarget.Green,
                        "YELLOW" => RaceTarget.Yellow,
                        "AI" => RaceTarget.Ai,
                        "PC" => RaceTarget.Pc,
                        _ => RaceTarget.All,
                    };
                    break;

                case "MACHINE":
                case "CONSTRUCTION":
                    entries.Add(ParseEntry(line, race));
                    break;

                default:
                    ignored.Add(line);
                    break;
            }
        }

        return new ScenarioResearch(entries, ignored);
    }

    private static ScenarioResearchEntry ParseEntry(ParmLine line, RaceTarget race)
    {
        if (line.Args.Count < 3)
            throw new ParmFormatException($"{line.Keyword} needs RESEARCHED/NOT, a type and a level", line.LineNumber);

        string? subType = null, weaponCombo = null;
        int? researchCost = null, buildingCost = null;

        for (var i = 3; i < line.Args.Count; i++)
        {
            var word = line.Args[i];
            if (word is not ("SUB" or "WCOMBO" or "RESEARCH_COST" or "BUILDING_COST"))
                throw new ParmFormatException($"unexpected '{word}'", line.LineNumber);
            if (i + 1 >= line.Args.Count)
                throw new ParmFormatException($"{word} needs a value", line.LineNumber);

            var value = line.Args[++i];
            switch (word)
            {
                case "SUB": subType = value; break;
                case "WCOMBO": weaponCombo = value; break;
                // As in the original, a zero override means "no override".
                case "RESEARCH_COST": researchCost = NonZero(ParmDataParser.ParseInt(value, line)); break;
                case "BUILDING_COST": buildingCost = NonZero(ParmDataParser.ParseInt(value, line)); break;
            }
        }

        return new ScenarioResearchEntry(
            Race: race,
            Kind: line.Keyword == "MACHINE" ? ResearchKind.Machine : ResearchKind.Construction,
            Researched: line.Args[0] == "RESEARCHED",
            Key: new ResearchKey(line.Args[1], subType, ParmDataParser.ParseInt(line.Args[2], line), weaponCombo),
            ResearchCostOverride: researchCost,
            BuildingCostOverride: buildingCost,
            LineNumber: line.LineNumber);
    }

    private static int? NonZero(int value) => value == 0 ? null : value;
}
