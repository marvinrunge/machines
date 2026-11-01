using Machines.Import.Model;
using Machines.Sim;
using Machines.Sim.World;

namespace Machines.Tests.Sim;

/// <summary>
/// The showcase (data/maps/showcase.scn): every building and machine of every race stands there, and as the races
/// are allied and have no AI script, a minute later all of it still stands where it was put.
/// </summary>
public class ShowcaseMapTests
{
    [Fact]
    public void Showcase_stands_still_and_nobody_fights()
    {
        var data = GameData.Load(RepoPaths.OriginalDataFolder());
        var scenario = data.LoadScenario("showcase");
        var races = scenario.Races.Select(r => r.Race).ToList();
        var world = MatchSetup.Create(data, "showcase", "m_desert", races.ToDictionary(r => r, _ => Controller.Ai), seed: 5);
        var start = world.Actors.ToDictionary(a => a.Id, a => (a.Position, a.HitPoints));

        for (var i = 0; i < 60 * GameWorld.TicksPerSecond; i++)
            world.Step();

        Assert.Equal(4, races.Count);
        Assert.All(scenario.Races, setup =>
        {
            Assert.Equal(27, setup.Constructions.Count);
            Assert.Equal(setup.Constructions.Count, world.Constructions.Count(c => c.Race == setup.Race));
            Assert.Equal(setup.Machines.Count, world.Machines.Count(m => m.Race == setup.Race));
            Assert.All(setup.Machines, unit => Assert.Contains(world.Machines, m => m.Race == setup.Race
                && System.Numerics.Vector2.Distance(m.Position, new(unit.Position.X, unit.Position.Y)) < 0.01f));
        });
        Assert.All(races, race => Assert.DoesNotContain(races, other => world.AreEnemies(race, other)));
        Assert.All(world.Actors, a => Assert.Equal(start[a.Id], (a.Position, a.HitPoints)));
        Assert.Null(world.Winner);
    }

    [Fact]
    public void An_ally_is_no_enemy_but_only_the_way_it_was_declared()
    {
        var data = GameData.Load(RepoPaths.OriginalDataFolder());
        var world = MatchSetup.Create(data, "m_desert", "m_desert", new Dictionary<Race, Controller> { [Race.Red] = Controller.Ai, [Race.Blue] = Controller.Ai });
        world.Races[Race.Red].Allies.Add(Race.Blue);

        Assert.False(world.AreEnemies(Race.Red, Race.Blue));
        Assert.True(world.AreEnemies(Race.Blue, Race.Red));
        Assert.False(world.AreEnemies(Race.Red, Race.Red));
    }
}
