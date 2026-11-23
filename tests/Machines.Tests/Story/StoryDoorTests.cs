using System.Numerics;
using Machines.Import.Model;
using Machines.Net;
using Machines.Sim;
using Machines.Sim.Story;
using Machines.Sim.World;
using Machines.Tests.Sim;

namespace Machines.Tests.Story;

/// <summary>
/// The deck's doors (GameWorld.Deck): a door slides open over its time (a Grunts' door 1.2 s, a blast door or gate slower)
/// and nobody passes a leaf that has not slid far enough, a computer's machine nor one driven in first person; a checkpoint's
/// world has every door opened before it open from its first tick, none sliding.
/// </summary>
public class StoryDoorTests
{
    private static readonly Lazy<GameData> DataLoader = new(() => GameData.Load(RepoPaths.OriginalDataFolder()));

    private static readonly PlayerId Locator = new(0), Ike = new(4), Bob = new(5);

    private static GameWorld Chapter(int checkpoint) =>
        MatchSetup.Create(DataLoader.Value, StoryOptions.Prologue, StoryOptions.Prologue,
            new Dictionary<Race, Controller> { [Race.Red] = Controller.Player, [Race.Blue] = Controller.Ai }, 3,
            new MatchOptions { Story = new StoryOptions { Checkpoint = checkpoint }, FogOfWar = false });

    private static GameWorld Unscripted() =>
        MatchSetup.Create(DataLoader.Value, StoryOptions.Prologue, StoryOptions.Prologue, new PlayerTable([
            new PlayerSeat(Locator, Race.Red, PlayerKind.Human, "Host"),
            new PlayerSeat(Ike, Race.Red, PlayerKind.Bot, "Ike"),
            new PlayerSeat(Bob, Race.Red, PlayerKind.Bot, "Bob"),
            new PlayerSeat(PlayerId.Of(Race.Blue), Race.Blue, PlayerKind.Ai, "Ship")]), 7,
            new MatchOptions { Story = new StoryOptions { Unscripted = true }, FogOfWar = false });

    private static void Place(GameWorld world, PlayerId seat, Vector2 at, float facing = 0)
    {
        var hero = world.HeroOf(seat)!;
        hero.Position = at;
        hero.Facing = facing;
        hero.Path.Clear();
        hero.PathIndex = 0;
    }

    /// <summary>Where each door's leaves stand closed (their middle), from a world at the chapter's start.</summary>
    private static readonly Lazy<Dictionary<char, Vector2>> Middles = new(() =>
    {
        var world = Unscripted();
        return world.Deck!.Doors.Where(d => world.DoorClosed(d.Letter)).ToDictionary(d => d.Letter,
            d => world.DoorLeaves(d.Letter).Aggregate(Vector2.Zero, (sum, leaf) => sum + leaf.Position) / world.DoorLeaves(d.Letter).Count);
    });

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(14)]
    public void A_checkpoint_has_every_door_opened_before_it_open_from_its_first_tick(int checkpoint)
    {
        var world = Chapter(checkpoint);

        // Open, none of them sliding: a door the trio had opened is not shut again, and does not open anew.
        Assert.Empty(world.DoorSwings);
        foreach (var letter in Prologue.DoorsOpen[checkpoint])
        {
            Assert.False(world.DoorClosed(letter), $"door {letter}");
            Assert.True(world.Nav.IsFree(Middles.Value[letter], GameWorld.MobilityOf(world.HeroOf(world.Story!.Locator)!)), $"door {letter}'s middle is not free ground");
        }
        // On its first tick only a door that opens by itself as the trio comes may begin to slide (door I beside checkpoint 2's start).
        world.Step();
        Assert.All(world.DoorSwings, s => Assert.Contains(s.Letter, Prologue.DoorsAuto[checkpoint]));
        Assert.All(world.DoorSwings, s => Assert.DoesNotContain(s.Letter, Prologue.DoorsOpen[checkpoint]));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(14)]
    public void Every_door_the_script_opens_before_a_checkpoint_stands_open_or_opens_by_itself_there(int checkpoint)
    {
        // The doors the beats of the stretches before it open (hacked, unlocked, opened by the story), and those that opened by
        // themselves as the trio came: each open at the checkpoint, or one that still opens as the trio comes. The gates the
        // hunters force (Then.ForceGates) are a lost fight, not progress: the Grunts hold them again at checkpoint 4.
        var before = Prologue.Script.Where(b => b.Span < Prologue.SetUpAs(checkpoint)).SelectMany(b => b.Then).ToList();
        var opened = before.OfType<Then.OpenDoors>().SelectMany(o => o.Letters).ToHashSet();
        // A door that opened by itself and was locked after (the armoury's lockdown, stage 7) counts again only once unlocked.
        var auto = new HashSet<char>();
        foreach (var action in before)
        {
            if (action is Then.AutoDoors a)
                auto.UnionWith(a.Letters);
            else if (action is Then.LockDoors l)
                auto.ExceptWith(l.Letters);
        }

        Assert.All(opened, letter => Assert.Contains(letter, Prologue.DoorsOpen[checkpoint]));
        Assert.All(auto, letter => Assert.True(Prologue.DoorsOpen[checkpoint].Contains(letter) || Prologue.DoorsAuto[checkpoint].Contains(letter),
            $"door {letter} opened by itself before checkpoint {checkpoint}"));
    }

    [Theory]
    [InlineData('B', 1.2f)]
    [InlineData('H', 2.2f)]
    [InlineData('G', 2f)]
    [InlineData('V', 0.9f)]
    public void A_door_slides_open_over_its_time(char letter, float seconds)
    {
        var world = Unscripted();
        var leaves = world.DoorLeaves(letter);
        Assert.NotEmpty(leaves);
        var start = world.Time;

        Assert.True(world.OpenDoor(letter));

        // Open for every plan at once (the director sees it open), its leaves sliding into the walls till the time is up.
        Assert.False(world.DoorClosed(letter));
        Assert.Equal(leaves.Select(l => l.Id), world.DoorSwings.Select(s => s.Leaf));
        Assert.All(world.DoorSwings, s => Assert.Equal((start, start + seconds), (s.From, s.Until)));
        // Each leaf slides away from the door's middle (a vent's one cover along its side), by its whole width.
        var middle = leaves.Aggregate(Vector2.Zero, (sum, leaf) => sum + leaf.Position) / leaves.Count;
        foreach (var swing in world.DoorSwings)
        {
            var leaf = leaves.Single(l => l.Id == swing.Leaf);
            if (leaves.Count > 1)
                Assert.True(Vector2.Dot(swing.Slide, leaf.Position - middle) > 0, $"leaf {leaf.Id} slides inward");
            Assert.InRange(swing.Slide.Length(), 3.9f, 8.1f);
            Assert.Equal(0, swing.Share(start));
            Assert.Equal(0.5f, swing.Share(start + seconds / 2), 3);
            Assert.Equal(1, swing.Share(start + seconds));
        }
        var ticks = (int)MathF.Round(seconds * GameWorld.TicksPerSecond);
        for (var i = 0; i < ticks; i++)
            world.Step();
        Assert.NotEmpty(world.DoorSwings);
        world.Step();
        Assert.Empty(world.DoorSwings);
        Assert.False(world.OpenDoor(letter));
    }

    /// <summary>The tick on which a machine first stands beyond the door B's middle (x 70, Bob's bay), or null.</summary>
    private static int? Through(GameWorld world, Machine machine, int ticks, Action? each = null)
    {
        for (var tick = 0; tick < ticks; tick++)
        {
            each?.Invoke();
            world.Step();
            // Never in a leaf where it stands now.
            Assert.All(world.DoorSwings, s => Assert.False(s.Blocks(machine.Position, machine.Radius, world.Time),
                $"tick {tick}: {machine.Position} in leaf {s.Leaf} at share {s.Share(world.Time):F2}"));
            if (machine.Position.X > 70)
                return tick;
        }
        return null;
    }

    [Fact]
    public void A_machine_on_an_order_through_a_door_waits_for_its_leaves()
    {
        int? Run(bool instant)
        {
            var world = Unscripted();
            // A Grunt of the trio's side just before door B in the Locator's bay, sent through into Bob's.
            Place(world, Bob, new Vector2(90, 58));
            var grunt = world.AddMachine(Race.Red, new Vector2(64, 52), "AGGRESSOR", "GRUNT", 1, null);
            // Its nose a hair before the leaves (they are 2 m deep across x 70).
            grunt.Position = new Vector2(69 - grunt.Radius - 0.05f, 52);
            world.Step();
            world.OpenDoor('B', instant);
            world.Issue(new MoveCommand(Race.Red, [grunt.Id], new Vector2(78, 52)) { Issuer = Locator });
            return Through(world, grunt, 10 * GameWorld.TicksPerSecond);
        }

        var open = Run(instant: true);
        var sliding = Run(instant: false);
        Assert.NotNull(open);
        Assert.NotNull(sliding);
        // It got through, but only once the leaves had made way for it.
        Assert.True(sliding > open, $"through at tick {sliding} with the leaves sliding, {open} through an open door");
    }

    [Fact]
    public void The_locator_driven_into_a_door_opening_waits_for_its_leaves()
    {
        int? Run(bool instant)
        {
            var world = Unscripted();
            Place(world, Locator, new Vector2(66, 52));
            Place(world, Ike, new Vector2(54, 47));
            Place(world, Bob, new Vector2(90, 58));
            world.Issue(new StoryOrderCommand(Race.Red, CompanionOrder.Wait, Vector2.Zero) { Issuer = Locator });
            // At a sprint: at the trio's walk (StoryDrive.Walk) the Locator would come to the door only once its leaves are apart.
            world.Story!.Flags.Add(StoryMarks.SprintFree);
            world.Step();
            world.OpenDoor('B', instant);
            var hero = world.HeroOf(Locator)!;
            return Through(world, hero, 10 * GameWorld.TicksPerSecond,
                () => world.Issue(new DriveCommand(Race.Red, hero.Id, true, 1, 0, Sprint: true) { Issuer = Locator }));
        }

        var open = Run(instant: true);
        var sliding = Run(instant: false);
        Assert.NotNull(open);
        Assert.NotNull(sliding);
        Assert.True(sliding > open, $"through at tick {sliding} with the leaves sliding, {open} through an open door");
    }

    [Fact]
    public void A_door_sliding_open_keeps_the_worlds_in_step()
    {
        ulong Sum()
        {
            var world = Unscripted();
            var grunt = world.AddMachine(Race.Red, new Vector2(64, 52), "AGGRESSOR", "GRUNT", 1, null);
            world.OpenDoor('B');
            world.Issue(new MoveCommand(Race.Red, [grunt.Id], new Vector2(78, 52)) { Issuer = Locator });
            for (var i = 0; i < 10; i++)
                world.Step();
            return world.Checksum();
        }

        Assert.Equal(Sum(), Sum());
    }
}
