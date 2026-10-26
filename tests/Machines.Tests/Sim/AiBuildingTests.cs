using System.Numerics;
using Machines.Import.Model;
using Machines.Sim;
using Machines.Sim.World;

namespace Machines.Tests.Sim;

/// <summary>What the 1999 AI built beyond its script: mines on found sites, and REBUILD buildings again.</summary>
public class AiBuildingTests
{
    private static readonly Lazy<GameData> DataLoader = new(() => GameData.Load(RepoPaths.OriginalDataFolder()));

    private static GameWorld NewMatch() =>
        MatchSetup.Create(DataLoader.Value, "m_desert", "m_desert",
            new Dictionary<Race, Controller> { [Race.Red] = Controller.Player, [Race.Blue] = Controller.Ai }, seed: 7);

    private static void Run(GameWorld world, float seconds, Func<bool>? until = null)
    {
        for (var i = 0; i < seconds * GameWorld.TicksPerSecond; i++)
        {
            world.Step();
            if (until?.Invoke() == true)
                return;
        }
    }

    [Fact]
    public void The_ai_rebuilds_a_destroyed_rebuild_building_where_it_stood()
    {
        var world = NewMatch();
        // m_desert places Blue's civilian factory with REBUILD.
        var factory = world.Constructions.Single(c => c.Race == Race.Blue && c.IsType("FACTORY", "CIVILIAN"));
        Assert.True(factory.Rebuild);
        var position = factory.Position;
        Run(world, 2);

        world.Damage(factory, 1_000_000, Race.Red);
        Run(world, 1);
        Assert.Null(world.Find(factory.Id));

        // The rebuild joins the end of Blue's list, after the scenario's other plans; money is no question here.
        world.Races[Race.Blue].Bmu = 20_000;
        Run(world, 1800, () => world.Constructions.Any(c => c.Race == Race.Blue && c.IsType("FACTORY", "CIVILIAN") && Vector2.Distance(c.Position, position) < 1));
        var again = world.Constructions.Single(c => c.Race == Race.Blue && c.IsType("FACTORY", "CIVILIAN") && Vector2.Distance(c.Position, position) < 1);
        Assert.NotEqual(factory.Id, again.Id);
    }

    [Fact]
    public void The_ai_puts_a_mine_on_every_site_it_finds()
    {
        var world = NewMatch();
        // Show Blue the sites near its base, as its locators would.
        var pod = world.Constructions.Single(c => c.Race == Race.Blue && c.IsType("POD"));
        var near = world.Sites.Where(s => s.Ore > 100).OrderBy(s => Vector2.Distance(s.Position, pod.Position)).Take(4).ToList();
        foreach (var site in near)
            site.DiscoveredBy.Add(Race.Blue);
        world.Races[Race.Blue].Bmu = 2000;

        Run(world, 900, () => near.All(s => s.Mine is not null));

        // More than the two mines the remake's first AI kept: one on each of the four found sites.
        Assert.All(near, s => Assert.NotNull(s.Mine));
    }
}
