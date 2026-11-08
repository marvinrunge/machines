using System.Numerics;
using Machines.Sim.Combat;
using Machines.Sim.Survival;

namespace Machines.Game;

// Where a Wired hero's weapons sit on a chassis they were not made for. Like HeroParts, this knows nothing of Godot, so
// the tests can run it over the real rigs in every look. Wired is the remake's own addition.

/// <summary>
/// A part of a rig as seating a weapon needs it: what it hangs from, where it rests (for row vectors, so a child's comes
/// first) and the corners of its triangles, three by three, for each surface of its mesh; none for one that does not count.
/// </summary>
public sealed record RigShape(int Parent, string Name, Matrix4x4 Rest, Vector3[][] Surfaces);

/// <summary>A weapon going on a chassis: the slot, the rig it comes from and its mount there.</summary>
public sealed record HeroWorn(Mounting Side, string Weapon, HeroDonor Donor, IReadOnlyList<RigShape> Rig, int Mount);

/// <summary>What of a part's mesh a chassis shows on its other side too, each surface's triangles by number (see <see cref="HeroSeat.Shoulder"/>).</summary>
public sealed record HeroShoulder(int Part, int[][] Triangles);

/// <summary>The room something takes on a mount, in the mount's own axes as the rigs have them: x forward, y up, z to the machine's right.</summary>
public readonly record struct MountBox(float MinX, float MaxX, float MinY, float MaxY, float MinZ, float MaxZ)
{
    /// <summary>The same room on the other side of the mount.</summary>
    public MountBox Across => this with { MinZ = -MaxZ, MaxZ = -MinZ };

    public static MountBox operator *(MountBox box, float scale) =>
        new(box.MinX * scale, box.MaxX * scale, box.MinY * scale, box.MaxY * scale, box.MinZ * scale, box.MaxZ * scale);

    /// <summary>The room some points take, or null with none.</summary>
    public static MountBox? Of(IReadOnlyCollection<Vector3> points) => points.Count == 0 ? null
        : new MountBox(points.Min(p => p.X), points.Max(p => p.X), points.Min(p => p.Y), points.Max(p => p.Y), points.Min(p => p.Z), points.Max(p => p.Z));
}

/// <summary>
/// A machine's body in cells of 10 cm: what it fills, and how far the rest lies from its skin. The meshes are open where
/// nobody looks, so a cell counts as inside with skin on at least five of its six sides.
/// </summary>
public sealed class HeroSolid
{
    public const float Cell = 0.1f;

    // The air kept round the body, in cells: as far as a distance is known.
    private const int Air = 14;
    private const byte Unknown = 255;

    private readonly Vector3 _origin;
    private readonly int _wide, _high, _deep;
    private readonly bool[] _inside;
    private readonly bool[] _column;
    private readonly byte[] _gap;

    public HeroSolid(IEnumerable<Vector3[]> surfaces)
    {
        var stamps = Spread(surfaces, Cell / 2);
        if (stamps.Count == 0)
            stamps.Add(Vector3.Zero);
        var (low, high) = (stamps.Aggregate(Vector3.Min), stamps.Aggregate(Vector3.Max));
        _origin = low - new Vector3(Air * Cell);
        var cells = (high - low) / Cell + new Vector3(2 * Air + 1);
        (_wide, _high, _deep) = ((int)MathF.Ceiling(cells.X), (int)MathF.Ceiling(cells.Y), (int)MathF.Ceiling(cells.Z));
        var size = _wide * _high * _deep;
        _inside = new bool[size];
        _gap = new byte[size];
        Array.Fill(_gap, Unknown);

        var edge = new Queue<int>();
        foreach (var stamp in stamps)
            if (Index(stamp) is >= 0 and var at && _gap[at] != 0)
            {
                _gap[at] = 0;
                edge.Enqueue(at);
            }

        var sides = new byte[size];
        void Look(int first, int stride, int count)
        {
            bool ahead = false, behind = false;
            for (var k = 0; k < count; k++)
            {
                var (near, far) = (first + k * stride, first + (count - 1 - k) * stride);
                ahead |= _gap[near] == 0;
                behind |= _gap[far] == 0;
                sides[near] += ahead ? (byte)1 : (byte)0;
                sides[far] += behind ? (byte)1 : (byte)0;
            }
        }
        for (var x = 0; x < _wide; x++)
            for (var y = 0; y < _high; y++)
                Look((x * _high + y) * _deep, 1, _deep);
        for (var x = 0; x < _wide; x++)
            for (var z = 0; z < _deep; z++)
                Look(x * _high * _deep + z, _deep, _high);
        for (var y = 0; y < _high; y++)
            for (var z = 0; z < _deep; z++)
                Look(y * _deep + z, _high * _deep, _wide);
        _column = new bool[_wide * _deep];
        for (var i = 0; i < size; i++)
        {
            _inside[i] = sides[i] >= 5;
            _column[i / (_high * _deep) * _deep + i % _deep] |= _inside[i];
        }

        // Outwards from the skin, a cell at a time.
        int[] ways = [1, -1, _deep, -_deep, _high * _deep, -_high * _deep];
        while (edge.TryDequeue(out var at))
            foreach (var way in ways)
                if (_gap[at] < Air && at + way is >= 0 and var next && next < size && _gap[next] == Unknown)
                {
                    _gap[next] = (byte)(_gap[at] + 1);
                    edge.Enqueue(next);
                }
    }

    private int Index(Vector3 point)
    {
        var cell = (point - _origin) / Cell;
        var (x, y, z) = ((int)MathF.Floor(cell.X), (int)MathF.Floor(cell.Y), (int)MathF.Floor(cell.Z));
        return x < 0 || y < 0 || z < 0 || x >= _wide || y >= _high || z >= _deep ? -1 : (x * _high + y) * _deep + z;
    }

    /// <summary>Whether a point lies in the body or on its skin.</summary>
    public bool Holds(Vector3 point) => Index(point) is >= 0 and var at && _inside[at];

    /// <summary>Whether the body lies straight above or below a point.</summary>
    public bool Covers(Vector3 point)
    {
        var cell = (point - _origin) / Cell;
        var (x, z) = ((int)MathF.Floor(cell.X), (int)MathF.Floor(cell.Z));
        return x >= 0 && z >= 0 && x < _wide && z < _deep && _column[x * _deep + z];
    }

    /// <summary>How far a point lies from the skin; out in the open it does not matter how far.</summary>
    public float Gap(Vector3 point) => Index(point) is >= 0 and var at && _gap[at] != Unknown ? _gap[at] * Cell : float.PositiveInfinity;

    /// <summary>How far along <paramref name="axis"/> from a point the body ends, seen from that side; null if nothing is there.</summary>
    public float? Skin(Vector3 point, Vector3 axis)
    {
        var along = Vector3.Normalize(axis);
        for (var d = 12f; d > -12; d -= Cell / 2)
            if (Holds(point + along * d))
                return d;
        return null;
    }

    /// <summary>Points spread evenly over surfaces, one to each square of <paramref name="step"/>.</summary>
    public static List<Vector3> Spread(IEnumerable<Vector3[]> surfaces, float step)
    {
        var points = new List<Vector3>();
        var owed = 0f;
        foreach (var corners in surfaces)
            for (var t = 0; t + 2 < corners.Length; t += 3)
            {
                var (a, ab, ac) = (corners[t], corners[t + 1] - corners[t], corners[t + 2] - corners[t]);
                var (along, across) = ((int)MathF.Ceiling(ab.Length() / step), (int)MathF.Ceiling(ac.Length() / step));
                if (along <= 1 && across <= 1)
                {
                    // Too small for a point of its own: its share goes to the next.
                    owed += Vector3.Cross(ab, ac).Length() / 2 / (step * step);
                    if (owed >= 1)
                    {
                        owed -= 1;
                        points.Add(a + (ab + ac) / 3);
                    }
                    continue;
                }
                for (var i = 0; i < along; i++)
                    for (var j = 0; j < across; j++)
                    {
                        var (u, v) = ((i + 0.33f) / along, (j + 0.33f) / across);
                        if (u + v <= 1)
                            points.Add(a + ab * u + ac * v);
                    }
            }
        return points;
    }
}

/// <summary>
/// How a weapon sits on a mount made for another. The original's weapons hang from their mount with nothing between
/// them and each mount was placed for that machine's weapons, so the room the chassis' own weapon took says where the
/// body ends: a new weapon keeps that weapon's inner edge, and moves along and up only as far as it takes for the
/// shorter of the two to lie within the longer. Then it is held against the body itself (see <see cref="Fit"/>), which
/// looks at the meshes of the look at hand.
/// </summary>
public static class HeroSeat
{
    /// <summary>
    /// What to move a weapon by, along the mount's axes. <paramref name="sideways"/> false leaves it where it leans.
    /// <paramref name="surface"/> is the height of the body over a mount that lies in it: the weapon stands on that, in
    /// the mount's middle.
    /// </summary>
    public static (float X, float Y, float Z) Between(MountBox own, MountBox weapon, Mounting mounting, bool sideways = true, float? surface = null) =>
        (Within(weapon.MinX, weapon.MaxX, own.MinX, own.MaxX), surface - weapon.MinY ?? Within(weapon.MinY, weapon.MaxY, own.MinY, own.MaxY),
            !sideways ? 0 : mounting switch
            {
                Mounting.Left when surface is null => own.MaxZ - weapon.MaxZ,
                Mounting.Right when surface is null => own.MinZ - weapon.MinZ,
                _ => (own.MinZ + own.MaxZ - weapon.MinZ - weapon.MaxZ) / 2,
            });

    private static float Within(float min, float max, float ownMin, float ownMax)
    {
        var (low, high) = (ownMin - min, ownMax - max);
        return Math.Clamp(0, Math.Min(low, high), Math.Max(low, high));
    }

    /// <summary>How far a weapon standing on the body goes into it, so no gap shows on a slope.</summary>
    private const float Embedded = 0.15f;

    /// <summary>The most of a weapon that lies in the body: the machines' own guns at home lie about that deep.</summary>
    public const float MostBuried = 0.38f;

    /// <summary>How near the skin a part of a weapon has to be to count as held by the body.</summary>
    public const float Touching = 0.2f;

    /// <summary>How much of its surface a weapon has to have that near, square metres, or it stands off the body.</summary>
    public const float LeastHeld = 0.5f;

    /// <summary>The share of a weapon that leans over a body the body has to hold: it has a tank to carry.</summary>
    public const float LeastHeldLeaning = 0.3f;

    /// <summary>The most of a muzzle's end the body may hide.</summary>
    public const float MostBlocked = 0.25f;

    /// <summary>What a weapon may have in common with one seated before it.</summary>
    public const float MostCrowded = 0.04f;

    /// <summary>The share of a weapon on top that has to stand over the body, or it hangs off a narrow machine.</summary>
    public const float LeastOver = 0.85f;

    // The share of a weapon's length counted as its muzzle, and how many of its points are looked at.
    private const float MuzzleShare = 0.15f;
    private const int PointsLookedAt = 400;

    /// <summary>A chassis as a solid: all of it but its root, which only stands it on the ground, and the weapons it came with, with the shoulder it may be given.</summary>
    public static HeroSolid Solid(string chassis, IReadOnlyList<RigShape> body)
    {
        var slots = WiredCatalogue.FindChassis(chassis)!.Slots.Where(s => s.Fixed is null).ToDictionary(s => s.Mounting, _ => (RigGraft.Source?)null);
        var kept = new RigGraft(Parts(body), slots).Nodes.Where(n => n.Chassis > 0 && !n.Stripped);
        var surfaces = kept.SelectMany(n => Corners(body, n.Chassis, World(body, n.Chassis))).ToList();
        if (Shoulder(chassis, body) is { } shoulder)
            surfaces.AddRange(Corners(body, shoulder));
        return new HeroSolid(surfaces);
    }

    /// <summary>
    /// The shoulder of a chassis that has one on a single side (see <see cref="HeroDonors.Shoulder"/>): it shows on the other
    /// as well, or the mount mirrored there would stand in the air. Null for any other chassis, and where the body has both.
    /// </summary>
    public static HeroShoulder? Shoulder(string chassis, IReadOnlyList<RigShape> body)
    {
        if (HeroDonors.Shoulder(chassis) is not { } lies)
            return null;
        var parts = Parts(body);
        var mount = RigGraft.MountOf(parts, Mounting.Left) is >= 0 and var left ? left : RigGraft.MountOf(parts, Mounting.Right);
        var (part, side) = (body[mount].Parent, MathF.Sign(body[mount].Rest.Translation.Z));
        int[][] Beyond(int way) => body[part].Surfaces.Select(s =>
            Enumerable.Range(0, s.Length / 3).Where(t => s.Skip(t * 3).Take(3).All(c => c.Z * way >= lies.Start && c.Y < lies.Top)).ToArray()).ToArray();
        float Size(int[][] triangles) => triangles.Select((list, s) => Area(list.SelectMany(t => body[part].Surfaces[s].Skip(t * 3).Take(3)).ToArray())).Sum();
        var shoulder = Beyond(side);
        return Size(Beyond(-side)) * 2 < Size(shoulder) ? new HeroShoulder(part, shoulder) : null;
    }

    // A shoulder across from where it stands, in the chassis' axes.
    private static IEnumerable<Vector3[]> Corners(IReadOnlyList<RigShape> body, HeroShoulder shoulder)
    {
        var place = Turned * World(body, shoulder.Part);
        return shoulder.Triangles.Select((list, s) => list.SelectMany(t => body[shoulder.Part].Surfaces[s].Skip(t * 3).Take(3)).Select(c => Vector3.Transform(c, place)).ToArray());
    }

    /// <summary>Triangles by their corners, three by three, wound the other way: those named, or all. A mesh turned over needs it to be lit from outside.</summary>
    public static int[] Rewound(int[] corners, IEnumerable<int>? triangles = null) =>
        (triangles ?? Enumerable.Range(0, corners.Length / 3)).SelectMany(t => new[] { corners[t * 3], corners[t * 3 + 2], corners[t * 3 + 1] }).ToArray();

    /// <summary>Tangents, four numbers each, of a mesh turned over: the last says which way the texture runs, and that turns too.</summary>
    public static float[] TurnedOver(float[] tangents)
    {
        var turned = (float[])tangents.Clone();
        for (var t = 0; t + 3 < turned.Length; t += 4)
            (turned[t + 2], turned[t + 3]) = (-turned[t + 2], -turned[t + 3]);
        return turned;
    }

    /// <summary>
    /// Where each weapon sits: what comes between its mount's rest and its own parts, for row vectors. They are seated
    /// left, right, top, and a right weapon starts from where the left one of its kind stands.
    /// </summary>
    public static Dictionary<Mounting, Matrix4x4> Seats(string chassis, IReadOnlyList<RigShape> body, HeroSolid solid, IEnumerable<HeroWorn> worn)
    {
        var seats = new Dictionary<Mounting, Matrix4x4>();
        var seated = new List<HeroSolid>();
        var all = worn.OrderBy(w => w.Side).ToList();
        foreach (var weapon in all)
        {
            var twin = weapon.Side == Mounting.Right && Twinned(chassis, weapon.Side) ? all.Find(w => w.Side == Mounting.Left && w.Weapon == weapon.Weapon) : null;
            var opposite = twin is not null && !HeroDonors.AtHome(chassis, twin.Donor, twin.Side) ? Across(chassis, body, weapon, twin, seats[twin.Side]) : (Matrix4x4?)null;
            var seat = seats[weapon.Side] = Seat(chassis, body, solid, seated, weapon, opposite);
            seated.Add(Solid(chassis, body, weapon, seat));
        }
        return seats;
    }

    /// <summary>The seat that stands a weapon across from its twin, the middle of one where the other's would be mirrored.</summary>
    private static Matrix4x4 Across(string chassis, IReadOnlyList<RigShape> body, HeroWorn worn, HeroWorn twin, Matrix4x4 seat)
    {
        var mount = MountPlace(body, worn.Side);
        Matrix4x4.Invert(mount, out var back);
        var place = Fitted(chassis, worn) * mount;
        var opposite = Vector3.Transform(Middle(chassis, twin), seat * MountPlace(body, twin.Side));
        place.Translation += opposite with { Z = -opposite.Z } - Vector3.Transform(Middle(chassis, worn), place);
        return place * back;
    }

    /// <summary>The middle of a weapon, in the axes of its mount.</summary>
    public static Vector3 Middle(string chassis, HeroWorn worn)
    {
        var box = MountBox.Of(Shape(chassis, worn).SelectMany(s => s).ToList())!.Value;
        return new Vector3(box.MinX + box.MaxX, box.MinY + box.MaxY, box.MinZ + box.MaxZ) / 2;
    }

    // Whether a chassis has a slot on either side, and so a pair of weapons on it stands alike.
    private static bool Twinned(string chassis, Mounting side) =>
        side != Mounting.Top && WiredCatalogue.FindChassis(chassis)!.Slot(side) is not null && WiredCatalogue.FindChassis(chassis)!.Slot(Opposite(side)) is not null;

    // Across the chassis' centre line, or a mount's.
    private static readonly Matrix4x4 Turned = Matrix4x4.CreateScale(1, 1, -1);

    // Row vectors: a weapon that leans the other way is turned over first of all.
    private static Matrix4x4 Fitted(string chassis, HeroWorn worn) =>
        (worn.Donor.Mirrored ? Turned : Matrix4x4.Identity) * Matrix4x4.CreateScale(HeroDonors.Scale(chassis, worn.Weapon));

    /// <summary>
    /// A seat without the turning over, for a rig that has it in the weapon's mesh instead (see HeroRig): a part turned over
    /// by its transform is lit as if from inside, whatever the light.
    /// </summary>
    public static Matrix4x4 Upright(HeroWorn worn, Matrix4x4 seat) => worn.Donor.Mirrored ? Turned * seat : seat;

    /// <summary>A seated weapon as a solid, in the chassis' axes.</summary>
    public static HeroSolid Solid(string chassis, IReadOnlyList<RigShape> body, HeroWorn worn, Matrix4x4 seat)
    {
        var place = seat * MountPlace(body, worn.Side);
        return new HeroSolid(Shape(chassis, worn).Select(s => s.Select(c => Vector3.Transform(c, place)).ToArray()));
    }

    private static Matrix4x4 Seat(string chassis, IReadOnlyList<RigShape> body, HeroSolid solid, List<HeroSolid> seated, HeroWorn worn, Matrix4x4? opposite)
    {
        var fitted = Fitted(chassis, worn);
        if (HeroDonors.AtHome(chassis, worn.Donor, worn.Side))
            return fitted;
        var parts = Parts(body);
        var side = worn.Side;
        var own = RigGraft.MountOf(parts, side);
        var across = own < 0;
        if (across)
            own = RigGraft.MountOf(parts, Opposite(side));
        var shape = Shape(chassis, worn).ToList();
        if (own < 0 || RoomOf(body, own) is not { } room || MountBox.Of(shape.SelectMany(s => s).ToList()) is not { } box)
            return fitted;

        var mount = MountPlace(body, side);
        Matrix4x4.Invert(mount, out var back);
        var turned = (worn.Donor.Mirrored ? box.Across : box) * HeroDonors.Scale(chassis, worn.Weapon);
        Matrix4x4 seat;
        if (opposite is { } start)
            seat = start;
        else if (HeroDonors.Rides(chassis))
        {
            var top = solid.Skin(mount.Translation with { Z = 0 }, Vector3.UnitY) ?? 0;
            seat = fitted * Matrix4x4.CreateTranslation(mount.Translation.X - (turned.MinX + turned.MaxX) / 2,
                mount.Translation.Y + top - Embedded - turned.MinY, -(turned.MinZ + turned.MaxZ) / 2) * back;
        }
        else
        {
            var sinks = HeroDonors.Sinks(chassis);
            var (x, y, z) = Between(across ? room.Across : room, turned, side, sinks || side == Mounting.Top || !HeroDonors.Leans(worn.Weapon),
                sinks ? solid.Skin(mount.Translation, Up(mount)) - Embedded : null);
            seat = fitted * Matrix4x4.CreateTranslation(x, y, z);
        }

        var points = Points(shape, box, seat * mount);
        var move = Fit(solid, seated, points, Wanted(chassis, worn), Twinned(chassis, side), side == Mounting.Right);
        return seat * mount * Matrix4x4.CreateTranslation(move) * back;
    }

    /// <summary>What a weapon has to come up to (see <see cref="Fit"/>): how much of it the body has to hold and to stand under it.</summary>
    public static (float Held, float Over) Wanted(string chassis, HeroWorn worn)
    {
        var scale = HeroDonors.Scale(chassis, worn.Weapon);
        var area = Shape(chassis, worn).Sum(Area) * scale * scale;
        return (HeroDonors.Leans(worn.Weapon) ? LeastHeldLeaning : MathF.Min(LeastHeldLeaning, LeastHeld / area), worn.Side == Mounting.Top ? LeastOver : 0);
    }

    /// <summary>How a seated weapon stands, beside <paramref name="others"/> if it has any (see <see cref="Stand"/>).</summary>
    public static HeroStand Measure(string chassis, IReadOnlyList<RigShape> body, HeroSolid solid, HeroWorn worn, Matrix4x4 seat, IReadOnlyList<HeroSolid>? others = null)
    {
        var shape = Shape(chassis, worn).ToList();
        return Stand(solid, others ?? [], Points(shape, MountBox.Of(shape.SelectMany(s => s).ToList())!.Value, seat * MountPlace(body, worn.Side)), Vector3.Zero);
    }

    /// <summary>A weapon's points to look at, where <paramref name="place"/> puts them, and which of them lie at its muzzle.</summary>
    private static HeroPoints Points(List<Vector3[]> shape, MountBox box, Matrix4x4 place)
    {
        var all = HeroSolid.Spread(shape, HeroSolid.Cell);
        if (all.Count > PointsLookedAt)
            all = Enumerable.Range(0, PointsLookedAt).Select(i => all[(int)((long)i * all.Count / PointsLookedAt)]).ToList();
        var front = box.MaxX - (box.MaxX - box.MinX) * MuzzleShare;
        return new HeroPoints(all.Select(p => Vector3.Transform(p, place)).ToList(), all.Where(p => p.X >= front).Select(p => Vector3.Transform(p, place)).ToList());
    }

    private static float Area(Vector3[] corners)
    {
        var area = 0f;
        for (var t = 0; t + 2 < corners.Length; t += 3)
            area += Vector3.Cross(corners[t + 1] - corners[t], corners[t + 2] - corners[t]).Length() / 2;
        return area;
    }

    private static Mounting Opposite(Mounting side) => side == Mounting.Left ? Mounting.Right : Mounting.Left;

    private static Vector3 Up(Matrix4x4 place) => new(place.M21, place.M22, place.M23);

    /// <summary>Where a side's mount stands in the chassis' axes, or the mirror of the other side's where it has none.</summary>
    public static Matrix4x4 MountPlace(IReadOnlyList<RigShape> body, Mounting side)
    {
        var parts = Parts(body);
        if (RigGraft.MountOf(parts, side) is >= 0 and var own)
            return World(body, own);
        var other = RigGraft.MountOf(parts, Opposite(side));
        return Turned * body[other].Rest * Turned * World(body, body[other].Parent);
    }

    /// <summary>The room a chassis' own weapon takes on a mount; null if it shows nothing.</summary>
    private static MountBox? RoomOf(IReadOnlyList<RigShape> body, int mount) =>
        MountBox.Of(RigGraft.Carried(Parts(body), mount).SelectMany(p => Corners(body, p, Below(body, p, mount))).SelectMany(s => s).ToList());

    /// <summary>A weapon's surfaces in the axes of its mount, without a foot where it does not stand on it.</summary>
    private static IEnumerable<Vector3[]> Shape(string chassis, HeroWorn worn)
    {
        var bare = HeroDonors.Footless(chassis, worn);
        foreach (var part in RigGraft.Carried(Parts(worn.Rig), worn.Mount))
        {
            if (bare && part == worn.Mount)
                continue;
            var place = Below(worn.Rig, part, worn.Mount);
            foreach (var surface in worn.Rig[part].Surfaces.Where(s => !bare || !IsFoot(s, worn.Rig[worn.Mount])))
                yield return surface.Select(c => Vector3.Transform(c, place)).ToArray();
        }
    }

    /// <summary>Whether a surface repeats its mount's mesh: the HD launchers' round foot, which they carry twice.</summary>
    public static bool IsFoot(Vector3[] surface, RigShape mount) => surface.Length > 0 && mount.Surfaces.Any(s => s.AsSpan().SequenceEqual(surface));

    private static IEnumerable<Vector3[]> Corners(IReadOnlyList<RigShape> rig, int part, Matrix4x4 place) =>
        rig[part].Surfaces.Select(s => s.Select(c => Vector3.Transform(c, place)).ToArray());

    private static RigPart[] Parts(IReadOnlyList<RigShape> rig) => rig.Select(p => new RigPart(p.Parent, p.Name)).ToArray();

    /// <summary>Where a part stands in its rig.</summary>
    internal static Matrix4x4 World(IReadOnlyList<RigShape> rig, int part)
    {
        var place = Matrix4x4.Identity;
        for (var p = part; p >= 0 && p < rig.Count; p = rig[p].Parent == p ? -1 : rig[p].Parent)
            place *= rig[p].Rest;
        return place;
    }

    private static Matrix4x4 Below(IReadOnlyList<RigShape> rig, int part, int mount)
    {
        var place = Matrix4x4.Identity;
        for (var p = part; p != mount; p = rig[p].Parent)
            place *= rig[p].Rest;
        return place;
    }

    // Every move a weapon may make, the shortest first; along the barrel comes cheaper than up, and up than across.
    private static readonly Lazy<Vector3[]> Moves = new(() =>
    {
        const int along = 25, up = 25, across = 12;
        var moves = new List<Vector3>();
        for (var x = -along; x <= along; x++)
            for (var y = -up; y <= up; y++)
                for (var z = -across; z <= across; z++)
                    moves.Add(new Vector3(x, y, z) * HeroSolid.Cell);
        return moves.OrderBy(m => (m * new Vector3(0.8f, 1, 1.4f)).LengthSquared()).ToArray();
    });

    // The same for a weapon on the centre line, which leaves it only if there is no other way.
    private static readonly Lazy<Vector3[]> MovesOnTop = new(() => Moves.Value.OrderBy(m => m.Z != 0).ToArray());

    /// <summary>How a weapon stands, moved so far.</summary>
    public static HeroStand Stand(HeroSolid body, IReadOnlyList<HeroSolid> others, HeroPoints points, Vector3 move)
    {
        var (buried, held, crowded, over, blocked) = (0, 0, 0, 0, 0);
        foreach (var point in points.All)
        {
            var at = point + move;
            buried += body.Holds(at) ? 1 : 0;
            held += body.Gap(at) <= Touching ? 1 : 0;
            over += body.Covers(at) ? 1 : 0;
            foreach (var other in others)
                crowded += other.Holds(at) ? 1 : 0;
        }
        foreach (var point in points.Muzzle)
            blocked += body.Holds(point + move) ? 1 : 0;
        var all = (float)Math.Max(1, points.All.Count);
        return new HeroStand(buried / all, held / all, crowded / all, over / all, blocked / (float)Math.Max(1, points.Muzzle.Count));
    }

    /// <summary>
    /// The shortest move, in the chassis' axes, that leaves a weapon neither buried in the body nor standing off it, with
    /// its muzzle clear and out of the weapons seated before it: one that stands well stays. Failing that, the least wrong.
    /// With <paramref name="twins"/> it has to stand as well on the chassis' other side and keep to its own half, so that a
    /// pair can stand alike; <paramref name="right"/> takes the moves mirrored, for the same reason.
    /// </summary>
    public static Vector3 Fit(HeroSolid body, IReadOnlyList<HeroSolid> others, HeroPoints points, (float Held, float Over) wanted, bool twins = false, bool right = false)
    {
        float Wrong(HeroStand stand) => MathF.Max(0, stand.Buried - MostBuried) + MathF.Max(0, wanted.Held - stand.Held) + 2 * MathF.Max(0, stand.Crowded - MostCrowded)
            + MathF.Max(0, wanted.Over - stand.Over) + MathF.Max(0, stand.Blocked - MostBlocked);

        var across = points.Across;
        var (best, least) = (Vector3.Zero, float.MaxValue);
        foreach (var way in wanted.Over > 0 ? MovesOnTop.Value : Moves.Value)
        {
            var move = right ? way with { Z = -way.Z } : way;
            var wrong = Wrong(Stand(body, others, points, move));
            if (twins && wrong < least)
                wrong += Wrong(Stand(body, [], across, move with { Z = -move.Z })) + 2 * MathF.Max(0, Past(points, move, right) - MostCrowded);
            if (wrong <= 0)
                return move;
            if (wrong < least)
                (best, least) = (move, wrong);
        }
        return best;
    }

    // The share of a weapon that lies past the centre line, in its twin's half.
    private static float Past(HeroPoints points, Vector3 move, bool right) =>
        points.All.Count(p => right ? p.Z + move.Z < 0 : p.Z + move.Z > 0) / (float)Math.Max(1, points.All.Count);
}

/// <summary>How a weapon stands: the shares of it in the body, by its skin, in another weapon and over or under the body, and of its muzzle in the body.</summary>
public readonly record struct HeroStand(float Buried, float Held, float Crowded, float Over, float Blocked);

/// <summary>The points of a weapon that are looked at, in the chassis' axes, and those of them at its muzzle.</summary>
public sealed record HeroPoints(List<Vector3> All, List<Vector3> Muzzle)
{
    /// <summary>The same on the chassis' other side.</summary>
    public HeroPoints Across => new(All.Select(p => p with { Z = -p.Z }).ToList(), Muzzle.Select(p => p with { Z = -p.Z }).ToList());
}
