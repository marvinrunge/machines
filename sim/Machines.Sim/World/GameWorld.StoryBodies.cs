using System.Numerics;
using Machines.Import.Model;
using Machines.Sim.Story;

namespace Machines.Sim.World;

/// <summary>
/// In a story the machines stand and are hit in their bodies as the story draws them (<see cref="StoryBodies"/>): a box
/// along each one's facing, fitted to its model, instead of the original's circle and the square box round it. Machines
/// stop where their bodies would meet (<see cref="Blocker"/>); a round's line meets a machine where it meets its body
/// (<see cref="FirstOnLine"/>), and what a round's end reaches is measured to the body (<see cref="Burst"/>), where a round
/// that does not fall off with the distance reaches a machine only by <see cref="StorySplash"/>: one that went past a
/// Grunt and struck the wall behind it no longer hurts it. A machine without a measured body keeps its circle and box.
/// The size classes the doors let through still go by the circle (<see cref="Navigation.Mobility"/>). Nothing here runs
/// outside a story.
/// </summary>
public sealed partial class GameWorld
{
    /// <summary>How far the end of a round that does not fall off reaches a machine's body in a story, metres (the original: 1 m for a bolt, 3 m for the rest).</summary>
    public const float StorySplash = 0.5f;

    /// <summary>The steps a move is looked along for bodies in the way, at most this long, metres.</summary>
    private const float BodyStep = 0.5f;

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
    }

    /// <summary>A machine's body in a story, as it stands, or would stand at <paramref name="at"/>; null outside a story or without one.</summary>
    private BodyFrame? StoryBodyOf(Actor actor, Vector2? at = null)
    {
        if (Story is null || actor is not Machine { Stats.Locomotion: not Locomotion.Glider } machine
            || StoryBodies.Of(machine.Spec.Type, machine.Spec.SubType, machine.Stats.HardwareLevel) is not { } body)
            return null;
        var forward = new Vector2(DetMath.Cos(machine.Facing), DetMath.Sin(machine.Facing));
        var position = at ?? machine.Position;
        var ground = Terrain?.At(machine.Position) ?? 0;
        return new BodyFrame(position + forward * body.Ahead, forward, body.HalfLength, body.Half, ground, ground + body.Height);
    }

    /// <summary>How far a machine's body reaches from its middle in a story, for filing it: at least its radius.</summary>
    private float StoryReach(Actor actor) =>
        Story is not null && actor is Machine machine && StoryBodies.Of(machine.Spec.Type, machine.Spec.SubType, machine.Stats.HardwareLevel) is { } body
            ? MathF.Max(actor.Radius, body.Reach)
            : actor.Radius;

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
    /// Whether a machine moving from <paramref name="from"/> to <paramref name="to"/> runs into another's body on the way, both
    /// in a story with bodies; null when either has none (then their circles decide). One whose body it already overlaps where
    /// it stands is passed over, as with the circles, so the two can part.
    /// </summary>
    private bool? BodiesMeet(Machine machine, Machine other, Vector2 from, Vector2 to)
    {
        if (StoryBodyOf(other) is not { } theirs || StoryBodyOf(machine, from) is not { } mine)
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

    /// <summary>Where a straight line first enters a machine's body in a story, as a share of its way; null if it misses or the machine has none.</summary>
    private bool EntryIntoBody(Actor actor, Vector3 from, Vector3 to, out float? along)
    {
        along = null;
        if (StoryBodyOf(actor) is not { } body)
            return false;
        along = EntryInto(body.Local(from), body.Local(to), body.Min, body.Max);
        return true;
    }

    /// <summary>The squared distance from a point to a machine's body in a story; null without one.</summary>
    private float? OffBody(Actor actor, Vector3 point)
    {
        if (StoryBodyOf(actor) is not { } body)
            return null;
        var local = body.Local(point);
        return Vector3.DistanceSquared(local, Vector3.Clamp(local, body.Min, body.Max));
    }
}
