using System.Numerics;
using Machines.Import.Model;
using Machines.Net;
using Machines.Sim;
using Machines.Sim.Story;
using Machines.Sim.World;

namespace Machines.Tests.Story;

/// <summary>
/// In a story the machines stand and are hit in bodies fitted to the models the story draws (Story/StoryBodies,
/// GameWorld.StoryBodies), not in the original's circle and the square box round it: the Locator drives up to a Grunt's
/// nose and stops at its tail as the models meet, a bolt over a Grunt's back or short of its nose goes past, and the size
/// classes the doors let through are the circle's as before.
/// </summary>
public class StoryBodyTests
{
    private static readonly Lazy<GameData> DataLoader = new(() => GameData.Load(RepoPaths.OriginalDataFolder()));

    private static readonly PlayerId Locator = new(0), Ike = new(4), Bob = new(5);

    // Ike and Bob at seats of people who do nothing, so that they stand where they are put.
    private static GameWorld Prologue() =>
        MatchSetup.Create(DataLoader.Value, StoryOptions.Prologue, StoryOptions.Prologue, new PlayerTable([
            new PlayerSeat(Locator, Race.Red, PlayerKind.Human, "Host"),
            new PlayerSeat(Ike, Race.Red, PlayerKind.Human, "Anna"),
            new PlayerSeat(Bob, Race.Red, PlayerKind.Human, "Ben"),
            new PlayerSeat(PlayerId.Of(Race.Blue), Race.Blue, PlayerKind.Ai, "Ship")]), 7,
            new MatchOptions { Story = new StoryOptions { Unscripted = true }, FogOfWar = false });

    private static Machine Hero(GameWorld world, PlayerId seat) => world.HeroOf(seat)!;

    private static void Place(Machine machine, Vector2 at, float facing)
    {
        machine.Position = at;
        machine.Facing = facing;
        machine.Path.Clear();
        machine.PathIndex = 0;
    }

    /// <summary>Drives the Locator straight ahead until it stops; how far its middle then is from <paramref name="other"/>'s.</summary>
    private static float DriveInto(GameWorld world, Machine other)
    {
        var locator = Hero(world, Locator);
        var last = locator.Position;
        for (var i = 0; i < 20 * 30; i++)
        {
            world.Issue(new DriveCommand(Race.Red, locator.Id, Driving: true, Forward: 1) { Issuer = Locator });
            world.Step();
            if (i > 20 && Vector2.Distance(last, locator.Position) < 1e-4f)
                break;
            last = locator.Position;
        }
        return Vector2.Distance(locator.Position, other.Position);
    }

    [Fact]
    public void The_locator_drives_up_to_a_grunts_nose_and_stops_at_its_tail_where_the_models_meet()
    {
        var (l7, grunt) = (StoryBodies.Of("GEO_LOCATOR", null, 1)!.Value, StoryBodies.Of("AGGRESSOR", "GRUNT", 1)!.Value);

        // Ike facing the Locator: nose to nose, closer than the circles' 3 m (1.5 + 1.5).
        var world = Prologue();
        var (locator, ike) = (Hero(world, Locator), Hero(world, Ike));
        Place(Hero(world, Bob), new Vector2(176, 40), 0);
        Place(ike, new Vector2(186, 52), MathF.PI);
        Place(locator, new Vector2(176, 52), 0);
        Assert.InRange(DriveInto(world, ike), l7.Fore + grunt.Fore, l7.Fore + grunt.Fore + 0.3f);
        Assert.True(Vector2.Distance(locator.Position, ike.Position) < 2.9f);

        // Ike turned away: the Locator stops at the Grunt's tail, which sticks out of its circle at the back.
        world = Prologue();
        (locator, ike) = (Hero(world, Locator), Hero(world, Ike));
        Place(Hero(world, Bob), new Vector2(176, 40), 0);
        Place(ike, new Vector2(186, 52), 0);
        Place(locator, new Vector2(176, 52), 0);
        Assert.InRange(DriveInto(world, ike), l7.Fore + grunt.Aft, l7.Fore + grunt.Aft + 0.3f);
    }

    [Fact]
    public void The_size_classes_the_doors_let_through_go_by_the_circles_as_before()
    {
        var world = Prologue();
        Assert.Equal(1, GameWorld.MobilityOf(Hero(world, Locator)).Size);
        Assert.Equal(1, GameWorld.MobilityOf(Hero(world, Ike)).Size);
        var reaper = world.AddMachine(Race.Blue, new Vector2(200, 52), "AGGRESSOR", "ASSASSIN", 3, "LR_PLASMA_RIFLE_X2");
        Assert.Equal(2, GameWorld.MobilityOf(reaper).Size);
        Assert.Equal(1.5f, Hero(world, Ike).Radius);
    }

    [Fact]
    public void Every_machine_of_the_prologues_cast_has_a_body_and_its_middle_stands_in_it()
    {
        foreach (var unit in new[] { Machines.Sim.Story.Prologue.Thresher, Machines.Sim.Story.Prologue.Reaper2, Machines.Sim.Story.Prologue.Reaper3, Machines.Sim.Story.Prologue.Commander, Machines.Sim.Story.Prologue.HostileGrunt, Machines.Sim.Story.Prologue.Grunt2, Machines.Sim.Story.Prologue.Grunt3 })
        {
            var body = StoryBodies.Of(unit.Type, unit.SubType, unit.Level);
            Assert.True(body is { Fore: > 0, Aft: > 0, Half: > 0, Height: > 1 }, unit.ToString());
        }
        Assert.NotNull(StoryBodies.Of("GEO_LOCATOR", null, 1));
    }

    /// <summary>The Locator armed with the Bolter from its rack, standing at <paramref name="at"/> facing east, and a hostile Grunt 8 m ahead facing it.</summary>
    private static (GameWorld World, Machine Locator, Machine Foe) Range()
    {
        var world = Prologue();
        var locator = Hero(world, Locator);
        var rack = world.Deck!.Marker("pickup_bolter").Position;
        Place(locator, rack + new Vector2(-1.5f, -1.5f), 0);
        Place(Hero(world, Ike), new Vector2(176, 40), 0);
        Place(Hero(world, Bob), new Vector2(180, 40), 0);
        world.Step();
        world.Issue(new StoryUseCommand(Race.Red, "pickup_bolter") { Issuer = Locator });
        world.Step();
        Assert.NotEmpty(locator.Weapons);
        // In the lane of maintenance deck A, clear of walls.
        Place(locator, new Vector2(176, 52), 0);
        var foe = world.AddMachine(Race.Blue, new Vector2(184, 52), "AGGRESSOR", "GRUNT", 1, "R_BOLTER");
        Place(foe, foe.Position, MathF.PI);
        return (world, locator, foe);
    }

    /// <summary>The Bolter's hits on the foe of rounds sent to a point, everyone kept where and as whole as they were.</summary>
    private static int HitsOn(GameWorld world, Machine locator, Machine foe, Vector2 point, float height, int rounds)
    {
        var (fired, hits) = (0, 0);
        var (at, facing) = (foe.Position, foe.Facing);
        for (var i = 0; i < 20 * 600 && fired < rounds; i++)
        {
            world.Issue(new DriveCommand(Race.Red, locator.Id, Driving: true, Aim: point, Weapons: 1, AimHeight: height) { Issuer = Locator });
            Place(foe, at, facing);
            foe.Order = new StandGroundOrder();
            (foe.HitPoints, foe.Armour, locator.HitPoints) = (foe.MaxHitPoints, foe.MaxArmour, locator.MaxHitPoints);
            world.Step();
            fired += world.Events.Count(e => e.Kind == GameEventKind.Fired && e.Actor == locator.Id);
            hits += world.Events.Count(e => e.Kind == GameEventKind.Hit && e.Actor == foe.Id && e.Other == locator.Id);
        }
        Assert.Equal(rounds, fired);
        return hits;
    }

    [Fact]
    public void A_bolt_on_a_grunt_hits_and_one_over_its_back_or_short_of_its_nose_goes_past()
    {
        var grunt = StoryBodies.Of("AGGRESSOR", "GRUNT", 1)!.Value;
        var (world, locator, foe) = Range();
        Assert.True(HitsOn(world, locator, foe, foe.Position, 1, 6) >= 5);

        // Over its back: inside the original's reach (its box of 2.4 m and a metre's burst), 1.5 m over the model.
        (world, locator, foe) = Range();
        Assert.Equal(0, HitsOn(world, locator, foe, foe.Position, grunt.Height + 1.5f, 6));

        // Short of its nose: the original's box reached 1.5 m ahead of its middle and the burst a metre further.
        (world, locator, foe) = Range();
        Assert.Equal(0, HitsOn(world, locator, foe, foe.Position - new Vector2(grunt.Fore + 1.2f, 0), 1, 6));
    }

    [Fact]
    public void A_sight_on_a_reapers_head_is_kept_where_it_is_and_one_beside_a_grunt_taken_into_its_body()
    {
        var world = Prologue();
        var reaper = world.AddMachine(Race.Blue, new Vector2(200, 52), "AGGRESSOR", "ASSASSIN", 2, "L_PLASMA_RIFLE");
        reaper.Facing = 0;
        // The head stands 5 m up, over the original's box of 2.9 m.
        Assert.Equal(5f, world.AimInside(reaper, reaper.Position, 5).Height);
        var grunt = world.AddMachine(Race.Blue, new Vector2(200, 70), "AGGRESSOR", "GRUNT", 1, "R_BOLTER");
        grunt.Facing = 0;
        var body = StoryBodies.Of("AGGRESSOR", "GRUNT", 1)!.Value;
        var (point, _) = world.AimInside(grunt, grunt.Position + new Vector2(3, 0), 1);
        Assert.Equal(grunt.Position.X + body.Fore, point.X, 3);
    }
}
