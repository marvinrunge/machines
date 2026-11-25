using System.Numerics;
using System.Text;
using Machines.Import.Model;
using Machines.Net;
using Machines.Sim;
using Machines.Sim.Story;
using Machines.Sim.World;
using Xunit.Abstractions;

namespace Machines.Tests.Story;

/// <summary>
/// The trio's walk and sprint (Story/StoryDrive): slower than their top speed, a sprint that gives them their whole top speed
/// back while Shift is held, locked by the maintenance limiter until the Reapers' first rounds hit the core, and limited by
/// the drive's heat after. Nothing of it touches a machine outside the trio or any other match.
/// </summary>
public class StorySprintTests(ITestOutputHelper output)
{
    private static readonly Lazy<GameData> DataLoader = new(() => GameData.Load(RepoPaths.OriginalDataFolder()));

    private static readonly PlayerId Locator = new(0), Ike = new(4), Bob = new(5);

    // Deck A's long lane, east from its west end: room to run for a while.
    private static readonly Vector2 Lane = new(140, 52);

    private static GameWorld Unscripted(int seed = 7) =>
        MatchSetup.Create(DataLoader.Value, StoryOptions.Prologue, StoryOptions.Prologue, new PlayerTable([
            new PlayerSeat(Locator, Race.Red, PlayerKind.Human, "Host"),
            new PlayerSeat(Ike, Race.Red, PlayerKind.Bot, "Ike"),
            new PlayerSeat(Bob, Race.Red, PlayerKind.Bot, "Bob"),
            new PlayerSeat(PlayerId.Of(Race.Blue), Race.Blue, PlayerKind.Ai, "Ship")]), seed,
            new MatchOptions { Story = new StoryOptions { Unscripted = true }, FogOfWar = false });

    private static GameWorld Chapter(int checkpoint) =>
        MatchSetup.Create(DataLoader.Value, StoryOptions.Prologue, StoryOptions.Prologue,
            new Dictionary<Race, Controller> { [Race.Red] = Controller.Player, [Race.Blue] = Controller.Ai }, 3,
            new MatchOptions { Story = new StoryOptions { Checkpoint = checkpoint }, FogOfWar = false });

    private static Machine Hero(GameWorld world, PlayerId seat) => world.HeroOf(seat)!;

    private static void Place(GameWorld world, PlayerId seat, Vector2 at, float facing = 0)
    {
        var hero = Hero(world, seat);
        hero.Position = at;
        hero.Facing = facing;
        hero.Path.Clear();
        hero.PathIndex = 0;
    }

    /// <summary>The Locator alone on deck A's lane, looking east; the Grunts wait far behind, out of its way.</summary>
    private static GameWorld OnTheLane(bool free)
    {
        var world = Unscripted();
        Place(world, Locator, Lane);
        Place(world, Ike, Lane - new Vector2(60, 0));
        Place(world, Bob, Lane - new Vector2(60, 6));
        world.Issue(new StoryOrderCommand(Race.Red, CompanionOrder.Wait, Vector2.Zero) { Issuer = Locator });
        world.SetStoryLamp(Hero(world, Locator).Id, false);
        if (free)
            world.Story!.Flags.Add(StoryMarks.SprintFree);
        world.Step();
        return world;
    }

    private static void Drive(GameWorld world, bool sprint, float forward = 1, float turn = 0) =>
        world.Issue(new DriveCommand(Race.Red, Hero(world, Locator).Id, true, forward, turn, Sprint: sprint) { Issuer = Locator });

    /// <summary>Metres the Locator covers in the last second of <paramref name="seconds"/> driving ahead.</summary>
    private static float SpeedAfter(GameWorld world, bool sprint, float seconds)
    {
        var locator = Hero(world, Locator);
        var mark = locator.Position;
        for (var tick = 0; tick < seconds * GameWorld.TicksPerSecond; tick++)
        {
            if (tick == (int)((seconds - 1) * GameWorld.TicksPerSecond))
                mark = locator.Position;
            Drive(world, sprint);
            world.Step();
        }
        return Vector2.Distance(mark, locator.Position);
    }

    [Fact]
    public void The_trio_walks_at_a_share_of_its_top_speed_and_shift_does_nothing_while_the_limiter_holds()
    {
        var walking = OnTheLane(free: false);
        var trying = OnTheLane(free: false);
        var top = Hero(walking, Locator).Stats.Speed;
        var walk = SpeedAfter(walking, sprint: false, 4);
        var tried = SpeedAfter(trying, sprint: true, 4);
        output.WriteLine($"Locator top {top:0.00} m/s, accel {Hero(walking, Locator).Stats.Acceleration:0.00}, walks {walk:0.00}, Shift held {tried:0.00}; Grunt top {Hero(walking, Ike).Stats.Speed:0.00}");

        Assert.Equal(top * StoryDrive.Walk, walk, 1);
        Assert.Equal(walk, tried);
        Assert.Equal(Hero(walking, Locator).Position, Hero(trying, Locator).Position);
        Assert.False(walking.StoryDriveOf(Hero(walking, Locator).Id)!.Sprinting);
        Assert.Equal(0, trying.StoryDriveOf(Hero(trying, Locator).Id)!.Heat);
    }

    [Fact]
    public void Once_free_the_sprint_is_the_old_top_speed_and_letting_go_slows_to_the_walk()
    {
        var world = OnTheLane(free: true);
        var locator = Hero(world, Locator);
        var top = locator.Stats.Speed;
        Assert.Equal(top, SpeedAfter(world, sprint: true, 3), 1);
        Assert.True(world.StoryDriveOf(locator.Id)!.Sprinting);
        // Let go: down to the walk at the walk's rate, not at once.
        Drive(world, sprint: false);
        world.Step();
        Drive(world, sprint: false);
        world.Step();
        Assert.Equal(top * StoryDrive.Walk, SpeedAfter(world, sprint: false, 3), 1);
    }

    [Fact]
    public void The_sprint_heats_the_drive_until_it_overheats_and_frees_it_again_at_a_third()
    {
        var world = OnTheLane(free: true);
        var locator = Hero(world, Locator);
        var drive = world.StoryDriveOf(locator.Id)!;
        var start = world.Tick;
        var cues = new List<string>();
        while (!drive.Blocked && world.Tick - start < 12 * GameWorld.TicksPerSecond)
        {
            Drive(world, sprint: true);
            world.Step();
            cues.AddRange(world.Events.Where(e => e.Kind == GameEventKind.StoryCue).Select(e => e.Detail!));
        }
        var filled = (world.Tick - start) * GameWorld.Dt;
        output.WriteLine($"overheated after {filled:0.00} s of sprint, {Vector2.Distance(Lane, locator.Position):0.0} m down the lane");
        Assert.True(drive.Blocked);
        Assert.InRange(filled, 6, 8);
        Assert.Contains("drive hot", cues);

        // Overheated: Shift does nothing, it walks (after a stutter) and cools.
        var blockedAt = world.Tick;
        var slowest = float.MaxValue;
        var stutter = new StringBuilder();
        while (drive.Blocked && world.Tick - blockedAt < 10 * GameWorld.TicksPerSecond)
        {
            var was = locator.Position;
            Drive(world, sprint: true);
            world.Step();
            Assert.False(drive.Sprinting && drive.Blocked);
            var speed = Vector2.Distance(was, locator.Position) / GameWorld.Dt;
            if (world.Tick - blockedAt <= StoryDrive.StutterSeconds * GameWorld.TicksPerSecond)
            {
                slowest = MathF.Min(slowest, speed);
                stutter.Append(FormattableString.Invariant($" {speed:0.0}"));
            }
            cues.AddRange(world.Events.Where(e => e.Kind == GameEventKind.StoryCue).Select(e => e.Detail!));
        }
        var cooled = (world.Tick - blockedAt) * GameWorld.Dt;
        output.WriteLine($"free again after {cooled:0.00} s at {locator.Position}, heat {drive.Heat:0.00}, the stutter (m/s):{stutter}");
        Assert.False(drive.Blocked);
        Assert.Equal(StoryDrive.Resume, drive.Heat, 1);
        Assert.InRange(cooled, (1 - StoryDrive.Resume) * StoryDrive.CoolSeconds - 0.2f, (1 - StoryDrive.Resume) * StoryDrive.CoolSeconds + 0.2f);
        Assert.True(slowest < locator.Stats.Speed * StoryDrive.Walk * 0.5f, "the overheated drive stutters");
        Assert.Contains("drive cool", cues);

        // And it sprints again.
        Drive(world, sprint: true);
        world.Step();
        Assert.True(drive.Sprinting);
    }

    [Fact]
    public void The_sprint_is_freed_as_the_reapers_first_rounds_hit_the_core()
    {
        var world = Chapter(6);
        var run = world.Story!;
        Assert.False(world.StorySprintFree);
        // The squad bursts in (as the barricade at door I gives way): the break-in, then the first hit on the core.
        run.Flags.Add(Prologue.Burst);
        var cues = new List<string>();
        for (var tick = 0; tick < 4 * GameWorld.TicksPerSecond && !run.Director.Has("limiter"); tick++)
        {
            world.Step();
            cues.AddRange(world.Events.Where(e => e.Kind == GameEventKind.StoryCue).Select(e => e.Detail!));
            if (!run.Director.Has("core_hit_1"))
                Assert.False(world.StorySprintFree);
        }
        Assert.True(run.Director.Has("break_in"));
        Assert.True(run.Director.Has("limiter"));
        Assert.Equal(run.Director.Fired["break_in"] + Prologue.CoreHit1, run.Director.Fired["limiter"], 2);
        Assert.Equal(run.Director.Fired["core_hit_1"], run.Director.Fired["limiter"]);
        world.Step();
        Assert.True(world.StorySprintFree);
        Assert.Contains("sprint free", cues);
    }

    [Fact]
    public void Checkpoints_from_the_break_in_on_start_with_the_sprint_free()
    {
        for (var checkpoint = 0; checkpoint <= Prologue.LastCheckpoint; checkpoint++)
            Assert.Equal(Prologue.SetUpAs(checkpoint) >= 7, Chapter(checkpoint).StorySprintFree);
    }

    [Fact]
    public void A_computer_grunt_sprints_after_a_sprinting_locator_and_catches_up_when_left_behind()
    {
        var world = Unscripted();
        world.Story!.Flags.Add(StoryMarks.SprintFree);
        // 20 m further east than Lane, so that Bob, 30 m behind, stands on deck A and not in the Leitstand beyond its grate.
        var lane = Lane + new Vector2(20, 0);
        Place(world, Locator, lane);
        Place(world, Ike, lane - new Vector2(8, 3));
        Place(world, Bob, lane - new Vector2(30, -3));
        world.Step();
        var ike = world.StoryDriveOf(Hero(world, Ike).Id)!;
        var bob = world.StoryDriveOf(Hero(world, Bob).Id)!;
        var seenIke = false;
        var seenBob = false;
        for (var tick = 0; tick < 3 * GameWorld.TicksPerSecond; tick++)
        {
            Drive(world, sprint: true);
            world.Step();
            seenIke |= ike.Sprinting;
            seenBob |= bob.Sprinting && bob.CatchingUp;
        }
        Assert.True(seenIke, "Ike sprints after the sprinting Locator");
        Assert.True(seenBob, "Bob, 30 m behind, catches up at a sprint");
        // The Locator stands: they come up to it and walk again.
        for (var tick = 0; tick < 12 * GameWorld.TicksPerSecond; tick++)
        {
            Drive(world, sprint: false, forward: 0);
            world.Step();
        }
        Assert.False(ike.Sprinting);
        Assert.False(bob.Sprinting);
        Assert.True(Vector2.Distance(Hero(world, Bob).Position, Hero(world, Locator).Position) < StoryDrive.CatchUpFrom);
    }

    [Fact]
    public void No_grunt_of_the_computer_sprints_while_the_limiter_holds()
    {
        var world = Unscripted();
        Place(world, Locator, Lane);
        Place(world, Ike, Lane - new Vector2(40, 3));
        Place(world, Bob, Lane - new Vector2(40, -3));
        for (var tick = 0; tick < 6 * GameWorld.TicksPerSecond; tick++)
        {
            Drive(world, sprint: true);
            world.Step();
            Assert.All(world.Story!.Drives.Of.Values, drive => Assert.False(drive.Sprinting));
        }
    }

    [Fact]
    public void Sprinting_is_louder_than_walking()
    {
        double Signature(bool sprint)
        {
            var world = OnTheLane(free: true);
            var locator = Hero(world, Locator);
            var loudest = 0f;
            for (var tick = 0; tick < 2 * GameWorld.TicksPerSecond; tick++)
            {
                Drive(world, sprint);
                world.Step();
                loudest = MathF.Max(loudest, world.StorySignature(locator.Id));
            }
            return loudest;
        }
        var walking = Signature(false);
        var sprinting = Signature(true);
        output.WriteLine($"walking {walking:0.00}, sprinting {sprinting:0.00}");
        // Walking flat out is half as loud as moving at the walk of stage 6 was (StorySight.MovingAt); the sprint is the whole
        // of moving and adds its drive's whine.
        Assert.Equal(StorySight.Still + StorySight.Moving * StoryDrive.Walk / StorySight.MovingAt, walking, 2);
        Assert.Equal(StorySight.Still + StorySight.Moving + StorySight.Sprint, sprinting, 2);
    }

    [Fact]
    public void The_sprint_is_carried_by_the_drive_command_and_left_out_where_it_is_not_held()
    {
        var plain = new DriveCommand(Race.Red, 7, true, 1);
        var json = Encoding.UTF8.GetString(NetJson.Encode(new Turn(3, Locator, [plain])));
        Assert.DoesNotContain("Sprint", json);
        var held = Encoding.UTF8.GetString(NetJson.Encode(new Turn(3, Locator, [plain with { Sprint = true }])));
        Assert.Contains("\"Sprint\":true", held);
        var back = (Turn)NetJson.Decode(Encoding.UTF8.GetBytes(held));
        Assert.True(((DriveCommand)back.Commands[0]).Sprint);
    }

    [Fact]
    public void Outside_a_story_there_is_no_drive_and_the_sprint_changes_nothing()
    {
        GameWorld Match() => MatchSetup.Create(DataLoader.Value, "m_moon", "m_moon",
            new Dictionary<Race, Controller> { [Race.Red] = Controller.Player, [Race.Blue] = Controller.Ai }, seed: 7);
        var (plain, held) = (Match(), Match());
        Vector2 Drove(GameWorld world, bool sprint)
        {
            var grunt = world.AddMachine(Race.Red, new Vector2(400, 400), "AGGRESSOR", "GRUNT", 1, "R_BOLTER");
            for (var tick = 0; tick < 2 * GameWorld.TicksPerSecond; tick++)
            {
                world.Issue(new DriveCommand(Race.Red, grunt.Id, true, 1, Sprint: sprint));
                world.Step();
            }
            Assert.Null(world.StoryDriveOf(grunt.Id));
            Assert.Equal(1, world.StoryPace(grunt));
            Assert.Equal(1, world.StoryTurnPace(grunt));
            return grunt.Position;
        }
        Assert.Equal(Drove(plain, false), Drove(held, true));
        Assert.Equal(plain.Checksum(), held.Checksum());
    }

    [Fact]
    public void A_following_grunt_in_the_way_of_the_walking_locator_makes_way()
    {
        var world = Unscripted();
        // The Locator turned back west down the lane; Ike, who followed it, stands right ahead of it, Bob far off.
        Place(world, Locator, Lane + new Vector2(45, 0), facing: MathF.PI);
        Place(world, Ike, Lane + new Vector2(36, 0));
        Place(world, Bob, Lane + new Vector2(100, 0));
        world.Step();
        var locator = Hero(world, Locator);
        var start = locator.Position;
        for (var tick = 0; tick < 12 * GameWorld.TicksPerSecond; tick++)
        {
            Drive(world, sprint: false);
            world.Step();
        }
        var ike = Hero(world, Ike);
        // Ike turned aside (a Grunt turns slowly) and the Locator got by it and on down the lane.
        Assert.True(locator.Position.X < ike.Position.X - 3, $"the Locator at {locator.Position}, Ike at {ike.Position}");
        Assert.True(start.X - locator.Position.X > 25,
            $"the Locator came {start.X - locator.Position.X:0.0} m");
    }

    [Fact]
    public void The_ships_machines_go_at_their_own_pace()
    {
        var world = Chapter(1);
        world.Step();
        var thresher = world.Story!.Director.Of(world, Prologue.ThresherRole).Single();
        var reapers = Chapter(7);
        reapers.Step();
        foreach (var role in new[] { Prologue.ReaperRole, Prologue.CommanderRole })
        {
            foreach (var m in reapers.Story!.Director.Of(reapers, role))
                output.WriteLine($"{role} L{m.Stats.HardwareLevel}: {m.Stats.Speed:0.0} m/s, accel {m.Stats.Acceleration:0.0}, turn {m.Stats.RotationSpeed:0}");
        }
        output.WriteLine($"thresher: {thresher.Stats.Speed:0.0} m/s; Locator {Hero(world, new PlayerId(0)).Stats.Speed:0.0}, turn {Hero(world, new PlayerId(0)).Stats.RotationSpeed:0}");
        Assert.Null(world.StoryDriveOf(thresher.Id));
        Assert.Equal(3, world.Story.Drives.Of.Count);
        // Section 2's Thresher goes by what he does, out of the walk's coupling (design-stealth §3.1): on his way and his round
        // a quarter of his top speed, so the walking trio is a little faster behind him; searching 0.4, hunting 0.7.
        Assert.Contains(StoryMarks.Stealth, world.Story.Flags);
        Assert.Equal(StorySight.PatrolPace, world.StoryPace(thresher));
        Assert.True(thresher.Stats.Speed * world.StoryPace(thresher) < Hero(world, new PlayerId(0)).Stats.Speed * StoryDrive.Walk);
        Assert.Equal(StorySight.PatrolTurn, world.StoryTurnPace(thresher));
        thresher.Order = new SearchOrder(thresher.Position, world.Time + 5, null);
        Assert.Equal(StorySight.SearchPace, world.StoryPace(thresher));
        thresher.Order = new AttackOrder(Hero(world, Ike).Id);
        Assert.Equal(StorySight.HuntPace, world.StoryPace(thresher));
        Assert.Equal(1, world.StoryTurnPace(thresher));
        Assert.True(thresher.Stats.Speed * world.StoryPace(thresher) > Hero(world, Ike).Stats.Speed * StoryDrive.Walk);
        // Out of section 2 (H shut behind the trio) the seeing hunters walk at the trio's walk while the sprint is locked, and
        // go at their whole pace once it is free.
        world.Story.Flags.Remove(StoryMarks.Stealth);
        Assert.Equal(StoryDrive.Walk, world.StoryPace(thresher));
        world.Story.Flags.Add(StoryMarks.SprintFree);
        Assert.Equal(1, world.StoryPace(thresher));
        Assert.All(reapers.Story!.Director.Of(reapers, Prologue.ReaperRole), m => Assert.Equal(1, reapers.StoryPace(m)));
    }
    [Fact]
    public void The_creep_from_slow_goes_at_half_the_walk_beats_the_sprint_and_halves_the_signature()
    {
        // Section 2's creep (design-stealth §3.1, §3.2): the drive command's slow flag (Ctrl, the pad's R3) is the creep.
        var world = OnTheLane(free: true);
        world.Story!.Flags.Add(StoryMarks.Stealth);
        var locator = Hero(world, Locator);
        (float Speed, float Signature) Over(bool slow)
        {
            var mark = locator.Position;
            for (var tick = 0; tick < 3 * GameWorld.TicksPerSecond; tick++)
            {
                if (tick == 2 * GameWorld.TicksPerSecond)
                    mark = locator.Position;
                world.Issue(new DriveCommand(Race.Red, locator.Id, true, 1, Slow: slow, Sprint: true) { Issuer = Locator });
                world.Step();
            }
            return (Vector2.Distance(mark, locator.Position), world.Story.Sight.Signatures[locator.Id]);
        }
        var creep = Over(slow: true);
        var drive = world.StoryDriveOf(locator.Id)!;
        Assert.True(drive.Creeping);
        Assert.False(drive.Sprinting);
        Assert.Equal(StoryDrive.Walk * StoryDrive.Creep, world.StoryPace(locator), 3);
        var sprint = Over(slow: false);
        Assert.False(drive.Creeping);
        Assert.True(drive.Sprinting);
        output.WriteLine($"creep {creep.Speed:0.00} m/s, signature {creep.Signature:0.000}; sprint {sprint.Speed:0.00} m/s, {sprint.Signature:0.000}");
        Assert.InRange(creep.Speed, locator.Stats.Speed * StoryDrive.Walk * StoryDrive.Creep * 0.9f, locator.Stats.Speed * StoryDrive.Walk * StoryDrive.Creep * 1.1f);
        // Creeping, the moving part of the signature is a quarter of the walk's (half the share, halved again).
        Assert.InRange(creep.Signature, StorySight.Still + StorySight.Moving * 0.25f * 0.9f, StorySight.Still + StorySight.Moving * 0.25f * 1.1f);
    }
}
