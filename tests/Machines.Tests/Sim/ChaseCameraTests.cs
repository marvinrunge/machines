using System.Numerics;
using Machines.Game;

namespace Machines.Tests.Sim;

/// <summary>
/// The third-person camera of a sighted hero (ChaseCamera): it stands behind and above the hero on the line of the view's
/// heading through the hero's pivot, so whatever lies under the crosshair beyond the hero lies along the heading the commands
/// carry; walls and hills shorten its arm, never below the shortest, and it keeps over the ground.
/// </summary>
public class ChaseCameraTests
{
    private const float Degree = MathF.PI / 180;

    private static readonly Vector3 Pivot = new(500, 30, 400);

    private static float Wrap(float angle) => MathF.IEEERemainder(angle, 2 * MathF.PI);

    /// <summary>The heading from the hero's pivot to a point, radians anticlockwise as the simulation's (Godot's z is its y).</summary>
    private static float Heading(Vector3 from, Vector3 to) => MathF.Atan2(to.Z - from.Z, to.X - from.X);

    [Theory]
    [InlineData(0f, -6f, 10f)]
    [InlineData(1.3f, -35f, 8.2f)]
    [InlineData(-2.9f, 20f, 33.8f)]
    [InlineData(3.1f, 0f, 4f)]
    public void Every_point_under_the_crosshair_beyond_the_hero_lies_along_the_heading(float yaw, float pitch, float arm)
    {
        var p = pitch * Degree;
        // Ground below, then a hill behind that shortens it and a floor that lifts it: the line of the heading holds in each.
        Func<float, float, float>?[] grounds = [null, (_, _) => 0, (x, z) => Vector3.Dot(new Vector3(x, 0, z) - Pivot, -ChaseCamera.Ahead(yaw, 0)) > 3 ? 40 : 0,
            (_, _) => 29.5f];
        foreach (var ground in grounds)
        {
            var spot = ChaseCamera.Place(Pivot, yaw, p, arm, 2, float.MaxValue, ground);
            var ahead = ChaseCamera.Ahead(yaw, p);
            var start = ChaseCamera.AimFrom(spot.Camera, ahead, Pivot);
            foreach (var far in new[] { 0.5f, 5f, 40f, 300f })
            {
                var point = spot.Camera + ahead * (start + far);
                Assert.True(MathF.Abs(Wrap(Heading(Pivot, point) - yaw)) < 1e-3f, $"heading {Heading(Pivot, point)} for yaw {yaw}, {far} m on");
            }
            // The camera stands behind the pivot on that line (raised straight up, if at all), and the aim begins abreast of the pivot.
            Assert.True(MathF.Abs(Wrap(Heading(spot.Camera, Pivot) - yaw)) < 1e-3f);
            Assert.Equal(spot.Arm, start, 3);
        }
    }

    [Fact]
    public void The_camera_stands_its_arm_behind_and_above_the_pivot_by_the_pitch()
    {
        var spot = ChaseCamera.Place(Pivot, 0, ChaseCamera.StartPitch, 10, 3, float.MaxValue, null);
        Assert.Equal(10, spot.Arm);
        Assert.Equal(0, spot.Raised);
        Assert.Equal(Pivot.X - 10 * MathF.Cos(ChaseCamera.StartPitch), spot.Camera.X, 3);
        Assert.Equal(Pivot.Y + 10 * MathF.Sin(-ChaseCamera.StartPitch), spot.Camera.Y, 3);
        Assert.Equal(Pivot.Z, spot.Camera.Z, 3);
    }

    [Fact]
    public void A_building_behind_shortens_the_arm_but_not_below_the_shortest()
    {
        Assert.Equal(6, ChaseCamera.Place(Pivot, 0, 0, 10, 3, 6, null).Arm);
        Assert.Equal(3, ChaseCamera.Place(Pivot, 0, 0, 10, 3, 1, null).Arm);
    }

    [Fact]
    public void A_hill_behind_the_hero_brings_the_camera_in_before_it()
    {
        // The ground rises to 40 m from 5 m behind the pivot (at 30 m): the arm ends half a metre short of where it first comes within 1.5 m.
        float Ground(float x, float z) => x < Pivot.X - 5 ? 40 : 0;
        var spot = ChaseCamera.Place(Pivot, 0, 0, 12, 2, float.MaxValue, Ground);
        Assert.Equal(5, spot.Arm);
        Assert.True(spot.Camera.X >= Pivot.X - 5);
    }

    [Fact]
    public void The_arm_comes_in_before_the_ground_and_at_its_shortest_the_camera_is_raised_straight_up()
    {
        // Looking up, the arm goes down behind the hero towards ground at 25 m: it ends before it comes within 1.5 m of it.
        var pitch = ChaseCamera.MostUp;
        var lower = ChaseCamera.Place(Pivot, 0.4f, pitch, 12, 2, float.MaxValue, null, _ => 25);
        Assert.Equal(0, lower.Raised);
        Assert.True(lower.Arm < 12 && lower.Camera.Y >= 25 + ChaseCamera.Floor, $"arm {lower.Arm}, camera at {lower.Camera.Y}");
        // Ground a metre under the pivot: the arm is at its shortest at once, and the camera stands 1.5 m over the ground.
        var low = ChaseCamera.Place(Pivot, 0.4f, pitch, 12, 2, float.MaxValue, null, _ => 29);
        var free = ChaseCamera.Place(Pivot, 0.4f, pitch, 2, 2, float.MaxValue, null);
        Assert.Equal(2, low.Arm);
        Assert.True(low.Raised > 0);
        Assert.Equal(29 + ChaseCamera.Floor, low.Camera.Y, 3);
        Assert.Equal(free.Camera.X, low.Camera.X, 4);
        Assert.Equal(free.Camera.Z, low.Camera.Z, 4);
    }

    [Fact]
    public void The_arm_and_the_pivot_grow_with_the_hero()
    {
        // A Grunt 1 (radius 1.5) and a Titan (about 8.8): the battle watch camera's arm, the pivot a quarter of it up.
        Assert.Equal(8.25f, ChaseCamera.Arm(1.5f, 1), 3);
        Assert.Equal(8.25f * 1.4f, ChaseCamera.Arm(1.5f, 1.4f), 3);
        Assert.True(ChaseCamera.Arm(8.8f, 1) > 30);
        Assert.Equal(2.5f, ChaseCamera.Lift(10), 4);
        Assert.Equal(3.3f, ChaseCamera.ShortestArm(1.5f), 4);
        Assert.True(ChaseCamera.ShortestArm(1.5f) > 1.3f * 1.5f, "the shortest arm leaves the camera outside a level hero");
    }

    [Fact]
    public void The_pitch_keeps_within_its_limits()
    {
        Assert.Equal(ChaseCamera.MostDown, ChaseCamera.Pitch(-80 * Degree));
        Assert.Equal(ChaseCamera.MostUp, ChaseCamera.Pitch(80 * Degree));
        Assert.Equal(-10 * Degree, ChaseCamera.Pitch(-10 * Degree));
    }

    [Fact]
    public void A_camera_close_to_the_hero_leaves_it_out_and_the_spread_shows_narrower_from_behind()
    {
        Assert.True(ChaseCamera.InsideHero(new Vector3(1, 0, 0), Vector3.Zero, 1.5f));
        Assert.False(ChaseCamera.InsideHero(new Vector3(3.3f, 0, 0), Vector3.Zero, 1.5f));
        Assert.Equal(20 / 30f, ChaseCamera.SpreadShare(20, 30), 4);
        Assert.Equal(1, ChaseCamera.SpreadShare(20, 0));
    }
}
