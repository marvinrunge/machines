using System.Numerics;
using Machines.Import.Model;
using Machines.Sim;
using Machines.Sim.Navigation;
using Machines.Sim.Story;
using Machines.Sim.World;
using Machines.Tests.Sim;
using static Machines.Sim.Story.Then;
using static Machines.Sim.Story.When;

namespace Machines.Tests.Story;

/// <summary>
/// Stage 7's shared director pieces (plan stufe7 §4 F item 5, C12, C13; the plan's "StoryGateTests", named apart from the
/// gate audit): CloseDoors waits for a free frame (and the trio in its zone) and blocks the ground anew, BlowDoor is final,
/// LockDoors keeps a door from opening by itself; named timers, the pace table, posts and CastHurt; and Easy's longer
/// countdown. None of it changes a run that does not use it.
/// </summary>
public class StoryDoorGateTests
{
    private static readonly Lazy<GameData> DataLoader = new(() => GameData.Load(RepoPaths.OriginalDataFolder()));

    private static readonly PlayerId Locator = new(0), Ike = new(4), Bob = new(5);

    private static GameWorld Unscripted(StoryDifficulty difficulty = StoryDifficulty.Normal) =>
        MatchSetup.Create(DataLoader.Value, StoryOptions.Prologue, StoryOptions.Prologue, new PlayerTable([
            new PlayerSeat(Locator, Race.Red, PlayerKind.Human, "Host"),
            new PlayerSeat(Ike, Race.Red, PlayerKind.Human, "Anna"),
            new PlayerSeat(Bob, Race.Red, PlayerKind.Human, "Ben"),
            new PlayerSeat(PlayerId.Of(Race.Blue), Race.Blue, PlayerKind.Ai, "Ship")]), 7,
            new MatchOptions { Story = new StoryOptions { Unscripted = true, Difficulty = difficulty }, FogOfWar = false });

    private static void Run(GameWorld world, float seconds)
    {
        for (var i = 0; i < seconds * GameWorld.TicksPerSecond; i++)
            world.Step();
    }

    private static void Place(GameWorld world, PlayerId seat, Vector2 at)
    {
        var hero = world.HeroOf(seat)!;
        hero.Position = at;
        hero.Path.Clear();
        hero.PathIndex = 0;
    }

    private static void Act(GameWorld world, StoryAction action) => world.Story!.Director.Act(world, world.Story, action);

    private static bool Holds(GameWorld world, StoryTrigger trigger) => world.Story!.Director.Holds(world, world.Story, trigger);

    /// <summary>A door of the deck that stands closed at the chapter's start (a Grunts' door), its letter and its middle.</summary>
    private static (char Letter, Vector2 Middle) ADoor(GameWorld world)
    {
        var door = world.Deck!.Doors.First(d => d.Kind == "door" && world.DoorClosed(d.Letter));
        var leaves = world.DoorLeaves(door.Letter);
        return (door.Letter, leaves.Aggregate(Vector2.Zero, (sum, leaf) => sum + leaf.Position) / leaves.Count);
    }

    /// <summary>The three heroes taken well away from a door (where the Locator spawned is far from most).</summary>
    private static void Away(GameWorld world, Vector2 middle)
    {
        var far = world.Deck!.Markers.Select(m => m.Position).Where(p => world.Nav.IsFree(p, Mobility.Land))
            .OrderByDescending(p => Vector2.DistanceSquared(p, middle)).First();
        Place(world, Locator, far);
        Place(world, Ike, far + new Vector2(0, 3));
        Place(world, Bob, far + new Vector2(0, -3));
    }

    [Fact]
    public void A_door_shuts_only_once_its_frame_is_free_and_blocks_the_ground_again()
    {
        var world = Unscripted();
        var (letter, middle) = ADoor(world);
        Assert.False(world.Nav.IsFree(middle, Mobility.Land));
        Assert.True(world.OpenDoor(letter, instant: true));
        Assert.True(world.Nav.IsFree(middle, Mobility.Land));
        Away(world, middle);
        Place(world, Ike, middle);

        Act(world, new CloseDoors(letter.ToString()));
        Run(world, 1);

        // Ike stands in the frame: the door waits.
        Assert.False(world.DoorClosed(letter));
        Assert.Contains(letter, world.Story!.Director.Closing.Keys);

        Place(world, Ike, middle + new Vector2(20, 20) is var off && world.Nav.IsFree(off, Mobility.Land) ? off : world.HeroOf(Locator)!.Position + new Vector2(0, 5));
        Run(world, 0.5f);

        Assert.True(world.DoorClosed(letter));
        Assert.Empty(world.Story.Director.Closing);
        Assert.False(world.Nav.IsFree(middle, Mobility.Land));
        // Shut, it can open again as any door.
        Assert.True(world.OpenDoor(letter, instant: true));
        Assert.True(world.Nav.IsFree(middle, Mobility.Land));
    }

    [Fact]
    public void A_door_shut_when_all_are_in_waits_for_the_whole_trio_in_the_zone()
    {
        var world = Unscripted();
        var (letter, middle) = ADoor(world);
        world.OpenDoor(letter, instant: true);
        Away(world, middle);
        var room = world.Deck!.Zones.First(z => z.Contains(world.HeroOf(Locator)!.Position));
        Place(world, Bob, middle + new Vector2(3, 3));
        if (room.Contains(world.HeroOf(Bob)!.Position))
            Place(world, Bob, middle - new Vector2(3, 3));

        Act(world, new CloseDoors(letter.ToString(), room.Name));
        Run(world, 1);
        Assert.False(world.DoorClosed(letter));

        Place(world, Bob, world.HeroOf(Locator)!.Position + new Vector2(2, 0));
        Run(world, 0.5f);
        Assert.True(world.DoorClosed(letter));
    }

    [Fact]
    public void A_blown_door_is_gone_for_good()
    {
        var world = Unscripted();
        var (letter, middle) = ADoor(world);
        Away(world, middle);

        Act(world, new BlowDoor(letter));
        world.Step();

        Assert.False(world.DoorClosed(letter));
        Assert.True(world.Nav.IsFree(middle, Mobility.Land));
        Assert.Contains(letter, world.Story!.Director.Blown);
        Assert.False(world.CloseDoor(letter));
        Act(world, new CloseDoors(letter.ToString()));
        Act(world, new LockDoors(letter.ToString()));
        Run(world, 1);
        Assert.False(world.DoorClosed(letter));
        Assert.Empty(world.Story.Director.Closing);
        Assert.Empty(world.Story.Director.Locked);
        Assert.False(world.OpenDoor(letter));
    }

    [Fact]
    public void A_locked_door_does_not_open_by_itself_until_it_is_unlocked()
    {
        var world = Unscripted();
        var (letter, middle) = ADoor(world);
        Away(world, middle);
        var director = world.Story!.Director;
        director.AutoDoors.Add(letter);
        Act(world, new LockDoors(letter.ToString()));
        Place(world, Locator, middle + Vector2.Normalize(world.HeroOf(Ike)!.Position - middle) * 4);

        Run(world, 2);
        Assert.True(world.DoorClosed(letter));

        Act(world, new UnlockDoors(letter.ToString()));
        Run(world, 0.5f);
        Assert.False(world.DoorClosed(letter));
    }

    [Fact]
    public void A_named_timer_runs_out_and_can_be_stopped()
    {
        var world = Unscripted();
        Act(world, new SetTimer("batch", 2));
        Assert.True(Holds(world, new TimerAt("batch", 2)));
        Assert.False(Holds(world, new TimerAt("batch", 0)));

        Run(world, 2.1f);
        Assert.True(Holds(world, new TimerAt("batch", 0)));

        Act(world, new SetTimer("batch", null));
        Assert.False(Holds(world, new TimerAt("batch", 0)));
    }

    [Fact]
    public void A_paced_role_goes_at_its_share_and_a_cast_role_is_hurt_by_its_share()
    {
        var world = Unscripted();
        var locator = world.HeroOf(Locator)!;
        var grunt = world.AddMachine(Race.Blue, locator.Position + new Vector2(6, 0), "AGGRESSOR", "GRUNT", 1, "R_BOLTER");
        world.Story!.Director.Cast["wave"] = [grunt.Id];
        var before = world.StoryPace(grunt);

        Act(world, new Pace("wave", 0.6f));
        Assert.Equal(0.6f, world.StoryPace(grunt));
        Act(world, new Pace("wave", null));
        Assert.Equal(before, world.StoryPace(grunt));
        Assert.Empty(world.Story.Drives.Paces);

        Assert.False(Holds(world, new CastHurt("wave", 0.5f)));
        grunt.HitPoints = (int)(grunt.MaxHitPoints * 0.45f);
        Assert.True(Holds(world, new CastHurt("wave", 0.5f)));
        Assert.False(Holds(world, new CastHurt("nobody", 0.5f)));
    }

    [Fact]
    public void A_role_holding_a_post_goes_back_to_it_when_idle()
    {
        var world = Unscripted();
        var post = world.HeroOf(Bob)!.Position;
        Place(world, Bob, world.HeroOf(Locator)!.Position + new Vector2(0, 3));
        var guard = world.AddMachine(Race.Blue, post + new Vector2(0, -6), "AGGRESSOR", "GRUNT", 1, "R_BOLTER");
        guard.Weapons.Clear();
        world.Story!.Director.Cast["guard"] = [guard.Id];
        var place = FormattableString.Invariant($"{post.X},{post.Y}");

        Act(world, new HoldAt("guard", place));
        Run(world, 15);
        Assert.True(Vector2.Distance(guard.Position, post) <= StoryDirector.PostReach + 0.5f, $"the guard stands {Vector2.Distance(guard.Position, post)} m off its post");
        // Sent off and left idle there, it comes back by itself.
        var off = post + new Vector2(0, -6);
        Act(world, new MoveTo("guard", FormattableString.Invariant($"{off.X},{off.Y}")));
        Run(world, 25);
        Assert.True(Vector2.Distance(guard.Position, post) <= StoryDirector.PostReach + 0.5f, $"the guard stands {Vector2.Distance(guard.Position, post)} m off its post");

        Act(world, new Release("guard"));
        Assert.Empty(world.Story.Director.Posts);
    }

    [Fact]
    public void Easy_gives_the_countdown_a_minute_more_at_each_start()
    {
        var normal = Unscripted();
        var easy = Unscripted(StoryDifficulty.Easy);
        Act(normal, new Countdown(240));
        Act(easy, new Countdown(240));

        Assert.Equal(240, normal.Story!.Timers[StoryMarks.Countdown] - normal.Time, 2);
        Assert.Equal(300, easy.Story!.Timers[StoryMarks.Countdown] - easy.Time, 2);
    }

    [Fact]
    public void Doors_shut_and_blown_keep_two_worlds_in_step()
    {
        var (a, b) = (Unscripted(), Unscripted());
        foreach (var world in new[] { a, b })
        {
            var (letter, middle) = ADoor(world);
            Away(world, middle);
            world.OpenDoor(letter);
            Act(world, new CloseDoors(letter.ToString()));
            Act(world, new SetTimer("batch", 3));
            Act(world, new LockDoors("Q"));
            Run(world, 3);
            Act(world, new BlowDoor(letter));
            Run(world, 1);
        }
        Assert.Equal(a.Checksum(), b.Checksum());
    }
}
