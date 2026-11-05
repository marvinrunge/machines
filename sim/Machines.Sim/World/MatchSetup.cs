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

        // Active artefacts (trees, rocks, wrecks, doors the scenario names ACTIVE) stand in the way: their boundary,
        // turned as they stand, is blocked to machines and building (MachLogArtefact::addObstaclePolygon), and they
        // can be destroyed. Scenery (DUMB) blocks nothing. ONDESTROY links name artefacts by their ID clause.
        if (scenario.Artefacts is { } artefacts)
        {
            var tagged = new Dictionary<int, Artefact>();
            for (var i = 0; i < artefacts.Placements.Count; i++)
            {
                var placement = artefacts.Placements[i];
                var subType = artefacts.Types.ToList().FindIndex(t => t.Name == placement.TypeName);
                if (subType < 0 || artefacts.Types[subType] is not { Active: true } type)
                    continue;
                var (sin, cos) = (DetMath.Sin(placement.Orientation * MathF.PI / 180), DetMath.Cos(placement.Orientation * MathF.PI / 180));
                var at = new Vector2(placement.Position.X, placement.Position.Y);
                var boundary = type.Boundary.Select(p => at + new Vector2(p.X * cos - p.Y * sin, p.X * sin + p.Y * cos)).ToList();
                var artefact = world.AddArtefact(i, at, type, subType, boundary);
                if (placement.Id is { } tag)
                    tagged[tag] = artefact;
            }
            foreach (var link in artefacts.OnDestroy)
            {
                if (tagged.TryGetValue(link.Id, out var dying) && tagged.TryGetValue(link.Damage, out var damaged))
                    dying.OnDestroy.Add((damaged, link.HitPoints, link.Armour));
            }
        }

        // Unmarked sites appear in every setting, marked ones only in theirs; ids are list positions.
        foreach (var site in scenario.MineralSites.Where(s => s.Level == ResourceLevel.Default || s.Level == options.Resources))
            world.Sites.Add(new MineralSiteState(world.Sites.Count, site));

        foreach (var (race, controller) in players)
        {
            var setup = scenario.Race(race) ?? throw new ArgumentException($"{scenarioName} has no {race} race");
            var state = new RaceState(race, controller)
            {
                Bmu = MatchOptions.BmuFor(options.StartingBmu) ?? setup.Bmu ?? 0,
                VirtualBeacon = setup.VirtualBeacon switch { "LEVEL_1_BEACON" => 1, "LEVEL_3_BEACON" => 3, _ => 0 },
                ScoreDisplayed = setup.ScoreDisplayed,
            };
            state.Allies.UnionWith(setup.Allies);
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
                placed.DisplayAs = building.DisplayAs;
                placed.IonCannonGranted = building.ActivateIonCannon;
                placed.Rebuild = building.Rebuild;
            }

            foreach (var unit in setup.Machines)
            {
                var machine = world.AddMachine(race, new Vector2(unit.Position.X, unit.Position.Y), unit.Type, unit.SubType, unit.HardwareLevel, unit.WeaponCombo);
                machine.Squad = unit.Squad;
                machine.Height = unit.Position.Z;
                machine.DisplayAs = unit.DisplayAs;
            }

            if (setup.AiScript is { } script)
                world.StandIns[race] = () => new Ai.AiController(race, data.LoadAiScript(script), setup);
            if (controller == Controller.Ai && world.StandIns.TryGetValue(race, out var standIn))
                world.AiControllers.Add(standIn());
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

        // RESTRICT_CONSTRUCTION locks just the one entry for the races it names (MachLogScenario::parseRestrictConstruction);
        // GameWorld.Buildable leaves it out while the option holds.
        foreach (var restriction in scenario.ConstructionRestrictions)
        {
            foreach (var state in world.Races.Values.Where(s => Targets(restriction.Race, s)))
                state.Locked.Add(new ConstructionOrderEntry(restriction.Type, restriction.SubType, restriction.HardwareLevel));
        }

        // As in the original, a pod victory needs pods: without any, the match falls back to annihilation.
        if (options.Victory == VictoryCondition.Pod && !world.Constructions.Any(c => c.IsType("POD")))
            world.Options = options with { Victory = VictoryCondition.Annihilation };
        foreach (var state in world.Races.Values)
        {
            state.StartedWithPod = world.Constructions.Any(c => c.Race == state.Race && c.IsType("POD"));
            state.ConstructionsReached = world.BestConstructor(state.Race);
        }

        // A campaign mission runs its script, which alone decides it; so does Default, if the scenario has one.
        if (options.Victory is VictoryCondition.Scripted or VictoryCondition.Default)
        {
            var script = MissionScript.Compile(scenario, options.Campaign);
            if (options.Victory == VictoryCondition.Default && script.ActionsLeft == 0)
                world.Options = options with { Victory = VictoryCondition.Annihilation };
            else
                world.Script = script;
        }

        return world;
    }

    private static void ApplyResearch(GameWorld world, ScenarioResearch research)
    {
        foreach (var entry in research.Entries)
        {
            if (world.Data.ResearchTree.Find(entry.Key) is null)
                continue; // known data defects (see data/README.md)

            foreach (var state in world.Races.Values.Where(s => Targets(entry.Race, s)))
            {
                state.Available.Add(entry.Key);
                if (entry.Researched)
                    state.Researched.Add(entry.Key);
                if (entry.ResearchCostOverride is { } cost)
                    state.ResearchCostOverrides[entry.Key] = cost;
            }
        }
    }

    /// <summary>
    /// Whether a scenario line for <paramref name="target"/> applies to the race: a named race, the AI's races, the
    /// players' races, or (without a race) all of them.
    /// </summary>
    private static bool Targets(RaceTarget target, RaceState state) => target switch
    {
        RaceTarget.Red => state.Race == Race.Red,
        RaceTarget.Blue => state.Race == Race.Blue,
        RaceTarget.Green => state.Race == Race.Green,
        RaceTarget.Yellow => state.Race == Race.Yellow,
        RaceTarget.Ai => state.Controller == Controller.Ai,
        RaceTarget.Pc => state.Controller == Controller.Player,
        _ => true,
    };
}
