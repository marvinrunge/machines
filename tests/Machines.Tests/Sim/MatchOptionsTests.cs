using Machines.Import.Model;
using Machines.Sim;
using Machines.Sim.World;

namespace Machines.Tests.Sim;

/// <summary>The skirmish menu settings, applied as the original applied them to m_desert.</summary>
public class MatchOptionsTests
{
    private static readonly Lazy<GameData> DataLoader = new(() => GameData.Load(RepoPaths.OriginalDataFolder()));

    private static GameWorld NewMatch(MatchOptions options, Controller red = Controller.Player) =>
        MatchSetup.Create(DataLoader.Value, "m_desert", "m_desert",
            new Dictionary<Race, Controller> { [Race.Red] = red, [Race.Blue] = Controller.Ai }, seed: 7, options);

    [Theory]
    [InlineData(StartingBmu.Default, 750)]
    [InlineData(StartingBmu.Low, 0)]
    [InlineData(StartingBmu.High, 500)]
    [InlineData(StartingBmu.SuperHigh, 10000)]
    public void Starting_bmus_replace_the_scenario_value(StartingBmu level, int expected)
    {
        Assert.Equal(expected, NewMatch(new MatchOptions { StartingBmu = level }).Races[Race.Red].Bmu);
    }

    [Theory]
    [InlineData(ResourceLevel.Low, 15)]
    [InlineData(ResourceLevel.Medium, 15 + 21)]
    [InlineData(ResourceLevel.High, 15 + 42)]
    public void Resource_level_adds_its_marked_mineral_sites(ResourceLevel level, int sites)
    {
        Assert.Equal(sites, NewMatch(new MatchOptions { Resources = level }).Sites.Count);
    }

    [Fact]
    public void Tech_level_picks_its_research_file()
    {
        var normal = NewMatch(new MatchOptions()).Races[Race.Red].Researched;
        var high = NewMatch(new MatchOptions { TechLevel = TechLevel.High }).Races[Race.Red].Researched;

        Assert.True(high.Count > normal.Count, $"hightech.rsi gives {high.Count} items, default.rsi {normal.Count}");
    }

    [Fact]
    public void Losing_the_pod_loses_a_pod_game()
    {
        var world = NewMatch(new MatchOptions { Victory = VictoryCondition.Pod });
        foreach (var pod in world.Constructions.Where(c => c.Race == Race.Blue && c.IsType("POD")))
            world.Damage(pod, 100000, Race.Red);

        world.Step();

        Assert.True(world.Races[Race.Blue].Defeated);
        Assert.Equal(Race.Red, world.Winner);
        Assert.Contains(world.Actors, a => a.Race == Race.Blue);
    }

    [Fact]
    public void Destroying_enemies_scores_their_cost_and_costs_the_loser_half()
    {
        var world = NewMatch(new MatchOptions());
        var grunt = world.Machines.First(m => m.Race == Race.Blue && m.IsType("AGGRESSOR"));

        world.Damage(grunt, 100000, Race.Red);
        world.Step();

        Assert.Equal(grunt.Cost, world.Races[Race.Red].Score.Destruction);
        Assert.Equal(-grunt.Cost / 2, world.Races[Race.Blue].Score.Destruction);
    }

    [Fact]
    public void Fixed_starts_give_the_player_the_position_of_their_colour()
    {
        var starts = StartPositions.Assign([Race.Red, Race.Blue, Race.Green, Race.Yellow], players: 3, Race.Green, random: false, new Random(1));

        Assert.Equal(Race.Green, starts.PlayerSlot);
        Assert.Equal([Race.Red, Race.Blue, Race.Green], starts.Players.Keys.Order());
        Assert.All(starts.Colours, kv => Assert.Equal(kv.Key, kv.Value));
    }

    [Fact]
    public void Random_starts_keep_the_player_colour_and_give_every_race_its_own()
    {
        for (var seed = 0; seed < 20; seed++)
        {
            var starts = StartPositions.Assign([Race.Red, Race.Blue], players: 2, Race.Yellow, random: true, new Random(seed));

            Assert.Equal(Race.Yellow, starts.Colours[starts.PlayerSlot]);
            Assert.Equal(2, starts.Colours.Values.Distinct().Count());
            Assert.Equal(Controller.Player, starts.Players[starts.PlayerSlot]);
        }
    }

    [Fact]
    public void A_timed_game_goes_to_the_highest_score()
    {
        var world = NewMatch(new MatchOptions { Victory = VictoryCondition.Timer, TimeLimit = 5 });
        var grunt = world.Machines.First(m => m.Race == Race.Red && m.IsType("AGGRESSOR"));
        world.Damage(grunt, 100000, Race.Blue);

        for (var i = 0; i < 6 * GameWorld.TicksPerSecond && world.Winner is null; i++)
            world.Step();

        Assert.Equal(0, world.TimeLeft);
        Assert.Equal(Race.Blue, world.Winner);
    }
}
