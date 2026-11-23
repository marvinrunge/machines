using System.Numerics;
using Machines.Import.Model;
using Machines.Sim;
using Machines.Sim.Story;
using Machines.Sim.World;
using Machines.Tests.Sim;

namespace Machines.Tests.Story;

/// <summary>
/// Use's one key for many things (plan stufe7 C9; GameWorld.StoryUseTarget): the nearest of the highest kind first, so a
/// downed Grunt beats a nearer switch for the Locator and the shut-down Locator beats a nearer switch for a Grunt.
/// </summary>
public class StoryUseTests
{
    private static readonly Lazy<GameData> DataLoader = new(() => GameData.Load(RepoPaths.OriginalDataFolder()));

    private static readonly PlayerId Locator = new(0), Ike = new(4), Bob = new(5);

    private static readonly Vector2 Switch = new(390, 252);

    private static GameWorld Prologue() =>
        MatchSetup.Create(DataLoader.Value, StoryOptions.Prologue, StoryOptions.Prologue, new PlayerTable([
            new PlayerSeat(Locator, Race.Red, PlayerKind.Human, "Host"),
            new PlayerSeat(Ike, Race.Red, PlayerKind.Human, "Anna"),
            new PlayerSeat(Bob, Race.Red, PlayerKind.Human, "Ben"),
            new PlayerSeat(PlayerId.Of(Race.Blue), Race.Blue, PlayerKind.Ai, "Ship")]), 7,
            new MatchOptions { Story = new StoryOptions { Unscripted = true }, FogOfWar = false });

    private static void Place(GameWorld world, PlayerId seat, Vector2 at)
    {
        var hero = world.HeroOf(seat)!;
        hero.Position = at;
        hero.Path.Clear();
        hero.PathIndex = 0;
    }

    [Fact]
    public void Kinds_of_the_same_rank_go_by_distance_and_the_kinds_by_rank()
    {
        Assert.Equal([StoryUseKind.Revive, StoryUseKind.Plug, StoryUseKind.Mend, StoryUseKind.Mount, StoryUseKind.Strip, StoryUseKind.Device,
            StoryUseKind.Switch], Enum.GetValues<StoryUseKind>().Order());
    }

    [Fact]
    public void A_downed_grunt_comes_before_a_nearer_switch()
    {
        var world = Prologue();
        Place(world, Locator, Switch + new Vector2(0, 1));
        Place(world, Ike, Switch + new Vector2(0, -2.5f));
        Place(world, Bob, Switch + new Vector2(-30, 0));
        world.Step();
        Assert.Equal("switch_g2", world.StoryUseTarget(Locator));
        world.Damage(world.HeroOf(Ike)!, 100000, Race.Blue);
        world.Step();
        Assert.Equal($"seat:{Ike.Value}", world.StoryUseTarget(Locator));
    }

    [Fact]
    public void The_shut_down_locator_comes_before_a_nearer_switch_for_a_grunt()
    {
        var world = Prologue();
        Place(world, Locator, Switch + new Vector2(0, 5));
        Place(world, Ike, Switch + new Vector2(0, 0.5f));
        Place(world, Bob, Switch + new Vector2(-30, 0));
        world.Step();
        Assert.Equal("switch_g2", world.StoryUseTarget(Ike));
        world.Story!.Flags.Add(StoryMarks.Reserve);
        world.Damage(world.HeroOf(Locator)!, 100000, Race.Blue);
        world.Step();
        Assert.True(world.LocatorShutDown);
        Assert.Equal($"seat:{Locator.Value}", world.StoryUseTarget(Ike));
    }
}
