using System.Numerics;
using System.Text.Json.Nodes;
using Machines.Import.Model;
using Machines.Net;
using Machines.Sim;
using Machines.Sim.World;

namespace Machines.Tests.Sim;

/// <summary>
/// Machines stand and are hit in bodies fitted to their models (<see cref="MachineBodies"/>, GameWorld.Bodies), not in the
/// original's circle and the square box round it, wherever the match's options say <see cref="MatchOptions.FittedBodies"/>,
/// which every new match does: a Grunt drives up to another's nose and stops at its tail as the models meet, a bolt short
/// of a slim machine's nose goes past, a sight beside any machine is taken into its body, and the door size classes stay
/// the circle's. A recording from before, whose options lack the field, plays back with the circles.
/// </summary>
public class FittedBodyTests
{
    private static readonly Lazy<GameData> DataLoader = new(() => GameData.Load(RepoPaths.OriginalDataFolder()));

    private static GameData Data => DataLoader.Value;

    private static GameWorld NewMatch(bool fitted = true) =>
        MatchSetup.Create(Data, "m_moon", "m_moon",
            new Dictionary<Race, Controller> { [Race.Red] = Controller.Player, [Race.Blue] = Controller.Player }, seed: 7,
            new MatchOptions { FittedBodies = fitted });

    /// <summary>The middle of an open square of ground <paramref name="size"/> metres across.</summary>
    private static Vector2 OpenGround(GameWorld world, float size = 120)
    {
        var (width, depth) = (world.Planet.Surface.Width, world.Planet.Surface.Depth);
        for (var y = size / 2; y < depth - size / 2; y += 20)
            for (var x = size / 2; x < width - size / 2; x += 20)
                if (world.Nav.IsAreaFree((new Vector2(x, y) - new Vector2(size / 2), new Vector2(x, y) + new Vector2(size / 2))))
                    return new Vector2(x, y);
        throw new InvalidOperationException("no open ground");
    }

    private static Machine Standing(GameWorld world, Race race, Vector2 at, float facing, string type, string? subType, int level, string? combo)
    {
        var machine = world.AddMachine(race, at, type, subType, level, combo);
        machine.Facing = facing;
        // Left to itself it holds still, neither evading nor taking anything on.
        machine.DefCon = DefCon.High;
        machine.Order = new StandGroundOrder();
        return machine;
    }

    // ---- the table ---------------------------------------------------------------------------------

    public static TheoryData<string, string?, int> Measured
    {
        get
        {
            var data = new TheoryData<string, string?, int>();
            foreach (var ((type, subType, level), _) in MachineBodies.All)
                data.Add(type, subType, level);
            return data;
        }
    }

    [Fact]
    public void Every_machine_on_the_ground_has_a_body_its_middle_stands_in_and_a_flyer_has_none()
    {
        var levels = Data.Parm.Machines.SelectMany(spec => spec.Levels.Select(level => (spec, level))).ToList();
        foreach (var (spec, level) in levels)
        {
            var body = MachineBodies.Of(spec.Type, spec.SubType, level.HardwareLevel);
            var name = $"{spec.TypeName} {level.HardwareLevel}";
            if (level.Locomotion == Locomotion.Glider)
            {
                Assert.True(body is null, $"{name} flies and keeps its box in the air");
                continue;
            }
            Assert.True(body is { Fore: > 0, Aft: > 0, Half: > 0, Height: > 1 }, $"{name}: {body}");
        }
        // And nothing in the table the data does not have: every body belongs to a machine on the ground.
        Assert.All(MachineBodies.All, entry => Assert.Contains(levels, l => (l.spec.Type, l.spec.SubType, l.level.HardwareLevel) == entry.Machine
            && l.level.Locomotion != Locomotion.Glider));
        Assert.Equal(levels.Count(l => l.level.Locomotion != Locomotion.Glider), MachineBodies.All.Count());
    }

    [Theory]
    [MemberData(nameof(Measured))]
    public void A_sight_beside_a_machine_is_taken_into_its_body_and_rounds_are_aimed_inside_it(string type, string? subType, int level)
    {
        var body = MachineBodies.Of(type, subType, level)!.Value;
        var world = NewMatch();
        var at = OpenGround(world);
        var machine = Standing(world, Race.Blue, at, 0, type, subType, level, null);
        Assert.Equal(body, machine.FittedBody);

        Assert.Equal(at.X + body.Fore, world.AimInside(machine, at + new Vector2(40, 0), 1).Point.X, 2);
        Assert.Equal(at.X - body.Aft, world.AimInside(machine, at - new Vector2(40, 0), 1).Point.X, 2);
        Assert.Equal(at.Y + body.Half, world.AimInside(machine, at + new Vector2(0, 40), 1).Point.Y, 2);
        Assert.Equal(at.Y - body.Half, world.AimInside(machine, at - new Vector2(0, 40), 1).Point.Y, 2);
        // A sight up to its top stays where it is; one over it goes to the aim point, which lies inside the body.
        Assert.Equal(body.Height - 0.05f, world.AimInside(machine, at, body.Height - 0.05f).Height);
        var aim = world.AimInside(machine, at, body.Height + 1).Height;
        Assert.Equal(world.AimPoint(machine).Z, aim);
        // The original's aim point, unchanged, stands in every body's lower three quarters (a Resource Carrier 4's at 0.71).
        Assert.InRange(aim, 0.1f, body.Height * 0.75f);

        // In the circles a sight ahead is taken to the square box's edge, at the radius.
        var circles = NewMatch(fitted: false);
        var old = Standing(circles, Race.Blue, at, 0, type, subType, level, null);
        Assert.Equal(at.X + old.Radius, circles.AimInside(old, at + new Vector2(40, 0), 1).Point.X, 2);
    }

    [Fact]
    public void The_circle_a_machine_fits_through_doors_by_stays_as_it_was()
    {
        foreach (var fitted in new[] { true, false })
        {
            var world = NewMatch(fitted);
            var at = OpenGround(world);
            var grunt = Standing(world, Race.Red, at, 0, "AGGRESSOR", "GRUNT", 1, "R_BOLTER");
            var reaper = Standing(world, Race.Red, at + new Vector2(20, 0), 0, "AGGRESSOR", "ASSASSIN", 3, "LR_PLASMA_RIFLE_X2");
            Assert.Equal((1.5f, 2.4f), (grunt.Radius, reaper.Radius));
        }
    }

    // ---- driving into one another -------------------------------------------------------------------

    /// <summary>Drives <paramref name="driver"/> straight ahead until it stops; how far its middle then is from <paramref name="other"/>'s.</summary>
    private static float DriveInto(GameWorld world, Machine driver, Machine other)
    {
        var last = driver.Position;
        for (var i = 0; i < 20 * 30; i++)
        {
            world.Issue(new DriveCommand(driver.Race, driver.Id, Driving: true, Forward: 1));
            world.Step();
            if (i > 20 && Vector2.Distance(last, driver.Position) < 1e-4f)
                break;
            last = driver.Position;
        }
        return Vector2.Distance(driver.Position, other.Position);
    }

    [Fact]
    public void A_grunt_drives_up_to_anothers_nose_and_stops_at_its_tail_where_the_bodies_meet()
    {
        var grunt = MachineBodies.Of("AGGRESSOR", "GRUNT", 1)!.Value;

        // Facing each other: nose to nose, closer than the circles' 3 m (1.5 + 1.5).
        var world = NewMatch();
        var at = OpenGround(world);
        var driver = Standing(world, Race.Red, at, 0, "AGGRESSOR", "GRUNT", 1, "R_BOLTER");
        var other = Standing(world, Race.Red, at + new Vector2(12, 0), MathF.PI, "AGGRESSOR", "GRUNT", 1, "R_BOLTER");
        Assert.InRange(DriveInto(world, driver, other), 2 * grunt.Fore, 2 * grunt.Fore + 0.3f);

        // The other turned away: the driver stops at its tail, which sticks out of its circle at the back.
        world = NewMatch();
        driver = Standing(world, Race.Red, at, 0, "AGGRESSOR", "GRUNT", 1, "R_BOLTER");
        other = Standing(world, Race.Red, at + new Vector2(12, 0), 0, "AGGRESSOR", "GRUNT", 1, "R_BOLTER");
        Assert.InRange(DriveInto(world, driver, other), grunt.Fore + grunt.Aft, grunt.Fore + grunt.Aft + 0.3f);

        // In the circles both stop 3 m apart whichever way they face.
        world = NewMatch(fitted: false);
        driver = Standing(world, Race.Red, at, 0, "AGGRESSOR", "GRUNT", 1, "R_BOLTER");
        other = Standing(world, Race.Red, at + new Vector2(12, 0), MathF.PI, "AGGRESSOR", "GRUNT", 1, "R_BOLTER");
        Assert.InRange(DriveInto(world, driver, other), 3f, 3.3f);
    }

    /// <summary>
    /// A Grunt driven nose to nose with another, held there (or let go of with <paramref name="letGo"/>), and the other then
    /// sent on an order to a point 15 m behind the driver, straight through it: the least distance between their middles.
    /// </summary>
    private static (float Parked, float Least) SentThrough(bool fitted, bool letGo)
    {
        var world = NewMatch(fitted);
        var at = OpenGround(world);
        var driver = Standing(world, Race.Red, at, 0, "AGGRESSOR", "GRUNT", 1, "R_BOLTER");
        var other = Standing(world, Race.Red, at + new Vector2(12, 0), MathF.PI, "AGGRESSOR", "GRUNT", 1, "R_BOLTER");
        var parked = DriveInto(world, driver, other);
        world.Issue(new DriveCommand(driver.Race, driver.Id, Driving: !letGo));
        world.Step();
        world.Issue(new MoveCommand(other.Race, [other.Id], driver.Position - new Vector2(15, 0)));
        var least = float.MaxValue;
        for (var i = 0; i < 20 * 20; i++)
        {
            if (!letGo)
                world.Issue(new DriveCommand(driver.Race, driver.Id, Driving: true));
            world.Step();
            least = MathF.Min(least, Vector2.Distance(driver.Position, other.Position));
        }
        return (parked, least);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void One_on_an_order_does_not_drive_through_a_machine_parked_nose_to_nose_closer_than_the_circles(bool letGo)
    {
        var grunt = MachineBodies.Of("AGGRESSOR", "GRUNT", 1)!.Value;
        var (parked, least) = SentThrough(fitted: true, letGo);
        Assert.True(parked < 3, $"parked {parked} m apart, inside the circles");
        // It does not come closer than where their bodies meet nose to nose (it drove through to 0.17 m before).
        Assert.True(least >= 2 * grunt.Fore - 0.2f, $"least {least} m");

        (parked, least) = SentThrough(fitted: false, letGo);
        Assert.True(least >= 3, $"circles: least {least} m");
    }

    // ---- rounds -------------------------------------------------------------------------------------

    /// <summary>A Grunt with a Bolter driven in first person, and a Spy Locator 8 m ahead facing it: slim, it reaches 0.65 m ahead of its middle where its circle reached 1.5.</summary>
    private static (GameWorld World, Machine Shooter, Machine Foe) Range(bool fitted)
    {
        var world = NewMatch(fitted);
        var at = OpenGround(world);
        var shooter = Standing(world, Race.Red, at, 0, "AGGRESSOR", "GRUNT", 1, "R_BOLTER");
        var foe = Standing(world, Race.Blue, at + new Vector2(8, 0), MathF.PI, "SPY_LOCATOR", null, 3, null);
        return (world, shooter, foe);
    }

    /// <summary>The Bolter's hits on the foe of rounds sent to a point, everyone kept where and as whole as they were.</summary>
    private static int HitsOn(GameWorld world, Machine shooter, Machine foe, Vector2 point, float height, int rounds)
    {
        var (fired, hits) = (0, 0);
        var (at, facing) = (foe.Position, foe.Facing);
        for (var i = 0; i < 20 * 600 && fired < rounds; i++)
        {
            world.Issue(new DriveCommand(shooter.Race, shooter.Id, Driving: true, Aim: point, Weapons: 1, AimHeight: height));
            foe.Position = at;
            foe.Facing = facing;
            (foe.HitPoints, foe.Armour, shooter.HitPoints) = (foe.MaxHitPoints, foe.MaxArmour, shooter.MaxHitPoints);
            world.Step();
            fired += world.Events.Count(e => e.Kind == GameEventKind.Fired && e.Actor == shooter.Id);
            hits += world.Events.Count(e => e.Kind == GameEventKind.Hit && e.Actor == foe.Id && e.Other == shooter.Id);
        }
        Assert.Equal(rounds, fired);
        return hits;
    }

    [Fact]
    public void A_bolt_on_a_spy_hits_and_one_short_of_its_nose_goes_past_where_the_circles_box_was_met()
    {
        var spy = MachineBodies.Of("SPY_LOCATOR", null, 3)!.Value;
        var (world, shooter, foe) = Range(fitted: true);
        Assert.True(HitsOn(world, shooter, foe, foe.Position, 1, 6) >= 5);

        // Short of its nose: the circles' box reached 1.5 m ahead of its middle and the bolt's burst a metre further.
        var shortOf = new Vector2(spy.Fore + 1.2f, 0);
        (world, shooter, foe) = Range(fitted: true);
        Assert.Equal(0, HitsOn(world, shooter, foe, foe.Position - shortOf, 1, 6));
        (world, shooter, foe) = Range(fitted: false);
        Assert.True(HitsOn(world, shooter, foe, foe.Position - shortOf, 1, 6) >= 5);
    }

    [Fact]
    public void A_sight_on_a_reapers_head_is_kept_where_it_is()
    {
        var world = NewMatch();
        var at = OpenGround(world);
        var reaper = Standing(world, Race.Blue, at, 0, "AGGRESSOR", "ASSASSIN", 2, "L_PLASMA_RIFLE");
        // The head stands 5 m up, over the circles' box of 2.9 m.
        Assert.Equal(5f, world.AimInside(reaper, reaper.Position, 5).Height);

        var circles = NewMatch(fitted: false);
        reaper = Standing(circles, Race.Blue, at, 0, "AGGRESSOR", "ASSASSIN", 2, "L_PLASMA_RIFLE");
        Assert.Equal(circles.AimPoint(reaper).Z, circles.AimInside(reaper, reaper.Position, 5).Height);
    }

    // ---- matches and recordings ---------------------------------------------------------------------

    private static ulong[] Checksums(GameWorld world, params int[] ticks)
    {
        var sums = new List<ulong>();
        foreach (var tick in ticks)
        {
            while (world.Tick < tick)
                world.Step();
            sums.Add(world.Checksum());
        }
        return [.. sums];
    }

    /// <summary>
    /// A skirmish of the computer's two races on Canyon Attack whose armies meet early: a mixed force of each side's, Grunts,
    /// Reapers, Knights and a Commander, set down 60 m apart in the open and sent at the other's.
    /// </summary>
    private static GameWorld Skirmish(bool fitted)
    {
        var world = MatchSetup.Create(Data, "m_desert", "m_desert",
            new Dictionary<Race, Controller> { [Race.Red] = Controller.Ai, [Race.Blue] = Controller.Ai }, seed: 11,
            new MatchOptions { FittedBodies = fitted });
        var at = OpenGround(world, 160);
        (string Type, string SubType, int Level, string Combo)[] army =
        [
            ("AGGRESSOR", "GRUNT", 1, "R_BOLTER"), ("AGGRESSOR", "GRUNT", 2, "L_FLAME_THROWER2"), ("AGGRESSOR", "GRUNT", 3, "LR_HEAVY_BOLTER1_X2"),
            ("AGGRESSOR", "ASSASSIN", 2, "L_AUTO_CANNON"), ("AGGRESSOR", "ASSASSIN", 3, "LR_PLASMA_RIFLE_X2"), ("AGGRESSOR", "KNIGHT", 3, "LR_PLASMA_CANNON1_X2"),
            ("AGGRESSOR", "KNIGHT", 4, "LR_MULTI_LAUNCHER3_X2"), ("ADMINISTRATOR", "COMMANDER", 5, "LR_PULSE_CANNON_X2"),
        ];
        var sides = new Dictionary<Race, List<Machine>> { [Race.Red] = [], [Race.Blue] = [] };
        for (var i = 0; i < army.Length; i++)
        {
            var (type, subType, level, combo) = army[i];
            var across = new Vector2(0, (i - army.Length / 2f) * 7);
            sides[Race.Red].Add(world.AddMachine(Race.Red, at - new Vector2(30, 0) + across, type, subType, level, combo));
            var blue = world.AddMachine(Race.Blue, at + new Vector2(30, 0) + across, type, subType, level, combo);
            blue.Facing = MathF.PI;
            sides[Race.Blue].Add(blue);
        }
        world.Step();
        world.Issue(new AttackCommand(Race.Red, [.. sides[Race.Red].Select(m => m.Id)], sides[Race.Blue][^1].Id));
        world.Issue(new AttackCommand(Race.Blue, [.. sides[Race.Blue].Select(m => m.Id)], sides[Race.Red][^1].Id));
        return world;
    }

    [Fact]
    public void A_skirmish_in_fitted_bodies_plays_the_same_every_time_and_otherwise_than_in_circles()
    {
        var first = Checksums(Skirmish(fitted: true), 200, 600, 1200);
        var again = Checksums(Skirmish(fitted: true), 200, 600, 1200);
        var circles = Checksums(Skirmish(fitted: false), 200, 600, 1200);

        Assert.Equal(first, again);
        Assert.NotEqual(first[^1], circles[^1]);
    }
    [Fact]
    public void A_new_match_has_fitted_bodies() => Assert.True(new MatchOptions().FittedBodies);

    /// <summary>A golden match recorded to its last sum, its file written with <paramref name="change"/> made to its options.</summary>
    private static (Replay Loaded, ulong Sum) Recorded(GoldenCase golden, Action<JsonObject> change)
    {
        var match = new GoldenMatch(golden, Data);
        var sum = match.PlayTo(golden.Ticks[^1])[0];
        var path = Path.Combine(Path.GetTempPath(), $"bodies-{Guid.NewGuid():N}{Replay.Extension}");
        try
        {
            match.Recording.Save(path);
            var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            change(json["Match"]!["Options"]!.AsObject());
            File.WriteAllText(path, json.ToJsonString());
            return (Replay.Load(path), sum);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static ulong PlayedBack(Replay replay, int tick)
    {
        var playback = replay.Playback(Replay.BuildWorld(Data, replay.Match));
        while (playback.World.Tick < tick)
            Assert.True(playback.TryStep(), $"the playback stuck at tick {playback.World.Tick}");
        return playback.World.Checksum();
    }

    [Fact]
    public void A_recording_from_before_fitted_bodies_plays_back_in_the_circles_it_was_played_in()
    {
        // G1, played in circles; its file as one from before reads, without the field.
        var golden = GoldenCase.Skirmish;
        var (old, sum) = Recorded(golden, options => Assert.True(options.Remove(nameof(MatchOptions.FittedBodies))));
        Assert.False(old.Match.Options.FittedBodies);
        Assert.Equal(sum, PlayedBack(old, golden.Ticks[^1]));

        // The same file saying true plays another match: the flag is what decides.
        var (claimed, _) = Recorded(golden, options => options[nameof(MatchOptions.FittedBodies)] = true);
        Assert.True(claimed.Match.Options.FittedBodies);
        Assert.NotEqual(sum, PlayedBack(claimed, golden.Ticks[^1]));
    }

    [Fact]
    public void A_recording_in_fitted_bodies_keeps_them_when_loaded()
    {
        var (loaded, sum) = Recorded(GoldenCase.FittedSkirmish, _ => { });
        Assert.True(loaded.Match.Options.FittedBodies);
        Assert.Equal(sum, PlayedBack(loaded, GoldenCase.FittedSkirmish.Ticks[^1]));
    }
}
