using System.Numerics;
using Machines.Import.Model;
using Machines.Sim;
using Machines.Sim.World;

namespace Machines.Tests.Sim;

/// <summary>
/// Machines turn and speed up as in the original (MachPhysMachine, PhysRampAcceleration, MachLogMachineMotionSequencer):
/// on the spot first, then up to speed at their ACCELERATION and down to a stop at the next rest point.
/// </summary>
public class MotionTests
{
    private static readonly Lazy<GameData> DataLoader = new(() => GameData.Load(RepoPaths.OriginalDataFolder()));

    private static GameWorld NewMatch(MatchOptions? options = null) =>
        MatchSetup.Create(DataLoader.Value, "m_moon", "m_moon",
            new Dictionary<Race, Controller> { [Race.Red] = Controller.Player, [Race.Blue] = Controller.Ai }, seed: 7, options);

    /// <summary>The middle of an open square of ground <paramref name="size"/> metres across.</summary>
    private static Vector2 OpenGround(GameWorld world, float size = 220)
    {
        var (width, depth) = (world.Planet.Surface.Width, world.Planet.Surface.Depth);
        for (var y = size / 2; y < depth - size / 2; y += 20)
            for (var x = size / 2; x < width - size / 2; x += 20)
                if (world.Nav.IsAreaFree((new Vector2(x, y) - new Vector2(size / 2), new Vector2(x, y) + new Vector2(size / 2))))
                    return new Vector2(x, y);
        throw new InvalidOperationException("no open ground");
    }

    /// <summary>Gives a machine a move along exactly these points.</summary>
    private static void SendAlong(Machine machine, params Vector2[] points)
    {
        machine.Order = new MoveOrder(points[^1]);
        machine.Path.Clear();
        machine.Path.AddRange(points);
        machine.PathIndex = 0;
    }

    private const float Dt = 1f / GameWorld.TicksPerSecond;
    private const float Degrees = MathF.PI / 180;

    /// <summary>Steps until <paramref name="until"/>, recording the machine's speed each tick; returns the speeds.</summary>
    private static List<float> Speeds(GameWorld world, Machine machine, float seconds, Func<bool>? until = null)
    {
        var speeds = new List<float>();
        for (var i = 0; i < seconds * GameWorld.TicksPerSecond; i++)
        {
            var before = machine.Position;
            world.Step();
            speeds.Add(Vector2.Distance(before, machine.Position) / Dt);
            if (until?.Invoke() == true)
                break;
        }
        return speeds;
    }

    [Fact]
    public void A_machine_turns_on_the_spot_before_it_drives_off()
    {
        var world = NewMatch();
        var at = OpenGround(world);
        var grunt = world.AddMachine(Race.Red, at, "AGGRESSOR", "GRUNT", 1, "R_BOLTER");
        Assert.Equal(0, grunt.Facing);
        world.Step();

        // 90° at 115°/s, ramped at 85°/s²: too short to reach full turning speed, so 2·√(90/85) = 2.06 s.
        world.Issue(new MoveCommand(Race.Red, [grunt.Id], at + new Vector2(0, 80)));
        var turning = 0;
        var halfway = float.NaN;
        while (grunt.Position == at && turning < 100)
        {
            world.Step();
            turning++;
            if (turning == 21)
                halfway = grunt.Facing;
        }

        var expected = 2 * MathF.Sqrt(90f / 85f);
        Assert.InRange(turning * Dt, expected, expected + 2 * Dt);
        // A symmetric ramp: half the time, half the turn.
        Assert.InRange(halfway, 40 * Degrees, 50 * Degrees);
        Assert.Equal(MathF.PI / 2, grunt.Facing, 2);
    }

    [Fact]
    public void It_speeds_up_at_its_acceleration_and_stops_at_the_goal()
    {
        var world = NewMatch();
        var at = OpenGround(world);
        var grunt = world.AddMachine(Race.Red, at, "AGGRESSOR", "GRUNT", 1, "R_BOLTER");
        var goal = at + new Vector2(100, 0);
        world.Step();

        world.Issue(new MoveCommand(Race.Red, [grunt.Id], goal));
        // Ten ticks at 19 m/s²: ½·19·0.5² = 2.375 m.
        for (var i = 0; i < 10; i++)
            world.Step();
        Assert.Equal(2.375f, grunt.Position.X - at.X, 1);

        // Up to 15 m/s, on at that, and down at the same rate to a stop on the goal: 100/15 + 15/19 = 7.46 s.
        var speeds = Speeds(world, grunt, 10, () => grunt.Order is IdleOrder);
        Assert.Equal(15, speeds.Max(), 1);
        Assert.True(Vector2.Distance(goal, grunt.Position) < 0.01f, $"stopped at {grunt.Position}");
        Assert.InRange((10 + speeds.Count) * Dt, 100f / 15 + 15f / 19 - 2 * Dt, 100f / 15 + 15f / 19 + 3 * Dt);
        // It slows down before the goal rather than stopping dead on it.
        Assert.True(speeds[^2] < 3, $"still at {speeds[^2]} m/s a tick before the goal");
    }

    [Fact]
    public void It_stops_at_a_sharp_corner_and_drives_through_a_gentle_one()
    {
        var world = NewMatch();
        var at = OpenGround(world);
        var sharp = world.AddMachine(Race.Red, at - new Vector2(80, 40), "AGGRESSOR", "GRUNT", 1, "R_BOLTER");
        var gentle = world.AddMachine(Race.Red, at - new Vector2(80, -40), "AGGRESSOR", "GRUNT", 1, "R_BOLTER");
        // A 90° corner and a 30° one, 60 m on.
        SendAlong(sharp, sharp.Position + new Vector2(60, 0), sharp.Position + new Vector2(60, -60));
        SendAlong(gentle, gentle.Position + new Vector2(60, 0), gentle.Position + new Vector2(60 + 60 * MathF.Cos(30 * Degrees), 60 * MathF.Sin(30 * Degrees)));

        var sharpSpeeds = new List<(float Speed, float ToCorner)>();
        var gentleSpeeds = new List<(float Speed, float ToCorner)>();
        var sharpCorner = sharp.Path[0];
        var gentleCorner = gentle.Path[0];
        for (var i = 0; i < 20 * 12; i++)
        {
            var (s, g) = (sharp.Position, gentle.Position);
            world.Step();
            sharpSpeeds.Add((Vector2.Distance(s, sharp.Position) / Dt, Vector2.Distance(sharp.Position, sharpCorner)));
            gentleSpeeds.Add((Vector2.Distance(g, gentle.Position) / Dt, Vector2.Distance(gentle.Position, gentleCorner)));
        }

        // The sharp corner is a rest point: it stops on it (and turns there).
        Assert.Contains(sharpSpeeds, s => s.Speed == 0 && s.ToCorner < 0.01f);
        // Through the gentle one it keeps its speed.
        Assert.All(gentleSpeeds.Where(s => s.ToCorner < 3), s => Assert.True(s.Speed > 14, $"{s.Speed} m/s {s.ToCorner} m from the corner"));
        // And it turns to the new way over the last two metres before it.
        Assert.Equal(30 * Degrees, gentle.Facing, 2);
    }

    [Fact]
    public void It_comes_to_rest_at_least_every_six_stretches()
    {
        var world = NewMatch();
        var at = OpenGround(world);
        var grunt = world.AddMachine(Race.Red, at - new Vector2(100, 0), "AGGRESSOR", "GRUNT", 1, "R_BOLTER");
        // Eight stretches of 25 m in a straight line.
        var points = Enumerable.Range(1, 8).Select(i => grunt.Position + new Vector2(25 * i, 0)).ToArray();
        SendAlong(grunt, points);

        var speeds = new List<(float Along, float Speed)>();
        for (var i = 0; i < 20 * 30 && grunt.Order is MoveOrder; i++)
        {
            var before = grunt.Position;
            world.Step();
            speeds.Add((grunt.Position.X - points[0].X + 25, Vector2.Distance(before, grunt.Position) / Dt));
        }

        // Six stretches, then a rest at the sixth point (150 m), then on to the end; no rest at the points before.
        Assert.True(speeds.Where(s => MathF.Abs(s.Along - 150) < 3).Min(s => s.Speed) < 2);
        Assert.True(speeds.Where(s => MathF.Abs(s.Along - 100) < 3).Min(s => s.Speed) > 14);
        Assert.True(Vector2.Distance(points[^1], grunt.Position) < 0.01f);
    }

    [Fact]
    public void A_stop_order_stops_dead()
    {
        var world = NewMatch();
        var at = OpenGround(world);
        var grunt = world.AddMachine(Race.Red, at - new Vector2(100, 0), "AGGRESSOR", "GRUNT", 1, "R_BOLTER");
        world.Issue(new MoveCommand(Race.Red, [grunt.Id], at + new Vector2(100, 0)));
        for (var i = 0; i < 40; i++)
            world.Step();

        world.Issue(new StopCommand(Race.Red, [grunt.Id]));
        var stoppedAt = grunt.Position;
        world.Step();
        // No way to slow down: it stands where the order found it.
        Assert.Equal(stoppedAt, grunt.Position);
        world.Step();
        Assert.Equal(stoppedAt, grunt.Position);
    }

    [Fact]
    public void Turning_to_face_a_target_takes_its_time()
    {
        var world = NewMatch();
        var at = OpenGround(world);
        // BODY_TURN_LINK none for a level-1 Grunt: it turns its whole self to the target.
        var grunt = world.AddMachine(Race.Red, at, "AGGRESSOR", "GRUNT", 1, "R_BOLTER");
        var target = world.AddMachine(Race.Blue, at + new Vector2(0, 20), "CONSTRUCTOR", "DOZER", 1, null);
        world.Issue(new AttackCommand(Race.Red, [grunt.Id], target.Id));

        for (var i = 0; i < 20; i++)
            world.Step();
        Assert.InRange(grunt.Facing, 30 * Degrees, 60 * Degrees);
        for (var i = 0; i < 25; i++)
            world.Step();
        Assert.Equal(MathF.PI / 2, grunt.Facing, 3);
    }

    [Fact]
    public void Locators_drive_at_half_speed_while_locating()
    {
        var world = NewMatch();
        var at = OpenGround(world);
        var locating = world.AddMachine(Race.Red, at - new Vector2(100, 20), "GEO_LOCATOR", null, 1, null);
        var moving = world.AddMachine(Race.Red, at - new Vector2(100, -20), "GEO_LOCATOR", null, 1, null);
        world.Issue(new LocateCommand(Race.Red, [locating.Id], locating.Position + new Vector2(200, 0)));
        world.Issue(new MoveCommand(Race.Red, [moving.Id], moving.Position + new Vector2(200, 0)));

        var top = (Locating: 0f, Moving: 0f);
        for (var i = 0; i < 20 * 8; i++)
        {
            var (l, m) = (locating.Position, moving.Position);
            world.Step();
            top = (MathF.Max(top.Locating, Vector2.Distance(l, locating.Position) / Dt), MathF.Max(top.Moving, Vector2.Distance(m, moving.Position) / Dt));
        }

        Assert.Equal(locating.Stats.Speed, top.Moving, 1);
        Assert.Equal(locating.Stats.Speed / 2, top.Locating, 1);
    }

    // ---- first person ----------------------------------------------------------------------------

    /// <summary>Drives a machine in first person for a while, as the client sends input every tick.</summary>
    private static void DriveFor(GameWorld world, Machine machine, float seconds, float forward = 0, float turn = 0, float strafe = 0, bool slow = false)
    {
        for (var i = 0; i < seconds * GameWorld.TicksPerSecond; i++)
        {
            world.Issue(new DriveCommand(machine.Race, machine.Id, Driving: true, forward, turn, Strafe: strafe, Slow: slow));
            world.Step();
        }
    }

    [Fact]
    public void A_driven_machine_speeds_up_and_coasts_to_a_halt()
    {
        var world = NewMatch();
        var at = OpenGround(world);
        var grunt = world.AddMachine(Race.Red, at - new Vector2(100, 0), "AGGRESSOR", "GRUNT", 1, "R_BOLTER");

        // Ten ticks at 19 m/s²: ½·19·0.5² = 2.375 m.
        DriveFor(world, grunt, 0.5f, forward: 1);
        Assert.Equal(2.375f, grunt.Position.X - (at.X - 100), 2);
        DriveFor(world, grunt, 2, forward: 1);
        var full = grunt.Position;
        // Let go at 15 m/s, it rolls on at 19 m/s² down: 15²/(2·19) = 5.9 m.
        DriveFor(world, grunt, 2);
        Assert.Equal(15f * 15 / (2 * 19), grunt.Position.X - full.X, 0);
    }

    [Fact]
    public void Reverse_is_a_quarter_of_top_speed()
    {
        var world = NewMatch();
        var at = OpenGround(world);
        var grunt = world.AddMachine(Race.Red, at, "AGGRESSOR", "GRUNT", 1, "R_BOLTER");
        DriveFor(world, grunt, 2, forward: -1);
        var before = grunt.Position;
        DriveFor(world, grunt, 1, forward: -1);
        Assert.Equal(15f / 4, before.X - grunt.Position.X, 2);
    }

    [Fact]
    public void Turning_is_capped_by_rotation_speed_and_slow_with_ctrl()
    {
        var world = NewMatch();
        var at = OpenGround(world);
        var grunt = world.AddMachine(Race.Red, at - new Vector2(40, 0), "AGGRESSOR", "GRUNT", 1, "R_BOLTER");
        var behemoth = world.AddMachine(Race.Red, at + new Vector2(40, 0), "CONSTRUCTOR", "BEHEMOTH", 4, null);
        var rate = behemoth.Stats.RotationSpeed;
        Assert.True(rate < 45);

        world.Issue(new DriveCommand(Race.Red, behemoth.Id, Driving: true, Turn: 1));
        DriveFor(world, grunt, 1, turn: 1);
        // The grunt turns at the first-person cap (45°/s), the behemoth at its own slower ROTATION_SPEED.
        Assert.Equal(45 * Degrees, grunt.Facing, 3);
        DriveFor(world, grunt, 1, turn: -1, slow: true);
        // With Ctrl, at the slow cap: 10°/s.
        Assert.Equal(35 * Degrees, grunt.Facing, 3);
        // Its input holds until the next command: two seconds of turning.
        Assert.Equal(2 * rate * Degrees, behemoth.Facing, 3);
    }

    [Fact]
    public void Uphill_is_slower_downhill_faster()
    {
        var world = NewMatch();
        var at = OpenGround(world);
        // A plain that rises one in five eastwards.
        var heights = new float[400, 400];
        for (var x = 0; x < 400; x++)
            for (var y = 0; y < 400; y++)
                heights[x, y] = x * 4 * 0.2f;
        world.Terrain = new Machines.Sim.Navigation.TerrainHeights(heights, 4);
        var up = world.AddMachine(Race.Red, at - new Vector2(60, 20), "AGGRESSOR", "GRUNT", 1, "R_BOLTER");
        var down = world.AddMachine(Race.Red, at + new Vector2(60, 20), "AGGRESSOR", "GRUNT", 1, "R_BOLTER");
        down.Facing = MathF.PI;
        world.Issue(new DriveCommand(Race.Red, down.Id, Driving: true, 1));
        DriveFor(world, up, 3, forward: 1);
        var (u, d) = (up.Position, down.Position);
        world.Issue(new DriveCommand(Race.Red, down.Id, Driving: true, 1));
        DriveFor(world, up, 1, forward: 1);

        Assert.Equal(15 * 0.8f, up.Position.X - u.X, 1);
        Assert.Equal(15 * 1.2f, d.X - down.Position.X, 1);
    }

    [Fact]
    public void A_legged_machine_strafes_and_tracks_do_not()
    {
        var world = NewMatch();
        var at = OpenGround(world);
        var assassin = world.AddMachine(Race.Red, at - new Vector2(30, 0), "AGGRESSOR", "ASSASSIN", 3, null);
        var grunt = world.AddMachine(Race.Red, at + new Vector2(30, 0), "AGGRESSOR", "GRUNT", 2, "L_BOLTER");
        Assert.True(assassin.CanStrafe);
        Assert.False(grunt.CanStrafe);
        var (a, g) = (assassin.Position, grunt.Position);

        world.Issue(new DriveCommand(Race.Red, grunt.Id, Driving: true, Strafe: 1));
        DriveFor(world, assassin, 2, strafe: 1);

        // Positive is to the left of where it faces (+x): +y.
        Assert.True(assassin.Position.Y - a.Y > 5, $"strafed only to {assassin.Position}");
        Assert.Equal(a.X, assassin.Position.X, 3);
        Assert.Equal(g, grunt.Position);
        Assert.Equal(0, assassin.Facing);
    }

    [Fact]
    public void Strafe_does_not_beat_top_speed_diagonally()
    {
        var world = NewMatch();
        var at = OpenGround(world);
        var assassin = world.AddMachine(Race.Red, at, "AGGRESSOR", "ASSASSIN", 3, null);
        DriveFor(world, assassin, 3, forward: 1, strafe: 1);
        var before = assassin.Position;
        DriveFor(world, assassin, 1, forward: 1, strafe: 1);
        Assert.InRange(Vector2.Distance(before, assassin.Position), assassin.Stats.Speed - 0.05f, assassin.Stats.Speed + 0.01f);
    }

    [Fact]
    public void A_driven_machine_stops_at_another()
    {
        var world = NewMatch();
        var at = OpenGround(world);
        var grunt = world.AddMachine(Race.Red, at - new Vector2(30, 0), "AGGRESSOR", "GRUNT", 1, "R_BOLTER");
        var dozer = world.AddMachine(Race.Red, at, "CONSTRUCTOR", "DOZER", 1, null);
        DriveFor(world, grunt, 5, forward: 1);

        // It neither drives through the other nor pushes it.
        Assert.True(Vector2.Distance(grunt.Position, dozer.Position) >= grunt.Radius + dozer.Radius, $"at {grunt.Position}");
        Assert.True(Vector2.Distance(grunt.Position, dozer.Position) < grunt.Radius + dozer.Radius + 1, $"stopped short at {grunt.Position}");
        Assert.Equal(at, dozer.Position);
    }

    [Fact]
    public void Machines_stop_for_a_driven_machine_and_never_ask_it_to_make_way()
    {
        var world = NewMatch();
        var at = OpenGround(world);
        var driven = world.AddMachine(Race.Red, at, "AGGRESSOR", "GRUNT", 1, "R_BOLTER");
        var dozer = world.AddMachine(Race.Red, at - new Vector2(40, 0), "CONSTRUCTOR", "DOZER", 1, null);
        world.Issue(new MoveCommand(Race.Red, [dozer.Id], at));
        var closest = float.MaxValue;
        for (var i = 0; i < 20 * 10; i++)
        {
            world.Issue(new DriveCommand(Race.Red, driven.Id, Driving: true));
            world.Step();
            closest = MathF.Min(closest, Vector2.Distance(driven.Position, dozer.Position));
        }

        Assert.Equal(at, driven.Position);
        Assert.True(closest >= driven.Radius + dozer.Radius, $"came within {closest} m");
    }

    [Fact]
    public void Old_recordings_move_as_before()
    {
        var world = NewMatch(new MatchOptions { OriginalMotion = false });
        var at = OpenGround(world);
        var grunt = world.AddMachine(Race.Red, at, "AGGRESSOR", "GRUNT", 1, "R_BOLTER");
        world.Issue(new MoveCommand(Race.Red, [grunt.Id], at + new Vector2(0, 80)));
        world.Step();

        // Recorded before machines moved as in the original: full speed from the first tick, facing its way at once.
        Assert.Equal(grunt.Stats.Speed * Dt, Vector2.Distance(at, grunt.Position), 3);
        Assert.Equal(MathF.PI / 2, grunt.Facing, 3);
    }
}
