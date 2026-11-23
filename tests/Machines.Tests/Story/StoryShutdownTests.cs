using System.Numerics;
using Machines.Import.Model;
using Machines.Sim;
using Machines.Sim.Story;
using Machines.Sim.World;
using Machines.Tests.Sim;

namespace Machines.Tests.Story;

/// <summary>
/// The Notabschaltung (plan stufe7 C7; GameWorld.StoryReserve): with the director's reserve on, the Locator at 0 hit points
/// shuts down at 1 instead of falling, inert, until a Grunt standing by holds Use on it for 3 s ("Kabel stecken"); it comes
/// back with half its hit points. A hit does not break the cable, the Grunt moving off does. Lost on a second shutdown before
/// the next checkpoint, after 20 s without a cable, and with both Grunts down; the reserve is whole again at a checkpoint.
/// </summary>
public class StoryShutdownTests
{
    private static readonly Lazy<GameData> DataLoader = new(() => GameData.Load(RepoPaths.OriginalDataFolder()));

    private static PlayerId P(int value) => new(value);

    private static readonly PlayerId Locator = P(0), Ike = P(4), Bob = P(5);

    private static GameWorld Prologue(PlayerKind grunts = PlayerKind.Human, int seed = 7) =>
        MatchSetup.Create(DataLoader.Value, StoryOptions.Prologue, StoryOptions.Prologue, new PlayerTable([
            new PlayerSeat(Locator, Race.Red, PlayerKind.Human, "Host"),
            new PlayerSeat(Ike, Race.Red, grunts, "Anna"),
            new PlayerSeat(Bob, Race.Red, grunts, "Ben"),
            new PlayerSeat(PlayerId.Of(Race.Blue), Race.Blue, PlayerKind.Ai, "Ship")]), seed,
            new MatchOptions { Story = new StoryOptions { Unscripted = true }, FogOfWar = false });

    private static void Run(GameWorld world, float seconds)
    {
        for (var i = 0; i < seconds * GameWorld.TicksPerSecond; i++)
            world.Step();
    }

    private static string? Do(GameWorld world, Command command, PlayerId by)
    {
        world.Issue(command with { Issuer = by });
        world.Step();
        return world.Rejected.FirstOrDefault()?.Reason;
    }

    private static Machine Hero(GameWorld world, PlayerId seat) => world.HeroOf(seat)!;

    private static void Place(GameWorld world, PlayerId seat, Vector2 at)
    {
        var hero = Hero(world, seat);
        hero.Position = at;
        hero.Path.Clear();
        hero.PathIndex = 0;
    }

    /// <summary>The trio in the lane of deck A, the Grunts beside the Locator, the reserve on or off; then shot down.</summary>
    private static GameWorld ShotDown(bool reserve, PlayerKind grunts = PlayerKind.Human, int seed = 7)
    {
        var world = Prologue(grunts, seed);
        Place(world, Locator, new Vector2(176, 52));
        Place(world, Ike, new Vector2(170, 54));
        Place(world, Bob, new Vector2(170, 50));
        Run(world, 1);
        if (reserve)
            world.Story!.Flags.Add(StoryMarks.Reserve);
        world.Damage(Hero(world, Locator), 100000, Race.Blue);
        world.Step();
        return world;
    }

    [Fact]
    public void Without_the_reserve_the_locator_falls_as_before()
    {
        var world = ShotDown(reserve: false);
        Run(world, 0.5f);
        Assert.Equal(StoryEnding.LocatorLost, world.Story!.Ending);
        Assert.False(world.LocatorShutDown);
    }

    [Fact]
    public void With_the_reserve_it_shuts_down_inert_and_a_three_second_cable_brings_it_back_at_half()
    {
        var world = ShotDown(reserve: true);
        var run = world.Story!;
        var locator = Hero(world, Locator);
        Assert.Null(run.Ending);
        Assert.True(world.LocatorShutDown);
        Assert.Equal(1, locator.HitPoints);
        Assert.True(world.StoryInert(locator));
        Assert.Contains(StoryMarks.ReserveUsed, run.Flags);
        // Nothing aims at it or harms it, and its own player cannot use anything.
        var foe = world.AddMachine(Race.Blue, locator.Position + new Vector2(8, 0), "AGGRESSOR", "GRUNT", 1, "R_BOLTER");
        Assert.False(world.CanFireAt(foe, locator));
        world.Damage(locator, 500, Race.Blue);
        Assert.Equal(1, locator.HitPoints);
        Assert.Equal("the Locator is shut down", Do(world, new StoryUseCommand(Race.Red), Locator));

        // Ike plugs in from beside it; a hit on Ike does not break the cable.
        Assert.Equal($"seat:{Locator.Value}", world.StoryUseTarget(Ike));
        Assert.Null(Do(world, new StoryUseCommand(Race.Red), Ike));
        Run(world, 1.5f);
        Assert.InRange(world.CableShare!.Value, 0.4f, 0.6f);
        world.Damage(Hero(world, Ike), 1, Race.Blue);
        Run(world, 1.4f);
        Assert.True(world.LocatorShutDown);
        Run(world, 0.2f);
        Assert.False(world.LocatorShutDown);
        Assert.Equal(locator.MaxHitPoints / 2, locator.HitPoints);
        Assert.False(world.StoryInert(locator));
        Assert.Equal(1, run.Reserve.Restarts);
        // A moment of grace: nothing harms it.
        var before = locator.HitPoints;
        world.Damage(locator, 10, Race.Blue);
        Assert.Equal(before, locator.HitPoints);
        Assert.Null(run.Ending);
    }

    [Fact]
    public void The_cable_lets_go_when_its_grunt_moves_more_than_four_metres_away()
    {
        var world = ShotDown(reserve: true);
        Assert.Null(Do(world, new StoryUseCommand(Race.Red), Ike));
        Run(world, 2);
        Place(world, Ike, Hero(world, Locator).Position + new Vector2(-12, 0));
        world.Step();
        Assert.Null(world.CableShare);
        Assert.Equal(0, world.Story!.Reserve.PlugTicks);
        Run(world, 2);
        Assert.True(world.LocatorShutDown);
    }

    [Fact]
    public void A_second_shutdown_before_the_next_checkpoint_is_the_end()
    {
        var world = ShotDown(reserve: true);
        Assert.Null(Do(world, new StoryUseCommand(Race.Red), Bob));
        Run(world, 5.5f);
        Assert.False(world.LocatorShutDown);

        // Spent: the next fall ends the chapter.
        world.Damage(Hero(world, Locator), 100000, Race.Blue);
        Run(world, 0.5f);
        Assert.Equal(StoryEnding.LocatorLost, world.Story!.Ending);
    }

    [Fact]
    public void A_checkpoint_reached_makes_the_reserve_whole_again()
    {
        // From the repair station (11) into the Notgang, which is checkpoint 12, with the reserve spent before it.
        var world = MatchSetup.Create(DataLoader.Value, StoryOptions.Prologue, StoryOptions.Prologue,
            new Dictionary<Race, Controller> { [Race.Red] = Controller.Player, [Race.Blue] = Controller.Ai }, 3,
            new MatchOptions { Story = new StoryOptions { Checkpoint = 11 }, FogOfWar = false });
        var run = world.Story!;
        run.Flags.Add(StoryMarks.Reserve);
        run.Flags.Add(StoryMarks.ReserveUsed);
        var locator = world.HeroOf(run.Locator)!;
        locator.Position = new Vector2(520, 240);
        locator.Path.Clear();
        Run(world, 0.2f);
        Assert.Equal(12, run.Checkpoint);
        Assert.DoesNotContain(StoryMarks.ReserveUsed, run.Flags);
    }

    [Fact]
    public void Lost_after_twenty_seconds_without_a_cable()
    {
        var world = ShotDown(reserve: true);
        Run(world, StoryReserve.Wait - 0.5f);
        Assert.Null(world.Story!.Ending);
        Run(world, 1);
        Assert.Equal(StoryEnding.LocatorLost, world.Story.Ending);
    }

    [Fact]
    public void Lost_when_both_grunts_are_down()
    {
        var world = ShotDown(reserve: true);
        world.Damage(Hero(world, Ike), 100000, Race.Blue);
        world.Step();
        Assert.Null(world.Story!.Ending);
        world.Damage(Hero(world, Bob), 100000, Race.Blue);
        Run(world, 0.2f);
        Assert.Equal(StoryEnding.LocatorLost, world.Story.Ending);
    }

    [Fact]
    public void A_computers_grunt_drives_over_and_plugs_in_by_itself_and_two_peers_agree()
    {
        var sums = new List<List<ulong>>();
        foreach (var _ in new[] { 1, 2 })
        {
            var world = ShotDown(reserve: true, PlayerKind.Bot, seed: 11);
            Place(world, Ike, Hero(world, Locator).Position + new Vector2(-14, 2));
            Place(world, Bob, Hero(world, Locator).Position + new Vector2(-18, -2));
            var sum = new List<ulong>();
            for (var i = 0; i < 10 && world.LocatorShutDown; i++)
            {
                Run(world, 1);
                sum.Add(world.Checksum());
            }
            Assert.False(world.LocatorShutDown);
            Assert.Null(world.Story!.Ending);
            Assert.Equal(1, world.Story.Reserve.Restarts);
            sums.Add(sum);
        }
        Assert.Equal(sums[0], sums[1]);
    }
}
