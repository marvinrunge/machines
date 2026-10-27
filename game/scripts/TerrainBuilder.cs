using Godot;
using Machines.Import.Planets;

namespace Machines.Game;

/// <summary>
/// Builds the ground from the original planet data: a height field from the tile heights (see
/// <see cref="HeightField"/>), textured with the planet's overview image when the game copy is present, and
/// the original obstacles raised as rock.
/// </summary>
/// <remarks>
/// With tools/classic-export run, the original terrain tiles are drawn instead (from the game copy's
/// <c>pdata/*.psb</c>, loaded by the original engine).
/// </remarks>
public static class TerrainBuilder
{
    /// <summary>Obstacles are drawn at their real height, but no taller than this, so the view stays clear.</summary>
    private const float MaxWallHeight = 14f;

    /// <summary>The ground's extra render layer: scorch marks and selection rings are cast onto it alone.</summary>
    public const uint GroundLayer = 4;

    public static Node3D Build(Planet planet, HeightField field, Texture2D? overview, Mesh? classicTerrain = null)
    {
        var root = new Node3D { Name = "Terrain" };

        // The original tiles hold the cliffs, ramps and water edges themselves.
        if (classicTerrain is not null)
        {
            // The original's coarse ground throws no shadows (they fell in hard-edged wedges across the map); it
            // takes those of machines and buildings, and its slopes shade by their normals.
            if (!GraphicsSettings.Remake)
            {
                root.AddChild(new MeshInstance3D
                {
                    Name = "ClassicTerrain", Mesh = classicTerrain, Layers = 1 | GroundLayer,
                    CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                });
                return root;
            }
            // The remake gives the coarse tiles a finer shape first (TerrainGeometry: it reads their own materials, and
            // gives those without corner colours some). The natural planets' ground is then painted from a material
            // map (TerrainMaterials); what it leaves (buildings, stations) gets its detail close up (TerrainDetail).
            // Grass and stones grow on both (GroundScatter), the same each time on a planet. It is drawn in squares, so
            // only those in view are, and casts shadows.
            var ground = TerrainGeometry.Refine(classicTerrain, field);
            var painted = TerrainMaterials.Apply(classicTerrain, ground, planet.Name, field.Width, field.Depth);
            var detail = TerrainDetail.Apply(classicTerrain);
            if (GroundScatter.Create(ground, detail, painted, planet.Name, (int)(field.Width * 7919 + field.Depth)) is { } scatter)
                root.AddChild(scatter);
            foreach (var piece in TerrainGeometry.Chunks(ground, classicTerrain.SurfaceGetMaterial))
                root.AddChild(new MeshInstance3D { Name = "Ground", Mesh = piece, Layers = 1 | GroundLayer });
            return root;
        }

        var groundMaterial = new StandardMaterial3D { AlbedoColor = new Color(0.76f, 0.62f, 0.42f), Roughness = 1 };
        if (overview is not null)
        {
            // The overview image runs like the planet: its top row is y = 0 (measured against the obstacles).
            groundMaterial.AlbedoTexture = overview;
            groundMaterial.AlbedoColor = Colors.White;
        }
        root.AddChild(new MeshInstance3D { Name = "Ground", Mesh = Ground(field), MaterialOverride = groundMaterial, Layers = 1 | GroundLayer });

        var rock = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.45f, 0.36f, 0.28f),
            Roughness = 1,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };
        var water = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.15f, 0.35f, 0.55f, 0.8f),
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        };

        foreach (var obstacle in planet.ConfigSpace.Obstacles)
        {
            var polygon = obstacle.Polygon.Select(p => new Vector2(p.X, p.Y)).ToArray();
            var grounds = polygon.Select(p => field.At(p.X, p.Y)).ToArray();
            var height = obstacle.Water ? 0.3f : Mathf.Min(obstacle.Height, obstacle.Low ? 3f : MaxWallHeight);
            // Walls stand on the lowest ground they touch and reach above the highest.
            var top = obstacle.Water ? grounds.Average() + height : grounds.Max() + height;
            var mesh = Prism(polygon, grounds.Min() - 0.5f, top);
            root.AddChild(new MeshInstance3D { Mesh = mesh, MaterialOverride = obstacle.Water ? water : rock });
        }
        return root;
    }

    private static ArrayMesh Ground(HeightField field)
    {
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        Vector3 At(int vx, int vy) => new(vx * field.Spacing, field.Vertex(vx, vy), vy * field.Spacing);
        void Add(int vx, int vy)
        {
            var p = At(vx, vy);
            st.SetUV(new Vector2(p.X / field.Width, p.Z / field.Depth));
            st.AddVertex(p);
        }

        for (var vx = 0; vx + 1 < field.VerticesX; vx++)
        {
            for (var vy = 0; vy + 1 < field.VerticesY; vy++)
            {
                Add(vx, vy);
                Add(vx + 1, vy);
                Add(vx + 1, vy + 1);
                Add(vx, vy);
                Add(vx + 1, vy + 1);
                Add(vx, vy + 1);
            }
        }
        st.GenerateNormals();
        return st.Commit();
    }

    /// <summary>A convex polygon on the ground (original x, y) extruded from one height to another.</summary>
    private static ArrayMesh Prism(Vector2[] polygon, float bottom, float top)
    {
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);

        Vector3 Low(Vector2 p) => new(p.X, bottom, p.Y);
        Vector3 High(Vector2 p) => new(p.X, top, p.Y);

        // Top face: a fan over the convex polygon.
        for (var i = 1; i + 1 < polygon.Length; i++)
        {
            st.AddVertex(High(polygon[0]));
            st.AddVertex(High(polygon[i]));
            st.AddVertex(High(polygon[i + 1]));
        }
        // Side walls.
        for (var i = 0; i < polygon.Length; i++)
        {
            var a = polygon[i];
            var b = polygon[(i + 1) % polygon.Length];
            st.AddVertex(Low(a));
            st.AddVertex(High(a));
            st.AddVertex(High(b));
            st.AddVertex(Low(a));
            st.AddVertex(High(b));
            st.AddVertex(Low(b));
        }
        st.GenerateNormals();
        return st.Commit();
    }
}
