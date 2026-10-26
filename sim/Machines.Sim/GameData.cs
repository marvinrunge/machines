using Machines.Import.Model;
using Machines.Import.Parm;
using Machines.Import.Planets;
using Machines.Import.Research;
using Machines.Import.Scenarios;

namespace Machines.Sim;

/// <summary>Everything the simulation reads from the original data, loaded once per game.</summary>
public sealed class GameData
{
    public required ParmData Parm { get; init; }
    public required ResearchTree ResearchTree { get; init; }
    public required ConstructionOrder ConstructionOrder { get; init; }
    public required IReadOnlyList<FactoryBuildList> FactoryLists { get; init; }

    /// <summary>The <c>data/original</c> folder (the original game's <c>data</c> folder, plus <c>models</c> and <c>sounds</c>).</summary>
    public required string Folder { get; init; }

    public static GameData Load(string originalDataFolder) => new()
    {
        Folder = originalDataFolder,
        Parm = ParmDataParser.Load(Path.Combine(originalDataFolder, "parmdata.dat")),
        ResearchTree = ResearchTreeParser.Load(Path.Combine(originalDataFolder, "global.rst")),
        ConstructionOrder = ConstructionOrderParser.Load(Path.Combine(originalDataFolder, "global.cst")),
        FactoryLists = FactoryBuildListParser.Load(Path.Combine(originalDataFolder, "factory.bld")),
    };

    public Scenario LoadScenario(string name) => ScenarioParser.Load(Path.Combine(Folder, name + ".scn"));

    public AiScript LoadAiScript(string path) => AiScriptParser.Load(Path.Combine(Folder, Path.GetFileName(path)));

    public ScenarioResearch LoadResearch(string path) => ScenarioResearchParser.Load(Path.Combine(Folder, Path.GetFileName(path)));

    public Planet LoadPlanet(string name) => Planet.Load(Path.Combine(Folder, "models", "planet", name));

    /// <summary>The machines a factory of this type and level offers (exactly its level's list, as in the original).</summary>
    public IReadOnlyList<ResearchKey> FactoryOffer(string factoryType, int level) =>
        FactoryLists.FirstOrDefault(l => l.FactoryType == factoryType && l.HardwareLevel == level)?.Machines ?? [];
}
