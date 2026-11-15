using System.Numerics;

namespace Machines.Game;

/// <summary>A place on a hero's skin: on which part, in that part's own terms, and which way the skin faces there.</summary>
public readonly record struct HullSpot(int Part, Vector3 At, Vector3 Facing);

/// <summary>
/// Where the hangar's traces end for what has no mount, modules and abilities: on the skin of the hero's body, found by looking
/// at the body from ahead. They belong to the part they lie on, so they turn with it. Free of Godot, so the tests can compile
/// it. Wired is the remake's own addition.
/// </summary>
public static class HeroHull
{
    private const float ModulesUp = 0.36f, AbilitiesUp = 0.72f;

    /// <summary>How many places across and up are tried round a spot that misses.</summary>
    private const int Tries = 13;

    /// <summary>Where the modules lie across the body, the first in the middle, and the abilities over them.</summary>
    private static readonly float[] Modules = [0, 0.36f, -0.36f, 0.68f, -0.68f], Abilities = [0.3f, -0.3f];

    /// <summary>What is no body by its name: the parts of each side, limbs and wheels, begin with r or l, and the rest are these.</summary>
    private static readonly string[] Starts = ["r_", "l_", "r1_", "l1_", "r2_", "l2_", "r3_", "l3_", "fplat", "gun", "recoil", "tail"];

    /// <summary>The part that makes up the hero: the upper body if it has one with a skin, otherwise the largest of the parts that are neither limbs nor weapons.</summary>
    public static int Body(IReadOnlyList<RigShape> rig, int upperBody)
    {
        if (upperBody > 0 && upperBody < rig.Count && Skinned(rig[upperBody]))
            return upperBody;
        var (body, most) = (-1, -1f);
        for (var part = 1; part < rig.Count; part++)
        {
            if (!Hull(rig[part]))
                continue;
            var (low, high) = Bounds(Triangles(rig, part).SelectMany(t => t.Corners));
            var size = (high.X - low.X) * (high.Y - low.Y) * (high.Z - low.Z);
            if (size > most)
                (body, most) = (part, size);
        }
        return body;
    }

    /// <summary>
    /// The hangar's view of a hero left alone, in the model's terms (MachineScreen, HangarScreen): the camera 0.3 up and 0.5 round
    /// from ahead, the turntable turned by -0.28 and swaying 0.2 either way.
    /// </summary>
    private const float HomeElevation = 0.3f, HomeRound = 0.5f - 0.28f, Sway = 0.2f;

    /// <summary>How far a spot's skin turns towards that camera at least, wherever the sway has the hero: from there on its trace shows in full (HangarTraces).</summary>
    private const float LeastSeen = 0.15f;

    /// <summary>How far a spot that had to move keeps from every other, as a share of the body's width: its trace does not end on another's.</summary>
    private const float Apart = 0.12f;

    /// <summary>
    /// The spots of a hero: first <see cref="ModuleSpots"/> for its modules, then those of its abilities. A spot lies where a line
    /// from ahead first meets the body or what stands before it, and where that would miss on the nearest place that does not.
    /// Where that skin is turned away from the hangar's camera, the spot moves on to the nearest place whose skin is turned
    /// towards it, clear of the other spots (the crab's middle module lay on its head's prow, which slopes away below and to
    /// its face's side, the Phantom's under its roof: their traces went out of sight as the hero swayed); where no place has
    /// such skin, it stays.
    /// </summary>
    public static HullSpot[] Spots(IReadOnlyList<RigShape> rig, int upperBody)
    {
        if (Body(rig, upperBody) is not (> 0 and var body))
            return [];
        var own = Triangles(rig, body).ToList();
        var (low, high) = Bounds(own.SelectMany(t => t.Corners));
        var skin = Enumerable.Range(1, rig.Count - 1).Where(p => Hull(rig[p])).SelectMany(p => Triangles(rig, p)).ToList();
        var (middle, half) = ((low.Z + high.Z) / 2, (high.Z - low.Z) / 2);
        var places = Enumerable.Range(0, Tries * Tries).Select(i => (Across: 2f * (i % Tries) / (Tries - 1) - 1, Up: (float)(i / Tries) / (Tries - 1))).ToList();
        // What lines from ahead meet, from the place asked for on to those ever further from it.
        IEnumerable<Hit> Round((float Across, float Up) wanted) => places
            .OrderBy(p => (p.Across - wanted.Across) * (p.Across - wanted.Across) + (p.Up - wanted.Up) * (p.Up - wanted.Up)).Prepend(wanted)
            .Select(p => Cast(rig, skin, middle + p.Across * half, low.Y + p.Up * (high.Y - low.Y))).OfType<Hit>();
        var wanted = Modules.Select(m => (m, ModulesUp)).Concat(Abilities.Select(a => (a, AbilitiesUp))).ToList();
        var first = wanted.Select(w => Round(w).Cast<Hit?>().FirstOrDefault()).ToList();
        var taken = first.OfType<Hit>().Where(h => Seen(h.Facing) >= LeastSeen).Select(h => h.Model).ToList();
        var apart = Apart * (high.Z - low.Z);
        var spots = new HullSpot[wanted.Count];
        for (var i = 0; i < wanted.Count; i++)
        {
            if (first[i] is { } hit && Seen(hit.Facing) >= LeastSeen)
                spots[i] = hit.Spot;
            else if (Round(wanted[i]).Where(h => Seen(h.Facing) >= LeastSeen && taken.All(t => Vector3.Distance(t, h.Model) >= apart)).Cast<Hit?>().FirstOrDefault() is { } moved)
            {
                spots[i] = moved.Spot;
                taken.Add(moved.Model);
            }
            else
                spots[i] = first[i]?.Spot ?? Widest(rig, own, body);
        }
        return spots;
    }

    /// <summary>Where a body too thin for any line from ahead to meet has its spots: the middle of what it has most of, seen from ahead.</summary>
    private static HullSpot Widest(IReadOnlyList<RigShape> rig, List<Triangle> own, int body)
    {
        var widest = own.MaxBy(t => MathF.Abs(Area(t)));
        var centre = (widest.A + widest.B + widest.C) / 3;
        return Cast(rig, [widest], centre.Z, centre.Y)?.Spot ?? new HullSpot(body, Vector3.Zero, Vector3.UnitX);
    }

    /// <summary>A triangle's size seen from ahead, twice over and with a sign.</summary>
    private static float Area(Triangle t) => (t.B.Z - t.A.Z) * (t.C.Y - t.A.Y) - (t.C.Z - t.A.Z) * (t.B.Y - t.A.Y);

    /// <summary>How many of the spots stand for modules.</summary>
    public static int ModuleSpots => Modules.Length;

    private static bool Skinned(RigShape part) => part.Surfaces.Any(s => s.Length >= 3);

    private static bool Hull(RigShape part) => Skinned(part) && !part.Name.EndsWith(".cdf", StringComparison.OrdinalIgnoreCase)
        && !Starts.Any(start => part.Name.StartsWith(start, StringComparison.OrdinalIgnoreCase));

    private readonly record struct Triangle(int Part, Vector3 A, Vector3 B, Vector3 C)
    {
        public IEnumerable<Vector3> Corners => [A, B, C];
    }

    private static IEnumerable<Triangle> Triangles(IReadOnlyList<RigShape> rig, int part)
    {
        var place = HeroSeat.World(rig, part);
        foreach (var surface in rig[part].Surfaces)
            for (var i = 0; i + 2 < surface.Length; i += 3)
                yield return new Triangle(part, Vector3.Transform(surface[i], place), Vector3.Transform(surface[i + 1], place), Vector3.Transform(surface[i + 2], place));
    }

    private static (Vector3 Low, Vector3 High) Bounds(IEnumerable<Vector3> points)
    {
        var (low, high) = (new Vector3(float.MaxValue), new Vector3(float.MinValue));
        foreach (var point in points)
            (low, high) = (Vector3.Min(low, point), Vector3.Max(high, point));
        return (low, high);
    }

    /// <summary>
    /// How far skin facing that way, in the model's terms, turns towards the hangar's camera at least while the hero sways at
    /// home: 1 straight at it, below 0 turned away. The sway is short, so the least lies at one of its ends.
    /// </summary>
    private static float Seen(Vector3 facing) => MathF.Min(Towards(facing, HomeRound - Sway), Towards(facing, HomeRound + Sway));

    private static float Towards(Vector3 facing, float round) => Vector3.Dot(facing,
        new Vector3(MathF.Cos(HomeElevation) * MathF.Cos(round), MathF.Sin(HomeElevation), MathF.Cos(HomeElevation) * MathF.Sin(round)));

    /// <summary>A spot where a line from ahead meets the skin, which way that skin faces and where it lies, both in the model's terms.</summary>
    private readonly record struct Hit(HullSpot Spot, Vector3 Facing, Vector3 Model);

    /// <summary>The first skin a line from far ahead meets at <paramref name="z"/> across and <paramref name="y"/> up, its facing turned towards the front.</summary>
    private static Hit? Cast(IReadOnlyList<RigShape> rig, List<Triangle> skin, float z, float y)
    {
        var (nearest, hit) = (float.MinValue, default(Triangle?));
        foreach (var triangle in skin)
        {
            var (a, b, c) = (triangle.A, triangle.B, triangle.C);
            var area = Area(triangle);
            if (MathF.Abs(area) < 1e-6f)
                continue;
            var u = ((z - a.Z) * (c.Y - a.Y) - (c.Z - a.Z) * (y - a.Y)) / area;
            var v = ((b.Z - a.Z) * (y - a.Y) - (z - a.Z) * (b.Y - a.Y)) / area;
            if (u < 0 || v < 0 || u + v > 1)
                continue;
            var x = a.X + u * (b.X - a.X) + v * (c.X - a.X);
            if (x > nearest)
                (nearest, hit) = (x, triangle);
        }
        if (hit is not { } skinHit || !Matrix4x4.Invert(HeroSeat.World(rig, skinHit.Part), out var back))
            return null;
        var facing = Vector3.Normalize(Vector3.Cross(skinHit.B - skinHit.A, skinHit.C - skinHit.A));
        if (facing.X < 0)
            facing = -facing;
        var at = new Vector3(nearest, y, z);
        return new Hit(new HullSpot(skinHit.Part, Vector3.Transform(at, back), Vector3.Normalize(Vector3.TransformNormal(facing, back))), facing, at);
    }
}
