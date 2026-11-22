using System.Numerics;

namespace Machines.Sim.World;

/// <summary>
/// Where the match's options say <see cref="MatchOptions.FittedBodies"/>, machines are hit in their bodies
/// (<see cref="MachineBodies"/>): a box along each one's facing, fitted to its model, instead of the original's circle and
/// the square box round it. A round's line meets a machine where it meets its body (<see cref="FirstOnLine"/>), what a
/// round's end reaches is measured to the body (<see cref="Burst"/>) and a sight is taken into it (<see cref="AimInside"/>);
/// rounds are aimed where they were (<see cref="AimPoint"/>), which lies inside every body. Whether a weapon's arc and
/// tilt take a machine in (<see cref="InArc"/>) and whom treachery's rounds go for go by the box round its body on the
/// map's axes (<see cref="BoxOf"/>).
/// <para>
/// A machine driven in first person stops where its body would meet another's (<see cref="BodiesBlock"/>): it drives up to
/// a Grunt's nose and stops at its tail. Machines on orders keep standing in their circles: the original's motion
/// sequencer (<see cref="MatchOptions.OriginalMotion"/>) places them, keeps them in formation, makes way and steps aside
/// by the circle, and a body that turns sweeps round where a circle does not, so with bodies in its way groups drove into
/// one another and stuck at places measured for circles. Only where two circles already overlap but their bodies do not,
/// as when a first-person driver stopped nose to nose with another, does one on an order stop where the bodies would meet
/// instead of passing the other over and driving through it (<see cref="Blocker"/>). A flyer, and a machine without a body, keep their circle and box;
/// how far weapons reach and which doors a machine fits through go by the circle as before.
/// </para>
/// <para>
/// A story always plays in bodies, a few of its cast in the story's own, and in it every machine, on an order or not,
/// stops where its body would meet another's (GameWorld.StoryBodies).
/// </para>
/// </summary>
public sealed partial class GameWorld
{
    /// <summary>The steps a move is looked along for bodies in the way, at most this long, metres.</summary>
    private const float BodyStep = 0.5f;

    /// <summary>Whether machines are hit in their fitted bodies in this match: where its options say so, and always in a story (GameWorld.StoryBodies).</summary>
    private bool FittedBodies => Options.FittedBodies || Story is not null;

    /// <summary>Whether a machine driven in first person (<see cref="DriveAsTheOriginal"/>) stops where its body would meet another's.</summary>
    private bool BodiesBlock(Machine machine) => FittedBodies;

    /// <summary>A body where it stands: its middle on the ground, its facing, half its length and width, its foot and top.</summary>
    internal readonly record struct BodyFrame(Vector2 Centre, Vector2 Forward, float HalfLength, float Half, float Bottom, float Top)
    {
        public Vector2 Left => new(-Forward.Y, Forward.X);

        /// <summary>A point in the body's own axes: along its facing, across to its left, up.</summary>
        public Vector3 Local(Vector3 point)
        {
            var off = new Vector2(point.X, point.Y) - Centre;
            return new Vector3(Vector2.Dot(off, Forward), Vector2.Dot(off, Left), point.Z);
        }

        public Vector3 Min => new(-HalfLength, -Half, Bottom);

        public Vector3 Max => new(HalfLength, Half, Top);

        /// <summary>The box round it on the map's axes.</summary>
        public (Vector3 Min, Vector3 Max) Bounds()
        {
            var across = new Vector2(
                HalfLength * MathF.Abs(Forward.X) + Half * MathF.Abs(Left.X),
                HalfLength * MathF.Abs(Forward.Y) + Half * MathF.Abs(Left.Y));
            return (new Vector3(Centre - across, Bottom), new Vector3(Centre + across, Top));
        }
    }

    /// <summary>A machine's body as it stands, or would stand at <paramref name="at"/>; null without fitted bodies or one of its own.</summary>
    internal BodyFrame? FittedBodyOf(Actor actor, Vector2? at = null)
    {
        if (!FittedBodies || actor is not Machine machine || FittedBodyFor(machine) is not { } body)
            return null;
        var forward = new Vector2(DetMath.Cos(machine.Facing), DetMath.Sin(machine.Facing));
        var position = at ?? machine.Position;
        var ground = Terrain?.At(machine.Position) ?? 0;
        return new BodyFrame(position + forward * body.Ahead, forward, body.HalfLength, body.Half, ground, ground + body.Height);
    }

    /// <summary>How far an actor reaches from its middle, for filing it: its radius, or its body's corners where they reach further.</summary>
    private float ReachOf(Actor actor) =>
        FittedBodies && actor is Machine machine && FittedBodyFor(machine) is { } body ? MathF.Max(actor.Radius, body.Reach) : actor.Radius;

    /// <summary>Whether two bodies overlap, flat on the ground (the separating axes of two boxes).</summary>
    internal static bool Overlap(in BodyFrame a, in BodyFrame b)
    {
        var between = b.Centre - a.Centre;
        foreach (var axis in (ReadOnlySpan<Vector2>)[a.Forward, a.Left, b.Forward, b.Left])
        {
            var reachA = a.HalfLength * MathF.Abs(Vector2.Dot(a.Forward, axis)) + a.Half * MathF.Abs(Vector2.Dot(a.Left, axis));
            var reachB = b.HalfLength * MathF.Abs(Vector2.Dot(b.Forward, axis)) + b.Half * MathF.Abs(Vector2.Dot(b.Left, axis));
            if (MathF.Abs(Vector2.Dot(between, axis)) >= reachA + reachB)
                return false;
        }
        return true;
    }

    /// <summary>
    /// Whether a machine moving from <paramref name="from"/> to <paramref name="to"/> runs into another's body on the way;
    /// null when either has none (then their circles decide). One whose body it already overlaps where it stands is passed
    /// over, as with the circles, so the two can part.
    /// </summary>
    private bool? BodiesMeet(Machine machine, Machine other, Vector2 from, Vector2 to)
    {
        if (FittedBodyOf(other) is not { } theirs || FittedBodyOf(machine, from) is not { } mine)
            return null;
        if (Overlap(mine, theirs))
            return false;
        var way = to - from;
        var steps = Math.Max(1, (int)MathF.Ceiling(way.Length() / BodyStep));
        for (var k = 1; k <= steps; k++)
        {
            if (Overlap(mine with { Centre = mine.Centre + way * ((float)k / steps) }, theirs))
                return true;
        }
        return false;
    }

    /// <summary>Where a straight line first enters a machine's body, as a share of its way; false if it has none (then its box decides).</summary>
    private bool EntryIntoBody(Actor actor, Vector3 from, Vector3 to, out float? along)
    {
        along = null;
        if (FittedBodyOf(actor) is not { } body)
            return false;
        along = EntryInto(body.Local(from), body.Local(to), body.Min, body.Max);
        return true;
    }

    /// <summary>The squared distance from a point to a machine's body; null without one.</summary>
    private float? OffBody(Actor actor, Vector3 point)
    {
        if (FittedBodyOf(actor) is not { } body)
            return null;
        var local = body.Local(point);
        return Vector3.DistanceSquared(local, Vector3.Clamp(local, body.Min, body.Max));
    }
}
