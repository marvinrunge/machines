using Machines.Import.Model;
using Machines.Import.Scenarios;
using Machines.Sim;
using Machines.Sim.World;

namespace Machines.Tests.Sim;

/// <summary>
/// The M2 gate: every skirmish and multiplayer setup of the original menus loads and plays.
/// Each map runs with all its races as AI for a few minutes of game time.
/// </summary>
public class AllMapsTests
{
    private static readonly Lazy<GameData> DataLoader = new(() => GameData.Load(RepoPaths.OriginalDataFolder()));

    public static IEnumerable<object[]> Setups() =>
        ScenarioCatalogueParser.Load(RepoPaths.OriginalData("sceninfo.dat")) is var catalogue
            ? catalogue.Skirmish.Concat(catalogue.Multiplayer)
                .SelectMany(t => t.Scenarios)
                .Select(s => (s.Name, s.Planet))
                .Distinct()
                .Select(s => new object[] { s.Name, s.Planet })
            : [];

    [Theory]
    [MemberData(nameof(Setups))]
    public void Skirmish_setup_loads_and_plays(string scenario, string planet)
    {
        var data = DataLoader.Value;
        var races = data.LoadScenario(scenario).Races.Select(r => r.Race).ToList();
        var world = MatchSetup.Create(data, scenario, planet, races.ToDictionary(r => r, _ => Controller.Ai), seed: 5);

        for (var i = 0; i < 3 * 60 * GameWorld.TicksPerSecond; i++)
            world.Step();

        Assert.All(races, race => Assert.True(world.Actors.Any(a => a.Race == race) || world.Races[race].Defeated));

        // Something happens, unless the scripts give no squad anything to do: then the armies stand and
        // defend, as in the original (e.g. joust.spl has no squads at all).
        var active = world.Machines.Any(m => m.Order is not IdleOrder || m.Inside is not null) || world.Winner is not null;
        var scriptsHaveWork = world.AiControllers.Any(ai => ai.Script.Squads.Any(s => s.Operations.Count > 0));
        Assert.True(active || !scriptsHaveWork, $"{scenario}: AI scripts have operations but nothing moved");
    }
}
