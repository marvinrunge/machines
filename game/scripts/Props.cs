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

    private static Model? Load(string name)
    {
        if (!Exists(name) || GD.Load<PackedScene>($"{Folder}/{name}.glb") is not { } scene)
            return null;
        var root = scene.Instantiate<Node3D>();
        try
        {
            if (FirstMesh(root) is not { Mesh: { } mesh } part)
                return null;
            // Where the mesh stands in the prop: its node's transform and its parents' up to the scene's root.
            var offset = Transform3D.Identity;
            for (Node? node = part; node is not null && node != root; node = node.GetParent())
                if (node is Node3D spatial)
                    offset = spatial.Transform * offset;
            for (var surface = 0; surface < mesh.GetSurfaceCount(); surface++)
                if ((part.GetSurfaceOverrideMaterial(surface) ?? mesh.SurfaceGetMaterial(surface)) is BaseMaterial3D material)
                    mesh.SurfaceSetMaterial(surface, Remade(material));
            return new Model(mesh, offset, offset * mesh.GetAabb());
        }
        finally
        {
            root.Free();
        }
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
