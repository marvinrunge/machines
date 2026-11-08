using System.Numerics;
using Godot;
using Machines.Import.Model;
using Machines.Sim.Combat;
using Machines.Sim.Survival;
using NVector3 = System.Numerics.Vector3;
using Vector3 = Godot.Vector3;

namespace Machines.Game;

/// <summary>
/// A Wired hero's model, put together from the game's own rigs: the chassis' rig with its weapons taken off and the
/// parts of the weapons it wears hung on its mounts (see <see cref="RigGraft"/>). It comes in the look and colours of
/// <see cref="ClassicAssets.RigFor"/>, and moves as any machine, its weapons recoiling on their sides. An empty slot shows
/// the bare mount. What stands turned over is turned over in its mesh, never by a part's transform: materials draw both
/// sides, so a part with its scale turned over would be lit as if from inside. Wired is the remake's own addition.
/// </summary>
public static class HeroRig
{
    // Across the machine's centre line: its sides are Godot's z.
    private static readonly Basis Across = Basis.FromScale(new Vector3(1, 1, -1));

    private const int TyreSides = 28;

    private static readonly Lazy<StandardMaterial3D> Rubber = new(() => new StandardMaterial3D { AlbedoColor = new Color(0.1f, 0.1f, 0.105f), Roughness = 0.85f, MetallicSpecular = 0.25f });

    /// <summary>The model of what <paramref name="loadout"/> wears, or null without the chassis' export.</summary>
    public static Rig? Compose(ClassicAssets assets, Race race, HeroLoadout loadout) =>
        Compose(assets, race, loadout.Chassis.Catalogue, loadout.Weapons.Select(w => w?.Catalogue).ToArray());

    /// <summary>
    /// The model of a chassis with <paramref name="weapons"/> on its mounts, by <see cref="Mounting"/>: left, right, top,
    /// null for empty. A weapon its chassis cannot take still shows, for a preview. Rigs are kept by what they wear.
    /// </summary>
    public static Rig? Compose(ClassicAssets assets, Race race, string chassis, IReadOnlyList<string?> weapons)
    {
        var key = $"{race}/{chassis}/{string.Join('/', weapons)}";
        if (assets.HeroRigs.TryGetValue(key, out var kept))
            return kept;
        var rig = Build(assets, race, chassis, weapons);
        assets.HeroRigs[key] = rig;
        return rig;
    }

    private static Rig? Build(ClassicAssets assets, Race race, string chassis, IReadOnlyList<string?> weapons)
    {
        if (WiredCatalogue.FindChassis(chassis) is not { } entry || !HeroDonors.Chassis.TryGetValue(chassis, out var model)
            || assets.RigFor(model, race) is not { } body)
            return null;

        // What goes on each slot of the chassis; a fixed weapon such as the Gorilla's punch stays as it is.
        var donors = new Dictionary<Mounting, (Rig Rig, HeroDonor Donor)>();
        var slots = new Dictionary<Mounting, RigGraft.Source?>();
        foreach (var slot in entry.Slots.Where(s => s.Fixed is null))
        {
            slots[slot.Mounting] = null;
            if ((int)slot.Mounting >= weapons.Count || weapons[(int)slot.Mounting] is not { } weapon)
                continue;
            if (HeroDonors.For(weapon, slot.Mounting) is not { } donor || assets.RigFor(donor.Model, race) is not { } rig)
            {
                GD.PrintErr($"hero: no model for {weapon}, the {slot.Mounting} slot of {chassis} stays bare");
                continue;
            }
            var parts = Parts(rig);
            if (RigGraft.MountOf(parts, donor.Mount) is not (>= 0 and var mount))
            {
                GD.PrintErr($"hero: {donor.Model} has no {donor.Mount} mount in this look, the {slot.Mounting} slot of {chassis} stays bare");
                continue;
            }
            donors[slot.Mounting] = (rig, donor);
            slots[slot.Mounting] = new RigGraft.Source(parts, mount);
        }

        var graft = new RigGraft(Parts(body), slots);
        var shapes = Shapes(assets, body);
        if (!assets.HeroSolids.TryGetValue(body, out var solid))
            assets.HeroSolids[body] = solid = HeroSeat.Solid(chassis, shapes);
        var worn = donors.ToDictionary(d => d.Key, d => new HeroWorn(d.Key, weapons[(int)d.Key]!, d.Value.Donor, Shapes(assets, d.Value.Rig), slots[d.Key]!.Mount));
        var seats = HeroSeat.Seats(chassis, shapes, solid, worn.Values).ToDictionary(s => s.Key, s => Place(HeroSeat.Upright(worn[s.Key], s.Value)));
        var nodes = graft.Nodes.Select(n => Node(n, graft, body, chassis, donors, worn, seats)).ToArray();
        for (var i = 0; i < nodes.Length; i++)
            if (graft.Nodes[i].Weapon is { } side && worn[side].Donor.Mirrored)
                nodes[i] = nodes[i] with { Mesh = TurnedOver(assets, nodes[i].Mesh) };
        foreach (var wheel in body.Wheels is { } rolling ? Kept(graft, rolling.Left).Concat(Kept(graft, rolling.Right)) : [])
            nodes[wheel] = nodes[wheel] with { Mesh = Hubbed(assets, nodes[wheel].Mesh) };
        if (HeroSeat.Shoulder(chassis, shapes) is { } shoulder && graft.Chassis(shoulder.Part) is >= 0 and var shouldered)
            nodes[shouldered] = nodes[shouldered] with { Mesh = WithShoulder(assets, nodes[shouldered].Mesh, shoulder) };
        return new Rig
        {
            Nodes = nodes,
            UpperBody = graft.Chassis(body.UpperBody),
            Animations = Animations(graft, body, donors),
            Wheels = body.Wheels is { } wheels ? wheels with { Left = Kept(graft, wheels.Left), Right = Kept(graft, wheels.Right) } : null,
            Tracks = body.Tracks is { } tracks ? tracks with { Left = graft.Chassis(tracks.Left), Right = graft.Chassis(tracks.Right) } : null,
            Idle = Idle(graft, body, donors),
            Weapons = RecoilOf(graft, body, donors),
            Faceplate = graft.Chassis(body.Faceplate),
            Eye = body.Eye,
        };
    }

    private static RigPart[] Parts(Rig rig) => rig.Nodes.Select(n => new RigPart(n.Parent, n.Name)).ToArray();

    private static int[] Kept(RigGraft graft, int[] nodes) => nodes.Select(graft.Chassis).Where(n => n >= 0).ToArray();

    /// <summary>Where the hangar's traces end on a hero's skin; kept with the rig.</summary>
    public static HullSpot[] Spots(ClassicAssets assets, Rig rig)
    {
        if (!assets.HeroSpots.TryGetValue(rig, out var spots))
            assets.HeroSpots[rig] = spots = HeroHull.Spots(Shapes(assets, rig), rig.UpperBody);
        return spots;
    }

    /// <summary>A rig as its weapons are seated by: its parts, and the triangles of each mesh, all but sprites and spinners.</summary>
    private static RigShape[] Shapes(ClassicAssets assets, Rig rig)
    {
        if (assets.HeroShapes.TryGetValue(rig, out var kept))
            return kept;
        var shapes = rig.Nodes.Select(n => new RigShape(n.Parent, n.Name, Rows(n.Rest), Surfaces(n.Mesh))).ToArray();
        assets.HeroShapes[rig] = shapes;
        return shapes;
    }

    private static NVector3[][] Surfaces(Mesh? mesh)
    {
        if (mesh is not ArrayMesh triangles)
            return [];
        return Enumerable.Range(0, triangles.GetSurfaceCount()).Select(s =>
        {
            if (triangles.SurfaceGetMaterial(s) is ShaderMaterial)
                return [];
            var arrays = triangles.SurfaceGetArrays(s);
            var points = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            var corners = arrays[(int)Mesh.ArrayType.Index].AsInt32Array();
            return (corners.Length > 0 ? corners.Select(c => points[c]) : points).Select(p => new NVector3(p.X, p.Y, p.Z)).ToArray();
        }).ToArray();
    }

    private static Matrix4x4 Rows(Transform3D at)
    {
        var (x, y, z, o) = (at.Basis.Column0, at.Basis.Column1, at.Basis.Column2, at.Origin);
        return new Matrix4x4(x.X, x.Y, x.Z, 0, y.X, y.Y, y.Z, 0, z.X, z.Y, z.Z, 0, o.X, o.Y, o.Z, 1);
    }

    private static Transform3D Place(Matrix4x4 m) =>
        new(new Basis(new Vector3(m.M11, m.M12, m.M13), new Vector3(m.M21, m.M22, m.M23), new Vector3(m.M31, m.M32, m.M33)), new Vector3(m.M41, m.M42, m.M43));

    private static RigNode Node(RigGraft.Node node, RigGraft graft, Rig body, string chassis, Dictionary<Mounting, (Rig Rig, HeroDonor Donor)> donors,
        Dictionary<Mounting, HeroWorn> worn, Dictionary<Mounting, Transform3D> seats)
    {
        var from = node.Weapon is { } side ? donors[side] : default;
        var mesh = node.Donor >= 0 ? Weapon(node, from.Rig.Nodes[node.Donor].Mesh, chassis, worn) : node.Chassis >= 0 && !node.Stripped ? body.Nodes[node.Chassis].Mesh : null;
        // A weapon's part keeps its place on the weapon; a mount stands where the chassis has it, or the mirror of the one it has.
        if (node.Chassis < 0 && node.Opposite < 0)
            return new RigNode(node.Parent, from.Rig.Nodes[node.Donor].Rest, mesh, node.Name);
        var rest = node.Chassis >= 0 ? body.Nodes[node.Chassis].Rest : Mirror(body.Nodes[node.Opposite].Rest);
        // What is left of the chassis on a mount stays where the chassis has it, wherever its weapon sits.
        if (node.Weapon is null && node.Parent >= 0 && graft.Nodes[node.Parent].Weapon is { } under)
            rest = seats[under].AffineInverse() * rest;
        if (node.Weapon is { } seated)
            rest *= seats[seated];
        return new RigNode(node.Parent, rest, mesh, node.Name);
    }

    /// <summary>A part of a weapon's mesh: without the foot where the weapon does not stand on it (see <see cref="HeroDonors.Footless"/>).</summary>
    private static Mesh? Weapon(RigGraft.Node node, Mesh? mesh, string chassis, Dictionary<Mounting, HeroWorn> worn)
    {
        var weapon = worn[node.Weapon!.Value];
        if (mesh is not ArrayMesh whole || !HeroDonors.Footless(chassis, weapon))
            return mesh;
        if (node.Donor == weapon.Mount)
            return null;
        var (surfaces, foot) = (weapon.Rig[node.Donor].Surfaces, weapon.Rig[weapon.Mount]);
        if (!surfaces.Any(s => HeroSeat.IsFoot(s, foot)))
            return mesh;
        var kept = new ArrayMesh();
        for (var s = 0; s < whole.GetSurfaceCount(); s++)
        {
            if (HeroSeat.IsFoot(surfaces[s], foot))
                continue;
            kept.AddSurfaceFromArrays(whole.SurfaceGetPrimitiveType(s), whole.SurfaceGetArrays(s));
            kept.SurfaceSetMaterial(kept.GetSurfaceCount() - 1, whole.SurfaceGetMaterial(s));
        }
        return kept;
    }

    /// <summary>A whole mesh turned over (see <see cref="HeroRig"/>), kept by what it came from.</summary>
    private static Mesh? TurnedOver(ClassicAssets assets, Mesh? mesh)
    {
        if (mesh is not ArrayMesh whole)
            return mesh;
        if (assets.HeroMeshes.TryGetValue(whole, out var kept))
            return kept;
        var turned = new ArrayMesh();
        for (var s = 0; s < whole.GetSurfaceCount(); s++)
            AddTurnedOver(turned, whole, s);
        return assets.HeroMeshes[whole] = turned;
    }

    /// <summary>
    /// A wheel with its hub showing. The export has the wheel's caps twice over in the same place, in black facing in and
    /// lit facing out; the original culled what faced away, while the remake draws both sides, so black lies over the hub.
    /// The tread is a black plane turned to the camera, which took on a shine from the hangar's green rim and otherwise stood
    /// between the caps as a black slab, however the hero is turned; it becomes a tyre that the light can show (<see cref="AddTyre"/>).
    /// Only the hero goes so here: the same wheels on any other machine stay as they have looked.
    /// </summary>
    private static Mesh? Hubbed(ClassicAssets assets, Mesh? mesh)
    {
        if (mesh is not ArrayMesh whole)
            return mesh;
        if (assets.HeroMeshes.TryGetValue(whole, out var kept))
            return kept;
        var materials = Enumerable.Range(0, whole.GetSurfaceCount()).Select(whole.SurfaceGetMaterial).ToArray();
        var lit = materials.OfType<StandardMaterial3D>().Where(m => !Black(m)).Select(Picture).ToHashSet();
        var hubbed = new ArrayMesh();
        for (var s = 0; s < materials.Length; s++)
        {
            if (materials[s] is StandardMaterial3D cap && Black(cap) && lit.Contains(Picture(cap)))
                continue;
            if (materials[s] is ShaderMaterial plane && Bare(plane))
            {
                AddTyre(hubbed, whole, s);
                continue;
            }
            hubbed.AddSurfaceFromArrays(whole.SurfaceGetPrimitiveType(s), whole.SurfaceGetArrays(s));
            hubbed.SurfaceSetMaterial(hubbed.GetSurfaceCount() - 1, materials[s] is ShaderMaterial tread ? Matt(tread) : materials[s]);
        }
        return assets.HeroMeshes[whole] = hubbed;
    }

    /// <summary>Whether a spinner is a plain black plane, with no picture on it.</summary>
    private static bool Bare(ShaderMaterial material) =>
        material.GetShaderParameter("sprite").VariantType == Variant.Type.Nil && material.GetShaderParameter("colour").AsVector3().LengthSquared() < 0.0001f;

    /// <summary>Round tyres in place of a spinner's planes, as wide and as high as the planes.</summary>
    private static void AddTyre(ArrayMesh tyres, ArrayMesh whole, int surface)
    {
        var arrays = whole.SurfaceGetArrays(surface);
        var (bases, axes, corners) = (arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array(), arrays[(int)Mesh.ArrayType.Normal].AsVector3Array(), arrays[(int)Mesh.ArrayType.TexUV2].AsVector2Array());
        foreach (var plane in Enumerable.Range(0, bases.Length).GroupBy(i => bases[i]))
        {
            var (at, axis) = (plane.Key, axes[plane.First()].Normalized());
            var (half, radius) = (plane.Max(i => Math.Abs(corners[i].X)), plane.Max(i => Math.Abs(corners[i].Y)));
            var side = axis.Cross(Math.Abs(axis.Y) < 0.9f ? Vector3.Up : Vector3.Right).Normalized();
            var (points, normals, indices) = (new List<Vector3>(), new List<Vector3>(), new List<int>());
            for (var i = 0; i <= TyreSides; i++)
            {
                var outward = side.Rotated(axis, i * Mathf.Tau / TyreSides);
                points.AddRange([at + outward * radius - axis * half, at + outward * radius + axis * half]);
                normals.AddRange([outward, outward]);
                if (i < TyreSides)
                    indices.AddRange([i * 2, i * 2 + 1, i * 2 + 3, i * 2, i * 2 + 3, i * 2 + 2]);
            }
            var tyre = new Godot.Collections.Array();
            tyre.Resize((int)Mesh.ArrayType.Max);
            tyre[(int)Mesh.ArrayType.Vertex] = points.ToArray();
            tyre[(int)Mesh.ArrayType.Normal] = normals.ToArray();
            tyre[(int)Mesh.ArrayType.Index] = indices.ToArray();
            tyres.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, tyre);
            tyres.SurfaceSetMaterial(tyres.GetSurfaceCount() - 1, Rubber.Value);
        }
    }

    private static bool Black(StandardMaterial3D material) => material.AlbedoColor is { R: < 0.01f, G: < 0.01f, B: < 0.01f };

    private static string Picture(StandardMaterial3D material) => material.GetMeta("texture", "").AsString();

    /// <summary>A spinner's material without its shine.</summary>
    private static ShaderMaterial Matt(ShaderMaterial material)
    {
        var matt = (ShaderMaterial)material.Duplicate();
        matt.Shader = new Shader { Code = material.Shader.Code.Replace("render_mode cull_disabled;", "render_mode cull_disabled, specular_disabled;") };
        return matt;
    }

    /// <summary>A body as it is, and its shoulder once more on the other side; kept by what it came from.</summary>
    private static Mesh? WithShoulder(ClassicAssets assets, Mesh? mesh, HeroShoulder shoulder)
    {
        if (mesh is not ArrayMesh whole)
            return mesh;
        if (assets.HeroMeshes.TryGetValue(whole, out var kept))
            return kept;
        var both = new ArrayMesh();
        for (var s = 0; s < whole.GetSurfaceCount(); s++)
        {
            both.AddSurfaceFromArrays(whole.SurfaceGetPrimitiveType(s), whole.SurfaceGetArrays(s));
            both.SurfaceSetMaterial(s, whole.SurfaceGetMaterial(s));
        }
        for (var s = 0; s < shoulder.Triangles.Length; s++)
            if (shoulder.Triangles[s].Length > 0)
                AddTurnedOver(both, whole, s, shoulder.Triangles[s]);
        return assets.HeroMeshes[whole] = both;
    }

    /// <summary>A surface across its z axis, all of it or the triangles named: corners, normals and tangents turn, and its triangles wind the other way.</summary>
    private static void AddTurnedOver(ArrayMesh to, ArrayMesh from, int surface, int[]? triangles = null)
    {
        var arrays = from.SurfaceGetArrays(surface);
        Vector3[] Over(Mesh.ArrayType type) => arrays[(int)type].AsVector3Array().Select(v => v with { Z = -v.Z }).ToArray();
        var corners = arrays[(int)Mesh.ArrayType.Index].AsInt32Array();
        if (corners.Length == 0)
            corners = Enumerable.Range(0, arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array().Length).ToArray();
        arrays[(int)Mesh.ArrayType.Vertex] = Over(Mesh.ArrayType.Vertex);
        arrays[(int)Mesh.ArrayType.Normal] = Over(Mesh.ArrayType.Normal);
        if (arrays[(int)Mesh.ArrayType.Tangent].VariantType != Variant.Type.Nil)
            arrays[(int)Mesh.ArrayType.Tangent] = HeroSeat.TurnedOver(arrays[(int)Mesh.ArrayType.Tangent].AsFloat32Array());
        arrays[(int)Mesh.ArrayType.Index] = HeroSeat.Rewound(corners, triangles);
        to.AddSurfaceFromArrays(from.SurfaceGetPrimitiveType(surface), arrays);
        to.SurfaceSetMaterial(to.GetSurfaceCount() - 1, from.SurfaceGetMaterial(surface));
    }

    private static Transform3D Mirror(Transform3D rest) =>
        new(Across * rest.Basis * Across, new Vector3(rest.Origin.X, rest.Origin.Y, -rest.Origin.Z));

    private static Dictionary<string, RigAnimation> Animations(RigGraft graft, Rig body, Dictionary<Mounting, (Rig Rig, HeroDonor Donor)> donors)
    {
        var tracks = body.Animations.ToDictionary(a => a.Key, a => (a.Value.Duration,
            Tracks: a.Value.Tracks.Select(t => t with { Node = graft.Chassis(t.Node) }).Where(t => t.Node >= 0).ToList()));
        // A weapon's own parts bring what they do in the cycles of the machine they came from.
        foreach (var (side, (rig, _)) in donors)
        {
            foreach (var (name, animation) in rig.Animations)
            {
                var moved = animation.Tracks.Select(t => t with { Node = graft.Weapon(side, t.Node) }).Where(t => t.Node >= 0).ToList();
                if (moved.Count == 0)
                    continue;
                if (!tracks.TryGetValue(name, out var own))
                    tracks[name] = own = (animation.Duration, []);
                own.Tracks.AddRange(moved);
            }
        }
        return tracks.ToDictionary(t => t.Key, t => new RigAnimation(t.Value.Duration, t.Value.Tracks.ToArray()));
    }

    private static RigIdle[] Idle(RigGraft graft, Rig body, Dictionary<Mounting, (Rig Rig, HeroDonor Donor)> donors) =>
        body.Idle.Select(i => i with { Track = i.Track with { Node = graft.Chassis(i.Track.Node) } })
            .Concat(donors.SelectMany(d => d.Value.Rig.Idle.Select(i => i with { Track = i.Track with { Node = graft.Weapon(d.Key, i.Track.Node) } })))
            .Where(i => i.Track.Node >= 0).ToArray();

    /// <summary>What recoils: the chassis' fixed weapons, and of each donor what moved on the mount it came from, now on the side it went to.</summary>
    private static RigWeapon[] RecoilOf(RigGraft graft, Rig body, Dictionary<Mounting, (Rig Rig, HeroDonor Donor)> donors) =>
        body.Weapons.Select(w => w with { Node = graft.Chassis(w.Node) })
            .Concat(donors.SelectMany(d => d.Value.Rig.Weapons.Where(w => w.Mounting == Side(d.Value.Donor.Mount))
                .Select(w => new RigWeapon(graft.Weapon(d.Key, w.Node), Side(d.Key)))))
            .Where(w => w.Node >= 0).ToArray();

    private static string Side(Mounting mounting) => mounting switch
    {
        Mounting.Left => "left",
        Mounting.Right => "right",
        _ => "top",
    };
}
