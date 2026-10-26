using System.Numerics;
using Machines.Import.Model;

namespace Machines.Sim.World;

/// <summary>Builds a <see cref="GameWorld"/> from an original scenario.</summary>
public static class MatchSetup
{
    /// <param name="players">The races taking part and who controls each; races not listed are left out.</param>
    public static GameWorld Create(GameData data, string scenarioName, string planetName, IReadOnlyDictionary<Race, Controller> players,
        int seed = 1, MatchOptions? options = null)
    {
        options ??= new MatchOptions();
        var scenario = data.LoadScenario(scenarioName);
        var world = new GameWorld(data, data.LoadPlanet(planetName), seed) { Options = options };

        // Unmarked sites appear in every setting, marked ones only in theirs; ids are list positions.
        foreach (var site in scenario.MineralSites.Where(s => s.Level == ResourceLevel.Default || s.Level == options.Resources))
            world.Sites.Add(new MineralSiteState(world.Sites.Count, site));

        foreach (var (race, controller) in players)
        {
            var setup = scenario.Race(race) ?? throw new ArgumentException($"{scenarioName} has no {race} race");
            var state = new RaceState(race, controller) { Bmu = MatchOptions.BmuFor(options.StartingBmu) ?? setup.Bmu ?? 0 };
            world.Races[race] = state;
            world.BuildingPlans[race] = [];

            foreach (var building in setup.Constructions)
            {
                if (building.NotBuilt)
                {
                    world.BuildingPlans[race].Add(building);
                    continue;
                }
                var placed = world.AddConstruction(race, new Vector2(building.Position.X, building.Position.Y), building.Type, building.SubType,
                    building.HardwareLevel, building.Orientation, complete: true, building.WeaponCombo);
                placed.Height = building.Position.Z;
                placed.IonCannonGranted = building.ActivateIonCannon;
                placed.Rebuild = building.Rebuild;
            }

            foreach (var unit in setup.Machines)
            {
                var machine = world.AddMachine(race, new Vector2(unit.Position.X, unit.Position.Y), unit.Type, unit.SubType, unit.HardwareLevel, unit.WeaponCombo);
                machine.Squad = unit.Squad;
                machine.Height = unit.Position.Z;
            }

            if (controller == Controller.Ai && setup.AiScript is not null)
                world.AiControllers.Add(new Ai.AiController(race, data.LoadAiScript(setup.AiScript), setup));
        }

        if (scenario.Research is { } research)
        {
            var file = options.TechLevel switch
            {
                TechLevel.Low => research.Low,
                TechLevel.Medium => research.Medium,
                TechLevel.High => research.High,
                _ => research.Default,
            };
            ApplyResearch(world, data.LoadResearch(file));
        }

        // As in the original, a pod victory needs pods: without any, the match falls back to annihilation.
        if (options.Victory == VictoryCondition.Pod && !world.Constructions.Any(c => c.IsType("POD")))
            world.Options = options with { Victory = VictoryCondition.Annihilation };
        foreach (var state in world.Races.Values)
            state.StartedWithPod = world.Constructions.Any(c => c.Race == state.Race && c.IsType("POD"));

        return world;
    }

    private static void ApplyResearch(GameWorld world, ScenarioResearch research)
    {
        foreach (var entry in research.Entries)
        {
            if (world.Data.ResearchTree.Find(entry.Key) is null)
                continue; // known data defects (see data/README.md)

            foreach (var state in world.Races.Values)
            {
                var applies = entry.Race switch
                {
                    RaceTarget.Red => state.Race == Race.Red,
                    RaceTarget.Blue => state.Race == Race.Blue,
                    RaceTarget.Green => state.Race == Race.Green,
                    RaceTarget.Yellow => state.Race == Race.Yellow,
                    RaceTarget.Ai => state.Controller == Controller.Ai,
                    RaceTarget.Pc => state.Controller == Controller.Player,
                    _ => true,
                };
                if (!applies)
                    continue;

                state.Available.Add(entry.Key);
                if (entry.Researched)
                    state.Researched.Add(entry.Key);
                if (entry.ResearchCostOverride is { } cost)
                    state.ResearchCostOverrides[entry.Key] = cost;
            }
        }
    }
}
