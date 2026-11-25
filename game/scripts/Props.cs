using Godot;
using Machines.Import.Model;

namespace Machines.Game;

/// <summary>
/// The story's props (game/assets/story/props): the deck's kit (floors, walls, ceilings, doors, grates) and its set pieces
/// (clamps, consoles, crates, the computer core, the pods' cradles ...), made with Meshy and fitted in Blender
/// (tools/blender/prop_pack.py) to metres, y up, their front facing +z, the origin at the bottom centre. Godot imports them
/// (textures compressed, mipmapped, packed into a build); here their materials are made the remake's: a metallic share kept
/// low as the machines' (in the dark decks fully metallic paint shows nothing but highlights) and only the front faces drawn.
/// Each is loaded once and shared. A scenario's artefacts of a deck take the prop of their name (<see cref="Artefact"/>).
/// The story is the remake's own.
/// </summary>
public static class Props
{
    public const string Folder = "res://assets/story/props";

    /// <summary>The metallic share of the props' paint, as the overhauled machines have it (ClassicAssets.ModelMetal).</summary>
    private const float Metal = 0.35f;

    /// <summary>A prop as loaded: its mesh, where the mesh stands in the prop's frame, and its bounds there.</summary>
    public sealed record Model(Mesh Mesh, Transform3D Offset, Aabb Bounds)
    {
        /// <summary>Its size, metres: across its front, up, and out of its front.</summary>
        public Vector3 Size => Bounds.Size;
    }

    private static readonly Dictionary<string, Model?> Loaded = [];
    private static readonly object Lock = new();

    /// <summary>Whether there is a prop of that name.</summary>
    public static bool Exists(string name) => ResourceLoader.Exists($"{Folder}/{name}.glb");

    /// <summary>The names of every prop (for the warm-up); a build lists the imported files only by their .import.</summary>
    public static IEnumerable<string> Names() =>
        DirAccess.GetFilesAt(Folder)
            .Select(f => f.EndsWith(".import", StringComparison.Ordinal) ? f[..^".import".Length] : f)
            .Where(f => f.EndsWith(".glb", StringComparison.Ordinal))
            .Select(f => f[..^".glb".Length])
            .Distinct()
            .Order(StringComparer.Ordinal);

    /// <summary>The prop of that name, loaded once; null when there is none.</summary>
    public static Model? Get(string name)
    {
        lock (Lock)
        {
            if (Loaded.TryGetValue(name, out var known))
                return known;
            var model = Load(name);
            Loaded[name] = model;
            return model;
        }
    }

    /// <summary>
    /// The door frame bent to a slot (<see cref="DeckFrame.Fit"/>), made once for each fit and kept under its own name with the
    /// props, so frames alike share one mesh; null when there is no kit_door_frame.
    /// </summary>
    public static (string Name, Model Model)? Frame(DeckFrame frame)
    {
        var name = string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"kit_door_frame@{frame.Half:F2},{frame.Open:F2},{frame.Depth:F2},{frame.PostMinus:F3},{frame.PostPlus:F3},{frame.Lintel:F3}");
        lock (Lock)
        {
            if (Loaded.TryGetValue(name, out var known))
                return known is null ? null : (name, known);
        }
        if (Get("kit_door_frame") is not { Mesh: ArrayMesh kit } model)
            return null;
        var bent = new ArrayMesh { ResourceName = name };
        var back = model.Offset.AffineInverse();
        for (var surface = 0; surface < kit.GetSurfaceCount(); surface++)
        {
            var arrays = kit.SurfaceGetArrays(surface);
            if (arrays[(int)Mesh.ArrayType.Vertex].As<Vector3[]>() is { Length: > 0 } vertices)
            {
                var normals = arrays[(int)Mesh.ArrayType.Normal].As<Vector3[]>();
                var tangents = arrays[(int)Mesh.ArrayType.Tangent].As<float[]>();
                var placed = new Vector3[vertices.Length];
                for (var v = 0; v < vertices.Length; v++)
                {
                    var at = model.Offset * vertices[v];
                    var (fitted, stretch) = frame.Fit(new System.Numerics.Vector3(at.X, at.Y, at.Z));
                    placed[v] = at;
                    vertices[v] = back * new Vector3(fitted.X, fitted.Y, fitted.Z);
                    // A stretch turns a normal the other way (by the inverse), a tangent along with it; a post squeezed flat keeps a direction.
                    var s = new Vector3(Mathf.Max(stretch.X, 1e-3f), Mathf.Max(stretch.Y, 1e-3f), Mathf.Max(stretch.Z, 1e-3f));
                    if (normals is { } n && n.Length == vertices.Length)
                        n[v] = (n[v] / s).Normalized();
                    if (tangents is { } t && t.Length == vertices.Length * 4)
                    {
                        var turned = (new Vector3(t[4 * v], t[4 * v + 1], t[4 * v + 2]) * s).Normalized();
                        (t[4 * v], t[4 * v + 1], t[4 * v + 2]) = (turned.X, turned.Y, turned.Z);
                    }
                }
                arrays[(int)Mesh.ArrayType.Vertex] = vertices;
                if (normals is { Length: > 0 })
                    arrays[(int)Mesh.ArrayType.Normal] = normals;
                if (tangents is { Length: > 0 })
                    arrays[(int)Mesh.ArrayType.Tangent] = tangents;
                // Nothing stands in the opening: a slab Meshy left in its left half would be stretched across the way, and a
                // face reaching in from a post with one corner (one does, 5 m tall) would stand in it as a wedge.
                if (kit.SurfaceGetPrimitiveType(surface) == Mesh.PrimitiveType.Triangles
                    && arrays[(int)Mesh.ArrayType.Index].As<int[]>() is { Length: > 0 } indices)
                {
                    bool Inside(int corner) => DeckFrame.InOpening(new System.Numerics.Vector3(placed[corner].X, placed[corner].Y, placed[corner].Z));
                    var kept = new List<int>(indices.Length);
                    for (var i = 0; i + 2 < indices.Length; i += 3)
                    {
                        if (!Inside(indices[i]) && !Inside(indices[i + 1]) && !Inside(indices[i + 2]))
                            kept.AddRange([indices[i], indices[i + 1], indices[i + 2]]);
                    }
                    arrays[(int)Mesh.ArrayType.Index] = kept.ToArray();
                }
            }
            bent.AddSurfaceFromArrays(kit.SurfaceGetPrimitiveType(surface), arrays);
            bent.SurfaceSetMaterial(surface, kit.SurfaceGetMaterial(surface));
        }
        var made = new Model(bent, model.Offset, model.Offset * bent.GetAabb());
        lock (Lock)
            Loaded[name] = made;
        return (name, made);
    }

    private static Model? Load(string name)
    {
        if (!Exists(name) || GD.Load<PackedScene>($"{Folder}/{name}.glb") is not { } scene)
            return null;
        var root = scene.Instantiate<Node3D>();
        try
        {
            if (FirstMesh(root) is not { Mesh: ArrayMesh imported } part)
                return null;
            // Where the mesh stands in the prop: its node's transform and its parents' up to the scene's root.
            var offset = Transform3D.Identity;
            for (Node? node = part; node is not null && node != root; node = node.GetParent())
                if (node is Node3D spatial)
                    offset = spatial.Transform * offset;
            var mesh = Footless(Turned(imported), offset);
            if (name == "pod_cradle")
                mesh = Opened(mesh, offset);
            var jambs = name == "kit_door_frame" && Darken(mesh, JambFaces);
            for (var surface = 0; surface < mesh.GetSurfaceCount(); surface++)
                if ((part.GetSurfaceOverrideMaterial(surface) ?? mesh.SurfaceGetMaterial(surface)) is BaseMaterial3D material)
                {
                    var remade = Remade(material);
                    remade.VertexColorUseAsAlbedo = jambs;
                    mesh.SurfaceSetMaterial(surface, remade);
                }
            return new Model(mesh, offset, offset * mesh.GetAabb());
        }
        finally
        {
            root.Free();
        }
    }

    /// <summary>
    /// The pods' cradle with its ring opened wider about its middle, its outer rim and feet as they were: drawn round the
    /// game's Pod at its own size (DeckLayout.PodCradle), its clamps and inner ring stood in the pod's hull, 34 m across
    /// where the ring left it 28 m. Within <see cref="CradleRim"/> every point moves out so that the ring's inner edge (2.81 m
    /// from the middle of the 10 m model) lies at <see cref="CradleOpening"/>, the rest of the ring closing up toward the rim.
    /// </summary>
    private static ArrayMesh Opened(ArrayMesh mesh, Transform3D offset)
    {
        const float inner = 2.81f;
        var squeeze = (CradleRim - CradleOpening) / (CradleRim - inner);
        var back = offset.AffineInverse();
        var opened = new ArrayMesh { ResourceName = mesh.ResourceName };
        for (var surface = 0; surface < mesh.GetSurfaceCount(); surface++)
        {
            var arrays = mesh.SurfaceGetArrays(surface);
            if (arrays[(int)Mesh.ArrayType.Vertex].As<Vector3[]>() is { Length: > 0 } vertices)
            {
                for (var v = 0; v < vertices.Length; v++)
                {
                    var p = offset * vertices[v];
                    var r = new Vector2(p.X, p.Z).Length();
                    if (r < CradleRim && r > 0.01f)
                    {
                        var widened = (CradleRim - (CradleRim - r) * squeeze) / r;
                        p = new Vector3(p.X * widened, p.Y, p.Z * widened);
                    }
                    vertices[v] = back * p;
                }
                arrays[(int)Mesh.ArrayType.Vertex] = vertices;
            }
            opened.AddSurfaceFromArrays(mesh.SurfaceGetPrimitiveType(surface), arrays);
            opened.SurfaceSetMaterial(surface, mesh.SurfaceGetMaterial(surface));
        }
        return opened;
    }

    /// <summary>The cradle's ring opened to this inner radius, and how far out (its rim) the model stays as it was, model metres.</summary>
    private const float CradleOpening = 3.55f, CradleRim = 6.0f;

    /// <summary>
    /// The mesh with its normals turned the way its faces are wound where Meshy left them pointing into the model: such a
    /// face (the clamp's inner faces, a crate's lid) faces the eye but catches the light of the side it hides, and shows
    /// white under a ceiling lamp. Which way a face is wound outward is what most of the model's faces say.
    /// </summary>
    private static ArrayMesh Turned(ArrayMesh imported)
    {
        var mesh = new ArrayMesh { ResourceName = imported.ResourceName };
        var turned = 0;
        for (var surface = 0; surface < imported.GetSurfaceCount(); surface++)
        {
            var arrays = imported.SurfaceGetArrays(surface);
            var material = imported.SurfaceGetMaterial(surface);
            if (imported.SurfaceGetPrimitiveType(surface) == Mesh.PrimitiveType.Triangles
                && arrays[(int)Mesh.ArrayType.Vertex].As<Vector3[]>() is { Length: > 0 } vertices
                && arrays[(int)Mesh.ArrayType.Normal].As<Vector3[]>() is { } normals && normals.Length == vertices.Length)
            {
                var indices = arrays[(int)Mesh.ArrayType.Index].As<int[]>() is { Length: > 0 } given ? given : Enumerable.Range(0, vertices.Length).ToArray();
                var faces = new Vector3[vertices.Length];
                var outward = 0;
                for (var t = 0; t + 2 < indices.Length; t += 3)
                {
                    var (a, b, c) = (indices[t], indices[t + 1], indices[t + 2]);
                    var face = (vertices[b] - vertices[a]).Cross(vertices[c] - vertices[a]);
                    faces[a] += face;
                    faces[b] += face;
                    faces[c] += face;
                    outward += Math.Sign(face.Dot(normals[a] + normals[b] + normals[c]));
                }
                var sign = outward >= 0 ? 1 : -1;
                for (var v = 0; v < normals.Length; v++)
                    if (normals[v].Dot(faces[v]) * sign < 0)
                    {
                        normals[v] = -normals[v];
                        turned++;
                    }
                arrays[(int)Mesh.ArrayType.Normal] = normals;
            }
            mesh.AddSurfaceFromArrays(imported.SurfaceGetPrimitiveType(surface), arrays);
            mesh.SurfaceSetMaterial(surface, material);
        }
        return turned > 0 ? mesh : imported;
    }

    /// <summary>How close to a prop's foot a face lies flat to count as its sole (<see cref="Footless"/>), metres.</summary>
    private const float Sole = 0.01f;

    /// <summary>
    /// The mesh without the faces that lie flat at its foot and look up: the door frame's sill across its opening, the
    /// pillars' base rims, the walls' plinth step. A prop stands with its foot on the deck's floor, so these lay in the
    /// floor's own plane; the two fought over each pixel, and as the eye or the screen's smoothing moved by a hair the
    /// floor and the sill took turns (a black bar across every doorway, flickering). The floor shows there instead. The
    /// vertices stay, so the prop's bounds and fitting do not change.
    /// </summary>
    private static ArrayMesh Footless(ArrayMesh mesh, Transform3D offset)
    {
        var foot = float.MaxValue;
        for (var surface = 0; surface < mesh.GetSurfaceCount(); surface++)
            if (mesh.SurfaceGetArrays(surface)[(int)Mesh.ArrayType.Vertex].As<Vector3[]>() is { } vertices)
                foreach (var vertex in vertices)
                    foot = Mathf.Min(foot, (offset * vertex).Y);
        var footless = new ArrayMesh { ResourceName = mesh.ResourceName };
        var dropped = 0;
        for (var surface = 0; surface < mesh.GetSurfaceCount(); surface++)
        {
            var arrays = mesh.SurfaceGetArrays(surface);
            if (mesh.SurfaceGetPrimitiveType(surface) == Mesh.PrimitiveType.Triangles
                && arrays[(int)Mesh.ArrayType.Vertex].As<Vector3[]>() is { Length: > 0 } vertices)
            {
                var indices = arrays[(int)Mesh.ArrayType.Index].As<int[]>() is { Length: > 0 } given ? given : Enumerable.Range(0, vertices.Length).ToArray();
                var kept = new List<int>(indices.Length);
                for (var t = 0; t + 2 < indices.Length; t += 3)
                {
                    var (a, b, c) = (offset * vertices[indices[t]], offset * vertices[indices[t + 1]], offset * vertices[indices[t + 2]]);
                    var up = (b - a).Cross(c - a);
                    // Up by its normals (Turned has them agree with the winding), by its winding where it has none.
                    var flat = Mathf.Max(a.Y, Mathf.Max(b.Y, c.Y)) < foot + Sole && Mathf.Abs(up.Y) > 0.9f * up.Length();
                    var looksUp = arrays[(int)Mesh.ArrayType.Normal].As<Vector3[]>() is { } normals && normals.Length == vertices.Length
                        ? offset.Basis * (normals[indices[t]] + normals[indices[t + 1]] + normals[indices[t + 2]]) is var n && n.Y > 0
                        : up.Y > 0;
                    if (flat && looksUp)
                        dropped++;
                    else
                        kept.AddRange([indices[t], indices[t + 1], indices[t + 2]]);
                }
                arrays[(int)Mesh.ArrayType.Index] = kept.ToArray();
            }
            footless.AddSurfaceFromArrays(mesh.SurfaceGetPrimitiveType(surface), arrays);
            footless.SurfaceSetMaterial(surface, mesh.SurfaceGetMaterial(surface));
        }
        return dropped > 0 ? footless : mesh;
    }

    /// <summary>
    /// The faces inside the door frame's opening (kit_door_frame, 7 m wide, its posts' inner sides about 1.3 m either side of
    /// its middle under a lintel at 5.55 m, see DeckFrame): its posts' inner sides and the lintel's underside, not their
    /// outer sides at ±3.5 m.
    /// Meshy painted them with the bare grey of its metal, and under a door's lamp they showed white beside the red frame.
    /// </summary>
    private static bool JambFaces(Vector3 at, Vector3 normal) =>
        Mathf.Abs(at.X) < 3.42f && (Mathf.Abs(normal.X) > 0.6f && at.Y < 6.6f || normal.Y < -0.6f && at.Y > 5.6f && at.Y < 7f);

    /// <summary>Gives a mesh vertex colours: dark where <paramref name="which"/> says, else white. False when nothing was darkened.</summary>
    private static bool Darken(ArrayMesh mesh, Func<Vector3, Vector3, bool> which)
    {
        var any = false;
        var surfaces = new List<(Godot.Collections.Array Arrays, Material? Material, Mesh.PrimitiveType Kind)>();
        for (var surface = 0; surface < mesh.GetSurfaceCount(); surface++)
        {
            var arrays = mesh.SurfaceGetArrays(surface);
            if (arrays[(int)Mesh.ArrayType.Vertex].As<Vector3[]>() is { Length: > 0 } vertices
                && arrays[(int)Mesh.ArrayType.Normal].As<Vector3[]>() is { } normals && normals.Length == vertices.Length)
            {
                var colours = new Color[vertices.Length];
                for (var v = 0; v < vertices.Length; v++)
                {
                    var dark = which(vertices[v], normals[v]);
                    any |= dark;
                    colours[v] = dark ? new Color(0.32f, 0.3f, 0.3f) : Colors.White;
                }
                arrays[(int)Mesh.ArrayType.Color] = colours;
            }
            surfaces.Add((arrays, mesh.SurfaceGetMaterial(surface), mesh.SurfaceGetPrimitiveType(surface)));
        }
        if (!any)
            return false;
        mesh.ClearSurfaces();
        foreach (var (arrays, material, kind) in surfaces)
        {
            mesh.AddSurfaceFromArrays(kind, arrays);
            mesh.SurfaceSetMaterial(mesh.GetSurfaceCount() - 1, material);
        }
        return true;
    }

    private static MeshInstance3D? FirstMesh(Node node)
    {
        if (node is MeshInstance3D mesh)
            return mesh;
        foreach (var child in node.GetChildren())
            if (FirstMesh(child) is { } found)
                return found;
        return null;
    }

    /// <summary>The remake's version of an imported material (see the class).</summary>
    private static BaseMaterial3D Remade(BaseMaterial3D imported)
    {
        var material = (BaseMaterial3D)imported.Duplicate();
        material.Metallic = Math.Min(material.Metallic, Metal);
        material.CullMode = BaseMaterial3D.CullModeEnum.Back;
        material.TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic;
        // The clamp's floor ring (tools/blender/prop_pack.py adds it plain, without UVs) wears the deck plates' steel,
        // laid on from above and the sides: a flat grey torus under Ike's wheels looked like greybox.
        if (imported.ResourceName == "ring_steel" && ResourceLoader.Exists($"{Folder}/kit_floor_0.jpg"))
        {
            material.AlbedoTexture = GD.Load<Texture2D>($"{Folder}/kit_floor_0.jpg");
            material.AlbedoColor = new Color(0.62f, 0.6f, 0.6f);
            material.Uv1Triplanar = true;
            material.Uv1WorldTriplanar = true;
            material.Uv1Scale = Vector3.One * 0.35f;
            material.Roughness = 0.65f;
        }
        return material;
    }

    /// <summary>
    /// The transform that sets a prop's bottom centre at <paramref name="at"/> (Godot's frame), its front facing the deck's angle
    /// <paramref name="angle"/> (degrees, 0 east, 90 north), scaled by <paramref name="scale"/> in its own frame.
    /// </summary>
    public static Transform3D Place(Model model, Vector3 at, float angle, Vector3 scale) =>
        new Transform3D(new Basis(Vector3.Up, Mathf.DegToRad(90 - angle)) * Basis.FromScale(scale), at) * model.Offset;

    /// <summary>A deck point (x east, y north, z up) in Godot's frame.</summary>
    public static Vector3 World(System.Numerics.Vector3 deck) => new(deck.X, deck.Z, deck.Y);

    /// <summary>Which prop a scenario's artefact type of a deck is shown by (tools/deck/deck.mjs names them), or null.</summary>
    public static string? ForArtefact(string type) => type switch
    {
        "door_small" => "kit_door_small",
        "vent_cover" => "kit_vent_cover",
        _ when type.StartsWith("door_leaf", StringComparison.Ordinal) => "kit_door_leaf",
        _ when type.StartsWith("gate_leaf", StringComparison.Ordinal) => "kit_grate_wall",
        _ => Exists(type) ? type : null,
    };

    /// <summary>
    /// A deck's artefact as its prop, fitted to the artefact's box (BOUNDARY across and deep, HEIGHT up) and placed and turned as
    /// the scenario has it; doors, gates and vent covers keep their own thickness in their slots. Null when no prop shows it.
    /// </summary>
    public static MeshInstance3D? Artefact(ArtefactType? type, ArtefactPlacement placement, Vector3 at)
    {
        if (ForArtefact(placement.TypeName) is not { } name || Get(name) is not { } model)
            return null;
        var size = model.Size;
        var scale = Vector3.One;
        if (type is { Boundary.Count: > 0 } box)
        {
            var across = box.Boundary.Max(p => p.X) - box.Boundary.Min(p => p.X);
            var deep = box.Boundary.Max(p => p.Y) - box.Boundary.Min(p => p.Y);
            var thin = name is "kit_door_small" or "kit_door_leaf" or "kit_grate_wall" or "kit_vent_cover";
            scale = new Vector3(across / Mathf.Max(size.X, 0.01f), (type.Height ?? size.Y) / Mathf.Max(size.Y, 0.01f), thin ? 1 : deep / Mathf.Max(size.Z, 0.01f));
        }
        // The scenario turns an artefact by its orientation; the prop's own frame is then the artefact's.
        var place = new Transform3D(new Basis(Vector3.Up, -Mathf.DegToRad(placement.Orientation)) * Basis.FromScale(scale), at) * model.Offset;
        return new MeshInstance3D { Name = placement.TypeName, Mesh = model.Mesh, Transform = place };
    }
}
