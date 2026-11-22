using System.Numerics;
using Machines.Import.Model;
using Machines.Net;
using Machines.Sim;
using Machines.Sim.Story;
using Machines.Sim.World;
using Machines.Tests.Sim;

namespace Machines.Tests.Story;

/// <summary>
/// How the story's Grunts move with the Locator (GameWorld.StoryMotion): they flow along behind it in a queue instead of
/// going a metre at a time, and one standing in its way steps aside when it drives into it.
/// </summary>
public class StoryMotionTests
{
    private static readonly Lazy<GameData> DataLoader = new(() => GameData.Load(RepoPaths.OriginalDataFolder()));

    private static readonly PlayerId Locator = new(0), Ike = new(4), Bob = new(5);

    private static GameWorld Prologue() =>
        MatchSetup.Create(DataLoader.Value, StoryOptions.Prologue, StoryOptions.Prologue, new PlayerTable([
            new PlayerSeat(Locator, Race.Red, PlayerKind.Human, "Host"),
            new PlayerSeat(Ike, Race.Red, PlayerKind.Bot, "Ike"),
            new PlayerSeat(Bob, Race.Red, PlayerKind.Bot, "Bob"),
            new PlayerSeat(PlayerId.Of(Race.Blue), Race.Blue, PlayerKind.Ai, "Ship")]), 7,
            new MatchOptions { Story = new StoryOptions { Unscripted = true }, FogOfWar = false });

    private static Machine Hero(GameWorld world, PlayerId seat) => world.HeroOf(seat)!;

    private static void Place(GameWorld world, PlayerId seat, Vector2 at, float facing = 0)
    {
        var hero = Hero(world, seat);
        hero.Position = at;
        hero.Facing = facing;
        hero.Path.Clear();
        hero.PathIndex = 0;
    }

    private static void Drive(GameWorld world, float forward, float turn = 0) =>
        world.Issue(new DriveCommand(Race.Red, Hero(world, Locator).Id, true, forward, turn) { Issuer = Locator });

    [Fact]
    public void The_grunts_roll_on_behind_a_locator_that_drives_on_in_a_queue()
    {
        var world = Prologue();
        Place(world, Locator, new Vector2(140, 52));
        Place(world, Ike, new Vector2(128, 56));
        Place(world, Bob, new Vector2(128, 48));
        for (var i = 0; i < 20; i++)
            world.Step();

        // Half speed east along deck A's lane: once rolling, neither stands for a single tick (they stopped dead behind
        // a Locator driving on, and drove up to where it had been a second and a half ago, stopped and turned).
        var standing = new Dictionary<PlayerId, int> { [Ike] = 0, [Bob] = 0 };
        var last = new Dictionary<PlayerId, Vector2> { [Ike] = Hero(world, Ike).Position, [Bob] = Hero(world, Bob).Position };
        for (var tick = 0; tick < 7 * GameWorld.TicksPerSecond; tick++)
        {
            Drive(world, 0.5f);
            world.Step();
            foreach (var seat in new[] { Ike, Bob })
            {
                var now = Hero(world, seat).Position;
                if (tick >= 3 * GameWorld.TicksPerSecond && Vector2.Distance(now, last[seat]) < 0.05f)
                    standing[seat]++;
                last[seat] = now;
            }
        }

        Assert.Equal(0, standing[Ike]);
        Assert.Equal(0, standing[Bob]);
        // One behind the other, not both after the same spot.
        var (locator, ike, bob) = (Hero(world, Locator).Position, Hero(world, Ike).Position, Hero(world, Bob).Position);
        var (first, second) = Vector2.Distance(ike, locator) < Vector2.Distance(bob, locator) ? (ike, bob) : (bob, ike);
        Assert.InRange(Vector2.Distance(first, locator), 3, 9);
        Assert.InRange(Vector2.Distance(second, first), 3, 9);
        Assert.True(Vector2.Distance(second, locator) > Vector2.Distance(first, locator) + 2);
    }

    [Fact]
    public void A_grunt_in_the_locators_way_steps_aside_when_it_drives_into_it()
    {
        var world = Prologue();
        world.OpenDoor('B');
        // Bob's bay: the Locator at the terminal, Ike told to wait between it and the door to the Locator's bay.
        Place(world, Locator, new Vector2(90, 52), MathF.PI);
        Place(world, Ike, new Vector2(78, 52), MathF.PI);
        Place(world, Bob, new Vector2(88, 61), MathF.PI);
        world.Issue(new StoryOrderCommand(Race.Red, CompanionOrder.Wait, Vector2.Zero) { Issuer = Locator });
        for (var i = 0; i < 20; i++)
            world.Step();

        for (var tick = 0; tick < 16 * GameWorld.TicksPerSecond && Hero(world, Locator).Position.X > 64; tick++)
        {
            Drive(world, 1);
            world.Step();
        }

        Assert.True(Hero(world, Locator).Position.X <= 64, $"the Locator stands at {Hero(world, Locator).Position}, Ike at {Hero(world, Ike).Position}, Bob at {Hero(world, Bob).Position}");
    }

    [Fact]
    public void A_grunt_still_asleep_in_its_clamp_never_steps_aside()
    {
        var world = Prologue();
        world.OpenDoor('B');
        // Bob hangs asleep in its clamp, which the bot plays; the Locator drives straight into it, and Ike, awake and sent
        // past it, runs into it as well.
        world.SetCompanionDormant(Bob, true);
        Place(world, Locator, new Vector2(93, 47), MathF.PI);
        Place(world, Bob, new Vector2(84, 47), MathF.PI / 2);
        Place(world, Ike, new Vector2(75, 47));
        var bob = Hero(world, Bob);
        for (var i = 0; i < 20; i++)
            world.Step();
        var (clamped, facing) = (bob.Position, bob.Facing);

        for (var tick = 0; tick < 6 * GameWorld.TicksPerSecond; tick++)
        {
            Drive(world, 1);
            world.Step();
            Assert.True(bob.Position == clamped && bob.Facing == facing, $"tick {tick}: Bob left its clamp for {bob.Position.X:F3}, {bob.Position.Y:F3} facing {bob.Facing:F3}");
        }
        Assert.True(Hero(world, Locator).Position.X > clamped.X, "the Locator drove through Bob");
        Place(world, Locator, new Vector2(90, 58), MathF.PI);

        // Ike on an order across the bay through where Bob hangs.
        world.Issue(new MoveCommand(Race.Red, [Hero(world, Ike).Id], new Vector2(93, 47)) { Issuer = Ike });
        for (var tick = 0; tick < 10 * GameWorld.TicksPerSecond; tick++)
        {
            world.Step();
            Assert.True(bob.Position == clamped && bob.Facing == facing, $"tick {tick}: Bob left its clamp for {bob.Position.X:F3}, {bob.Position.Y:F3} facing {bob.Facing:F3}");
        }
        Assert.IsNotType<EvadeOrder>(bob.Order);
    }

    [Fact]
    public void A_machine_the_story_holds_still_is_not_asked_to_make_way()
    {
        var world = Prologue();
        world.OpenDoor('B');
        // Bob awake and computer-driven, but held by the director (as all three are while the pod is boarded).
        Place(world, Locator, new Vector2(93, 47), MathF.PI);
        Place(world, Bob, new Vector2(84, 47), MathF.PI / 2);
        world.Issue(new StoryOrderCommand(Race.Red, CompanionOrder.Wait, Vector2.Zero) { Issuer = Locator });
        for (var i = 0; i < 20; i++)
            world.Step();
        var bob = Hero(world, Bob);
        world.Story!.Director.Held.Add(bob.Id);
        var still = bob.Position;

        for (var tick = 0; tick < 6 * GameWorld.TicksPerSecond; tick++)
        {
            Drive(world, 1);
            world.Step();
        }
        Assert.Equal(still, bob.Position);
    }
}
