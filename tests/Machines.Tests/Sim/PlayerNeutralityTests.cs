using Machines.Import.Model;
using Machines.Net;
using Machines.Sim;
using Machines.Sim.World;

namespace Machines.Tests.Sim;

/// <summary>
/// Players apart from races change nothing for a match with one player per race (P1 design §7.4, tests T4 and T5): each
/// golden match (<see cref="GoldenCase"/>) built once through its races, as <see cref="Replay.BuildWorld"/> built it before
/// players were told apart from races, and once through the table it records (<see cref="MatchInfo.Table"/>), as it builds
/// it now, plays to the same checksum every hundred ticks, and at every golden tick no actor belongs to anyone but its race's
/// first player and no race is shared.
/// </summary>
public class PlayerNeutralityTests
{
    private static readonly Lazy<GameData> DataLoader = new(() => GameData.Load(RepoPaths.OriginalDataFolder()));

    private static GameData Data => DataLoader.Value;

    public static TheoryData<string> Cases => [.. GoldenCase.All.Select(c => c.Name)];

    /// <summary>The world of a match built from the table it records, in its rows' order.</summary>
    private static GameWorld FromTable(MatchInfo match) =>
        MatchSetup.Create(Data, match.Scenario, match.Planet, match.Table(), match.Seed, match.Options);

    /// <summary>
    /// The world of a match built through its races, a controller for each row's slot in the rows' order: the road
    /// <see cref="Replay.BuildWorld"/> took before it built from the table (it would throw on two rows of one race).
    /// </summary>
    private static GameWorld ByRace(MatchInfo match) =>
        MatchSetup.Create(Data, match.Scenario, match.Planet, match.Players.ToDictionary(p => p.Slot, p => p.Ai ? Controller.Ai : Controller.Player),
            match.Seed, match.Options);

    [Theory]
    [MemberData(nameof(Cases))]
    public void Two_roads_build_one_world(string name)
    {
        var golden = GoldenCase.All.Single(c => c.Name == name);
        var byRace = new GoldenMatch(golden, ByRace(golden.Match));
        var byTable = new GoldenMatch(golden, Replay.BuildWorld(Data, golden.Match));

        Assert.Equal(byRace.World.Races.Keys, byTable.World.Races.Keys);
        Assert.Equal(byRace.World.Actors.Select(a => (a.Id, a.Race)), byTable.World.Actors.Select(a => (a.Id, a.Race)));
        Assert.Equal(byRace.World.Players.Rows.Select(r => (r.Id, r.Race, r.Kind)), byTable.World.Players.Rows.Select(r => (r.Id, r.Race, r.Kind)));

        var last = golden.Ticks.Max();
        foreach (var tick in Enumerable.Range(1, last / 100).Select(i => i * 100).Union(golden.Ticks).Order())
        {
            Assert.Equal(byRace.PlayTo(tick), byTable.PlayTo(tick));
            if (!golden.Ticks.Contains(tick))
                continue;
            foreach (var world in new[] { byRace.World, byTable.World })
            {
                Assert.False(world.Players.AnyShared, $"{name}: a race is shared at tick {tick}");
                Assert.DoesNotContain(world.Actors, a => a.OwnerOverride is not null);
                Assert.Empty(world.Players.Keepers);
            }
        }
    }

    [Fact]
    public void A_battle_completes_its_table_from_its_seats()
    {
        var golden = GoldenCase.Battle.Match;
        // Only the seated humans: the computer's seats are filled in as bots, as the sim composes their heroes.
        var humans = golden with { Players = [.. golden.Players.Where(p => !p.Ai)] };

        var world = FromTable(humans);
        var full = Replay.BuildWorld(Data, golden);

        Assert.Equal([Race.Red, Race.Green, Race.Blue, Race.Yellow], world.Players.Rows.Select(r => r.Race));
        Assert.Equal([PlayerKind.Human, PlayerKind.Bot, PlayerKind.Human, PlayerKind.Bot], world.Players.ById.Select(r => r.Kind));
        Assert.Equal(world.Players.ById.Select(r => r.Name).Where((_, i) => i is 1 or 3),
            world.Options.Battle!.Heroes.Where(h => h.Race is Race.Blue or Race.Yellow).Select(h => h.Name));
        for (var tick = 0; tick < 400; tick++)
        {
            world.Step();
            full.Step();
        }
        Assert.Equal(full.Checksum(), world.Checksum());
    }
}
